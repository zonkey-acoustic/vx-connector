using System.Diagnostics;

namespace VxProxy;

[Flags]
public enum SimTarget
{
    None = 0,
    InfiniteTees = 1 << 0,
    Drills = 1 << 1,
}

static class SimTargetExtensions
{
    /// <summary>
    /// User-facing name. The Drills target is shown as "OpenAPI Compatible" because it
    /// isn't Drills-specific: it speaks plain OpenConnect on :921 (or whatever
    /// DrillsConnect's settings.json says), which any OpenAPI-style sim — Drills,
    /// ShotForge — can accept.
    /// </summary>
    public static string DisplayName(this SimTarget sim) => sim switch
    {
        SimTarget.Drills => "OpenAPI Compatible",
        SimTarget.InfiniteTees => "Infinite Tees",
        _ => sim.ToString(),
    };
}

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Launched as our own GSPconnect.exe stub: park behind its title window and do
        // nothing else. Must be checked before the tray UI or killing sibling processes.
        if (args.Any(a => a.Equals(GspConnectStub.StubArgument, StringComparison.OrdinalIgnoreCase)))
        {
            GspConnectStub.RunAsStub();
            return;
        }

        KillOlderInstances();

        // Only safe once the previous instance is gone: a self-update leaves the old exe
        // renamed aside, and it stays locked while that process lives.
        UpdateApplier.CleanUpLeftovers();

        ApplicationConfiguration.Initialize();

        // Startup mode resolution: explicit CLI flag wins; otherwise fall back
        // to whatever the user last picked via the tray menu (persisted); finally,
        // with nothing saved, Proxy → Infinite Tees if it's on this machine and
        // Proxy → OpenAPI Compatible (Drills, ShotForge, …) if it isn't.
        var startupMode = ParseStartupModeFromArgs(args)
            ?? UserSettings.LoadLastMode()
            ?? (SimConfig.IsInfiniteTeesInstalled()
                ? EngineMode.ProxyInfiniteTees
                : EngineMode.ProxyDrills);

        var detected = SimConfig.DetectDirectTarget();

        Application.Run(new TrayApplicationContext(startupMode, detected));
    }

    /// <summary>
    /// CLI:
    ///   (none)                          → use persisted preference, else default
    ///   --proxy-drills                  → Proxy → Drills
    ///   --proxy-itees / --proxy-infinite-tees → Proxy → Infinite Tees
    ///   --watch-drills                  → Folder Watcher → Drills (legacy PTL only)
    ///   --watch-itees / --watch-infinite-tees → Folder Watcher → Infinite Tees (legacy)
    ///   --folder-watcher / -w           → alias for --watch-itees (back-compat)
    ///
    /// CLI flags are one-shot overrides: they pick the mode for this launch but
    /// do not update the persisted preference. Use the tray menu to change the
    /// sticky default.
    ///
    /// The --watch-* flags are the only way into Folder Watcher mode since v1.5.5
    /// dropped it from the tray menu. They're kept, undocumented, for anyone still on a
    /// ProTee Labs build older than 1.12.9.0.
    ///
    /// --direct exists but is intentionally undocumented; Direct mode is hidden
    /// from users since v1.3.x. Kept as an escape hatch for the rare case where
    /// someone wants VX Connector to just exist as a tray process without
    /// forwarding any shots themselves.
    /// </summary>
    /// <returns>The mode the user explicitly requested via CLI, or null if no flag was passed.</returns>
    private static EngineMode? ParseStartupModeFromArgs(string[] args)
    {
        foreach (var arg in args)
        {
            if (arg.Equals("--direct", StringComparison.OrdinalIgnoreCase))
                return EngineMode.Direct;
            if (arg.Equals("--proxy-drills", StringComparison.OrdinalIgnoreCase))
                return EngineMode.ProxyDrills;
            if (arg.Equals("--proxy-itees", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--proxy-infinite-tees", StringComparison.OrdinalIgnoreCase))
                return EngineMode.ProxyInfiniteTees;
            if (arg.Equals("--watch-drills", StringComparison.OrdinalIgnoreCase))
                return EngineMode.FolderWatcherDrills;
            if (arg.Equals("--watch-itees", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--watch-infinite-tees", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--folder-watcher", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-w", StringComparison.OrdinalIgnoreCase))
                return EngineMode.FolderWatcherInfiniteTees;
        }
        return null;
    }

    /// <summary>
    /// The flag that reproduces <paramref name="mode"/> on a fresh launch — the inverse of
    /// <see cref="ParseStartupModeFromArgs"/>. Used when relaunching ourselves after a
    /// self-update, so a session started from a flag doesn't quietly revert to the saved
    /// preference. Returns null if the mode has no flag.
    /// </summary>
    internal static string? CliFlagFor(EngineMode mode) => mode switch
    {
        EngineMode.Direct => "--direct",
        EngineMode.ProxyDrills => "--proxy-drills",
        EngineMode.ProxyInfiniteTees => "--proxy-itees",
        EngineMode.FolderWatcherDrills => "--watch-drills",
        EngineMode.FolderWatcherInfiniteTees => "--watch-itees",
        _ => null,
    };

    /// <summary>
    /// Kill any other VX Connector instances (running our exact same binary path),
    /// including stub children they left behind.
    ///
    /// Note this deliberately does NOT reap stray "GSPconnect" processes any more.
    /// Up to v1.3.1 it did, treating them as legacy leftovers from when our exe
    /// mimicked that name. Since PTL beta 1.12.9.0 that name is load-bearing again:
    /// a running GSPconnect.exe is what opens the :12321 lane (see
    /// <see cref="GspConnectStub"/>), so killing one would close the only route
    /// shot data has. Stubs are owned by the instance that started them and are
    /// cleaned up on exit.
    /// </summary>
    private static void KillOlderInstances()
    {
        var currentPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(currentPath)) return;

        var currentPid = Environment.ProcessId;

        foreach (var p in Process.GetProcessesByName("vx-connector"))
        {
            if (p.Id == currentPid) continue;
            try
            {
                if (string.Equals(p.MainModule?.FileName, currentPath, StringComparison.OrdinalIgnoreCase))
                {
                    // entireProcessTree takes the old instance's GSPconnect stub with it.
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(2000);
                }
            }
            catch
            {
                // Access Denied on a process we don't own — leave it be.
            }
            finally
            {
                p.Dispose();
            }
        }
    }
}
