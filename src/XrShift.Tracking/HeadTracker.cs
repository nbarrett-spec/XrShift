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

    readonly object _gate = new();
    readonly OrientationFilter _filter = new();
    AxisMap _axes = AxisMap.Default;
    float _yawReference;
    bool _recenterPending = true;

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
        lock (_gate) _filter.Update(sample);
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
        lock (_gate)
        {
            Quaternion q = _filter.Orientation;
            if (predictSeconds != 0f)
            {
                Vector3 rate = _filter.Rate;
                float speed = rate.Length();
                if (speed > 1e-6f)
                    q = Quaternion.Normalize(q * Quaternion.CreateFromAxisAngle(rate / speed, speed * predictSeconds));
            }
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
