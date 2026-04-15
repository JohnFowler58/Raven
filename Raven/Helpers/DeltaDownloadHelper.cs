using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Raven.Helpers;

public static class DeltaDownloadHelper
{
    private const int MergeGapThresholdBytes = 64 * 1024;

    private sealed record RangePatch(long Offset, int Length);

    public static async Task<string?> TryComputeSha256HexAsync(
        string filePath,
        CancellationToken token
    )
    {
        if (!File.Exists(filePath))
            return null;

        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    public static async Task DownloadToFileAsync(
        HttpClient http,
        string url,
        string destinationPath,
        CancellationToken token,
        IProgress<(long bytesDownloaded, long totalBytes)>? progress = null
    )
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var totalBytes = resp.Content.Headers.ContentLength ?? 0;
        long downloadedBytes = 0;

        await using var src = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var dst = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 81920,
            useAsync: true
        );

        var buffer = new byte[81920];
        int read;
        while ((read = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            downloadedBytes += read;
            progress?.Report((downloadedBytes, totalBytes));
        }
    }

    public static async Task ApplyDeltaUsingBlockmapAsync(
        HttpClient http,
        string packageUrl,
        string destinationPath,
        string blockmapCabUrl,
        string? blockmapCabFileDigest,
        CancellationToken token,
        IProgress<(long bytesDownloaded, long totalBytes)>? progress = null
    )
    {
        var destBaseName = Path.GetFileName(destinationPath);
        if (string.IsNullOrWhiteSpace(destBaseName))
            destBaseName = "package";

        var cacheRoot = Path.Combine(Path.GetTempPath(), "raven", "blockmaps", destBaseName);
        Directory.CreateDirectory(cacheRoot);

        var cabPath = Path.Combine(cacheRoot, "blockmap.cab");
        var blockmapPath = Path.Combine(cacheRoot, "blockmap.xml");

        async Task<bool> CabMatchesDigestAsync()
        {
            if (string.IsNullOrWhiteSpace(blockmapCabFileDigest))
                return false;
            if (!File.Exists(cabPath))
                return false;

            // FE3 gives the digest as base64 of raw SHA1 bytes.
            byte[] serviceBytes;
            try
            {
                serviceBytes = Convert.FromBase64String(blockmapCabFileDigest);
            }
            catch
            {
                return false;
            }

            await using var stream = File.OpenRead(cabPath);
            var localSha1 = await SHA1.HashDataAsync(stream, token).ConfigureAwait(false);
            return localSha1.AsSpan().SequenceEqual(serviceBytes);
        }

        var cabOk = await CabMatchesDigestAsync().ConfigureAwait(false);
        if (!File.Exists(cabPath) || (!string.IsNullOrWhiteSpace(blockmapCabFileDigest) && !cabOk))
        {
            if (File.Exists(cabPath))
            {
                try { File.Delete(cabPath); } catch { }
            }

            await DownloadToFileAsync(http, blockmapCabUrl, cabPath, token).ConfigureAwait(false);
        }


        // Extract BlockMap.xml from CAB into a stable path under temp.
        var xmlPath = await CabExtractor
                .ExtractFileToTempAsync(cabPath, "BlockMap.xml", blockmapPath, token)
                .ConfigureAwait(false);

        var xml = await File.ReadAllTextAsync(xmlPath, token).ConfigureAwait(false);
        var blockMap = BlockMapReader.Parse(xml);

        // Verify server supports range requests. If not, full download.
        if (!await SupportsRangesAsync(http, packageUrl, token).ConfigureAwait(false))
        {
            await DownloadToFileAsync(http, packageUrl, destinationPath, token, progress)
                .ConfigureAwait(false);
            return;
        }

        var remoteLength = await TryGetContentLengthAsync(http, packageUrl, token).ConfigureAwait(false);

        // If local file doesn't exist at all, full download.
        var fileInfo = new FileInfo(destinationPath);
        if (!fileInfo.Exists)
        {
            await DownloadToFileAsync(http, packageUrl, destinationPath, token, progress)
                .ConfigureAwait(false);
            return;
        }

        var expectedBlocks = blockMap.BlockHashes.Count;
        var localFileLength = fileInfo.Length;

        if (remoteLength is long lenFromServer && lenFromServer > 0)
        {
            if (lenFromServer <= 0)
                remoteLength = null;
            else
                remoteLength = lenFromServer;
        }

        // Compute local SHA256 per block and determine which blocks differ.
        // Blocks beyond the local file length are automatically marked as changed.
        var changed = new List<int>();
        await using (
            var fs = new FileStream(
                destinationPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: blockMap.BlockSize,
                useAsync: true
            )
        )
        {
            var buffer = new byte[blockMap.BlockSize];
            for (var i = 0; i < expectedBlocks; i++)
            {
                token.ThrowIfCancellationRequested();

                var blockStart = (long)i * blockMap.BlockSize;
                if (blockStart >= localFileLength)
                {
                    // All remaining blocks are beyond the local file; mark them all as changed.
                    for (var j = i; j < expectedBlocks; j++)
                        changed.Add(j);
                    break;
                }

                var read = await ReadExactlyOrLessAsync(fs, buffer, 0, blockMap.BlockSize, token)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    // Unexpected EOF – mark this and all remaining blocks as changed.
                    for (var j = i; j < expectedBlocks; j++)
                        changed.Add(j);
                    break;
                }

                var localHash = SHA256.HashData(buffer.AsSpan(0, read));
                var localB64 = Convert.ToBase64String(localHash);
                if (!string.Equals(localB64, blockMap.BlockHashes[i], StringComparison.Ordinal))
                    changed.Add(i);
            }
        }

        if (changed.Count == 0)
            return; // already matches blockmap

        var changedSet = new HashSet<int>(changed);

        static int GetBlockLength(int blockIndex, int expectedBlocks, int blockSize, long? totalLength)
        {
            if (blockIndex != expectedBlocks - 1)
                return blockSize;

            if (totalLength is not long len || len <= 0)
                return blockSize;

            var tail = (int)(len % blockSize);
            return tail == 0 ? blockSize : tail;
        }

        var patches = BuildMergedPatches(changedSet, expectedBlocks, blockMap.BlockSize, remoteLength);
        long totalRangeBytes = patches.Sum(p => (long)p.Length);

        long downloadedRangeBytes = 0;
        progress?.Report((0, totalRangeBytes));

        await using (
            var fs = new FileStream(
                destinationPath,
                FileMode.OpenOrCreate,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: blockMap.BlockSize,
                useAsync: true
            )
        )
        {
            var buffer = new byte[blockMap.BlockSize];
            var blockScratch = new byte[blockMap.BlockSize];

            foreach (var patch in patches)
            {
                token.ThrowIfCancellationRequested();

                using var req = new HttpRequestMessage(HttpMethod.Get, packageUrl);
                req.Headers.Range = new RangeHeaderValue(patch.Offset, patch.Offset + patch.Length - 1);

                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token)
                    .ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();

                await using var src = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);

                var remaining = patch.Length;
                var currentPos = patch.Offset;

                while (remaining > 0)
                {
                    token.ThrowIfCancellationRequested();

                    var globalBlockIndex = (int)(currentPos / blockMap.BlockSize);
                    var blockOffsetInPatch = (int)(currentPos - ((long)globalBlockIndex * blockMap.BlockSize));
                    var blockRemaining = blockMap.BlockSize - blockOffsetInPatch;

                    var toRead = Math.Min(remaining, blockRemaining);

                    var read = await ReadExactlyOrLessAsync(src, buffer, 0, toRead, token)
                        .ConfigureAwait(false);

                    if (read != toRead)
                        throw new IOException("Unexpected EOF while reading HTTP range response.");

                    // Only validate & patch blocks that were identified as changed.
                    // We still stream the whole merged range to keep HTTP requests low,
                    // but we avoid rewriting unchanged blocks.
                    if (changedSet.Contains(globalBlockIndex))
                    {
                        // Blockmap hashes are per full block (except possibly the final block).
                        // When using merged ranges we might be reading only a portion of a block at a time.
                        // Reconstruct the full block bytes for hashing before validating/writing.
                        var blockStart = (long)globalBlockIndex * blockMap.BlockSize;
                        var expectedLen = GetBlockLength(globalBlockIndex, expectedBlocks, blockMap.BlockSize, remoteLength);

                        if (blockOffsetInPatch == 0 && read >= expectedLen)
                        {
                            // Common case: we read an entire block in one go.
                            var remoteHash = SHA256.HashData(buffer.AsSpan(0, expectedLen));
                            var remoteB64 = Convert.ToBase64String(remoteHash);
                            if (!string.Equals(remoteB64, blockMap.BlockHashes[globalBlockIndex], StringComparison.Ordinal))
                                throw new InvalidDataException("Remote block hash does not match blockmap.");

                            fs.Position = blockStart;
                            await fs.WriteAsync(buffer.AsMemory(0, expectedLen), token).ConfigureAwait(false);
                        }
                        else
                        {
                            // Slow path: fill missing bytes by combining existing local data with newly downloaded bytes,
                            // then verify the full reconstructed block.
                            fs.Position = blockStart;
                            var gotLocal = await ReadExactlyOrLessAsync(
                                    fs,
                                    blockScratch,
                                    0,
                                    expectedLen,
                                    token
                                )
                                .ConfigureAwait(false);

                            if (gotLocal != expectedLen)
                                Array.Clear(blockScratch, gotLocal, expectedLen - gotLocal);

                            buffer.AsSpan(0, read).CopyTo(blockScratch.AsSpan(blockOffsetInPatch, read));

                            var remoteHash = SHA256.HashData(blockScratch.AsSpan(0, expectedLen));
                            var remoteB64 = Convert.ToBase64String(remoteHash);
                            if (!string.Equals(remoteB64, blockMap.BlockHashes[globalBlockIndex], StringComparison.Ordinal))
                                throw new InvalidDataException("Remote block hash does not match blockmap.");

                            fs.Position = blockStart;
                            await fs.WriteAsync(blockScratch.AsMemory(0, expectedLen), token).ConfigureAwait(false);
                        }
                    }

                    // Even when not writing (merged gap/unchanged block), advance within the range.

                    currentPos += read;
                    remaining -= read;

                    downloadedRangeBytes += read;
                    progress?.Report((downloadedRangeBytes, totalRangeBytes));
                }
            }
        }
    }

    private static List<RangePatch> BuildMergedPatches(
        HashSet<int> changed,
        int expectedBlocks,
        int blockSize,
        long? totalLength
    )
    {
        if (changed.Count == 0)
            return [];

        var ordered = changed.OrderBy(i => i).ToArray();
        var patches = new List<RangePatch>();

        var startIdx = ordered[0];
        var endIdx = ordered[0];

        for (var i = 1; i < ordered.Length; i++)
        {
            var next = ordered[i];

            var endOffsetInclusive = ((long)endIdx * blockSize) + blockSize - 1;
            var nextStart = (long)next * blockSize;
            var gap = nextStart - (endOffsetInclusive + 1);

            if (gap <= MergeGapThresholdBytes)
            {
                endIdx = next;
                continue;
            }

            patches.Add(ToPatch(startIdx, endIdx, expectedBlocks, blockSize, totalLength));
            startIdx = endIdx = next;
        }

        patches.Add(ToPatch(startIdx, endIdx, expectedBlocks, blockSize, totalLength));
        return patches;

        static RangePatch ToPatch(int start, int end, int expectedBlocks, int blockSize, long? totalLength)
        {
            var offset = (long)start * blockSize;
            var endExclusiveByBlocks = Math.Min((long)(end + 1) * blockSize, (long)expectedBlocks * blockSize);
            var endExclusive = totalLength is long total && total > 0
                ? Math.Min(endExclusiveByBlocks, total)
                : endExclusiveByBlocks;
            var length = checked((int)(endExclusive - offset));
            return new RangePatch(offset, length);
        }
    }

    private static async Task<long?> TryGetContentLengthAsync(HttpClient http, string url, CancellationToken token)
    {
        try
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, url);
            using var resp = await http.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);
            if (resp.IsSuccessStatusCode && resp.Content.Headers.ContentLength is long len && len > 0)
                return len;
        }
        catch
        {
            // ignore
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new RangeHeaderValue(0, 0);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null;

            // Prefer Content-Range total when available.
            if (resp.Content.Headers.ContentRange?.Length is long total && total > 0)
                return total;

            if (resp.Content.Headers.ContentLength is long len && len > 0)
                return len;
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static async Task<int> ReadExactlyOrLessAsync(
        Stream s,
        byte[] buffer,
        int offset,
        int count,
        CancellationToken token
    )
    {
        var total = 0;
        while (total < count)
        {
            var read = await s.ReadAsync(buffer.AsMemory(offset + total, count - total), token)
                .ConfigureAwait(false);
            if (read == 0)
                break;
            total += read;
        }

        return total;
    }

    private static async Task<bool> SupportsRangesAsync(
        HttpClient http,
        string url,
        CancellationToken token
    )
    {
        try
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, url);
            using var resp = await http.SendAsync(
                    head,
                    HttpCompletionOption.ResponseHeadersRead,
                    token
                )
                .ConfigureAwait(false);

            if (resp.IsSuccessStatusCode)
            {
                if (
                    resp.Headers.AcceptRanges.Any(r =>
                        string.Equals(r, "bytes", StringComparison.OrdinalIgnoreCase)
                    )
                )
                    return true;

                // Some servers don't return Accept-Ranges on HEAD. Probe with a small range GET.
            }
        }
        catch
        {
            // ignore and fall back to probe
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new RangeHeaderValue(0, 0);
            using var resp = await http.SendAsync(
                    req,
                    HttpCompletionOption.ResponseHeadersRead,
                    token
                )
                .ConfigureAwait(false);
            return resp.StatusCode == System.Net.HttpStatusCode.PartialContent;
        }
        catch
        {
            return false;
        }
    }

    public static async Task DownloadRangeToStreamAsync(
        HttpClient http,
        string url,
        long from,
        long toInclusive,
        Stream destination,
        CancellationToken token
    )
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Range = new RangeHeaderValue(from, toInclusive);

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        await using var src = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await src.CopyToAsync(destination, token).ConfigureAwait(false);
    }
}
