using System.Net;
using System.Net.Sockets;
using System.Text;

namespace VxProxy;

/// <summary>
/// Accepts ProTee Labs's OpenConnect stream on :12321 — the lane the licensed
/// GSPconnect normally occupies. As of PTL beta 1.12.9.0 this is the only way to
/// get shot data: the OpenAPI (:921) lane was removed and ShotData.json is no
/// longer written.
///
/// PTL opens the lane only when a process named GSPconnect.exe exists (see
/// <see cref="GspConnectStub"/>), then dials us as a client and pushes shots.
///
/// Framing note: PTL does NOT newline-terminate on this lane. It writes bare,
/// back-to-back JSON objects, so we frame on brace depth rather than '\n'.
/// </summary>
public class OpenConnectListener : IDisposable
{
    /// <summary>The lane PTL dials for the "ProTee VX Connector" game connection.</summary>
    public const int DefaultPort = 12321;

    /// <summary>Guards against a malformed stream growing the buffer without bound.</summary>
    private const int MaxMessageBytes = 1024 * 1024;

    private readonly int _port;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private bool _disposed;

    // Reported when the upstream connection ends. ProTee Labs going quiet is what lets
    // the sim's idle timer run, so the gap before a drop is the first thing to check
    // when the relay falls over between holes.
    private DateTime _lastUpstreamActivityUtc;

    public int Port => _port;
    public bool IsRunning { get; private set; }

    /// <summary>One complete JSON object received from ProTee Labs.</summary>
    public event Action<string>? MessageReceived;
    public event Action<bool>? UpstreamConnectionChanged;
    public event Action<string>? Log;
    public event Action<string>? Error;

    public OpenConnectListener(int port = DefaultPort)
    {
        _port = port;
    }

    public bool Start()
    {
        if (IsRunning || _disposed) return IsRunning;

        try
        {
            _listener = new TcpListener(IPAddress.Loopback, _port)
            {
                // Without this, another process can bind the same port later and
                // silently steal ProTee Labs's connection out from under us.
                ExclusiveAddressUse = true,
            };
            _listener.Start();
        }
        catch (SocketException ex)
        {
            Error?.Invoke(ex.SocketErrorCode == SocketError.AddressAlreadyInUse
                ? $"Port {_port} is already in use — the licensed GSPconnect is probably running. " +
                  "Close GSPro/GSPconnect and try again."
                : $"Cannot listen on port {_port}: {ex.SocketErrorCode} ({ex.Message})");
            _listener = null;
            return false;
        }

        _cts = new CancellationTokenSource();
        IsRunning = true;
        Log?.Invoke($"Listening on 127.0.0.1:{_port} for ProTee Labs.");
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        return true;
    }

    public void Stop()
    {
        if (!IsRunning) return;

        _cts?.Cancel();
        DropClient();
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _cts?.Dispose();
        _cts = null;
        IsRunning = false;
        Log?.Invoke("Listener stopped.");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException ex)
            {
                Error?.Invoke($"Accept failed: {ex.SocketErrorCode} ({ex.Message})");
                await Task.Delay(1000, ct).ContinueWith(_ => { }, TaskScheduler.Default);
                continue;
            }

            // ProTee Labs reconnects on its own Connect action; the newest
            // connection always wins.
            DropClient();

            // Each relayed object should reach PTL as its own segment: it closed the
            // connection 4ms after receiving two batched objects in one write
            // (2026-08-16). ProxyEngine spaces them; NoDelay stops the stack recombining
            // them on the way out.
            client.NoDelay = true;

            _client = client;
            _stream = client.GetStream();

            // Logged before the event: the handler emits its own lines, and firing first
            // put "ProTee Labs connected." underneath them in the log window.
            Log?.Invoke("ProTee Labs connected.");
            UpstreamConnectionChanged?.Invoke(true);

            var streamRef = _stream;
            _ = Task.Run(() => ReadLoopAsync(streamRef, ct), ct);
        }
    }

    private async Task ReadLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        var buf = new byte[16384];
        var framer = new JsonObjectFramer(MaxMessageBytes);
        _lastUpstreamActivityUtc = DateTime.UtcNow;

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
                    Log?.Invoke(inner is not null
                        ? $"ProTee Labs connection error: {inner.SocketErrorCode}{IdleSuffix()}"
                        : $"ProTee Labs connection error: {ex.Message}{IdleSuffix()}");
                    InvalidateIfCurrent(stream);
                    return;
                }

                if (read == 0)
                {
                    Log?.Invoke($"ProTee Labs disconnected.{IdleSuffix()}");
                    InvalidateIfCurrent(stream);
                    return;
                }

                _lastUpstreamActivityUtc = DateTime.UtcNow;
                foreach (var message in framer.Consume(buf, read))
                {
                    MessageReceived?.Invoke(message);
                }

                if (framer.TakeOverflowFlag())
                {
                    Error?.Invoke("Upstream message exceeded size limit — frame parser reset.");
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error?.Invoke($"Reader error: {ex.Message}");
            InvalidateIfCurrent(stream);
        }
    }

    /// <summary>Relay a sim response back upstream to ProTee Labs.</summary>
    public async Task<bool> SendUpstreamAsync(byte[] bytes, CancellationToken ct = default)
    {
        var stream = _stream;
        if (stream is null) return false;

        await _sendLock.WaitAsync(ct);
        try
        {
            await stream.WriteAsync(bytes, ct);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            InvalidateIfCurrent(stream);
            return false;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>How long ProTee Labs had sent nothing before the connection ended.</summary>
    private string IdleSuffix() =>
        _lastUpstreamActivityUtc == default
            ? string.Empty
            : $" (silent for {(DateTime.UtcNow - _lastUpstreamActivityUtc).TotalSeconds:F1}s)";

    private void InvalidateIfCurrent(NetworkStream stream)
    {
        if (ReferenceEquals(_stream, stream)) DropClient();
    }

    private void DropClient()
    {
        var stream = _stream;
        var client = _client;
        _stream = null;
        _client = null;
        if (stream is not null || client is not null) UpstreamConnectionChanged?.Invoke(false);
        try { stream?.Dispose(); } catch { }
        try { client?.Dispose(); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _sendLock.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Splits a byte stream of concatenated JSON objects into complete messages.
///
/// ProTee Labs sends bare objects with no delimiter, and an object can span
/// several reads, so parser state (brace depth, string/escape) is carried
/// across <see cref="Consume"/> calls rather than rescanning a buffer.
///
/// Byte-level scanning is safe here: JSON's structural characters are ASCII, and
/// a UTF-8 continuation byte is never mistaken for one.
/// </summary>
internal sealed class JsonObjectFramer
{
    private readonly int _maxBytes;
    private readonly List<byte> _pending = new();
    private int _depth;
    private bool _inObject;
    private bool _inString;
    private bool _escaped;

    private bool _overflowed;

    public JsonObjectFramer(int maxBytes) => _maxBytes = maxBytes;

    /// <summary>Returns whether an oversized message was dropped since the last call, and clears the flag.</summary>
    public bool TakeOverflowFlag()
    {
        var flag = _overflowed;
        _overflowed = false;
        return flag;
    }

    public void Reset()
    {
        _pending.Clear();
        _depth = 0;
        _inObject = false;
        _inString = false;
        _escaped = false;
    }

    public IEnumerable<string> Consume(byte[] buffer, int count)
    {
        var messages = new List<string>();

        for (int i = 0; i < count; i++)
        {
            byte b = buffer[i];

            if (!_inObject)
            {
                // Skip whitespace or any stray bytes between objects.
                if (b != (byte)'{') continue;
                _inObject = true;
                _depth = 0;
            }

            _pending.Add(b);

            if (_pending.Count > _maxBytes)
            {
                _overflowed = true;
                Reset();
                continue;
            }

            if (_inString)
            {
                if (_escaped) _escaped = false;
                else if (b == (byte)'\\') _escaped = true;
                else if (b == (byte)'"') _inString = false;
                continue;
            }

            switch (b)
            {
                case (byte)'"':
                    _inString = true;
                    break;
                case (byte)'{':
                    _depth++;
                    break;
                case (byte)'}':
                    _depth--;
                    if (_depth == 0)
                    {
                        messages.Add(Encoding.UTF8.GetString(_pending.ToArray()));
                        _pending.Clear();
                        _inObject = false;
                    }
                    break;
            }
        }

        return messages;
    }
}
