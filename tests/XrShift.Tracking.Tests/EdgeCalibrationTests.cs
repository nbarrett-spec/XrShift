using System.Numerics;
using XrShift.Tracking;
using Xunit.Abstractions;

namespace XrShift.Tracking.Tests;

/// <summary>
/// A pretend wearer with glasses whose true view angle, level and lean are known: for each line asked for they hold
/// their head roughly as told and tilt it until a true vertical lies along the drawn line, give or take their aim.
/// </summary>
sealed class EdgeWearer(float fovDeg, float levelDeg, float leanDeg, int seed = 7)
{
    const float DegToRad = MathF.PI / 180f;
    readonly Random _random = new(seed);
    double _clock;

    /// <summary>How far off (degrees of line tilt) a press is, rms. Half a degree is a careful person.</summary>
    public float AimDeg { get; init; }
    /// <summary>Heading drift, deg/s, as the tracker would report it. Heading should not matter at all.</summary>
    public float DriftDegPerSecond { get; init; }
    /// <summary>Share of presses made before the head had finished tilting: these are off by 4 to 8 degrees.</summary>
    public float EarlyShare { get; init; }
    /// <summary>Looks the other way from what is asked (up instead of down) on the first try.</summary>
    public bool WrongWayFirst { get; init; }

    public int Presses { get; private set; }
    public int Refused { get; private set; }

    public Quaternion HeadFor(EdgeTarget t, bool wrongWay = false)
    {
        _clock += 4; // seconds per press, for the drift
        float pitch = t.Posture switch
        {
            HeadPosture.Up => 30 + 10 * _random.NextSingle(),
            HeadPosture.Down => -30 - 10 * _random.NextSingle(),
            _ => -5 + 10 * _random.NextSingle(),
        };
        if (wrongWay) pitch = -pitch;
        float yaw = -40 + 80 * _random.NextSingle();
        float want = t.DrawnTiltDeg + AimDeg * Gaussian();
        if (_random.NextSingle() < EarlyShare) want += (4 + 4 * _random.NextSingle()) * (_random.Next(2) * 2 - 1);

        // Tilt the head until a true vertical leans as wanted.
        float roll = 0;
        for (int i = 0; i < 30; i++)
        {
            float miss = Seen(t, yaw, pitch, roll) - want;
            float slope = (Seen(t, yaw, pitch, roll + 0.01f) - Seen(t, yaw, pitch, roll - 0.01f)) / 0.02f;
            roll -= miss / slope;
        }

        Quaternion drift = Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)(DriftDegPerSecond * _clock) * DegToRad);
        return drift * Head(yaw, pitch, roll);
    }

    float Seen(EdgeTarget t, float yaw, float pitch, float roll) =>
        EdgeFitter.VerticalTiltDeg(Head(yaw, pitch, roll), t.XPx, fovDeg, levelDeg, leanDeg, 1920);

    /// <summary>Runs a calibration to the end. Returns it for the result.</summary>
    public EdgeCalibration Run(EdgeCalibration calibration)
    {
        bool first = WrongWayFirst;
        while (calibration.Current is { } t)
        {
            Presses++;
            if (!calibration.Press(HeadFor(t, first))) Refused++;
            first = false;
            if (Presses > 100) throw new InvalidOperationException("never finished");
        }
        return calibration;
    }

    /// <summary>Head axes into the Y-up world: turned left by yaw, up by pitch, tilted right by roll.</summary>
    public static Quaternion Head(float yawDeg, float pitchDeg, float rollDeg) =>
        Quaternion.CreateFromYawPitchRoll(yawDeg * DegToRad, pitchDeg * DegToRad, -rollDeg * DegToRad);

    float Gaussian() =>
        MathF.Sqrt(-2f * MathF.Log(1f - _random.NextSingle())) * MathF.Cos(2f * MathF.PI * _random.NextSingle());
}

public class EdgeCalibrationTests(ITestOutputHelper output)
{
    const float TrueFov = 47.5f, TrueLevel = 2.5f, TrueLean = -1.2f;

    [Fact]
    public void HeadPoseConventionsMatchTheTracker()
    {
        HeadPose p = PoseOf(EdgeWearer.Head(20, 30, 10));
        Assert.InRange(p.PitchDeg, 29.9f, 30.1f);
        Assert.InRange(p.RollDeg, 9.9f, 10.1f);
    }

    [Fact]
    public void AVerticalLooksUprightInTheMiddleAndLeansInAtTheSidesWhenLookingUp()
    {
        Quaternion level = EdgeWearer.Head(0, 0, 0), up = EdgeWearer.Head(0, 30, 0);
        Assert.Equal(0, EdgeFitter.VerticalTiltDeg(level, 800, 50.7f, 0, 0, 1920), 3);
        Assert.Equal(0, EdgeFitter.VerticalTiltDeg(up, 0, 50.7f, 0, 0, 1920), 3);
        Assert.True(EdgeFitter.VerticalTiltDeg(up, 800, 50.7f, 0, 0, 1920) < -5); // right side: top leans left, toward the middle
        Assert.True(EdgeFitter.VerticalTiltDeg(up, -800, 50.7f, 0, 0, 1920) > 5);

        // Tilting the head right makes the room's verticals lean left in the picture.
        Assert.True(EdgeFitter.VerticalTiltDeg(EdgeWearer.Head(0, 0, 5), 0, 50.7f, 0, 0, 1920) < -4.9f);
        // A + lean trim turns the drawn world clockwise, which is what a display turned anticlockwise needs.
        Assert.True(EdgeFitter.VerticalTiltDeg(level, 0, 50.7f, 0, 2, 1920) > 1.9f);
    }

    [Fact]
    public void HeadingDoesNotMatter()
    {
        float a = EdgeFitter.VerticalTiltDeg(EdgeWearer.Head(0, 30, 3), 700, 48, 2, 1, 1920);
        float b = EdgeFitter.VerticalTiltDeg(EdgeWearer.Head(137, 30, 3), 700, 48, 2, 1, 1920);
        Assert.Equal(a, b, 3);
    }

    [Fact]
    public void APerfectWearerGivesTheExactAngles()
    {
        EdgeCalibration levelLean = new EdgeWearer(TrueFov, TrueLevel, TrueLean).Run(EdgeCalibration.LevelLean(TrueFov));
        Assert.Equal(CalibrationState.Done, levelLean.State);
        Assert.Equal(TrueLevel, levelLean.Result!.LevelDeg, 1);
        Assert.Equal(TrueLean, levelLean.Result.LeanDeg, 2);

        // The view angle step starts from the default 50.7 and the presses must pull it to the truth.
        EdgeCalibration view = new EdgeWearer(TrueFov, TrueLevel, TrueLean).Run(EdgeCalibration.ViewAngle(50.7f, earlier: levelLean.Presses));
        Assert.Equal(CalibrationState.Done, view.State);
        Assert.Equal(TrueFov, view.Result!.FovDeg, 1);
        Assert.Equal(TrueLevel, view.Result.LevelDeg, 1);
        Assert.Equal(TrueLean, view.Result.LeanDeg, 1);
        Assert.Empty(view.Result.Rejected);
    }

    [Fact]
    public void ANoisyWearerLandsCloseAndSaysHowClose()
    {
        // Thirty wearers each half a degree off per press: the errors should be about what the fit claims.
        var fov = new List<float>(); var level = new List<float>(); var lean = new List<float>(); var claimed = new List<float>();
        var jointLevel = new List<float>(); var jointLean = new List<float>();
        int failed = 0;
        for (int seed = 1; seed <= 30; seed++)
        {
            EdgeCalibration ll = new EdgeWearer(TrueFov, TrueLevel, TrueLean, seed) { AimDeg = 0.5f }.Run(EdgeCalibration.LevelLean(50.7f));
            EdgeCalibration va = new EdgeWearer(TrueFov, TrueLevel, TrueLean, seed + 100) { AimDeg = 0.5f }.Run(EdgeCalibration.ViewAngle(50.7f, earlier: ll.Presses));
            if (ll.State != CalibrationState.Done || va.State != CalibrationState.Done) { failed++; continue; }
            level.Add(ll.Result!.LevelDeg - TrueLevel);
            lean.Add(ll.Result.LeanDeg - TrueLean);
            fov.Add(va.Result!.FovDeg - TrueFov);
            claimed.Add(va.Result.FovErrorDeg);
            jointLevel.Add(va.Result.LevelDeg - TrueLevel);
            jointLean.Add(va.Result.LeanDeg - TrueLean);
        }
        output.WriteLine($"0.5 deg aim, 30 wearers, {failed} refused: view angle rms {Rms(fov):F2} (claimed {claimed.Average():F2}); " +
            $"level rms {Rms(level):F2} alone, {Rms(jointLevel):F2} with the view-angle presses; lean rms {Rms(lean):F2} alone, {Rms(jointLean):F2} with");
        Assert.True(failed <= 3);
        Assert.InRange(Rms(fov), 0, 0.8f);
        Assert.InRange(Rms(level), 0, 1f);
        Assert.InRange(Rms(jointLevel), 0, Rms(level));
        Assert.InRange(Rms(lean), 0, 0.3f);
        Assert.InRange(claimed.Average(), Rms(fov) * 0.5f, Rms(fov) * 2f);
    }

    [Fact]
    public void HeadingDriftChangesNothing()
    {
        var still = new EdgeWearer(TrueFov, TrueLevel, TrueLean, 3) { AimDeg = 0.3f }.Run(EdgeCalibration.ViewAngle(50.7f));
        var drifting = new EdgeWearer(TrueFov, TrueLevel, TrueLean, 3) { AimDeg = 0.3f, DriftDegPerSecond = 1f }.Run(EdgeCalibration.ViewAngle(50.7f));
        Assert.Equal(CalibrationState.Done, drifting.State);
        Assert.Equal(still.Result!.FovDeg, drifting.Result!.FovDeg, 2);
        Assert.Equal(still.Result.LevelDeg, drifting.Result.LevelDeg, 2);
    }

    [Fact]
    public void LookingTheWrongWayIsRefusedAndAskedAgain()
    {
        var wearer = new EdgeWearer(TrueFov, TrueLevel, TrueLean) { WrongWayFirst = true };
        var calibration = EdgeCalibration.ViewAngle(50.7f);
        EdgeTarget first = calibration.Current!.Value;
        Assert.Equal(HeadPosture.Up, first.Posture);
        Assert.False(calibration.Press(wearer.HeadFor(first, wrongWay: true)));
        Assert.Contains("DOWN", calibration.Note);
        Assert.Equal(first, calibration.Current);
        Assert.Empty(calibration.Presses);

        wearer.Run(calibration);
        Assert.Equal(CalibrationState.Done, calibration.State);
        Assert.Equal(TrueFov, calibration.Result!.FovDeg, 1);
    }

    [Fact]
    public void PressesMadeTooEarlyAreLeftOutAndRetaken()
    {
        var wearer = new EdgeWearer(TrueFov, TrueLevel, TrueLean, 11) { AimDeg = 0.3f, EarlyShare = 0.2f };
        EdgeCalibration view = wearer.Run(EdgeCalibration.ViewAngle(50.7f));
        output.WriteLine($"{wearer.Presses} presses, {view.Result?.Rejected.Count} left out, view angle {view.Result?.FovDeg:F2} ± {view.Result?.FovErrorDeg:F2} {view.Failure}");
        Assert.Equal(CalibrationState.Done, view.State);
        Assert.NotEmpty(view.Result!.Rejected);
        Assert.True(wearer.Presses > 12);
        Assert.InRange(view.Result.FovDeg, TrueFov - 0.8f, TrueFov + 0.8f);
    }

    [Fact]
    public void AWearerWhoKeepsMissingIsRefused()
    {
        // Every press several degrees out at random: no consistent answer, so nothing is claimed.
        var wearer = new EdgeWearer(TrueFov, TrueLevel, TrueLean, 5) { AimDeg = 4f };
        EdgeCalibration view = wearer.Run(EdgeCalibration.ViewAngle(50.7f));
        Assert.Equal(CalibrationState.Failed, view.State);
        Assert.False(string.IsNullOrEmpty(view.Failure));
    }

    [Fact]
    public void AnImplausibleViewAngleIsRefused()
    {
        EdgeCalibration view = new EdgeWearer(30f, 0, 0).Run(EdgeCalibration.ViewAngle(50.7f));
        Assert.Equal(CalibrationState.Failed, view.State);
        Assert.Contains("outside the believable", view.Failure);
    }

    [Fact]
    public void DrawsOneLineWhereItAsks()
    {
        var calibration = EdgeCalibration.ViewAngle(50.7f);
        EdgeTarget t = calibration.Current!.Value;
        GuideLine line = Assert.Single(calibration.Drawing.Lines);
        Assert.Equal(GuideSpace.Display, line.Space);
        Assert.Equal(t.XPx, (line.From.X + line.To.X) / 2, 1);
        Assert.True(line.To.Y > 400 && line.From.Y < -400);
        Assert.True(t.DrawnTiltDeg < -5); // right line, looking up: drawn leaning in already
        Assert.Contains("right", calibration.Instruction);
    }

    static HeadPose PoseOf(Quaternion q)
    {
        // Same angle definitions as HeadTracker, worked out in the Y-up world.
        Vector3 forward = Vector3.Transform(-Vector3.UnitZ, q), right = Vector3.Transform(Vector3.UnitX, q), up = Vector3.Transform(Vector3.UnitY, q);
        const float r = 180f / MathF.PI;
        return new HeadPose(0, MathF.Asin(forward.Y) * r, MathF.Atan2(-right.Y, up.Y) * r, q);
    }

    static float Rms(List<float> v) => MathF.Sqrt(v.Sum(x => x * x) / Math.Max(1, v.Count));
}
