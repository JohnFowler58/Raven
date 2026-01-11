using System.Diagnostics;
using Downloader;
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
        var reporter = updateService.GetReporter();
        var downloadManager = DownloadManagerService.Instance;

        // Clear any leftover details from previous attempts
        reporter.Report(new UIUpdate(Progress: 0, Details: string.Empty));

        // Per-file progress tracking state
        int lastWholePercent = -1;
        long lastReportTicks = 0;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var config = new DownloadConfiguration
        {
            ChunkCount = 4,
            ParallelDownload = true,
            Timeout = 30000,
            ParallelCount = 2,
            BufferBlockSize = 8192,
            MaximumBytesPerSecond = 0,
            MinimumSizeOfChunking = 1024,
            ReserveStorageSpaceBeforeStartingDownload = true,
        };

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

        // Single continuous animation for the page status
        updateService.StartStatusAnimation(
            $"Downloading ({currentFileIndex}/{totalFiles}) {FilesLabel()}"
        );

        // Also make DownloadItem.StatusText stable
        downloadManager.UpdateDownloadStatusText(
            productId,
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
            downloadManager.UpdateDownloadStatusText(
                productId,
                $"Downloading ({currentFileIndex}/{totalFiles}) {FilesLabel()}"
            );

            var file = flattened[i];

            string destinationPath = Path.Combine(
                AppContext.BaseDirectory,
                "downloads",
                Path.GetFileName(file.FileName)
            );

            Debug.WriteLine(destinationPath);
            var destinationDir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(destinationDir) && !Directory.Exists(destinationDir))
                Directory.CreateDirectory(destinationDir);

            // Reset per-file progress tracking before starting each file
            lastWholePercent = -1;
            lastReportTicks = 0;
            stopwatch.Restart();

            reporter.Report(new UIUpdate(Progress: 0, Details: string.Empty));

            // Create a fresh DownloadService for each file to avoid state leakage
            using var svc = new DownloadService(config);

            bool currentFileCancelled = false;

            // Bridge external cancellation into the download service
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

            svc.DownloadProgressChanged += (s, e) =>
            {
                int whole = (int)e.ProgressPercentage;
                long now = stopwatch.ElapsedMilliseconds;

                if (whole > lastWholePercent)
                {
                    if (now - lastReportTicks < THROTTLE_MS && whole != 100)
                        return;
                    lastWholePercent = whole;
                    lastReportTicks = now;

                    double receivedMB = e.ReceivedBytesSize / (1024.0 * 1024.0);
                    double totalMB = e.TotalBytesToReceive / (1024.0 * 1024.0);

                    reporter.Report(
                        new UIUpdate(
                            Progress: e.ProgressPercentage,
                            Details: $"{whole}% • {receivedMB:F1} / {totalMB:F0} MB"
                        )
                    );

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
                await svc.DownloadFileTaskAsync(file.Url, destinationPath, token)
                    .ConfigureAwait(false);

                if (token.IsCancellationRequested || currentFileCancelled)
                {
                    cancelled = true;
                    break;
                }

                downloadManager.AddDownloadedFilePath(productId, destinationPath);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                break;
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

        // End of batch: finalize UI
        updateService.StopStatusAnimation();

        // Clear details so next phase (install) starts clean
        reporter.Report(new UIUpdate(Details: string.Empty));

        if (cancelled)
        {
            downloadManager.UpdateDownloadStatusText(productId, "Download canceled.");
        }
        else if (!hadError)
        {
            reporter.Report(new UIUpdate(Progress: 100, Details: string.Empty));
        }
    }
}
