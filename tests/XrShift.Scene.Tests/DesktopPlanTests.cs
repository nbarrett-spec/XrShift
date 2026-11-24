using System.Drawing;
using XrShift.Scene;

namespace XrShift.Scene.Tests;

public class DesktopPlanTests
{
    static readonly Rectangle Main = new(0, 0, 3440, 1440);
    static DesktopPlan.Screen Extra(string id, float yaw, float pitch) => new(id, 1920, 1080, yaw, pitch);

    [Fact]
    public void AScreenToTheRightGoesRightAndOneAboveItGoesAboveIt()
    {
        // The wearer's real arrangement on 2026-11-24: one extra screen right of the main one, a second above the first.
        var places = DesktopPlan.Place(Main, 15, 0, [Extra("above", -40, 29), Extra("right", -44, 0)], []);
        Assert.Contains(("right", 3440, 0), places);
        Assert.Contains(("above", 3440, -1080), places);
    }

    [Fact]
    public void LeftGoesLeftAndBelowGoesBelow()
    {
        var places = DesktopPlan.Place(Main, 0, 0, [Extra("left", 50, 0), Extra("below", 0, -30)], []);
        Assert.Contains(("left", -1920, 0), places);
        Assert.Contains(("below", 0, 1440), places);
    }

    [Fact]
    public void ATakenSpotIsSteppedOverInTheSameDirection()
    {
        // The laptop's own panel already sits left of the main monitor, and the glasses' area below it.
        Rectangle laptop = new(-2560, 0, 2560, 1600), glasses = new(0, 1440, 1920, 1080);
        var places = DesktopPlan.Place(Main, 0, 0, [Extra("left", 60, 0), Extra("below", 0, -30)], [laptop, glasses]);
        Assert.Contains(("left", -2560 - 1920, 0), places);
        Assert.Contains(("below", 0, 1440 + 1080), places);
    }

    [Fact]
    public void TwoScreensOnTheSameSideLineUpOutward()
    {
        var places = DesktopPlan.Place(Main, 0, 0, [Extra("far", -90, 0), Extra("near", -45, 0)], []);
        Assert.Contains(("near", 3440, 0), places);
        Assert.Contains(("far", 3440 + 1920, 0), places);
    }

    [Fact]
    public void NothingEverOverlaps()
    {
        var extras = new[] { Extra("a", -40, 29), Extra("b", -44, 0), Extra("c", -42, 14), Extra("d", 40, 0), Extra("e", 0, 30) };
        var places = DesktopPlan.Place(Main, 0, 0, extras, []);
        List<Rectangle> all = [Main, .. places.Select(p => new Rectangle(p.X, p.Y, 1920, 1080))];
        for (int i = 0; i < all.Count; i++)
            for (int j = i + 1; j < all.Count; j++)
                Assert.False(all[i].IntersectsWith(all[j]), $"{i} overlaps {j}");
    }
}
