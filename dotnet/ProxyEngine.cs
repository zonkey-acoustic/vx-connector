using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VxProxy;

public enum ConnectionStatus { Stopped, Direct, FolderWatcher, Proxy }

public enum EngineMode
{
    Direct,
    FolderWatcherDrills,
    FolderWatcherInfiniteTees,
    ProxyDrills,
    ProxyInfiniteTees,
}

public record ShotInfo(
    int ShotNumber,
    double BallSpeed,
    double TotalSpin,
    double VLA,
    double ClubSpeed,
    bool HasBall,
    bool HasClub
);

/// <summary>
/// Runs in one of three modes:
///   Proxy         - the default since ProTee Labs beta 1.12.9.0. We hold open a
///                   GSPconnect.exe stub so PTL will dial us on :12321, accept its
///                   OpenConnect stream, and relay each message to the chosen sim
///                   over a persistent TCP connection (adding the newline the sims
///                   expect but PTL doesn't send). Sim acks are relayed back upstream.
///   FolderWatcher - legacy. Watches ProTee's shots folder and converts each
///                   ShotData.json to OpenConnect JSON. Only works on pre-beta
///                   ProTee Labs; the beta no longer writes that file.
///   Direct        - process exists as a tray-only idle process. Hidden from UI as of
///                   v1.3.x; reachable only via the undocumented --direct flag.
/// </summary>
public class ProxyEngine : IDisposable
{
    private const string ForwardHost = "127.0.0.1";

    private CancellationTokenSource? _cts;
    private ShotFolderWatcher? _folderWatcher;
    private OpenConnectListener? _listener;
    private int _watcherShotNumber;

    // Persistent TCP connection to the sim. Held across shots so the sim's UI sees us as
    // continuously connected, matching the OpenConnect spec's "constant 2-way
    // communication" intent. Held for the whole session in Folder Watcher mode; in Proxy
    // mode only while ProTee Labs is connected, since the sim's UI reads socket state as
    // "ready for a shot" and that would be a lie with PTL away.
    private TcpClient? _persistentClient;
    private NetworkStream? _persistentStream;
    private Task? _readerTask;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    // Background supervisor that re-establishes the persistent connection. Without it
    // the only reconnect trigger is an inbound message, so starting before the sim
    // left us disconnected until a shot arrived.
    private Task? _reconnectTask;
    private bool _connectFailureLogged;

    // Timestamps that exist purely so a dropped connection reports *why* it dropped.
    // Infinite Tees carries ClientIdleTimeoutSec=10; an idle-timeout close and a
    // level-transition reset look identical in the log without the elapsed time, and
    // they need different fixes. _lastSimActivityUtc covers both directions on the sim
    // socket (our writes and its acks) because that is what an idle timer would measure.
    private DateTime _lastSimActivityUtc;
    private DateTime? _simDownSinceUtc;

    // Set when a ball-data shot goes to the sim, cleared once the ready signal below has
    // been sent (or the sim sent its own). Written and read from different pool threads.
    private volatile bool _shotAwaitingReadySignal;

    // Supersedes a scheduled ready signal without cancellation-token churn: every
    // schedule takes a generation, and only the newest one is allowed to fire.
    private int _readySignalGeneration;

    // GSPro sends its 202 ~250ms after the last of its two 201s. Infinite Tees's 201s
    // arrive ~101ms apart, so this comfortably lands after the pair rather than between.
    private static readonly TimeSpan ReadyAfterPlayerInfo = TimeSpan.FromMilliseconds(400);

    // Only for a sim that announces nothing at all after a shot, and only until we learn
    // otherwise — see _simAnnouncesPlayerInfo. Infinite Tees's 201 latency is variable
    // (8.6s and ~19s on consecutive shots, observed 2026-08-16), so no fixed timeout can
    // both cover a silent sim and stay out of the way of a slow one.
    private static readonly TimeSpan ReadyFallback = TimeSpan.FromSeconds(15);

    // Spacing between consecutive objects relayed upstream in one batch, so PTL's next
    // read cannot return two. The real GSPconnect's 201 pair is 256ms apart.
    private static readonly TimeSpan UpstreamObjectGap = TimeSpan.FromMilliseconds(60);

    // Set the first time the sim sends a Code 201. From then on it is trusted to announce
    // its own ready moment and the blind fallback is retired: firing it would pre-empt the
    // real signal with one at the wrong time, which is what happened to shot #5 above.
    private volatile bool _simAnnouncesPlayerInfo;

    /// <summary>Guards against a malformed sim stream growing the frame buffer without bound.</summary>
    private const int MaxSimResponseBytes = 1024 * 1024;

    /// <summary>
    /// "The game is ready for the next shot" — byte-for-byte what the licensed
    /// GSPconnect sends ProTee Labs once a shot has resolved (`samples/protee-gspro.pcapng`,
    /// t=29.4 / 102.6 / 123.4, each ~10s after a shot sequence). No trailing newline:
    /// GSPconnect doesn't send one on this lane, so neither do we.
    ///
    /// Neither sim produces anything like it — Infinite Tees and Drills only ack with
    /// Code 200 — so without this PTL never hears the game come back to ready. That is
    /// the one substantive difference between what PTL gets from us and what it gets
    /// from the real GSPconnect.
    ///
    /// We deliberately do **not** synthesize the `Code 201` player-information message
    /// GSPro pairs with this. 201 carries Handed/Club/DistanceToTarget, we have no source
    /// for any of it, and PTL acts on club information — inventing it would be worse than
    /// staying quiet. 202 is content-free, so it can only ever be true.
    /// </summary>
    private static readonly byte[] ReadyForNextShot =
        Encoding.UTF8.GetBytes("{\"Code\":202,\"Message\":\"GSPro ready\",\"Player\":null}");

    public int ShotCount { get; private set; }
    public bool IsRunning { get; private set; }

    // No application-level keep-alive is needed. Infinite Tees's config carries
    // ClientIdleTimeoutSec=10 under [/Script/OPENGS.SafeSocketHubSubsystem], which
    // looked like it would drop us between shots — but measured against the real sim,
    // an idle connection survived 80s+ untouched and the UI stayed "connected".
    // A heartbeat was implemented, tested, and removed as unnecessary.

    /// <summary>Which mode Start() runs. Default Proxy → Infinite Tees.</summary>
    public EngineMode Mode { get; set; } = EngineMode.ProxyInfiniteTees;

    public bool FolderWatcherMode =>
        Mode is EngineMode.FolderWatcherDrills or EngineMode.FolderWatcherInfiniteTees;

    public bool ProxyMode =>
        Mode is EngineMode.ProxyDrills or EngineMode.ProxyInfiniteTees;

    /// <summary>True in any mode that forwards shots to a sim.</summary>
    public bool ForwardingMode => Mode != EngineMode.Direct;

    public event Action<string>? Log;
    public event Action<ConnectionStatus>? StatusChanged;
    public event Action<ShotInfo>? ShotReceived;
    public event Action<bool>? ConnectionStateChanged;

    private bool _isConnected;
    /// <summary>
    /// True when the persistent TCP connection to the sim is currently established.
    /// Always false in Direct mode and when stopped. In Proxy mode it also goes false
    /// whenever ProTee Labs is away — the link is closed on purpose then, so the sim
    /// stops showing ready (see <see cref="OnUpstreamConnectionChanged"/>).
    /// </summary>
    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (_isConnected == value) return;
            _isConnected = value;
            ConnectionStateChanged?.Invoke(value);
        }
    }

    private bool _isUpstreamConnected;
    /// <summary>
    /// True when ProTee Labs is connected to our :12321 listener. Proxy modes only —
    /// Folder Watcher has no upstream socket, and Direct mode has no listener at all.
    ///
    /// Paired with <see cref="IsConnected"/> this gives the full picture: the relay only
    /// carries shots when both halves are up, which is what the tray icon reflects.
    /// </summary>
    public bool IsUpstreamConnected
    {
        get => _isUpstreamConnected;
        private set
        {
            if (_isUpstreamConnected == value) return;
            _isUpstreamConnected = value;
            ConnectionStateChanged?.Invoke(value);
        }
    }

    public ConnectionStatus Status =>
        !IsRunning ? ConnectionStatus.Stopped :
        Mode == EngineMode.Direct ? ConnectionStatus.Direct :
        ProxyMode ? ConnectionStatus.Proxy :
        ConnectionStatus.FolderWatcher;

    public bool Start()
    {
        if (IsRunning) return true;

        _cts = new CancellationTokenSource();
        _connectFailureLogged = false;
        _lastSimActivityUtc = default;
        _simDownSinceUtc = null;

        // Banner first, so any log a user sends us identifies the build it came from.
        Emit($"VX Connector {AppVersion.Display}");

        if (ProxyMode)
        {
            var (proxySim, proxyPort) = ResolveForwardTarget();

            // We listen on 12321; forwarding there would just loop back to us.
            if (proxyPort == OpenConnectListener.DefaultPort)
            {
                Emit($"{proxySim} is configured for port {proxyPort}, which is the port " +
                     "ProTee Labs connects to us on. Change the sim to a different port.");
                _cts.Dispose();
                _cts = null;
                return false;
            }

            // Without a GSPconnect.exe process, ProTee Labs never opens the lane.
            GspConnectStub.Ensure(Emit);

            _listener = new OpenConnectListener();
            _listener.Log += Emit;
            _listener.Error += Emit;
            _listener.MessageReceived += json => _ = HandleUpstreamMessageAsync(json, _cts.Token);
            _listener.UpstreamConnectionChanged += OnUpstreamConnectionChanged;

            if (!_listener.Start())
            {
                _listener.Dispose();
                _listener = null;
                GspConnectStub.Stop();
                _cts.Dispose();
                _cts = null;
                return false;
            }

            IsRunning = true;
            Emit($"Proxy mode — forwarding shots to {proxySim} at {ForwardHost}:{proxyPort}.");
            OnStatusChanged();

            // Deliberately no eager connect here, unlike Folder Watcher mode below: in
            // Proxy mode the sim connection is tied to ProTee Labs's, so it opens when
            // PTL arrives (OnUpstreamConnectionChanged). The supervisor still starts now
            // — it idles until PTL is here, then keeps the sim link up from that point.
            _reconnectTask = Task.Run(() => ReconnectLoopAsync(_cts.Token), _cts.Token);
            return true;
        }

        if (FolderWatcherMode)
        {
            _folderWatcher = new ShotFolderWatcher(ShotFolderWatcher.DefaultShotsDirectory);
            _folderWatcher.Log += Emit;
            _folderWatcher.Error += Emit;
            _folderWatcher.ShotDetected += dir => _ = HandleShotFolderAsync(dir, _cts.Token);

            if (!_folderWatcher.Start())
            {
                _folderWatcher.Dispose();
                _folderWatcher = null;
                _cts.Dispose();
                _cts = null;
                return false;
            }

            IsRunning = true;
            var (sim, port) = ResolveForwardTarget();
            Emit($"Folder watcher mode — forwarding shots to {sim} at {ForwardHost}:{port}.");
            OnStatusChanged();

            // Best-effort initial connect so the sim sees us as connected from
            // the moment we enter Folder Watcher mode, not just on first shot.
            // The supervisor keeps retrying if the sim isn't up yet.
            _ = TryConnectAsync(_cts.Token);
            _reconnectTask = Task.Run(() => ReconnectLoopAsync(_cts.Token), _cts.Token);
            return true;
        }

        // Direct mode — nothing to bind, nothing to watch. Just exist.
        IsRunning = true;
        var directTarget = SimConfig.DetectDirectTarget();
        var label = TrayApplicationContext.FormatSims(directTarget) ?? "the target sim";
        Emit($"Direct mode — VX Connector is idle. ProTee Labs talks straight to {label} on :921.");
        OnStatusChanged();
        return true;
    }

    /// <summary>
    /// Resolve the forward target from the current Mode. The port is re-read
    /// from sim config each call so a user changing the sim's port mid-session
    /// is picked up on the next shot.
    /// </summary>
    public (string sim, int port) ResolveForwardTarget() => Mode switch
    {
        EngineMode.FolderWatcherDrills or EngineMode.ProxyDrills =>
            (SimTarget.Drills.DisplayName(), SimConfig.GetDrillsPort() ?? 921),
        EngineMode.FolderWatcherInfiniteTees or EngineMode.ProxyInfiniteTees =>
            (SimTarget.InfiniteTees.DisplayName(), SimConfig.GetInfiniteTeesPort() ?? 999),
        _ => throw new InvalidOperationException(
            "ResolveForwardTarget is only valid in a forwarding mode."),
    };

    public void Stop()
    {
        if (!IsRunning) return;
        Emit("Stopping...");
        _cts?.Cancel();
        _folderWatcher?.Dispose();
        _folderWatcher = null;
        _listener?.Dispose();
        _listener = null;
        GspConnectStub.Stop();
        // Disposing the connection forces any in-flight ReadAsync to unblock
        // (cancellation doesn't propagate cleanly through socket reads on Windows).
        DisposeConnection();
        IsUpstreamConnected = false;
        _cts?.Dispose();
        _cts = null;
        _reconnectTask = null;
        IsRunning = false;
        OnStatusChanged();
    }

    /// <summary>Switch modes while running. Stops the current mode and starts the new one.</summary>
    public bool RestartIn(EngineMode mode)
    {
        Mode = mode;
        if (IsRunning) Stop();
        return Start();
    }

    /// <summary>
    /// Relay one OpenConnect message from ProTee Labs to the sim.
    ///
    /// Forwarded verbatim: PTL's JSON is already the format the sims expect, so
    /// re-serializing would only risk field drift. The one change is appending the
    /// newline PTL omits on this lane but the sims require as a message delimiter.
    ///
    /// PTL splits a shot across messages inconsistently — sometimes one combined
    /// ball+club message, sometimes ball-only then club-only under the same
    /// ShotNumber. We pass each through as-is rather than trying to merge, which is
    /// what the sims already expect (see samples/itees-protee.pcapng).
    /// </summary>
    private async Task HandleUpstreamMessageAsync(string json, CancellationToken ct)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(json + "\n");
            var carriedBallData = LogShotFromUpstream(json);

            for (int attempt = 1; attempt <= 2; attempt++)
            {
                if (!await EnsureConnectedAsync(ct))
                {
                    if (attempt == 2) Emit("  Message dropped: no connection to sim.");
                    continue;
                }

                if (await TryWriteAsync(bytes, ct))
                {
                    // Arm the ready signal — only for ball data, so the cadence is one per
                    // shot like GSPro's. The sims ack PTL's status chatter too, and arming
                    // on that would fire it constantly. See ScheduleReadySignal.
                    if (carriedBallData)
                    {
                        _shotAwaitingReadySignal = true;

                        // Only blind-schedule for a sim we've never heard announce itself.
                        // Once one has sent a 201 we wait for the real thing however long
                        // it takes, rather than racing it with a guess.
                        if (!_simAnnouncesPlayerInfo) ScheduleReadySignal(ReadyFallback, ct);
                    }
                    return;
                }

                if (attempt == 1) Emit("  Reconnecting and retrying...");
            }
            Emit("  Message dropped: could not send after reconnect.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Emit($"Upstream message handler error: {ex.Message}");
        }
    }

    /// <summary>
    /// Read-only inspection of a relayed message purely to drive the UI. Only
    /// messages actually carrying ball data count as shots; the status and
    /// heartbeat messages PTL interleaves are relayed but not counted.
    /// </summary>
    /// <returns>True if the message carried ball data, i.e. it was the shot itself.</returns>
    private bool LogShotFromUpstream(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var options = root.TryGetProperty("ShotDataOptions", out var o) ? o : default;
            bool hasBall = options.ValueKind == JsonValueKind.Object
                && options.TryGetProperty("ContainsBallData", out var cb)
                && cb.ValueKind == JsonValueKind.True;
            bool hasClub = options.ValueKind == JsonValueKind.Object
                && options.TryGetProperty("ContainsClubData", out var cc)
                && cc.ValueKind == JsonValueKind.True;

            if (!hasBall && !hasClub) return false;   // status / heartbeat — relayed, not a shot

            var ball = root.TryGetProperty("BallData", out var b) ? b : default;
            var club = root.TryGetProperty("ClubData", out var c) ? c : default;

            // ProTee Labs owns the shot numbering on this lane; pass it through.
            int shotNum = root.TryGetProperty("ShotNumber", out var sn)
                && sn.ValueKind == JsonValueKind.Number ? sn.GetInt32() : 0;

            var ballSpeed = ParseNumeric(ball, "Speed");
            var spin = ParseNumeric(ball, "TotalSpin");
            var vla = ParseNumeric(ball, "VLA");
            var clubSpeed = ParseNumeric(club, "Speed");

            // When PTL splits a swing it sends ball-only then club-only under one
            // ShotNumber. The ball message is the shot; the club follow-up is noted
            // but must not count again or re-fire the shot readout.
            if (!hasBall)
            {
                Emit($"  Club data for #{shotNum}: ClubSpeed={clubSpeed:F1}");
                return false;
            }

            Emit($"SHOT #{shotNum} [Proxy] Speed={ballSpeed:F1} Spin={spin:F0} VLA={vla:F1} ClubSpeed={clubSpeed:F1}");
            ShotCount++;
            ShotReceived?.Invoke(new ShotInfo(shotNum, ballSpeed, spin, vla, clubSpeed, hasBall, hasClub));
            return true;
        }
        catch (JsonException)
        {
            Emit("  Relayed a message that wasn't valid JSON.");
            return false;
        }
    }

    private async Task HandleShotFolderAsync(string shotDir, CancellationToken ct)
    {
        try
        {
            var json = await WaitForShotDataAsync(shotDir, ct);
            if (json is null)
            {
                Emit($"ShotData.json never appeared in {Path.GetFileName(shotDir)} — skipping.");
                return;
            }

            using (json)
            {
                int shotNum = ++_watcherShotNumber;
                var openConnect = BuildOpenConnectMessage(json.RootElement, shotNum);
                var bytes = Encoding.UTF8.GetBytes(openConnect + "\n");

                LogShotFromFolder(json.RootElement, shotNum);

                // Try once on the existing persistent connection. On failure,
                // reconnect once and try again. After that, drop the shot.
                for (int attempt = 1; attempt <= 2; attempt++)
                {
                    if (!await EnsureConnectedAsync(ct))
                    {
                        if (attempt == 2) Emit("  Shot dropped: no connection to sim.");
                        continue;
                    }

                    if (await TryWriteAsync(bytes, ct)) return;

                    if (attempt == 1) Emit("  Reconnecting and retrying...");
                }
                Emit("  Shot dropped: could not send after reconnect.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Emit($"Folder shot handler error: {ex.Message}");
        }
    }

    /// <summary>
    /// Ties the sim connection's lifetime to ProTee Labs's. Proxy mode only — Folder
    /// Watcher has no upstream socket and keeps connecting at Start.
    ///
    /// Both sims drive their connected/ready UI purely from socket-open state, with no
    /// app-level liveness check in either direction (see the protocol notes: Infinite
    /// Tees never sends anything unprompted). Holding our end open while PTL is away
    /// therefore advertises a relay that cannot deliver: the sim sits at READY, the user
    /// swings, and nothing happens. Closing our side is the only signal the sim
    /// understands — and it's readable from where the user is actually standing.
    ///
    /// The cost is that the sim briefly shows disconnected whenever PTL reconnects. That
    /// is the honest reading: until PTL is back, no shot can reach the sim.
    /// </summary>
    private void OnUpstreamConnectionChanged(bool connected)
    {
        IsUpstreamConnected = connected;

        // Any shot still awaiting its ready signal belonged to the connection that just
        // ended. Carrying it across would fire a "ready" at a PTL that never sent a shot.
        ClearPendingReadySignal();

        if (connected)
        {
            Emit("ProTee Labs is connected — waiting for shots.");

            // Connect now rather than waiting for the supervisor's next tick, so the sim
            // is up before the first swing rather than a couple of seconds into it.
            var cts = _cts;
            if (cts is null) return;
            try { _ = TryConnectAsync(cts.Token); }
            catch (ObjectDisposedException) { }   // Stop() raced us
            return;
        }

        // No "ProTee Labs disconnected" line here: the listener already reports the drop,
        // and with how long PTL had been silent first — which is the diagnostic half.
        if (_persistentStream is not null)
        {
            var (sim, _) = ResolveForwardTarget();
            Emit($"  Closing the {sim} connection too — leaving it open would keep {sim} " +
                 "showing ready for shots that can't arrive.");
        }

        DisposeConnection();
    }

    /// <summary>
    /// Schedule the synthesized "ready for the next shot" signal to ProTee Labs,
    /// superseding any signal already pending for this shot.
    ///
    /// Timing is the whole point. The sim acks a shot in about a millisecond, but the
    /// game is not ready then — it's still resolving the ball flight. Infinite Tees says
    /// so ~12s later with a pair of Code 201s, which is when GSPro sends its 202. Firing
    /// on the ack instead would tell PTL "ready" twelve seconds early.
    ///
    /// Nothing here fires if the sim sends its own 202 first — see ClearPendingReadySignal.
    /// </summary>
    private void ScheduleReadySignal(TimeSpan delay, CancellationToken ct)
    {
        var generation = Interlocked.Increment(ref _readySignalGeneration);

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, ct);

                // Superseded by a later 201, a sim-sent 202, a new shot, or a disconnect.
                if (Volatile.Read(ref _readySignalGeneration) != generation) return;
                if (!_shotAwaitingReadySignal) return;

                var listener = _listener;
                if (listener is null) return;

                _shotAwaitingReadySignal = false;
                if (await listener.SendUpstreamAsync(ReadyForNextShot, ct))
                    Emit("  → ProTee Labs: [202] GSPro ready (synthesized).");
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                // Engine stopping — nothing to report.
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// Drop any pending ready signal. Called when the sim sends its own Code 202, and
    /// whenever a connection ends — a shot mid-flight belongs to the session that just
    /// went away, so announcing "ready" afterwards would be describing nothing.
    /// </summary>
    private void ClearPendingReadySignal()
    {
        Interlocked.Increment(ref _readySignalGeneration);
        _shotAwaitingReadySignal = false;
    }

    /// <summary>
    /// Ensure we have an open persistent connection to the sim. If the existing
    /// one is gone (or never established), connect now. Returns false on failure
    /// so callers can decide whether to retry or drop the shot.
    /// </summary>
    private async Task<bool> EnsureConnectedAsync(CancellationToken ct)
    {
        if (_persistentClient?.Connected == true && _persistentStream is not null) return true;
        return await TryConnectAsync(ct);
    }

    private async Task<bool> TryConnectAsync(CancellationToken ct)
    {
        // Single chokepoint for the "no sim link without ProTee Labs" rule, so a late
        // in-flight message or a supervisor tick can't quietly re-open what
        // OnUpstreamConnectionChanged just closed.
        if (ProxyMode && !IsUpstreamConnected) return false;

        await _connectLock.WaitAsync(ct);
        try
        {
            if (_persistentClient?.Connected == true && _persistentStream is not null) return true;

            DisposeConnection();

            var (sim, port) = ResolveForwardTarget();
            var client = new TcpClient();
            using var connectTimeout = new CancellationTokenSource(5000);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, connectTimeout.Token);
            try
            {
                await client.ConnectAsync(ForwardHost, port, linked.Token);
            }
            catch (SocketException sex)
            {
                EmitConnectFailure($"Cannot reach {sim} on port {port}: {sex.SocketErrorCode} ({sex.Message})");
                client.Dispose();
                return false;
            }
            catch (OperationCanceledException) when (connectTimeout.IsCancellationRequested)
            {
                EmitConnectFailure($"Cannot reach {sim} on port {port}: connect timed out after 5s");
                client.Dispose();
                return false;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Engine is stopping — not a connect failure worth reporting.
                client.Dispose();
                return false;
            }
            catch (Exception ex)
            {
                EmitConnectFailure($"Cannot reach {sim} on port {port}: {ex.Message}");
                client.Dispose();
                return false;
            }

            // Re-check after the await: connecting can take up to 5s, and if ProTee Labs
            // left during it, OnUpstreamConnectionChanged has already run and torn down a
            // connection that didn't exist yet. Publishing this one now would hand the sim
            // an open socket with nothing left to close it until PTL came back.
            if (ProxyMode && !IsUpstreamConnected)
            {
                client.Dispose();
                return false;
            }

            _persistentClient = client;
            _persistentStream = client.GetStream();
            IsConnected = true;
            _connectFailureLogged = false;
            _lastSimActivityUtc = DateTime.UtcNow;

            var downFor = _simDownSinceUtc is { } since
                ? $" after {(DateTime.UtcNow - since).TotalSeconds:F1}s down"
                : string.Empty;
            _simDownSinceUtc = null;
            Emit($"Connected to {sim} on :{port} (persistent){downFor}.");

            // Background reader; captured stream so it survives field swaps.
            var streamRef = _persistentStream;
            _readerTask = Task.Run(() => ReadResponsesAsync(streamRef, ct), ct);
            return true;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    /// <summary>
    /// Keep the persistent connection to the sim established for as long as it *should*
    /// be up — which in Proxy mode means "while ProTee Labs is connected", not "while we
    /// are running". See <see cref="OnUpstreamConnectionChanged"/>.
    ///
    /// The connection is opened once at Start (Folder Watcher) or on PTL's arrival
    /// (Proxy), but before this loop existed a sim
    /// launched *after* us was never picked up: the only other reconnect trigger is an
    /// inbound message, so the sim stayed disconnected until a shot arrived and the
    /// user had to stop and restart VX Connector. Infinite Tees compounds it — it
    /// binds :999 only on entering a round or practice range, so "the sim is running"
    /// is not the same thing as "the port is open".
    ///
    /// Retries stay fast for the first few attempts so a sim opened moments later is
    /// picked up promptly, then back off so a genuinely absent sim stays cheap.
    /// </summary>
    private async Task ReconnectLoopAsync(CancellationToken ct)
    {
        int[] delaysMs = { 2000, 2000, 3000, 5000, 5000, 10000 };
        int streak = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(delaysMs[Math.Min(streak, delaysMs.Length - 1)], ct);

                // In Proxy mode a missing sim link is intentional while ProTee Labs is
                // away — reconnecting here would put the sim straight back to advertising
                // ready. Reset the streak so the first attempt after PTL returns is fast.
                if (ProxyMode && !IsUpstreamConnected)
                {
                    // Backstop for any ordering that still slipped a connection through:
                    // the rule is enforced where the link opens, but this is the one place
                    // that re-checks it on a timer, so a stale link can't outlive PTL.
                    if (_persistentStream is not null) DisposeConnection();
                    streak = 0;
                    continue;
                }

                if (_persistentStream is not null && _persistentClient?.Connected == true)
                {
                    streak = 0;   // healthy — the next outage starts at the fast cadence
                    continue;
                }

                streak = await TryConnectAsync(ct) ? 0 : streak + 1;
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }   // Stop() disposed the CTS mid-wait
        }
    }

    /// <summary>
    /// Report a failed connect once per outage. The reconnect loop retries every few
    /// seconds, so emitting every failure would bury everything else in the log.
    /// Reset when a connect succeeds, so the next outage reports again.
    /// </summary>
    private void EmitConnectFailure(string message)
    {
        if (_connectFailureLogged) return;
        _connectFailureLogged = true;
        Emit(message);
        Emit("  Retrying in the background — no need to restart.");
    }

    private async Task<bool> TryWriteAsync(byte[] bytes, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var stream = _persistentStream;
            if (stream is null) return false;
            try
            {
                await stream.WriteAsync(bytes, ct);
                _lastSimActivityUtc = DateTime.UtcNow;
                return true;
            }
            catch (IOException ex)
            {
                var inner = ex.InnerException as SocketException;
                Emit(inner is not null
                    ? $"  Write failed: {inner.SocketErrorCode} ({inner.Message}){IdleSuffix()}"
                    : $"  Write failed: {ex.Message}{IdleSuffix()}");
                DisposeConnection();
                return false;
            }
            catch (ObjectDisposedException)
            {
                // Stream was torn down by reader or Stop(); just signal caller to retry.
                return false;
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Read the sim's responses, re-frame them, and relay them upstream one object at a
    /// time.
    ///
    /// The re-framing is not cosmetic. Infinite Tees batches messages: observed
    /// 2026-08-16 sending two `Code 201`s in a single write with **no delimiter between
    /// them** (`{…} {…}`), at a hole transition. Relaying that verbatim handed ProTee Labs
    /// a frame shape the real GSPconnect never produces — every one of its 20 server
    /// frames in `samples/protee-gspro.pcapng` is a single object, its 201 pair spaced
    /// 256ms apart — and PTL closed the connection 4ms later.
    ///
    /// So we frame on brace depth (the same <see cref="JsonObjectFramer"/> that handles
    /// PTL's un-delimited stream in the other direction) and write each object separately,
    /// spaced by <see cref="UpstreamObjectGap"/> so a batch cannot land in one read on
    /// PTL's side. It also fixes our own parsing: a batched pair failed
    /// <see cref="LogServerResponse"/> outright, logging "Server (raw)" and silently
    /// missing the 201 that drives the ready signal.
    /// </summary>
    private async Task ReadResponsesAsync(NetworkStream stream, CancellationToken ct)
    {
        var buf = new byte[4096];
        var framer = new JsonObjectFramer(MaxSimResponseBytes);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int read;
                try
                {
                    read = await stream.ReadAsync(buf.AsMemory(), ct);
                }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }
                catch (IOException ex)
                {
                    // A stream that is no longer the current one was closed by us — a mode
                    // switch, PTL leaving, or Stop. Reporting our own teardown as a "server
                    // connection error" sent past debugging down the wrong path.
                    if (!ReferenceEquals(_persistentStream, stream)) return;

                    var inner = ex.InnerException as SocketException;
                    Emit(inner is not null
                        ? $"  Server connection error: {inner.SocketErrorCode} ({inner.Message}){IdleSuffix()}"
                        : $"  Server connection error: {ex.Message}{IdleSuffix()}");
                    InvalidateIfCurrent(stream);
                    return;
                }

                if (read == 0)
                {
                    Emit($"  Server closed the connection (FIN).{IdleSuffix()}");
                    InvalidateIfCurrent(stream);
                    return;
                }

                _lastSimActivityUtc = DateTime.UtcNow;

                var relayed = 0;
                foreach (var message in framer.Consume(buf, read))
                {
                    var kinds = LogServerResponse(message);

                    // In proxy mode ProTee Labs is the real client — it needs the sim's
                    // acks to consider the connection healthy, so pass them straight back.
                    if (_listener is null) continue;

                    // Keep a batch out of a single read on PTL's side.
                    if (relayed++ > 0) await Task.Delay(UpstreamObjectGap, ct);

                    await _listener.SendUpstreamAsync(Encoding.UTF8.GetBytes(message), ct);

                    if (kinds.Ready)
                    {
                        // The sim announced ready itself — stand down. Sending ours too
                        // would hand PTL the same message twice per shot.
                        ClearPendingReadySignal();
                    }
                    else if (kinds.PlayerInfo)
                    {
                        _simAnnouncesPlayerInfo = true;
                        if (!_shotAwaitingReadySignal) continue;

                        // The sim's player-info burst is the real "ball placed, game is
                        // back" moment — GSPro's 202 follows it, so ours does too. Each
                        // 201 pushes it back, so a pair produces one signal after the last.
                        ScheduleReadySignal(ReadyAfterPlayerInfo, ct);
                    }
                }

                if (framer.TakeOverflowFlag())
                    Emit("  Sim response exceeded size limit — frame parser reset.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Emit($"  Reader error: {ex.Message}");
            InvalidateIfCurrent(stream);
        }
    }

    /// <summary>Tear down the persistent connection if it's still the one this reader was reading.</summary>
    private void InvalidateIfCurrent(NetworkStream stream)
    {
        if (ReferenceEquals(_persistentStream, stream)) DisposeConnection();
    }

    /// <summary>
    /// How long the sim socket had been silent in both directions. Reported on every
    /// drop: a close that lands ~10s after the last traffic is Infinite Tees's
    /// ClientIdleTimeoutSec, whereas one that lands mid-conversation is the sim tearing
    /// its socket hub down. The log line is the only place that distinction is visible.
    /// </summary>
    private string IdleSuffix() =>
        _lastSimActivityUtc == default
            ? string.Empty
            : $" (no traffic for {(DateTime.UtcNow - _lastSimActivityUtc).TotalSeconds:F1}s)";

    private void DisposeConnection()
    {
        // The shot that was awaiting a ready signal was going to that sim. With the link
        // gone there is no game state left to describe.
        ClearPendingReadySignal();

        var stream = _persistentStream;
        var client = _persistentClient;
        _persistentStream = null;
        _persistentClient = null;
        if (IsConnected) _simDownSinceUtc = DateTime.UtcNow;
        IsConnected = false;
        try { stream?.Dispose(); } catch { }
        try { client?.Dispose(); } catch { }
    }

    /// <summary>
    /// Polls for ShotData.json in the new shot directory. The directory event
    /// fires before ProTee Labs finishes writing the file, so we retry on
    /// IOException / JsonException until parseable or the deadline lapses.
    /// </summary>
    private static async Task<JsonDocument?> WaitForShotDataAsync(string shotDir, CancellationToken ct)
    {
        var path = Path.Combine(shotDir, "ShotData.json");
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (File.Exists(path))
            {
                try
                {
                    var bytes = await File.ReadAllBytesAsync(path, ct);
                    return JsonDocument.Parse(bytes);
                }
                catch (JsonException) { /* still being written — retry */ }
                catch (IOException) { /* file locked — retry */ }
            }
            await Task.Delay(100, ct);
        }
        return null;
    }

    /// <summary>
    /// Convert a ShotData.json document (with stringly-typed values like "147.5 mph")
    /// into a single-shot OpenConnect JSON message with raw numeric fields.
    /// Includes every numeric field ShotData.json carries; receivers should ignore
    /// fields they don't recognize.
    /// </summary>
    private static string BuildOpenConnectMessage(JsonElement shotData, int shotNumber)
    {
        var ball = shotData.TryGetProperty("BallData", out var b) ? b : default;
        var club = shotData.TryGetProperty("ClubData", out var c) ? c : default;
        var flight = shotData.TryGetProperty("FlightData", out var f) ? f : default;
        var smash = ParseNumeric(shotData, "SmashFactor");
        var clubName = ReadString(club, "ClubName");

        var payload = new
        {
            DeviceID = "ProTee Labs",
            Units = "Yards",
            ShotNumber = shotNumber,
            APIversion = "1",
            ClubIndex = 0,
            BallData = new
            {
                Speed = ParseNumeric(ball, "Speed"),
                SpinAxis = ParseNumeric(ball, "SpinAxis"),
                TotalSpin = ParseNumeric(ball, "TotalSpin"),
                BackSpin = ParseNumeric(ball, "BackSpin"),
                SideSpin = ParseNumeric(ball, "SideSpin"),
                RifleSpin = ParseNumeric(ball, "RifleSpin"),
                HLA = ParseNumeric(ball, "LaunchDirection"),
                VLA = ParseNumeric(ball, "LaunchAngle"),
                CarryDistance = ParseNumeric(flight, "Carry"),
                SmashFactor = smash,
            },
            ClubData = new
            {
                Speed = ParseNumeric(club, "Speed"),
                SpeedAtImpact = ParseNumeric(club, "Speed"),
                AngleOfAttack = ParseNumeric(club, "AttackAngle"),
                FaceToTarget = ParseNumeric(club, "FaceAngle"),
                FaceToPath = ParseNumeric(club, "FaceToPath"),
                Lie = ParseNumeric(club, "Lie"),
                Loft = ParseNumeric(club, "Loft"),
                SpinLoft = ParseNumeric(club, "SpinLoft"),
                Path = ParseNumeric(club, "SwingPath"),
                HorizontalFaceImpact = ParseNumeric(club, "ImpactPointX"),
                VerticalFaceImpact = ParseNumeric(club, "ImpactPointY"),
                ImpactRatioX = ParseNumeric(club, "ImpactRatioX"),
                ImpactRatioY = ParseNumeric(club, "ImpactRatioY"),
                ClosureRate = ParseNumeric(club, "ClosureRate"),
                ClubName = clubName,
            },
            ShotDataOptions = new
            {
                ContainsBallData = true,
                ContainsClubData = true,
                LaunchMonitorIsReady = false,
                LaunchMonitorBallDetected = false,
                IsHeartBeat = false,
            },
        };

        return JsonSerializer.Serialize(payload);
    }

    private static string? ReadString(JsonElement parent, string property)
    {
        if (parent.ValueKind != JsonValueKind.Object) return null;
        if (!parent.TryGetProperty(property, out var v)) return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    private static readonly Regex LeadingNumber = new(@"-?\d+(\.\d+)?", RegexOptions.Compiled);

    private static double ParseNumeric(JsonElement parent, string property)
    {
        if (parent.ValueKind != JsonValueKind.Object) return 0;
        if (!parent.TryGetProperty(property, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        if (v.ValueKind != JsonValueKind.String) return 0;

        var s = v.GetString();
        if (string.IsNullOrEmpty(s)) return 0;
        var m = LeadingNumber.Match(s);
        return m.Success && double.TryParse(m.Value, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0;
    }

    private void LogShotFromFolder(JsonElement shotData, int shotNum)
    {
        var ball = shotData.TryGetProperty("BallData", out var b) ? b : default;
        var club = shotData.TryGetProperty("ClubData", out var c) ? c : default;

        var ballSpeed = ParseNumeric(ball, "Speed");
        var spin = ParseNumeric(ball, "TotalSpin");
        var vla = ParseNumeric(ball, "LaunchAngle");
        var clubSpeed = ParseNumeric(club, "Speed");

        Emit($"SHOT #{shotNum} [Folder] Speed={ballSpeed:F1} Spin={spin:F0} VLA={vla:F1} ClubSpeed={clubSpeed:F1}");
        ShotCount++;
        ShotReceived?.Invoke(new ShotInfo(shotNum, ballSpeed, spin, vla, clubSpeed, true, true));
    }

    /// <summary>Which OpenConnect codes a batch of sim responses carried.</summary>
    /// <param name="PlayerInfo">Code 201 — GSPro's "GSPro Player Information", Infinite Tees's "IT Player Information".</param>
    /// <param name="Ready">Code 202 — the sim announced ready for the next shot on its own.</param>
    private readonly record struct SimResponseKinds(bool PlayerInfo, bool Ready);

    /// <summary>
    /// Log one framed sim response. Takes a single JSON object — the framer has already
    /// split any batch, so this no longer splits on newlines, which is what failed on
    /// Infinite Tees's un-delimited `{…} {…}` pair.
    /// </summary>
    private SimResponseKinds LogServerResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var msg = doc.RootElement.TryGetProperty("Message", out var m) ? m.GetString() : null;
            int? code = doc.RootElement.TryGetProperty("Code", out var c) && c.ValueKind == JsonValueKind.Number
                ? c.GetInt32()
                : null;

            Emit($"  Server: [{code?.ToString() ?? "?"}] {msg ?? json}{PlayerSuffix(doc.RootElement)}");
            return new SimResponseKinds(code == 201, code == 202);
        }
        catch (JsonException)
        {
            Emit($"  Server (raw): {json}");
            return default;
        }
    }

    /// <summary>
    /// Player context carried by a Code 201, appended to its log line.
    ///
    /// This is what lets a hole transition be identified in a log after the fact: the pair
    /// "LW at 10.2 to target" then "GW at 111.1 to target" is holing out and teeing off,
    /// which is how the 2026-08-16 disconnect was placed against a hole change. Parsing
    /// the message down to "IT Player Information" threw that away — the only reason it
    /// was visible at the time was that the batched pair failed to parse and got dumped raw.
    /// </summary>
    private static string PlayerSuffix(JsonElement root)
    {
        if (!root.TryGetProperty("Player", out var player) || player.ValueKind != JsonValueKind.Object)
            return string.Empty;

        var parts = new List<string>();

        if (player.TryGetProperty("Handed", out var handed) && handed.ValueKind == JsonValueKind.String)
            parts.Add(handed.GetString()!);
        if (player.TryGetProperty("Club", out var club) && club.ValueKind == JsonValueKind.String)
            parts.Add(club.GetString()!);
        if (player.TryGetProperty("DistanceToTarget", out var distance) && distance.ValueKind == JsonValueKind.Number)
            parts.Add($"{distance.GetDouble():F1} to target");
        // Null in every capture so far, but it costs nothing to surface it if it ever isn't.
        if (player.TryGetProperty("Surface", out var surface) && surface.ValueKind == JsonValueKind.String)
            parts.Add(surface.GetString()!);

        return parts.Count == 0 ? string.Empty : $" ({string.Join(", ", parts)})";
    }

    /// <summary>
    /// Write a message to the log window from outside the engine (e.g. the tray menu).
    /// Events can only be raised by their declaring type, so callers route through here.
    /// </summary>
    public void RaiseLog(string message) => Emit(message);

    private void Emit(string msg) =>
        Log?.Invoke($"[{DateTime.Now:HH:mm:ss.fff}] {msg}");

    private void OnStatusChanged() =>
        StatusChanged?.Invoke(Status);

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
        GC.SuppressFinalize(this);
    }
}
