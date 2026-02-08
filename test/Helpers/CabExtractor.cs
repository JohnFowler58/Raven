using System.Diagnostics;

namespace test.Helpers;

public static class CabExtractor
{
    public static async Task<string> ExtractFileToTempAsync(
        string cabPath,
        string fileNameInCab,
        string destinationPath,
        CancellationToken token
    )
    {
        if (!File.Exists(cabPath))
            throw new FileNotFoundException("CAB not found", cabPath);

        var destDir = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrWhiteSpace(destDir))
            throw new ArgumentException("Destination directory is invalid.", nameof(destinationPath));
        Directory.CreateDirectory(destDir);

        // expand.exe is available on Windows and can extract CABs.
        var psi = new ProcessStartInfo
        {
            FileName = "expand.exe",
            // Use -R to ignore internal directory structure and always write to the specified directory.
            // Use -F: to select the file.
            Arguments = $"-R \"{cabPath}\" -F:{fileNameInCab} \"{destDir}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start expand.exe");

        await p.WaitForExitAsync(token).ConfigureAwait(false);

        if (p.ExitCode != 0)
        {
            var stderr = await p.StandardError.ReadToEndAsync(token).ConfigureAwait(false);
            throw new InvalidOperationException($"CAB extraction failed (exit {p.ExitCode}): {stderr}");
        }

        // expand.exe writes to destDir (and may preserve subfolders). Find the extracted file.
        var found = Directory.EnumerateFiles(destDir, fileNameInCab, SearchOption.AllDirectories).FirstOrDefault();
        if (found is null)
            throw new FileNotFoundException($"{fileNameInCab} not found after CAB extraction.");

        if (!string.Equals(found, destinationPath, StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(destinationPath))
                File.Delete(destinationPath);
            File.Move(found, destinationPath);
        }

        return destinationPath;
    }
}
