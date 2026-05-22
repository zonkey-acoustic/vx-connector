using System.Diagnostics;

namespace VxProxy;

[Flags]
public enum SimTarget
{
    None = 0,
    InfiniteTees = 1 << 0,
    Drills = 1 << 1,
}

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        KillOlderInstances();

        ApplicationConfiguration.Initialize();

        bool folderWatcherMode = args.Any(a =>
            a.Equals("--folder-watcher", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("-w", StringComparison.OrdinalIgnoreCase));

        var detected = SimConfig.DetectDirectTarget();

        Application.Run(new TrayApplicationContext(folderWatcherMode, detected));
    }

    /// <summary>
    /// Kill any other VxProxy instances (running our exact same binary path).
    /// Real GSPconnect.exe at the canonical GSPro install path is left alone —
    /// we only target processes whose module path matches our own.
    /// </summary>
    private static void KillOlderInstances()
    {
        var currentPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(currentPath)) return;

        var currentPid = Environment.ProcessId;

        foreach (var p in Process.GetProcessesByName("GSPconnect"))
        {
            if (p.Id == currentPid) continue;
            try
            {
                if (string.Equals(p.MainModule?.FileName, currentPath, StringComparison.OrdinalIgnoreCase))
                {
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(2000);
                }
            }
            catch
            {
                // Most likely Access Denied — that's expected for the real GSPconnect.exe
                // (different binary, possibly different user/elevation). Skip it.
            }
            finally
            {
                p.Dispose();
            }
        }
    }
}
