using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace XrShift.App;

/// <summary>
/// Listens to the glasses' camera port. The glasses stream their camera only while they are in their own
/// stationary (Anchor) mode, so pictures arriving means the glasses are holding their picture still in the room:
/// that is how XrShift knows which mode they are in. The stream is passed on, whole messages only, to the hand
/// tracking helper on this PC (127.0.0.1:<see cref="RelayPort"/>), so the glasses have a single listener.
/// Nothing is ever sent to the glasses.
/// </summary>
internal sealed class GlassesCamera : IDisposable
{
    public const int RelayPort = 52412;
    const string Host = "169.254.2.1";
    const int Port = 52997;

    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly object _gate = new();
    CancellationTokenSource? _stop;
    Thread? _reader, _accepter;
    TcpListener? _relay;
    NetworkStream? _client;
    long _lastDataMs = -100_000, _streamingSinceMs = -1;
    bool _anchored;

    /// <summary>The glasses are in stationary (Anchor) mode: camera pictures are arriving. Settles over a moment
    /// either way, so a hiccup in the stream does not flip the app between modes.</summary>
    public bool Anchored
    {
        get
        {
            long now = _clock.ElapsedMilliseconds, last = Interlocked.Read(ref _lastDataMs);
            if (!_anchored && _streamingSinceMs >= 0 && now - _streamingSinceMs > 600 && now - last < 500) _anchored = true;
            else if (_anchored && now - last > 2500) _anchored = false;
            return _anchored;
        }
    }

    /// <summary>Plain screen: the camera port is let go of entirely.</summary>
    public volatile bool Paused;

    public void Start()
    {
        if (_stop is not null) return;
        _stop = new CancellationTokenSource();
        CancellationToken ct = _stop.Token;
        try
        {
            _relay = new TcpListener(IPAddress.Loopback, RelayPort);
            _relay.Start();
            _accepter = new Thread(() => Accept(ct)) { IsBackground = true, Name = "Glasses camera relay" };
            _accepter.Start();
        }
        catch (SocketException e)
        {
            Log.Line($"Camera relay could not start ({e.Message}); hand tracking will not get pictures.");
            _relay = null;
        }
        _reader = new Thread(() => Read(ct)) { IsBackground = true, Name = "Glasses camera" };
        _reader.Start();
    }

    void Accept(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _relay is { } relay)
        {
            try
            {
                TcpClient client = relay.AcceptTcpClient();
                client.NoDelay = true;
                lock (_gate)
                {
                    _client?.Dispose();
                    _client = client.GetStream();
                }
            }
            catch (Exception) when (ct.IsCancellationRequested) { return; }
            catch (Exception) { Thread.Sleep(200); }
        }
    }

    void Read(CancellationToken ct)
    {
        byte[] buffer = new byte[256 * 1024];
        var pending = new List<byte>(512 * 1024);
        while (!ct.IsCancellationRequested)
        {
            if (Paused)
            {
                try { if (ct.WaitHandle.WaitOne(500)) return; } catch (ObjectDisposedException) { return; }
                continue;
            }
            try
            {
                using var glasses = new TcpClient { NoDelay = true, ReceiveTimeout = 0 };
                using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    connect.CancelAfter(2000);
                    glasses.ConnectAsync(IPAddress.Parse(Host), Port, connect.Token).AsTask().GetAwaiter().GetResult();
                }
                using CancellationTokenRegistration close = ct.Register(glasses.Close);
                NetworkStream stream = glasses.GetStream();
                pending.Clear();
                while (!ct.IsCancellationRequested && !Paused)
                {
                    int n = stream.Read(buffer, 0, buffer.Length);
                    if (n == 0) break;
                    long now = _clock.ElapsedMilliseconds;
                    if (now - Interlocked.Read(ref _lastDataMs) > 1500) _streamingSinceMs = now;
                    Interlocked.Exchange(ref _lastDataMs, now);
                    Pass(buffer, n, pending);
                }
            }
            catch (Exception) when (ct.IsCancellationRequested) { return; }
            catch (Exception)
            {
                // Glasses unplugged or restarting: try again shortly.
            }
            try { if (ct.WaitHandle.WaitOne(1500)) return; } catch (ObjectDisposedException) { return; }
        }
    }

    // The stream is a run of messages: a 2-byte id and a 4-byte length (both big-endian), then that many bytes.
    // A helper that connects midway must start at the beginning of a message, so only whole ones are passed on.
    void Pass(byte[] data, int count, List<byte> pending)
    {
        NetworkStream? client;
        lock (_gate) client = _client;
        // The framing is followed even with nobody listening, so a helper that connects later starts on a boundary.
        for (int i = 0; i < count; i++) pending.Add(data[i]);
        int at = 0;
        while (pending.Count - at >= 6)
        {
            int length = (pending[at + 2] << 24) | (pending[at + 3] << 16) | (pending[at + 4] << 8) | pending[at + 5];
            if (length < 0 || length > 8 * 1024 * 1024) { pending.Clear(); return; } // lost the thread: start over
            if (pending.Count - at < 6 + length) break;
            if (client is not null)
            {
                try
                {
                    byte[] message = pending.GetRange(at, 6 + length).ToArray();
                    client.Write(message, 0, message.Length);
                }
                catch (Exception)
                {
                    lock (_gate) { if (ReferenceEquals(_client, client)) { _client.Dispose(); _client = null; } }
                    client = null;
                }
            }
            at += 6 + length;
        }
        pending.RemoveRange(0, at);
    }

    public void Dispose()
    {
        _stop?.Cancel();
        try { _relay?.Stop(); } catch (Exception) { }
        lock (_gate) { _client?.Dispose(); _client = null; }
        _reader?.Join(2000);
        _accepter?.Join(1000);
        _stop?.Dispose();
    }
}
