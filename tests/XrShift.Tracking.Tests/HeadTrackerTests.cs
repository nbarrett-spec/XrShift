using System.Numerics;
using XrShift.Tracking;

namespace XrShift.Tracking.Tests;

public class HeadTrackerTests
{
    const float DegToRad = MathF.PI / 180f;

    static void Run(ImuSimulator sim, HeadTracker tracker, float seconds, float yawLeft = 0, float pitchUp = 0, float rollRight = 0)
    {
        int steps = (int)MathF.Round(seconds * 1000);
        for (int i = 0; i < steps; i++)
            tracker.Update(sim.Step(yawLeft * DegToRad, pitchUp * DegToRad, rollRight * DegToRad));
    }

    static (ImuSimulator, HeadTracker) Start(AxisMap? axes = null)
    {
        var sim = new ImuSimulator(axes);
        var tracker = new HeadTracker { Axes = axes ?? AxisMap.Default };
        Run(sim, tracker, 0.5f);
        tracker.GetPose(); // first read sets straight-ahead
        return (sim, tracker);
    }

    static void AssertPose(HeadPose pose, float yaw, float pitch, float roll, float tolerance = 0.5f)
    {
        Assert.InRange(pose.YawDeg, yaw - tolerance, yaw + tolerance);
        Assert.InRange(pose.PitchDeg, pitch - tolerance, pitch + tolerance);
        Assert.InRange(pose.RollDeg, roll - tolerance, roll + tolerance);
    }

    static void AssertClose(Vector3 expected, Vector3 actual, float tolerance = 0.02f) =>
        Assert.True((expected - actual).Length() < tolerance, $"expected {expected}, got {actual}");

    [Fact]
    public void StaysLevelWhenStill()
    {
        var (sim, tracker) = Start();
        Run(sim, tracker, 5);
        AssertPose(tracker.GetPose(), 0, 0, 0, 0.1f);
    }

    [Fact]
    public void TurningLeftIsPositiveYaw()
    {
        var (sim, tracker) = Start();
        Run(sim, tracker, 0.5f, yawLeft: 90);
        AssertPose(tracker.GetPose(), 45, 0, 0);
    }

    [Fact]
    public void LookingUpIsPositivePitch()
    {
        var (sim, tracker) = Start();
        Run(sim, tracker, 0.5f, pitchUp: 60);
        AssertPose(tracker.GetPose(), 0, 30, 0);
    }

    [Fact]
    public void TiltingRightIsPositiveRoll()
    {
        var (sim, tracker) = Start();
        Run(sim, tracker, 0.5f, rollRight: 40);
        AssertPose(tracker.GetPose(), 0, 0, 20);
    }

    [Fact]
    public void OrientationPointsWhereTheAnglesSay()
    {
        var (sim, tracker) = Start();
        AssertClose(-Vector3.UnitZ, Vector3.Transform(-Vector3.UnitZ, tracker.GetPose().Orientation));

        Run(sim, tracker, 0.5f, yawLeft: 90); // now 45° left
        Quaternion q = tracker.GetPose().Orientation;
        float h = MathF.Sqrt(0.5f);
        AssertClose(new Vector3(-h, 0, -h), Vector3.Transform(-Vector3.UnitZ, q)); // forward swings toward -X (left)
        AssertClose(Vector3.UnitY, Vector3.Transform(Vector3.UnitY, q));           // up stays up

        Run(sim, tracker, 0.5f, yawLeft: -90); // back to centre
        Run(sim, tracker, 0.5f, pitchUp: 60);  // now 30° up
        q = tracker.GetPose().Orientation;
        AssertClose(new Vector3(0, 0.5f, -MathF.Sqrt(0.75f)), Vector3.Transform(-Vector3.UnitZ, q));
    }

    [Fact]
    public void RecenterMakesCurrentHeadingZero()
    {
        var (sim, tracker) = Start();
        Run(sim, tracker, 0.5f, yawLeft: 90);
        tracker.Recenter();
        AssertPose(tracker.GetPose(), 0, 0, 0);
        Run(sim, tracker, 0.25f, yawLeft: -40);
        AssertPose(tracker.GetPose(), -10, 0, 0);
    }

    [Fact]
    public void LearnsTheGyroOffsetAtRestAndStopsDrifting()
    {
        var bias = new Vector3(0.010f, -0.020f, 0.015f); // about 1 deg/s per axis
        var sim = new ImuSimulator { GyroBias = bias };
        var tracker = new HeadTracker();

        Run(sim, tracker, 2);
        Assert.True(tracker.BiasKnown);
        AssertClose(bias, tracker.GyroBias, 0.0005f);

        tracker.Recenter();
        tracker.GetPose();
        Run(sim, tracker, 20);
        AssertPose(tracker.GetPose(), 0, 0, 0, 0.3f);

        Run(sim, tracker, 0.5f, yawLeft: 90); // real motion still tracks
        AssertPose(tracker.GetPose(), 45, 0, 0, 0.6f);
    }

    [Fact]
    public void DoesNotMistakeASteadyTurnForGyroOffset()
    {
        var sim = new ImuSimulator();
        var tracker = new HeadTracker();
        Run(sim, tracker, 2);      // learns a zero offset at rest
        tracker.GetPose();
        Run(sim, tracker, 3, yawLeft: 10); // slow steady turn
        AssertPose(tracker.GetPose(), 30, 0, 0, 0.6f);
    }

    [Fact]
    public void ASteadyTurnFromTheStartIsNotTakenForTheGyroOffset()
    {
        // No saved offset and no still second yet: turning about the vertical leaves gravity unchanged,
        // so one steady second of it looked exactly like a resting offset.
        var sim = new ImuSimulator();
        var tracker = new HeadTracker();
        Run(sim, tracker, 0.05f);
        tracker.GetPose();
        Run(sim, tracker, 3, yawLeft: 10);
        Assert.False(tracker.BiasKnown);
        AssertPose(tracker.GetPose(), 30, 0, 0, 0.6f);
    }

    [Fact]
    public void AnOffsetLearnedFromASlowTurnGivesWayToARealRest()
    {
        // Under 3 deg/s a steady turn still passes for an offset; the first real rest must replace it,
        // or the wrong offset locks out every later measurement and heading drifts for good.
        var bias = new Vector3(0.002f, -0.003f, 0.001f);
        var sim = new ImuSimulator { GyroBias = bias };
        var tracker = new HeadTracker();
        Run(sim, tracker, 0.05f);
        Run(sim, tracker, 1.5f, yawLeft: 2);
        Assert.True(tracker.BiasKnown);
        Run(sim, tracker, 2);
        AssertClose(bias, tracker.GyroBias, 0.0005f);
    }

    [Fact]
    public void StartsFromASavedGyroOffsetWithoutNeedingToSitStill()
    {
        var bias = new Vector3(0.010f, -0.020f, 0.015f); // drifts heading about 1.4 deg/s if ignored
        var sim = new ImuSimulator { GyroBias = bias };
        var tracker = new HeadTracker();
        tracker.SetGyroBias(bias);
        Assert.True(tracker.BiasKnown);

        // Never still: wobble first, then a slow turn. Nothing here can be measured live.
        for (int i = 0; i < 300; i++)
            tracker.Update(sim.Step(pitchUp: MathF.Sin(i * 0.05f) * 0.2f));
        tracker.GetPose();
        float startYaw = tracker.GetPose().YawDeg;
        Run(sim, tracker, 3, yawLeft: 10);

        Assert.Equal(0, tracker.BiasUpdates);
        Assert.InRange(tracker.GetPose().YawDeg - startYaw, 29.4f, 30.6f);
    }

    [Fact]
    public void PredictionLeadsAMovingHeadAndLeavesAStillOneAlone()
    {
        var (sim, tracker) = Start();
        AssertPose(tracker.GetPose(0.03f), 0, 0, 0, 0.05f);

        Run(sim, tracker, 0.5f, yawLeft: 90); // now at 45, still turning at 90 deg/s
        Assert.InRange(tracker.GetPose(0.02f).YawDeg - tracker.GetPose().YawDeg, 1.7f, 1.9f);

        Run(sim, tracker, 0.1f);              // stopped
        Assert.InRange(tracker.GetPose(0.02f).YawDeg - tracker.GetPose().YawDeg, -0.01f, 0.01f);
    }

    [Fact]
    public void AShoveDuringATurnDoesNotTipTheHorizon()
    {
        // Turning the head swings the sensor sideways, and the accelerometer reads that as gravity
        // leaning over (here 1 m/s², about 6°). The tilt estimate must not chase it.
        var (sim, tracker) = Start();
        Run(sim, tracker, 2);
        for (int i = 0; i < 600; i++)
        {
            ImuSample s = sim.Step(yawLeft: 60 * DegToRad);
            tracker.Update(s with { Accel = s.Accel + AxisMap.Default.Right * 1.0f });
        }

        HeadPose pose = tracker.GetPose();
        Assert.InRange(pose.YawDeg, 35.5f, 36.5f);
        Assert.InRange(pose.PitchDeg, -0.05f, 0.05f);
        Assert.InRange(pose.RollDeg, -0.05f, 0.05f);
    }

    [Fact]
    public void GravityStillPullsATiltErrorOutWhileTheHeadIsStill()
    {
        var (sim, tracker) = Start();
        Run(sim, tracker, 2);

        // Make the estimate wrong by 6°: the gyro reports a nod that the accelerometer never saw.
        var still = sim.Step();
        for (int i = 0; i < 200; i++)
            tracker.Update(still with { TimestampNs = still.TimestampNs + (ulong)(i + 1) * 1_000_000, Gyro = AxisMap.Default.Right * (30 * DegToRad) });
        float wrong = tracker.GetPose().PitchDeg;
        Assert.InRange(wrong, 5.5f, 6.1f);

        // Sitting still, it comes back: slowly (so it never shows as a wobble), but all the way.
        ulong t = still.TimestampNs + 200_000_000;
        for (int i = 0; i < 5000; i++)
            tracker.Update(still with { TimestampNs = t += 1_000_000 });
        Assert.InRange(tracker.GetPose().PitchDeg, 3.2f, 4.0f); // about 6 x e^(-0.5) after 5 s

        for (int i = 0; i < 55000; i++)
            tracker.Update(still with { TimestampNs = t += 1_000_000 });
        Assert.InRange(tracker.GetPose().PitchDeg, -0.1f, 0.1f);
    }

    [Fact]
    public void CountsLiveGyroOffsetMeasurements()
    {
        var sim = new ImuSimulator { GyroBias = new Vector3(0.002f, 0.001f, -0.003f) };
        var tracker = new HeadTracker();
        Run(sim, tracker, 0.5f);
        Assert.Equal(0, tracker.BiasUpdates);
        Run(sim, tracker, 2);
        Assert.True(tracker.BiasUpdates > 0);
    }

    [Fact]
    public void WorksWithADifferentSensorMounting()
    {
        var axes = new AxisMap(Vector3.UnitY, Vector3.UnitZ, Vector3.UnitX);
        var (sim, tracker) = Start(axes);
        Run(sim, tracker, 0.5f, yawLeft: 90);
        AssertPose(tracker.GetPose(), 45, 0, 0);
        Run(sim, tracker, 0.5f, pitchUp: 40);
        AssertPose(tracker.GetPose(), 45, 20, 0);
    }

    [Fact]
    public void WrongAxisMapGivesWrongAngles()
    {
        // Guards against tests that would pass no matter what the map is. A turn about the
        // vertical reads the same with any level map, so this uses a look-up instead.
        var sim = new ImuSimulator(AxisMap.Default);
        var tracker = new HeadTracker { Axes = new AxisMap(-Vector3.UnitY, -Vector3.UnitZ, Vector3.UnitX) };
        Run(sim, tracker, 0.5f);
        tracker.GetPose();
        Run(sim, tracker, 0.5f, pitchUp: 60);
        Assert.False(MathF.Abs(tracker.GetPose().PitchDeg - 30) < 5);
    }
}

public class AxisMapTests
{
    static void AssertClose(Vector3 expected, Vector3 actual, float tolerance = 0.001f) =>
        Assert.True((expected - actual).Length() < tolerance, $"expected {expected}, got {actual}");

    [Fact]
    public void RoundTripsThroughText()
    {
        // A sensor tilted inside the frame: nothing lines up with a sensor axis.
        Assert.True(AxisMap.TryFromRightUp(new Vector3(-1, 0.02f, 0.03f), new Vector3(0, -0.775f, 0.632f), out AxisMap map));
        Assert.True(AxisMap.TryParse(map.ToString(), out AxisMap parsed));
        AssertClose(map.Right, parsed.Right);
        AssertClose(map.Up, parsed.Up);
        AssertClose(map.Back, parsed.Back);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Right=1,0,0;Up=1,0,0;Back=0,0,1")]   // not perpendicular
    [InlineData("Right=1,0,0;Up=0,1,0;Back=0,0,-1")]  // left-handed
    [InlineData("Right=2,0,0;Up=0,1,0;Back=0,0,1")]   // not unit length
    [InlineData("Right=a,0,0;Up=0,1,0;Back=0,0,1")]
    public void RejectsBadText(string text) => Assert.False(AxisMap.TryParse(text, out _));

    [Fact]
    public void BuildsASquareRightHandedSetFromRoughMeasurements()
    {
        var up = Vector3.Normalize(new Vector3(0.05f, -0.78f, 0.62f));
        Assert.True(AxisMap.TryFromRightUp(new Vector3(-0.9f, 0.2f, 0.1f), up, out AxisMap map));

        AssertClose(up, map.Up);
        Assert.InRange(Vector3.Dot(map.Right, map.Up), -1e-4f, 1e-4f);
        Assert.InRange(map.Right.Length(), 0.999f, 1.001f);
        AssertClose(Vector3.Cross(map.Right, map.Up), map.Back);
        Assert.True(Vector3.Dot(map.Right, new Vector3(-0.9f, 0.2f, 0.1f)) > 0.9f);

        Assert.False(AxisMap.TryFromRightUp(up * 2 + new Vector3(0.01f, 0, 0), up, out _)); // nearly parallel
    }

    static Vector3 MeanAccel(ImuSimulator sim, int steps)
    {
        Vector3 sum = Vector3.Zero;
        for (int i = 0; i < steps; i++) sum += sim.Step().Accel;
        return sum / steps;
    }

    [Theory]
    [InlineData(0f)]    // a clean nod
    [InlineData(25f)]   // turning left on the way: gravity does not see it
    [InlineData(-40f)]  // turning right on the way
    public void MeasuresTheMountingFromGravityLevelAndLookingUp(float turnDeg)
    {
        Assert.True(AxisMap.TryFromRightUp(new Vector3(0.99f, -0.10f, -0.11f), new Vector3(-0.02f, -0.84f, 0.54f), out AxisMap real));
        var sim = new ImuSimulator(real);

        Vector3 level = MeanAccel(sim, 200);
        for (int i = 0; i < 500; i++) sim.Step(yawLeft: turnDeg * MathF.PI / 180 * 2); // 0.5 s
        for (int i = 0; i < 500; i++) sim.Step(pitchUp: 28f * MathF.PI / 180 * 2);
        Vector3 lookingUp = MeanAccel(sim, 200);

        Assert.True(AxisMap.TryFromLevelAndLookUp(level, lookingUp, out AxisMap measured, out float pitchDeg));
        Assert.InRange(pitchDeg, 27.5f, 28.5f);
        AssertClose(real.Right, measured.Right, 0.01f);
        AssertClose(real.Up, measured.Up, 0.01f);
        AssertClose(real.Back, measured.Back, 0.01f);
    }

    [Fact]
    public void LookingDownGivesTheOppositeSideAxisSoItCanBeCaught()
    {
        var sim = new ImuSimulator();
        Vector3 level = MeanAccel(sim, 100);
        for (int i = 0; i < 500; i++) sim.Step(pitchUp: -0.8f);
        Assert.True(AxisMap.TryFromLevelAndLookUp(level, MeanAccel(sim, 100), out AxisMap measured, out _));
        AssertClose(-AxisMap.Default.Right, measured.Right, 0.01f);
    }

    [Fact]
    public void RefusesAMountingFromTooSmallANod()
    {
        var sim = new ImuSimulator();
        Vector3 level = MeanAccel(sim, 100);
        for (int i = 0; i < 100; i++) sim.Step(pitchUp: 0.3f); // 1.7 degrees
        Assert.False(AxisMap.TryFromLevelAndLookUp(level, MeanAccel(sim, 100), out _, out float pitchDeg));
        Assert.InRange(pitchDeg, 1.5f, 2f);
    }

    [Fact]
    public void GyroOffsetRoundTripsThroughText()
    {
        var bias = new Vector3(-0.00213f, 0.00133f, 0.00155f);
        Assert.True(TrackingConfig.TryParseGyroBias(TrackingConfig.FormatGyroBias(bias), out Vector3 parsed));
        AssertClose(bias, parsed, 1e-7f);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0.001,0.002")]
    [InlineData("a,b,c")]
    [InlineData("NaN,0,0")]
    [InlineData("0.5,0,0")] // 29 deg/s is not a resting offset
    public void RejectsBadGyroOffsetText(string text) => Assert.False(TrackingConfig.TryParseGyroBias(text, out _));

    [Fact]
    public void TrackerWorksWithATiltedSensor()
    {
        Assert.True(AxisMap.TryFromRightUp(new Vector3(-1, 0.02f, 0.03f), new Vector3(0, -0.775f, 0.632f), out AxisMap axes));
        var sim = new ImuSimulator(axes);
        var tracker = new HeadTracker { Axes = axes };
        for (int i = 0; i < 500; i++) tracker.Update(sim.Step());
        tracker.GetPose();
        for (int i = 0; i < 500; i++) tracker.Update(sim.Step(yawLeft: MathF.PI / 2));
        for (int i = 0; i < 500; i++) tracker.Update(sim.Step(pitchUp: MathF.PI / 6));

        HeadPose pose = tracker.GetPose();
        Assert.InRange(pose.YawDeg, 44.5f, 45.5f);
        Assert.InRange(pose.PitchDeg, 14.5f, 15.5f);
        Assert.InRange(pose.RollDeg, -0.5f, 0.5f);
    }
}
