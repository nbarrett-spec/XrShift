using XrShift.Tracking;

namespace XrShift.Tracking.Tests;

public class SteadinessTests(Xunit.Abstractions.ITestOutputHelper output)
{
    const float DegToRad = MathF.PI / 180f;
    const float NoiseDegPerSec = 0.3f; // per-sample gyro noise, per axis; assumed, of the order the One Pro shows

    /// <summary>Head motion from a turn rate (yaw, pitch in deg/s) as a function of time, with gyro noise, at 1 kHz.</summary>
    static List<ImuSample> Record(float seconds, Func<float, (float Yaw, float Pitch)> rate)
    {
        var sim = new ImuSimulator { GyroNoise = NoiseDegPerSec * DegToRad };
        var samples = new List<ImuSample>();
        for (int i = 0; i < (int)(seconds * 1000); i++)
        {
            (float yaw, float pitch) = rate(i / 1000f);
            samples.Add(sim.Step(yaw * DegToRad, pitch * DegToRad));
        }
        return samples;
    }

    /// <summary>Slow head tremor of a reader: under a tenth of a degree, 3-5 Hz.</summary>
    static (float, float) Tremor(float t) =>
        (0.08f * 2 * MathF.PI * 3.1f * MathF.Cos(2 * MathF.PI * 3.1f * t),
         0.05f * 2 * MathF.PI * 4.7f * MathF.Cos(2 * MathF.PI * 4.7f * t));

    /// <summary>
    /// Starts from a saved gyro offset, as the app does. Without one, the filter would take a second of
    /// steady turning about the vertical for the offset, since gravity does not change during it.
    /// </summary>
    static HeadTracker Tracker(bool readingSteady = true)
    {
        var tracker = new HeadTracker { ReadingSteady = readingSteady };
        tracker.SetGyroBias(System.Numerics.Vector3.Zero);
        return tracker;
    }

    static SteadyScore Old(List<ImuSample> samples, float ms) =>
        SteadyScore.Of(SteadyScore.Replay(samples, Tracker(), t => t.GetPose(ms / 1000f)), ms / 1000f);

    static SteadyScore New(List<ImuSample> samples, float ms) =>
        SteadyScore.Of(SteadyScore.Replay(samples, Tracker(), t => t.GetDisplayPose(ms, 0f, out _)), ms / 1000f);

    [Fact]
    public void StillHeadIsUnderAPixelWhereTheOldPredictionShookSeveral()
    {
        List<ImuSample> still = Record(8, Tremor);
        SteadyScore old = Old(still, 50), now = New(still, 50);
        string numbers = $"old {old.WanderRmsPx:F2} px rms ({old.WanderPeakPx:F2} peak), new {now.WanderRmsPx:F2} px rms ({now.WanderPeakPx:F2} peak)";
        output.WriteLine(numbers);

        Assert.True(old.WanderRmsPx > 2.5f, numbers);
        Assert.True(now.WanderRmsPx < 1f, numbers);
        Assert.True(now.WanderPeakPx < 2.5f, numbers); // what is left is the estimate's own slow drift from gyro noise
    }

    /// <summary>Still, then a turn left at 60 deg/s that stops over 40 ms, then still.</summary>
    static List<ImuSample> TurnThenStop() => Record(4.5f, t =>
    {
        (float y, float p) = Tremor(t);
        if (t < 1.5f) return (y, p);
        if (t < 1.55f) return (60 * (t - 1.5f) / 0.05f, 0);
        if (t < 3f) return (60, 0);
        if (t < 3.04f) return (60 * (3.04f - t) / 0.04f, 0);
        return (y, p);
    });

    [Fact]
    public void StoppingOvershootsAtLeastHalfAsMuch()
    {
        List<ImuSample> samples = TurnThenStop();
        SteadyScore old = Old(samples, 50), now = New(samples, 50);
        string numbers = $"overshoot at 50 ms: old {old.OvershootDeg:F2}°, new {now.OvershootDeg:F2}°";
        output.WriteLine(numbers);
        Assert.True(now.OvershootDeg <= old.OvershootDeg / 2, numbers);
        Assert.True(old.OvershootDeg > 1f, $"old {old.OvershootDeg:F2}°"); // the case is a real test
    }

    [Theory]
    [InlineData(15f)]
    [InlineData(25f)]
    public void ASteadyTurnLagsNoMoreThanBeforeWithinTheCap(float ms)
    {
        List<ImuSample> samples = TurnThenStop();
        SteadyScore old = Old(samples, ms), now = New(samples, ms);
        string numbers = $"lag at {ms} ms: old {old.LagMs:F1} ms, new {now.LagMs:F1} ms";
        output.WriteLine(numbers);
        Assert.True(now.LagMs - old.LagMs < 3f, numbers);
    }

    [Fact]
    public void BeyondTheCapTheTurnTrailsByTheRest()
    {
        // The price of the cap: a 50 ms display delay is only half predicted, so a steady turn trails ~25 ms.
        SteadyScore now = New(TurnThenStop(), 50);
        output.WriteLine($"lag at 50 ms: new {now.LagMs:F1} ms");
        Assert.InRange(now.LagMs, 22f, 28f);
    }

    [Fact]
    public void AQuickGlanceIsNotDelayedAndDoesNotJump()
    {
        // Settled and held for reading, then 20° left in 150 ms (peak 267 deg/s).
        List<ImuSample> samples = Record(2.5f, t =>
        {
            if (t < 1.5f || t >= 1.65f) return Tremor(t);
            float phase = (t - 1.5f) / 0.15f;
            return (20f / 0.15f * (1 - MathF.Cos(2 * MathF.PI * phase)), 0);
        });

        (float oldAt, float oldStep) = Glance(samples, t => t.GetPose(0.025f));
        (float newAt, float newStep) = Glance(samples, t => t.GetDisplayPose(25f, 0f, out _));
        string numbers = $"half-way at old {oldAt:F1} ms, new {newAt:F1} ms; largest step per ms old {oldStep:F3}°, new {newStep:F3}°";
        output.WriteLine(numbers);

        Assert.True(newAt - oldAt < 2f, numbers);
        Assert.True(newStep < oldStep * 1.1f, numbers);
    }

    /// <summary>When the drawn yaw first passes 10° (ms after the glance starts) and its largest change in one sample.</summary>
    static (float HalfWayMs, float LargestStepDeg) Glance(List<ImuSample> samples, Func<HeadTracker, HeadPose> read)
    {
        HeadTracker tracker = Tracker();
        float halfWay = float.NaN, largest = 0, previous = float.NaN;
        for (int i = 0; i < samples.Count; i++)
        {
            tracker.Update(samples[i]);
            float yaw = read(tracker).YawDeg;
            if (i > 1000 && !float.IsNaN(previous)) largest = MathF.Max(largest, MathF.Abs(yaw - previous));
            if (float.IsNaN(halfWay) && i >= 1500 && yaw > 10f) halfWay = i - 1500;
            previous = yaw;
        }
        return (halfWay, largest);
    }

    [Fact]
    public void LookAheadIsCappedAndASlowFrameDoesNotJerkIt()
    {
        var sim = new ImuSimulator();
        var tracker = new HeadTracker();
        void Frame(float measuredMs, out float used)
        {
            for (int i = 0; i < 11; i++) tracker.Update(sim.Step()); // about one 90 Hz frame
            tracker.GetDisplayPose(measuredMs, 3f, out used);
        }

        float used = 0;
        for (int i = 0; i < 200; i++) Frame(10f, out used);
        Assert.InRange(used, 12.9f, 13.1f);

        Frame(99f, out used); // one very slow frame
        Assert.InRange(used, 13f, 14.2f);

        for (int i = 0; i < 300; i++) Frame(60f, out used); // the far desk
        Assert.Equal(25f, used);

        tracker.MaxLookAheadMs = 40f;
        Frame(60f, out used);
        Assert.Equal(40f, used);
    }

    [Fact]
    public void ReadingSteadyOffFollowsEveryTremor()
    {
        List<ImuSample> still = Record(8, Tremor);
        SteadyScore off = SteadyScore.Of(SteadyScore.Replay(still, Tracker(readingSteady: false),t => t.GetDisplayPose(15f, 0f, out _)), 0.015f);
        Assert.True(off.WanderRmsPx > 2f, $"{off.WanderRmsPx:F2} px");
    }
}
