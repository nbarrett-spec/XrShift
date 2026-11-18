using System.Numerics;

namespace XrShift.Scene;

/// <summary>What a hand is doing, read from MediaPipe's 21 points.</summary>
public enum HandShape { None, Open, Fist, Other }

public static class HandShapes
{
    /// <summary>
    /// Open: all four fingers stretched out and thumb and index apart (a flat hand, palm out). Fist: all four
    /// folded. A finger counts as stretched when its tip is clearly further from the wrist than its middle joint.
    /// </summary>
    public static HandShape Of(IReadOnlyList<Vector3>? p)
    {
        if (p is null || p.Count < 21) return HandShape.None;
        static Vector2 F(Vector3 v) => new(v.X, v.Y);
        float size = Vector2.Distance(F(p[0]), F(p[9]));
        if (size < 0.02f) return HandShape.None;
        int stretched = 0, folded = 0;
        foreach ((int tip, int joint) in new[] { (8, 6), (12, 10), (16, 14), (20, 18) })
        {
            float tipOut = Vector2.Distance(F(p[tip]), F(p[0])), jointOut = Vector2.Distance(F(p[joint]), F(p[0]));
            if (tipOut > jointOut * 1.15f) stretched++;
            else if (tipOut < jointOut * 0.95f) folded++;
        }
        bool thumbApart = Vector2.Distance(F(p[4]), F(p[8])) / size > 0.5f;
        if (stretched == 4 && thumbApart) return HandShape.Open;
        if (folded == 4) return HandShape.Fist;
        return HandShape.Other;
    }
}

/// <summary>
/// The hand quick menu. Hold a flat hand up, palm out, still for a moment: a row of buttons appears with the
/// middle one lit. Move the hand left or right to light another; pinch to pick it. A fist, a hand gone for a
/// second or eight seconds without a pick closes the menu.
/// </summary>
public sealed class HandMenu
{
    public HandMenu(int items) => Items = Math.Max(1, items);

    public int Items { get; }
    /// <summary>How long the open hand must stay still to open the menu, seconds.</summary>
    public double HoldSeconds { get; init; } = 0.6;
    /// <summary>How far (picture widths) the hand may drift while holding still.</summary>
    public float StillLimit { get; init; } = 0.025f;
    /// <summary>Hand travel (picture widths) from one button to the next.</summary>
    public float ItemSpan { get; init; } = 0.06f;

    public bool IsOpen { get; private set; }
    public int Highlight { get; private set; }

    Vector2? _holdFrom;
    double _holdSince, _openedAt, _lastSeen;
    float _originX;

    static Vector2 Palm(IReadOnlyList<Vector3> p) =>
        new((p[0].X + p[5].X + p[9].X + p[13].X + p[17].X) / 5f, (p[0].Y + p[5].Y + p[9].Y + p[13].Y + p[17].Y) / 5f);

    /// <summary>One camera frame. Returns the index of the button picked by a pinch, if one was.</summary>
    /// <param name="pinchStarted">The hand began a pinch in this frame.</param>
    public int? Step(IReadOnlyList<Vector3>? points, double time, bool pinchStarted)
    {
        if (!IsOpen)
        {
            if (HandShapes.Of(points) != HandShape.Open) { _holdFrom = null; return null; }
            Vector2 palm = Palm(points!);
            if (_holdFrom is not { } from || Vector2.Distance(from, palm) > StillLimit)
            {
                _holdFrom = palm;
                _holdSince = time;
                return null;
            }
            if (time - _holdSince < HoldSeconds) return null;
            IsOpen = true;
            _openedAt = _lastSeen = time;
            _originX = palm.X;
            Highlight = Items / 2;
            _holdFrom = null;
            return null;
        }

        if (time - _openedAt > 8) { Close(); return null; }
        if (points is null || points.Count < 21)
        {
            if (time - _lastSeen > 1.0) Close();
            return null;
        }
        _lastSeen = time;
        if (HandShapes.Of(points) == HandShape.Fist) { Close(); return null; }
        if (pinchStarted)
        {
            int chosen = Highlight;
            Close();
            return chosen;
        }
        float steps = (Palm(points).X - _originX) / ItemSpan;
        Highlight = Math.Clamp(Items / 2 + (int)MathF.Round(steps), 0, Items - 1);
        return null;
    }

    public void Close()
    {
        IsOpen = false;
        _holdFrom = null;
    }
}
