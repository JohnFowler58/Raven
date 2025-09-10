using System.Runtime.InteropServices;
using StoreListings.Library;
using Windows.System.Profile;

namespace test.Helpers;

static class SystemInfo
{
    // Prefer exact Windows build (works well in packaged WinUI 3):
    public static StoreListings.Library.Version GetExactWindowsVersion()
    {
        // AnalyticsInfo.VersionInfo.DeviceFamilyVersion is a ulong encoded as:
        // major (16b), minor (16b), build (16b), revision (16b).
        var vStr = AnalyticsInfo.VersionInfo.DeviceFamilyVersion;
        if (ulong.TryParse(vStr, out var v))
        {
            var major = (uint)((v >> 48) & 0xFFFFu);
            var minor = (uint)((v >> 32) & 0xFFFFu);
            var build = (uint)((v >> 16) & 0xFFFFu);
            var rev = (uint)(v & 0xFFFFu);
            return new StoreListings.Library.Version(major, minor, build, rev);
        }
        // Fallback if parsing fails:
        return new StoreListings.Library.Version(10u, 0u, 19045u, 0u);
    }

    public static string GetOsArchRid()
    {
        return RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => "x64",
        };
    }
}
