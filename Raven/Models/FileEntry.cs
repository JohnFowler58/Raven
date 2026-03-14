namespace Raven.Models;

public sealed record FileEntry(
    string FileName,
    string Url,
    IReadOnlyList<FileEntry> Dependencies,
    string? Digest = null,
    string? Sha256 = null,
    string? BlockmapUrl = null,
    string? BlockmapCabFileDigest = null
);
