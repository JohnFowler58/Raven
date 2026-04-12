using System.Diagnostics;
using System.Security.Cryptography;

if (!TryParseArguments(args, out var options))
{
    return 1;
}

try
{
    var updater = new Updater(options);
    updater.Run();
    return 0;
}
catch
{
    return 1;
}

static bool TryParseArguments(string[] args, out UpdateOptions options)
{
    options = new UpdateOptions(0, string.Empty, string.Empty, string.Empty, string.Empty);

    string? pidArg = null;
    string? sourceDir = null;
    string? targetDir = null;
    string? executablePath = null;
    string? workspaceDir = null;

    for (var i = 0; i < args.Length - 1; i += 2)
    {
        switch (args[i])
        {
            case "--pid":
                pidArg = args[i + 1];
                break;
            case "--source":
                sourceDir = args[i + 1];
                break;
            case "--target":
                targetDir = args[i + 1];
                break;
            case "--exe":
                executablePath = args[i + 1];
                break;
            case "--workspace":
                workspaceDir = args[i + 1];
                break;
        }
    }

    if (!int.TryParse(pidArg, out var pid))
        return false;

    if (string.IsNullOrWhiteSpace(sourceDir)
        || string.IsNullOrWhiteSpace(targetDir)
        || string.IsNullOrWhiteSpace(executablePath)
        || string.IsNullOrWhiteSpace(workspaceDir))
    {
        return false;
    }

    options = new UpdateOptions(pid, sourceDir, targetDir, executablePath, workspaceDir);
    return true;
}

internal sealed record UpdateOptions(
    int ProcessId,
    string SourceDirectory,
    string TargetDirectory,
    string ExecutablePath,
    string WorkspaceDirectory
);

internal sealed class Updater
{
    private readonly UpdateOptions _options;

    public Updater(UpdateOptions options)
    {
        _options = options;
    }

    public void Run()
    {
        WaitForProcessExit(_options.ProcessId, TimeSpan.FromMinutes(2));

        var sourceFiles = Directory
            .GetFiles(_options.SourceDirectory, "*", SearchOption.AllDirectories)
            .ToList();

        EnsureSourceIntegrity(sourceFiles);

        var backupRoot = Path.Combine(_options.WorkspaceDirectory, "backup");
        var addedFiles = new List<string>();
        var backedUpFiles = new List<(string target, string backup)>();

        try
        {
            foreach (var sourceFile in sourceFiles)
            {
                var relativePath = Path.GetRelativePath(_options.SourceDirectory, sourceFile);
                var targetFile = Path.Combine(_options.TargetDirectory, relativePath);

                var targetDirectory = Path.GetDirectoryName(targetFile);
                if (!string.IsNullOrWhiteSpace(targetDirectory))
                {
                    Directory.CreateDirectory(targetDirectory);
                }

                if (File.Exists(targetFile))
                {
                    var backupFile = Path.Combine(backupRoot, relativePath);
                    var backupDir = Path.GetDirectoryName(backupFile);
                    if (!string.IsNullOrWhiteSpace(backupDir))
                    {
                        Directory.CreateDirectory(backupDir);
                    }

                    File.Copy(targetFile, backupFile, overwrite: true);
                    backedUpFiles.Add((targetFile, backupFile));
                }
                else
                {
                    addedFiles.Add(targetFile);
                }

                CopyWithRetry(sourceFile, targetFile, retries: 5);
                ValidateCopiedFile(sourceFile, targetFile);
            }
        }
        catch
        {
            RollBack(addedFiles, backedUpFiles);
            throw;
        }
        finally
        {
            TryDeleteDirectory(backupRoot);
        }

        Process.Start(
            new ProcessStartInfo
            {
                FileName = _options.ExecutablePath,
                UseShellExecute = true,
                WorkingDirectory = _options.TargetDirectory,
            }
        );

        TryDeleteDirectory(_options.WorkspaceDirectory);
    }

    private static void WaitForProcessExit(int processId, TimeSpan timeout)
    {
        try
        {
            var process = Process.GetProcessById(processId);
            process.WaitForExit((int)timeout.TotalMilliseconds);
        }
        catch (ArgumentException)
        {
        }
    }

    private static void EnsureSourceIntegrity(IReadOnlyCollection<string> sourceFiles)
    {
        if (sourceFiles.Count == 0)
            throw new InvalidOperationException("Update package is empty.");
    }

    private static void CopyWithRetry(string sourceFile, string targetFile, int retries)
    {
        for (var attempt = 1; attempt <= retries; attempt++)
        {
            try
            {
                File.Copy(sourceFile, targetFile, overwrite: true);
                return;
            }
            catch when (attempt < retries)
            {
                Thread.Sleep(300 * attempt);
            }
        }

        File.Copy(sourceFile, targetFile, overwrite: true);
    }

    private static void ValidateCopiedFile(string sourceFile, string targetFile)
    {
        using var sourceStream = File.OpenRead(sourceFile);
        using var targetStream = File.OpenRead(targetFile);

        var sourceHash = SHA256.HashData(sourceStream);
        var targetHash = SHA256.HashData(targetStream);

        if (!sourceHash.AsSpan().SequenceEqual(targetHash))
            throw new IOException($"Integrity check failed for {targetFile}");
    }

    private static void RollBack(
        IEnumerable<string> addedFiles,
        IEnumerable<(string target, string backup)> backedUpFiles
    )
    {
        foreach (var file in addedFiles)
        {
            TryDeleteFile(file);
        }

        foreach (var (target, backup) in backedUpFiles)
        {
            var dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.Copy(backup, target, overwrite: true);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
