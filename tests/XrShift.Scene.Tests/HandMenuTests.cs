using System.Numerics;
using XrShift.Scene;

namespace XrShift.Scene.Tests;

public class HandMenuTests
{
    /// <summary>A hand with its wrist at (x, 0.7): fingers straight up (open) or curled back towards the palm (fist).</summary>
    static List<Vector3> Hand(float x, bool open, bool pinch = false)
    {
        var p = new Vector3[21];
        p[0] = new(x, 0.7f, 0);
        // Knuckles across the palm.
        p[5] = new(x - 0.04f, 0.6f, 0); p[9] = new(x, 0.59f, 0); p[13] = new(x + 0.03f, 0.6f, 0); p[17] = new(x + 0.06f, 0.62f, 0);
        foreach ((int knuckle, int joint, int tip) in new[] { (5, 6, 8), (9, 10, 12), (13, 14, 16), (17, 18, 20) })
        {
            Vector3 k = p[knuckle];
            p[joint] = k + new Vector3(0, -0.04f, 0);
            p[joint + 1] = k + new Vector3(0, open ? -0.07f : -0.03f, 0);
            p[tip] = open ? k + new Vector3(0, -0.10f, 0) : k + new Vector3(0, 0.04f, 0);
        }
        p[1] = new(x - 0.05f, 0.68f, 0); p[2] = new(x - 0.08f, 0.65f, 0); p[3] = new(x - 0.10f, 0.62f, 0);
        p[4] = pinch ? p[8] + new Vector3(0.005f, 0, 0) : new(x - 0.13f, 0.60f, 0);
        return p.ToList();
    }

    [Fact]
    public void ShapesAreRecognised()
    {
        Assert.Equal(HandShape.Open, HandShapes.Of(Hand(0.5f, open: true)));
        Assert.Equal(HandShape.Fist, HandShapes.Of(Hand(0.5f, open: false)));
        Assert.Equal(HandShape.None, HandShapes.Of(null));
    }

    [Fact]
    public void AnOpenHandHeldStillOpensTheMenuOnTheMiddleButton()
    {
        var menu = new HandMenu(5);
        for (double t = 0; t < 0.5; t += 1 / 15.0) menu.Step(Hand(0.5f, true), t, false);
        Assert.False(menu.IsOpen);
        menu.Step(Hand(0.5f, true), 0.7, false);
        Assert.True(menu.IsOpen);
        Assert.Equal(2, menu.Highlight);
    }

    [Fact]
    public void AMovingHandDoesNotOpenIt()
    {
        var menu = new HandMenu(5);
        for (int i = 0; i < 20; i++) menu.Step(Hand(0.3f + i * 0.03f, true), i / 15.0, false);
        Assert.False(menu.IsOpen);
    }

    [Fact]
    public void MovingSidewaysLightsAnotherButtonAndAPinchPicksIt()
    {
        var menu = new HandMenu(5);
        menu.Step(Hand(0.5f, true), 0, false);
        menu.Step(Hand(0.5f, true), 0.7, false);
        menu.Step(Hand(0.62f, true), 0.8, false);
        Assert.Equal(4, menu.Highlight);
        Assert.Equal(4, menu.Step(Hand(0.62f, true, pinch: true), 0.9, pinchStarted: true));
        Assert.False(menu.IsOpen);
    }

    [Fact]
    public void AFistOrALostHandClosesIt()
    {
        var menu = new HandMenu(5);
        menu.Step(Hand(0.5f, true), 0, false);
        menu.Step(Hand(0.5f, true), 0.7, false);
        menu.Step(Hand(0.5f, false), 0.8, false);
        Assert.False(menu.IsOpen);

        menu.Step(Hand(0.5f, true), 1, false);
        menu.Step(Hand(0.5f, true), 1.7, false);
        Assert.True(menu.IsOpen);
        menu.Step(null, 2.0, false);
        Assert.True(menu.IsOpen);
        menu.Step(null, 2.8, false);
        Assert.False(menu.IsOpen);
    }
}
