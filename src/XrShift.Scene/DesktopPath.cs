using System.Drawing;

namespace XrShift.Scene;

/// <summary>
/// Keeps the mouse pointer out of a part of the Windows desktop it has no business in (the
/// glasses' own area, which is covered by the scene). Windows may have put that area anywhere
/// among the monitors, even between two of them, so the pointer is carried across it.
/// </summary>
public static class DesktopPath
{
    /// <summary>
    /// Where to put a pointer that has strayed into <paramref name="blocked"/>: just inside the
    /// nearest allowed area on the far side, or back where it came from if there is nothing over there.
    /// </summary>
    /// <param name="inside">Where the pointer is now (inside the blocked area).</param>
    /// <param name="lastOutside">Where it last was before it went in; tells which way it is travelling.</param>
    public static Point Redirect(Point inside, Point lastOutside, Rectangle blocked, IReadOnlyList<Rectangle> allowed)
    {
        if (allowed.Count == 0) return inside;

        bool fromLeft = lastOutside.X < blocked.Left, fromRight = lastOutside.X >= blocked.Right;
        bool fromTop = lastOutside.Y < blocked.Top, fromBottom = lastOutside.Y >= blocked.Bottom;

        if (!(fromLeft || fromRight || fromTop || fromBottom))
        {
            // No idea where it came from (it started in there): the middle of the first monitor will do.
            Rectangle home = allowed[0];
            return new Point(home.Left + home.Width / 2, home.Top + home.Height / 2);
        }

        Rectangle? target = null;
        if (fromLeft) target = Nearest(allowed.Where(r => r.Left >= blocked.Right), r => r.Left - blocked.Right);
        else if (fromRight) target = Nearest(allowed.Where(r => r.Right <= blocked.Left), r => blocked.Left - r.Right);
        else if (fromTop) target = Nearest(allowed.Where(r => r.Top >= blocked.Bottom), r => r.Top - blocked.Bottom);
        else target = Nearest(allowed.Where(r => r.Bottom <= blocked.Top), r => blocked.Top - r.Bottom);

        if (target is not { } t) return lastOutside; // nothing on the far side: it stays where it was

        // Land just inside the near edge, at the same relative height (or width) it crossed at.
        if (fromLeft || fromRight)
        {
            int y = t.Top + (int)((long)(inside.Y - blocked.Top) * t.Height / Math.Max(blocked.Height, 1));
            return new Point(fromLeft ? t.Left + 1 : t.Right - 2, Math.Clamp(y, t.Top, t.Bottom - 1));
        }
        int x = t.Left + (int)((long)(inside.X - blocked.Left) * t.Width / Math.Max(blocked.Width, 1));
        return new Point(Math.Clamp(x, t.Left, t.Right - 1), fromTop ? t.Top + 1 : t.Bottom - 2);
    }

    static Rectangle? Nearest(IEnumerable<Rectangle> candidates, Func<Rectangle, int> gap)
    {
        Rectangle? best = null;
        int bestGap = int.MaxValue;
        foreach (Rectangle r in candidates)
        {
            int g = gap(r);
            if (g < bestGap)
            {
                best = r;
                bestGap = g;
            }
        }
        return best;
    }
}
