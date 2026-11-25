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
            case "analyze" when path is not null: return Analyze(path);
            case "replay" when path is not null: return Replay(path);
            case "steady" when path is not null: return Steady(path);
            case "synth" when path is not null: return Synth(path);
            case "serve" when path is not null: return Serve(path, s_port);
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
            xrprobe synth <file>               write a fake recording (no glasses needed)
            xrprobe serve <file> [--port N]    act as the glasses on this PC, playing a recording
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

    // ---- axes ---------------------------------------------------------------------------

    /// <summary>On the One Pro+ sensor X points to the wearer's right (measured 2026-11-15). Catches a look down or a head tilt.</summary>
    static readonly Vector3 ExpectedRight = Vector3.UnitX;

    static int Axes()
    {
        using CancellationTokenSource cts = MakeCts(0);
        var hold = new HoldDetector(1.5f);
        var turned = new MotionWindow();
        var conn = NewConnection();
        conn.Sample += hold.Add;
        conn.Sample += turned.Add;

        using FileStream? raw = OpenRaw("axes-last.bin");
        conn.RawTee = raw;
        Thread thread = conn.Start(cts.Token);

        int result = RunAxes(conn, hold, turned, cts.Token);
        cts.Cancel();
        thread.Join(5000);
        return result;
    }

    static int RunAxes(GlassesConnection conn, HoldDetector hold, MotionWindow turned, CancellationToken ct)
    {
        if (!WaitForData(conn, ct)) return 2;

        Console.WriteLine("Axis check: two steps, about 10 seconds. Wear the glasses and sit normally.");
        Console.WriteLine("Each step ends with a beep; there is no countdown.");
        Console.WriteLine();
        Console.WriteLine("1/2  Look straight ahead and hold still");
        hold.Reset();
        if (!WaitForHold(hold, _ => true, 15, ct, out HoldReading level, out _))
        {
            Console.WriteLine("     Never steady for 1.5 seconds. Nothing saved; run it again.");
            return 2;
        }
        turned.Reset();
        Beep();
        Console.WriteLine("     Got it.");

        Vector3 upLevel = Vector3.Normalize(level.AccelMean);
        Console.WriteLine();
        Console.WriteLine("2/2  Nod UP about 30 degrees (chin up, no turning or tilting) and hold until the beep");
        hold.Reset();
        bool got = WaitForHold(hold, r => AngleDeg(upLevel, r.AccelMean) >= 12f, 15, ct, out HoldReading looking, out _);
        Motion between = turned.Snapshot();
        if (ct.IsCancellationRequested) return 1;
        if (!got)
        {
            Console.WriteLine("     No steady look up of at least 12 degrees seen. Nothing saved; run it again.");
            return 2;
        }
        Beep();
        Console.WriteLine("     Got it. Relax.");
        Console.WriteLine();

        bool hadBias = TrackingConfig.TryLoadGyroBias(out Vector3 bias);
        if (!hadBias) bias = level.GyroMean;

        if (!AxisMap.TryFromLevelAndLookUp(level.AccelMean, looking.AccelMean, out AxisMap map, out float pitchDeg))
        {
            Console.WriteLine("Could not tell the side axis from the two readings. Nothing saved; run it again.");
            return 2;
        }

        // The gyro saw the same nod; it should agree with what gravity says.
        Vector3 rotation = between.Rotation(bias);
        float turnDeg = Vector3.Dot(rotation, upLevel) * RadToDeg;
        Vector3 nod = rotation - Vector3.Dot(rotation, upLevel) * upLevel;
        float gyroPitchDeg = nod.Length() * RadToDeg;
        float agreeDeg = gyroPitchDeg > 3 ? AngleDeg(nod, map.Right) : float.NaN;
        float sideDeg = AngleDeg(map.Right, ExpectedRight);

        Console.WriteLine($"Gyro while holding still reads {Deg(level.GyroMean)} deg/s");
        Console.WriteLine($"Level:      gravity {level.AccelMean.Length():F2} m/s2 along {Vec(upLevel)}, steady within {level.TiltSpreadDeg:F1} deg");
        Console.WriteLine($"Looking up: {pitchDeg:F0} deg (gyro says {gyroPitchDeg:F0}), steady within {looking.TiltSpreadDeg:F1} deg, head turned {MathF.Abs(turnDeg):F0} deg {(turnDeg >= 0 ? "left" : "right")}");
        Console.WriteLine($"Side axis:  {Vec(map.Right)}, {sideDeg:F1} deg off sensor X, gyro agrees within {agreeDeg:F0} deg");

        if (sideDeg > 150)
        {
            Console.WriteLine("That read as looking DOWN, not up. Nothing saved; run it again.");
            return 2;
        }
        if (sideDeg > 20)
        {
            Console.WriteLine("The head tilted sideways during the nod. Nothing saved; run it again.");
            return 2;
        }
        if (agreeDeg > 45)
        {
            Console.WriteLine("Gyro and gravity disagree about the nod. Nothing saved; run it again.");
            return 2;
        }

        TrackingConfig.SaveAxes(map);
        Console.WriteLine($"Axes: {map}");
        Console.WriteLine($"Saved to {TrackingConfig.AxesPath}");

        if (!hadBias)
        {
            TrackingConfig.SaveGyroBias(level.GyroMean);
            Console.WriteLine("Gyro offset saved from the worn reading. 'xrprobe rest' on a desk gives a more exact one.");
        }
        return 0;
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

    static float AngleDeg(Vector3 a, Vector3 b) =>
        MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(a), Vector3.Normalize(b)), -1f, 1f)) * RadToDeg;

    /// <summary>Sums the motion seen between Reset and Snapshot.</summary>
    sealed class MotionWindow
    {
        readonly object _gate = new();
        Vector3 _gyroSum, _accelSum, _rotation;
        double _time;
        int _count;
        ulong _last;

        public void Add(ImuSample s)
        {
            lock (_gate)
            {
                double dt = _last != 0 && s.TimestampNs > _last ? (s.TimestampNs - _last) * 1e-9 : 0.001;
                if (dt > 0.05) dt = 0.001;
                _last = s.TimestampNs;
                _gyroSum += s.Gyro;
                _accelSum += s.Accel;
                _rotation += s.Gyro * (float)dt;
                _time += dt;
                _count++;
            }
        }

        public void Reset()
        {
            lock (_gate)
            {
                _gyroSum = _accelSum = _rotation = Vector3.Zero;
                _time = 0;
                _count = 0;
            }
        }

        public Motion Snapshot()
        {
            lock (_gate)
            {
                int n = Math.Max(_count, 1);
                return new Motion(_gyroSum / n, _accelSum / n, _rotation, (float)_time);
            }
        }
    }

    readonly record struct Motion(Vector3 GyroMean, Vector3 AccelMean, Vector3 RawRotation, float Seconds)
    {
        /// <summary>Net rotation (radians, as an axis times angle) after removing the resting gyro offset.</summary>
        public Vector3 Rotation(Vector3 bias) => RawRotation - bias * Seconds;
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
