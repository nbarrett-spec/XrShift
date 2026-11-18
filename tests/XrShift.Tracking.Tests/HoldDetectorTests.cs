using System.Numerics;
using XrShift.Tracking;

namespace XrShift.Tracking.Tests;

public class HoldDetectorTests
{
    const float DegToRad = MathF.PI / 180f;

    static void Feed(HoldDetector detector, ImuSimulator sim, float seconds, float yawLeft = 0, float pitchUp = 0)
    {
        int steps = (int)MathF.Round(seconds * 1000);
        for (int i = 0; i < steps; i++)
            detector.Add(sim.Step(yawLeft * DegToRad, pitchUp * DegToRad));
    }

    [Fact]
    public void NeedsAFullStretchBeforeItReads()
    {
        var sim = new ImuSimulator();
        var detector = new HoldDetector(1.5f);
        Feed(detector, sim, 1.3f);
        Assert.False(detector.TryRead(out _));
        Feed(detector, sim, 0.3f);
        Assert.True(detector.TryRead(out HoldReading r));
        Assert.True(detector.IsHold(r));
    }

    [Fact]
    public void ReadsGyroOffsetAndGravityWhileStill()
    {
        var bias = new Vector3(0.002f, -0.003f, 0.001f);
        var sim = new ImuSimulator { GyroBias = bias };
        var detector = new HoldDetector(1.5f);
        Feed(detector, sim, 2);

        Assert.True(detector.TryRead(out HoldReading r));
        Assert.True(detector.IsHold(r));
        Assert.True((r.GyroMean - bias).Length() < 1e-5f);
        Assert.InRange(r.AccelMean.Length(), 9.80f, 9.82f);
        Assert.True((Vector3.Normalize(r.AccelMean) - AxisMap.Default.Up).Length() < 1e-3f);
        Assert.InRange(r.TiltSpreadDeg, 0f, 0.05f);
        Assert.InRange(r.GyroNoise, 0f, 1e-4f);
    }

    [Fact]
    public void ANodIsNotAHoldUntilItSettles()
    {
        var sim = new ImuSimulator();
        var detector = new HoldDetector(1.5f);
        Feed(detector, sim, 2);
        Feed(detector, sim, 1, pitchUp: 30);
        Assert.True(detector.TryRead(out HoldReading moving));
        Assert.False(detector.IsHold(moving));

        Feed(detector, sim, 1.6f);
        Assert.True(detector.TryRead(out HoldReading settled));
        Assert.True(detector.IsHold(settled));

        // Gravity has swung 30 degrees from where it was when level.
        float cos = Vector3.Dot(Vector3.Normalize(settled.AccelMean), AxisMap.Default.Up);
        Assert.InRange(MathF.Acos(cos) / DegToRad, 29.5f, 30.5f);
    }

    [Fact]
    public void ASteadyTurnIsNotAHoldEvenThoughGravityStaysPut()
    {
        var sim = new ImuSimulator();
        var detector = new HoldDetector(1.5f);
        Feed(detector, sim, 2, yawLeft: 20);
        Assert.True(detector.TryRead(out HoldReading r));
        Assert.InRange(r.TiltSpreadDeg, 0f, 0.1f);
        Assert.False(detector.IsHold(r));
    }

    [Fact]
    public void ResetForgetsWhatCameBefore()
    {
        var sim = new ImuSimulator();
        var detector = new HoldDetector(1.5f);
        Feed(detector, sim, 2);
        detector.Reset();
        Assert.False(detector.TryRead(out _));
        Feed(detector, sim, 1.6f);
        Assert.True(detector.TryRead(out _));
    }

    [Fact]
    public void DeskSettingsRejectAWornHeadsWobble()
    {
        var sim = new ImuSimulator();
        var worn = new HoldDetector(1.5f);
        var desk = new HoldDetector(1.5f) { MaxTiltSpreadDeg = 0.3f, MaxGyroRate = 0.035f, MaxGyroSpread = 0.0045f };

        // A head held "still": sways about 1 deg/s, a fraction of a degree each way.
        for (int i = 0; i < 2000; i++)
        {
            ImuSample s = sim.Step(pitchUp: MathF.Sin(i * 0.004f) * 1f * DegToRad, yawLeft: MathF.Cos(i * 0.003f) * 1f * DegToRad);
            worn.Add(s);
            desk.Add(s);
        }

        Assert.True(worn.TryRead(out HoldReading w));
        Assert.True(worn.IsHold(w));
        Assert.True(desk.TryRead(out HoldReading d));
        Assert.False(desk.IsHold(d));
    }
}
