using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Raven.Services;

/// <summary>
/// Checks whether UWP/MSIX sideloading is enabled on the current device.
///
/// Detection logic:
///   1. Check Group Policy override key first (takes precedence over local setting).
///   2. Fall back to the local AppModelUnlock key.
///   3. If neither key is present, infer from OS build:
///      - Build < 19041 (pre-2004): treat as Disabled (sideloading was off by default).
///      - Build >= 19041 (2004+):   treat as Enabled (sideloading on by default, toggle removed).
/// </summary>
public static class SideloadingCheckService
{
    private const string PolicyKey = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Appx";
    private const string AppModelUnlockKey = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock";
    private const string ValueName = "AllowAllTrustedApps";

    // Build 19041 = Windows 10 2004, where sideloading became enabled by default.
    private const int SideloadingDefaultEnabledBuild = 19041;

    /// <summary>
    /// Returns <c>true</c> if sideloading is enabled, <c>false</c> if disabled.
    /// On any unexpected error, returns <c>true</c> to avoid blocking the user.
    /// </summary>
    public static bool IsSideloadingEnabled(ILogger? logger = null)
    {
        try
        {
            // Step 1: Check Group Policy key (highest precedence).
            var policyValue = Registry.GetValue(PolicyKey, ValueName, null);
            if (policyValue is int policyInt)
            {
                var policyEnabled = policyInt == 1;
                logger?.LogInformation(
                    "Sideloading: Group Policy key present, AllowAllTrustedApps={Value} → {State}",
                    policyInt, policyEnabled ? "Enabled" : "Disabled");
                return policyEnabled;
            }

            // Step 2: Check local AppModelUnlock key.
            var localValue = Registry.GetValue(AppModelUnlockKey, ValueName, null);
            if (localValue is int localInt)
            {
                var localEnabled = localInt == 1;
                logger?.LogInformation(
                    "Sideloading: AppModelUnlock key present, AllowAllTrustedApps={Value} → {State}",
                    localInt, localEnabled ? "Enabled" : "Disabled");
                return localEnabled;
            }

            // Step 3: Neither key present — infer from OS build.
            var osBuild = Environment.OSVersion.Version.Build;
            var defaultEnabled = osBuild >= SideloadingDefaultEnabledBuild;

            logger?.LogInformation(
                "Sideloading: No registry key found. OS Build={Build} → treating as {State}",
                osBuild, defaultEnabled ? "Enabled" : "Disabled");

            return defaultEnabled;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Sideloading: Failed to read registry, assuming enabled");
            return true;
        }
    }
}
