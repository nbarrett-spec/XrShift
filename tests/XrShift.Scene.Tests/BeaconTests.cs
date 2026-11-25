using System.Drawing;
using System.Numerics;
using XrShift.Scene;

namespace XrShift.Scene.Tests;

public class BeaconTests
{
    static readonly DisplayOptics Optics = DisplayOptics.OnePro;
    static readonly Rectangle Main = new(0, 0, 1920, 1080), Side = new(1920, 0, 1920, 1080);
    static readonly ScreenSpec Ahead = new() { Id = "ahead", Source = ScreenSpec.DisplaySource("main") };
    static readonly ScreenSpec Right = new() { Id = "right", Source = ScreenSpec.DisplaySource("side"), YawDeg = -70 };

    [Fact]
    public void ThePointerInTheMiddleOfAScreenIsAtThatScreensMiddleInTheRoom()
    {
        Vector3? spot = RoomSpot.Of(new Point(960, 540), [(Ahead, Main), (Right, Side)], Optics);
        Assert.NotNull(spot);
        Vector3 centre = Ahead.ToPanel(1920, 1080, Optics).Centre;
        Assert.True(Vector3.Distance(centre, spot.Value) < 0.01f, $"{spot} is not {centre}");
        Assert.True(RoomSpot.InView(spot.Value, Quaternion.Identity, Optics));
    }

    [Fact]
    public void FindingTheSpotAgainFromTheRoomGivesTheSamePointOnTheScreen()
    {
        // FindGaze's way round: the direction to the spot hits the screen where the pointer is.
        Panel panel = Ahead.ToPanel(1920, 1080, Optics);
        Vector3 spot = RoomSpot.Of(new Point(300, 200), [(Ahead, Main)], Optics)!.Value;
        Assert.True(panel.TryHit(Vector3.Normalize(spot), out float s, out float t));
        Assert.InRange((s + 0.5f) * 1920, 298f, 302f);
        Assert.InRange((0.5f - t) * 1080, 198f, 202f);
    }

    [Fact]
    public void APointerOnAScreenFarToTheSideIsOutOfViewUntilTheWearerTurns()
    {
        Vector3 spot = RoomSpot.Of(new Point(2880, 540), [(Ahead, Main), (Right, Side)], Optics)!.Value;
        Assert.False(RoomSpot.InView(spot, Quaternion.Identity, Optics));
        Assert.True(RoomSpot.Angles(spot, Quaternion.Identity).Across > 60f); // to the right
        Quaternion turned = Quaternion.CreateFromYawPitchRoll(-70f * MathF.PI / 180f, 0, 0);
        Assert.True(RoomSpot.InView(spot, turned, Optics));
    }

    [Fact]
    public void APointerOnNoShownScreenIsNowhere()
    {
        Assert.Null(RoomSpot.Of(new Point(2880, 540), [(Ahead, Main), (Right with { Visible = false }, Side)], Optics));
        Assert.Null(RoomSpot.Of(new Point(-50, 10), [(Ahead, Main)], Optics));
    }

    [Fact]
    public void ARingAppearsWhenTheRestingPointerStartsMovingAndGoesAfterASecond()
    {
        var beacon = new PointerBeacon();
        Assert.True(beacon.Step(20, moved: true, inView: true).Ring); // never moved before: it has rested long enough
        Assert.True(beacon.Step(20.5, moved: true, inView: true).Ring);
        Assert.False(beacon.Step(21.2, moved: true, inView: true).Ring); // a second on, still moving: no ring
        Assert.False(beacon.Step(22.5, moved: false, inView: true).Ring);
        // A pause of a few seconds is not long enough to lose the pointer: no ring.
        Assert.False(beacon.Step(26, moved: true, inView: true).Ring);
        // Left alone for a good while: the next move rings again.
        Assert.True(beacon.Step(26 + PointerBeacon.RestSeconds + 1, moved: true, inView: true).Ring);
    }

    [Fact]
    public void APointerIsFreshForAFewSecondsAfterItMoves()
    {
        var beacon = new PointerBeacon();
        Assert.False(beacon.Step(1, moved: false, inView: false).Fresh);
        beacon.Step(2, moved: true, inView: false);
        Assert.True(beacon.Step(4.5, moved: false, inView: false).Fresh);
        Assert.False(beacon.Step(5.5, moved: false, inView: false).Fresh);
    }

    [Fact]
    public void TheWearerIsToldOnceHowToBringALostPointerBack()
    {
        var beacon = new PointerBeacon();
        Assert.False(beacon.Step(0, moved: false, inView: false).TellMouseToMe);
        Assert.False(beacon.Step(2, moved: true, inView: false).TellMouseToMe); // not lost for long yet
        Assert.True(beacon.Step(4, moved: true, inView: false).TellMouseToMe);
        Assert.False(beacon.Step(9, moved: true, inView: false).TellMouseToMe); // once per session

        // Seen again resets how long it has been lost, but the hint was given already.
        var fresh = new PointerBeacon();
        fresh.Step(0, false, false);
        fresh.Step(2.9, false, true);
        Assert.False(fresh.Step(4, moved: true, inView: false).TellMouseToMe);
    }

    [Fact]
    public void ABroughtWindowKeepsItsSizeIfItFitsAndIsShrunkIfNot()
    {
        var work = new Rectangle(1920, 0, 1920, 1040);
        Assert.Equal(new Rectangle(1920 + 560, 220, 800, 600), WindowFit.Into(new Size(800, 600), work));
        Assert.Equal(work, WindowFit.Into(new Size(2560, 1400), work));
        Assert.Equal(new Rectangle(1920, 20, 1920, 1000), WindowFit.Into(new Size(3000, 1000), work));
    }

    [Fact]
    public void ScreensBroughtBackByAChangeOfDeskAreNotNew()
    {
        var home = new ScreenSpec { Id = "home", Source = ScreenSpec.DisplaySource("home") };
        var away = new ScreenSpec { Id = "away", Source = ScreenSpec.DisplaySource("away") };
        var before = new Layout { Desk = "A", Screens = [home], Desks = [new DeskLayout { Key = "B", Screens = [away] }] };
        var brand = new ScreenSpec { Id = "brand-new" };
        var after = new Layout { Desk = "B", Screens = [away, brand], Desks = [new DeskLayout { Key = "A", Screens = [home] }] };
        Assert.Equal(["brand-new"], Desks.Added(before, after).Select(s => s.Id));
        Assert.Empty(Desks.Added(before, before));
    }
}
