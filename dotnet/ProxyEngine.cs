using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VxProxy;

public enum ConnectionStatus { Stopped, Direct, FolderWatcher }

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

    public int ShotCount { get; private set; }
    public bool IsRunning { get; private set; }

    /// <summary>When true, Start() runs a folder watcher; otherwise runs Direct.</summary>
    public bool FolderWatcherMode { get; set; }

    public event Action<string>? Log;
    public event Action<ConnectionStatus>? StatusChanged;
    public event Action<ShotInfo>? ShotReceived;

    public ConnectionStatus Status =>
        !IsRunning ? ConnectionStatus.Stopped :
        FolderWatcherMode ? ConnectionStatus.FolderWatcher :
        ConnectionStatus.Direct;

    public bool Start()
    {
        if (IsRunning) return true;

        _cts = new CancellationTokenSource();

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
            var (sim, port) = SimConfig.GetForwardTarget();
            Emit($"Folder watcher mode — forwarding shots to {sim} at {ForwardHost}:{port}.");
            Emit("(Target re-resolved per shot, so toggling Infinite Tees direct mode mid-session is fine.)");
            OnStatusChanged();
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

    public void Stop()
    {
        if (!IsRunning) return;
        Emit("Stopping...");
        _cts?.Cancel();
        _folderWatcher?.Dispose();
        _folderWatcher = null;
        _cts?.Dispose();
        _cts = null;
        IsRunning = false;
        OnStatusChanged();
    }

    /// <summary>Switch modes while running. Stops the current mode and starts the new one.</summary>
    public bool RestartIn(bool folderWatcherMode)
    {
        FolderWatcherMode = folderWatcherMode;
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

                var (sim, port) = SimConfig.GetForwardTarget();

                using var client = new TcpClient();
                using var connectTimeout = new CancellationTokenSource(5000);
                using var connectLinked = CancellationTokenSource.CreateLinkedTokenSource(ct, connectTimeout.Token);
                try
                {
                    await client.ConnectAsync(ForwardHost, port, connectLinked.Token);
                }
                catch (Exception ex)
                {
                    Emit($"Cannot reach {sim} on port {port}: {ex.Message}");
                    return;
                }

                var stream = client.GetStream();
                await stream.WriteAsync(bytes, ct);

                var buf = new byte[4096];
                using var readTimeout = new CancellationTokenSource(2000);
                using var readLinked = CancellationTokenSource.CreateLinkedTokenSource(ct, readTimeout.Token);
                try
                {
                    int read = await stream.ReadAsync(buf.AsMemory(), readLinked.Token);
                    if (read > 0) LogServerResponse(buf[..read]);
                }
                catch (OperationCanceledException) { /* no response — that's ok */ }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Emit($"Folder shot handler error: {ex.Message}");
        }
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
        try
        {
            using var doc = JsonDocument.Parse(data);
            var msg = doc.RootElement.TryGetProperty("Message", out var m)
                ? m.GetString() : null;
            Emit($"  Server: {msg ?? Encoding.UTF8.GetString(data).Trim()}");
        }
        catch
        {
            // non-JSON response — ignore
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
