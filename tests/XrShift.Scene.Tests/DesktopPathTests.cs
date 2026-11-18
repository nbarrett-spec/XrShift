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
}
