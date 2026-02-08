using System.Diagnostics;
using StoreListings.Library;
using test.Models;

namespace test.Helpers;

public static class GetDownloadUrl
{
    private static bool IsLikelyResourceOnlyPackage(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return false;

        var n = fileName;

        // Prune common resource-only / language / scale satellite packages.
        return n.Contains("language-", StringComparison.OrdinalIgnoreCase)
            || n.Contains("_language", StringComparison.OrdinalIgnoreCase)
            || n.Contains("scale-", StringComparison.OrdinalIgnoreCase)
            || n.Contains("_scale", StringComparison.OrdinalIgnoreCase)
            || n.Contains("localization", StringComparison.OrdinalIgnoreCase)
            || n.Contains(".resources", StringComparison.OrdinalIgnoreCase)
            || n.Contains("resources", StringComparison.OrdinalIgnoreCase)
            || n.Contains("resource", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<(
        FE3Handler.SyncUpdatesResponse.Update Update,
        string Url
    )> ReduceFrameworkDependencyFiles(
        IReadOnlyList<(
            FE3Handler.SyncUpdatesResponse.Update Update,
            string Url
        )> latestVersionGroup,
        string archRid
    )
    {
        if (latestVersionGroup.Count == 0)
            return latestVersionGroup;

        bool IsCompatibleArch(string? name)
        {
            name ??= string.Empty;

            var hasX86 = name.Contains("x86", StringComparison.OrdinalIgnoreCase);
            var hasX64 =
                name.Contains("x64", StringComparison.OrdinalIgnoreCase)
                || name.Contains("amd64", StringComparison.OrdinalIgnoreCase);
            var hasArm64 = name.Contains("arm64", StringComparison.OrdinalIgnoreCase);
            var hasArm = name.Contains("arm", StringComparison.OrdinalIgnoreCase) && !hasArm64;
            var neutral = name.Contains("neutral", StringComparison.OrdinalIgnoreCase);

            return archRid switch
            {
                "x64" => hasX64 || neutral || (!hasX86 && !hasX64 && !hasArm && !hasArm64),
                "x86" => hasX86 || neutral || (!hasX86 && !hasX64 && !hasArm && !hasArm64),
                "arm64" => hasArm64 || neutral || (!hasX86 && !hasX64 && !hasArm && !hasArm64),
                "arm" => hasArm || neutral || (!hasX86 && !hasX64 && !hasArm && !hasArm64),
                _ => true,
            };
        }

        // Prefer non-resource packages.
        var nonResource = latestVersionGroup
            .Where(a => !IsLikelyResourceOnlyPackage(a.Update.FileName))
            .ToList();

        // Filter out cross-arch packages (e.g., ARM on x64).
        var archFiltered = nonResource
            .Where(a =>
                IsCompatibleArch(a.Update.FileName)
                || IsCompatibleArch(a.Update.PackageIdentityName)
            )
            .ToList();

        // If filtering removed everything, fall back to previous behavior.
        if (archFiltered.Count == 0)
            return latestVersionGroup;

        // If multiple remain, prefer the best arch match to avoid pulling extra variants.
        static int ScoreArch(string? name, string arch)
        {
            name ??= string.Empty;
            var hasX86 = name.Contains("x86", StringComparison.OrdinalIgnoreCase);
            var hasX64 =
                name.Contains("x64", StringComparison.OrdinalIgnoreCase)
                || name.Contains("amd64", StringComparison.OrdinalIgnoreCase);
            var hasArm64 = name.Contains("arm64", StringComparison.OrdinalIgnoreCase);
            var hasArm = name.Contains("arm", StringComparison.OrdinalIgnoreCase) && !hasArm64;
            var neutral = name.Contains("neutral", StringComparison.OrdinalIgnoreCase);

            return arch switch
            {
                "arm64" => hasArm64 ? 3
                : neutral ? 2
                : 0,
                "arm" => hasArm ? 3
                : neutral ? 2
                : 0,
                "x86" => hasX86 ? 3
                : neutral ? 2
                : 0,
                "x64" => hasX64 ? 3
                : neutral ? 2
                : (!hasArm64 && !hasArm && !hasX86 && !hasX64) ? 1
                : 0,
                _ => 0,
            };
        }

        var best = archFiltered
            .OrderByDescending(a =>
                ScoreArch(a.Update.FileName ?? a.Update.PackageIdentityName, archRid)
            )
            .ThenByDescending(a => (a.Update.FileName ?? string.Empty).Length)
            .FirstOrDefault();

        return best.Update is null ? archFiltered : new[] { best };
    }

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

                var archRid = SystemInfo.GetOsArchRid();
                var osArch = archRid switch
                {
                    "arm64" => OSArch.ARM64,
                    "x86" => OSArch.X86,
                    "arm" => OSArch.ARM,
                    _ => OSArch.AMD64,
                };

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
                    cancellationToken,
                    osArch
                );

                if (!fe3sync.IsSuccess)
                    return null;

                var updates = fe3sync.Value.Updates.ToList();

                // Choose latest applicable main (non-framework) that matches OS + device family + arch
                static IReadOnlyList<string> GetArchPreferenceOrder(string archRid)
                {
                    return archRid switch
                    {
                        // x64 PCs can also run x86 apps (WOW64)
                        "x64" => new[] { "x64", "x86" },
                        // x86 PCs can only run x86 apps
                        "x86" => new[] { "x86" },
                        // ARM64 can run ARM natively, and x64/x86 via emulation
                        "arm64" => new[] { "arm64", "arm", "x64", "x86" },
                        // ARM (32-bit) only runs ARM apps
                        "arm" => new[] { "arm" },
                        _ => new[] { archRid },
                    };
                }

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

                var archPreferences = GetArchPreferenceOrder(archRid);

                var candidates = updates
                    .Where(t =>
                        !t.IsFramework
                        && t.TargetPlatforms.Any(p =>
                            (p.Family == deviceFamily || p.Family == DeviceFamily.Universal)
                            && p.MinVersion <= OSVersion.Value
                        )
                    )
                    .OrderByDescending(t => t.Version)
                    .ToList();

                // Try preferred architectures in order (native first, then compatible fallbacks).
                foreach (var arch in archPreferences)
                {
                    // Find the first candidate whose dependencies are fully applicable
                    foreach (
                        var main in candidates.Where(c =>
                            ArchMatches(c.FileName ?? c.PackageIdentityName, arch)
                        )
                    )
                    {
                        // Fetch download-info only for the chosen main candidate.
                        var mainDownloadInfo = await FE3Handler.GetPackageDownloadInfo(
                            fe3sync.Value.NewCookie,
                            main.UpdateID,
                            main.RevisionNumber,
                            main.Digest,
                            language,
                            market,
                            currentBranch,
                            flightRing,
                            flightingBranchName,
                            OSVersion.Value,
                            deviceFamily,
                            cancellationToken,
                            osArch
                        );

                        if (!mainDownloadInfo.IsSuccess)
                            continue;

                        var dcatMain = packageResult.Value.FirstOrDefault(p =>
                            p.PackageIdentity.Equals(
                                main.PackageIdentityName,
                                StringComparison.OrdinalIgnoreCase
                            )
                            && p.Version == main.Version
                        );

                        var depEntries = new List<FileEntry>();

                        // Accumulate the dependency updates we actually need.
                        var requiredDepUpdates = new List<FE3Handler.SyncUpdatesResponse.Update>();

                        if (dcatMain is not null && dcatMain.FrameworkDependencies.Any())
                        {
                            var allDepsOk = true;

                            foreach (var dep in dcatMain.FrameworkDependencies)
                            {
                                var applicable = updates
                                    .Where(d =>
                                        d.PackageIdentityName.Equals(
                                            dep.PackageIdentity,
                                            StringComparison.OrdinalIgnoreCase
                                        )
                                        && d.Version >= dep.MinVersion
                                        && d.TargetPlatforms.Any(tp =>
                                            tp.MinVersion <= OSVersion.Value
                                            && (
                                                tp.Family == DeviceFamily.Universal
                                                || tp.Family == deviceFamily
                                            )
                                        )
                                        && ArchMatches(
                                            d.FileName ?? d.PackageIdentityName,
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
                                    .GroupBy(a => a.Version)
                                    .OrderByDescending(g => g.Key)
                                    .First()
                                    .ToList();

                                // Reduce using existing helper (operates over a tuple list).
                                var reduced = ReduceFrameworkDependencyFiles(
                                    latestGroup.Select(u => (Update: u, Url: string.Empty)).ToList(),
                                    arch
                                );

                                requiredDepUpdates.AddRange(reduced.Select(r => r.Update));
                            }

                            if (!allDepsOk)
                                continue; // try next candidate
                        }

                        // Fetch download-info only for required dependency updates.
                        foreach (var depUpdate in requiredDepUpdates.Distinct())
                        {
                            var depDownloadInfo = await FE3Handler.GetPackageDownloadInfo(
                                fe3sync.Value.NewCookie,
                                depUpdate.UpdateID,
                                depUpdate.RevisionNumber,
                                depUpdate.Digest,
                                language,
                                market,
                                currentBranch,
                                flightRing,
                                flightingBranchName,
                                OSVersion.Value,
                                deviceFamily,
                                cancellationToken,
                                osArch
                            );

                            if (!depDownloadInfo.IsSuccess)
                            {
                                // If any required dep can't be resolved, try next main candidate.
                                depEntries.Clear();
                                goto NextMainCandidate;
                            }

                            depUpdate.SetDownloadInfoPackageDigest(depDownloadInfo.Value.Package.Digest);
                            depUpdate.SetDownloadInfoBlockmapUrl(depDownloadInfo.Value.BlockmapCab?.Url);
                            depUpdate.SetDownloadInfoBlockmapDigest(depDownloadInfo.Value.BlockmapCab?.Digest);

                            depEntries.Add(
                                new FileEntry(
                                    FileName: depUpdate.FileName,
                                    Url: depDownloadInfo.Value.Package.Url,
                                    Dependencies: Array.Empty<FileEntry>(),
                                    Digest: depUpdate.GetDownloadInfoPackageDigest(),
                                    BlockmapUrl: depUpdate.GetDownloadInfoBlockmapUrl(),
                                    BlockmapCabFileDigest: depUpdate.GetDownloadInfoBlockmapDigest()
                                )
                            );
                        }

                        // Store download-info on main update for downstream cache/delta logic.
                        main.SetDownloadInfoPackageDigest(mainDownloadInfo.Value.Package.Digest);
                        main.SetDownloadInfoBlockmapUrl(mainDownloadInfo.Value.BlockmapCab?.Url);
                        main.SetDownloadInfoBlockmapDigest(mainDownloadInfo.Value.BlockmapCab?.Digest);

                        // Return main with dependencies
                        return new FileEntry(
                            FileName: main.FileName,
                            Url: mainDownloadInfo.Value.Package.Url,
                            Dependencies: depEntries,
                            Digest: main.GetDownloadInfoPackageDigest(),
                            BlockmapUrl: main.GetDownloadInfoBlockmapUrl(),
                            BlockmapCabFileDigest: main.GetDownloadInfoBlockmapDigest()
                        );

                    NextMainCandidate:
                        ;
                    }
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
                var fileName = unpackagedResult.Value.FileName;
                var sha256 = unpackagedResult.Value.InstallerSha256;

                return new FileEntry(
                    FileName: fileName,
                    Url: url,
                    Dependencies: Array.Empty<FileEntry>(),
                    Sha256: sha256
                );
            }
            default:
                return null;
        }
    }
}
