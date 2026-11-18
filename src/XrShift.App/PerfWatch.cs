using System.Diagnostics;
using System.Globalization;

namespace XrShift.App;

/// <summary>
/// Keeps an eye on what is loading the graphics cards while the app runs, and writes it to the log
/// when things go wrong (the glasses dropping frames, or a card pinned near 100%). It answers "what was
/// starving what" after the fact, e.g. when a game makes everything crawl.
/// </summary>
internal sealed class PerfWatch : IDisposable
{
    const int PeriodMs = 2000;

    readonly Func<(double Fps, double RefreshHz, long SlowFrames, double PoseToScreenMs)> _glasses;
    readonly Dictionary<string, string> _cards = new(StringComparer.OrdinalIgnoreCase); // luid -> short name
    readonly Dictionary<int, string> _names = [];
    readonly System.Threading.Timer _timer;
    Dictionary<string, CounterSample> _last = new(StringComparer.OrdinalIgnoreCase);
    PerformanceCounter? _cpu;
    long _lastSlow;
    int _quietTicks, _lines;
    double _worstCard;
    int _badTicks, _ticks;
    bool _busy;
    /// <summary>More to say in each line (where the time goes in the app); called on a timer thread.</summary>
    public Func<string>? Extra { get; set; }

    public PerfWatch(Func<(double Fps, double RefreshHz, long SlowFrames, double PoseToScreenMs)> glasses)
    {
        _glasses = glasses;
        foreach ((string adapter, string luid, _) in Renderer.AdaptersAndMonitors())
        {
            string name = adapter.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? "NVIDIA"
                : adapter.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "Intel"
                : adapter.Contains("AMD", StringComparison.OrdinalIgnoreCase) ? "AMD"
                : adapter.Contains("Basic Render", StringComparison.OrdinalIgnoreCase) ? "" : adapter;
            if (name.Length > 0 && !_cards.ContainsKey(luid)) _cards[luid] = name;
        }
        _timer = new System.Threading.Timer(_ => Tick(), null, PeriodMs, PeriodMs);
    }

    /// <summary>One line for the end of the run.</summary>
    public string Summary =>
        _ticks == 0 ? "Load watch: no readings."
        : string.Create(CultureInfo.InvariantCulture, $"Load watch: trouble in {_badTicks} of {_ticks} readings; busiest graphics card reading {_worstCard:F0}%.");

    void Tick()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            Read();
        }
        catch (Exception)
        {
            // Counters are a convenience. If Windows will not hand them over, carry on without.
        }
        finally
        {
            _busy = false;
        }
    }

    void Read()
    {
        InstanceDataCollection? engines = new PerformanceCounterCategory("GPU Engine").ReadCategory()["Utilization Percentage"];
        if (engines is null) return;

        var now = new Dictionary<string, CounterSample>(StringComparer.OrdinalIgnoreCase);
        var card3d = new Dictionary<string, double>();   // card -> % on its 3D engines
        var cardCopy = new Dictionary<string, double>(); // card -> % on its copy engines (moving pictures between cards)
        var byProcess = new Dictionary<(string Card, int Pid), double>();

        foreach (InstanceData engine in engines.Values)
        {
            string instance = engine.InstanceName; // pid_1234_luid_0x00000000_0x000113C3_phys_0_eng_0_engtype_3D
            now[instance] = engine.Sample;
            if (!_last.TryGetValue(instance, out CounterSample before)) continue;
            double percent = CounterSample.Calculate(before, engine.Sample);
            // Above 100 is a counter that restarted between two readings, not a load.
            if (percent < 0.05 || percent > 100.5 || !TryParse(instance, out int pid, out string luid, out string type)) continue;
            if (!_cards.TryGetValue(luid, out string? card)) continue;

            if (type.Equals("3D", StringComparison.OrdinalIgnoreCase))
            {
                card3d[card] = card3d.GetValueOrDefault(card) + percent;
                byProcess[(card, pid)] = byProcess.GetValueOrDefault((card, pid)) + percent;
            }
            else if (type.Equals("Copy", StringComparison.OrdinalIgnoreCase))
            {
                cardCopy[card] = cardCopy.GetValueOrDefault(card) + percent;
            }
        }
        bool first = _last.Count == 0;
        _last = now;
        if (first) return;

        (double fps, double refresh, long slow, double poseMs) = _glasses();
        long newSlow = slow - _lastSlow;
        _lastSlow = slow;
        double cpu = 0;
        try
        {
            _cpu ??= new PerformanceCounter("Processor Information", "% Processor Utility", "_Total");
            cpu = _cpu.NextValue();
        }
        catch (Exception) { }

        double busiest = card3d.Count > 0 ? card3d.Values.Max() : 0;
        _worstCard = Math.Max(_worstCard, busiest);
        _ticks++;
        bool dropping = refresh > 1 && fps > 1 && fps < refresh * 0.9;
        // Head-to-screen delay is reported but is not "trouble" by itself: with virtual monitors it sits at about
        // 33 ms all the time, and flagging that filled the log before a game was ever started.
        bool bad = dropping || busiest >= 90;
        if (bad) _badTicks++;

        // Say something every reading while there is trouble, otherwise once a minute, and never fill the disk.
        _quietTicks = bad ? 0 : _quietTicks + 1;
        if (!bad && _quietTicks % 30 != 1) return;
        if (++_lines > 1500) return;

        var parts = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture, $"glasses {fps:F0} of {refresh:F0} fps, {newSlow} late, head-to-screen {poseMs:F0} ms"),
        };
        foreach ((string card, double percent) in card3d.OrderByDescending(kv => kv.Value))
        {
            IEnumerable<string> top = byProcess.Where(kv => kv.Key.Card == card).OrderByDescending(kv => kv.Value).Take(3)
                .Select(kv => string.Create(CultureInfo.InvariantCulture, $"{NameOf(kv.Key.Pid)} {kv.Value:F0}%"));
            string copy = cardCopy.GetValueOrDefault(card) >= 1
                ? string.Create(CultureInfo.InvariantCulture, $", copying {cardCopy[card]:F0}%")
                : "";
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{card} {percent:F0}% ({string.Join(", ", top)}){copy}"));
        }
        parts.Add(string.Create(CultureInfo.InvariantCulture, $"processor {cpu:F0}%"));
        try { if (Extra?.Invoke() is { Length: > 0 } extra) parts.Add(extra); } catch (Exception) { }
        Log.Line($"[load {DateTime.Now:HH:mm:ss}{(bad ? " TROUBLE" : "")}] {string.Join(" | ", parts)}");
    }

    static bool TryParse(string instance, out int pid, out string luid, out string type)
    {
        pid = 0;
        luid = type = "";
        string[] p = instance.Split('_');
        // pid, <n>, luid, <hi>, <lo>, phys, <n>, eng, <n>, engtype, <type...>
        if (p.Length < 11 || p[0] != "pid" || p[2] != "luid" || !int.TryParse(p[1], out pid)) return false;
        luid = $"{p[3]}_{p[4]}";
        type = string.Join("_", p.Skip(10));
        return true;
    }

    string NameOf(int pid)
    {
        if (_names.TryGetValue(pid, out string? name)) return name;
        try { name = Process.GetProcessById(pid).ProcessName; }
        catch (Exception) { name = "pid " + pid; }
        return _names[pid] = name;
    }

    public void Dispose()
    {
        _timer.Dispose();
        _cpu?.Dispose();
    }
}
