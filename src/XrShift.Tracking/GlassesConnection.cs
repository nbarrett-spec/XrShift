using System.Net;
using System.Net.Sockets;

namespace XrShift.Tracking;

public enum LinkState { Disconnected, Connecting, Connected }

/// <summary>
/// Reads the head-motion stream from the glasses over their USB network link and keeps
/// reconnecting. Only one program can hold this stream at a time.
/// </summary>
public sealed class GlassesConnection
{
    public const string DefaultHost = "169.254.2.1";
    public const int ImuPort = 52998;

    public string Host { get; init; } = DefaultHost;
    public int Port { get; init; } = ImuPort;

    public ImuFrameParser Parser { get; } = new();
    public LinkState State { get; private set; } = LinkState.Disconnected;
    public long BytesReceived => Interlocked.Read(ref _bytes);

    /// <summary>Optional copy of every received byte, for recording.</summary>
    public Stream? RawTee { get; set; }

    public event Action<ImuSample>? Sample;
    public event Action<LinkState>? StateChanged;

    long _bytes;

    /// <summary>Runs the read loop on its own background thread until cancelled.</summary>
    public Thread Start(CancellationToken ct)
    {
        var thread = new Thread(() => Run(ct)) { IsBackground = true, Name = "Glasses IMU" };
        thread.Start();
        return thread;
    }

    public void Run(CancellationToken ct)
    {
        byte[] buffer = new byte[8192];
        void Emit(ImuSample s) => Sample?.Invoke(s);

        while (!ct.IsCancellationRequested)
        {
            Set(LinkState.Connecting);
            try
            {
                using var client = new TcpClient { NoDelay = true, ReceiveTimeout = 3000 };
                using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    connectCts.CancelAfter(2000);
                    client.ConnectAsync(IPAddress.Parse(Host), Port, connectCts.Token).AsTask().GetAwaiter().GetResult();
                }

                using CancellationTokenRegistration closeOnCancel = ct.Register(client.Close);
                Parser.Reset();
                Set(LinkState.Connected);

                NetworkStream stream = client.GetStream();
                while (!ct.IsCancellationRequested)
                {
                    int n = stream.Read(buffer, 0, buffer.Length);
                    if (n == 0) break;
                    Interlocked.Add(ref _bytes, n);
                    RawTee?.Write(buffer, 0, n);
                    Parser.Feed(buffer.AsSpan(0, n), Emit);
                }
            }
            catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or ObjectDisposedException)
            {
                // Link down, timed out or cancelled: fall through and retry.
            }

            Set(LinkState.Disconnected);
            try
            {
                if (ct.WaitHandle.WaitOne(1000)) break;
            }
            catch (ObjectDisposedException)
            {
                break; // the owner has gone (a session that failed while starting): stop quietly, never crash the app
            }
        }
    }

    void Set(LinkState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(state);
    }
}
