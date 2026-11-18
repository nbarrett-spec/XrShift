using System.Numerics;
using XrShift.Scene;
using XrShift.Tracking;

namespace XrShift.Scene.Tests;

public class CameraTests
{
    const float DegToRad = MathF.PI / 180f;
    static readonly DisplayOptics Optics = DisplayOptics.OnePro;

    static Quaternion Head(float yawLeftDeg, float pitchUpDeg = 0) =>
        Quaternion.CreateFromYawPitchRoll(yawLeftDeg * DegToRad, pitchUpDeg * DegToRad, 0);

    static Vector2 Project(Vector3 world, Quaternion head)
    {
        Assert.True(Camera.TryProject(world, head, Optics, out Vector2 pixel));
        return pixel;
    }

    static float PixelsOffCentre(float degrees) =>
        MathF.Tan(degrees * DegToRad) / MathF.Tan(Optics.HorizontalFovDeg / 2 * DegToRad) * Optics.Width / 2;

    [Fact]
    public void OpticsMatchA57DegreeDiagonal()
    {
        Assert.InRange(Optics.VerticalFovDeg, 29.6f, 30.0f);
        float diagonal = 2 * MathF.Atan(MathF.Sqrt(
            MathF.Pow(MathF.Tan(Optics.HorizontalFovDeg / 2 * DegToRad), 2) +
            MathF.Pow(MathF.Tan(Optics.VerticalFovDeg / 2 * DegToRad), 2))) / DegToRad;
        Assert.InRange(diagonal, 56.8f, 57.2f);
        Assert.InRange(Optics.PixelsPerDegree, 35f, 36f);
    }

    [Fact]
    public void StraightAheadLandsInTheMiddle()
    {
        Vector2 p = Project(new Vector3(0, 0, -2), Quaternion.Identity);
        Assert.InRange(p.X, 959.9f, 960.1f);
        Assert.InRange(p.Y, 539.9f, 540.1f);
    }

    [Fact]
    public void TurningLeftSlidesTheWorldRight()
    {
        Vector2 p = Project(new Vector3(0, 0, -2), Head(10));
        Assert.InRange(p.X - 960, PixelsOffCentre(10) - 0.5f, PixelsOffCentre(10) + 0.5f);
        Assert.InRange(p.Y, 539.5f, 540.5f);
    }

    [Fact]
    public void LookingUpSlidesTheWorldDown()
    {
        Vector2 p = Project(new Vector3(0, 0, -2), Head(0, 8));
        Assert.InRange(p.X, 959.5f, 960.5f);
        Assert.InRange(p.Y - 540, PixelsOffCentre(8) - 0.5f, PixelsOffCentre(8) + 0.5f);
    }

    [Fact]
    public void TheEdgeOfTheFieldOfViewIsTheEdgeOfTheFrame()
    {
        float half = Optics.HorizontalFovDeg / 2 * DegToRad;
        Vector2 p = Project(new Vector3(MathF.Sin(half), 0, -MathF.Cos(half)) * 3, Quaternion.Identity);
        Assert.InRange(p.X, 1919.5f, 1920.5f);
    }

    [Fact]
    public void WhatIsBehindIsNotDrawn()
    {
        Assert.False(Camera.TryProject(new Vector3(0, 0, 2), Quaternion.Identity, Optics, out _));
        Assert.True(Camera.TryProject(new Vector3(0, 0, 2), Head(180), Optics, out _));
    }

    [Fact]
    public void TheTrackersPoseKeepsAScreenWhereItWasPut()
    {
        // The whole chain: fake glasses -> tracker -> camera. Head turns 20 left then 10 up.
        var sim = new ImuSimulator();
        var tracker = new HeadTracker();
        for (int i = 0; i < 500; i++) tracker.Update(sim.Step());
        tracker.GetPose();
        for (int i = 0; i < 500; i++) tracker.Update(sim.Step(yawLeft: 40 * DegToRad));
        for (int i = 0; i < 500; i++) tracker.Update(sim.Step(pitchUp: 20 * DegToRad));
        Quaternion head = tracker.GetPose().Orientation;

        // A screen placed exactly where the head now points is dead centre.
        var there = new Panel { YawDeg = 20, PitchDeg = 10 };
        Vector2 centre = Project(there.Centre, head);
        Assert.InRange(centre.X, 957f, 963f);
        Assert.InRange(centre.Y, 537f, 543f);

        // The screen that was straight ahead at the start is now down and to the right.
        Vector2 start = Project(new Panel().Centre, head);
        Assert.True(start.X > 960 + 500, $"x {start.X}");
        Assert.True(start.Y > 540 + 250, $"y {start.Y}");
    }
}

public class PanelTests
{
    const float DegToRad = MathF.PI / 180f;

    static void AssertClose(Vector3 expected, Vector3 actual, float tolerance = 1e-4f) =>
        Assert.True((expected - actual).Length() < tolerance, $"expected {expected}, got {actual}");

    [Fact]
    public void AFlatScreenIsARectangleFacingTheWearer()
    {
        Mesh mesh = PanelMesh.Build(new Panel { Distance = 2, Width = 1.6f, Height = 0.9f });
        Assert.Equal(4, mesh.Vertices.Length);
        Assert.Equal(6, mesh.Indices.Length);
        AssertClose(new Vector3(-0.8f, 0.45f, -2), mesh.Vertices[0].Position); // top-left
        AssertClose(new Vector3(0.8f, -0.45f, -2), mesh.Vertices[3].Position); // bottom-right
        Assert.Equal(new Vector2(0, 0), mesh.Vertices[0].Uv);
        Assert.Equal(new Vector2(1, 1), mesh.Vertices[3].Uv);
    }

    [Fact]
    public void YawAndPitchPutTheScreenWhereTheHeadWouldLook()
    {
        var panel = new Panel { YawDeg = 30, PitchDeg = 0, Distance = 2 };
        AssertClose(new Vector3(-1, 0, -MathF.Sqrt(3)), panel.Centre); // 30 degrees to the left

        var above = new Panel { PitchDeg = 20, Distance = 1 };
        AssertClose(new Vector3(0, MathF.Sin(20 * DegToRad), -MathF.Cos(20 * DegToRad)), above.Centre);
    }

    [Fact]
    public void RollLeansTheTopToTheRight()
    {
        var panel = new Panel { RollDeg = 90, Width = 2, Height = 1 };
        AssertClose(new Vector3(0.5f, 0, -2), panel.WorldPoint(0, 0.5f)); // top edge is now on the right
    }

    [Fact]
    public void ACurveCentredOnTheWearerKeepsEveryPointAtTheSameDistance()
    {
        // 90 degrees of arc, 2 m away: the width along the arc is a quarter circle.
        var panel = new Panel { Distance = 2, Width = MathF.PI, Height = 1, CurveDeg = 90, YawDeg = 15 };
        Mesh mesh = PanelMesh.Build(panel);
        Assert.True(mesh.Vertices.Length > 40);
        foreach (MeshVertex v in mesh.Vertices)
            Assert.InRange(new Vector2(v.Position.X, v.Position.Z).Length(), 1.999f, 2.001f);
        Assert.InRange(panel.AngularWidthDeg, 89.9f, 90.1f);
    }

    [Fact]
    public void AFlatScreenLooksNarrowerThanItsArcWouldSuggest()
    {
        var flat = new Panel { Distance = 2, Width = 1.6f };
        Assert.InRange(flat.AngularWidthDeg, 43.5f, 43.7f); // 2 * atan(0.8 / 2)
    }

    [Fact]
    public void ARingGoesAllTheWayRoundAndCloses()
    {
        Panel ring = Panel.Ring(10, 0.7f);
        Mesh mesh = PanelMesh.Build(ring);
        Assert.InRange(ring.AngularWidthDeg, 359.5f, 360.5f);
        AssertClose(mesh.Vertices[0].Position, mesh.Vertices[^2].Position, 1e-3f); // seam, straight behind
        AssertClose(new Vector3(0, 0.35f, 10), mesh.Vertices[0].Position, 1e-3f);

        // Half way along is straight ahead.
        MeshVertex middle = mesh.Vertices[mesh.Vertices.Length / 2 - (mesh.Vertices.Length / 2 % 2)];
        Assert.InRange(middle.Uv.X, 0.49f, 0.51f);
        Assert.InRange(middle.Position.Z, -10.01f, -9.99f);
    }

    [Theory]
    [InlineData(0f)]    // flat
    [InlineData(60f)]   // curved
    [InlineData(150f)]  // wrapped well round
    public void ALineOfSightFindsThePointItPassesThrough(float curveDeg)
    {
        var panel = new Panel { YawDeg = 35, PitchDeg = -12, RollDeg = 8, Distance = 2.5f, Width = 2.2f, Height = 1.1f, CurveDeg = curveDeg };
        foreach ((float s, float t) in ((float, float)[])[(0, 0), (0.4f, 0.3f), (-0.45f, -0.2f), (0.1f, -0.49f)])
        {
            Vector3 towards = Vector3.Normalize(panel.WorldPoint(s, t));
            Assert.True(panel.TryHit(towards, out float hitS, out float hitT));
            Assert.InRange(hitS, s - 0.002f, s + 0.002f);
            Assert.InRange(hitT, t - 0.002f, t + 0.002f);
        }
    }

    [Fact]
    public void LookingPastTheEdgeReportsAPointOffTheScreen()
    {
        var panel = new Panel { Distance = 2, Width = 1.6f, Height = 0.9f };
        Assert.True(panel.TryHit(Vector3.Normalize(new Vector3(1.2f, 0, -2)), out float s, out _));
        Assert.InRange(s, 0.74f, 0.76f); // 1.2 m right of centre on a 1.6 m screen

        Assert.False(panel.TryHit(new Vector3(0, 0, 1), out _, out _)); // looking the other way
    }

    [Fact]
    public void EveryTriangleUsesRealVertices()
    {
        Mesh mesh = PanelMesh.Build(new Panel { CurveDeg = 60, Width = 2 });
        Assert.Equal(0, mesh.Indices.Length % 3);
        Assert.All(mesh.Indices, i => Assert.InRange(i, 0, mesh.Vertices.Length - 1));
    }

    [Fact]
    public void AHeadLockedRectangleCoversTheAnglesAsked()
    {
        Mesh mesh = PanelMesh.HeadLocked(0, -10, 20, 4);
        Vector3 topLeft = mesh.Vertices[0].Position, bottomRight = mesh.Vertices[3].Position;
        Assert.InRange(MathF.Atan2(topLeft.X, -topLeft.Z) / DegToRad, -10.01f, -9.99f);
        Assert.InRange(MathF.Atan2(bottomRight.X, -bottomRight.Z) / DegToRad, 9.99f, 10.01f);
        Assert.InRange(MathF.Atan2(topLeft.Y, -topLeft.Z) / DegToRad, -8.01f, -7.99f);
        Assert.InRange(MathF.Atan2(bottomRight.Y, -bottomRight.Z) / DegToRad, -12.01f, -11.99f);
    }
}
