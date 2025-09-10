namespace test.Models;

public sealed record FileEntry(string FileName, string Url, IReadOnlyList<FileEntry> Dependencies);
