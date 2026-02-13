namespace test.Helpers;

public static class VersionComparison
{
    public static bool IsStoreNewer(string? storeVersion, string? installedVersion)
    {
        if (string.IsNullOrWhiteSpace(storeVersion) || string.IsNullOrWhiteSpace(installedVersion))
            return false;

        if (
            System.Version.TryParse(storeVersion, out var storeV)
            && System.Version.TryParse(installedVersion, out var installedV)
        )
        {
            return storeV > installedV;
        }

        return false;
    }
}
