using System.Text;

namespace test.Services;

public static class InstallLogService
{
    private static readonly object _lock = new();

    public static string LogFilePath { get; } = Path.Combine(
        AppContext.BaseDirectory,
        "install.log"
    );

    public static void WriteLine(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";

        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogFilePath)!);
            File.AppendAllText(LogFilePath, line + Environment.NewLine, Encoding.UTF8);
        }
    }

    public static void WriteException(string context, Exception ex)
    {
        WriteLine($"{context}: {ex.GetType().Name} (0x{ex.HResult:X8}) {ex.Message}");
    }
}
