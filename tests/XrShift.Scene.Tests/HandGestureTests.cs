using System.Numerics;
using XrShift.Scene;

namespace XrShift.Scene.Tests;

public class HandGestureTests
{
    /// <summary>A flat hand with its palm at (x, y); the thumb tip this far from the index tip, as a share of the hand's size (0.2).</summary>
    static List<Vector3> Hand(float x, float y, float pinchShare)
    {
        var points = Enumerable.Range(0, 21).Select(_ => new Vector3(x, y, 0)).ToList();
        points[0] = new Vector3(x, y + 0.1f, 0);        // wrist
        points[9] = new Vector3(x, y - 0.1f, 0);        // middle knuckle: size 0.2
        points[5] = new Vector3(x - 0.03f, y - 0.09f, 0);
        points[13] = new Vector3(x + 0.03f, y - 0.09f, 0);
        points[17] = new Vector3(x + 0.06f, y - 0.07f, 0);
        points[8] = new Vector3(x - 0.05f, y - 0.2f, 0); // index tip
        points[4] = points[8] + new Vector3(pinchShare * 0.2f, 0, 0);
        return points;
    }

    [Fact]
    public void APinchPressesAndOpeningLetsGoWithoutChattering()
    {
        var g = new HandGesture();
        Assert.Null(g.Step(Hand(0.5f, 0.5f, 1.0f)).Press);
        Assert.True(g.Step(Hand(0.5f, 0.5f, 0.2f)).Press);
        Assert.Null(g.Step(Hand(0.5f, 0.5f, 0.38f)).Press); // between the two thresholds: still pinching
        Assert.True(g.Pinching);
        Assert.False(g.Step(Hand(0.5f, 0.5f, 0.6f)).Press);
        Assert.False(g.Pinching);
    }

    [Fact]
    public void ThePalmSteersAndAStillHandHoldsStill()
    {
        var g = new HandGesture();
        g.Step(Hand(0.5f, 0.5f, 1f));
        Assert.Equal(Vector2.Zero, g.Step(Hand(0.5f, 0.5f, 1f)).Move);
        Assert.Equal(Vector2.Zero, g.Step(Hand(0.5005f, 0.5f, 1f)).Move); // jitter
        HandAction right = g.Step(Hand(0.53f, 0.5f, 1f));
        Assert.True(right.Move.X > 0.01f && MathF.Abs(right.Move.Y) < 1e-4f);
    }

    [Fact]
    public void AHandFoundAgainElsewhereDoesNotFlingThePointer()
    {
        var g = new HandGesture();
        g.Step(Hand(0.2f, 0.5f, 1f));
        Assert.Equal(Vector2.Zero, g.Step(Hand(0.8f, 0.5f, 1f)).Move);
    }

    [Fact]
    public void LosingTheHandMidPinchLetsGo()
    {
        var g = new HandGesture();
        g.Step(Hand(0.5f, 0.5f, 0.1f));
        Assert.False(g.Step(null).Press);
        Assert.Null(g.Step(null).Press);
    }
}
