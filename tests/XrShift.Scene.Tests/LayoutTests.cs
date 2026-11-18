using XrShift.Scene;

namespace XrShift.Scene.Tests;

public class LayoutTests
{
    static readonly DisplayOptics Optics = DisplayOptics.OnePro;

    [Fact]
    public void AtScaleOneAPictureIsPixelExactInTheMiddleOfTheView()
    {
        // A 1920-wide picture at scale 1 spans exactly the view's width.
        Panel panel = new ScreenSpec { Scale = 1f }.ToPanel(1920, 1080, Optics);
        Assert.InRange(panel.AngularWidthDeg, 50.6f, 50.8f);
        Assert.InRange(panel.Height / panel.Width, 0.5624f, 0.5626f);

        // Half the pixels, or half the scale: half the width.
        Assert.InRange(new ScreenSpec { Scale = 1f }.ToPanel(960, 540, Optics).Width / panel.Width, 0.499f, 0.501f);
        Assert.InRange(new ScreenSpec { Scale = 0.5f }.ToPanel(1920, 1080, Optics).Width / panel.Width, 0.499f, 0.501f);
    }

    [Fact]
    public void FitScaleKeepsATallPictureInsideTheView()
    {
        float scale = ScreenSpec.ScaleToFit(1200, Optics);
        Assert.InRange(scale, 0.84f, 0.85f);
        Panel panel = new ScreenSpec { Scale = scale }.ToPanel(1920, 1200, Optics);
        float tallDeg = 2 * MathF.Atan(panel.Height / 2 / panel.Distance) * 180 / MathF.PI;
        Assert.True(tallDeg < Optics.VerticalFovDeg, $"{tallDeg} deg tall");

        Assert.Equal(1f, ScreenSpec.ScaleToFit(720, Optics)); // never blown up past pixel-exact
    }

    [Fact]
    public void PlacementCarriesThroughToThePanel()
    {
        Panel panel = new ScreenSpec { YawDeg = -40, PitchDeg = 10, TiltDeg = 5, CurveDeg = 30, Distance = 3 }.ToPanel(1920, 1080, Optics);
        Assert.Equal((-40f, 10f, 5f, 30f, 3f), (panel.YawDeg, panel.PitchDeg, panel.RollDeg, panel.CurveDeg, panel.Distance));
    }

    [Fact]
    public void TheFirstScreenGoesStraightAheadAndTheNextOnesBesideItWithoutOverlap()
    {
        var layout = new Layout();
        ScreenSpec first = layout.PlaceNew("Laptop", ScreenSpec.DisplaySource(@"\\.\DISPLAY1"), 1920, 1200, Optics);
        Assert.Equal(0f, first.YawDeg);
        layout = layout.Add(first);

        ScreenSpec second = layout.PlaceNew("Second", ScreenSpec.TestSource, 1920, 1080, Optics);
        layout = layout.Add(second);
        ScreenSpec third = layout.PlaceNew("Third", ScreenSpec.TestSource, 1920, 1080, Optics);

        // Three screens this size: middle, right, then left (a third on the right would end up behind the shoulder).
        Assert.True(second.YawDeg < 0, "second goes to the right");
        Assert.True(third.YawDeg > 0, "third goes to the left");
        Assert.InRange(Gap(first, second), 2.9f, 3.1f);
        Assert.InRange(Gap(first, third), 2.9f, 3.1f);

        static float Gap(ScreenSpec a, ScreenSpec b)
        {
            float halfA = a.ToPanel(1920, 1080, Optics).AngularWidthDeg / 2, halfB = b.ToPanel(1920, 1080, Optics).AngularWidthDeg / 2;
            return MathF.Abs(a.YawDeg - b.YawDeg) - halfA - halfB;
        }
    }

    [Fact]
    public void WhenTheRightSideIsFullNewScreensGoLeft()
    {
        var layout = new Layout();
        for (int i = 0; i < 6; i++)
            layout = layout.Add(layout.PlaceNew($"S{i}", ScreenSpec.TestSource, 1920, 1080, Optics));

        Assert.Contains(layout.Screens, s => s.YawDeg > 20);
        Assert.All(layout.Screens, s => Assert.InRange(s.YawDeg, -180f, 180f));
        Assert.Equal(6, layout.Screens.Select(s => MathF.Round(s.YawDeg)).Distinct().Count());
    }

    [Fact]
    public void RoundTripsThroughJson()
    {
        var layout = new Layout().Add(new ScreenSpec
        {
            Name = "Halo",
            Source = ScreenSpec.DisplaySource(@"\\.\DISPLAY7"),
            YawDeg = 48.5f, PitchDeg = -6, TiltDeg = 2, CurveDeg = 35, Scale = 1.1f, Distance = 2.5f, Visible = false,
        }).Add(new ScreenSpec { Name = "Spare" });

        Assert.True(Layout.TryParse(layout.ToJson(), out Layout back));
        Assert.Equal(layout.Screens, back.Screens);
        Assert.Equal(@"\\.\DISPLAY7", back.Screens[0].DisplayName);
        Assert.Null(back.Screens[1].DisplayName);
        Assert.True(back.Shows(@"\\.\display7"));
    }

    [Fact]
    public void AWindowSourceRemembersTheProgramAndTheTitle()
    {
        var screen = new ScreenSpec { Source = ScreenSpec.WindowSource("ms-teams", "Chat | Project X | Microsoft Teams") };
        Assert.Equal(("ms-teams", "Chat | Project X | Microsoft Teams"), screen.Window);
        Assert.Null(screen.DisplayName);

        Assert.Null(new ScreenSpec { Source = ScreenSpec.DisplaySource("DISPLAY1") }.Window);
        Assert.Null(new ScreenSpec().Window);
        Assert.Equal(("notepad", ""), new ScreenSpec { Source = "window:notepad" }.Window);

        // Survives saving and loading.
        Assert.True(Layout.TryParse(new Layout().Add(screen).ToJson(), out Layout back));
        Assert.Equal(screen.Window, back.Screens[0].Window);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"Screens\": 5}")]
    [InlineData("null")]
    public void RejectsBrokenFiles(string text) => Assert.False(Layout.TryParse(text, out _));

    [Fact]
    public void PullsWildValuesBackIntoRange()
    {
        Assert.True(Layout.TryParse(
            "{\"Screens\":[{\"Id\":\"a\",\"Name\":\"\",\"Source\":\"\",\"YawDeg\":999,\"PitchDeg\":-200,\"Scale\":0,\"Distance\":100,\"CurveDeg\":-5}]}",
            out Layout layout));
        ScreenSpec s = Assert.Single(layout.Screens);
        Assert.Equal(("Screen", ScreenSpec.TestSource, 180f, -80f, 0.2f, 9f, 0f), (s.Name, s.Source, s.YawDeg, s.PitchDeg, s.Scale, s.Distance, s.CurveDeg));
    }

    [Fact]
    public void AScreensMonitorSizeAndRefreshAreKeptAndNonsenseIsDropped()
    {
        var wide = new ScreenSpec { PixelWidth = 3840, PixelHeight = 1080, RefreshHz = 120 };
        Assert.True(Layout.TryParse(new Layout().Add(wide).ToJson(), out Layout back));
        ScreenSpec s = Assert.Single(back.Screens);
        Assert.Equal((3840, 1080, 120), (s.PixelWidth, s.PixelHeight, s.RefreshHz));

        // Half a size is no size, and an old file without these values means "leave the monitor alone".
        ScreenSpec odd = Layout.Clamp(new ScreenSpec { PixelWidth = 3840, PixelHeight = 0, RefreshHz = 5000 });
        Assert.Equal((0, 0, 0), (odd.PixelWidth, odd.PixelHeight, odd.RefreshHz));
        Assert.True(Layout.TryParse("{\"Screens\":[{\"Id\":\"a\"}]}", out Layout old));
        Assert.Equal((0, 0, 0), (old.Screens[0].PixelWidth, old.Screens[0].PixelHeight, old.Screens[0].RefreshHz));
    }

    [Fact]
    public void EditingAndRemovingGoByIdentity()
    {
        var a = new ScreenSpec { Name = "A" };
        var b = new ScreenSpec { Name = "B" };
        Layout layout = new Layout().Add(a).Add(b);

        layout = layout.With(b with { YawDeg = 30 });
        Assert.Equal(30f, layout.Find(b.Id)!.YawDeg);
        Assert.Equal(0f, layout.Find(a.Id)!.YawDeg);

        layout = layout.Remove(a.Id);
        Assert.Equal("B", Assert.Single(layout.Screens).Name);
    }
}
