using System.Numerics;

namespace XrShift.Tracking;

public enum AxisCheckStage { Level, LookUp, Done, Failed }

/// <summary>What the axis check measured. <see cref="Problem"/> is null when the map is good to save.</summary>
/// <param name="PitchDeg">How far up the head went, by gravity.</param>
/// <param name="GyroPitchDeg">The same, by the gyro.</param>
/// <param name="TurnDeg">How far the head turned left (+) or right during the nod.</param>
/// <param name="AgreeDeg">Angle between the side axis by gravity and by the gyro; NaN if the gyro saw too little.</param>
/// <param name="SideDeg">How far the side axis is from sensor X, where it is on the One Pro+.</param>
public sealed record AxisCheckResult(
    AxisMap Map, HoldReading Level, HoldReading LookingUp, Vector3 Bias,
    float PitchDeg, float GyroPitchDeg, float TurnDeg, float AgreeDeg, float SideDeg, string? Problem);

/// <summary>
/// Measures how the sensor sits in the frame: the wearer looks straight ahead and holds still, then nods up and
/// holds. Gravity in the two holds gives the map; the gyro, which saw the nod, checks it. Fed with raw samples;
/// all timing is by the samples' own clock. Thread-safe.
/// </summary>
public sealed class AxisCheck
{
    const float RadToDeg = 180f / MathF.PI;
    public const float HoldSeconds = 1.5f;
    public const float StepSeconds = 15f;
    public const float MinLookUpDeg = 12f;
    const float CheckEverySeconds = 0.05f;

    /// <summary>On the One Pro+ sensor X points to the wearer's right (measured 2026-11-15). Catches a look down or a head tilt.</summary>
    public static readonly Vector3 ExpectedRight = Vector3.UnitX;

    readonly object _gate = new();
    readonly HoldDetector _hold = new(HoldSeconds);
    readonly Vector3? _knownBias;
    readonly Func<HoldReading, bool>? _levelOk;
    Vector3 _rawRotation;
    float _motionSeconds, _stageSeconds, _sinceCheck;
    ulong _last;
    bool _heldButNotLevel;

    /// <param name="knownBias">The gyro's resting offset if measured; otherwise the level hold's reading stands in.</param>
    /// <param name="levelOk">Extra test for the first hold, e.g. that the head has come back to level.</param>
    public AxisCheck(Vector3? knownBias = null, Func<HoldReading, bool>? levelOk = null)
    {
        _knownBias = knownBias;
        _levelOk = levelOk;
    }

    public AxisCheckStage Stage { get { lock (_gate) return _stage; } }
    AxisCheckStage _stage;

    public HoldReading? LevelReading { get { lock (_gate) return _level; } }
    HoldReading? _level;

    public HoldReading? LookUpReading { get { lock (_gate) return _lookUp; } }
    HoldReading? _lookUp;

    /// <summary>Set once both holds were taken, even if the map was then refused (<see cref="AxisCheckResult.Problem"/>).</summary>
    public AxisCheckResult? Result { get { lock (_gate) return _result; } }
    AxisCheckResult? _result;

    /// <summary>Why it failed, in the words the probe has always printed.</summary>
    public string? Failure { get { lock (_gate) return _failure; } }
    string? _failure;

    public float Progress { get { lock (_gate) return _stage switch { AxisCheckStage.Level => 0f, AxisCheckStage.LookUp => 0.5f, _ => 1f }; } }

    public static string InstructionFor(AxisCheckStage stage) => stage switch
    {
        AxisCheckStage.Level => "Look straight ahead and hold still",
        AxisCheckStage.LookUp => "Nod UP about 30 degrees (chin up, no turning or tilting) and hold until the beep",
        _ => "",
    };

    public void Add(ImuSample s)
    {
        lock (_gate)
        {
            if (_stage is AxisCheckStage.Done or AxisCheckStage.Failed) return;
            float dt = _last != 0 && s.TimestampNs > _last ? (float)((s.TimestampNs - _last) * 1e-9) : 0.001f;
            if (dt > 0.05f) dt = 0.001f;
            _last = s.TimestampNs;

            _hold.Add(s);
            _rawRotation += s.Gyro * dt;
            _motionSeconds += dt;
            _stageSeconds += dt;
            _sinceCheck += dt;
            if (_sinceCheck < CheckEverySeconds) return;
            _sinceCheck = 0;

            if (_hold.TryRead(out HoldReading r) && _hold.IsHold(r))
            {
                if (_stage == AxisCheckStage.Level)
                {
                    if (_levelOk is null || _levelOk(r)) { AcceptLevel(r); return; }
                    _heldButNotLevel = true;
                }
                else if (AngleDeg(_level!.Value.AccelMean, r.AccelMean) >= MinLookUpDeg)
                {
                    AcceptLookUp(r);
                    return;
                }
            }
            if (_stageSeconds >= StepSeconds) TimeOutLocked();
        }
    }

    /// <summary>Gives up on the current step as if its time had run out, e.g. when the samples stop arriving.</summary>
    public void TimeOut()
    {
        lock (_gate) if (_stage is AxisCheckStage.Level or AxisCheckStage.LookUp) TimeOutLocked();
    }

    void TimeOutLocked()
    {
        _failure = _stage == AxisCheckStage.Level
            ? _heldButNotLevel
                ? "Held still, but not with the head level. Nothing saved; look straight ahead and run it again."
                : $"Never steady for {HoldSeconds:F1} seconds. Nothing saved; run it again."
            : $"No steady look up of at least {MinLookUpDeg:F0} degrees seen. Nothing saved; run it again.";
        _stage = AxisCheckStage.Failed;
    }

    void AcceptLevel(HoldReading r)
    {
        _level = r;
        _rawRotation = Vector3.Zero;
        _motionSeconds = 0;
        _hold.Reset();
        _stageSeconds = 0;
        _stage = AxisCheckStage.LookUp;
    }

    void AcceptLookUp(HoldReading looking)
    {
        _lookUp = looking;
        HoldReading level = _level!.Value;
        Vector3 bias = _knownBias ?? level.GyroMean;
        Vector3 upLevel = Vector3.Normalize(level.AccelMean);

        if (!AxisMap.TryFromLevelAndLookUp(level.AccelMean, looking.AccelMean, out AxisMap map, out float pitchDeg))
        {
            _failure = "Could not tell the side axis from the two readings. Nothing saved; run it again.";
            _stage = AxisCheckStage.Failed;
            return;
        }

        // The gyro saw the same nod; it should agree with what gravity says.
        Vector3 rotation = _rawRotation - bias * _motionSeconds;
        float turnDeg = Vector3.Dot(rotation, upLevel) * RadToDeg;
        Vector3 nod = rotation - Vector3.Dot(rotation, upLevel) * upLevel;
        float gyroPitchDeg = nod.Length() * RadToDeg;
        float agreeDeg = gyroPitchDeg > 3 ? AngleDeg(nod, map.Right) : float.NaN;
        float sideDeg = AngleDeg(map.Right, ExpectedRight);

        string? problem =
            sideDeg > 150 ? "That read as looking DOWN, not up. Nothing saved; run it again." :
            sideDeg > 20 ? "The head tilted sideways during the nod. Nothing saved; run it again." :
            agreeDeg > 45 ? "Gyro and gravity disagree about the nod. Nothing saved; run it again." :
            null;

        _result = new AxisCheckResult(map, level, looking, bias, pitchDeg, gyroPitchDeg, turnDeg, agreeDeg, sideDeg, problem);
        _failure = problem;
        _stage = problem is null ? AxisCheckStage.Done : AxisCheckStage.Failed;
    }

    static float AngleDeg(Vector3 a, Vector3 b) =>
        MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(a), Vector3.Normalize(b)), -1f, 1f)) * RadToDeg;
}
