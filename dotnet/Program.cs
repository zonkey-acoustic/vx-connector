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

        var startupMode = ParseStartupMode(args);
        var detected = SimConfig.DetectDirectTarget();

        Application.Run(new TrayApplicationContext(startupMode, detected));
    }

    /// <summary>
    /// CLI:
    ///   (none)                          → Folder Watcher → Infinite Tees (default)
    ///   --direct                        → Direct
    ///   --watch-drills                  → Folder Watcher → Drills
    ///   --watch-itees / --watch-infinite-tees → Folder Watcher → Infinite Tees
    ///   --folder-watcher / -w           → alias for --watch-itees (back-compat)
    /// </summary>
    private static EngineMode ParseStartupMode(string[] args)
    {
        foreach (var arg in args)
        {
            if (arg.Equals("--direct", StringComparison.OrdinalIgnoreCase))
                return EngineMode.Direct;
            if (arg.Equals("--watch-drills", StringComparison.OrdinalIgnoreCase))
                return EngineMode.FolderWatcherDrills;
            if (arg.Equals("--watch-itees", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--watch-infinite-tees", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--folder-watcher", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-w", StringComparison.OrdinalIgnoreCase))
                return EngineMode.FolderWatcherInfiniteTees;
        }
        return EngineMode.FolderWatcherInfiniteTees;
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
