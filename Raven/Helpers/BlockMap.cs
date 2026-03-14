using System.Xml.Linq;

namespace Raven.Helpers;

public sealed record BlockMap(int BlockSize, IReadOnlyList<string> BlockHashes);

public static class BlockMapReader
{
    private static readonly XNamespace Ns =
        "http://schemas.microsoft.com/msus/2002/12/UpdateHandlers/AppxWUBlockTable";

    public static BlockMap Parse(string xml)
    {
        var doc = XDocument.Parse(xml);
        var table =
            doc.Root?.Element(Ns + "AppxBlockTable")
            ?? throw new FormatException("Invalid blockmap.xml: missing AppxBlockTable.");

        var blockSizeAttr = table.Attribute("BlockSize")?.Value;
        if (!int.TryParse(blockSizeAttr, out var blockSize) || blockSize <= 0)
            throw new FormatException("Invalid blockmap.xml: invalid BlockSize.");

        var hashes = table
            .Elements(Ns + "Block")
            .Select(e => e.Attribute("Hash")?.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Cast<string>()
            .ToList();

        if (hashes.Count == 0)
            throw new FormatException("Invalid blockmap.xml: no blocks.");

        return new BlockMap(blockSize, hashes);
    }
}
