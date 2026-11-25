using System.Numerics;
using XrShift.Scene;

namespace XrShift.Scene.Tests;

public class LayoutPresetTests
{
    static readonly DisplayOptics Optics = DisplayOptics.OnePro;
    static readonly (int, int)[] Mixed = [(3440, 1440), (2560, 1600), (1920, 1080)];

    /// <summary>n screens in a loose row, sizes taken in turn from <see cref="Mixed"/>; the picture size is kept on the screen.</summary>
    static Layout Row(int n) => new Layout
    {
        Screens = Enumerable.Range(0, n).Select(i => new ScreenSpec
        {
            Id = $"s{i}",
            YawDeg = 70 - 140f * i / Math.Max(n - 1, 1) + 3,
            Scale = 0.705f,
            PixelWidth = Mixed[i % 3].Item1,
            PixelHeight = Mixed[i % 3].Item2,
        }).ToList(),
    }.Add(new ScreenSpec { Id = "parked", YawDeg = 150, Visible = false });

    static (int, int) Picture(ScreenSpec s) => s.PixelWidth > 0 ? (s.PixelWidth, s.PixelHeight) : (1920, 1080);

    /// <summary>The screen's outline as seen from the head, sampled densely and worked out here rather than by the code under test.</summary>
    static (float L, float R, float T, float B) Rect(ScreenSpec s)
    {
        (int w, int h) = Picture(s);
        Panel panel = s.ToPanel(w, h, Optics);
        var points = new List<Vector3>();
        for (int i = 0; i <= 40; i++)
        {
            float u = i / 40f - 0.5f;
            points.AddRange([panel.WorldPoint(u, 0.5f), panel.WorldPoint(u, -0.5f), panel.WorldPoint(-0.5f, u), panel.WorldPoint(0.5f, u)]);
        }
        float Yaw(Vector3 p) => MathF.Atan2(-p.X, -p.Z) * 180 / MathF.PI;
        float Pitch(Vector3 p) => MathF.Atan2(p.Y, MathF.Sqrt(p.X * p.X + p.Z * p.Z)) * 180 / MathF.PI;
        return (points.Max(Yaw), points.Min(Yaw), points.Max(Pitch), points.Min(Pitch));
    }

    public static TheoryData<LayoutPreset, int> EveryPresetWithOneToSixScreens()
    {
        var data = new TheoryData<LayoutPreset, int>();
        foreach (LayoutPreset how in Enum.GetValues<LayoutPreset>())
            for (int n = 1; n <= 6; n++) data.Add(how, n);
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryPresetWithOneToSixScreens))]
    public void EveryLayoutPresetStaysInReachWithoutOverlapKeepsOrderAndLeavesTheMainScreenSharp(LayoutPreset how, int n)
    {
        Layout before = Row(n);
        string mainId = before.Screens.Where(s => s.Visible).OrderBy(s => MathF.Abs(s.YawDeg)).First().Id;
        Layout after = Presets.Apply(before, how, SizePreset.Sharp, Picture, Optics);
        List<ScreenSpec> shown = after.Screens.Where(s => s.Visible).ToList();

        ScreenSpec main = after.Find(mainId)!;
        Assert.True(main.Visible);
        Assert.Equal((1f, 0f, 0f, 0f), (main.Scale, main.YawDeg, main.PitchDeg, main.CurveDeg));
        int places = how switch { LayoutPreset.Solo => 1, LayoutPreset.MainAndSide or LayoutPreset.Stack => 2, LayoutPreset.TripleArc => 3, _ => 4 };
        Assert.Equal(Math.Min(n, places), shown.Count);
        Assert.False(after.Find("parked")!.Visible);

        foreach (ScreenSpec s in shown)
        {
            Assert.True(MathF.Abs(s.YawDeg) <= Presets.ReachDeg && MathF.Abs(s.PitchDeg) <= Presets.ReachDeg, $"{s.Id} at yaw {s.YawDeg}, pitch {s.PitchDeg}");
            Assert.Equal(0f, s.CurveDeg);
            Assert.True(s.Scale <= 1f);
        }
        for (int i = 0; i < shown.Count; i++)
        for (int j = i + 1; j < shown.Count; j++)
        {
            var a = Rect(shown[i]);
            var b = Rect(shown[j]);
            bool apart = a.R >= b.L || b.R >= a.L || a.B >= b.T || b.B >= a.T;
            Assert.True(apart, $"{shown[i].Id} {a} overlaps {shown[j].Id} {b}");
        }

        // Screens at eye level are still in the order they were, left to right.
        List<string> eyeLevel = shown.Where(s => s.PitchDeg == 0 && s.Id != mainId).OrderByDescending(s => s.YawDeg).Select(s => s.Id).ToList();
        Assert.Equal(before.Screens.Where(s => eyeLevel.Contains(s.Id)).OrderByDescending(s => s.YawDeg).Select(s => s.Id), eyeLevel);
    }

    [Fact]
    public void TripleArcTurnsTheSidesInAndTheOthersStayFacingYou()
    {
        Layout arc = Presets.Apply(Row(3), LayoutPreset.TripleArc, SizePreset.Sharp, Picture, Optics);
        Assert.True(arc.Find("s0")!.TurnDeg > 0 && arc.Find("s2")!.TurnDeg < 0); // left one +, right one -
        Layout cockpit = Presets.Apply(Row(4), LayoutPreset.Cockpit, SizePreset.Sharp, Picture, Optics);
        Assert.All(cockpit.Screens.Where(s => s.Visible), s => Assert.Equal(0f, s.TurnDeg));
        Assert.Single(cockpit.Screens, s => s.Visible && s.PitchDeg > 0); // one above
    }

    [Fact]
    public void SideScreensShrinkBeforeTheMainOneAndAChosenMainGoesInFront()
    {
        Layout two = Presets.Apply(Row(2), LayoutPreset.MainAndSide, SizePreset.Sharp, Picture, Optics, mainId: "s1");
        Assert.Equal((0f, 1f), (two.Find("s1")!.YawDeg, two.Find("s1")!.Scale));
        Assert.True(two.Find("s0")!.Scale < 1f); // the 3440 side shrank to stay in reach
        Assert.True(two.Find("s0")!.YawDeg > 0); // and stayed on the left, where it was
    }

    [Fact]
    public void SoloWithOverviewFitsTheMainScreenInOneView()
    {
        Layout solo = Presets.Apply(Row(3), LayoutPreset.Solo, SizePreset.Overview, Picture, Optics);
        ScreenSpec main = Assert.Single(solo.Screens, s => s.Visible);
        var r = Rect(main);
        Assert.True(r.L - r.R <= Optics.HorizontalFovDeg && r.T - r.B <= Optics.VerticalFovDeg);
    }

    [Theory]
    [InlineData(3440, 1440)]
    [InlineData(2560, 1600)]
    [InlineData(1920, 1080)]
    public void EverySizePresetIsRecognisedOnceApplied(int w, int h)
    {
        foreach (SizePreset size in Enum.GetValues<SizePreset>())
        {
            ScreenSpec s = Sizes.Apply(new ScreenSpec { Scale = 0.705f }, size, w, h, Optics);
            Assert.Equal(size, Sizes.Of(s, w, h, Optics));
            Assert.StartsWith(size + ":", Sizes.Describe(s, w, h, Optics));
        }
        Assert.Null(Sizes.Of(new ScreenSpec { Scale = 0.705f }, w, h, Optics));
    }

    [Fact]
    public void TheDescriptionNamesTheTvSizeAndDistance()
    {
        ScreenSpec sharp = Sizes.Apply(new ScreenSpec { Distance = 2f }, SizePreset.Sharp, 1920, 1080, Optics);
        Assert.Equal("Sharp: 86-inch screen at 6.6 ft, text as sharp as the glasses allow", Sizes.Describe(sharp, 1920, 1080, Optics));
        Assert.StartsWith("Custom: ", Sizes.Describe(sharp with { Scale = 0.9f }, 1920, 1080, Optics));
    }

    [Fact]
    public void SizingEveryShownScreenLeavesHiddenOnesAlone()
    {
        Layout sized = Sizes.Apply(Row(3), SizePreset.Comfort, Picture, Optics);
        Assert.All(sized.Screens.Where(s => s.Visible), s => Assert.Equal(Sizes.ComfortScale, s.Scale));
        Assert.Equal(0.85f, sized.Find("parked")!.Scale); // its own default, untouched
        Assert.Equal(Row(3).Find("s0")!.YawDeg, sized.Find("s0")!.YawDeg);
    }

    [Theory]
    [InlineData(1.2f, 1.15f)]
    [InlineData(0.94f, 1f)]
    [InlineData(0.705f, 0.75f)]
    [InlineData(5f, 1.3f)]
    [InlineData(float.NaN, 1f)]
    public void ZoomSnapsToTheNearestStep(float zoom, float step) => Assert.Equal(step, Sizes.SnapZoom(zoom));

    [Fact]
    public void ZoomStepsSayWhatTheyCost()
    {
        Assert.Equal("100%: sharp", Sizes.DescribeZoom(1f));
        Assert.Equal("115%: enlarged, softer", Sizes.DescribeZoom(1.15f));
        Assert.Equal("85%: smaller, softer", Sizes.DescribeZoom(0.85f));
    }

    [Fact]
    public void TextOnASharpScreenIsAboutAsBigAsOnADeskMonitor()
    {
        (float degrees, float vsDesk) = Sizes.TextHeight(1920, 1080, 100, SizePreset.Sharp, 10, Optics);
        Assert.InRange(degrees, 0.36f, 0.39f);
        Assert.InRange(vsDesk, 1.1f, 1.25f);
        // An ultrawide shrunk to fit the view makes the same text much smaller; Windows scaling wins it back.
        float overview = Sizes.TextHeight(3440, 1440, 100, SizePreset.Overview, 10, Optics).VsDeskMonitor;
        Assert.True(overview < 0.7f);
        Assert.Equal(overview * 1.5f, Sizes.TextHeight(3440, 1440, 150, SizePreset.Overview, 10, Optics).VsDeskMonitor, 2);
    }

    [Theory]
    [InlineData("triple arc", LayoutPreset.TripleArc)]
    [InlineData("MainAndSide", LayoutPreset.MainAndSide)]
    [InlineData("main-and-side", LayoutPreset.MainAndSide)]
    [InlineData(" COCKPIT ", LayoutPreset.Cockpit)]
    public void PresetNamesAreReadLoosely(string text, LayoutPreset expected)
    {
        Assert.True(Presets.TryParse(text, out LayoutPreset how));
        Assert.Equal(expected, how);
    }

    [Theory]
    [InlineData("2")]
    [InlineData("solo, stack")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("tiled")]
    public void ANumberOrAnUnknownNameIsNotAPreset(string? text) => Assert.False(Presets.TryParse(text, out LayoutPreset _));
}
