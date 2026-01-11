using Windows.Management.Deployment;

namespace test.Services;

public static class AppPackageInstaller
{
    public sealed record InstallProgress(int Percent, string? State, string? Activity);

    private static readonly string[] SupportedExtensions =
    [
        ".msix",
        ".appx",
        ".msixbundle",
        ".appxbundle",
    ];

    private static bool IsPackageFile(string path)
    {
        var ext = Path.GetExtension(path);
        return SupportedExtensions.Any(e => ext.Equals(e, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task AddPackageAsync(
        PackageManager packageManager,
        string packagePath,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken
    )
    {
        var packageUri = new Uri(packagePath);

        var deploymentOperation = packageManager.AddPackageAsync(
            packageUri,
            Array.Empty<Uri>(),
            DeploymentOptions.ForceApplicationShutdown
        );

        deploymentOperation.Progress = (_, p) =>
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            var percent = (int)Math.Clamp(p.percentage, 0, 100);
            progress?.Report(new InstallProgress(percent, p.state.ToString(), "Install"));
        };

        var result = await deploymentOperation.AsTask(cancellationToken);

        if (result.ErrorText is { Length: > 0 })
            throw new InvalidOperationException(result.ErrorText);
    }

    public static async Task InstallAsync(
        string packagePath,
        IEnumerable<string>? dependencyPackagePaths = null,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(packagePath))
            throw new ArgumentException("Package path is required.", nameof(packagePath));

        if (!File.Exists(packagePath))
            throw new FileNotFoundException("Package file not found.", packagePath);

        var deps = (dependencyPackagePaths ?? Array.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(File.Exists)
            .Where(IsPackageFile)
            .ToList();

        progress?.Report(new InstallProgress(0, "Starting", "Install"));

        var packageManager = new PackageManager();

        // Install main package first. If this fails, the install fails.
        try
        {
            await AddPackageAsync(packageManager, packagePath, progress, cancellationToken);
        }
        catch (Exception ex)
        {
            InstallLogService.WriteLine($"MAIN INSTALL FAILED: {packagePath}");
            InstallLogService.WriteException("Main package install error", ex);
            throw;
        }

        // Best-effort install for dependencies: ignore failures, but log them.
        foreach (var dep in deps)
        {
            try
            {
                await AddPackageAsync(packageManager, dep, progress: null, cancellationToken);
            }
            catch (Exception ex)
            {
                InstallLogService.WriteLine($"DEPENDENCY INSTALL FAILED (ignored): {dep}");
                InstallLogService.WriteException("Dependency package install error", ex);
            }
        }

        progress?.Report(new InstallProgress(100, "Completed", "Install"));
    }
}
