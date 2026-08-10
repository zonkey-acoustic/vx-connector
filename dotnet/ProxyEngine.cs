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

    // Persistent TCP connection to the sim (Folder Watcher mode). Held for the
    // lifetime of a session so the sim's UI sees us as continuously connected,
    // matching the OpenConnect spec's "constant 2-way communication" intent.
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
    /// Only meaningful in Folder Watcher modes; always false in Direct mode and when stopped.
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
            _listener.UpstreamConnectionChanged += connected =>
            {
                IsUpstreamConnected = connected;
                Emit(connected
                    ? "ProTee Labs is connected — waiting for shots."
                    : "ProTee Labs disconnected.");
            };

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

            // Connect to the sim up front so its UI shows us as connected from the
            // moment we start, not just on the first shot. The supervisor then keeps
            // retrying, so starting before the sim is up recovers without a restart.
            _ = TryConnectAsync(_cts.Token);
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
        var label = directTarget == SimTarget.None ? "the target sim" : directTarget.ToString();
        Emit($"Direct mode — VX Connector is idle. ProTee Labs talks straight to {label} on :921.");
        OnStatusChanged();
        return true;
    }

    /// <summary>
    /// Resolve the forward target from the current Mode. The port is re-read
    /// from sim config each call so a user changing the sim's port mid-session
    /// is picked up on the next shot.
    /// </summary>
    public (SimTarget sim, int port) ResolveForwardTarget() => Mode switch
    {
        EngineMode.FolderWatcherDrills or EngineMode.ProxyDrills =>
            (SimTarget.Drills, SimConfig.GetDrillsPort() ?? 921),
        EngineMode.FolderWatcherInfiniteTees or EngineMode.ProxyInfiniteTees =>
            (SimTarget.InfiniteTees, SimConfig.GetInfiniteTeesPort() ?? 999),
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
            LogShotFromUpstream(json);

            for (int attempt = 1; attempt <= 2; attempt++)
            {
                if (!await EnsureConnectedAsync(ct))
                {
                    if (attempt == 2) Emit("  Message dropped: no connection to sim.");
                    continue;
                }

                if (await TryWriteAsync(bytes, ct)) return;

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
    private void LogShotFromUpstream(string json)
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

            if (!hasBall && !hasClub) return;   // status / heartbeat — relayed, not a shot

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
                return;
            }

            Emit($"SHOT #{shotNum} [Proxy] Speed={ballSpeed:F1} Spin={spin:F0} VLA={vla:F1} ClubSpeed={clubSpeed:F1}");
            ShotCount++;
            ShotReceived?.Invoke(new ShotInfo(shotNum, ballSpeed, spin, vla, clubSpeed, hasBall, hasClub));
        }
        catch (JsonException)
        {
            Emit("  Relayed a message that wasn't valid JSON.");
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

            _persistentClient = client;
            _persistentStream = client.GetStream();
            IsConnected = true;
            _connectFailureLogged = false;
            Emit($"Connected to {sim} on :{port} (persistent).");

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
    /// Keep the persistent connection to the sim established for as long as we run.
    ///
    /// The connection is opened once at Start, but before this loop existed a sim
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
                return true;
            }
            catch (IOException ex)
            {
                var inner = ex.InnerException as SocketException;
                Emit(inner is not null
                    ? $"  Write failed: {inner.SocketErrorCode} ({inner.Message})"
                    : $"  Write failed: {ex.Message}");
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

    private async Task ReadResponsesAsync(NetworkStream stream, CancellationToken ct)
    {
        var buf = new byte[4096];
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
                    var inner = ex.InnerException as SocketException;
                    Emit(inner is not null
                        ? $"  Server connection error: {inner.SocketErrorCode} ({inner.Message})"
                        : $"  Server connection error: {ex.Message}");
                    InvalidateIfCurrent(stream);
                    return;
                }

                if (read == 0)
                {
                    Emit("  Server closed the connection (FIN).");
                    InvalidateIfCurrent(stream);
                    return;
                }

                var response = buf[..read];
                LogServerResponse(response);

                // In proxy mode ProTee Labs is the real client — it needs the sim's
                // acks to consider the connection healthy, so pass them straight back.
                if (_listener is not null)
                {
                    await _listener.SendUpstreamAsync(response, ct);
                }
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

    private void DisposeConnection()
    {
        var stream = _persistentStream;
        var client = _persistentClient;
        _persistentStream = null;
        _persistentClient = null;
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

    private void LogServerResponse(byte[] data)
    {
        var text = Encoding.UTF8.GetString(data);
        var lines = text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in lines)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var msg = doc.RootElement.TryGetProperty("Message", out var m) ? m.GetString() : null;
                var code = doc.RootElement.TryGetProperty("Code", out var c) && c.ValueKind == JsonValueKind.Number
                    ? c.GetInt32().ToString()
                    : "?";
                Emit($"  Server: [{code}] {msg ?? line}");
            }
            catch
            {
                Emit($"  Server (raw): {line}");
            }
        }
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
