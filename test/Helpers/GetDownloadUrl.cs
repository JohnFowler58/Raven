using StoreListings.Library;
using test.Models;

namespace test.Helpers;

public static class GetDownloadUrl
{
    public static async Task<FileEntry?> fetch(
        string productId,
        CancellationToken cancellationToken = default,
        DeviceFamily deviceFamily = DeviceFamily.Desktop,
        Market market = Market.US,
        Lang language = Lang.en,
        string flightRing = "Retail",
        string flightingBranchName = "Retail",
        StoreListings.Library.Version? OSVersion = null,
        string currentBranch = "ge_release"
    )
    {
        // Resolve OS version if not supplied (exact Windows build).
        OSVersion ??= SystemInfo.GetExactWindowsVersion();

        // 1) Query product
        var result = await StoreEdgeFDProduct.GetProductAsync(
            productId,
            deviceFamily,
            market,
            language,
            cancellationToken
        );

        if (!result.IsSuccess)
            return null;

        var product = result.Value;

        switch (product.InstallerType)
        {
            case InstallerType.Packaged:
            {
                // Query DCAT packages (with framework deps)
                var packageResult = await DCATPackage.GetPackagesAsync(
                    productId,
                    market,
                    language,
                    true
                );

                if (!packageResult.IsSuccess)
                    return null;

                // Ensure at least one package applicable to our OS version
                if (
                    !packageResult.Value.Any(p =>
                        p.PlatformDependencies.Any(pd => pd.MinVersion <= OSVersion.Value)
                    )
                )
                    return null;

                // FE3 cookie + SyncUpdates
                var cookieResult = await FE3Handler.GetCookieAsync(cancellationToken);
                if (!cookieResult.IsSuccess)
                    return null;

                var fe3sync = await FE3Handler.SyncUpdatesAsync(
                    cookieResult.Value,
                    packageResult.Value.First().WuCategoryId,
                    language,
                    market,
                    currentBranch,
                    flightRing,
                    flightingBranchName,
                    OSVersion.Value,
                    deviceFamily,
                    cancellationToken
                );

                if (!fe3sync.IsSuccess)
                    return null;

                // Resolve all file URLs once
                var updatesAndUrl = new List<(
                    FE3Handler.SyncUpdatesResponse.Update Update,
                    string Url
                )>(fe3sync.Value.Updates.Count());
                foreach (var update in fe3sync.Value.Updates)
                {
                    var fileUrlResult = await FE3Handler.GetFileUrl(
                        fe3sync.Value.NewCookie,
                        update.UpdateID,
                        update.RevisionNumber,
                        update.Digest,
                        language,
                        market,
                        currentBranch,
                        flightRing,
                        flightingBranchName,
                        OSVersion.Value,
                        deviceFamily,
                        cancellationToken
                    );

                    if (fileUrlResult.IsSuccess)
                        updatesAndUrl.Add((update, fileUrlResult.Value));
                }

                // Choose latest applicable main (non-framework) that matches OS + device family + arch
                var arch = SystemInfo.GetOsArchRid();

                static bool ArchMatches(string name, string archRid)
                {
                    name ??= string.Empty;
                    var hasX86 = name.Contains("x86", StringComparison.OrdinalIgnoreCase);
                    var hasX64 =
                        name.Contains("x64", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("amd64", StringComparison.OrdinalIgnoreCase);
                    var hasArm64 = name.Contains("arm64", StringComparison.OrdinalIgnoreCase);
                    var neutral = name.Contains("neutral", StringComparison.OrdinalIgnoreCase);

                    return archRid switch
                    {
                        "arm64" => hasArm64 || neutral,
                        "x64" => hasX64 || neutral || (!hasArm64 && !hasX86 && !hasX64),
                        "x86" => hasX86 || neutral,
                        _ => true,
                    };
                }

                var candidates = updatesAndUrl
                    .Where(t =>
                        !t.Update.IsFramework
                        && t.Update.TargetPlatforms.Any(p =>
                            (p.Family == deviceFamily || p.Family == DeviceFamily.Universal)
                            && p.MinVersion <= OSVersion.Value
                        )
                    )
                    .OrderByDescending(t => t.Update.Version)
                    .ToList();

                // Find the first candidate whose dependencies are fully applicable
                foreach (
                    var main in candidates.Where(c =>
                        ArchMatches(c.Update.FileName ?? c.Update.PackageIdentityName, arch)
                    )
                )
                {
                    var dcatMain = packageResult.Value.FirstOrDefault(p =>
                        p.PackageIdentity.Equals(
                            main.Update.PackageIdentityName,
                            StringComparison.OrdinalIgnoreCase
                        )
                        && p.Version == main.Update.Version
                    );

                    var depEntries = new List<FileEntry>();

                    if (dcatMain is not null && dcatMain.FrameworkDependencies.Any())
                    {
                        var allDepsOk = true;

                        foreach (var dep in dcatMain.FrameworkDependencies)
                        {
                            var applicable = updatesAndUrl
                                .Where(d =>
                                    d.Update.PackageIdentityName.Equals(
                                        dep.PackageIdentity,
                                        StringComparison.OrdinalIgnoreCase
                                    )
                                    && d.Update.Version >= dep.MinVersion
                                    && d.Update.TargetPlatforms.Any(tp =>
                                        tp.MinVersion <= OSVersion.Value
                                        && (
                                            tp.Family == DeviceFamily.Universal
                                            || tp.Family == deviceFamily
                                        )
                                    )
                                    && ArchMatches(
                                        d.Update.FileName ?? d.Update.PackageIdentityName,
                                        arch
                                    )
                                )
                                .ToList();

                            if (!applicable.Any())
                            {
                                allDepsOk = false;
                                break;
                            }

                            var latestGroup = applicable
                                .GroupBy(a => a.Update.Version)
                                .OrderByDescending(g => g.Key)
                                .First();

                            foreach (var a in latestGroup)
                            {
                                depEntries.Add(
                                    new FileEntry(
                                        FileName: a.Update.FileName,
                                        Url: a.Url,
                                        Dependencies: Array.Empty<FileEntry>()
                                    )
                                );
                            }
                        }

                        if (!allDepsOk)
                            continue; // try next candidate
                    }

                    // Return main with dependencies
                    return new FileEntry(
                        FileName: main.Update.FileName,
                        Url: main.Url,
                        Dependencies: depEntries
                    );
                }

                // No applicable candidate found
                return null;
            }

            case InstallerType.Unpackaged:
            {
                // Query unpackaged installer
                var unpackagedResult = await product.GetUnpackagedInstall(
                    market,
                    language,
                    cancellationToken
                );
                if (!unpackagedResult.IsSuccess)
                    return null;

                var url = unpackagedResult.Value.InstallerUrl;
                string fileName;
                try
                {
                    fileName = Path.GetFileName(new Uri(url).AbsolutePath);
                    if (string.IsNullOrEmpty(fileName))
                        fileName = "installer";
                }
                catch
                {
                    fileName = "installer";
                }

                return new FileEntry(
                    FileName: fileName,
                    Url: url,
                    Dependencies: Array.Empty<FileEntry>()
                );
            }

            case InstallerType.Unknown:
            default:
                return null;
        }
    }
}
