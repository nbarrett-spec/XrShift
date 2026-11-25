using System.Numerics;

namespace XrShift.Tracking;

/// <summary>
/// Head direction. Angles in degrees: yaw + = turned left, pitch + = looking up, roll + = tilted right.
/// Orientation rotates head axes (X right, Y up, Z back) into a Y-up world where the recentred
/// forward direction is -Z.
/// </summary>
public readonly record struct HeadPose(float YawDeg, float PitchDeg, float RollDeg, Quaternion Orientation);

/// <summary>Feeds samples through the filter and reports where the wearer is looking. Thread-safe.</summary>
public sealed class HeadTracker
{
    const float RadToDeg = 180f / MathF.PI;

    const float DegToRad = MathF.PI / 180f;

    // Turn-rate smoothing for prediction: a low-pass whose cutoff rises with speed, so tremor and
    // sensor noise are calmed while a quick turn comes through almost at once.
    const float MinCutoffHz = 1.5f;
    const float CutoffHzPerDegPerSec = 0.3f;
    /// <summary>The measured delay is averaged over about this long, so one slow frame does not jerk the picture.</summary>
    const float DelaySmoothingSeconds = 1f;

    // Reading steady: engage when the smoothed turn rate stays under EngageDegPerSec for DwellSeconds,
    // release the moment it passes ReleaseDegPerSec.
    const float EngageDegPerSec = 3f;
    const float ReleaseDegPerSec = 6f;
    const float DwellSeconds = 0.25f;
    /// <summary>Held picture may sit this far from the head before it is dragged along. 9.5 px on the One Pro.</summary>
    const float DeadbandDeg = 0.25f;
    /// <summary>While held, the picture eases back onto the head this slowly, so no offset builds up.</summary>
    const float RecentreSeconds = 2f;
    /// <summary>On release the held offset closes this fast: quick enough to be gone within a glance, slow enough not to show as a jump.</summary>
    const float ReleaseSeconds = 0.05f;

    readonly object _gate = new();
    readonly OrientationFilter _filter = new();
    AxisMap _axes = AxisMap.Default;
    float _yawReference;
    bool _recenterPending = true;

    ulong _lastNs;
    double _time, _displayTime;
    Vector3 _smoothRate;
    float _stillSeconds;
    float _delayMs = float.NaN;
    Quaternion _held;
    bool _heldKnown;
    float _band;

    /// <summary>
    /// Longest look-ahead <see cref="GetDisplayPose"/> will use, ms. Predicting further turns tremor into
    /// visible jitter and makes the picture sail past where the head stops.
    /// </summary>
    public float MaxLookAheadMs { get; set; } = 25f;

    /// <summary>
    /// Hold the picture still while the head is nearly still, so small head tremor does not shake text.
    /// A real movement releases it at once.
    /// </summary>
    public bool ReadingSteady { get; set; } = true;

    public AxisMap Axes
    {
        get { lock (_gate) return _axes; }
        set { lock (_gate) _axes = value; }
    }

    public Vector3 GyroBias { get { lock (_gate) return _filter.GyroBias; } }
    public bool BiasKnown { get { lock (_gate) return _filter.BiasKnown; } }
    public bool HasPose { get { lock (_gate) return _filter.IsInitialized; } }
    /// <summary>How many times the gyro offset has been measured from the live stream since start.</summary>
    public int BiasUpdates { get { lock (_gate) return _filter.BiasUpdates; } }

    /// <summary>Starts from a gyro offset (rad/s) measured earlier, e.g. <see cref="TrackingConfig.TryLoadGyroBias"/>.</summary>
    public void SetGyroBias(Vector3 bias)
    {
        lock (_gate) _filter.SetBias(bias);
    }

    public void Update(in ImuSample sample)
    {
        lock (_gate)
        {
            _filter.Update(sample);

            float dt = _lastNs != 0 && sample.TimestampNs > _lastNs ? (float)((sample.TimestampNs - _lastNs) * 1e-9) : 0.001f;
            if (dt > 0.05f) dt = 0.001f; // gap in the stream
            _lastNs = sample.TimestampNs;
            _time += dt;

            Vector3 raw = _filter.Rate;
            float cutoff = MinCutoffHz + CutoffHzPerDegPerSec * raw.Length() * RadToDeg;
            _smoothRate += (raw - _smoothRate) * (1 - MathF.Exp(-2 * MathF.PI * cutoff * dt));

            float speed = _smoothRate.Length() * RadToDeg;
            if (speed > ReleaseDegPerSec) _stillSeconds = 0;
            else if (speed < EngageDegPerSec) _stillSeconds += dt;
        }
    }

    /// <summary>Makes the current heading the new straight-ahead.</summary>
    public void Recenter()
    {
        lock (_gate) _recenterPending = true;
    }

    /// <param name="predictSeconds">
    /// Look this far ahead by carrying on at the current turn rate. A frame reaches the eye some
    /// milliseconds after the pose is read; predicting across that gap stops screens trailing the head.
    /// </param>
    public HeadPose GetPose(float predictSeconds = 0f)
    {
        lock (_gate) return PoseFrom(Ahead(_filter.Orientation, _filter.Rate, predictSeconds));
    }

    /// <summary>
    /// The pose to draw a frame with: looks ahead by the display delay, but capped at
    /// <see cref="MaxLookAheadMs"/>, from a smoothed turn rate, and held still for reading when the head is.
    /// Call once per frame; it keeps state between calls.
    /// </summary>
    /// <param name="measuredDelayMs">This app's measured pose-to-screen time. Noisy; it is averaged here.</param>
    /// <param name="glassesDelayMs">The glasses' own delay (a setting).</param>
    /// <param name="lookAheadMs">The look-ahead actually used.</param>
    public HeadPose GetDisplayPose(float measuredDelayMs, float glassesDelayMs, out float lookAheadMs)
    {
        lock (_gate)
        {
            float dt = (float)(_time - _displayTime);
            _displayTime = _time;

            if (float.IsNaN(_delayMs)) _delayMs = measuredDelayMs;
            else _delayMs += (measuredDelayMs - _delayMs) * (1 - MathF.Exp(-dt / DelaySmoothingSeconds));
            lookAheadMs = Math.Clamp(_delayMs + glassesDelayMs, 0f, MathF.Max(0f, MaxLookAheadMs));

            // Predict along the smoothed rate, but never faster than the fresh reading goes that way: when the
            // head stops or bounces back, the smoothed rate is still running on and would carry the picture past.
            Vector3 rate = _smoothRate;
            float smooth = rate.Length();
            if (smooth > 1e-6f)
                rate *= Math.Clamp(Vector3.Dot(_filter.Rate, rate) / (smooth * smooth), 0f, 1f);
            Quaternion q = Ahead(_filter.Orientation, rate, lookAheadMs / 1000f);

            // Holding is a deadband: the picture stays put until the head has moved further than the band
            // from it, then is dragged along at the band's edge. Widening the band (engaging) never moves
            // the picture; shrinking it (releasing) closes the gap smoothly over ReleaseSeconds.
            bool hold = ReadingSteady && _stillSeconds >= DwellSeconds;
            _band = hold ? DeadbandDeg * DegToRad : _band * MathF.Exp(-dt / ReleaseSeconds);
            if (!_heldKnown || !ReadingSteady)
            {
                _held = q;
                _heldKnown = true;
                _band = 0;
            }
            else if (hold)
                _held = Quaternion.Slerp(_held, q, 1 - MathF.Exp(-dt / RecentreSeconds));

            float gap = Angle(_held, q);
            if (gap > _band) _held = Quaternion.Normalize(Quaternion.Slerp(q, _held, _band / gap));
            return PoseFrom(_held);
        }
    }

    static Quaternion Ahead(Quaternion q, Vector3 rate, float seconds)
    {
        float speed = rate.Length();
        return seconds == 0f || speed < 1e-6f
            ? q
            : Quaternion.Normalize(q * Quaternion.CreateFromAxisAngle(rate / speed, speed * seconds));
    }

    /// <summary>Angle between two orientations, radians. Taken from the sine so tiny angles stay exact in floats.</summary>
    static float Angle(Quaternion a, Quaternion b)
    {
        Quaternion d = Quaternion.Conjugate(a) * b;
        return 2 * MathF.Asin(MathF.Min(1f, new Vector3(d.X, d.Y, d.Z).Length()));
    }

    /// <summary>Turns a filter orientation into angles and a render-world rotation. Call under the lock.</summary>
    HeadPose PoseFrom(Quaternion q)
    {
        Vector3 right = Vector3.Transform(_axes.Right, q);
        Vector3 up = Vector3.Transform(_axes.Up, q);
        Vector3 back = Vector3.Transform(_axes.Back, q);
        Vector3 forward = -back;

        float heading = MathF.Atan2(forward.Y, forward.X);
        if (_recenterPending && _filter.IsInitialized)
        {
            _yawReference = heading;
            _recenterPending = false;
        }

        float yaw = Wrap(heading - _yawReference);
        float pitch = MathF.Asin(Math.Clamp(forward.Z, -1f, 1f));
        float roll = MathF.Atan2(-right.Z, up.Z);

        Vector3 r = ToRenderWorld(right), u = ToRenderWorld(up), b = ToRenderWorld(back);
        var basis = new Matrix4x4(
            r.X, r.Y, r.Z, 0,
            u.X, u.Y, u.Z, 0,
            b.X, b.Y, b.Z, 0,
            0, 0, 0, 1);

        return new HeadPose(yaw * RadToDeg, pitch * RadToDeg, roll * RadToDeg,
            Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(basis)));
    }

    /// <summary>Gravity-aligned world (Z up) to render world (X right, Y up, Z back), with the reference heading removed.</summary>
    Vector3 ToRenderWorld(Vector3 v)
    {
        float c = MathF.Cos(_yawReference), s = MathF.Sin(_yawReference);
        float x = c * v.X + s * v.Y;
        float y = -s * v.X + c * v.Y;
        return new Vector3(-y, v.Z, -x);
    }

    static float Wrap(float angle)
    {
        while (angle > MathF.PI) angle -= 2 * MathF.PI;
        while (angle < -MathF.PI) angle += 2 * MathF.PI;
        return angle;
    }
}
