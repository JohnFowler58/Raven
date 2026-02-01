using System.Diagnostics;
using System.Net;
using Downloader;
using Windows.Management.Deployment;
using test.Models;
using test.Services;
using StoreListings.Library;

namespace test.Helpers;

public sealed class DownloadHelper
{
    private static string FormatBytes(long bytes)
    {
        const double KB = 1024d;
        const double MB = KB * 1024d;
        const double GB = MB * 1024d;
        const double TB = GB * 1024d;

        if (bytes >= TB)
            return $"{bytes / TB:0.#} TB";
        if (bytes >= GB)
            return $"{bytes / GB:0.#} GB";
        if (bytes >= MB)
            return $"{bytes / MB:0.#} MB";
        if (bytes >= KB)
            return $"{bytes / KB:0.#} KB";
        return $"{bytes} B";
    }

    public static async Task StartDownloadAsync(
        FileEntry entry,
        string productId,
        CancellationToken token,
        UIUpdateService updateService
    )
    {
        const int MAX_RETRIES_PER_FILE = 5;
        const int NO_PROGRESS_TIMEOUT_MS = 60_000;
        const int MAX_BACKOFF_MS = 30_000;
        const int PERSIST_THROTTLE_MS = 2_000;
        
        // Simple throttle: update UI at most every 250ms
        const int UI_THROTTLE_MS = 250;

        var downloadManager = DownloadManagerService.Instance;

        // Clear any leftover details from previous attempts
        downloadManager.UpdateDownloadDetailsText(productId, string.Empty);

        // Per-file progress tracking state
        int lastWholePercent = -1;
        long lastUIUpdateMs = 0;
        long lastProgressTicks = 0;
        long startTicks = Environment.TickCount64;

        var config = new DownloadConfiguration
        {
            // Best practice for large files: avoid up-front reservation / pre-allocation.
            // Pre-allocating multi-GB files can look like a hang due to long disk writes.
            ReserveStorageSpaceBeforeStartingDownload = false,

            // CRITICAL for large files: Disable parallel chunking.
            // With ParallelDownload=true, the library holds chunk data in memory before merging.
            // For a 2GB file with 2 chunks, that's 2x1GB buffers causing severe memory pressure.
            // Sequential download uses much less memory and avoids the merge step entirely.
            ParallelDownload = false,
            ChunkCount = 1,
            ParallelCount = 1,

            Timeout = 30000,

            // Smaller buffer reduces memory footprint per download.
            // 64KB is a good balance between throughput and memory usage.
            BufferBlockSize = 64 * 1024,

            MaximumBytesPerSecond = 0,
        };

        static string SanitizeFolderName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return string.Empty;

            var invalid = Path.GetInvalidFileNameChars();
            var cleaned = new string(
                name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()
            ).Trim();

            // Avoid super-long paths.
            return cleaned.Length <= 80 ? cleaned : cleaned[..80];
        }

        var downloadItem = downloadManager.GetDownload(productId);
        if (downloadItem is null)
        {
            return;
        }

        // Ensure the Downloads list is in the correct phase.
        // AppPage sets Status=Pending during URL fetch; once we start transferring bytes we must be Downloading.
        downloadManager.UpdateDownloadStatus(productId, test.Models.DownloadStatus.Downloading);

        var animator = new DownloadItemStatusAnimator(updateService.DispatcherQueue);

        var appFolderName = SanitizeFolderName(downloadItem.Title);
        if (string.IsNullOrWhiteSpace(appFolderName))
            appFolderName = productId;

        var baseDownloadDir = Path.Combine(AppContext.BaseDirectory, "downloads", appFolderName);
        var depsDownloadDir = Path.Combine(baseDownloadDir, "Dependencies");

        // Track file index/total for consistent text in DownloadItem.StatusText
        int totalFiles = 1;
        int currentFileIndex = 1;
        string FilesLabel() => $"file{(totalFiles == 1 ? string.Empty : "s")}";

        // Flatten dependencies (dependencies first), skipping duplicates by URL
        var flattened = new List<FileEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(FileEntry node)
        {
            if (node.Dependencies != null)
            {
                foreach (var dep in node.Dependencies)
                    Visit(dep);
            }

            if (seen.Add(node.Url))
                flattened.Add(node);
        }

        Visit(entry);

        totalFiles = Math.Max(1, flattened.Count);
        currentFileIndex = 1;

        var mainUrl = entry.Url;

        var baseStatus = $"Downloading ({currentFileIndex}/{totalFiles}) {FilesLabel()}";

        // Single continuous animation for the page status (AppPage only)
        updateService.StartStatusAnimation(baseStatus);

        // Animated dots in the Downloads list
        animator.Start(downloadItem, baseStatus);

        bool cancelled = false;
        bool hadError = false;

        for (int i = 0; i < flattened.Count; i++)
        {
            if (token.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            currentFileIndex = i + 1;

            baseStatus = $"Downloading ({currentFileIndex}/{totalFiles}) {FilesLabel()}";
            updateService.UpdateAnimatedStatusBase(baseStatus);
            animator.UpdateBase(downloadItem, baseStatus);

            var file = flattened[i];

            var isMain = file.Url.Equals(mainUrl, StringComparison.OrdinalIgnoreCase);
            var targetDir = isMain ? baseDownloadDir : depsDownloadDir;

            string destinationPath = Path.Combine(targetDir, Path.GetFileName(file.FileName));

            Debug.WriteLine(destinationPath);
            var destinationDir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(destinationDir) && !Directory.Exists(destinationDir))
                Directory.CreateDirectory(destinationDir);

            bool downloaded = false;

            for (int attempt = 1; attempt <= MAX_RETRIES_PER_FILE; attempt++)
            {
                if (token.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }

                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                var attemptToken = attemptCts.Token;

                // Reset per-file progress tracking before starting each attempt
                lastWholePercent = -1;
                lastUIUpdateMs = 0;
                startTicks = Environment.TickCount64;
                lastProgressTicks = 0;

                // Show retry status if not first attempt
                if (attempt > 1)
                {
                    downloadManager.UpdateDownloadDetailsText(
                        productId,
                        $"Retry {attempt}/{MAX_RETRIES_PER_FILE}"
                    );
                }

                // Create a fresh DownloadService for each attempt to avoid state leakage
                using var svc = new DownloadService(config);


                bool currentFileCancelled = false;

                using var cancellationRegistration = token.Register(() =>
                {
                    try
                    {
                        updateService.UpdateAnimatedStatusBase("Cancelling");
                        svc.CancelAsync();
                    }
                    catch
                    {
                        // ignore
                    }
                });

                using var stallTimer = new System.Threading.Timer(
                    _ =>
                    {
                        try
                        {
                            var now = Environment.TickCount64;
                            var elapsed = now - startTicks;
                            var last = Interlocked.Read(ref lastProgressTicks);
                            if (elapsed - last >= NO_PROGRESS_TIMEOUT_MS)
                            {
                                downloadManager.UpdateDownloadDetailsText(
                                    productId,
                                    "No progress detected. Restarting..."
                                );
                                attemptCts.Cancel();
                                svc.CancelAsync();
                            }
                        }
                        catch
                        {
                            // ignore
                        }
                    },
                    null,
                    NO_PROGRESS_TIMEOUT_MS,
                    NO_PROGRESS_TIMEOUT_MS
                );

                // Throttle persisting the downloads list to disk; frequent writes to downloads.json
                // during big transfers can block the UI thread.
                long lastPersistMs = 0;
                void PersistMilestoneIfDue(long nowMs, bool force = false)
                {
                    if (force || nowMs - Interlocked.Read(ref lastPersistMs) >= PERSIST_THROTTLE_MS)
                    {
                        Interlocked.Exchange(ref lastPersistMs, nowMs);
                        downloadManager.SaveDownloadsThrottled();
                    }
                }

                // Cache item reference
                var cachedItem = downloadItem!;

                svc.DownloadProgressChanged += (s, e) =>
                {
                    long tickNow = Environment.TickCount64;
                    
                    // Simple throttle: don't update more than every 250ms (except for 100%)
                    int wholePercent = (int)Math.Clamp(e.ProgressPercentage, 0, 100);
                    bool isComplete = wholePercent == 100;
                    
                    if (!isComplete && tickNow - Volatile.Read(ref lastUIUpdateMs) < UI_THROTTLE_MS)
                        return;
                    
                    // We remove the strict 'wholePercent == lastWholePercent' check here so that
                    // we can update byte counts and text even if the percentage hasn't changed.
                    // This ensures the UI doesn't look "stuck" on large files where % is slow to move.
                    
                    lastWholePercent = wholePercent;
                    Volatile.Write(ref lastUIUpdateMs, tickNow);
                    Interlocked.Exchange(ref lastProgressTicks, tickNow - startTicks);

                    var receivedText = FormatBytes(e.ReceivedBytesSize);
                    var totalText = FormatBytes(e.TotalBytesToReceive);
                    var detailsString = $"{wholePercent}% • {receivedText} / {totalText}";

                    // Skip UI updates if nobody is watching (saves CPU when on other pages)
                    // Still update backing fields so values are ready when user navigates back
                    if (!downloadManager.IsAnyoneObserving)
                    {
                        cachedItem.SetProgressSilent(wholePercent);
                        cachedItem.ReceivedBytes = e.ReceivedBytesSize;
                        cachedItem.TotalBytes = e.TotalBytesToReceive;
                        cachedItem.SetDisplayDetailsTextSilent(detailsString);
                        
                        PersistMilestoneIfDue(tickNow - startTicks, force: isComplete);
                        return;
                    }

                    // Update on UI thread - only when someone is watching
                    downloadManager.RunOnUIThread(() =>
                    {
                        cachedItem.Progress = wholePercent;
                        cachedItem.ReceivedBytes = e.ReceivedBytesSize;
                        cachedItem.TotalBytes = e.TotalBytesToReceive;
                        cachedItem.DisplayDetailsText = detailsString;
                    });

                    // Persist occasionally
                    PersistMilestoneIfDue(tickNow - startTicks, force: isComplete);
                };

                svc.DownloadFileCompleted += (s, e) =>
                {
                    if (e.Cancelled)
                        currentFileCancelled = true;
                };

                try
                {
                    await svc.DownloadFileTaskAsync(file.Url, destinationPath, attemptToken)
                        .ConfigureAwait(false);

                    if (token.IsCancellationRequested || currentFileCancelled)
                    {
                        cancelled = true;
                        break;
                    }

                    downloadManager.AddDownloadedFilePath(productId, destinationPath);
                    downloaded = true;
                    break;
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    // Stall timeout / per-attempt cancel: retry.
                    if (attempt < MAX_RETRIES_PER_FILE)
                    {
                        var delayMs = GetRetryDelayMs(null, attempt, MAX_BACKOFF_MS);
                        downloadManager.UpdateDownloadDetailsText(
                            productId,
                            $"Restarting... retrying in {delayMs / 1000.0:F1}s..."
                        );
                        await Task.Delay(delayMs, token).ConfigureAwait(false);
                        continue;
                    }

                    hadError = true;
                    downloadManager.UpdateDownloadStatusText(
                        productId,
                        "Error: Download stalled and exhausted retries."
                    );
                    break;
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    break;
                }
                catch (WebException ex) when (attempt < MAX_RETRIES_PER_FILE)
                {
                    var delayMs = GetRetryDelayMs(ex, attempt, MAX_BACKOFF_MS);
                    downloadManager.UpdateDownloadDetailsText(
                        productId,
                        $"Temporary network/server issue. Retrying in {delayMs / 1000.0:F1}s..."
                    );
                    await Task.Delay(delayMs, token).ConfigureAwait(false);
                }
                catch (Exception ex) when (attempt < MAX_RETRIES_PER_FILE)
                {
                    var delayMs = GetRetryDelayMs(null, attempt, MAX_BACKOFF_MS);
                    downloadManager.UpdateDownloadDetailsText(
                        productId,
                        $"Error: {ex.Message}. Retrying in {delayMs / 1000.0:F1}s..."
                    );
                    await Task.Delay(delayMs, token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    hadError = true;
                    downloadManager.UpdateDownloadStatusText(productId, $"Error: {ex.Message}");
                    downloadManager.UpdateDownloadDetailsText(
                        productId,
                        "Check network or disk space."
                    );
                    break;
                }
            }

            if (cancelled || hadError)
                break;

            if (!downloaded)
            {
                hadError = true;
                downloadManager.UpdateDownloadStatusText(
                    productId,
                    "Error: Download failed after retries."
                );
                break;
            }
        }

        // End of batch: finalize UI
        updateService.StopStatusAnimation();
        animator.Stop(downloadItem);

        // Clear details so next phase (install) starts clean
        downloadManager.UpdateDownloadDetailsText(productId, string.Empty);

        if (cancelled)
        {
            downloadManager.UpdateDownloadStatusText(productId, "Download canceled.");
            try
            {
                downloadManager.UpdateDownloadBytes(productId, null, null);
            }
            catch { }
            downloadManager.UpdateDownloadStatus(productId, test.Models.DownloadStatus.Cancelled);
            return;
        }

        if (hadError)
        {
            try
            {
                downloadManager.UpdateDownloadBytes(productId, null, null);
            }
            catch { }
            return;
        }

        // Mark download phase complete
        downloadManager.UpdateDownloadProgress(productId, 100);
        downloadManager.UpdateDownloadDetailsText(productId, string.Empty);

        // Persist the final download state.
        downloadManager.SaveDownloadsThrottled(force: true);

        var isUnpackaged = downloadItem.ProductInfo?.InstallerType == InstallerType.Unpackaged;

        if (isUnpackaged)
        {
            animator.Stop(downloadItem);
            updateService.StopStatusAnimation();
            downloadManager.UpdateDownloadStatusText(productId, "Download completed. Open to install.");
            downloadManager.UpdateDownloadStatus(productId, test.Models.DownloadStatus.Completed);
            return;
        }

        // Begin install phase and reflect it in Downloads page.
        downloadManager.UpdateDownloadStatus(productId, test.Models.DownloadStatus.Installing);
        downloadManager.UpdateDownloadProgress(productId, 0);
        downloadManager.UpdateDownloadStatusText(productId, "Installing");
        try
        {
            downloadManager.UpdateDownloadBytes(productId, null, null);
        }
        catch { }
        updateService.StartStatusAnimation("Installing");

        // Animated dots in the Downloads list during install
        animator.Start(downloadItem, "Installing");

        var mainPackagePath = downloadItem.DownloadedFilePaths.FirstOrDefault(p =>
            !string.IsNullOrWhiteSpace(p)
            && string.Equals(
                Path.GetFileName(p),
                Path.GetFileName(entry.FileName),
                StringComparison.OrdinalIgnoreCase
            )
        );

        if (string.IsNullOrWhiteSpace(mainPackagePath) || !File.Exists(mainPackagePath))
        {
            // Can't locate main package on disk; mark failed.
            updateService.StopStatusAnimation();
            downloadManager.UpdateDownloadStatusText(
                productId,
                "Install failed: main package missing."
            );
            downloadManager.UpdateDownloadStatus(productId, test.Models.DownloadStatus.Failed);
            return;
        }

        var depPaths = (downloadItem?.DownloadedFilePaths ?? [])
            .Where(p =>
                !string.IsNullOrWhiteSpace(p)
                && File.Exists(p)
                && !string.Equals(p, mainPackagePath, StringComparison.OrdinalIgnoreCase)
            )
            .ToList();

        try
        {
            // Throttle install progress updates similar to download progress
            int lastInstallPercent = -1;
            long lastInstallProgressMs = 0;
            const int INSTALL_PROGRESS_THROTTLE_MS = 100;

            var installProgress = new Progress<AppPackageInstaller.InstallProgress>(p =>
            {
                var percent = (int)Math.Clamp(p.Percent, 0, 100);

                // Throttle updates unless we hit 100%
                var now = Environment.TickCount64;
                if (percent != 100 && percent == lastInstallPercent)
                    return;
                if (percent != 100 && now - lastInstallProgressMs < INSTALL_PROGRESS_THROTTLE_MS)
                    return;

                lastInstallPercent = percent;
                lastInstallProgressMs = now;

                downloadManager.UpdateDownloadProgress(productId, percent);
            });

            await AppPackageInstaller.InstallAsync(
                mainPackagePath,
                dependencyPackagePaths: depPaths,
                progress: installProgress,
                cancellationToken: token
            );

            animator.Stop(downloadItem);
            updateService.StopStatusAnimation();
            downloadManager.UpdateDownloadStatusText(productId, null);
            downloadManager.UpdateDownloadStatus(productId, test.Models.DownloadStatus.Completed);
        }
        catch (OperationCanceledException)
        {
            animator.Stop(downloadItem);
            updateService.StopStatusAnimation();
            var packageFamilyName = downloadItem.ProductInfo?.PackageFamilyName;
            if (IsPackageInstalled(packageFamilyName))
            {
                downloadManager.UpdateDownloadStatusText(productId, null);
                downloadManager.UpdateDownloadStatus(productId, test.Models.DownloadStatus.Completed);
            }
            else
            {
                downloadManager.UpdateDownloadStatusText(productId, null);
                downloadManager.UpdateDownloadStatus(productId, test.Models.DownloadStatus.Cancelled);
            }
        }
        catch (Exception ex)
        {
            animator.Stop(downloadItem);
            updateService.StopStatusAnimation();
            downloadManager.UpdateDownloadStatusText(productId, $"Install failed: {ex.Message}");
            downloadManager.UpdateDownloadStatus(productId, test.Models.DownloadStatus.Failed);
        }
    }

    private static bool IsPackageInstalled(string? packageFamilyName)
    {
        if (string.IsNullOrWhiteSpace(packageFamilyName))
            return false;

        try
        {
            var packageManager = new PackageManager();
            return packageManager.FindPackagesForUser(string.Empty, packageFamilyName).Any();
        }
        catch
        {
            return false;
        }
    }

    private static int GetRetryDelayMs(WebException? webEx, int attempt, int maxBackoffMs)
    {
        int baseDelayMs = (int)Math.Min(maxBackoffMs, 1000 * Math.Pow(2, attempt - 1));

        if (webEx?.Response is HttpWebResponse resp)
        {
            // Honor Retry-After when present (common for 429/503).
            var retryAfter = resp.Headers["Retry-After"];
            if (int.TryParse(retryAfter, out var seconds) && seconds > 0)
                return (int)Math.Min(maxBackoffMs, seconds * 1000);
        }

        // Add small jitter to avoid thundering herd.
        return baseDelayMs + Random.Shared.Next(0, 500);
    }
}
