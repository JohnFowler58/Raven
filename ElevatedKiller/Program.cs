using System.Diagnostics;
using System.IO;

namespace ElevatedKiller;

internal static class Program
{
    // Usage: ElevatedKiller.exe <procName1> <procName2> ...
    private static int Main(string[] args)
    {
        if (args.Length == 0)
            return 1;

        foreach (var raw in args)
        {
            var name = Path.GetFileNameWithoutExtension(raw);
            if (string.IsNullOrWhiteSpace(name))
                continue;

            try
            {
                foreach (var proc in Process.GetProcessesByName(name))
                {
                    try
                    {
                        if (proc.CloseMainWindow())
                        {
                            proc.WaitForExit(3000);
                            if (!proc.HasExited)
                            {
                                proc.Kill(true);
                                proc.WaitForExit(2000);
                            }
                        }
                        else
                        {
                            proc.Kill(true);
                            proc.WaitForExit(2000);
                        }
                    }
                    catch { /* ignore */ }
                }
            }
            catch { /* ignore */ }
        }

        return 0;
    }
}
