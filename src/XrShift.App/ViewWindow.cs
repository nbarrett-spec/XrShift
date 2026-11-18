using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace XrShift.App;

/// <summary>The window the scene is shown in: borderless over a whole monitor, or a plain window for trying things out.</summary>
internal sealed class ViewWindow : Form
{
    const int WmHotKey = 0x0312;
    const uint ModAlt = 0x0001, ModControl = 0x0002, ModNoRepeat = 0x4000;

    readonly bool _fullScreen;
    readonly List<int> _hotKeys = [];

    /// <summary>A Ctrl+Alt hotkey added with <see cref="AddHotKey"/> was pressed, in any program.</summary>
    public event Action<Keys>? HotKey;

    public ViewWindow(Rectangle area, bool fullScreen)
    {
        _fullScreen = fullScreen;
        Text = "XrShift";
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        KeyPreview = true;
        if (fullScreen)
        {
            FormBorderStyle = FormBorderStyle.None;
            Bounds = area;
            TopMost = true; // or the taskbar on that monitor comes back over it once another program has focus
        }
        else
        {
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            Location = area.Location;
            ClientSize = area.Size;
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e) { } // Direct3D owns every pixel

    // Over the glasses the scene is a picture, not a program to type into: it must never take the keyboard.
    // When it did (the settings window was hidden and it was next in line), the whole PC turned sluggish
    // until another window was brought forward; most likely Windows treats a full-screen 3D program in
    // front as a game and puts everything else second.
    protected override bool ShowWithoutActivation => _fullScreen;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams p = base.CreateParams;
            if (_fullScreen) p.ExStyle |= 0x08000000 /* never activated */ | 0x00000080 /* not in the taskbar or Alt+Tab */;
            return p;
        }
    }

    /// <summary>Makes Ctrl+Alt+<paramref name="key"/> reach this window from anywhere. False if another program already has it.</summary>
    public bool AddHotKey(Keys key)
    {
        int id = (int)key;
        if (!RegisterHotKey(Handle, id, ModControl | ModAlt | ModNoRepeat, (uint)key)) return false;
        _hotKeys.Add(id);
        return true;
    }

    /// <summary>Keeps the mouse pointer inside a desktop area, or frees it with null. Windows forgets this now and then, so call it regularly.</summary>
    public static void ConfineCursor(Rectangle? area)
    {
        if (area is { } a)
        {
            var rect = new RECT { Left = a.Left, Top = a.Top, Right = a.Right, Bottom = a.Bottom };
            ClipCursor(ref rect);
        }
        else
        {
            ClipCursor(IntPtr.Zero);
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotKey) HotKey?.Invoke((Keys)(int)m.WParam);
        base.WndProc(ref m);
    }

    // The pointer is deliberately not hidden here. Windows hides a pointer per program, not per
    // window, so hiding it for the scene also made it vanish over the settings window.

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        foreach (int id in _hotKeys) UnregisterHotKey(Handle, id);
        _hotKeys.Clear();
        base.OnFormClosed(e);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    static extern bool UnregisterHotKey(IntPtr window, int id);

    [DllImport("user32.dll")]
    static extern bool ClipCursor(ref RECT area);

    [DllImport("user32.dll")]
    static extern bool ClipCursor(IntPtr none);
}

/// <summary>
/// Owns the link between the renderer and a window, and the thread that draws a frame for
/// every screen refresh. It also measures how old each frame's head pose is by the time the
/// screen starts showing it, and keeps that as small as it safely can.
/// </summary>
internal sealed class Presenter : IDisposable
{
    readonly Renderer _renderer;
    readonly IDXGISwapChain1 _swapChain = null!;
    readonly ID3D11Texture2D _backBuffer = null!;
    readonly DirectDisplay? _direct;
    long _worstWait, _worstDraw, _worstShow;

    /// <summary>
    /// The longest waits since the last call, in ms: for Windows to take a frame, drawing it (including the
    /// screen copies), and handing it over. Says whether a slow patch is the app or Windows.
    /// </summary>
    public string TakeWorst()
    {
        long w = Interlocked.Exchange(ref _worstWait, 0), d = Interlocked.Exchange(ref _worstDraw, 0), s = Interlocked.Exchange(ref _worstShow, 0);
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"worst wait {TicksToMs(w):F0} ms, draw {TicksToMs(d):F1} ms, hand-over {TicksToMs(s):F0} ms");
    }

    static void Worst(ref long field, long ticks)
    {
        long seen;
        while (ticks > (seen = Interlocked.Read(ref field)) && Interlocked.CompareExchange(ref field, ticks, seen) != seen) { }
    }

    /// <summary>Direct mode: frames go straight to the glasses, never through the Windows compositor.</summary>
    public Presenter(Renderer renderer, DirectDisplay direct)
    {
        _renderer = renderer;
        _direct = direct;
        RefreshMs = 1000.0 / direct.RefreshHz;
    }
    readonly IntPtr _frameReady, _timer;
    readonly (uint Id, long PoseTicks)[] _sent = new (uint, long)[64];
    Thread? _thread;
    volatile bool _running;
    long _frames, _drawTicks, _slowFrames, _timed;
    long _lastSyncTicks;
    uint _lastCounted;
    double _poseToScreenMs, _poseToScreenSum, _poseToScreenMin = double.MaxValue, _poseToScreenMax;

    public long Frames => Interlocked.Read(ref _frames);
    /// <summary>Average time spent drawing a frame, milliseconds.</summary>
    public double DrawMs => TicksToMs(Interlocked.Read(ref _drawTicks)) / Math.Max(Frames, 1);
    /// <summary>Frames that took longer than 1.5 screen refreshes from one to the next.</summary>
    public long SlowFrames => Interlocked.Read(ref _slowFrames);
    /// <summary>The screen's refresh interval. 0 = unknown: no slow-frame count and no late pose reading.</summary>
    public double RefreshMs { get; set; }
    /// <summary>
    /// If above 0, the head pose is read this long before the screen refresh the frame is aimed at,
    /// instead of as soon as Windows is ready for a frame. That makes the picture fresher, but frames
    /// start missing their refresh, and a missed frame is a visible hitch. Measured on the glasses at
    /// 120 Hz: off = 8.3 ms pose-to-screen with 0.3% of frames late; 6 ms = 6.2 ms with 1% late;
    /// 4 ms = 4.4 ms with 4.5% late; 3 ms = 4.0 ms with 11% late. Off by default: prediction covers a
    /// steady delay well, and nothing covers a hitch.
    /// </summary>
    public double LeadMs { get; set; }

    /// <summary>From reading the head pose to the screen starting to show that frame, milliseconds, recent average. Read on the drawing thread.</summary>
    public double PoseToScreenMs => _poseToScreenMs > 0 ? _poseToScreenMs : (RefreshMs > 0 ? RefreshMs : 8.3);
    /// <summary>The same over the whole run: average, least, most, and how many frames were timed.</summary>
    public (double Average, double Min, double Max, long Count) PoseToScreen
    {
        get
        {
            long n = Interlocked.Read(ref _timed);
            return n == 0 ? (0, 0, 0, 0) : (_poseToScreenSum / n, _poseToScreenMin, _poseToScreenMax, n);
        }
    }
    /// <summary>Frames drawn per second over the last second or so.</summary>
    public double FramesPerSecond { get; private set; }
    long _rateFrames, _rateTicks;

    /// <summary>Why drawing stopped, if it stopped by itself.</summary>
    public Exception? Failure { get; private set; }
    /// <summary>While true nothing is drawn (the window is hidden for game mode).</summary>
    public volatile bool Paused;

    static double TicksToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
    static long MsToTicks(double ms) => (long)(ms * Stopwatch.Frequency / 1000.0);

    public Presenter(Renderer renderer, IntPtr window)
    {
        _renderer = renderer;

        using IDXGIDevice1 dxgiDevice = renderer.Device.QueryInterface<IDXGIDevice1>();
        using IDXGIAdapter adapter = dxgiDevice.GetAdapter();
        using IDXGIFactory2 factory = adapter.GetParent<IDXGIFactory2>();

        // Flip presentation with a "frame ready" signal: Windows says when it can take a new frame.
        var description = new SwapChainDescription1((uint)renderer.Width, (uint)renderer.Height, Format.B8G8R8A8_UNorm,
            flags: SwapChainFlags.FrameLatencyWaitableObject);
        _swapChain = factory.CreateSwapChainForHwnd(renderer.Device, window, description);
        factory.MakeWindowAssociation(window, WindowAssociationFlags.IgnoreAltEnter);

        using (IDXGISwapChain2 chain = _swapChain.QueryInterface<IDXGISwapChain2>())
        {
            chain.MaximumFrameLatency = 1;
            _frameReady = chain.FrameLatencyWaitableObject;
        }
        _backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);

        // A timer that can wake to a fraction of a millisecond (Windows 10 1803 and later).
        _timer = CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero, 0x00000002 /* high resolution */, 0x1F0003);
    }

    /// <summary>Starts drawing. <paramref name="drawFrame"/> runs on the drawing thread once per refresh and should read the head pose itself.</summary>
    public void Start(Action drawFrame, Action<Exception> stopped)
    {
        _running = true;
        if (_direct is not null)
        {
            StartDirect(drawFrame, stopped);
            return;
        }
        _thread = new Thread(() =>
        {
            try
            {
                long last = Stopwatch.GetTimestamp();
                while (_running)
                {
                    if (Paused)
                    {
                        Thread.Sleep(50);
                        last = Stopwatch.GetTimestamp();
                        continue;
                    }
                    long waitFrom = Stopwatch.GetTimestamp();
                    WaitForSingleObject(_frameReady, 100);
                    WaitForLateStart();

                    long start = Stopwatch.GetTimestamp();
                    drawFrame();
                    _renderer.ResolveTo(_backBuffer);
                    long drawn = Stopwatch.GetTimestamp();
                    _swapChain.Present(1).CheckError();
                    long done = Stopwatch.GetTimestamp();
                    Worst(ref _worstWait, start - waitFrom);
                    Worst(ref _worstDraw, drawn - start);
                    Worst(ref _worstShow, done - drawn);

                    NoteSent(start);
                    Interlocked.Add(ref _drawTicks, drawn - start);
                    if (RefreshMs > 0 && Frames > 30 && TicksToMs(done - last) > RefreshMs * 1.5)
                        Interlocked.Increment(ref _slowFrames);
                    last = done;
                    Interlocked.Increment(ref _frames);
                    if (_rateTicks == 0) _rateTicks = done;
                    if (++_rateFrames >= 30 && done - _rateTicks >= Stopwatch.Frequency / 2)
                    {
                        FramesPerSecond = _rateFrames * (double)Stopwatch.Frequency / (done - _rateTicks);
                        _rateFrames = 0;
                        _rateTicks = done;
                    }
                }
            }
            catch (Exception e)
            {
                Failure = e;
                if (_running) stopped(e);
            }
        })
        { IsBackground = true, Name = "XrShift draw", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    void StartDirect(Action drawFrame, Action<Exception> stopped)
    {
        DirectDisplay direct = _direct!;
        _thread = new Thread(() =>
        {
            try
            {
                direct.Attach(_renderer.Device);
                long last = Stopwatch.GetTimestamp(), shownStart = 0;
                while (_running)
                {
                    if (Paused)
                    {
                        Thread.Sleep(50);
                        last = Stopwatch.GetTimestamp();
                        shownStart = 0;
                        continue;
                    }
                    long waitFrom = Stopwatch.GetTimestamp();
                    direct.WaitForRefresh();
                    long start = Stopwatch.GetTimestamp();
                    _lastSyncTicks = start;
                    // The frame begun after the previous refresh is on the glasses from this one.
                    if (shownStart != 0)
                    {
                        double ms = TicksToMs(start - shownStart);
                        if (ms > 0 && ms < 100)
                        {
                            _poseToScreenMs = _poseToScreenMs <= 0 ? ms : _poseToScreenMs + (ms - _poseToScreenMs) * 0.1;
                            _poseToScreenSum += ms;
                            _poseToScreenMin = Math.Min(_poseToScreenMin, ms);
                            _poseToScreenMax = Math.Max(_poseToScreenMax, ms);
                            Interlocked.Increment(ref _timed);
                        }
                    }
                    shownStart = start;

                    drawFrame();
                    long drawn = Stopwatch.GetTimestamp();
                    direct.Show(_renderer);
                    long done = Stopwatch.GetTimestamp();
                    Worst(ref _worstWait, start - waitFrom);
                    Worst(ref _worstDraw, drawn - start);
                    Worst(ref _worstShow, done - drawn);

                    Interlocked.Add(ref _drawTicks, drawn - start);
                    if (Frames > 30 && TicksToMs(start - last) > RefreshMs * 1.5) Interlocked.Increment(ref _slowFrames);
                    last = start;
                    Interlocked.Increment(ref _frames);
                    if (_rateTicks == 0) _rateTicks = done;
                    if (++_rateFrames >= 30 && done - _rateTicks >= Stopwatch.Frequency / 2)
                    {
                        FramesPerSecond = _rateFrames * (double)Stopwatch.Frequency / (done - _rateTicks);
                        _rateFrames = 0;
                        _rateTicks = done;
                    }
                }
            }
            catch (Exception e)
            {
                Failure = e;
                if (_running) stopped(e);
            }
        })
        { IsBackground = true, Name = "XrShift draw (direct)", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    /// <summary>Sleeps until shortly before the next screen refresh, so the pose that goes into the frame is as new as possible.</summary>
    void WaitForLateStart()
    {
        if (LeadMs <= 0 || RefreshMs <= 0 || _timer == IntPtr.Zero || _lastSyncTicks == 0) return;

        long now = Stopwatch.GetTimestamp();
        long refresh = MsToTicks(RefreshMs);
        long sinceSync = now - _lastSyncTicks;
        if (sinceSync < 0 || sinceSync > refresh * 30) return; // refresh times are stale; do not guess

        long next = _lastSyncTicks + (sinceSync / refresh + 1) * refresh;
        long target = next - MsToTicks(LeadMs);
        double wait = TicksToMs(target - now);
        if (wait <= 0.05 || wait > RefreshMs) return;

        if (wait > 1.0)
        {
            long due = -(long)((wait - 0.5) * 10_000); // relative, in 100 ns units
            if (SetWaitableTimer(_timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
                WaitForSingleObject(_timer, 50);
        }
        while (Stopwatch.GetTimestamp() < target) Thread.SpinWait(30);
    }

    /// <summary>Remembers when this frame's pose was read, and checks Windows' report of when earlier frames reached the screen.</summary>
    void NoteSent(long poseTicks)
    {
        uint id = _swapChain.LastPresentCount;
        _sent[id % _sent.Length] = (id, poseTicks);

        if (_swapChain.GetFrameStatistics(out FrameStatistics stats).Failure || stats.SyncQPCTime == 0) return;
        _lastSyncTicks = stats.SyncQPCTime;
        if (RefreshMs <= 0 || stats.PresentCount == _lastCounted) return;

        (uint sentId, long sentTicks) = _sent[stats.PresentCount % _sent.Length];
        if (sentId != stats.PresentCount) return;
        _lastCounted = stats.PresentCount;

        // The report is from a later refresh than the one that first showed the frame; step back to it.
        long refreshesAgo = (long)stats.SyncRefreshCount - stats.PresentRefreshCount;
        long shownTicks = stats.SyncQPCTime - MsToTicks(refreshesAgo * RefreshMs);
        double ms = TicksToMs(shownTicks - sentTicks);
        if (ms < 0 || ms > 100) return;

        _poseToScreenMs = _poseToScreenMs <= 0 ? ms : _poseToScreenMs + (ms - _poseToScreenMs) * 0.1;
        _poseToScreenSum += ms;
        _poseToScreenMin = Math.Min(_poseToScreenMin, ms);
        _poseToScreenMax = Math.Max(_poseToScreenMax, ms);
        Interlocked.Increment(ref _timed);
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(2000);
        _thread = null;
    }

    public void Dispose()
    {
        Stop();
        _backBuffer?.Dispose();
        _swapChain?.Dispose();
        if (_frameReady != IntPtr.Zero) CloseHandle(_frameReady);
        if (_timer != IntPtr.Zero) CloseHandle(_timer);
    }

    [DllImport("kernel32.dll")]
    static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, IntPtr name, uint flags, uint access);

    [DllImport("kernel32.dll")]
    static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr completion, IntPtr argument, bool resume);
}
