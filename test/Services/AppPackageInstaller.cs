using Windows.Management.Deployment;

namespace test.Services;

public static class AppPackageInstaller
{
    public sealed record InstallProgress(int Percent, string? State, string? Activity);

    public static async Task InstallAsync(
        string packagePath,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(packagePath))
            throw new ArgumentException("Package path is required.", nameof(packagePath));

        if (!File.Exists(packagePath))
            throw new FileNotFoundException("Package file not found.", packagePath);

        progress?.Report(new InstallProgress(0, "Starting", "Install"));

        var packageUri = new Uri(packagePath);

        var packageManager = new PackageManager();

        var deploymentOperation = packageManager.AddPackageAsync(
            packageUri,
            null,
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
        {
            throw new InvalidOperationException(result.ErrorText);
        }

        progress?.Report(new InstallProgress(100, "Completed", "Install"));
    }
}
