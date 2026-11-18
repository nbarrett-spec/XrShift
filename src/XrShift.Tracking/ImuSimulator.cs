using System.Numerics;

namespace XrShift.Tracking;

/// <summary>
/// Fake glasses: produces the samples a real head would for given turn rates.
/// For tests and for working on the app without the glasses plugged in.
/// </summary>
public sealed class ImuSimulator
{
    const float Gravity = 9.80665f;

    readonly AxisMap _axes;
    Quaternion _orientation; // sensor axes -> world (X forward, Y left, Z up)
    ulong _timestampNs = 1_000_000_000;

    public Vector3 GyroBias { get; set; }

    public ImuSimulator(AxisMap? axes = null)
    {
        _axes = axes ?? AxisMap.Default;
        Vector3 rightW = new(0, -1, 0), upW = new(0, 0, 1), backW = new(-1, 0, 0);
        Vector3 Image(Vector3 sensorAxis) =>
            Vector3.Dot(sensorAxis, _axes.Right) * rightW +
            Vector3.Dot(sensorAxis, _axes.Up) * upW +
            Vector3.Dot(sensorAxis, _axes.Back) * backW;
        Vector3 x = Image(Vector3.UnitX), y = Image(Vector3.UnitY), z = Image(Vector3.UnitZ);
        _orientation = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(new Matrix4x4(
            x.X, x.Y, x.Z, 0,
            y.X, y.Y, y.Z, 0,
            z.X, z.Y, z.Z, 0,
            0, 0, 0, 1)));
    }

    /// <summary>Advances time. Rates are rad/s about the head's own axes: turn left, look up, tilt right.</summary>
    public ImuSample Step(float yawLeft = 0, float pitchUp = 0, float rollRight = 0, float dt = 0.001f)
    {
        Vector3 rate = yawLeft * _axes.Up + pitchUp * _axes.Right - rollRight * _axes.Back;
        float speed = rate.Length();
        if (speed * dt > 1e-12f)
            _orientation = Quaternion.Normalize(_orientation * Quaternion.CreateFromAxisAngle(rate / speed, speed * dt));

        _timestampNs += (ulong)Math.Round(dt * 1e9);
        Vector3 accel = Vector3.Transform(new Vector3(0, 0, Gravity), Quaternion.Conjugate(_orientation));
        return new ImuSample(_timestampNs, rate + GyroBias, accel);
    }
}
