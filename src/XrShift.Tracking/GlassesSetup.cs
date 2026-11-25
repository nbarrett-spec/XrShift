using System.Numerics;

namespace XrShift.Tracking;

public enum SetupStepKind { Rest, Axes, LevelLean, ViewAngle }

public enum SetupStepStatus { Pending, Running, Done, Failed, Skipped }

/// <summary>What the current step is waiting for, so the app knows whether to feed samples, listen for the key, or ask the wearer.</summary>
public enum SetupWait
{
    /// <summary>The glasses lying still on the desk (feed samples).</summary>
    DeskStill,
    /// <summary>The head back to looking straight ahead, level, and still (feed samples).</summary>
    Neutral,
    /// <summary>A held head position (feed samples).</summary>
    Hold,
    /// <summary>The key, with the head pose at that moment (<see cref="GlassesSetup.Press"/>).</summary>
    Press,
    /// <summary>Nothing: the step failed (offer <see cref="GlassesSetup.Retry"/> or <see cref="GlassesSetup.Skip"/>), or setup is over.</summary>
    Nothing,
}

/// <summary>What setup found. Null for a step that was skipped, failed or not reached.</summary>
public sealed record SetupResults(Vector3? GyroBias, AxisCheckResult? Axes, EdgeFit? LevelLean, EdgeFit? ViewAngle)
{
    // The view-angle fit includes the level-and-lean presses as well as its own looks up and down,
    // which show level far more strongly, so it gives the better level and lean when both ran.
    public float? LevelDeg => ViewAngle?.LevelDeg ?? LevelLean?.LevelDeg;
    public float? LeanDeg => ViewAngle?.LeanDeg ?? LevelLean?.LeanDeg;
    public float? FovDeg => ViewAngle?.FovDeg;
}

/// <summary>Where setup starts and what it may assume.</summary>
/// <param name="Axes">The map in use now: only for judging "head level" before the axis step has measured a better one.</param>
/// <param name="SavedGyroBias">A resting offset measured before, used if the rest step is skipped.</param>
/// <param name="FovDeg">The view angle the app assumes now.</param>
public sealed record SetupOptions(AxisMap Axes, Vector3? SavedGyroBias = null, float FovDeg = 50.7f,
    int WidthPx = 1920, int HeightPx = 1080, SetupStepKind StartAt = SetupStepKind.Rest);

/// <summary>
/// The whole glasses setup as one sequence: resting gyro offset, sensor axes, level and lean, view angle.
/// Every step says what to tell the wearer, what to draw and how far along it is, and ends done or failed with a
/// reason; a failed step waits for <see cref="Retry"/> or <see cref="Skip"/>. Before each worn step the head must come
/// back to looking straight ahead and level, so a step never starts from a pose left over from the one before.
/// <para>
/// Feed every sample with <see cref="Add"/> and each key press with <see cref="Press"/> (the tracker's
/// <see cref="HeadTracker.GetPose"/> orientation, not the display pose). After the axis step the app must put the new
/// map into its tracker before the edge steps, since their presses are read in the tracker's head axes.
/// Not thread-safe: call from one thread.
/// </para>
/// </summary>
public sealed class GlassesSetup
{
    const float RadToDeg = 180f / MathF.PI;
    /// <summary>Before the axis step has measured anything, "level" is head-up within this of gravity by the old map.</summary>
    const float RoughLevelDeg = 15f;
    /// <summary>Later, the head must come back within this of the reading that defined level.</summary>
    const float NeutralDeg = 7f;
    const float NeutralTimeoutSeconds = 30f;
    const float RestTimeoutSeconds = 40f;

    static readonly SetupStepKind[] Order = [SetupStepKind.Rest, SetupStepKind.Axes, SetupStepKind.LevelLean, SetupStepKind.ViewAngle];

    readonly SetupOptions _options;
    readonly SetupStepStatus[] _status = new SetupStepStatus[Order.Length];
    readonly string?[] _failure = new string?[Order.Length];
    int _index;
    bool _neutralDone;

    HoldDetector _detector = new();
    float _seconds;
    ulong _last;
    bool _sawMotion;
    Vector3? _neutralUp;

    AxisCheck? _axisCheck;
    EdgeCalibration? _edges;
    Vector3? _bias;
    AxisCheckResult? _axes;
    EdgeFit? _levelLean, _viewAngle;
    IReadOnlyList<EdgePress> _levelLeanPresses = [];

    public GlassesSetup(SetupOptions options)
    {
        _options = options;
        _bias = options.SavedGyroBias;
        _index = Array.IndexOf(Order, options.StartAt);
        for (int i = 0; i < _index; i++) _status[i] = SetupStepStatus.Skipped;
        Begin();
    }

    public SetupStepKind Step => Order[Math.Min(_index, Order.Length - 1)];
    public bool Finished => _index >= Order.Length;
    public SetupStepStatus StatusOf(SetupStepKind step) => _status[Array.IndexOf(Order, step)];
    public string? FailureOf(SetupStepKind step) => _failure[Array.IndexOf(Order, step)];
    public SetupResults Results => new(_bias, _axes, _levelLean, _viewAngle);
    /// <summary>The axis step's last measurement, also when it was refused, so the numbers behind a refusal can be shown.</summary>
    public AxisCheckResult? AxisAttempt => _axisCheck?.Result ?? _axes;

    /// <summary>The steps that need something drawn in the glasses. A console run can only skip them.</summary>
    public static bool NeedsDrawing(SetupStepKind step) => step is SetupStepKind.LevelLean or SetupStepKind.ViewAngle;

    public SetupWait Waiting =>
        Finished || CurrentStatus != SetupStepStatus.Running ? SetupWait.Nothing
        : Step == SetupStepKind.Rest ? SetupWait.DeskStill
        : !_neutralDone ? SetupWait.Neutral
        : Step == SetupStepKind.Axes ? SetupWait.Hold
        : SetupWait.Press;

    /// <summary>What to tell the wearer now, in plain words.</summary>
    public string Instruction => Waiting switch
    {
        SetupWait.DeskStill => "Take the glasses off, set them on the desk, and leave them and the desk alone.",
        SetupWait.Hold when AfterRest && !_sawMotion => "Put the glasses on, look straight ahead with your head level, and hold still.",
        SetupWait.Neutral => "Look straight ahead with your head level, and hold still.",
        SetupWait.Hold => AxisCheck.InstructionFor(_axisCheck!.Stage),
        SetupWait.Press => _edges!.Instruction,
        _ => Finished ? "Setup is finished." : FailureOf(Step) ?? "",
    };

    /// <summary>The line being asked for during an edge step: its place, how it is drawn, and how to hold the head.</summary>
    public EdgeTarget? Target => Waiting == SetupWait.Press ? _edges!.Current : null;

    /// <summary>Why the last key press was not taken, or null.</summary>
    public string? Note => Waiting == SetupWait.Press ? _edges!.Note : null;

    public GuideDrawing Drawing => Waiting switch
    {
        SetupWait.Neutral or SetupWait.Hold => GuideDrawing.CentreCross,
        SetupWait.Press => _edges!.Drawing,
        _ => GuideDrawing.None,
    };

    /// <summary>How far through the current step, 0 to 1.</summary>
    public float Progress => Waiting switch
    {
        SetupWait.Hold => _axisCheck!.Progress,
        SetupWait.Press => _edges!.Progress,
        _ => CurrentStatus is SetupStepStatus.Done ? 1f : 0f,
    };

    SetupStepStatus CurrentStatus => Finished ? SetupStepStatus.Done : _status[_index];

    public void Add(ImuSample s)
    {
        if (Waiting is SetupWait.Nothing or SetupWait.Press) return;

        float dt = _last != 0 && s.TimestampNs > _last ? (float)((s.TimestampNs - _last) * 1e-9) : 0.001f;
        if (dt > 0.05f) dt = 0.001f;
        _last = s.TimestampNs;
        _seconds += dt;

        if (Waiting == SetupWait.Hold)
        {
            _detector.Add(s);
            if (_detector.TryRead(out HoldReading m) && !_detector.IsHold(m)) _sawMotion = true;
            _axisCheck!.Add(s);
            if (_axisCheck.Stage == AxisCheckStage.Done)
            {
                _axes = _axisCheck.Result;
                _neutralUp = Vector3.Normalize(_axes!.Level.AccelMean); // this reading is what level now means
                Complete();
            }
            else if (_axisCheck.Stage == AxisCheckStage.Failed)
                Fail(_axisCheck.Failure!);
            return;
        }

        _detector.Add(s);
        if (!_detector.TryRead(out HoldReading r)) return;
        bool hold = _detector.IsHold(r);
        if (!hold) _sawMotion = true;

        if (Waiting == SetupWait.DeskStill)
        {
            if (hold)
            {
                _bias = r.GyroMean;
                Complete();
            }
            else if (_seconds > RestTimeoutSeconds)
                Fail("The glasses were never still for 5 seconds. Set them down on a steady desk and leave them alone.");
            return;
        }

        if (hold && IsNeutral(r))
        {
            _neutralUp ??= Vector3.Normalize(r.AccelMean);
            StartMeasuring();
        }
        else if (_seconds > NeutralTimeoutSeconds)
            Fail("Never came back to looking straight ahead with the head level and still.");
    }

    /// <summary>The key was pressed with the head at <paramref name="head"/>. False if it was not taken; <see cref="Note"/> says why.</summary>
    public bool Press(Quaternion head)
    {
        if (Waiting != SetupWait.Press) return false;
        bool taken = _edges!.Press(head);
        if (_edges.State == CalibrationState.WaitingForPress) return taken;

        if (_edges.State == CalibrationState.Failed)
        {
            Fail(_edges.Failure!);
            return taken;
        }
        if (Step == SetupStepKind.LevelLean)
        {
            _levelLean = _edges.Result;
            _levelLeanPresses = _edges.Presses;
        }
        else _viewAngle = _edges.Result;
        Complete();
        return taken;
    }

    /// <summary>Leaves the current step as it is (a failed step keeps its reason) and moves on.</summary>
    public void Skip()
    {
        if (Finished) return;
        if (_status[_index] != SetupStepStatus.Failed) _status[_index] = SetupStepStatus.Skipped;
        _index++;
        Begin();
    }

    /// <summary>Starts the current step again from the top, including the return to level.</summary>
    public void Retry()
    {
        if (Finished) return;
        Begin();
    }

    /// <summary>
    /// Straight ahead and level: within a few degrees of the reading that defined level, or before there is one,
    /// roughly level by the old map. Straight after the rest step the glasses must have been picked up first,
    /// or the desk itself would pass.
    /// </summary>
    bool IsNeutral(HoldReading r)
    {
        if (AfterRest && !_sawMotion) return false;
        Vector3 up = Vector3.Normalize(r.AccelMean);
        return _neutralUp is { } n ? AngleDeg(up, n) <= NeutralDeg : AngleDeg(up, _options.Axes.Up) <= RoughLevelDeg;
    }

    bool AfterRest => _index > 0 && Order[_index - 1] == SetupStepKind.Rest && _status[_index - 1] == SetupStepStatus.Done;

    void Begin()
    {
        if (Finished) return;
        _status[_index] = SetupStepStatus.Running;
        _failure[_index] = null;
        _neutralDone = false;
        _detector = Step == SetupStepKind.Rest
            ? new HoldDetector(5f) { MaxTiltSpreadDeg = 0.3f, MaxGyroRate = 0.035f, MaxGyroSpread = 0.007f } // as 'xrprobe rest'
            : new HoldDetector(1f);
        _seconds = 0;
        _sawMotion = false;
        _axisCheck = null;
        _edges = null;
        // The axis check opens with "look straight ahead and hold still", which only passes when neutral:
        // that is its return to level, and a separate one would just make the wearer hold twice.
        if (Step == SetupStepKind.Axes) StartMeasuring();
    }

    void StartMeasuring()
    {
        _neutralDone = true;
        _seconds = 0;
        switch (Step)
        {
            case SetupStepKind.Axes:
                _axisCheck = new AxisCheck(_bias, IsNeutral);
                break;
            case SetupStepKind.LevelLean:
                _edges = EdgeCalibration.LevelLean(_options.FovDeg, _options.WidthPx, _options.HeightPx);
                break;
            case SetupStepKind.ViewAngle:
                _edges = EdgeCalibration.ViewAngle(_options.FovDeg, _options.WidthPx, _options.HeightPx, _levelLeanPresses);
                break;
        }
    }

    void Complete()
    {
        _status[_index] = SetupStepStatus.Done;
        _index++;
        Begin();
    }

    void Fail(string reason)
    {
        _status[_index] = SetupStepStatus.Failed;
        _failure[_index] = reason;
    }

    static float AngleDeg(Vector3 a, Vector3 b) =>
        MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(a), Vector3.Normalize(b)), -1f, 1f)) * RadToDeg;
}
