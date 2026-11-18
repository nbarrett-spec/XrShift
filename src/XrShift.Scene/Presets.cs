namespace XrShift.Scene;

/// <summary>Ready-made arrangements of the screens in a layout.</summary>
public enum Arrangement
{
    /// <summary>Side by side at eye level, flat.</summary>
    Row,
    /// <summary>Side by side at eye level, each wrapped around the wearer so the row reads as one curved band.</summary>
    Curve,
    /// <summary>Two rows: one at eye level, one above it.</summary>
    TwoRows,
    /// <summary>One screen straight ahead at full size, the others smaller beside it.</summary>
    Focus,
}

public static class Presets
{
    const float GapDeg = 2.5f, FocusSideScale = 0.6f;

    /// <summary>
    /// Rearranges the shown screens; hidden ones keep their place. Screens keep their left-to-right order.
    /// </summary>
    /// <param name="pictureSize">The size in pixels of what a screen shows (a monitor's resolution, a window's size).</param>
    /// <param name="mainId">For <see cref="Arrangement.Focus"/>: the screen to put in front. Null = the one nearest straight ahead.</param>
    public static Layout Arrange(Layout layout, Arrangement how, Func<ScreenSpec, (int Width, int Height)> pictureSize, DisplayOptics optics, string? mainId = null)
    {
        // Left to right: yaw + is to the left.
        List<ScreenSpec> shown = layout.Screens.Where(s => s.Visible).OrderByDescending(s => s.YawDeg).ToList();
        if (shown.Count == 0) return layout;

        (float W, float H) Size(ScreenSpec s)
        {
            (int w, int h) = pictureSize(s);
            float wide = s.ToPanel(w, h, optics).AngularWidthDeg;
            return (wide, wide * Math.Max(h, 1) / Math.Max(w, 1));
        }

        var placed = new Dictionary<string, ScreenSpec>();
        void Row(IReadOnlyList<ScreenSpec> row, float pitch, bool curve)
        {
            float total = row.Sum(s => Size(s).W) + GapDeg * (row.Count - 1);
            float left = total / 2f;
            foreach (ScreenSpec s in row)
            {
                float w = Size(s).W;
                placed[s.Id] = s with { YawDeg = left - w / 2f, PitchDeg = pitch, TiltDeg = 0, CurveDeg = curve ? MathF.Min(w, 180f) : 0 };
                left -= w + GapDeg;
            }
        }

        switch (how)
        {
            case Arrangement.Row:
            case Arrangement.Curve:
                Row(shown, 0, how == Arrangement.Curve);
                break;

            case Arrangement.TwoRows:
            {
                // The screens nearest eye level stay down; the rest go up a row.
                int down = (shown.Count + 1) / 2;
                var lower = shown.OrderBy(s => MathF.Abs(s.PitchDeg)).ThenBy(s => MathF.Abs(s.YawDeg)).Take(down).ToHashSet();
                List<ScreenSpec> bottom = shown.Where(lower.Contains).ToList(), top = shown.Where(s => !lower.Contains(s)).ToList();
                Row(bottom, 0, false);
                if (top.Count > 0)
                {
                    float below = bottom.Max(s => Size(s).H) / 2f, above = top.Max(s => Size(s).H) / 2f;
                    Row(top, below + GapDeg + above, false);
                }
                break;
            }

            case Arrangement.Focus:
            {
                ScreenSpec main = shown.FirstOrDefault(s => s.Id == mainId)
                    ?? shown.OrderBy(s => MathF.Abs(s.YawDeg) + MathF.Abs(s.PitchDeg)).First();
                (int mw, int mh) = pictureSize(main);
                main = main with { Scale = ScreenSpec.ScaleToFit(mh, optics), YawDeg = 0, PitchDeg = 0, TiltDeg = 0, CurveDeg = 0 };
                placed[main.Id] = main;
                float half = Size(main).W / 2f;
                float right = -half, left = half; // the edges taken so far, in yaw
                int i = 0;
                // The others, smaller, alternating right and left of the main screen in their own order.
                foreach (ScreenSpec other in shown.Where(s => s.Id != main.Id))
                {
                    ScreenSpec small = other with { Scale = MathF.Min(other.Scale, FocusSideScale), PitchDeg = 0, TiltDeg = 0, CurveDeg = 0 };
                    float w = Size(small).W;
                    if (i++ % 2 == 0)
                    {
                        placed[other.Id] = small with { YawDeg = right - GapDeg - w / 2f };
                        right -= GapDeg + w;
                    }
                    else
                    {
                        placed[other.Id] = small with { YawDeg = left + GapDeg + w / 2f };
                        left += GapDeg + w;
                    }
                }
                break;
            }
        }

        return layout with
        {
            Screens = layout.Screens.Select(s => placed.TryGetValue(s.Id, out ScreenSpec? p) ? Layout.Clamp(p) : s).ToList(),
        };
    }

    /// <summary>
    /// Shrinks and centres the shown screens so all of them fit inside one view of the glasses, keeping their
    /// arrangement. For stationary mode, where the glasses hold one picture still in the room and everything
    /// must be inside it. A layout that already fits is only centred.
    /// </summary>
    public static Layout FitInView(Layout layout, Func<ScreenSpec, (int Width, int Height)> pictureSize, DisplayOptics optics, float marginDeg = 1.5f)
    {
        // A screen's angle does not shrink quite in step with its size, so a few passes settle it.
        for (int pass = 0; pass < 6; pass++) layout = FitOnce(layout, pictureSize, optics, marginDeg);
        return layout;
    }

    static Layout FitOnce(Layout layout, Func<ScreenSpec, (int Width, int Height)> pictureSize, DisplayOptics optics, float marginDeg)
    {
        List<ScreenSpec> shown = layout.Screens.Where(s => s.Visible).ToList();
        if (shown.Count == 0) return layout;
        float left = float.MinValue, right = float.MaxValue, top = float.MinValue, bottom = float.MaxValue;
        foreach (ScreenSpec s in shown)
        {
            (int w, int h) = pictureSize(s);
            float wide = s.ToPanel(w, h, optics).AngularWidthDeg;
            float high = wide * Math.Max(h, 1) / Math.Max(w, 1);
            left = MathF.Max(left, s.YawDeg + wide / 2f);   // yaw + is to the left
            right = MathF.Min(right, s.YawDeg - wide / 2f);
            top = MathF.Max(top, s.PitchDeg + high / 2f);
            bottom = MathF.Min(bottom, s.PitchDeg - high / 2f);
        }
        float centreYaw = (left + right) / 2f, centrePitch = (top + bottom) / 2f;
        float fit = MathF.Min(1f, MathF.Min(
            (optics.HorizontalFovDeg - 2 * marginDeg) / MathF.Max(left - right, 1e-3f),
            (optics.VerticalFovDeg - 2 * marginDeg) / MathF.Max(top - bottom, 1e-3f)));
        return layout with
        {
            Screens = layout.Screens.Select(s => !s.Visible ? s : Layout.Clamp(s with
            {
                YawDeg = (s.YawDeg - centreYaw) * fit,
                PitchDeg = (s.PitchDeg - centrePitch) * fit,
                Scale = s.Scale * fit,
            })).ToList(),
        };
    }
}
