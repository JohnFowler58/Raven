namespace Raven.Helpers;

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
            // Skip when the major components differ by more than 100× — this indicates
            // the two versions use incompatible schemes (e.g. semantic 0.1.0.1 vs
            // calendar-based 2018.2.3.3) and cannot be meaningfully compared.
            var installedMajor = Math.Max(1, installedV.Major);
            var storeMajor = Math.Max(1, storeV.Major);
            var ratio = (double)Math.Max(storeMajor, installedMajor)
                              / Math.Min(storeMajor, installedMajor);
            if (ratio > 100)
                return false;

            return storeV > installedV;
        }

        return false;
    }
}
