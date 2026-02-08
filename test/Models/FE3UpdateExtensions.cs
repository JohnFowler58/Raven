using System.Runtime.CompilerServices;
using StoreListings.Library;

namespace test.Models;

public static class FE3UpdateExtensions
{
    public static string? GetDownloadInfoPackageDigest(this FE3Handler.SyncUpdatesResponse.Update u) =>
        DownloadInfoStore.GetOrCreate(u).PackageDigest;

    public static void SetDownloadInfoPackageDigest(
        this FE3Handler.SyncUpdatesResponse.Update u,
        string? digest
    ) => DownloadInfoStore.GetOrCreate(u).PackageDigest = digest;

    public static string? GetDownloadInfoBlockmapUrl(this FE3Handler.SyncUpdatesResponse.Update u) =>
        DownloadInfoStore.GetOrCreate(u).BlockmapUrl;

    public static void SetDownloadInfoBlockmapUrl(this FE3Handler.SyncUpdatesResponse.Update u, string? url) =>
        DownloadInfoStore.GetOrCreate(u).BlockmapUrl = url;

    public static string? GetDownloadInfoBlockmapDigest(this FE3Handler.SyncUpdatesResponse.Update u) =>
        DownloadInfoStore.GetOrCreate(u).BlockmapDigest;

    public static void SetDownloadInfoBlockmapDigest(
        this FE3Handler.SyncUpdatesResponse.Update u,
        string? digest
    ) => DownloadInfoStore.GetOrCreate(u).BlockmapDigest = digest;

    private sealed class DownloadInfo
    {
        public string? PackageDigest { get; set; }
        public string? BlockmapUrl { get; set; }
        public string? BlockmapDigest { get; set; }
    }

    private static class DownloadInfoStore
    {
        private static readonly ConditionalWeakTable<FE3Handler.SyncUpdatesResponse.Update, DownloadInfo> _table =
            new();

        public static DownloadInfo GetOrCreate(FE3Handler.SyncUpdatesResponse.Update u) =>
            _table.GetValue(u, _ => new DownloadInfo());
    }
}
