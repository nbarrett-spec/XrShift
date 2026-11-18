using System.Numerics;

namespace XrShift.Scene;

/// <summary>What one camera frame's hand does to the mouse.</summary>
/// <param name="Move">How far the pointer should move, in camera-picture units (1 = the picture's width).</param>
/// <param name="Press">True: press the button. False: let it go. Null: no change.</param>
public readonly record struct HandAction(Vector2 Move, bool? Press);

/// <summary>
/// Turns a tracked hand into mouse input, frame by frame. The middle of the palm steers the pointer like a
/// touchpad (moving the hand moves the pointer by as much); touching thumb and index finger together presses
/// the button, opening them lets it go, so a pinch is a click and a pinch held while moving is a drag. Pinch
/// and release use different thresholds so a hand on the edge does not chatter. The palm, not the fingertips,
/// steers, so pinching does not pull the pointer off its target.
/// </summary>
public sealed class HandGesture
{
    /// <summary>Thumb tip to index tip, as a share of the hand's size, below which the hand counts as pinching.</summary>
    public float PinchAt { get; init; } = 0.30f;
    /// <summary>...and above which it counts as open again.</summary>
    public float ReleaseAt { get; init; } = 0.45f;
    /// <summary>Palm movements smaller than this (picture widths) are tracking jitter and are ignored.</summary>
    public float Deadband { get; init; } = 0.0015f;
    /// <summary>A jump bigger than this between frames is a hand found again somewhere else, not a movement.</summary>
    public float MaxStep { get; init; } = 0.12f;

    Vector2? _palm;
    public bool Pinching { get; private set; }

    /// <param name="points">MediaPipe's 21 hand points (x, y in 0..1 across the picture), or null when no hand is seen.</param>
    public HandAction Step(IReadOnlyList<Vector3>? points)
    {
        if (points is null || points.Count < 21)
        {
            _palm = null;
            if (!Pinching) return new HandAction(Vector2.Zero, null);
            Pinching = false; // a hand that leaves the view lets go
            return new HandAction(Vector2.Zero, false);
        }

        static Vector2 Flat(Vector3 p) => new(p.X, p.Y);
        float size = Vector2.Distance(Flat(points[0]), Flat(points[9])); // wrist to the middle finger's knuckle
        if (size < 0.02f) return new HandAction(Vector2.Zero, null);

        float pinch = Vector2.Distance(Flat(points[4]), Flat(points[8])) / size;
        bool? press = null;
        if (!Pinching && pinch < PinchAt) { Pinching = true; press = true; }
        else if (Pinching && pinch > ReleaseAt) { Pinching = false; press = false; }

        // The middle of the palm: wrist and the four knuckles.
        Vector2 palm = (Flat(points[0]) + Flat(points[5]) + Flat(points[9]) + Flat(points[13]) + Flat(points[17])) / 5f;
        Vector2 move = Vector2.Zero;
        if (_palm is { } before)
        {
            // Lightly smoothed, so a still hand gives a still pointer.
            Vector2 smoothed = Vector2.Lerp(before, palm, 0.6f);
            Vector2 step = smoothed - before;
            if (step.Length() > MaxStep) { _palm = palm; return new HandAction(Vector2.Zero, press); }
            if (step.Length() >= Deadband) { move = step; _palm = smoothed; }
        }
        else
        {
            _palm = palm;
        }
        return new HandAction(move, press);
    }
}
