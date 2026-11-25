using System.Numerics;
using XrShift.Tracking;

namespace XrShift.Tracking.Tests;

public class AxisCheckTests
{
    const float DegToRad = MathF.PI / 180f;

    static void Feed(AxisCheck check, ImuSimulator sim, float seconds, float pitchUp = 0, float yawLeft = 0)
    {
        for (int i = 0; i < (int)MathF.Round(seconds * 1000); i++) check.Add(sim.Step(yawLeft * DegToRad, pitchUp * DegToRad));
    }

    static readonly AxisMap Tilted = AxisMap.TryFromRightUp(new Vector3(0.999f, -0.02f, -0.04f), new Vector3(0.003f, -0.88f, 0.475f), out AxisMap m) ? m : AxisMap.Default;

    [Fact]
    public void MeasuresTheMountingFromAStillLookAndANod()
    {
        var sim = new ImuSimulator(Tilted) { GyroBias = new Vector3(0.002f, -0.001f, 0.0005f) };
        var check = new AxisCheck();
        Feed(check, sim, 2);
        Assert.Equal(AxisCheckStage.LookUp, check.Stage);
        Feed(check, sim, 1, pitchUp: 30);
        Feed(check, sim, 2);

        Assert.Equal(AxisCheckStage.Done, check.Stage);
        AxisCheckResult r = check.Result!;
        Assert.Null(r.Problem);
        Assert.True((r.Map.Up - Tilted.Up).Length() < 0.002f);
        Assert.True((r.Map.Right - Tilted.Right).Length() < 0.002f);
        Assert.InRange(r.PitchDeg, 29, 31);
        Assert.InRange(r.GyroPitchDeg, 29, 31);
        Assert.InRange(r.AgreeDeg, 0, 2);
    }

    [Fact]
    public void ANodDownIsCaught()
    {
        var sim = new ImuSimulator();
        var check = new AxisCheck();
        Feed(check, sim, 2);
        Feed(check, sim, 1, pitchUp: -30);
        Feed(check, sim, 2);
        Assert.Equal(AxisCheckStage.Failed, check.Stage);
        Assert.Contains("looking DOWN", check.Failure);
        Assert.NotNull(check.Result);
    }

    [Fact]
    public void GivesUpWhenNeverStill()
    {
        var sim = new ImuSimulator();
        var check = new AxisCheck();
        for (int i = 0; i < 16000; i++) check.Add(sim.Step(MathF.Sin(i * 0.003f) * 0.5f, MathF.Cos(i * 0.004f) * 0.5f));
        Assert.Equal(AxisCheckStage.Failed, check.Stage);
        Assert.Equal("Never steady for 1.5 seconds. Nothing saved; run it again.", check.Failure);
    }

    [Fact]
    public void AHoldThatIsNotLevelIsNotTakenAsLevel()
    {
        var sim = new ImuSimulator();
        Vector3 levelUp = AxisMap.Default.Up;
        var check = new AxisCheck(levelOk: r => Vector3.Dot(Vector3.Normalize(r.AccelMean), levelUp) > MathF.Cos(7 * DegToRad));
        Feed(check, sim, 1, pitchUp: 30);
        Feed(check, sim, 14.5f);
        Assert.Equal(AxisCheckStage.Failed, check.Stage);
        Assert.Contains("not with the head level", check.Failure);
    }
}

public class GlassesSetupTests
{
    const float DegToRad = MathF.PI / 180f;

    static void Feed(GlassesSetup setup, ImuSimulator sim, float seconds, float pitchUp = 0, float yawLeft = 0)
    {
        for (int i = 0; i < (int)MathF.Round(seconds * 1000); i++) setup.Add(sim.Step(yawLeft * DegToRad, pitchUp * DegToRad));
    }

    static void PressThrough(GlassesSetup setup, EdgeWearer wearer)
    {
        for (int i = 0; i < 100 && setup.Target is { } t; i++) setup.Press(wearer.HeadFor(t));
    }

    [Fact]
    public void RunsEveryStepInTurn()
    {
        var bias = new Vector3(0.003f, -0.002f, 0.001f);
        var sim = new ImuSimulator { GyroBias = bias };
        var setup = new GlassesSetup(new SetupOptions(AxisMap.Default));

        Assert.Equal(SetupStepKind.Rest, setup.Step);
        Assert.Equal(SetupWait.DeskStill, setup.Waiting);
        Feed(setup, sim, 6); // lying still on the desk
        Assert.Equal(SetupStepKind.Axes, setup.Step);
        Assert.True((setup.Results.GyroBias!.Value - bias).Length() < 1e-4f);

        // Still lying there: the desk must not pass for a level head.
        Assert.Contains("Put the glasses on", setup.Instruction);
        Feed(setup, sim, 3);
        Assert.Contains("Put the glasses on", setup.Instruction);

        Feed(setup, sim, 0.5f, pitchUp: 20); // picked up and put on
        Feed(setup, sim, 0.5f, pitchUp: -20);
        Feed(setup, sim, 2);
        Assert.Contains("Nod UP", setup.Instruction);
        Feed(setup, sim, 1, pitchUp: 30);
        Feed(setup, sim, 2);
        Assert.Equal(SetupStepStatus.Done, setup.StatusOf(SetupStepKind.Axes));

        // Still looking up from the nod: level and lean must wait for the head to come back.
        Assert.Equal(SetupStepKind.LevelLean, setup.Step);
        Assert.Equal(SetupWait.Neutral, setup.Waiting);
        Feed(setup, sim, 3);
        Assert.Equal(SetupWait.Neutral, setup.Waiting);
        Assert.Equal(GuideDrawing.CentreCross, setup.Drawing);
        Feed(setup, sim, 1, pitchUp: -30);
        Feed(setup, sim, 1.2f);
        Assert.Equal(SetupWait.Press, setup.Waiting);
        Assert.Single(setup.Drawing.Lines);

        var wearer = new EdgeWearer(48f, 1.5f, -0.8f) { AimDeg = 0.3f };
        PressThrough(setup, wearer);
        Assert.Equal(SetupStepStatus.Done, setup.StatusOf(SetupStepKind.LevelLean));

        Assert.Equal(SetupWait.Neutral, setup.Waiting); // back to level again before the view angle
        Feed(setup, sim, 1.2f);
        PressThrough(setup, wearer);

        Assert.True(setup.Finished);
        Assert.Equal(SetupWait.Nothing, setup.Waiting);
        SetupResults r = setup.Results;
        Assert.InRange(r.FovDeg!.Value, 47.3f, 48.7f);
        Assert.InRange(r.LevelDeg!.Value, 0.8f, 2.2f);
        Assert.InRange(r.LeanDeg!.Value, -1.2f, -0.4f);
    }

    [Fact]
    public void TheAxisCheckWaitsForTheHeadToComeBackFromAPreviousLookUp()
    {
        // The real first attempt: the wearer was still looking up from a try before, and that was taken as level.
        var sim = new ImuSimulator();
        var setup = new GlassesSetup(new SetupOptions(AxisMap.Default, StartAt: SetupStepKind.Axes));
        Feed(setup, sim, 1, pitchUp: 30);
        Feed(setup, sim, 4);
        Assert.Contains("Look straight ahead", setup.Instruction); // a still look up is not taken

        Feed(setup, sim, 1, pitchUp: -30);
        Feed(setup, sim, 2);
        Assert.Contains("Nod UP", setup.Instruction);
        Feed(setup, sim, 1, pitchUp: 30);
        Feed(setup, sim, 2);
        Assert.Equal(SetupStepStatus.Done, setup.StatusOf(SetupStepKind.Axes));
        Assert.True((setup.Results.Axes!.Map.Up - AxisMap.Default.Up).Length() < 0.002f);
    }

    [Fact]
    public void AFailedStepWaitsAndCanBeRetriedOrSkipped()
    {
        var sim = new ImuSimulator();
        var setup = new GlassesSetup(new SetupOptions(AxisMap.Default, StartAt: SetupStepKind.Axes));
        Feed(setup, sim, 2);
        Feed(setup, sim, 1, pitchUp: -30); // nods down
        Feed(setup, sim, 2);
        Assert.Equal(SetupStepStatus.Failed, setup.StatusOf(SetupStepKind.Axes));
        Assert.Equal(SetupWait.Nothing, setup.Waiting);
        Assert.Contains("looking DOWN", setup.FailureOf(SetupStepKind.Axes));
        Assert.Contains("looking DOWN", setup.Instruction);
        Assert.NotNull(setup.AxisAttempt);
        Assert.Null(setup.Results.Axes);

        setup.Retry();
        Assert.Equal(SetupStepStatus.Running, setup.StatusOf(SetupStepKind.Axes));
        Feed(setup, sim, 1, pitchUp: 30); // back to level first
        Feed(setup, sim, 2);
        Feed(setup, sim, 1, pitchUp: 30);
        Feed(setup, sim, 2);
        Assert.Equal(SetupStepStatus.Done, setup.StatusOf(SetupStepKind.Axes));

        setup.Skip(); // level and lean
        Assert.Equal(SetupStepStatus.Skipped, setup.StatusOf(SetupStepKind.LevelLean));
        Assert.Equal(SetupStepKind.ViewAngle, setup.Step);
        setup.Skip();
        Assert.True(setup.Finished);
        Assert.Null(setup.Results.FovDeg);
    }

    [Fact]
    public void ResumesAtALaterStepAndRefusesAPressMadeLookingTheWrongWay()
    {
        var sim = new ImuSimulator();
        var setup = new GlassesSetup(new SetupOptions(AxisMap.Default, StartAt: SetupStepKind.ViewAngle));
        Assert.Equal(SetupStepStatus.Skipped, setup.StatusOf(SetupStepKind.Rest));
        Assert.Equal(SetupWait.Neutral, setup.Waiting);
        Feed(setup, sim, 1.2f);
        Assert.Equal(SetupWait.Press, setup.Waiting);

        EdgeTarget t = setup.Target!.Value;
        Assert.Equal(HeadPosture.Up, t.Posture);
        var wearer = new EdgeWearer(50.7f, 0, 0);
        Assert.False(setup.Press(wearer.HeadFor(t, wrongWay: true)));
        Assert.Contains("DOWN", setup.Note);
        PressThrough(setup, wearer);
        Assert.True(setup.Finished);
        Assert.Equal(50.7f, setup.Results.FovDeg!.Value, 1);
    }

    [Fact]
    public void OnlyTheEdgeStepsNeedDrawing()
    {
        Assert.False(GlassesSetup.NeedsDrawing(SetupStepKind.Rest));
        Assert.False(GlassesSetup.NeedsDrawing(SetupStepKind.Axes));
        Assert.True(GlassesSetup.NeedsDrawing(SetupStepKind.LevelLean));
        Assert.True(GlassesSetup.NeedsDrawing(SetupStepKind.ViewAngle));
    }
}
