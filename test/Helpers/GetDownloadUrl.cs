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

        // Prefer non-resource packages.
        var nonResource = latestVersionGroup
            .Where(a => !IsLikelyResourceOnlyPackage(a.Update.FileName))
            .ToList();

        var candidates = nonResource.Count > 0 ? nonResource : latestVersionGroup.ToList();
        var priorities = Utils.GetArchPriorities(archRid, isPackaged: true);

        foreach (var pref in priorities)
        {
            var matches = candidates
                .Where(a =>
                    Utils.ParseArchString(
                        a.Update.FileName ?? a.Update.PackageIdentityName,
                        isPackaged: true
                    ) == pref
                )
                .ToList();

            if (matches.Any())
            {
                // If multiple remain, pick the shortest filename to avoid edge-case variants
                var best = matches.OrderBy(a => (a.Update.FileName ?? string.Empty).Length).First();
                return new[] { best };
            }
        }

        return new[] { candidates.First() };
    }

    public static async Task<FileEntry?> fetch(
        string productId,
        InstallerType installerType,
        CancellationToken cancellationToken = default,
        DeviceFamily deviceFamily = DeviceFamily.Desktop,
        Market market = Market.US,
        Lang language = Lang.en,
        string flightRing = "Retail",
        string flightingBranchName = "Retail",
        string currentBranch = "ge_release"
    )
    {
        var OSVersion = SystemInfo.GetExactWindowsVersion();
        var archRid = SystemInfo.GetOsArchRid();
        Debug.WriteLine($"{installerType}, {OSVersion}, {archRid}, {productId}");
        switch (installerType)
        {
            case InstallerType.Packaged:
            {
                var packageResult = await DCATPackage.GetPackagesAsync(
                    productId,
                    market,
                    language,
                    true
                );
                if (!packageResult.IsSuccess)
                    return null;

                if (
                    !packageResult.Value.Any(p =>
                        p.PlatformDependencies.Any(pd => pd.MinVersion <= OSVersion)
                    )
                )
                    return null;

                var cookieResult = await FE3Handler.GetCookieAsync(cancellationToken);
                if (!cookieResult.IsSuccess)
                    return null;

                var osArch = archRid switch
                {
                    "arm64" => FE3OSArch.ARM64,
                    "x86" => FE3OSArch.X86,
                    "arm" => FE3OSArch.ARM,
                    _ => FE3OSArch.AMD64,
                };

                var fe3sync = await FE3Handler.SyncUpdatesAsync(
                    cookieResult.Value,
                    packageResult.Value.First().WuCategoryId,
                    language,
                    market,
                    currentBranch,
                    flightRing,
                    flightingBranchName,
                    OSVersion,
                    deviceFamily,
                    cancellationToken,
                    osArch
                );

                if (!fe3sync.IsSuccess)
                    return null;

                var updates = fe3sync.Value.Updates.ToList();
                var priorities = Utils.GetArchPriorities(archRid, isPackaged: true);
                var candidates = updates
                    .Where(t =>
                        !t.IsFramework
                        && t.TargetPlatforms.Any(p =>
                            (p.Family == deviceFamily || p.Family == DeviceFamily.Universal)
                            && p.MinVersion <= OSVersion
                        )
                    )
                    .OrderByDescending(t => t.Version)
                    .ToList();

                foreach (var archPref in priorities)
                {
                    var archCandidates = candidates.Where(c =>
                        Utils.ParseArchString(c.FileName ?? c.PackageIdentityName, isPackaged: true)
                        == archPref
                    );

                    foreach (var main in archCandidates)
                    {
                        // --- 1. SELECTION ---
                        var dcatMain = packageResult.Value.FirstOrDefault(p =>
                            p.PackageIdentityName.Equals(
                                main.PackageIdentityName,
                                StringComparison.OrdinalIgnoreCase
                            )
                            && (
                                p.AppVersion is null
                                || p.AppVersion.ToString() == main.Version.ToString()
                            )
                        );

                        var requiredDepUpdates = new List<FE3Handler.SyncUpdatesResponse.Update>();
                        var allDepsOk = true;

                        if (dcatMain is not null && dcatMain.FrameworkDependencies.Any())
                        {
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
                                            tp.MinVersion <= OSVersion
                                            && (
                                                tp.Family == DeviceFamily.Universal
                                                || tp.Family == deviceFamily
                                            )
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

                                var reduced = ReduceFrameworkDependencyFiles(
                                    latestGroup
                                        .Select(u => (Update: u, Url: string.Empty))
                                        .ToList(),
                                    archRid
                                );

                                requiredDepUpdates.AddRange(reduced.Select(r => r.Update));
                            }
                        }

                        if (!allDepsOk)
                            continue;

                        // --- 2. FETCH URLs ---
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
                            OSVersion,
                            deviceFamily,
                            cancellationToken,
                            osArch
                        );

                        if (!mainDownloadInfo.IsSuccess)
                            continue;

                        var depEntries = new List<FileEntry>();
                        var networkDepsOk = true;

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
                                OSVersion,
                                deviceFamily,
                                cancellationToken,
                                osArch
                            );

                            if (!depDownloadInfo.IsSuccess)
                            {
                                networkDepsOk = false;
                                break;
                            }

                            depUpdate.SetDownloadInfoPackageDigest(
                                depDownloadInfo.Value.Package.Digest
                            );
                            depUpdate.SetDownloadInfoBlockmapUrl(
                                depDownloadInfo.Value.BlockmapCab?.Url
                            );
                            depUpdate.SetDownloadInfoBlockmapDigest(
                                depDownloadInfo.Value.BlockmapCab?.Digest
                            );

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

                        if (!networkDepsOk)
                            continue; // Dependency URL failed, fallback to next main candidate

                        // --- 3. ASSEMBLY ---
                        main.SetDownloadInfoPackageDigest(mainDownloadInfo.Value.Package.Digest);
                        main.SetDownloadInfoBlockmapUrl(mainDownloadInfo.Value.BlockmapCab?.Url);
                        main.SetDownloadInfoBlockmapDigest(
                            mainDownloadInfo.Value.BlockmapCab?.Digest
                        );

                        return new FileEntry(
                            FileName: main.FileName,
                            Url: mainDownloadInfo.Value.Package.Url,
                            Dependencies: depEntries,
                            Digest: main.GetDownloadInfoPackageDigest(),
                            BlockmapUrl: main.GetDownloadInfoBlockmapUrl(),
                            BlockmapCabFileDigest: main.GetDownloadInfoBlockmapDigest()
                        );
                    }
                }
                return null;
            }
            case InstallerType.Unpackaged:
            {
                var unpackagedResult = await StoreEdgeFDProduct.GetUnpackagedInstall(
                    productId,
                    market,
                    language,
                    cancellationToken
                );

                if (
                    !unpackagedResult.IsSuccess
                    || unpackagedResult.Value == null
                    || !unpackagedResult.Value.Any()
                )
                    return null;

                var items = unpackagedResult.Value;
                var priorities = Utils.GetArchPriorities(archRid, isPackaged: false);

                foreach (var prefArch in priorities)
                {
                    var matchingCandidates = items
                        .Where(i =>
                            Utils.ParseArchString(i.architecture, isPackaged: false) == prefArch
                        )
                        .ToList();

                    if (matchingCandidates.Any())
                    {
                        var bestCandidate = matchingCandidates
                            .OrderByDescending(c =>
                                System.Version.TryParse(c.Version, out var v)
                                    ? v
                                    : new System.Version(0, 0)
                            )
                            .First();

                        return new FileEntry(
                            FileName: bestCandidate.FileName,
                            Url: bestCandidate.InstallerUrl,
                            Dependencies: Array.Empty<FileEntry>(),
                            Sha256: bestCandidate.InstallerSha256
                        );
                    }
                }

                return null;
            }

            default:
                return null;
        }
    }
}
