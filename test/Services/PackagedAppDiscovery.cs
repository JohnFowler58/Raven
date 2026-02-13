using System.Diagnostics;
using Windows.Management.Deployment;

namespace test.Services;

public static class PackagedAppDiscovery
{
    public sealed record InstalledAppInfo(bool IsInstalled);

    public enum LaunchFailureReason
    {
        None = 0,
        PackageFamilyNameMissing,
        NotInstalled,
        NoAppEntries,
        LaunchFailed,
    }

    public static string? GetInstalledVersion(string? packageFamilyName)
    {
        if (string.IsNullOrWhiteSpace(packageFamilyName))
            return null;

        try
        {
            var pm = new PackageManager();
            var pkg = pm.FindPackagesForUser(string.Empty, packageFamilyName).FirstOrDefault();
            var v = pkg?.Id?.Version;
            return v == null
                ? null
                : $"{v.Value.Major}.{v.Value.Minor}.{v.Value.Build}.{v.Value.Revision}";
        }
        catch
        {
            return null;
        }
    }

    public sealed record PackagedLaunchResult(
        bool Success,
        string? LaunchedAppUserModelId,
        LaunchFailureReason FailureReason
    );

    public static bool IsInstalled(string? packageFamilyName)
    {
        var info = GetInstalledInfo(packageFamilyName);
        return info.IsInstalled;
    }

    public static InstalledAppInfo GetInstalledInfo(string? packageFamilyName)
    {
        if (string.IsNullOrWhiteSpace(packageFamilyName))
            return new InstalledAppInfo(false);

        try
        {
            var pm = new PackageManager();
            var pkg = pm.FindPackagesForUser(string.Empty, packageFamilyName).FirstOrDefault();
            return new InstalledAppInfo(pkg != null);
        }
        catch
        {
            return new InstalledAppInfo(false);
        }
    }

    public static async Task<bool> TryLaunchAsync(string? packageFamilyName)
    {
        var result = await TryLaunchDetailedAsync(packageFamilyName);
        return result.Success;
    }

    public static async Task<PackagedLaunchResult> TryLaunchDetailedAsync(string? packageFamilyName)
    {
        if (string.IsNullOrWhiteSpace(packageFamilyName))
        {
            return new PackagedLaunchResult(
                Success: false,
                LaunchedAppUserModelId: null,
                FailureReason: LaunchFailureReason.PackageFamilyNameMissing
            );
        }

        try
        {
            var pm = new PackageManager();
            var pkg = pm.FindPackagesForUser(string.Empty, packageFamilyName).FirstOrDefault();
            if (pkg == null)
            {
                return new PackagedLaunchResult(
                    Success: false,
                    LaunchedAppUserModelId: null,
                    FailureReason: LaunchFailureReason.NotInstalled
                );
            }

            var entries = await pkg.GetAppListEntriesAsync();
            var entry = entries.FirstOrDefault();
            if (entry == null)
            {
                return new PackagedLaunchResult(
                    Success: false,
                    LaunchedAppUserModelId: null,
                    FailureReason: LaunchFailureReason.NoAppEntries
                );
            }

            await entry.LaunchAsync();
            return new PackagedLaunchResult(
                Success: true,
                LaunchedAppUserModelId: entry.AppUserModelId,
                FailureReason: LaunchFailureReason.None
            );
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return new PackagedLaunchResult(
                Success: false,
                LaunchedAppUserModelId: null,
                FailureReason: LaunchFailureReason.LaunchFailed
            );
        }
    }
}
