using System.Drawing;

namespace XrShift.Scene;

/// <summary>
/// Where extra monitors go on the Windows desktop so the mouse crosses between them the way they sit in the room:
/// a screen to the right of another is to its right on the desktop, one above another is above it.
/// </summary>
public static class DesktopPlan
{
    /// <summary>A monitor to place: its size in pixels and the direction of its screen in the room (yaw + is left, pitch + is up).</summary>
    public readonly record struct Screen(string Id, int Width, int Height, float YawDeg, float PitchDeg);

    /// <param name="main">The monitor everything is arranged around; it does not move.</param>
    /// <param name="taken">Desktop areas that must stay free: the other real monitors and the glasses' own area.</param>
    public static List<(string Id, int X, int Y)> Place(Rectangle main, float mainYaw, float mainPitch, IReadOnlyList<Screen> extras, IReadOnlyList<Rectangle> taken)
    {
        var placed = new List<(Rectangle Area, float Yaw, float Pitch)> { (main, mainYaw, mainPitch) };
        var result = new List<(string, int, int)>();
        static float Apart(float yawA, float pitchA, float yawB, float pitchB) => MathF.Abs(yawA - yawB) + MathF.Abs(pitchA - pitchB);

        // Nearest to the main screen first, each one set against whichever placed screen it is nearest to in the room.
        foreach (Screen e in extras.OrderBy(e => Apart(e.YawDeg, e.PitchDeg, mainYaw, mainPitch)))
        {
            (Rectangle anchor, float yaw, float pitch) = placed.MinBy(p => Apart(e.YawDeg, e.PitchDeg, p.Yaw, p.Pitch));
            float across = e.YawDeg - yaw, up = e.PitchDeg - pitch;
            (int dx, int dy) = MathF.Abs(up) > MathF.Abs(across) ? (0, up > 0 ? -1 : 1) : (across > 0 ? -1 : 1, 0); // desktop y grows downward

            Rectangle Beside(Rectangle of) => new(
                dx < 0 ? of.Left - e.Width : dx > 0 ? of.Right : of.Left,
                dy < 0 ? of.Top - e.Height : dy > 0 ? of.Bottom : of.Top,
                e.Width, e.Height);

            Rectangle area = Beside(anchor);
            // Something already there (a real monitor, another extra one): keep going the same way until there is room.
            for (int tries = 0; tries < 16; tries++)
            {
                Rectangle? blocker = taken.Concat(placed.Select(p => p.Area)).Cast<Rectangle?>().FirstOrDefault(r => r!.Value.IntersectsWith(area));
                if (blocker is null) break;
                area = Beside(blocker.Value) with { X = dx == 0 ? area.X : Beside(blocker.Value).X, Y = dy == 0 ? area.Y : Beside(blocker.Value).Y };
            }
            placed.Add((area, e.YawDeg, e.PitchDeg));
            result.Add((e.Id, area.X, area.Y));
        }
        return result;
    }
}
