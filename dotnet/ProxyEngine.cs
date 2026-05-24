using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VxProxy;

public enum ConnectionStatus { Stopped, Direct, FolderWatcher }

public enum EngineMode { Direct, FolderWatcherDrills, FolderWatcherInfiniteTees }

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
/// Runs in one of two modes:
///   Direct        - process exists only to satisfy ProTee Labs's GSPconnect.exe
///                   check. ProTee Labs talks straight to the target sim on :921.
///   FolderWatcher - watches ProTee's shots folder, converts each shot to
///                   OpenConnect JSON, forwards it to the target sim's port.
/// </summary>
public class ProxyEngine : IDisposable
{
    private const string ForwardHost = "127.0.0.1";

    private CancellationTokenSource? _cts;
    private ShotFolderWatcher? _folderWatcher;
    private int _watcherShotNumber;

    // Persistent TCP connection to the sim (Folder Watcher mode). Held for the
    // lifetime of a session so the sim's UI sees us as continuously connected,
    // matching the OpenConnect spec's "constant 2-way communication" intent.
    private TcpClient? _persistentClient;
    private NetworkStream? _persistentStream;
    private Task? _readerTask;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    public int ShotCount { get; private set; }
    public bool IsRunning { get; private set; }

    /// <summary>Which mode Start() runs. Default Folder Watcher → Infinite Tees.</summary>
    public EngineMode Mode { get; set; } = EngineMode.FolderWatcherInfiniteTees;

    public bool FolderWatcherMode => Mode != EngineMode.Direct;

    public event Action<string>? Log;
    public event Action<ConnectionStatus>? StatusChanged;
    public event Action<ShotInfo>? ShotReceived;

    public ConnectionStatus Status =>
        !IsRunning ? ConnectionStatus.Stopped :
        Mode == EngineMode.Direct ? ConnectionStatus.Direct :
        ConnectionStatus.FolderWatcher;

    public bool Start()
    {
        if (IsRunning) return true;

        _cts = new CancellationTokenSource();

        if (Mode != EngineMode.Direct)
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
            _ = TryConnectAsync(_cts.Token);
            return true;
        }

        // Direct mode — nothing to bind, nothing to watch. Just exist.
        IsRunning = true;
        var directTarget = SimConfig.DetectDirectTarget();
        var label = directTarget == SimTarget.None ? "the target sim" : directTarget.ToString();
        Emit($"Direct mode — VX Proxy is idle. ProTee Labs talks straight to {label} on :921.");
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
        EngineMode.FolderWatcherDrills =>
            (SimTarget.Drills, SimConfig.GetDrillsPort() ?? 921),
        EngineMode.FolderWatcherInfiniteTees =>
            (SimTarget.InfiniteTees, SimConfig.GetInfiniteTeesPort() ?? 999),
        _ => throw new InvalidOperationException(
            "ResolveForwardTarget is only valid in a folder-watcher mode."),
    };

    public void Stop()
    {
        if (!IsRunning) return;
        Emit("Stopping...");
        _cts?.Cancel();
        _folderWatcher?.Dispose();
        _folderWatcher = null;
        // Disposing the connection forces any in-flight ReadAsync to unblock
        // (cancellation doesn't propagate cleanly through socket reads on Windows).
        DisposeConnection();
        _cts?.Dispose();
        _cts = null;
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
                Emit($"Cannot reach {sim} on port {port}: {sex.SocketErrorCode} ({sex.Message})");
                client.Dispose();
                return false;
            }
            catch (OperationCanceledException) when (connectTimeout.IsCancellationRequested)
            {
                Emit($"Cannot reach {sim} on port {port}: connect timed out after 5s");
                client.Dispose();
                return false;
            }
            catch (Exception ex)
            {
                Emit($"Cannot reach {sim} on port {port}: {ex.Message}");
                client.Dispose();
                return false;
            }

            _persistentClient = client;
            _persistentStream = client.GetStream();
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

                LogServerResponse(buf[..read]);
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
