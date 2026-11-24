using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace XrShift.App;

/// <summary>
/// The glasses' control channel (TCP 169.254.2.1:52999). XrShift uses exactly two requests, and refuses to send
/// anything else:
/// - read the glasses' mode (0x28B5): 0 = stationary (Anchor), 1 = Follow, 2 = Side View, 3 = UltraWide;
/// - recentre (0x2827): in stationary mode the glasses glide the picture they hold still to where the wearer
///   is looking (tested 2026-11-18: an instant, smooth slide).
/// Framing: 2-byte id and 4-byte length (big-endian), then a 4-byte transaction number with the top bit set
/// and a small protobuf body; the answer carries the same transaction number without the top bit.
/// </summary>
internal sealed class GlassesControl : IDisposable
{
    const string Host = "169.254.2.1";
    const int Port = 52999;
    const ushort GetMode = 0x28B5, Recentre = 0x2827;

    readonly object _gate = new();
    readonly Dictionary<uint, TaskCompletionSource<byte[]>> _pending = [];
    CancellationTokenSource? _stop;
    TcpClient? _client;
    NetworkStream? _stream;
    uint _transaction;
    Thread? _poller;

    /// <summary>Plain screen: XrShift leaves the glasses completely alone (no requests at all).</summary>
    public volatile bool Paused;

    /// <summary>The glasses' mode as they last reported it, or null while it is not known.</summary>
    public int? Mode { get; private set; }
    bool _modeLogged;

    /// <summary>The glasses are holding their picture still (stationary / Anchor).</summary>
    public bool? Stationary => Mode is { } m ? m is 0 or 3 : null; // 3 = their ultrawide, also held still

    public void Start()
    {
        if (_stop is not null) return;
        _stop = new CancellationTokenSource();
        CancellationToken ct = _stop.Token;
        _poller = new Thread(() =>
        {
            while (!ct.IsCancellationRequested)
            {
                if (Paused) { Mode = null; Drop(); }
                else
                {
                    int? read = ReadMode();
                    // Said once per change, so the log shows which mode the glasses were really in (or that they would not say).
                    if (read != Mode || !_modeLogged) Log.Line(read is { } m ? $"Glasses report mode {m} (0 Anchor, 1 Follow, 2 Side View, 3 UltraWide)." : "The glasses did not say which mode they are in; assuming Follow.");
                    _modeLogged = true;
                    Mode = read;
                }
                try { if (ct.WaitHandle.WaitOne(700)) return; } catch (ObjectDisposedException) { return; }
            }
        }) { IsBackground = true, Name = "Glasses control" };
        _poller.Start();
    }

    /// <summary>Asks the glasses to glide their held picture to where the wearer looks. Returns at once.</summary>
    public void RecentreHeldPicture() => ThreadPool.QueueUserWorkItem(_ =>
    {
        if (Paused) return;
        byte[]? answer = Request(Recentre, [0x1a, 0x00]);
        if (answer is null) Log.Line("The glasses did not answer a recentre.");
    });

    int? ReadMode()
    {
        byte[]? answer = Request(GetMode, [0x18, 0x00]);
        if (answer is null || answer.Length < 2 || answer[0] != 0x22) return null;
        // 22 <len> { 10 <mode> }; an empty inner message means mode 0.
        int length = answer[1], at = 2, end = Math.Min(answer.Length, 2 + length);
        while (at + 1 < end)
        {
            byte tag = answer[at++];
            if (tag == 0x10) return answer[at] & 0x7F;
            if ((tag & 7) == 0) { while (at < end && (answer[at++] & 0x80) != 0) { } }
            else return null;
        }
        return 0;
    }

    byte[]? Request(ushort id, byte[] body)
    {
        if (id != GetMode && id != Recentre) throw new InvalidOperationException("XrShift sends only mode reads and recentres to the glasses");
        TaskCompletionSource<byte[]> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        NetworkStream? stream;
        uint transaction;
        lock (_gate)
        {
            if (!Connect()) return null;
            stream = _stream;
            transaction = ++_transaction & 0x7FFFFFFF;
            _pending[transaction] = answer;
        }
        try
        {
            byte[] packet = new byte[6 + 4 + body.Length];
            BinaryPrimitives.WriteUInt16BigEndian(packet, id);
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(2), (uint)(4 + body.Length));
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(6), transaction | 0x80000000);
            body.CopyTo(packet, 10);
            lock (_gate) stream!.Write(packet);
            return answer.Task.Wait(1500) ? answer.Task.Result : null;
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException or AggregateException)
        {
            Drop();
            return null;
        }
        finally
        {
            lock (_gate) _pending.Remove(transaction);
        }
    }

    bool Connect()
    {
        if (_stream is not null) return true;
        try
        {
            var client = new TcpClient { NoDelay = true };
            if (!client.ConnectAsync(IPAddress.Parse(Host), Port).Wait(1500)) { client.Dispose(); return false; }
            _client = client;
            _stream = client.GetStream();
            NetworkStream stream = _stream;
            new Thread(() => Read(stream)) { IsBackground = true, Name = "Glasses control answers" }.Start();
            return true;
        }
        catch (Exception e) when (e is SocketException or AggregateException or IOException)
        {
            return false;
        }
    }

    void Read(NetworkStream stream)
    {
        byte[] head = new byte[6];
        try
        {
            while (true)
            {
                stream.ReadExactly(head);
                uint length = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(2));
                if (length > 1 << 20) throw new IOException("message too long");
                byte[] payload = new byte[length];
                stream.ReadExactly(payload);
                if (payload.Length < 4) continue; // events without a transaction
                uint transaction = BinaryPrimitives.ReadUInt32BigEndian(payload) & 0x7FFFFFFF;
                TaskCompletionSource<byte[]>? waiting;
                lock (_gate) _pending.TryGetValue(transaction, out waiting);
                waiting?.TrySetResult(payload[4..]);
            }
        }
        catch (Exception)
        {
            Drop(stream);
        }
    }

    void Drop(NetworkStream? which = null)
    {
        lock (_gate)
        {
            if (which is not null && !ReferenceEquals(which, _stream)) return;
            try { _client?.Dispose(); } catch (Exception) { }
            _client = null;
            _stream = null;
        }
    }

    public void Dispose()
    {
        _stop?.Cancel();
        _poller?.Join(1500);
        Drop();
        _stop?.Dispose();
    }
}
