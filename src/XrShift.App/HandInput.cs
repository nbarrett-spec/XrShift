using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace XrShift.App;

/// <summary>One hand as the helper saw it: 21 points across the camera picture (x right, y down, 0..1).</summary>
internal sealed record TrackedHand(string Side, float Score, Vector3[] Points);

/// <summary>What the helper reported last.</summary>
internal sealed record HandFrame(double At, bool Camera, IReadOnlyList<TrackedHand> Hands);

/// <summary>
/// Hand tracking for gesture mode. Runs the helper (Google's MediaPipe in its own Python environment in
/// %LOCALAPPDATA%\XrShift\hands, set up by scripts\hands\setup.ps1), which reads the glasses' camera while the
/// glasses are in Anchor mode and sends hand points here over UDP on this PC only.
/// Nothing in this class or the helper ever sends anything to the glasses.
/// </summary>
internal sealed class HandInput : IDisposable
{
    public const int Port = 52411;

    static string Home => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XrShift", "hands");
    static string Python => Path.Combine(Home, "venv", "Scripts", "python.exe");
    static string Model => Path.Combine(Home, "hand_landmarker.task");
    static string Script => Path.Combine(AppContext.BaseDirectory, "hands", "handserver.py");

    /// <summary>The helper's Python environment, model and script are all there.</summary>
    public static bool Installed => File.Exists(Python) && File.Exists(Model) && File.Exists(Script);

    readonly Stopwatch _clock = Stopwatch.StartNew();
    UdpClient? _udp;
    Thread? _listener;
    Process? _helper;
    volatile HandFrame? _latest;
    volatile bool _running;
    string _lastError = "";

    public bool Running => _running;
    public HandFrame? Latest => _latest;
    public double Now => _clock.Elapsed.TotalSeconds;

    /// <summary>The glasses are sending camera frames (they are in Anchor mode) and the helper is getting them.</summary>
    public bool CameraLive => _latest is { Camera: true } f && Now - f.At < 0.6;

    /// <summary>A short word for the settings window and the status line.</summary>
    public string Status =>
        !Installed ? "not set up"
        : !_running ? "off"
        : _helper is { HasExited: true } ? "helper stopped" + (_lastError.Length > 0 ? ": " + _lastError : "")
        : _latest is null ? "starting"
        : !CameraLive ? "switch the glasses to stationary"
        : _latest.Hands.Count == 0 ? "no hand in view"
        : "hand in view";

    public bool Start()
    {
        if (_running) return true;
        if (!Installed) return false;
        try
        {
            _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, Port));
            _running = true;
            _listener = new Thread(Listen) { IsBackground = true, Name = "XrShift hands" };
            _listener.Start();

            var start = new ProcessStartInfo(Python)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                WorkingDirectory = Home,
            };
            start.ArgumentList.Add(Script);
            start.ArgumentList.Add("--model");
            start.ArgumentList.Add(Model);
            start.ArgumentList.Add("--port");
            start.ArgumentList.Add(Port.ToString());
            // The camera comes through XrShift's own relay, so the glasses have a single listener.
            start.ArgumentList.Add("--host");
            start.ArgumentList.Add("127.0.0.1");
            start.ArgumentList.Add("--camera-port");
            start.ArgumentList.Add(GlassesCamera.RelayPort.ToString());
            _helper = Process.Start(start);
            if (_helper is not null)
            {
                // MediaPipe chats on stderr; keep only real errors, and never let a full pipe stall the helper.
                _helper.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data is { } line && (line.Contains("Error", StringComparison.Ordinal) || line.Contains("Traceback", StringComparison.Ordinal)))
                        _lastError = line.Length > 160 ? line[..160] : line;
                };
                _helper.OutputDataReceived += (_, _) => { };
                _helper.BeginErrorReadLine();
                _helper.BeginOutputReadLine();
            }
            Log.Line("Hand tracking started.");
            return true;
        }
        catch (Exception e) when (e is SocketException or System.ComponentModel.Win32Exception or IOException)
        {
            Log.Line($"Hand tracking could not start: {e.Message}");
            Stop();
            return false;
        }
    }

    public void Stop()
    {
        bool was = _running;
        _running = false;
        try { _udp?.Dispose(); } catch (Exception) { }
        _udp = null;
        try
        {
            if (_helper is { HasExited: false }) _helper.Kill(entireProcessTree: true);
        }
        catch (Exception) { }
        if (_helper is { HasExited: true } && _lastError.Length > 0) Log.Line($"Hand helper: {_lastError}");
        _helper?.Dispose();
        _helper = null;
        _latest = null;
        if (was) Log.Line("Hand tracking stopped.");
    }

    void Listen()
    {
        var any = new IPEndPoint(IPAddress.Any, 0);
        while (_running && _udp is { } udp)
        {
            try
            {
                byte[] data = udp.Receive(ref any);
                if (Parse(data, Now) is { } frame) _latest = frame;
            }
            catch (Exception) when (!_running)
            {
                return;
            }
            catch (Exception)
            {
                // A message that does not parse is skipped.
            }
        }
    }

    static HandFrame? Parse(byte[] data, double at)
    {
        using JsonDocument doc = JsonDocument.Parse(data);
        JsonElement root = doc.RootElement;
        bool camera = root.TryGetProperty("camera", out JsonElement c) && c.ValueKind == JsonValueKind.True;
        var hands = new List<TrackedHand>();
        if (camera && root.TryGetProperty("hands", out JsonElement list))
        {
            foreach (JsonElement hand in list.EnumerateArray())
            {
                JsonElement points = hand.GetProperty("points");
                if (points.GetArrayLength() < 21) continue;
                var p = new Vector3[21];
                int i = 0;
                foreach (JsonElement point in points.EnumerateArray())
                {
                    if (i >= 21) break;
                    p[i++] = new Vector3(point[0].GetSingle(), point[1].GetSingle(), point[2].GetSingle());
                }
                hands.Add(new TrackedHand(
                    hand.TryGetProperty("side", out JsonElement side) ? side.GetString() ?? "" : "",
                    hand.TryGetProperty("score", out JsonElement score) ? score.GetSingle() : 0f,
                    p));
            }
        }
        return new HandFrame(at, camera, hands);
    }

    public void Dispose() => Stop();

    // ---- the mouse ----------------------------------------------------------------------------------

    public static void MoveMouse(int dx, int dy)
    {
        if (dx == 0 && dy == 0) return;
        Point at = Cursor.Position;
        Cursor.Position = new Point(at.X + dx, at.Y + dy);
    }

    public static void MouseButton(bool down) => mouse_event(down ? 0x0002u : 0x0004u, 0, 0, 0, UIntPtr.Zero);

    [DllImport("user32.dll")]
    static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
}
