using System.Diagnostics;
using System.IO.Compression;
using System.Xml.Linq;

namespace test.Helpers;

public static class InstallHelper
{
    public static async Task<bool> TryCloseBlockingProcessesElevatedAsync(string packagePath)
    {
        try
        {
            var ext = Path.GetExtension(packagePath).ToLowerInvariant();
            IEnumerable<string> exeNames;
            if (ext is ".appx" or ".msix")
            {
                exeNames = await ExtractExecutablesFromAppxAsync(packagePath);
            }
            else if (ext is ".appxbundle" or ".msixbundle")
            {
                exeNames = await ExtractExecutablesFromBundleAsync(packagePath);
            }
            else
            {
                return false;
            }

            var names = exeNames
                .Select(n => Path.GetFileNameWithoutExtension(n))
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (names.Length == 0)
                return false;

            var exePath = Path.Combine(AppContext.BaseDirectory, "ElevatedKiller.exe");
            if (!File.Exists(exePath))
                return false;

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                Verb = "runas",
                Arguments = string.Join(' ', names.Select(n => '"' + n + '"')),
                WindowStyle = ProcessWindowStyle.Normal,
            };

            using var proc = Process.Start(psi);
            if (proc == null)
                return false;

            proc.WaitForExit();
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<IEnumerable<string>> ExtractExecutablesFromAppxAsync(
        string packagePath
    )
    {
        var list = new List<string>();
        await Task.Run(() =>
        {
            using var zip = ZipFile.OpenRead(packagePath);
            list.AddRange(ParseExecutablesFromAppxZip(zip));
        });
        return list;
    }

    public static async Task<IEnumerable<string>> ExtractExecutablesFromBundleAsync(
        string packagePath
    )
    {
        var list = new List<string>();
        await Task.Run(() =>
        {
            using var bundleZip = ZipFile.OpenRead(packagePath);
            var bundleManifestEntry =
                bundleZip.GetEntry("AppxBundleManifest.xml")
                ?? bundleZip.Entries.FirstOrDefault(e =>
                    e.FullName.EndsWith(
                        "AppxBundleManifest.xml",
                        StringComparison.OrdinalIgnoreCase
                    )
                );
            if (bundleManifestEntry == null)
                return;

            using var bStream = bundleManifestEntry.Open();
            var bdoc = XDocument.Load(bStream);
            var bns =
                bdoc.Root?.GetDefaultNamespace()
                ?? "http://schemas.microsoft.com/appx/manifest/bundle/windows10";
            var packages = bdoc.Descendants(bns + "Package")
                .Where(p =>
                    string.Equals(
                        p.Attribute("Type")?.Value,
                        "application",
                        StringComparison.OrdinalIgnoreCase
                    )
                );

            foreach (var pkg in packages)
            {
                var fileName = pkg.Attribute("FileName")?.Value;
                if (string.IsNullOrWhiteSpace(fileName))
                    continue;

                var appxEntry =
                    bundleZip.GetEntry(fileName)
                    ?? bundleZip.Entries.FirstOrDefault(e =>
                        e.FullName.EndsWith(fileName, StringComparison.OrdinalIgnoreCase)
                    );
                if (appxEntry == null)
                    continue;

                using var appxStream = appxEntry.Open();
                using var ms = new MemoryStream();
                appxStream.CopyTo(ms);
                ms.Position = 0;

                using var appxZip = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: false);
                list.AddRange(ParseExecutablesFromAppxZip(appxZip));
            }
        });
        return list;
    }

    private static IEnumerable<string> ParseExecutablesFromAppxZip(ZipArchive appxZip)
    {
        var results = new List<string>();
        var manifestEntry =
            appxZip.GetEntry("AppxManifest.xml")
            ?? appxZip.Entries.FirstOrDefault(e =>
                e.FullName.EndsWith("AppxManifest.xml", StringComparison.OrdinalIgnoreCase)
            );
        if (manifestEntry == null)
            return results;

        using var stream = manifestEntry.Open();
        var xdoc = XDocument.Load(stream);
        var ns =
            xdoc.Root?.GetDefaultNamespace()
            ?? "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        var applications = xdoc.Descendants(ns + "Application");
        foreach (var app in applications)
        {
            var exeAttr =
                app.Attribute("Executable")?.Value ?? app.Element(ns + "Executable")?.Value;
            if (!string.IsNullOrWhiteSpace(exeAttr))
            {
                results.Add(exeAttr);
            }
        }
        return results;
    }
}
