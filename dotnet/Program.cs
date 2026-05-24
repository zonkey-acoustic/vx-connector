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
    ///   --watch-drills                  → Folder Watcher → Drills
    ///   --watch-itees / --watch-infinite-tees → Folder Watcher → Infinite Tees
    ///   --folder-watcher / -w           → alias for --watch-itees (back-compat)
    ///
    /// --direct exists but is intentionally undocumented; Direct mode is hidden
    /// from users since v1.3.x. Kept as an escape hatch for the rare case where
    /// someone wants VX Connector to just exist as a tray process without
    /// forwarding any shots themselves.
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
    /// Kill any other VX Connector instances (running our exact same binary path).
    /// Also reaps stale "GSPconnect" processes left over from v1.2.x/v1.3.0 binaries
    /// in any location — those mimicked the licensed GSPconnect.exe name, and
    /// upgraders may still have one running when they first launch the renamed exe.
    /// </summary>
    private static void KillOlderInstances()
    {
        var currentPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(currentPath)) return;

        var currentPid = Environment.ProcessId;

        // "vx-connector" = current name; "GSPconnect" = legacy name (v1.3.0 and earlier).
        // For the legacy name we only kill processes outside the canonical GSPro install
        // path so we don't disturb the real licensed binary.
        foreach (var name in new[] { "vx-connector", "GSPconnect" })
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                if (p.Id == currentPid) continue;
                try
                {
                    var modulePath = p.MainModule?.FileName;
                    bool isOurself = string.Equals(modulePath, currentPath, StringComparison.OrdinalIgnoreCase);
                    bool isLegacyOurs = name == "GSPconnect" && modulePath is not null
                        && !modulePath.StartsWith(@"C:\GSPro\", StringComparison.OrdinalIgnoreCase);

                    if (isOurself || isLegacyOurs)
                    {
                        p.Kill(entireProcessTree: true);
                        p.WaitForExit(2000);
                    }
                }
                catch
                {
                    // Most likely Access Denied — that's expected for the real licensed
                    // GSPconnect.exe (different binary, possibly different user/elevation). Skip it.
                }
                finally
                {
                    p.Dispose();
                }
            }
        }
    }
}
