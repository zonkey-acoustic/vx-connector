using System.Diagnostics;

namespace VxProxy;

/// <summary>
/// Notices when the real GSPro is running, so Proxy mode can get out of its way.
///
/// While we run, ProTee Labs's GSPro option can't tell us from GSPro. Our stub passes
/// its <c>GSPconnect</c> process check, so PTL never launches GSPro itself, and our
/// listener holds :12321 with <c>ExclusiveAddressUse</c>, so the real GSPconnect
/// can't bind it. Users who own GSPro and leave us running end up sending their shots
/// to Infinite Tees or Drills, which usually isn't even open.
///
/// "GSPro is running" means either of these:
/// <list type="bullet">
/// <item>a <c>GSPro</c> process. This is the earliest signal, because it appears
/// before GSPro starts its GSPconnect, which gives us time to release :12321
/// first.</item>
/// <item>a <c>GSPconnect</c> process that isn't our stub. This catches a real
/// GSPconnect running without the game, which would also want :12321.</item>
/// </list>
///
/// This is a poll, not WMI process-start events: <c>Win32_ProcessStartTrace</c> needs
/// admin, and two by-name lookups every couple of seconds cost next to nothing.
/// </summary>
public sealed class GsproWatcher : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly System.Threading.Timer _timer;
    private readonly object _gate = new();
    private bool? _lastState;
    private bool _disposed;

    /// <summary>
    /// Raised on a thread-pool thread whenever the answer changes, and never for the
    /// initial state. Callers read that with <see cref="IsGsproRunning"/> before
    /// <see cref="Start"/>.
    /// </summary>
    public event Action<bool>? GsproRunningChanged;

    public GsproWatcher()
    {
        _timer = new System.Threading.Timer(_ => Poll(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _lastState ??= IsGsproRunning();
            _timer.Change(PollInterval, PollInterval);
        }
    }

    private void Poll()
    {
        bool changed;
        bool now;
        lock (_gate)
        {
            if (_disposed) return;
            now = IsGsproRunning();
            changed = _lastState != now;
            _lastState = now;
        }
        if (changed) GsproRunningChanged?.Invoke(now);
    }

    public static bool IsGsproRunning()
    {
        if (AnyProcessNamed("GSPro", _ => true)) return true;
        return AnyProcessNamed("GSPconnect", p => !GspConnectStub.IsOwnStub(p) && !IsVxConnectorBinary(p));
    }

    /// <summary>
    /// True for any VX Connector stub, including one belonging to another install, such
    /// as the installed copy while a dev build runs. That stub is a hard link to
    /// vx-connector.exe, so its version resource says "VX Connector". Two of us fighting
    /// over :12321 is a real problem, but it isn't GSPro, and pausing would mislabel it.
    /// An unreadable image counts as foreign, as in <see cref="GspConnectStub.IsOwnStub"/>.
    /// </summary>
    private static bool IsVxConnectorBinary(Process process)
    {
        try
        {
            var path = process.MainModule?.FileName;
            if (string.IsNullOrEmpty(path)) return false;
            return FileVersionInfo.GetVersionInfo(path).ProductName == "VX Connector";
        }
        catch
        {
            return false;
        }
    }

    private static bool AnyProcessNamed(string name, Func<Process, bool> predicate)
    {
        var found = Process.GetProcessesByName(name);
        try
        {
            foreach (var p in found)
            {
                // HasExited throws access-denied for an elevated process. GSPro may well
                // be run as admin, and a process we can't open is still a running one.
                try { if (p.HasExited) continue; }
                catch { /* still running as far as we can tell */ }

                if (predicate(p)) return true;
            }
            return false;
        }
        finally
        {
            foreach (var p in found) p.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Dispose();
        }
    }
}
