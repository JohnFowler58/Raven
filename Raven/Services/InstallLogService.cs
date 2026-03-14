using System.Text;

namespace Raven.Services;

public static class InstallLogService
{
    private static readonly object _lock = new();

    public static string LogFilePath { get; } = Path.Combine(
        AppContext.BaseDirectory,
        "install.log"
    );

    private static readonly object _writeGate = new();
    private static Task _writeTask = Task.CompletedTask;

    public static void WriteLine(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";

        // Queue log writes so we never block the UI thread.
        lock (_writeGate)
        {
            _writeTask = _writeTask.ContinueWith(
                _ => WriteLineAsync(line),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default
            ).Unwrap();
        }
    }

    private static async Task WriteLineAsync(string line)
    {
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogFilePath)!);
            }

            await File.AppendAllTextAsync(LogFilePath, line + Environment.NewLine, Encoding.UTF8)
                .ConfigureAwait(false);
        }
        catch
        {
            // Swallow logging failures.
        }
    }

    public static void WriteException(string context, Exception ex)
    {
        WriteLine($"{context}: {ex.GetType().Name} (0x{ex.HResult:X8}) {ex.Message}");
    }
}
