using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace VxProxy;

public enum ConnectionStatus { Stopped, Listening, ProTeeOnly, Connected }

public record ShotInfo(
    int ShotNumber,
    double BallSpeed,
    double TotalSpin,
    double VLA,
    double ClubSpeed,
    bool HasBall,
    bool HasClub
);

public class ProxyEngine : IDisposable
{
    private const string ListenHost = "127.0.0.1";
    private const int ListenPort = 921;
    private const string InfiniteTeesHost = "127.0.0.1";
    private const int InfiniteTeesPort = 999;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private bool _proTeeConnected;
    private bool _infiniteTeesConnected;

    public int ShotCount { get; private set; }
    public bool IsRunning { get; private set; }

    /// <summary>Fired on every log line (already timestamped).</summary>
    public event Action<string>? Log;

    /// <summary>Fired when connection status changes.</summary>
    public event Action<ConnectionStatus>? StatusChanged;

    /// <summary>Fired when a shot with ball data is received.</summary>
    public event Action<ShotInfo>? ShotReceived;

    public ConnectionStatus Status =>
        !IsRunning ? ConnectionStatus.Stopped :
        _proTeeConnected && _infiniteTeesConnected ? ConnectionStatus.Connected :
        _proTeeConnected ? ConnectionStatus.ProTeeOnly :
        ConnectionStatus.Listening;

    public bool Start()
    {
        if (IsRunning) return true;

        try
        {
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Parse(ListenHost), ListenPort);
            _listener.Start(1);
            IsRunning = true;

            Emit($"Proxy listening on {ListenHost}:{ListenPort}");
            Emit($"Forwarding to Infinite Tees at {InfiniteTeesHost}:{InfiniteTeesPort}");
            Emit("Waiting for ProTee Labs to connect...");
            OnStatusChanged();

            _ = AcceptLoopAsync(_cts.Token);
            return true;
        }
        catch (SocketException ex)
        {
            Emit($"Cannot bind to port {ListenPort}: {ex.Message}");
            Emit("Is something else already listening on that port?");
            return false;
        }
    }

    public void Stop()
    {
        if (!IsRunning) return;
        Emit("Stopping proxy...");
        _cts?.Cancel();
        _listener?.Stop();
        IsRunning = false;
        _proTeeConnected = false;
        _infiniteTeesConnected = false;
        OnStatusChanged();
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await _listener!.AcceptTcpClientAsync(ct);
                _ = HandleClientAsync(client, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            IsRunning = false;
            OnStatusChanged();
        }
    }

    private async Task HandleClientAsync(TcpClient proTee, CancellationToken ct)
    {
        var ep = proTee.Client.RemoteEndPoint as IPEndPoint;
        Emit($"ProTee Labs connected from {ep?.Address}:{ep?.Port}");
        _proTeeConnected = true;
        OnStatusChanged();

        // Try Infinite Tees once up front, but don't block on retries — the read
        // loop below handles per-message reconnects and falls back to auto-generated
        // responses when Infinite Tees is unavailable. Blocking here would leave
        // ProTee Labs without any responses to its messages.
        TcpClient? infiniteTees = await ConnectToInfiniteTeesAsync();

        var buffer = new StringBuilder();

        try
        {
            var stream = proTee.GetStream();
            var readBuf = new byte[4096];

            while (!ct.IsCancellationRequested)
            {
                int read;
                try
                {
                    using var timeout = new CancellationTokenSource(2000);
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
                    read = await stream.ReadAsync(readBuf.AsMemory(), linked.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    continue; // read timeout — keep looping
                }

                if (read == 0)
                {
                    Emit("ProTee Labs disconnected.");
                    break;
                }

                buffer.Append(Encoding.UTF8.GetString(readBuf, 0, read));

                // Extract complete JSON objects from the buffer
                while (TryExtractJson(buffer, out var json))
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    var opts = root.TryGetProperty("ShotDataOptions", out var o) ? o : default;
                    bool hasBall = GetBool(opts, "ContainsBallData");
                    bool hasClub = GetBool(opts, "ContainsClubData");
                    bool isHb = GetBool(opts, "IsHeartBeat");
                    bool ready = GetBool(opts, "LaunchMonitorIsReady");
                    bool detected = GetBool(opts, "LaunchMonitorBallDetected");
                    int shotNum = root.TryGetProperty("ShotNumber", out var sn)
                        ? sn.GetInt32() : 0;

                    // Log shot info
                    LogShotMessage(root, hasBall, hasClub, isHb, ready, detected, shotNum);

                    // Forward to Infinite Tees
                    var forward = Encoding.UTF8.GetBytes(json + "\n");

                    if (!_infiniteTeesConnected)
                    {
                        infiniteTees?.Dispose();
                        infiniteTees = await ConnectToInfiniteTeesAsync();
                    }

                    byte[]? infiniteTeesResponse = null;
                    if (_infiniteTeesConnected && infiniteTees != null)
                    {
                        infiniteTeesResponse = await ForwardToInfiniteTeesAsync(infiniteTees, forward);
                        if (infiniteTeesResponse == null && (hasBall || hasClub))
                        {
                            Emit("Reconnecting to infiniteTees...");
                            infiniteTees.Dispose();
                            infiniteTees = await ConnectToInfiniteTeesAsync();
                            if (_infiniteTeesConnected && infiniteTees != null)
                                infiniteTeesResponse = await ForwardToInfiniteTeesAsync(infiniteTees, forward);
                        }
                    }

                    // Respond to ProTee Labs
                    byte[] response;
                    if (infiniteTeesResponse != null)
                    {
                        LogInfiniteTeesResponse(infiniteTeesResponse);
                        response = infiniteTeesResponse;
                    }
                    else
                    {
                        var msg = (hasBall, hasClub) switch
                        {
                            (true, true) => "Club & Ball Data received",
                            (true, false) => "Ball Data received",
                            (false, true) => "Club Data received",
                            _ => "Shot received successfully"
                        };
                        response = MakeResponse(200, msg);
                    }

                    await stream.WriteAsync(response, ct);
                }
            }
        }
        catch (IOException)
        {
            Emit("ProTee Labs connection reset.");
        }
        catch (SocketException)
        {
            Emit("ProTee Labs connection reset.");
        }
        catch (Exception ex)
        {
            Emit($"Error: {ex.Message}");
        }
        finally
        {
            proTee.Dispose();
            infiniteTees?.Dispose();
            _proTeeConnected = false;
            _infiniteTeesConnected = false;
            OnStatusChanged();
            Emit("Session ended.");
        }
    }

    private async Task<TcpClient?> ConnectToInfiniteTeesAsync()
    {
        try
        {
            var client = new TcpClient();
            using var timeout = new CancellationTokenSource(5000);
            await client.ConnectAsync(InfiniteTeesHost, InfiniteTeesPort, timeout.Token);
            Emit($"Connected to Infinite Tees at {InfiniteTeesHost}:{InfiniteTeesPort}");
            _infiniteTeesConnected = true;
            OnStatusChanged();
            return client;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            Emit($"Cannot connect to Infinite Tees on port {InfiniteTeesPort} — will retry");
            _infiniteTeesConnected = false;
            OnStatusChanged();
            return null;
        }
        catch (Exception ex)
        {
            Emit($"Infinite Tees connection error: {ex.Message}");
            _infiniteTeesConnected = false;
            OnStatusChanged();
            return null;
        }
    }

    private async Task<byte[]?> ForwardToInfiniteTeesAsync(TcpClient infiniteTees, byte[] data)
    {
        try
        {
            var stream = infiniteTees.GetStream();
            await stream.WriteAsync(data);

            using var timeout = new CancellationTokenSource(2000);
            var buf = new byte[4096];
            try
            {
                int read = await stream.ReadAsync(buf.AsMemory(), timeout.Token);
                return read > 0 ? buf[..read] : null;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }
        catch (Exception ex)
        {
            Emit($"Infinite Tees connection lost: {ex.Message}");
            _infiniteTeesConnected = false;
            OnStatusChanged();
            return null;
        }
    }

    /// <summary>
    /// Extract the first complete JSON object from the buffer using brace-depth counting.
    /// Handles cases where multiple messages arrive in one read, or a message spans reads.
    /// </summary>
    private static bool TryExtractJson(StringBuilder buffer, out string json)
    {
        json = "";
        var s = buffer.ToString().TrimStart();
        if (s.Length == 0 || s[0] != '{')
        {
            buffer.Clear();
            return false;
        }

        int depth = 0;
        bool inString = false;
        bool escaped = false;

        for (int i = 0; i < s.Length; i++)
        {
            char ch = s[i];

            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (ch == '\\' && inString)
            {
                escaped = true;
                continue;
            }

            if (ch == '"')
            {
                inString = !inString;
                continue;
            }

            if (inString) continue;

            if (ch == '{') depth++;
            else if (ch == '}') depth--;

            if (depth == 0)
            {
                json = s[..(i + 1)];
                buffer.Clear();
                buffer.Append(s[(i + 1)..]);
                return true;
            }
        }

        return false; // incomplete — wait for more data
    }

    private static byte[] MakeResponse(int code, string message) =>
        Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { Code = code, Message = message }) + "\n");

    private static bool GetBool(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty(prop, out var v)
        && v.ValueKind == JsonValueKind.True;

    private void LogShotMessage(JsonElement root, bool hasBall, bool hasClub,
        bool isHb, bool ready, bool detected, int shotNum)
    {
        if (hasBall)
        {
            var ball = root.GetProperty("BallData");
            var speed = ball.GetProperty("Speed").GetDouble();
            var spin = ball.GetProperty("TotalSpin").GetDouble();
            var vla = ball.GetProperty("VLA").GetDouble();

            if (hasClub)
            {
                var club = root.GetProperty("ClubData");
                var clubSpeed = club.GetProperty("Speed").GetDouble();
                Emit($"SHOT #{shotNum} [Ball+Club] Speed={speed:F1} Spin={spin:F0} " +
                     $"VLA={vla:F1} ClubSpeed={clubSpeed:F1}");
                ShotCount++;
                ShotReceived?.Invoke(new ShotInfo(shotNum, speed, spin, vla, clubSpeed, true, true));
            }
            else
            {
                Emit($"SHOT #{shotNum} [Ball] Speed={speed:F1} Spin={spin:F0} VLA={vla:F1}");
                ShotCount++;
                ShotReceived?.Invoke(new ShotInfo(shotNum, speed, spin, vla, 0, true, false));
            }
        }
        else if (hasClub)
        {
            var club = root.GetProperty("ClubData");
            Emit($"SHOT #{shotNum} [Club] ClubSpeed={club.GetProperty("Speed").GetDouble():F1} " +
                 $"AoA={club.GetProperty("AngleOfAttack").GetDouble():F1} " +
                 $"Path={club.GetProperty("Path").GetDouble():F1}");
        }
        else if (isHb)
        {
            // suppress heartbeat spam
        }
        else if (ready && detected)
        {
            Emit($"Status #{shotNum}: Ready, ball detected");
        }
        else if (ready)
        {
            Emit($"Status #{shotNum}: Ready");
        }
        else
        {
            Emit($"Status #{shotNum}: Not ready");
        }
    }

    private void LogInfiniteTeesResponse(byte[] data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var msg = doc.RootElement.TryGetProperty("Message", out var m)
                ? m.GetString() : null;
            Emit($"  Infinite Tees: {msg ?? Encoding.UTF8.GetString(data).Trim()}");
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
