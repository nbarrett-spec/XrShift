using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using XrShift.Tracking;

namespace XrShift.Probe;

/// <summary>Command-line tool for checking the glasses' head-tracking link.</summary>
internal static class Program
{
    const float RadToDeg = 180f / MathF.PI;

    static string s_host = GlassesConnection.DefaultHost;
    static int s_port = GlassesConnection.ImuPort;

    static int Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        string command = args.Length > 0 ? args[0].ToLowerInvariant() : "live";
        string? path = Positional(args);
        s_host = Option(args, "--host") ?? s_host;
        if (int.TryParse(Option(args, "--port"), out int port)) s_port = port;

        switch (command)
        {
            case "live": return Live(Seconds(args, 0));
            case "dump": return Dump(path ?? Path.Combine("recordings", "imu.bin"), Seconds(args, 5));
            case "axes": return Axes();
            case "rest": return Rest(Seconds(args, 40));
            case "setup": return Setup(path, Option(args, "--from"));
            case "analyze" when path is not null: return Analyze(path);
            case "replay" when path is not null: return Replay(path);
            case "steady" when path is not null: return Steady(path);
            case "synth" when path is not null: return Synth(path);
            case "serve" when path is not null: return Serve(path, s_port);
            case "wearer": return Wearer(args);
            default: return Usage();
        }
    }

    static int Usage()
    {
        Console.WriteLine("""
            xrprobe live [--seconds N]         show head angles from the glasses
            xrprobe dump [file] [--seconds N]  record the raw stream (default 5 s)
            xrprobe analyze <file>             check a recording's format and rates
            xrprobe replay <file>              run a recording through the tracker
            xrprobe steady <file>              score picture steadiness on a recording, old prediction vs new
            xrprobe axes                       worn: measure how the sensor sits in the frame (10 s)
            xrprobe rest [--seconds N]         on a desk: measure the gyro's resting offset
            xrprobe setup [file] [--from S]    rest offset, then axes, as one guided run; S = rest|axes|level|view.
                                               With a recording, replays it and saves nothing.
            xrprobe synth <file>               write a fake recording (no glasses needed)
            xrprobe serve <file> [--port N]    act as the glasses on this PC, playing a recording
            xrprobe wearer [--port N]          act as glasses worn by a pretend person who goes through the app's glasses
              [--fov D --level D --lean D]     setup (over its trial Control API), with display angles known; prints what
                                               setup found against them. For trial sessions: --host 127.0.0.1 --port N --api
            Any command that reads the glasses takes --host H --port N (e.g. 127.0.0.1 for 'serve').
            """);
        return 1;
    }

    // ---- live ---------------------------------------------------------------------------

    static int Live(double seconds)
    {
        using CancellationTokenSource cts = MakeCts(seconds);
        HeadTracker tracker = TrackingConfig.CreateTracker();
        var conn = NewConnection();
        conn.Sample += s => tracker.Update(s);
        Thread thread = conn.Start(cts.Token);

        Console.WriteLine($"Connecting to {conn.Host}:{conn.Port}, axes {tracker.Axes}. Ctrl+C to stop.");
        bool inline = !Console.IsOutputRedirected;
        var clock = Stopwatch.StartNew();
        long lastCount = 0;
        double lastTime = 0;
        int tick = 0;

        while (!cts.IsCancellationRequested)
        {
            cts.Token.WaitHandle.WaitOne(100);
            if (!inline && ++tick % 10 != 0) continue;

            double now = clock.Elapsed.TotalSeconds;
            long count = conn.Parser.SamplesParsed;
            double rate = (count - lastCount) / Math.Max(now - lastTime, 1e-3);
            lastCount = count;
            lastTime = now;

            string line;
            if (conn.State != LinkState.Connected)
                line = $"{conn.State}: no link to the glasses yet";
            else if (count == 0)
                line = $"Connected, but no head-motion records yet ({conn.BytesReceived} bytes, {conn.Parser.OtherFrames} other records)";
            else
            {
                HeadPose p = tracker.GetPose();
                Vector3 b = tracker.GyroBias * RadToDeg;
                string bias = !tracker.BiasKnown ? "not known"
                    : $"{b.X:F2} {b.Y:F2} {b.Z:F2}{(tracker.BiasUpdates == 0 ? " (saved)" : " (live)")}";
                line = $"{rate,5:F0} Hz  yaw {p.YawDeg,7:F1}  pitch {p.PitchDeg,6:F1}  roll {p.RollDeg,6:F1}  gyro offset {bias}";
            }

            if (inline) Console.Write("\r" + line.PadRight(110));
            else Console.WriteLine(line);
        }

        if (inline) Console.WriteLine();
        thread.Join(5000);
        Console.WriteLine($"{conn.Parser.SamplesParsed} samples, {conn.Parser.OtherFrames} other records, {conn.BytesReceived} bytes");
        return conn.Parser.SamplesParsed > 0 ? 0 : 2;
    }

    // ---- dump ---------------------------------------------------------------------------

    static int Dump(string path, double seconds)
    {
        string? folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (folder is not null) Directory.CreateDirectory(folder);

        using CancellationTokenSource cts = MakeCts(seconds);
        var conn = NewConnection();
        using (FileStream file = File.Create(path))
        {
            conn.RawTee = file;
            Thread thread = conn.Start(cts.Token);
            Console.WriteLine($"Recording {seconds:F0} s of raw data to {path}");
            cts.Token.WaitHandle.WaitOne();
            thread.Join();
        }

        Console.WriteLine($"{conn.BytesReceived} bytes, {conn.Parser.SamplesParsed} samples, {conn.Parser.OtherFrames} other records");
        return conn.BytesReceived > 0 ? 0 : 2;
    }

    // ---- analyze ------------------------------------------------------------------------

    static int Analyze(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        Console.WriteLine($"{path}: {data.Length} bytes");

        var starts = new List<int>();
        var firstBytes = new Dictionary<string, int>();
        for (int i = 1; i + ImuFrame.HeaderTail.Length <= data.Length; i++)
        {
            if (!data.AsSpan(i, ImuFrame.HeaderTail.Length).SequenceEqual(ImuFrame.HeaderTail)) continue;
            starts.Add(i - 1);
            Tally(firstBytes, data[i - 1].ToString("x2"));
        }

        Console.WriteLine($"Header candidates (xx 36 00 00 00 80): {starts.Count}");
        PrintTop("  first byte", firstBytes);

        var spacing = new Dictionary<string, int>();
        var markers = new Dictionary<string, int>();
        for (int i = 0; i < starts.Count; i++)
        {
            if (i > 0) Tally(spacing, (starts[i] - starts[i - 1]).ToString());
            if (starts[i] + ImuFrame.ParseLength <= data.Length)
                Tally(markers, Convert.ToHexString(data, starts[i] + ImuFrame.MarkerOffset, 6).ToLowerInvariant());
        }
        PrintTop("  bytes between headers", spacing);
        PrintTop("  record type at offset 78", markers);

        if (starts.Count == 0)
        {
            Console.WriteLine("No known header found. First bytes:");
            HexDump(data, 320);
            return 2;
        }

        var samples = new List<ImuSample>();
        var parser = new ImuFrameParser();
        parser.Feed(data, samples.Add);
        Console.WriteLine($"Parsed {samples.Count} samples, {parser.OtherFrames} other records");
        if (samples.Count < 2) return 2;

        double span = (samples[^1].TimestampNs - samples[0].TimestampNs) * 1e-9;
        var steps = new List<double>(samples.Count);
        for (int i = 1; i < samples.Count; i++)
            steps.Add((samples[i].TimestampNs - (double)samples[i - 1].TimestampNs) * 1e-6);
        steps.Sort();
        Console.WriteLine($"Device clock: {span:F3} s, {(samples.Count - 1) / span:F1} Hz, step min/median/max {steps[0]:F3}/{steps[steps.Count / 2]:F3}/{steps[^1]:F3} ms");

        Vector3 gyroMean = Vector3.Zero, accelMean = Vector3.Zero;
        float accelLength = 0;
        foreach (ImuSample s in samples)
        {
            gyroMean += s.Gyro;
            accelMean += s.Accel;
            accelLength += s.Accel.Length();
        }
        gyroMean /= samples.Count;
        accelMean /= samples.Count;
        accelLength /= samples.Count;

        Vector3 gyroVar = Vector3.Zero;
        foreach (ImuSample s in samples)
        {
            Vector3 d = s.Gyro - gyroMean;
            gyroVar += d * d;
        }
        Vector3 gyroStd = Vector3.SquareRoot(gyroVar / samples.Count);

        Console.WriteLine($"Gyro mean  {Deg(gyroMean)} deg/s   spread {Deg(gyroStd)} deg/s");
        Console.WriteLine($"Accel mean {accelMean.X,7:F2} {accelMean.Y,7:F2} {accelMean.Z,7:F2} m/s2   average length {accelLength:F2} (gravity is 9.81)");
        return 0;
    }

    // ---- replay -------------------------------------------------------------------------

    static int Replay(string path)
    {
        HeadTracker tracker = TrackingConfig.CreateTracker();
        var parser = new ImuFrameParser();
        ulong first = 0, next = 0;

        parser.Feed(File.ReadAllBytes(path), s =>
        {
            tracker.Update(s);
            if (first == 0) first = next = s.TimestampNs;
            if (s.TimestampNs < next) return;
            next = s.TimestampNs + 500_000_000;
            HeadPose p = tracker.GetPose();
            Console.WriteLine($"t {(s.TimestampNs - first) * 1e-9,6:F2} s  yaw {p.YawDeg,7:F1}  pitch {p.PitchDeg,6:F1}  roll {p.RollDeg,6:F1}");
        });

        Console.WriteLine($"{parser.SamplesParsed} samples, {parser.OtherFrames} other records");
        return parser.SamplesParsed > 0 ? 0 : 2;
    }

    // ---- steady -------------------------------------------------------------------------

    /// <summary>
    /// Replays a recording through the old prediction (raw turn rate, no cap) and the new one, as if each
    /// frame were seen 15, 30 and 50 ms after its pose was read, and scores both.
    /// </summary>
    static int Steady(string path)
    {
        var samples = new List<ImuSample>();
        new ImuFrameParser().Feed(File.ReadAllBytes(path), samples.Add);
        if (samples.Count < 2)
        {
            Console.WriteLine("No head-motion records in that file.");
            return 2;
        }
        Console.WriteLine($"{samples.Count} samples, {(samples[^1].TimestampNs - samples[0].TimestampNs) * 1e-9:F1} s. Wander is while the head is still; px at {SteadyScore.PixelsPerDegree} px/deg.");
        Console.WriteLine("ahead  method          wander rms          wander peak         overshoot  steady-turn lag");

        foreach (float ms in new[] { 15f, 30f, 50f })
        {
            Print(ms, "old", t => t.GetPose(ms / 1000f));
            Print(ms, "new", t => t.GetDisplayPose(ms, 0f, out _));
            Print(ms, "new, no hold", t => t.GetDisplayPose(ms, 0f, out _), readingSteady: false);
        }
        return 0;

        void Print(float ms, string name, Func<HeadTracker, HeadPose> read, bool readingSteady = true)
        {
            HeadTracker tracker = TrackingConfig.CreateTracker();
            tracker.ReadingSteady = readingSteady;
            SteadyScore s = SteadyScore.Of(SteadyScore.Replay(samples, tracker, read), ms / 1000f);
            Console.WriteLine($"{ms,3:F0} ms  {name,-14}  {s.WanderRmsArcmin,5:F2}' {s.WanderRmsPx,5:F2} px    {s.WanderPeakArcmin,5:F2}' {s.WanderPeakPx,5:F2} px    {s.OvershootDeg,5:F2} deg  {s.LagMs,5:F1} ms");
        }
    }

    // ---- synth --------------------------------------------------------------------------

    /// <summary>Still 7 s, turn left 45° over 1 s, hold 1 s, look up 25° over 1 s, hold 4 s.</summary>
    static int Synth(string path)
    {
        string? folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (folder is not null) Directory.CreateDirectory(folder);

        var sim = new ImuSimulator { GyroBias = new Vector3(0.004f, -0.006f, 0.003f) };
        byte[] frame = new byte[ImuFrame.WireLength];
        using FileStream file = File.Create(path);

        void Run(int milliseconds, float yawLeft = 0, float pitchUp = 0)
        {
            for (int i = 0; i < milliseconds; i++)
            {
                ImuFrame.Write(frame, sim.Step(yawLeft, pitchUp));
                file.Write(frame);
            }
        }

        Run(7000);
        Run(1000, yawLeft: 45f / RadToDeg);
        Run(1000);
        Run(1000, pitchUp: 25f / RadToDeg);
        Run(4000);
        Console.WriteLine($"Wrote 14 s of fake head motion to {path}");
        return 0;
    }

    // ---- serve --------------------------------------------------------------------------

    /// <summary>Plays a recording to one client at the speed it was recorded, standing in for the glasses.</summary>
    static int Serve(string path, int port)
    {
        byte[] data = File.ReadAllBytes(path);
        var times = new List<ulong>();
        new ImuFrameParser().Feed(data, s => times.Add(s.TimestampNs));
        if (times.Count < 2 || times[^1] <= times[0])
        {
            Console.WriteLine("No head-motion records in that file.");
            return 2;
        }
        double seconds = (times[^1] - times[0]) * 1e-9;
        double bytesPerSecond = data.Length / seconds;

        using CancellationTokenSource cts = MakeCts(0);
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        using CancellationTokenRegistration stop = cts.Token.Register(listener.Stop);
        Console.WriteLine($"Fake glasses on 127.0.0.1:{port}, {seconds:F1} s of data. Read it with --host 127.0.0.1 --port {port}.");

        try
        {
            using TcpClient client = listener.AcceptTcpClient();
            client.NoDelay = true;
            NetworkStream stream = client.GetStream();
            var clock = Stopwatch.StartNew();
            int sent = 0;
            while (sent < data.Length && !cts.IsCancellationRequested)
            {
                int due = (int)Math.Min(data.Length, clock.Elapsed.TotalSeconds * bytesPerSecond);
                if (due > sent)
                {
                    stream.Write(data, sent, due - sent);
                    sent = due;
                }
                Thread.Sleep(2);
            }
            Console.WriteLine($"Sent {sent} of {data.Length} bytes.");
        }
        catch (SocketException) { }
        catch (IOException) { Console.WriteLine("Reader went away."); }
        finally { listener.Stop(); }
        return 0;
    }

    // ---- wearer -------------------------------------------------------------------------

    /// <summary>
    /// Stands in for the glasses and the person wearing them, for trying glasses setup without either: streams motion
    /// live, like 'serve', and watches setup through a trial session's Control API, doing what it asks. Set on the
    /// desk, then picked up, nodding, and for each line the head held so a true upright edge lies on it, for display
    /// angles chosen here. The sensor sits a few degrees off the usual mounting, so the axis step has something to find.
    /// </summary>
    static int Wearer(string[] args)
    {
        float Arg(string name, float fallback) =>
            float.TryParse(Option(args, name), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : fallback;
        float fov = Arg("--fov", 48.3f), level = Arg("--level", 1.2f), lean = Arg("--lean", -0.6f);
        string pipe = Option(args, "--pipe") ?? "ArWorkspace.Api.Trial";

        Assert(AxisMap.TryFromRightUp(new Vector3(0.998f, 0.04f, -0.03f), new Vector3(0.02f, -0.78f, 0.62f), out AxisMap mount));
        var sim = new ImuSimulator(mount) { GyroBias = new Vector3(0.004f, -0.006f, 0.003f), GyroNoise = 0.002f };
        var moves = new System.Collections.Concurrent.ConcurrentQueue<(Vector3 Rate, int Ms)>();
        int moving = 0; // moves taken but not yet played out
        using CancellationTokenSource cts = MakeCts(0);

        var listener = new TcpListener(IPAddress.Loopback, s_port);
        listener.Start();
        Console.WriteLine($"Pretend glasses on 127.0.0.1:{s_port}: display {fov:F1} degrees wide, level {level:F2}, lean {lean:F2}. Waiting for the app.");
        var streaming = new Thread(() =>
        {
            try
            {
                using TcpClient client = listener.AcceptTcpClient();
                NetworkStream stream = client.GetStream();
                byte[] frame = new byte[ImuFrame.WireLength];
                var clock = Stopwatch.StartNew();
                long sent = 0;
                (Vector3 Rate, int Ms) now = (Vector3.Zero, 0);
                while (!cts.IsCancellationRequested)
                {
                    for (long due = clock.ElapsedMilliseconds; sent < due; sent++)
                    {
                        if (now.Ms == 0 && moves.TryDequeue(out var next)) now = next;
                        Vector3 r = now.Ms > 0 ? now.Rate : Vector3.Zero;
                        ImuFrame.Write(frame, sim.Step(r.X, r.Y, r.Z));
                        stream.Write(frame);
                        if (now.Ms > 0 && --now.Ms == 0) Interlocked.Decrement(ref moving);
                    }
                    Thread.Sleep(2);
                }
            }
            catch (Exception e) when (e is IOException or SocketException) { Console.WriteLine("The app stopped reading."); cts.Cancel(); }
        }) { IsBackground = true };
        streaming.Start();

        // The head as the wearer holds it, degrees: turned left, looking up, tilted right.
        (float Yaw, float Pitch, float Roll) head = (0, 0, 0);
        void Turn(int axis, float degrees, int ms)
        {
            if (MathF.Abs(degrees) < 1e-4f) return;
            float speed = degrees / RadToDeg / (ms / 1000f);
            Vector3 rate = axis switch { 0 => new(speed, 0, 0), 1 => new(0, speed, 0), _ => new(0, 0, speed) }; // turn left, look up, tilt right
            Interlocked.Increment(ref moving);
            moves.Enqueue((rate, ms));
        }
        // Undo roll and pitch, turn, then pitch and roll again: each a turn about one of the head's own axes, so the
        // head ends exactly at yaw, then pitch, then roll, as EdgeFitter reads a head.
        void MoveTo(float yaw, float pitch, float roll, int ms = 250)
        {
            Turn(2, -head.Roll, ms);
            Turn(1, -head.Pitch, ms);
            Turn(0, yaw - head.Yaw, ms);
            Turn(1, pitch, ms);
            Turn(2, roll, ms);
            head = (yaw, pitch, roll);
        }

        var random = new Random(11);
        float Seen(EdgeTarget t, float yaw, float pitch, float roll) => EdgeFitter.VerticalTiltDeg(
            Quaternion.CreateFromYawPitchRoll(yaw / RadToDeg, pitch / RadToDeg, -roll / RadToDeg), t.XPx, fov, level, lean, 1920);
        (float, float, float) PoseFor(EdgeTarget t)
        {
            float pitch = t.Posture switch { HeadPosture.Up => 33, HeadPosture.Down => -33, _ => 0 } + (random.NextSingle() - 0.5f) * 6;
            float yaw = (random.NextSingle() - 0.5f) * 60, roll = 0;
            for (int i = 0; i < 30; i++) // tilt the head until the edge lies along the drawn line
            {
                float slope = (Seen(t, yaw, pitch, roll + 0.01f) - Seen(t, yaw, pitch, roll - 0.01f)) / 0.02f;
                roll -= (Seen(t, yaw, pitch, roll) - t.DrawnTiltDeg) / slope;
            }
            return (yaw, pitch, roll);
        }

        using var api = new System.IO.Pipes.NamedPipeClientStream(".", pipe, System.IO.Pipes.PipeDirection.InOut);
        StreamWriter? send = null;
        StreamReader? read = null;
        int id = 0;
        System.Text.Json.Nodes.JsonNode Call(string method, string? from = null)
        {
            string request = $$"""{"jsonrpc":"2.0","id":{{++id}},"method":"{{method}}"{{(from is null ? "" : $$""","params":{"from":"{{from}}"}""")}}}""";
            send!.WriteLine(request);
            System.Text.Json.Nodes.JsonNode reply = System.Text.Json.Nodes.JsonNode.Parse(read!.ReadLine() ?? throw new IOException("the app closed the line"))!;
            return reply["result"] ?? throw new InvalidOperationException($"{method}: {reply["error"]?["message"]}");
        }

        var wall = Stopwatch.StartNew();
        string said = "", pressedFor = "", movedFor = "";
        double settledAt = 0;
        bool started = false, pickedUp = false, nodded = false;
        int exit = 2;
        try
        {
            while (!cts.IsCancellationRequested && wall.Elapsed.TotalMinutes < 6)
            {
                cts.Token.WaitHandle.WaitOne(100);
                if (send is null)
                {
                    try { api.Connect(200); } catch (TimeoutException) { continue; }
                    send = new StreamWriter(api) { AutoFlush = true };
                    read = new StreamReader(api);
                }
                System.Text.Json.Nodes.JsonNode s = Call("calibration.status");
                bool active = s["active"]!.GetValue<bool>();
                if (!active && !started)
                {
                    if (s["pose"]?["pitchDeg"] is null) continue;
                    Call("calibration.start", Option(args, "--from"));
                    started = true;
                    continue;
                }
                if (!active)
                {
                    Console.WriteLine("Setup closed. What it said:");
                    foreach (var line in s["results"]!.AsArray()) Console.WriteLine("  " + line);
                    System.Text.Json.Nodes.JsonNode v = s["view"]!;
                    float gotFov = v["fovDeg"]!.GetValue<float>(), gotLevel = v["levelDeg"]!.GetValue<float>(), gotLean = v["leanDeg"]!.GetValue<float>();
                    Console.WriteLine($"Saved view angle {gotFov:F2} (true {fov:F2}, off {gotFov - fov:F2}), level {gotLevel:F2} (true {level:F2}, off {gotLevel - level:F2}), lean {gotLean:F2} (true {lean:F2}, off {gotLean - lean:F2}).");
                    exit = 0;
                    break;
                }

                string step = s["step"]!.GetValue<string>(), waiting = s["waiting"]!.GetValue<string>();
                string progress = s["progress"]!.GetValue<string>(), note = s["note"]?.GetValue<string>() ?? "";
                string now = $"{progress}: {s["instruction"]}{(note.Length > 0 ? "  [" + note + "]" : "")}";
                if (now != said) Console.WriteLine($"{wall.Elapsed.TotalSeconds,6:F1} s  {now}");
                said = now;
                double t = wall.Elapsed.TotalSeconds;
                bool still = Volatile.Read(ref moving) == 0;

                if (step == "mode") { Call("calibration.press"); continue; } // the box stayed in front: Follow
                if (step == "done") { Call("calibration.press"); continue; }
                switch (waiting)
                {
                    case "DeskStill":
                        break; // lying on the desk: nothing moves
                    case "Hold" when progress.EndsWith("1 of 2") && !pickedUp:
                        MoveTo(0, 20, 0, 400); // picked up and put on
                        MoveTo(15, -6, 4, 400);
                        MoveTo(0, 0, 0, 400);
                        pickedUp = true;
                        break;
                    case "Hold" when progress.EndsWith("2 of 2") && !nodded:
                        MoveTo(0, 30, 0, 600); // the nod, chin up, and hold
                        nodded = true;
                        break;
                    case "Neutral" or "Hold" when head != (0, 0, 0) && still && !progress.EndsWith("2 of 2"):
                        MoveTo(0, 0, 0, 400); // straight ahead and level
                        break;
                    case "Press" when s["target"] is { } target:
                    {
                        var t0 = new EdgeTarget(Enum.Parse<HeadPosture>(target["posture"]!.GetValue<string>()), target["xPx"]!.GetValue<float>(), target["drawnTiltDeg"]!.GetValue<float>());
                        if (movedFor != progress || (pressedFor == progress && note.Length > 0))
                        {
                            (float y, float p, float r) = PoseFor(t0);
                            MoveTo(y, p, r);
                            movedFor = progress;
                            pressedFor = "";
                            settledAt = double.MaxValue;
                        }
                        else if (still && settledAt == double.MaxValue) settledAt = t;
                        else if (still && pressedFor != progress && t - settledAt > 2.5)
                        {
                            Call("calibration.press"); // the line lies on the edge: press
                            pressedFor = progress;
                        }
                        break;
                    }
                    case "Nothing":
                        Console.WriteLine("Setup refused a step; stopping here.");
                        Call("calibration.cancel");
                        return 2;
                }
            }
        }
        catch (Exception e) when (e is IOException or InvalidOperationException) { Console.WriteLine($"Lost the app: {e.Message}"); }
        finally
        {
            cts.Cancel();
            listener.Stop();
        }
        return exit;
    }

    static void Assert(bool ok)
    {
        if (!ok) throw new InvalidOperationException("A fixed value did not hold.");
    }

    // ---- axes ---------------------------------------------------------------------------

    static int Axes()
    {
        using CancellationTokenSource cts = MakeCts(0);
        bool hadBias = TrackingConfig.TryLoadGyroBias(out Vector3 bias);
        var check = new AxisCheck(hadBias ? bias : null);
        var conn = NewConnection();
        conn.Sample += check.Add;

        using FileStream? raw = OpenRaw("axes-last.bin");
        conn.RawTee = raw;
        Thread thread = conn.Start(cts.Token);

        int result = WaitForData(conn, cts.Token) ? RunAxes(check, hadBias, save: true, cts.Token) : 2;
        cts.Cancel();
        thread.Join(5000);
        return result;
    }

    /// <summary>Talks the wearer through an <see cref="AxisCheck"/> that is being fed elsewhere, and saves what it found.</summary>
    static int RunAxes(AxisCheck check, bool hadBias, bool save, CancellationToken ct)
    {
        Console.WriteLine("Axis check: two steps, about 10 seconds. Wear the glasses and sit normally.");
        Console.WriteLine("Each step ends with a beep; there is no countdown.");
        Console.WriteLine();
        Console.WriteLine($"1/2  {AxisCheck.InstructionFor(AxisCheckStage.Level)}");
        if (!WaitWhile(check, AxisCheckStage.Level, ct)) return 1;
        if (check.LevelReading is not { } level)
        {
            Console.WriteLine($"     {check.Failure}");
            return 2;
        }
        Beep();
        Console.WriteLine("     Got it.");

        Console.WriteLine();
        Console.WriteLine($"2/2  {AxisCheck.InstructionFor(AxisCheckStage.LookUp)}");
        if (!WaitWhile(check, AxisCheckStage.LookUp, ct)) return 1;
        if (check.LookUpReading is null)
        {
            Console.WriteLine($"     {check.Failure}");
            return 2;
        }
        Beep();
        Console.WriteLine("     Got it. Relax.");
        Console.WriteLine();

        if (check.Result is not { } r)
        {
            Console.WriteLine(check.Failure);
            return 2;
        }
        return ReportAxes(r, hadBias, save) ? 0 : 2;
    }

    /// <summary>Prints what an axis check found and, if it is good and <paramref name="save"/>, saves it. False if refused.</summary>
    static bool ReportAxes(AxisCheckResult r, bool hadBias, bool save)
    {
        HoldReading level = r.Level;
        Console.WriteLine($"Gyro while holding still reads {Deg(level.GyroMean)} deg/s");
        Console.WriteLine($"Level:      gravity {level.AccelMean.Length():F2} m/s2 along {Vec(Vector3.Normalize(level.AccelMean))}, steady within {level.TiltSpreadDeg:F1} deg");
        Console.WriteLine($"Looking up: {r.PitchDeg:F0} deg (gyro says {r.GyroPitchDeg:F0}), steady within {r.LookingUp.TiltSpreadDeg:F1} deg, head turned {MathF.Abs(r.TurnDeg):F0} deg {(r.TurnDeg >= 0 ? "left" : "right")}");
        Console.WriteLine($"Side axis:  {Vec(r.Map.Right)}, {r.SideDeg:F1} deg off sensor X, gyro agrees within {r.AgreeDeg:F0} deg");

        if (r.Problem is not null)
        {
            Console.WriteLine(r.Problem);
            return false;
        }

        Console.WriteLine($"Axes: {r.Map}");
        if (!save)
        {
            Console.WriteLine("Replaying a recording: nothing saved.");
            return true;
        }
        TrackingConfig.SaveAxes(r.Map);
        Console.WriteLine($"Saved to {TrackingConfig.AxesPath}");

        if (!hadBias)
        {
            TrackingConfig.SaveGyroBias(level.GyroMean);
            Console.WriteLine("Gyro offset saved from the worn reading. 'xrprobe rest' on a desk gives a more exact one.");
        }
        return true;
    }

    // ---- setup --------------------------------------------------------------------------

    /// <summary>
    /// The whole glasses setup in the console, or replayed from a recording. Steps that need lines drawn in the
    /// glasses are skipped and named; the app runs those.
    /// </summary>
    static int Setup(string? path, string? from)
    {
        SetupStepKind? start = from?.ToLowerInvariant() switch
        {
            null or "rest" => SetupStepKind.Rest,
            "axes" => SetupStepKind.Axes,
            "level" or "lean" => SetupStepKind.LevelLean,
            "view" => SetupStepKind.ViewAngle,
            _ => null,
        };
        if (start is null) return Usage();

        bool hadBias = TrackingConfig.TryLoadGyroBias(out Vector3 saved);
        var setup = new GlassesSetup(new SetupOptions(TrackingConfig.LoadAxes(), hadBias ? saved : null, StartAt: start.Value));
        var talk = new SetupTalk(setup, hadBias, save: path is null);

        if (path is not null)
        {
            var samples = new List<ImuSample>();
            new ImuFrameParser().Feed(File.ReadAllBytes(path), samples.Add);
            Console.WriteLine($"Replaying {samples.Count} samples from {path}. Nothing is saved.");
            bool going = talk.Report();
            foreach (ImuSample s in samples)
            {
                if (!going) break;
                setup.Add(s);
                going = talk.Report();
            }
            if (going) Console.WriteLine("The recording ended before setup did.");
            return talk.ExitCode;
        }

        using CancellationTokenSource cts = MakeCts(0);
        var gate = new object();
        var conn = NewConnection();
        conn.Sample += s => { lock (gate) setup.Add(s); };
        using FileStream? raw = OpenRaw("setup-last.bin");
        conn.RawTee = raw;
        Thread thread = conn.Start(cts.Token);

        if (WaitForData(conn, cts.Token))
        {
            long count = -1;
            var quiet = Stopwatch.StartNew();
            while (!cts.IsCancellationRequested)
            {
                lock (gate) if (!talk.Report()) break;
                if (conn.Parser.SamplesParsed != count) { count = conn.Parser.SamplesParsed; quiet.Restart(); }
                else if (quiet.Elapsed.TotalSeconds > 5) { Console.WriteLine("Lost the glasses. Run 'xrprobe live' to check the link."); break; }
                cts.Token.WaitHandle.WaitOne(50);
            }
        }
        cts.Cancel();
        thread.Join(5000);
        return cts.IsCancellationRequested && !setup.Finished ? 1 : talk.ExitCode;
    }

    /// <summary>Says what a <see cref="GlassesSetup"/> wants each time that changes, beeps when a hold is taken, and saves results.</summary>
    sealed class SetupTalk(GlassesSetup setup, bool hadBias, bool save)
    {
        string? _said;
        SetupStepKind _step = (SetupStepKind)(-1);
        bool _over;

        public int ExitCode { get; private set; } = 2;

        /// <summary>False once setup has finished or stopped on a failure.</summary>
        public bool Report()
        {
            while (!_over)
            {
                if (_step != setup.Step || setup.Finished) StepChanged();
                if (_over) break;

                if (GlassesSetup.NeedsDrawing(setup.Step) && setup.StatusOf(setup.Step) == SetupStepStatus.Running)
                {
                    Console.WriteLine();
                    Console.WriteLine($"Skipped {Name(setup.Step)}: it needs lines drawn in the glasses, so the app runs it.");
                    setup.Skip();
                    continue;
                }

                if (setup.Waiting == SetupWait.Nothing)
                {
                    string failure = setup.FailureOf(setup.Step) ?? "";
                    if (setup.Step == SetupStepKind.Axes && setup.AxisAttempt is { } r)
                        ReportAxes(r, true, false); // prints the numbers and the problem
                    else
                        Console.WriteLine($"     {failure}");
                    if (setup.Step == SetupStepKind.Rest)
                    {
                        Console.WriteLine("     Going on without it: the axis check will take the offset from the worn reading.");
                        setup.Skip();
                        continue;
                    }
                    Console.WriteLine($"Stopped. To try again from here: xrprobe setup --from {Flag(setup.Step)}");
                    _over = true;
                    break;
                }

                string say = setup.Instruction;
                if (say == _said) return true;
                if (_said is not null)
                {
                    if (_saidWait == SetupWait.Neutral) Console.WriteLine("     Level and still.");
                    else
                    {
                        Beep(); // the axis check's level hold was taken
                        Console.WriteLine("     Got it.");
                    }
                }
                Console.WriteLine();
                Console.WriteLine($"{(int)setup.Step + 1}/4  {say}");
                _said = say;
                _saidWait = setup.Waiting;
                return true;
            }
            return false;
        }

        SetupWait _saidWait;

        void StepChanged()
        {
            if ((int)_step >= 0 && setup.StatusOf(_step) == SetupStepStatus.Done) Finished(_step);
            _said = null;
            if (setup.Finished)
            {
                Console.WriteLine();
                foreach (SetupStepKind k in Enum.GetValues<SetupStepKind>())
                    Console.WriteLine($"  {Name(k),-15} {setup.StatusOf(k).ToString().ToLowerInvariant()}");
                ExitCode = 0; // a failed axis check stops before here; a failed rest is made up for by the worn reading
                _over = true;
                return;
            }
            _step = setup.Step;
        }

        void Finished(SetupStepKind step)
        {
            Beep();
            SetupResults r = setup.Results;
            switch (step)
            {
                case SetupStepKind.Rest when r.GyroBias is { } bias:
                    Console.WriteLine($"     Got it. Gyro at rest reads {Deg(bias)} deg/s");
                    if (save)
                    {
                        TrackingConfig.SaveGyroBias(bias);
                        Console.WriteLine($"     Saved to {TrackingConfig.GyroPath}");
                    }
                    hadBias = true;
                    break;
                case SetupStepKind.Axes when r.Axes is { } axes:
                    Console.WriteLine("     Got it. Relax.");
                    Console.WriteLine();
                    ReportAxes(axes, hadBias, save);
                    break;
            }
        }

        static string Name(SetupStepKind k) => k switch
        {
            SetupStepKind.Rest => "gyro offset",
            SetupStepKind.Axes => "sensor axes",
            SetupStepKind.LevelLean => "level and lean",
            _ => "view angle",
        };

        static string Flag(SetupStepKind k) => k switch
        {
            SetupStepKind.Rest => "rest",
            SetupStepKind.Axes => "axes",
            SetupStepKind.LevelLean => "level",
            _ => "view",
        };
    }

    /// <summary>
    /// Waits for the check to leave a stage. False if cancelled. The check times out by the samples' clock;
    /// this also gives up by the wall clock, in case the samples stop.
    /// </summary>
    static bool WaitWhile(AxisCheck check, AxisCheckStage stage, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        while (check.Stage == stage)
        {
            if (ct.IsCancellationRequested) return false;
            if (clock.Elapsed.TotalSeconds > AxisCheck.StepSeconds + 3) check.TimeOut();
            ct.WaitHandle.WaitOne(50);
        }
        return true;
    }

    // ---- rest ---------------------------------------------------------------------------

    static int Rest(double timeoutSeconds)
    {
        using CancellationTokenSource cts = MakeCts(0);
        var hold = new HoldDetector(5f) { MaxTiltSpreadDeg = 0.3f, MaxGyroRate = 0.035f, MaxGyroSpread = 0.007f };
        var conn = NewConnection();
        conn.Sample += hold.Add;

        using FileStream? raw = OpenRaw("rest-last.bin");
        conn.RawTee = raw;
        Thread thread = conn.Start(cts.Token);

        int result = RunRest(conn, hold, timeoutSeconds, cts.Token);
        cts.Cancel();
        thread.Join(5000);
        return result;
    }

    static int RunRest(GlassesConnection conn, HoldDetector hold, double timeoutSeconds, CancellationToken ct)
    {
        if (!WaitForData(conn, ct)) return 2;

        Console.WriteLine("Rest check. Take the glasses off, set them on the desk, and leave them and the desk alone.");
        Console.WriteLine($"Waiting up to {timeoutSeconds:F0} s for 5 still seconds...");
        hold.Reset();
        bool got = WaitForHold(hold, _ => true, timeoutSeconds, ct, out HoldReading rest, out HoldReading? quietest);
        if (ct.IsCancellationRequested) return 1;

        if (!got)
        {
            if (quietest is { } q)
            {
                Console.WriteLine("Never still enough. Quietest 5 seconds:");
                Console.WriteLine($"  100 ms gyro averages varied by {q.GyroSpread * RadToDeg:F3} deg/s (limit {hold.MaxGyroSpread * RadToDeg:F2})");
                Console.WriteLine($"  fastest 100 ms average {q.GyroPeak * RadToDeg:F2} deg/s (limit {hold.MaxGyroRate * RadToDeg:F1})");
                Console.WriteLine($"  tilt wandered {q.TiltSpreadDeg:F2} deg (limit {hold.MaxTiltSpreadDeg:F1})");
            }
            Console.WriteLine("Nothing saved.");
            return 2;
        }

        Beep();
        Console.WriteLine($"Gyro at rest reads {Deg(rest.GyroMean)} deg/s");
        Console.WriteLine($"Steadiness: 100 ms averages within {rest.GyroSpread * RadToDeg:F3} deg/s, sample noise {rest.GyroNoise * RadToDeg:F3} deg/s rms, tilt within {rest.TiltSpreadDeg:F2} deg");
        Console.WriteLine($"Gravity reads {rest.AccelMean.Length():F3} m/s2 along {Vec(Vector3.Normalize(rest.AccelMean))}");
        TrackingConfig.SaveGyroBias(rest.GyroMean);
        Console.WriteLine($"Saved to {TrackingConfig.GyroPath}");
        return 0;
    }

    // ---- shared by axes and rest --------------------------------------------------------

    static bool WaitForData(GlassesConnection conn, CancellationToken ct)
    {
        Console.WriteLine("Waiting for the glasses...");
        var wait = Stopwatch.StartNew();
        while (conn.Parser.SamplesParsed < 200)
        {
            if (ct.IsCancellationRequested) return false;
            if (wait.Elapsed.TotalSeconds > 8)
            {
                Console.WriteLine("No head-motion data. Run 'xrprobe live' first to check the link.");
                return false;
            }
            Thread.Sleep(100);
        }
        return true;
    }

    /// <summary>Waits until the glasses are held steady in a position that <paramref name="accept"/> likes.</summary>
    static bool WaitForHold(HoldDetector detector, Func<HoldReading, bool> accept, double timeoutSeconds,
        CancellationToken ct, out HoldReading reading, out HoldReading? quietest)
    {
        reading = default;
        quietest = null;
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < timeoutSeconds && !ct.IsCancellationRequested)
        {
            ct.WaitHandle.WaitOne(50);
            if (!detector.TryRead(out HoldReading r)) continue;
            if (quietest is not { } q || r.GyroSpread < q.GyroSpread) quietest = r;
            if (detector.IsHold(r) && accept(r))
            {
                reading = r;
                return true;
            }
        }
        return false;
    }

    /// <summary>Keeps the raw stream of the last run next to the settings, for looking into a bad result.</summary>
    static FileStream? OpenRaw(string name)
    {
        try
        {
            Directory.CreateDirectory(TrackingConfig.Folder);
            return File.Create(Path.Combine(TrackingConfig.Folder, name));
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    static void Beep()
    {
        try { Console.Beep(); } catch (PlatformNotSupportedException) { }
    }

    // ---- helpers ------------------------------------------------------------------------

    static CancellationTokenSource MakeCts(double seconds)
    {
        var cts = new CancellationTokenSource();
        if (seconds > 0) cts.CancelAfter(TimeSpan.FromSeconds(seconds));
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        };
        return cts;
    }

    static double Seconds(string[] args, double fallback)
    {
        int i = Array.IndexOf(args, "--seconds");
        return i >= 0 && i + 1 < args.Length && double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            ? v
            : fallback;
    }

    /// <summary>First argument after the command that is not an option or an option's value.</summary>
    static string? Positional(string[] args)
    {
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal)) { i++; continue; }
            return args[i];
        }
        return null;
    }

    static string? Option(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    static GlassesConnection NewConnection() => new() { Host = s_host, Port = s_port };

    static void Tally(Dictionary<string, int> counts, string key) =>
        counts[key] = counts.GetValueOrDefault(key) + 1;

    static void PrintTop(string label, Dictionary<string, int> counts)
    {
        if (counts.Count == 0) return;
        IEnumerable<string> top = counts.OrderByDescending(kv => kv.Value).Take(5).Select(kv => $"{kv.Key} x{kv.Value}");
        Console.WriteLine($"{label}: {string.Join(", ", top)}");
    }

    static void HexDump(byte[] data, int limit)
    {
        for (int i = 0; i < Math.Min(limit, data.Length); i += 16)
            Console.WriteLine($"  {i:x4}  {Convert.ToHexString(data, i, Math.Min(16, data.Length - i))}");
    }

    static string Deg(Vector3 radians) =>
        $"{radians.X * RadToDeg,7:F3} {radians.Y * RadToDeg,7:F3} {radians.Z * RadToDeg,7:F3}";

    static string Vec(Vector3 v) => $"({v.X:F2}, {v.Y:F2}, {v.Z:F2})";
}
