using XrShift.Scene;

namespace XrShift.Scene.Tests;

public class PresetTests
{
    static readonly DisplayOptics Optics = DisplayOptics.OnePro;
    static (int, int) Hd(ScreenSpec _) => (1920, 1080);

    static Layout Three() => new Layout()
        .Add(new ScreenSpec { Id = "a", YawDeg = 40, PitchDeg = 10, Scale = 0.6f })
        .Add(new ScreenSpec { Id = "b", YawDeg = 0, PitchDeg = -5, Scale = 0.6f })
        .Add(new ScreenSpec { Id = "c", YawDeg = -40, Scale = 0.6f, Visible = true })
        .Add(new ScreenSpec { Id = "hidden", YawDeg = 123, Visible = false });

    static float Half(ScreenSpec s) => s.ToPanel(1920, 1080, Optics).AngularWidthDeg / 2;

    [Fact]
    public void ARowKeepsTheOrderLevelsTheScreensAndLeavesNoOverlap()
    {
        Layout row = Presets.Arrange(Three(), Arrangement.Row, Hd, Optics);
        ScreenSpec a = row.Find("a")!, b = row.Find("b")!, c = row.Find("c")!;
        Assert.True(a.YawDeg > b.YawDeg && b.YawDeg > c.YawDeg); // still left to right
        Assert.All(new[] { a, b, c }, s => Assert.Equal(0f, s.PitchDeg));
        Assert.True(a.YawDeg - Half(a) - (b.YawDeg + Half(b)) > 1f);
        Assert.Equal(0f, a.YawDeg + c.YawDeg, 2); // centred
        Assert.Equal(123f, row.Find("hidden")!.YawDeg); // hidden screens stay put
    }

    [Fact]
    public void ACurveWrapsEachScreenByItsOwnWidth()
    {
        Layout curve = Presets.Arrange(Three(), Arrangement.Curve, Hd, Optics);
        ScreenSpec b = curve.Find("b")!;
        Assert.Equal(2 * Half(new ScreenSpec { Scale = 0.6f }), b.CurveDeg, 1);
    }

    [Fact]
    public void FocusPutsTheChosenScreenAheadAtFullSizeAndTheOthersSmallerBeside()
    {
        Layout focus = Presets.Arrange(Three(), Arrangement.Focus, Hd, Optics, mainId: "c");
        ScreenSpec c = focus.Find("c")!;
        Assert.Equal((0f, 0f), (c.YawDeg, c.PitchDeg));
        Assert.Equal(ScreenSpec.ScaleToFit(1080, Optics), c.Scale);
        foreach (string id in new[] { "a", "b" })
        {
            ScreenSpec s = focus.Find(id)!;
            Assert.True(s.Scale < c.Scale);
            Assert.True(MathF.Abs(s.YawDeg) - Half(s) > Half(c)); // beside, not over it
        }
        Assert.True(focus.Find("a")!.YawDeg * focus.Find("b")!.YawDeg < 0); // one each side
    }

    [Fact]
    public void TwoRowsPutsTheTopRowClearAboveTheBottomOne()
    {
        Layout rows = Presets.Arrange(Three(), Arrangement.TwoRows, Hd, Optics);
        var pitches = rows.Screens.Where(s => s.Visible).Select(s => s.PitchDeg).Distinct().OrderBy(p => p).ToList();
        Assert.Equal(2, pitches.Count);
        Assert.Equal(0f, pitches[0]);
        float height = 2 * Half(new ScreenSpec { Scale = 0.6f }) * 1080 / 1920;
        Assert.True(pitches[1] > height);
    }

    [Fact]
    public void ZoomScalesSizesAndSpreadTogether()
    {
        Layout zoomed = Three().Zoomed(0.5f);
        ScreenSpec a = zoomed.Find("a")!;
        Assert.Equal((20f, 5f, 0.3f), (a.YawDeg, a.PitchDeg, a.Scale));
        Layout same = Three();
        Assert.Same(same, same.Zoomed(1f));
    }

    [Fact]
    public void FitInViewPutsEveryScreenInsideOneViewOfTheGlasses()
    {
        Layout fitted = Presets.FitInView(Presets.Arrange(Three(), Arrangement.Row, Hd, Optics), Hd, Optics);
        foreach (ScreenSpec s in fitted.Screens.Where(s => s.Visible))
        {
            Assert.True(MathF.Abs(s.YawDeg) + Half(s) <= Optics.HorizontalFovDeg / 2 + 0.01f, $"yaw {s.YawDeg} half {Half(s)} scale {s.Scale} fov {Optics.HorizontalFovDeg}");
            Assert.True(MathF.Abs(s.PitchDeg) + Half(s) * 1080f / 1920f <= Optics.VerticalFovDeg / 2 + 0.01f, $"pitch {s.PitchDeg} half {Half(s)} vfov {Optics.VerticalFovDeg}");
        }
        // Order kept: left to right is still decreasing yaw.
        List<float> yaws = fitted.Screens.Where(s => s.Visible).Select(s => s.YawDeg).ToList();
        Assert.Equal(yaws.OrderByDescending(y => y), yaws);
    }

    [Fact]
    public void CinemaShowsOneBigCurvedScreenAheadAndHidesTheRest()
    {
        Layout cinema = Presets.Arrange(Three(), Arrangement.Cinema, Hd, Optics, mainId: "b");
        ScreenSpec main = cinema.Find("b")!;
        Assert.True(main.Visible && main.YawDeg == 0 && main.CurveDeg > 0);
        Assert.False(cinema.Find("a")!.Visible);
        Assert.False(cinema.Find("c")!.Visible);
    }

    [Fact]
    public void ATvSizeInInchesComesOutAsAsked()
    {
        var tv = new ScreenSpec { Distance = 2.5f }.WithDiagonalInches(55, 1920, 1080, Optics);
        Assert.InRange(tv.DiagonalInches(1920, 1080, Optics), 54.9f, 55.1f);
        // Moving it further keeps its real size.
        ScreenSpec further = tv.AtDistanceKeepingSize(4f);
        Assert.InRange(further.DiagonalInches(1920, 1080, Optics), 54.9f, 55.1f);
        Assert.True(further.Scale < tv.Scale);
    }

    [Fact]
    public void ATurnedScreenIsStillHitWhereItIsDrawn()
    {
        var panel = new Panel { Distance = 2, Width = 1.6f, Height = 0.9f, TurnDeg = 40 };
        foreach ((float s, float t) in new[] { (0f, 0f), (0.3f, 0.2f), (-0.4f, -0.3f) })
        {
            System.Numerics.Vector3 point = panel.LocalPoint(s, t);
            Assert.True(panel.TryHit(System.Numerics.Vector3.Normalize(point), out float hs, out float ht));
            Assert.InRange(hs, s - 1e-3f, s + 1e-3f);
            Assert.InRange(ht, t - 1e-3f, t + 1e-3f);
        }
        // Turned +40: the right edge is further away than the left.
        Assert.True(panel.LocalPoint(0.5f, 0).Z < panel.LocalPoint(-0.5f, 0).Z);
    }
}
