using XrShift.Scene;

namespace XrShift.Scene.Tests;

public class DeskTests
{
    static readonly DisplayOptics Optics = DisplayOptics.OnePro;
    const string Tail = "#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

    // The monitors of the real failure: an LG ultrawide at the first desk, the laptop's own panel and a Samsung
    // ultrawide at the second, and a virtual monitor that goes along to both.
    static readonly MonitorFacts Lg = new(@"\\?\DISPLAY#GSM7817#5&273dee&0&UID4356" + Tail, "LG SMART WQHD", false, true, 3440, 1440);
    static readonly MonitorFacts Panel = new(@"\\?\DISPLAY#BOE0B8B#4&1a8a6d3d&0&UID8388688" + Tail, null, false, true, 2560, 1600);
    static readonly MonitorFacts Samsung = new(@"\\?\DISPLAY#SAM7428#5&1b2c3d&0&UID4357" + Tail, "LC34G55T", false, false, 3440, 1440);
    static readonly MonitorFacts Extra = new(@"\\?\DISPLAY#PSCCDD0#1&28a6823a&0&UID256" + Tail, "ParsecVDA", true, false, 1920, 1080);

    static bool IsVirtual(string id) => id.Contains("#PSCCDD", StringComparison.OrdinalIgnoreCase);

    static (Layout Layout, IReadOnlyList<string> Hidden) Fit(Layout layout, params MonitorFacts[] here) =>
        Desks.Fit(layout, here, here, Optics, IsVirtual);

    static IEnumerable<string> Names(Layout layout) => layout.Screens.Select(s => s.Name);

    [Fact]
    public void NewScreensAreNamedAfterTheirMonitor()
    {
        Assert.Equal("LG SMART WQHD", ScreenNames.For(Lg, []));
        Assert.Equal("Laptop", ScreenNames.For(Panel, []));
        Assert.Equal("Extra 1", ScreenNames.For(Extra, []));
        Assert.Equal("Extra 2", ScreenNames.For(Extra, ["Extra 1"]));
        // A monitor Windows told nothing about is not taken for the laptop's.
        Assert.Equal("Monitor", ScreenNames.For(new MonitorFacts(@"\\.\DISPLAY9", null, false, false, 1920, 1080), []));
    }

    [Fact]
    public void NoTwoScreensGetTheSameName()
    {
        Assert.Equal("LG SMART WQHD 2", ScreenNames.For(Lg, ["lg smart wqhd"]));
        Assert.Equal("Laptop 3", ScreenNames.For(Panel, ["Laptop", "Laptop 2"]));

        var twin = Lg with { Id = Lg.Id.Replace("UID4356", "UID4400"), Primary = false };
        Assert.Equal(["LG SMART WQHD", "LG SMART WQHD 2"], Names(Fit(new Layout(), Lg, twin).Layout));
    }

    [Fact]
    public void NamesTheWearerGaveAreKept()
    {
        Layout mine = Fit(new Layout(), Lg).Layout;
        mine = mine.With(mine.Screens[0] with { Name = "Big one" });
        Assert.Equal(["Big one", "Extra 1"], Names(Fit(mine, Lg, Extra).Layout));
    }

    [Fact]
    public void ANewScreenGoesBesideTheOthersOnTheSideWithRoom()
    {
        var layout = new Layout().Add(new ScreenSpec { Name = "Main", Scale = 0.94f });
        // Something already far out on the right: the new one goes left instead of further out.
        layout = layout.Add(new ScreenSpec { Name = "Right", Scale = 0.94f, YawDeg = -50 });
        (ScreenSpec placed, bool shown) = layout.PlaceInReach(new ScreenSpec { Name = "New", Scale = 0.94f }, 1920, 1080, Optics);
        Assert.True(shown);
        Assert.InRange(placed.YawDeg, 45f, 55f);
    }

    [Fact]
    public void WithNoRoomInReachANewScreenIsAddedHiddenAndReported()
    {
        // Ultrawides fitted to the view are about 62 degrees wide: a second beside the first would be centred 65 degrees out.
        MonitorFacts Wide(int n) => Lg with { Id = Lg.Id.Replace("UID4356", $"UID{n}"), Primary = n == 1 };
        (Layout layout, IReadOnlyList<string> hidden) = Fit(new Layout(), Wide(1), Wide(2));
        Assert.Equal(2, layout.Screens.Count);
        Assert.All(layout.Screens, s => Assert.InRange(s.YawDeg, -60f, 60f));
        Assert.Equal(["LG SMART WQHD 2"], hidden);
        Assert.False(layout.Screens[1].Visible);
    }

    [Fact]
    public void NoMoreThanTheCapAreShownAtOnce()
    {
        var layout = new Layout().Add(new ScreenSpec { Scale = 0.3f }).Add(new ScreenSpec { Scale = 0.3f, YawDeg = 20 });
        Assert.False(layout.PlaceInReach(new ScreenSpec { Scale = 0.3f }, 1920, 1080, Optics, maxShown: 2).Shown);
        Assert.True(layout.PlaceInReach(new ScreenSpec { Scale = 0.3f }, 1920, 1080, Optics, maxShown: 3).Shown);
    }

    [Fact]
    public void NothingChangedGivesBackTheSameLayout()
    {
        Layout layout = Fit(new Layout(), Lg, Extra).Layout;
        Assert.Same(layout, Fit(layout, Lg, Extra).Layout);
    }

    [Fact]
    public void AnOldFileLoadsUnchangedAtTheDeskItWasSavedAt()
    {
        string old = $$"""
        { "Profiles": [ { "Name": "Build day", "Layout": { "Screens": [
            { "Id": "d8fae401", "Name": "Laptop screen", "Source": "display:{{Lg.Id.Replace(@"\", @"\\")}}", "YawDeg": 39.3, "Scale": 0.705 },
            { "Id": "d46a3004", "Name": "Extra monitor 1", "Source": "display:{{Extra.Id.Replace(@"\", @"\\")}}", "YawDeg": -57.4, "Scale": 0.94 }
        ] } } ], "Active": 0 }
        """;
        Assert.True(Workspace.TryParse(old, out Workspace workspace));
        Layout before = workspace.Current.Layout;
        Layout after = Fit(before, Lg, Extra).Layout;
        Assert.Equal(before.Screens, after.Screens);
        Assert.Empty(after.Desks);
    }

    [Fact]
    public void AnOldFilesScreensOfMonitorsNotHereArePutAsideNotDeleted()
    {
        var old = new Layout()
            .Add(new ScreenSpec { Name = "Laptop screen", Source = ScreenSpec.DisplaySource(Lg.Id) })
            .Add(new ScreenSpec { Name = "Monitor 3", Source = ScreenSpec.DisplaySource(Panel.Id), YawDeg = -3.5f })
            .Add(new ScreenSpec { Name = "Monitor 6", Source = ScreenSpec.DisplaySource(@"\\.\DISPLAY10"), YawDeg = -108 });
        Layout layout = Fit(old, Lg).Layout;
        Assert.Equal(["Laptop screen"], Names(layout));
        Assert.Equal(["Monitor 3", "Monitor 6"], Assert.Single(layout.Desks).Screens.Select(s => s.Name));
    }

    [Fact]
    public void ScreensOfOtherDesksSurviveSavingAndLoading()
    {
        Layout layout = Fit(Fit(new Layout(), Lg, Extra).Layout, Panel, Samsung, Extra).Layout;
        Assert.True(Workspace.TryParse(new Workspace().WithLayout(layout).ToJson(), out Workspace loaded));
        Layout back = loaded.Current.Layout;
        Assert.Equal(layout.Desk, back.Desk);
        Assert.Equal(layout.Screens, back.Screens);
        Assert.Equal(layout.Desks[0].Screens, Assert.Single(back.Desks).Screens);
    }

    [Fact]
    public void ReplayOfMovingBetweenTwoDesks()
    {
        // The first desk: the LG ultrawide as main monitor, the laptop's lid closed. Then a virtual monitor is added.
        Layout layout = Fit(new Layout(), Lg).Layout;
        (layout, IReadOnlyList<string> hidden) = Fit(layout, Lg, Extra);
        Assert.Empty(hidden);
        Assert.Equal(["LG SMART WQHD", "Extra 1"], Names(layout));
        Layout firstDesk = layout;

        // The second desk: the laptop's panel as main monitor beside a Samsung ultrawide. The virtual monitor comes along.
        (layout, hidden) = Fit(layout, Panel, Samsung, Extra);
        Assert.Empty(hidden);
        Assert.Equal(["Laptop", "LC34G55T", "Extra 1"], Names(layout));
        Assert.Equal(0f, layout.Screens[0].YawDeg);
        Assert.All(layout.Screens, s => Assert.True(s.Visible && MathF.Abs(s.YawDeg) <= 60f, $"{s.Name} at {s.YawDeg:F0}"));
        Layout secondDesk = layout;

        // Back at the first desk: its own screens as they were, none of the second desk's.
        layout = Fit(layout, Lg, Extra).Layout;
        Assert.Equal(firstDesk.Screens, layout.Screens);

        // And at the second again: also as it was left.
        layout = Fit(layout, Panel, Samsung, Extra).Layout;
        Assert.Equal(secondDesk.Screens, layout.Screens);

        // Nothing lost: every screen is still in the layout, at this desk or kept for the other.
        Assert.Equal(firstDesk.Screens, Assert.Single(layout.Desks).Screens);
        Assert.Equal(layout.Screens.Count, layout.Screens.Select(s => s.Name).Distinct().Count());
    }

    [Fact]
    public void AListWithNoRealMonitorLeavesTheDeskAlone()
    {
        Layout layout = Fit(new Layout(), Lg, Extra).Layout;
        // Stationary mode only hands over the virtual monitors.
        (Layout after, _) = Desks.Fit(layout, [Extra], [Extra], Optics, IsVirtual);
        Assert.Same(layout, after);
    }

    [Fact]
    public void TheLaptopPanelMovingToTheOtherGraphicsCardIsTheSameDesk()
    {
        Layout layout = Fit(new Layout(), Panel).Layout;
        MonitorFacts moved = Panel with { Id = Panel.Id.Replace("4&1a8a6d3d", "5&99aa00") };
        Assert.Equal(Desks.KeyOf([Panel.Id]), Desks.KeyOf([moved.Id]));
        Layout after = Fit(layout, moved).Layout;
        Assert.Equal(layout.Screens[0].Id, Assert.Single(after.Screens).Id);
        Assert.Equal(moved.Id, after.Screens[0].DisplayName);
    }
}
