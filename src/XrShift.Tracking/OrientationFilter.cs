using System.Numerics;

namespace XrShift.Tracking;

/// <summary>
/// Fuses gyro and accelerometer into an orientation. The gyro drives it; gravity from the
/// accelerometer slowly corrects tilt. There is no compass, so heading (yaw) drifts over time.
/// </summary>
public sealed class OrientationFilter
{
    const float Gravity = 9.80665f;
    const float NominalDt = 0.001f;
    const float MaxDt = 0.05f;
    const float StartupSeconds = 1.0f;
    const float StartupGain = 5f;
    const float BlockSeconds = 0.1f;
    const int BlockCount = 10;

    /// <summary>
    /// How strongly gravity pulls the tilt estimate back, per second, while the head is still.
    /// Kept weak on purpose: a moving head shakes the accelerometer, and a strong pull turns that
    /// into screens that wobble after every turn. On real worn recordings 0.5 pushed the horizon up
    /// to 0.7° in half a second; 0.1 with the fade below keeps it under 0.05°. The gyro drifts so
    /// little (under 0.1 deg/s) that a weak pull is plenty.
    /// </summary>
    public float Gain { get; set; } = 0.1f;
    /// <summary>
    /// Turn rate (rad/s) at which the pull has faded to half; it fades with the square of the rate
    /// beyond that, so gravity is only really trusted when the head is nearly still. About 10 deg/s.
    /// </summary>
    public float QuietRate { get; set; } = 0.175f;
    /// <summary>Accelerometer is ignored when its length differs from 1 g by more than this fraction.</summary>
    public float AccelTolerance { get; set; } = 0.05f;
    /// <summary>
    /// Largest spread (rad/s) between 100 ms gyro averages that still counts as "not moving".
    /// Measured on the One Pro+: 0.10-0.24 deg/s resting on a desk, about 2 deg/s on a quiet head. This is 0.4.
    /// </summary>
    public float StillGyroSpread { get; set; } = 0.007f;
    /// <summary>
    /// Largest spread (m/s²) between 100 ms accelerometer averages that still counts as "not moving".
    /// Measured: 0.02 on a desk, 0.3 or more on a quiet head.
    /// </summary>
    public float StillAccelSpread { get; set; } = 0.08f;
    /// <summary>Once a bias is known, a still reading further than this (rad/s) from it is treated as slow motion.</summary>
    public float BiasStep { get; set; } = 0.02f;

    /// <summary>Rotation from sensor axes to a gravity-aligned world (Z up, heading arbitrary).</summary>
    public Quaternion Orientation { get; private set; } = Quaternion.Identity;
    public Vector3 GyroBias { get; private set; }
    /// <summary>Latest turn rate in sensor axes with the offset removed, rad/s. Used to look a few milliseconds ahead.</summary>
    public Vector3 Rate { get; private set; }
    public bool BiasKnown { get; private set; }
    /// <summary>How many times the offset has been measured from the live stream since start.</summary>
    public int BiasUpdates { get; private set; }
    public bool IsInitialized { get; private set; }

    ulong _lastTimestamp;
    float _elapsed;

    readonly Vector3[] _gyroBlocks = new Vector3[BlockCount];
    readonly Vector3[] _accelBlocks = new Vector3[BlockCount];
    int _blocksFilled, _blockIndex, _blockSamples;
    float _blockTime;
    Vector3 _gyroSum, _accelSum;

    /// <summary>
    /// Starts from an offset measured earlier (rad/s). A worn head is rarely still enough to
    /// measure it live, so this is what keeps heading drift down from the first second.
    /// </summary>
    public void SetBias(Vector3 bias)
    {
        GyroBias = bias;
        BiasKnown = true;
    }

    public void Update(in ImuSample s)
    {
        float accelLength = s.Accel.Length();

        if (!IsInitialized)
        {
            if (accelLength < 1f) return;
            Orientation = FromTo(s.Accel / accelLength, Vector3.UnitZ);
            _lastTimestamp = s.TimestampNs;
            IsInitialized = true;
            return;
        }

        float dt = s.TimestampNs > _lastTimestamp ? (float)((s.TimestampNs - _lastTimestamp) * 1e-9) : NominalDt;
        if (dt > MaxDt) dt = NominalDt; // gap in the stream or a timestamp jump
        _lastTimestamp = s.TimestampNs;
        _elapsed += dt;

        UpdateBias(s, dt);
        Vector3 rate = s.Gyro - GyroBias;
        Rate = rate;

        if (MathF.Abs(accelLength - Gravity) < AccelTolerance * Gravity)
        {
            Vector3 measuredUp = s.Accel / accelLength;
            Vector3 estimatedUp = Vector3.Transform(Vector3.UnitZ, Quaternion.Conjugate(Orientation));
            float gain = StartupGain;
            if (_elapsed >= StartupSeconds)
            {
                float busy = rate.Length() / MathF.Max(QuietRate, 1e-6f);
                gain = Gain / (1f + busy * busy);
            }
            rate += gain * Vector3.Cross(measuredUp, estimatedUp);
        }

        float speed = rate.Length();
        if (speed * dt > 1e-9f)
        {
            Quaternion step = Quaternion.CreateFromAxisAngle(rate / speed, speed * dt);
            Orientation = Quaternion.Normalize(Orientation * step);
        }
    }

    /// <summary>Learns the gyro's resting offset whenever the glasses are held still for a second.</summary>
    void UpdateBias(in ImuSample s, float dt)
    {
        _gyroSum += s.Gyro;
        _accelSum += s.Accel;
        _blockSamples++;
        _blockTime += dt;
        if (_blockTime < BlockSeconds) return;

        _gyroBlocks[_blockIndex] = _gyroSum / _blockSamples;
        _accelBlocks[_blockIndex] = _accelSum / _blockSamples;
        _blockIndex = (_blockIndex + 1) % BlockCount;
        _blocksFilled = Math.Min(_blocksFilled + 1, BlockCount);
        _gyroSum = _accelSum = Vector3.Zero;
        _blockSamples = 0;
        _blockTime = 0;
        if (_blocksFilled < BlockCount) return;

        if (Spread(_gyroBlocks, out Vector3 gyroMean) > StillGyroSpread) return;
        if (Spread(_accelBlocks, out _) > StillAccelSpread) return;

        if (!BiasKnown)
        {
            GyroBias = gyroMean;
            BiasKnown = true;
            BiasUpdates++;
        }
        else if ((gyroMean - GyroBias).Length() < BiasStep)
        {
            GyroBias = Vector3.Lerp(GyroBias, gyroMean, 0.1f);
            BiasUpdates++;
        }
    }

    static float Spread(Vector3[] blocks, out Vector3 mean)
    {
        Vector3 min = blocks[0], max = blocks[0], sum = Vector3.Zero;
        foreach (Vector3 b in blocks)
        {
            min = Vector3.Min(min, b);
            max = Vector3.Max(max, b);
            sum += b;
        }
        mean = sum / blocks.Length;
        Vector3 d = max - min;
        return MathF.Max(d.X, MathF.Max(d.Y, d.Z));
    }

    static Quaternion FromTo(Vector3 from, Vector3 to)
    {
        float dot = Vector3.Dot(from, to);
        if (dot < -0.9999f)
        {
            Vector3 axis = Vector3.Cross(from, Vector3.UnitX);
            if (axis.LengthSquared() < 1e-6f) axis = Vector3.Cross(from, Vector3.UnitY);
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
        }
        Vector3 c = Vector3.Cross(from, to);
        return Quaternion.Normalize(new Quaternion(c.X, c.Y, c.Z, 1 + dot));
    }
}
