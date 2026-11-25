using System.Numerics;
using XrShift.Tracking;

namespace XrShift.Tracking.Tests;

public class SetupRunTests
{
    const float DegToRad = MathF.PI / 180f;

    static readonly SetupValues Was = new(null, null, 50.7f, 0, 0);

    static void Feed(SetupRun run, ImuSimulator sim, float seconds, float pitchUp = 0)
    {
        for (int i = 0; i < (int)MathF.Round(seconds * 1000); i++) run.Add(sim.Step(0, pitchUp * DegToRad));
    }

    [Fact]
    public void TheModeCheckComesFirstAndMeasuresNothing()
    {
        var sim = new ImuSimulator();
        var run = new SetupRun(new SetupOptions(AxisMap.Default), Was);
        Assert.True(run.CheckingMode);
        Assert.Equal(SetupRun.ModeCheckText, run.Screen.Instruction);
        Assert.Equal(4, run.Screen.Drawing.Lines.Count); // the box
        Assert.All(run.Screen.Drawing.Lines, l => Assert.Equal(GuideSpace.Display, l.Space));

        Feed(run, sim, 7); // lying still: would finish the rest step, but the check has not been passed
        Assert.Equal(SetupStepStatus.Running, run.Setup.StatusOf(SetupStepKind.Rest));
        Assert.Null(run.TakeChanges());

        Assert.True(run.Confirm(Quaternion.Identity));
        Assert.False(run.CheckingMode);
        Assert.Contains("set them on the desk", run.Screen.Instruction);
        Feed(run, sim, 6);
        Assert.Equal(SetupStepStatus.Done, run.Setup.StatusOf(SetupStepKind.Rest));
    }

    [Fact]
    public void EachFinishedStepIsAppliedOnceAndSaidInPlainWords()
    {
        var sim = new ImuSimulator();
        var run = new SetupRun(new SetupOptions(AxisMap.Default, StartAt: SetupStepKind.LevelLean), Was, checkMode: false);
        Feed(run, sim, 1.2f);
        Assert.Equal("Level and lean: 1 of 6", run.Progress);

        var wearer = new EdgeWearer(48f, 2f, -1f);
        run.Confirm(wearer.HeadFor(run.Setup.Target!.Value));
        run.Confirm(wearer.HeadFor(run.Setup.Target!.Value));
        Assert.Equal("Level and lean: 3 of 6", run.Progress);
        Assert.Null(run.TakeChanges());
        while (run.Setup.Target is { } t) run.Confirm(wearer.HeadFor(t));

        SetupValues now = run.TakeChanges()!;
        Assert.InRange(now.LevelDeg, 1.7f, 2.3f); // the view angle is only assumed here
        Assert.InRange(now.LeanDeg, -1.2f, -0.8f);
        Assert.Equal(50.7f, now.FovDeg);
        Assert.Null(run.TakeChanges()); // applied once
        Assert.Equal(Was, run.Before);
        string said = Assert.Single(run.Results);
        Assert.StartsWith($"Level {now.LevelDeg:F1} degrees, was 0.0. Lean {now.LeanDeg:F1}, was 0.0. The 6 presses agree within", said);
        Assert.Equal([said], run.Screen.Results);

        Feed(run, sim, 1.2f);
        while (run.Setup.Target is { } t) run.Confirm(wearer.HeadFor(t));
        Assert.True(run.Finished, run.Progress + " " + run.Screen.Instruction + " " + run.Screen.Note);
        now = run.TakeChanges()!;
        Assert.InRange(now.FovDeg, 47.7f, 48.3f);
        Assert.Contains("View angle", run.Results[^1]);
        Assert.Contains($", was {50.7f:F1} (give or take", run.Results[^1]);
        Assert.True(run.Finished);
        Assert.Equal(SetupRun.FinishedText, run.Screen.Instruction);
        Assert.Equal(2, run.Screen.Results.Count); // all of them at the end
    }

    [Fact]
    public void ARefusedStepChangesNothingAndSaysSo()
    {
        var sim = new ImuSimulator();
        var run = new SetupRun(new SetupOptions(AxisMap.Default, StartAt: SetupStepKind.Axes), Was, checkMode: false);
        Feed(run, sim, 2);
        Feed(run, sim, 1, pitchUp: -30); // nods down
        Feed(run, sim, 2);
        Assert.Null(run.TakeChanges());
        Assert.Equal("Sensor axes: refused", run.Progress);
        Assert.Equal(SetupRun.RefusedNote, run.Screen.Note);
        Assert.StartsWith("Sensor axes: refused, nothing changed. That read as looking DOWN", Assert.Single(run.Results));
        Assert.Null(run.TakeChanges());
        Assert.Single(run.Results); // said once

        run.Retry();
        Feed(run, sim, 1, pitchUp: 30);
        Feed(run, sim, 2);
        Assert.Equal("Sensor axes: 2 of 2", run.Progress);
        Feed(run, sim, 1, pitchUp: 30);
        Feed(run, sim, 2);
        Assert.NotNull(run.TakeChanges()!.Axes);
        Assert.StartsWith("Sensor axes measured from a nod of 30 degrees", run.Results[^1]);
    }

    [Fact]
    public void SkippingTheModeCheckGoesStraightToTheFirstStep()
    {
        var run = new SetupRun(new SetupOptions(AxisMap.Default, StartAt: SetupStepKind.ViewAngle), Was);
        run.Skip();
        Assert.False(run.CheckingMode);
        Assert.Equal("View angle: look straight ahead first", run.Progress);
    }

    [Theory]
    [InlineData("rest", SetupStepKind.Rest)]
    [InlineData("axes", SetupStepKind.Axes)]
    [InlineData("Level", SetupStepKind.LevelLean)]
    [InlineData("view", SetupStepKind.ViewAngle)]
    public void ReadsWhereToStart(string text, SetupStepKind step) => Assert.Equal(step, SetupRun.ParseStep(text));

    [Fact]
    public void DisplayPointsLandOnPicturePixelsFromTheMiddle()
    {
        // x right, y up, from the middle: 600 px right of the middle of 1920x1080 is column 1560 of the picture.
        Assert.Equal(new Vector2(1560, 540), GuideDrawing.ToPicture(new Vector2(600, 0)));
        Assert.Equal(new Vector2(144, 54), GuideDrawing.ToPicture(new Vector2(-816, 486)));
        Assert.Equal(new Vector2(960, 1080), GuideDrawing.ToPicture(new Vector2(0, -540)));
    }

    [Fact]
    public void RoomDirectionsLandWhereTheViewSeesThem()
    {
        Quaternion ahead = Quaternion.Identity;
        Assert.Equal(new Vector2(960, 540), GuideDrawing.ToPicture(Vector2.Zero, ahead, 50.7f));
        // Half the view angle to the right is the right edge; up by the vertical half-angle is the top.
        Vector2 right = GuideDrawing.ToPicture(new Vector2(-25.35f, 0), ahead, 50.7f)!.Value;
        Assert.Equal(1920, right.X, 1);
        float halfUp = MathF.Atan(MathF.Tan(25.35f * DegToRad) * 1080 / 1920) / DegToRad;
        Assert.Equal(0, GuideDrawing.ToPicture(new Vector2(0, halfUp), ahead, 50.7f)!.Value.Y, 1);
        // Turned 30 degrees left, a mark 30 degrees left is in the middle; one behind is not drawn.
        Quaternion left = Quaternion.CreateFromYawPitchRoll(30 * DegToRad, 0, 0);
        Assert.Equal(960, GuideDrawing.ToPicture(new Vector2(30, 0), left, 50.7f)!.Value.X, 1);
        Assert.Null(GuideDrawing.ToPicture(new Vector2(180, 0), ahead, 50.7f));
    }

    [Fact]
    public void SavedAxesAndGyroGoBackExactlyAsTheyWere()
    {
        string folder = Path.Combine(Path.GetTempPath(), "xrshift-setup-test-" + Guid.NewGuid().ToString("N"));
        string? old = Environment.GetEnvironmentVariable("XRSHIFT_CONFIG");
        Environment.SetEnvironmentVariable("XRSHIFT_CONFIG", folder);
        try
        {
            Assert.Null(TrackingConfig.SavedAxes());
            var bias = new Vector3(0.001f, -0.002f, 0.003f);
            TrackingConfig.Restore(AxisMap.Default, bias); // what setup saves
            Assert.True((AxisMap.Default.Up - TrackingConfig.SavedAxes()!.Up).Length() < 1e-3f); // saved to four places
            Assert.True(TrackingConfig.TryLoadGyroBias(out Vector3 back) && back == bias);

            TrackingConfig.Restore(null, null); // undo to "none saved"
            Assert.Null(TrackingConfig.SavedAxes());
            Assert.False(TrackingConfig.TryLoadGyroBias(out _));
            Assert.False(File.Exists(TrackingConfig.AxesPath) || File.Exists(TrackingConfig.GyroPath));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XRSHIFT_CONFIG", old);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}
