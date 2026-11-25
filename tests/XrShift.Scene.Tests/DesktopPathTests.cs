using System.Drawing;
using XrShift.Scene;

namespace XrShift.Scene.Tests;

public class DesktopPathTests
{
    // The user's desk: laptop 1920x1200 at the left, glasses 1920x1080 in the middle, a virtual monitor beyond.
    static readonly Rectangle Laptop = new(0, 0, 1920, 1200);
    static readonly Rectangle Glasses = new(1920, 0, 1920, 1080);
    static readonly Rectangle Virtual = new(3840, 0, 1920, 1080);

    [Fact]
    public void CarriesThePointerAcrossToTheMonitorOnTheFarSide()
    {
        Point landed = DesktopPath.Redirect(new Point(1925, 540), new Point(1915, 538), Glasses, [Laptop, Virtual]);
        Assert.Equal(new Point(3841, 540), landed);

        // And back again.
        Point back = DesktopPath.Redirect(new Point(3835, 300), new Point(3845, 300), Glasses, [Laptop, Virtual]);
        Assert.Equal(1918, back.X);
        Assert.InRange(back.Y, 330, 336); // same relative height on a taller screen: 300/1080 of 1200
    }

    [Fact]
    public void WithNothingOnTheFarSideThePointerStaysWhereItWas()
    {
        var before = new Point(1915, 538);
        Assert.Equal(before, DesktopPath.Redirect(new Point(1925, 540), before, Glasses, [Laptop]));
    }

    [Fact]
    public void WorksWhenTheGlassesSitAboveOrBelow()
    {
        var glassesAbove = new Rectangle(0, -1080, 1920, 1080);
        var top = new Rectangle(0, -2160, 1920, 1080);
        Point landed = DesktopPath.Redirect(new Point(960, -5), new Point(960, 3), glassesAbove, [Laptop, top]);
        Assert.Equal(new Point(960, -1082), landed);

        Point down = DesktopPath.Redirect(new Point(400, -1075), new Point(400, -1085), glassesAbove, [Laptop, top]);
        Assert.Equal(new Point(400, 1), down);
    }

    [Fact]
    public void PicksTheNearestMonitorWhenSeveralLieBeyond()
    {
        var farther = new Rectangle(5760, 0, 1920, 1080);
        Point landed = DesktopPath.Redirect(new Point(1930, 100), new Point(1900, 100), Glasses, [Laptop, farther, Virtual]);
        Assert.Equal(3841, landed.X);
    }

    [Fact]
    public void APointerThatStartedInsideGoesToTheMiddleOfTheFirstMonitor()
    {
        Point landed = DesktopPath.Redirect(new Point(2500, 500), new Point(2400, 480), Glasses, [Laptop, Virtual]);
        Assert.Equal(new Point(960, 600), landed);
    }

    [Fact]
    public void AlwaysLandsOutsideTheBlockedAreaAndOnAMonitor()
    {
        Rectangle[] allowed = [Laptop, Virtual];
        foreach (int y in (int[])[0, 1, 500, 1079])
        {
            foreach ((Point inside, Point from) in ((Point, Point)[])[(new(1920, y), new(1919, y)), (new(3839, y), new(3840, y))])
            {
                Point landed = DesktopPath.Redirect(inside, from, Glasses, allowed);
                Assert.False(Glasses.Contains(landed), $"{landed} is still on the glasses");
                Assert.Contains(allowed, r => r.Contains(landed));
            }
        }
    }

    [Fact]
    public void WithNoMonitorsToGoToItLeavesThePointerAlone()
    {
        var p = new Point(2000, 500);
        Assert.Equal(p, DesktopPath.Redirect(p, new Point(1900, 500), Glasses, []));
    }

    // Further right on the same desktop: a monitor whose screen is hidden, then one that is shown.
    static readonly Rectangle Hidden = new(5760, 0, 1920, 1080);
    static readonly Rectangle Beyond = new(7680, 0, 1920, 1080);

    [Fact]
    public void AMonitorWithNoScreenBetweenTwoShownOnesIsCrossedLikeTheGlasses()
    {
        Rectangle[] monitors = [Laptop, Virtual, Hidden, Beyond];
        Point landed = DesktopPath.Redirect(new Point(5765, 400), new Point(5755, 400), [Glasses, Hidden], monitors);
        Assert.Equal(new Point(7681, 400), landed);

        // Back the other way it lands on the virtual monitor, not on the unseen one.
        Point back = DesktopPath.Redirect(new Point(7675, 400), new Point(7685, 400), [Glasses, Hidden], monitors);
        Assert.Equal(new Point(5758, 400), back);
    }

    [Fact]
    public void AnUnseenMonitorAtTheEndStopsThePointerAtTheEdge()
    {
        // The laptop, and to its right a monitor whose screen is hidden; nothing beyond.
        var side = new Rectangle(1920, 0, 1920, 1080);
        var before = new Point(1915, 300);
        Assert.Equal(before, DesktopPath.Redirect(new Point(1925, 300), before, [side], [Laptop, side]));
    }

    [Fact]
    public void WithEveryMonitorButOneUnseenThePointerStaysOnThatOne()
    {
        Rectangle[] monitors = [Laptop, Virtual, Hidden, Beyond];
        Rectangle[] blocked = [Glasses, Virtual, Hidden, Beyond];
        var before = new Point(1915, 538);
        Assert.Equal(before, DesktopPath.Redirect(new Point(1925, 540), before, blocked, monitors));
        // Started out on an unseen one: it goes to the one monitor left.
        Assert.Equal(new Point(960, 600), DesktopPath.Redirect(new Point(6000, 500), new Point(6000, 500), blocked, monitors));
        // Not in any blocked area: left alone.
        Assert.Equal(new Point(100, 100), DesktopPath.Redirect(new Point(100, 100), before, blocked, monitors));
    }

    [Fact]
    public void UnseenMonitorsAreTheHiddenOnesAndTheOnesWithNoScreen()
    {
        var layout = new Layout()
            .Add(new ScreenSpec { Source = ScreenSpec.DisplaySource("laptop") })
            .Add(new ScreenSpec { Source = ScreenSpec.DisplaySource("virtual"), Visible = false });
        List<Rectangle> unseen = DesktopPath.Unseen(layout, [("LAPTOP", Laptop), ("virtual", Virtual), ("other", Hidden)]);
        Assert.Equal(new[] { Virtual, Hidden }, unseen);
    }
}
