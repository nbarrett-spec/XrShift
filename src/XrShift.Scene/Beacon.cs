using System.Drawing;
using System.Numerics;

namespace XrShift.Scene;

/// <summary>
/// A spot in the room to point the wearer's eye to: where the mouse pointer is, or a window that has just opened.
/// <paramref name="Label"/> names the edge arrow drawn while the spot is out of view (empty: no arrow);
/// <paramref name="Ring"/> draws a ring around it.
/// </summary>
public readonly record struct Mark(Vector3 Point, string Label, bool Ring);

/// <summary>Where a point of the Windows desktop is in the room, and whether the wearer can see it.</summary>
public static class RoomSpot
{
    /// <summary>
    /// Where <paramref name="p"/> is in the room: on the first shown screen whose part of the desktop
    /// (<c>On</c>: a monitor, or a pinned window wherever it really is) holds it. FindGaze the other way round.
    /// Null when no shown screen holds it.
    /// shortcut: a screen that follows the head is skipped, so a pointer on one counts as nowhere; give it a head-locked spot if that is ever used for a monitor.
    /// </summary>
    public static Vector3? Of(Point p, IEnumerable<(ScreenSpec Screen, Rectangle On)> shown, DisplayOptics optics)
    {
        foreach ((ScreenSpec spec, Rectangle on) in shown)
        {
            if (!spec.Visible || spec.FollowsMe || on.Width < 2 || on.Height < 2 || !on.Contains(p)) continue;
            float s = (p.X - on.Left + 0.5f) / on.Width - 0.5f, t = 0.5f - (p.Y - on.Top + 0.5f) / on.Height;
            return spec.ToPanel(on.Width, on.Height, optics).WorldPoint(s, t);
        }
        return null;
    }

    /// <summary>Whether a point in the room is inside the view from this head direction.</summary>
    public static bool InView(Vector3 point, Quaternion head, DisplayOptics optics)
    {
        (float across, float up) = Angles(point, head);
        return MathF.Abs(across) < optics.HorizontalFovDeg / 2f && MathF.Abs(up) < optics.VerticalFovDeg / 2f;
    }

    /// <summary>Degrees from the middle of the view: + across is to the right, + up is up.</summary>
    public static (float Across, float Up) Angles(Vector3 point, Quaternion head)
    {
        Vector3 v = Vector3.Transform(point, Quaternion.Conjugate(head));
        return (MathF.Atan2(v.X, -v.Z) * 180f / MathF.PI, MathF.Atan2(v.Y, MathF.Sqrt(v.X * v.X + v.Z * v.Z)) * 180f / MathF.PI);
    }
}

/// <summary>
/// When to help the eye find the mouse pointer. A pointer that starts moving after resting a while gets a ring for
/// a moment; one that is moving, or just was, is fresh (an edge arrow points to it when it is out of view); and the
/// first time it is moved after being out of view for a while, the wearer is told once how to bring it to them.
/// </summary>
public sealed class PointerBeacon
{
    public const double RestSeconds = 2, RingSeconds = 1, FreshSeconds = 3, LostSeconds = 3;

    double _movedAt = double.NegativeInfinity, _ringUntil = double.NegativeInfinity, _lostSince = double.NaN;
    bool _told;

    /// <param name="moved">The pointer moved since the last step.</param>
    /// <param name="inView">The wearer can see where the pointer is.</param>
    public (bool Ring, bool Fresh, bool TellMouseToMe) Step(double time, bool moved, bool inView)
    {
        if (inView) _lostSince = double.NaN;
        else if (double.IsNaN(_lostSince)) _lostSince = time;
        bool tell = false;
        if (moved)
        {
            if (time - _movedAt >= RestSeconds) _ringUntil = time + RingSeconds;
            if (!inView && time - _lostSince >= LostSeconds && !_told) _told = tell = true;
            _movedAt = time;
        }
        return (time < _ringUntil, time - _movedAt < FreshSeconds, tell);
    }
}

/// <summary>Where a window goes when it is brought onto another monitor.</summary>
public static class WindowFit
{
    /// <summary>
    /// The window's new place in <paramref name="to"/> (a monitor's working area): its own size if that fits, else
    /// shrunk to fit, in the middle.
    /// </summary>
    public static Rectangle Into(Size window, Rectangle to)
    {
        int width = Math.Min(window.Width, to.Width), height = Math.Min(window.Height, to.Height);
        return new Rectangle(to.Left + (to.Width - width) / 2, to.Top + (to.Height - height) / 2, width, height);
    }
}
