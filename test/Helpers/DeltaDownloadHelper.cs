using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace test.Helpers;

public static class DeltaDownloadHelper
{
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

    public static async Task<string?> TryComputeSha1HexAsync(
        string filePath,
        CancellationToken token
    )
    {
        if (!File.Exists(filePath))
            return null;

        await using var stream = File.OpenRead(filePath);
        var hash = await SHA1.HashDataAsync(stream, token).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    public static bool DigestsMatchSha1(string sha1Hex, string? digestFromService)
    {
        if (string.IsNullOrWhiteSpace(digestFromService))
            return false;

        // FE3 gives the digest as base64 of raw SHA1 bytes.
        byte[] serviceBytes;
        try
        {
            serviceBytes = Convert.FromBase64String(digestFromService);
        }
        catch
        {
            return false;
        }

        var serviceHex = Convert.ToHexString(serviceBytes);
        return string.Equals(serviceHex, sha1Hex, StringComparison.OrdinalIgnoreCase);
    }

    public static async Task DownloadToFileAsync(
        HttpClient http,
        string url,
        string destinationPath,
        CancellationToken token
    )
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        await using var src = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var dst = File.Create(destinationPath);
        await src.CopyToAsync(dst, token).ConfigureAwait(false);
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

            var sha1Hex = await TryComputeSha1HexAsync(cabPath, token).ConfigureAwait(false);
            return sha1Hex is not null && DigestsMatchSha1(sha1Hex, blockmapCabFileDigest);
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
            await DownloadToFileAsync(http, packageUrl, destinationPath, token)
                .ConfigureAwait(false);
            return;
        }

        // If local file doesn't exist at all, full download.
        var fileInfo = new FileInfo(destinationPath);
        if (!fileInfo.Exists)
        {
            await DownloadToFileAsync(http, packageUrl, destinationPath, token)
                .ConfigureAwait(false);
            return;
        }

        var expectedBlocks = blockMap.BlockHashes.Count;
        var localFileLength = fileInfo.Length;

        // Compute local SHA256 per block and determine which blocks differ.
        // Blocks beyond the local file length are automatically marked as changed.
        var changed = new List<int>();
        await using (
            var fs = new FileStream(
                destinationPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
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

        // Calculate total bytes to download from ranges so callers can show progress.
        long totalRangeBytes = 0;
        foreach (var idx in changed)
        {
            var start = (long)idx * blockMap.BlockSize;
            // Last block may be smaller; use blockSize for all except possibly the last.
            totalRangeBytes += blockMap.BlockSize;
        }

        long downloadedRangeBytes = 0;
        progress?.Report((0, totalRangeBytes));

        // Patch changed blocks in-place using HTTP range requests.
        await using (
            var fs = new FileStream(
                destinationPath,
                FileMode.OpenOrCreate,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: blockMap.BlockSize,
                useAsync: true
            )
        )
        {
            foreach (var idx in changed)
            {
                token.ThrowIfCancellationRequested();

                var start = (long)idx * blockMap.BlockSize;
                // Always request a full block from the server.
                var rangeLen = blockMap.BlockSize;

                using var ms = new MemoryStream(capacity: rangeLen);
                await DownloadRangeToStreamAsync(
                        http,
                        packageUrl,
                        start,
                        start + rangeLen - 1,
                        ms,
                        token
                    )
                    .ConfigureAwait(false);

                var bytes = ms.ToArray();
                var remoteHash = SHA256.HashData(bytes);
                var remoteB64 = Convert.ToBase64String(remoteHash);
                if (!string.Equals(remoteB64, blockMap.BlockHashes[idx], StringComparison.Ordinal))
                {
                    // If we can't validate a single block, safest is full download.
                    await DownloadToFileAsync(http, packageUrl, destinationPath, token)
                        .ConfigureAwait(false);
                    return;
                }

                fs.Position = start;
                await fs.WriteAsync(bytes, 0, bytes.Length, token).ConfigureAwait(false);

                downloadedRangeBytes += bytes.Length;
                progress?.Report((downloadedRangeBytes, totalRangeBytes));
            }
        }
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
