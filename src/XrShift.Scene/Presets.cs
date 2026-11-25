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
    /// <summary>The chosen screen big, gently curved and straight ahead; every other screen hidden.</summary>
    Cinema,
}

/// <summary>Standard layouts that set where screens go and how big they are, all within an easy head turn.</summary>
public enum LayoutPreset
{
    /// <summary>One screen straight ahead; the others hidden.</summary>
    Solo,
    /// <summary>The main screen ahead and one beside it.</summary>
    MainAndSide,
    /// <summary>The main screen ahead and one each side, turned in like the wings of a three-monitor desk.</summary>
    TripleArc,
    /// <summary>The main screen ahead and a second straight above it.</summary>
    Stack,
    /// <summary>The main screen ahead, one each side and one above.</summary>
    Cockpit,
}

public static class Presets
{
    const float GapDeg = 2.5f, FocusSideScale = 0.6f;
    /// <summary>The furthest a layout preset puts a screen's centre from straight ahead, either way: an easy head turn.</summary>
    public const float ReachDeg = 60f;
    /// <summary>Side screens shrink to stay in reach, but not below this width; past it the main screen shrinks instead.</summary>
    const float MinSideWidthDeg = 20f, ArcTurnDeg = 20f, ShrinkStep = 0.97f;
    const float DegToRad = MathF.PI / 180f;

    static ScreenSpec Main(IReadOnlyList<ScreenSpec> shown, string? mainId) =>
        shown.FirstOrDefault(s => s.Id == mainId) ?? shown.OrderBy(s => MathF.Abs(s.YawDeg) + MathF.Abs(s.PitchDeg)).First();

    /// <summary>A layout preset's name, or a size preset's, from what someone typed or said: "triple arc", "TripleArc", "sharp".</summary>
    public static bool TryParse<T>(string? text, out T value) where T : struct, Enum
    {
        value = default;
        string letters = new((text ?? "").Where(char.IsLetter).ToArray());
        // Letters only, so Enum.TryParse cannot read a number or a list of names.
        return letters.Length > 0 && Enum.TryParse(letters, ignoreCase: true, out value) && Enum.IsDefined(value);
    }

    /// <summary>What a layout preset does, in one line.</summary>
    public static string About(LayoutPreset how) => how switch
    {
        LayoutPreset.Solo => "One screen straight ahead; the others hidden.",
        LayoutPreset.MainAndSide => "The main screen straight ahead and one more beside it.",
        LayoutPreset.TripleArc => "The main screen ahead and one each side, turned in toward you.",
        LayoutPreset.Stack => "The main screen ahead and a second one straight above it.",
        _ => "The main screen ahead, one each side and one above.",
    };

    /// <summary>
    /// How far a screen reaches from the head, in degrees: Left and Top are + like yaw and pitch. Measured round
    /// the border, since the middle of an edge can stand out further than its corners.
    /// </summary>
    public static (float Left, float Right, float Top, float Bottom) Bounds(ScreenSpec screen, int pictureWidth, int pictureHeight, DisplayOptics optics)
    {
        Panel panel = screen.ToPanel(pictureWidth, pictureHeight, optics);
        float left = float.MinValue, right = float.MaxValue, top = float.MinValue, bottom = float.MaxValue;
        const int steps = 16;
        for (int i = 0; i <= steps; i++)
        {
            float u = (float)i / steps - 0.5f;
            foreach (System.Numerics.Vector3 p in new[] { panel.WorldPoint(u, 0.5f), panel.WorldPoint(u, -0.5f), panel.WorldPoint(-0.5f, u), panel.WorldPoint(0.5f, u) })
            {
                float yaw = MathF.Atan2(-p.X, -p.Z) / DegToRad, pitch = MathF.Atan2(p.Y, MathF.Sqrt(p.X * p.X + p.Z * p.Z)) / DegToRad;
                left = MathF.Max(left, yaw);
                right = MathF.Min(right, yaw);
                top = MathF.Max(top, pitch);
                bottom = MathF.Min(bottom, pitch);
            }
        }
        return (left, right, top, bottom);
    }

    /// <summary>
    /// Places and sizes the shown screens as a preset: the main one flat and straight ahead at <paramref name="size"/>,
    /// the others around it, flat, no centre further than <see cref="ReachDeg"/> from ahead and none overlapping.
    /// Side and top screens shrink to fit before the main one does. Shown screens the preset has no place for are
    /// hidden, the ones furthest from the main screen first; already hidden ones stay as they are.
    /// </summary>
    /// <param name="mainId">The screen to put in front. Null = the one nearest straight ahead.</param>
    public static Layout Apply(Layout layout, LayoutPreset how, SizePreset size, Func<ScreenSpec, (int Width, int Height)> pictureSize, DisplayOptics optics, string? mainId = null)
    {
        List<ScreenSpec> shown = layout.Screens.Where(s => s.Visible).ToList();
        if (shown.Count == 0) return layout;
        ScreenSpec main = Main(shown, mainId);
        (int sides, bool above) = how switch
        {
            LayoutPreset.Solo => (0, false),
            LayoutPreset.MainAndSide => (1, false),
            LayoutPreset.TripleArc => (2, false),
            LayoutPreset.Stack => (0, true),
            _ => (2, true),
        };
        List<ScreenSpec> kept = shown.Where(s => s.Id != main.Id)
            .OrderBy(s => MathF.Abs(s.YawDeg - main.YawDeg) + MathF.Abs(s.PitchDeg - main.PitchDeg))
            .Take(sides + (above ? 1 : 0)).ToList();
        // With fewer screens than places the top is left empty first, so the screens stay at eye level.
        ScreenSpec? up = above && kept.Count == sides + 1 ? kept.MaxBy(s => s.PitchDeg) : null;
        List<ScreenSpec> row = kept.Where(s => s != up).OrderByDescending(s => s.YawDeg).ToList();
        // One place each side: screens go to the side they were on, unless both were on one side,
        // when they keep their left-to-right order around the main screen.
        int leftCount = Math.Clamp(row.Count(s => s.YawDeg > main.YawDeg), row.Count - 1, 1);
        ScreenSpec? left = leftCount == 1 ? row[0] : null, right = row.Count > leftCount ? row[^1] : null;

        ScreenSpec Flat(ScreenSpec s, float scale, float turn = 0) =>
            s with { Scale = scale, YawDeg = 0, PitchDeg = 0, TiltDeg = 0, CurveDeg = 0, TurnDeg = turn, Visible = true };
        (float Left, float Right, float Top, float Bottom) BoundsOf(ScreenSpec s)
        {
            (int w, int h) = pictureSize(s);
            return Bounds(s, w, h, optics);
        }
        float Preset(ScreenSpec s)
        {
            (int w, int h) = pictureSize(s);
            return Sizes.ScaleFor(size, w, h, optics);
        }

        var placed = new Dictionary<string, ScreenSpec>();
        // Shrinks one screen until its centre is in reach or it is as small as a side screen may be.
        bool Fit(ScreenSpec s, Func<float, ScreenSpec> at)
        {
            for (float scale = Preset(s); ; scale *= ShrinkStep)
            {
                ScreenSpec candidate = at(scale);
                var b = BoundsOf(candidate);
                bool inReach = MathF.Abs(candidate.YawDeg) <= ReachDeg && MathF.Abs(candidate.PitchDeg) <= ReachDeg;
                if (inReach || b.Left - b.Right <= MinSideWidthDeg || scale * ShrinkStep < 0.2f)
                {
                    placed[s.Id] = candidate;
                    return inReach;
                }
            }
        }
        ScreenSpec Beside(ScreenSpec s, float scale, float edge, bool onLeft)
        {
            ScreenSpec flat = Flat(s, scale, how == LayoutPreset.TripleArc ? (onLeft ? ArcTurnDeg : -ArcTurnDeg) : 0);
            var b = BoundsOf(flat);
            // Turning about the head keeps a screen's shape at eye level, so its edges move with its yaw.
            return flat with { YawDeg = onLeft ? edge + GapDeg - b.Right : edge - GapDeg - b.Left };
        }
        ScreenSpec Over(ScreenSpec s, float scale, float rowTop)
        {
            ScreenSpec flat = Flat(s, scale);
            // Tipped up, a screen's lower corners sink below its lower middle, so settle the height in a few passes.
            for (int pass = 0; pass < 5; pass++) flat = flat with { PitchDeg = flat.PitchDeg + rowTop + GapDeg - BoundsOf(flat).Bottom };
            return flat;
        }

        float mainScale = Preset(main);
        for (int attempt = 0; attempt < 60; attempt++, mainScale *= ShrinkStep)
        {
            placed.Clear();
            ScreenSpec m = placed[main.Id] = Flat(main, mainScale);
            var mb = BoundsOf(m);
            bool fits = true;
            if (left is not null) fits &= Fit(left, scale => Beside(left, scale, mb.Left, true));
            if (right is not null) fits &= Fit(right, scale => Beside(right, scale, mb.Right, false));
            if (up is not null)
            {
                float rowTop = placed.Values.Max(s => BoundsOf(s).Top);
                fits &= Fit(up, scale => Over(up, scale, rowTop));
            }
            if (fits) break;
        }

        return layout with
        {
            Screens = layout.Screens.Select(s =>
                placed.TryGetValue(s.Id, out ScreenSpec? p) ? Layout.Clamp(p) : s.Visible ? s with { Visible = false } : s).ToList(),
        };
    }

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
                ScreenSpec main = Main(shown, mainId);
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

            case Arrangement.Cinema:
            {
                // Like a cinema: one big screen ahead, wrapped a little so its edges are as near as its middle.
                ScreenSpec main = Main(shown, mainId);
                (int mw, int mh) = pictureSize(main);
                placed[main.Id] = main with
                {
                    Scale = ScreenSpec.ScaleToFit(mh, optics), YawDeg = 0, PitchDeg = 0, TiltDeg = 0, TurnDeg = 0, CurveDeg = 25, Visible = true,
                };
                foreach (ScreenSpec other in shown.Where(s => s.Id != main.Id)) placed[other.Id] = other with { Visible = false };
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
