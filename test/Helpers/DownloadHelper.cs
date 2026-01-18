using System.Diagnostics;
using System.Net;
using Downloader;
using Microsoft.UI.Dispatching;
using test.Models;
using test.Services;

namespace test.Helpers;

public sealed class DownloadHelper
{
    public static async Task StartDownloadAsync(
        FileEntry entry,
        string productId,
        CancellationToken token,
        UIUpdateService updateService
    )
    {
        const int THROTTLE_MS = 500;
        const int MAX_RETRIES_PER_FILE = 5;
        const int NO_PROGRESS_TIMEOUT_MS = 60_000;
        const int MAX_BACKOFF_MS = 30_000;

        var reporter = updateService.GetReporter();
        var downloadManager = DownloadManagerService.Instance;

        // Clear any leftover details from previous attempts
        reporter.Report(new UIUpdate(Progress: 0, Details: string.Empty));

        // Per-file progress tracking state
        int lastWholePercent = -1;
        long lastReportTicks = 0;
        long lastProgressTicks = 0;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var config = new DownloadConfiguration
        {
            ChunkCount = 4,
            ParallelDownload = false,
            Timeout = 30000,
            ParallelCount = 2,
            BufferBlockSize = 8192,
            MaximumBytesPerSecond = 0,
            MinimumSizeOfChunking = 1024,
            ReserveStorageSpaceBeforeStartingDownload = true,
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
        downloadManager.UpdateDownloadStatusText(productId, null);

        var animator = new DownloadItemStatusAnimator(updateService.DispatcherQueue);

        var appFolderName = SanitizeFolderName(downloadItem?.Title);
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

        // Single continuous animation for the page status
        updateService.StartStatusAnimation(
            $"Downloading ({currentFileIndex}/{totalFiles}) {FilesLabel()}"
        );

        // Animated dots in the Downloads list (same timer technique as UIUpdateService)
        animator.Start(
            downloadItem,
            $"Downloading ({currentFileIndex}/{totalFiles}) {FilesLabel()}"
        );

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

            updateService.UpdateAnimatedStatusBase(
                $"Downloading ({currentFileIndex}/{totalFiles}) {FilesLabel()}"
            );
            animator.UpdateBase(
                downloadItem,
                $"Downloading ({currentFileIndex}/{totalFiles}) {FilesLabel()}"
            );

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
                lastReportTicks = 0;
                stopwatch.Restart();
                lastProgressTicks = 0;

                reporter.Report(
                    new UIUpdate(
                        Progress: 0,
                        Details: attempt == 1
                            ? string.Empty
                            : $"Retry {attempt}/{MAX_RETRIES_PER_FILE}"
                    )
                );

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
                            var elapsed = stopwatch.ElapsedMilliseconds;
                            var last = Interlocked.Read(ref lastProgressTicks);
                            if (elapsed - last >= NO_PROGRESS_TIMEOUT_MS)
                            {
                                reporter.Report(
                                    new UIUpdate(Details: "No progress detected. Restarting...")
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

                svc.DownloadProgressChanged += (s, e) =>
                {
                    int whole = (int)e.ProgressPercentage;
                    long now = stopwatch.ElapsedMilliseconds;

                    // mark progress (bytes or percent)
                    Interlocked.Exchange(ref lastProgressTicks, now);

                    // Always update byte counts so the list can show size / total even when percent throttles
                    try
                    {
                        downloadManager.UpdateDownloadBytes(productId, e.ReceivedBytesSize, e.TotalBytesToReceive);
                    }
                    catch
                    {
                        // ignore (UI-only enhancement)
                    }

                    // Keep AppPage right-side details in sync (throttled)
                    if (now - lastReportTicks >= THROTTLE_MS || whole == 100)
                    {
                        double receivedMB = e.ReceivedBytesSize / (1024.0 * 1024.0);
                        double totalMB = e.TotalBytesToReceive / (1024.0 * 1024.0);

                        reporter.Report(
                            new UIUpdate(
                                Progress: e.ProgressPercentage,
                                Details: $"{whole}% • {receivedMB:F1} / {totalMB:F0} MB"
                            )
                        );
                    }

                    if (whole > lastWholePercent)
                    {
                        if (now - lastReportTicks < THROTTLE_MS && whole != 100)
                            return;
                        lastWholePercent = whole;
                        lastReportTicks = now;

                        downloadManager.UpdateDownloadProgress(productId, e.ProgressPercentage);
                    }
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
                        reporter.Report(
                            new UIUpdate(
                                Details: $"Restarting... retrying in {delayMs / 1000.0:F1}s..."
                            )
                        );
                        await Task.Delay(delayMs, token).ConfigureAwait(false);
                        continue;
                    }

                    hadError = true;
                    reporter.Report(
                        new UIUpdate(Status: "Error: Download stalled and exhausted retries.")
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
                    reporter.Report(
                        new UIUpdate(
                            Details: $"Temporary network/server issue. Retrying in {delayMs / 1000.0:F1}s..."
                        )
                    );
                    await Task.Delay(delayMs, token).ConfigureAwait(false);
                }
                catch (Exception ex) when (attempt < MAX_RETRIES_PER_FILE)
                {
                    var delayMs = GetRetryDelayMs(null, attempt, MAX_BACKOFF_MS);
                    reporter.Report(
                        new UIUpdate(
                            Details: $"Error: {ex.Message}. Retrying in {delayMs / 1000.0:F1}s..."
                        )
                    );
                    await Task.Delay(delayMs, token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    hadError = true;
                    reporter.Report(
                        new UIUpdate(
                            Status: $"Error: {ex.Message}",
                            Details: "Check network or disk space."
                        )
                    );
                    break;
                }
            }

            if (cancelled || hadError)
                break;

            if (!downloaded)
            {
                hadError = true;
                reporter.Report(new UIUpdate(Status: "Error: Download failed after retries."));
                break;
            }
        }

        // End of batch: finalize UI
        updateService.StopStatusAnimation();
        animator.Stop(downloadItem);

        // Clear details so next phase (install) starts clean
        reporter.Report(new UIUpdate(Details: string.Empty));

        if (cancelled)
        {
            downloadManager.UpdateDownloadStatusText(productId, "Download canceled.");
            try
            {
                downloadManager.UpdateDownloadBytes(productId, null, null);
            }
            catch { }
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

        reporter.Report(new UIUpdate(Progress: 100, Details: string.Empty));

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

        // Animated dots in the Downloads list during install as well.
        animator.Start(downloadItem, "Installing");

        var mainPackagePath = downloadItem?.DownloadedFilePaths.FirstOrDefault(p =>
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
            var installProgress = new Progress<AppPackageInstaller.InstallProgress>(p =>
            {
                var percent = Math.Clamp(p.Percent, 0, 100);
                downloadManager.UpdateDownloadProgress(productId, percent);

                // Keep the Downloads list status simple during install.
                downloadManager.UpdateDownloadStatusText(productId, "Installing");
            });

            await AppPackageInstaller.InstallAsync(
                mainPackagePath,
                dependencyPackagePaths: depPaths,
                progress: installProgress,
                cancellationToken: token
            );

            updateService.StopStatusAnimation();
            downloadManager.UpdateDownloadStatusText(productId, null);
            downloadManager.UpdateDownloadStatus(productId, test.Models.DownloadStatus.Completed);
        }
        catch (OperationCanceledException)
        {
            updateService.StopStatusAnimation();
            downloadManager.UpdateDownloadStatusText(productId, "Install canceled.");
            downloadManager.UpdateDownloadStatus(productId, test.Models.DownloadStatus.Cancelled);
        }
        catch (Exception ex)
        {
            updateService.StopStatusAnimation();
            downloadManager.UpdateDownloadStatusText(productId, $"Install failed: {ex.Message}");
            downloadManager.UpdateDownloadStatus(productId, test.Models.DownloadStatus.Failed);
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
