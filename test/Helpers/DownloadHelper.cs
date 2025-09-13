using System.Collections.Concurrent;
using Downloader;
using test.Models;
using test.Services;

namespace test.Helpers;

public sealed class DownloadHelper
{
    public static async Task StartDownloadAsync(
        FileEntry entry,
        CancellationToken token,
        UIUpdateService updateService
    )
    {
        const int THROTTLE_MS = 500;
        var reporter = updateService.GetReporter();

        // Flatten dependency graph (dependencies first), skip duplicate URLs
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

        if (flattened.Count == 0)
        {
            reporter.Report(
                new UIUpdate(
                    Status: "No files to download.",
                    Progress: 100,
                    Details: "Nothing to do."
                )
            );
            return;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Aggregated state
        var perFileProgress = new ConcurrentDictionary<string, (long received, long total)>();
        long lastReportTicks = 0;
        double lastPercentReported = -1;
        int totalFiles = flattened.Count;
        int completedFiles = 0;
        int failedFiles = 0;
        int canceledFiles = 0;
        int prevfailed = -1;
        var animationEnabled = false;

        object aggLock = new();

        void EmitProgressIfNeeded()
        {
            long now = stopwatch.ElapsedMilliseconds;
            if (now - lastReportTicks < THROTTLE_MS)
                return;

            // Aggregate
            long aggReceived = 0;
            long aggTotal = 0;
            foreach (var v in perFileProgress.Values)
            {
                aggReceived += v.received;
                // Some servers may not provide total (-1 or 0). Only add positive totals.
                if (v.total > 0)
                    aggTotal += v.total;
            }

            double percent = 0;
            if (aggTotal > 0)
                percent = (double)aggReceived / aggTotal * 100.0;
            else
            {
                // Fallback: average of known percentages
                var known = perFileProgress.Values.Where(v => v.total > 0).ToList();
                if (known.Count > 0)
                    percent = known.Average(v => (double)v.received / v.total * 100.0);
            }

            // Avoid spamming identical percent
            if (Math.Floor(percent) == Math.Floor(lastPercentReported))
            {
                if (percent < 100)
                    return;
            }

            lastPercentReported = percent;
            lastReportTicks = now;

            double receivedMB = aggReceived / (1024.0 * 1024.0);
            double totalMB = aggTotal > 0 ? aggTotal / (1024.0 * 1024.0) : 0;

            string details;
            if (aggTotal > 0)
                details =
                    $"{percent:F0}% • {receivedMB:F1} / {totalMB:F1} MB • {completedFiles}/{totalFiles} files";
            else
                details =
                    $"{percent:F0}% • {receivedMB:F1} MB received • {completedFiles}/{totalFiles} files";

            if (failedFiles > 0)
            {
                if (failedFiles != prevfailed)
                {
                    updateService.StartStatusAnimation($"Downloading ({failedFiles} failed)");
                    prevfailed = failedFiles;
                }
            }
            else
            {
                if (!animationEnabled)
                {
                    updateService.StartStatusAnimation($"Downloading {totalFiles} file(s)");
                    animationEnabled = true;
                }
            }
            reporter.Report(new UIUpdate(Progress: percent, Details: details));
        }

        var config = new DownloadConfiguration
        {
            // Each file itself can be chunked; multiple files run in parallel (one service per file)
            ChunkCount = 8,
            ParallelDownload = true,
            Timeout = 100000,
        };

        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

        // Create tasks
        var downloadTasks = new List<Task>();

        foreach (var file in flattened)
        {
            token.ThrowIfCancellationRequested();

            string destinationPath = Path.Combine(desktop, file.FileName);
            var destinationDir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(destinationDir) && !Directory.Exists(destinationDir))
                Directory.CreateDirectory(destinationDir);

            var svc = new DownloadService(config);

            // Capture for closure
            string url = file.Url;
            string fileName = file.FileName;

            svc.DownloadStarted += (_, e) =>
            {
                lock (aggLock)
                {
                    // Initialize with unknown total until first progress
                    perFileProgress[url] = (0, e.TotalBytesToReceive);
                    EmitProgressIfNeeded();
                }
            };

            svc.DownloadProgressChanged += (_, e) =>
            {
                if (token.IsCancellationRequested)
                {
                    updateService.StartStatusAnimation("Cancelling");
                }

                lock (aggLock)
                {
                    perFileProgress[url] = (e.ReceivedBytesSize, e.TotalBytesToReceive);
                    EmitProgressIfNeeded();
                }
            };

            svc.DownloadFileCompleted += (_, e) =>
            {
                lock (aggLock)
                {
                    if (e.Cancelled)
                    {
                        canceledFiles++;
                    }
                    else if (e.Error != null)
                    {
                        failedFiles++;
                    }
                    else
                    {
                        completedFiles++;
                        // Ensure final progress for this file registers as full
                        if (perFileProgress.TryGetValue(url, out var cur))
                        {
                            long total = cur.total > 0 ? cur.total : cur.received;
                            perFileProgress[url] = (total, total);
                        }
                    }

                    EmitProgressIfNeeded();
                }
            };

            async Task RunAsync()
            {
                try
                {
                    await svc.DownloadFileTaskAsync(url, destinationPath, token);
                }
                catch (OperationCanceledException)
                {
                    // Count as canceled, handled in completed event (or here if not raised)
                    lock (aggLock)
                    {
                        if (!perFileProgress.ContainsKey(url))
                            perFileProgress[url] = (0, 0);
                        canceledFiles++;
                        EmitProgressIfNeeded();
                    }
                }
                catch (Exception ex)
                {
                    lock (aggLock)
                    {
                        failedFiles++;
                        reporter.Report(
                            new UIUpdate(
                                Status: $"Error downloading {fileName}",
                                Details: ex.Message
                            )
                        );
                        EmitProgressIfNeeded();
                    }
                }
            }

            downloadTasks.Add(RunAsync());
        }

        try
        {
            await Task.WhenAll(downloadTasks);
        }
        catch (OperationCanceledException)
        {
            // Aggregated cancellation
        }

        // Final UI state
        lock (aggLock)
        {
            updateService.StopStatusAnimation();

            bool wasCancelled =
                token.IsCancellationRequested
                || canceledFiles > 0 && completedFiles + failedFiles + canceledFiles == totalFiles;

            if (wasCancelled)
            {
                reporter.Report(
                    new UIUpdate(
                        Status: "Download canceled.",
                        Details: $"{completedFiles} completed, {failedFiles} failed, {canceledFiles} canceled."
                    )
                );
            }
            else if (failedFiles > 0)
            {
                reporter.Report(
                    new UIUpdate(
                        Status: "Completed with errors.",
                        Progress: 100,
                        Details: $"{completedFiles} succeeded, {failedFiles} failed."
                    )
                );
            }
            else
            {
                reporter.Report(
                    new UIUpdate(
                        Status: "All downloads completed successfully!",
                        Progress: 100,
                        Details: $"{completedFiles} file(s) downloaded."
                    )
                );
            }
        }
    }
}
