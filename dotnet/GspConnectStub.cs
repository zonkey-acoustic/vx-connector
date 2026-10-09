using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace VxProxy;

/// <summary>
/// Keeps a process named <c>GSPconnect.exe</c> alive so ProTee Labs will open its
/// :12321 lane.
///
/// PTL gates the lane on a process-presence check (<c>IsGameRunning</c> →
/// <c>GetProcessesByName</c>). With no such process it reports "Connection to GSPro
/// failed, attempting to launch game" and never opens a socket. Any binary with the
/// right filename satisfies it — there is no signature or path validation, and the
/// real GSPro does not need to be installed.
///
/// ProTee Labs 1.12.13.0 tightened the gate: the process must also own a visible
/// window titled exactly "ProTee United VX" — see <see cref="ExpectedWindowTitle"/>.
///
/// Rather than rename our own exe (reversing the v1.3.1 branding decision) or copy a
/// ~70 MB self-contained binary, we hard-link our exe under the required name and run
/// it with <c>--stub</c>, which parks the process doing nothing. A hard link shares
/// the original's data, so it costs no extra disk. If linking fails — different
/// volume, or a filesystem without hard-link support — we fall back to a copy.
/// </summary>
public static class GspConnectStub
{
    public const string StubArgument = "--stub";
    private const string StubExeName = "GSPconnect.exe";

    [DllImport("Kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    private static Process? _stub;

    /// <summary>
    /// Fallback staging directory, used only when our install directory isn't
    /// writable. Note this location only works for the published single-file build:
    /// a framework-dependent layout needs its .dll/.deps.json/.runtimeconfig.json
    /// siblings, which don't exist here.
    /// </summary>
    private static string FallbackStubDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VxConnector", "stub");

    /// <summary>
    /// The main-window title the real GSPconnect shows once "ProTee VX" is selected
    /// (<c>OpenSDKv1</c>, <c>LMtype == 2</c>). ProTee Labs 1.12.13.0 added
    /// <c>IsVXConnectorValid</c>: it polls the GSPconnect process's
    /// <c>MainWindowTitle</c> for up to 60s and requires an exact match, otherwise it
    /// shows "Incompatible GSPro connector detected" and never opens the socket.
    /// </summary>
    public const string ExpectedWindowTitle = "ProTee United VX";

    /// <summary>
    /// Park this process, holding a window that carries <see cref="ExpectedWindowTitle"/>.
    /// Entered when we were launched with --stub: we exist purely so ProTee Labs's
    /// process and window-title checks pass.
    /// </summary>
    public static void RunAsStub()
    {
        ApplicationConfiguration.Initialize();
        using var window = new TitleWindow();

        // The parent kills us on exit; this also self-terminates if the parent died
        // without cleaning up (crash, kill -9), so we never leak a permanent process.
        var parentId = ResolveParentFromArgs();
        using var watchdog = new System.Windows.Forms.Timer { Interval = 2000 };
        watchdog.Tick += (_, _) =>
        {
            if (parentId is null) return;
            try
            {
                using var parent = Process.GetProcessById(parentId.Value);
                if (!parent.HasExited) return;
            }
            catch (ArgumentException)
            {
                // parent is gone
            }
            Application.ExitThread();
        };
        watchdog.Start();

        Application.Run(window);
    }

    /// <summary>
    /// A window that exists only to be found by <c>Process.MainWindowTitle</c>.
    ///
    /// .NET picks the main window as the first top-level window that is unowned and
    /// <c>IsWindowVisible</c> — so a hidden window doesn't count, and neither would a
    /// <c>CreateNoWindow</c> console. It has to be "shown". Everything else is spent
    /// making that invisible to the user: fully transparent, click-through, 1×1 and
    /// off-screen, never activated, and a tool window so it stays out of the taskbar
    /// and Alt-Tab.
    /// </summary>
    private sealed class TitleWindow : Form
    {
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        public TitleWindow()
        {
            Text = ExpectedWindowTitle;
            FormBorderStyle = FormBorderStyle.None;
            // Deliberately not ShowInTaskbar = false: WinForms implements that by giving
            // the form a hidden owner window, and an owned window is never a main window.
            // WS_EX_TOOLWINDOW keeps it off the taskbar instead.
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-32000, -32000);
            Size = new Size(1, 1);
            Opacity = 0;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT;
                return cp;
            }
        }
    }

    private static int? ResolveParentFromArgs()
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--parent", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(args[i + 1], out var id))
            {
                return id;
            }
        }
        return null;
    }

    /// <summary>
    /// Ensure a GSPconnect.exe process exists. No-op if one is already running —
    /// including the real licensed binary, which we must never disturb.
    /// </summary>
    /// <returns>True if the gate is satisfied (by us or by someone else).</returns>
    public static bool Ensure(Action<string>? log = null)
    {
        if (_stub is { HasExited: false }) return true;

        var existing = Process.GetProcessesByName("GSPconnect");
        try
        {
            if (existing.Length > 0)
            {
                log?.Invoke("GSPconnect.exe already running — leaving it alone.");
                return true;
            }
        }
        finally
        {
            foreach (var p in existing) p.Dispose();
        }

        var currentPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(currentPath))
        {
            log?.Invoke("Cannot locate our own exe — GSPconnect stub not started.");
            return false;
        }

        string stubPath;
        try
        {
            stubPath = PrepareStubBinary(currentPath, log);
        }
        catch (Exception ex)
        {
            log?.Invoke($"Could not stage GSPconnect stub: {ex.Message}");
            return false;
        }

        try
        {
            _stub = Process.Start(new ProcessStartInfo
            {
                FileName = stubPath,
                Arguments = $"{StubArgument} --parent {Environment.ProcessId}",
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (_stub is null)
            {
                log?.Invoke("Could not start GSPconnect stub — ProTee Labs will refuse to connect.");
                return false;
            }

            // Process.Start only reports that the process launched. A misconfigured
            // stub (missing runtime siblings, blocked by policy) exits immediately, so
            // confirm it actually stayed up rather than reporting a false success.
            if (_stub.WaitForExit(500))
            {
                log?.Invoke($"GSPconnect stub exited immediately (code {_stub.ExitCode}) from {stubPath} — " +
                            "ProTee Labs will refuse to connect.");
                _stub.Dispose();
                _stub = null;
                return false;
            }

            log?.Invoke($"Started GSPconnect stub (pid {_stub.Id}) — ProTee Labs's :12321 lane is now open.");
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"Could not start GSPconnect stub: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Stage the stub binary, preferring a hard link (no extra disk) over a copy.
    ///
    /// Staged next to our own exe by default. That matters for the framework-dependent
    /// (Debug) layout: the apphost embeds the path of the managed dll it launches, so a
    /// renamed copy works — but only if vx-connector.dll and its .runtimeconfig.json are
    /// siblings. Somewhere isolated like %LOCALAPPDATA% would start and die instantly.
    /// The published single-file build is self-contained and works from either location.
    /// </summary>
    private static string PrepareStubBinary(string currentPath, Action<string>? log)
    {
        var preferred = Path.GetDirectoryName(currentPath);
        var candidates = string.IsNullOrEmpty(preferred)
            ? new[] { FallbackStubDirectory }
            : new[] { preferred, FallbackStubDirectory };

        Exception? lastFailure = null;
        foreach (var dir in candidates)
        {
            try
            {
                Directory.CreateDirectory(dir);
                var stubPath = Path.Combine(dir, StubExeName);

                if (File.Exists(stubPath))
                {
                    var current = new FileInfo(currentPath);
                    var staged = new FileInfo(stubPath);
                    // Same length and timestamp means it's current (a hard link to the
                    // same file trivially matches); otherwise re-stage after an upgrade.
                    if (staged.Length == current.Length && staged.LastWriteTimeUtc == current.LastWriteTimeUtc)
                    {
                        return stubPath;
                    }

                    try { File.Delete(stubPath); }
                    catch (IOException) { return stubPath; }  // in use by an untracked stub; good enough
                }

                if (CreateHardLinkW(stubPath, currentPath, IntPtr.Zero)) return stubPath;

                // Different volume, or a filesystem without hard links — copy instead.
                var err = Marshal.GetLastWin32Error();
                log?.Invoke($"Hard link unavailable in {dir} (error {err}); copying instead.");
                File.Copy(currentPath, stubPath, overwrite: true);
                return stubPath;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // Read-only install directory (e.g. Program Files) — try the next one.
                lastFailure = ex;
            }
        }

        throw lastFailure ?? new IOException("No writable location for the GSPconnect stub.");
    }

    /// <summary>Stop the stub we started. Never touches a GSPconnect we didn't launch.</summary>
    public static void Stop()
    {
        var stub = _stub;
        _stub = null;
        if (stub is null) return;

        try
        {
            if (!stub.HasExited)
            {
                stub.Kill(entireProcessTree: true);
                stub.WaitForExit(2000);
            }
        }
        catch
        {
            // Already gone, or we lost the right to signal it — nothing to do.
        }
        finally
        {
            stub.Dispose();
        }
    }
}
