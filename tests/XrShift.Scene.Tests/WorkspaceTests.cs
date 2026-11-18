using XrShift.Scene;

namespace XrShift.Scene.Tests;

public class WorkspaceTests
{
    static Workspace TwoScreens() =>
        new Workspace().WithLayout(new Layout().Add(new ScreenSpec { Name = "Laptop" }).Add(new ScreenSpec { Name = "Side", YawDeg = -46 }));

    [Fact]
    public void StartsWithOneEmptyProfile()
    {
        var workspace = new Workspace();
        Assert.Equal("Main", Assert.Single(workspace.Profiles).Name);
        Assert.Empty(workspace.Current.Layout.Screens);
    }

    [Fact]
    public void ACopyStartsTheSameAndThenGoesItsOwnWay()
    {
        Workspace workspace = TwoScreens().AddCopy("Movie");
        Assert.Equal(1, workspace.Active);
        Assert.Equal("Movie", workspace.Current.Name);
        Assert.Equal(["Laptop", "Side"], workspace.Current.Layout.Screens.Select(s => s.Name));

        // Editing the copy leaves the original alone.
        ScreenSpec side = workspace.Current.Layout.Screens[1];
        workspace = workspace.WithLayout(workspace.Current.Layout.With(side with { Scale = 2f }));
        Assert.Equal(2f, workspace.Current.Layout.Screens[1].Scale);
        Assert.Equal(0.85f, workspace.Select(0).Current.Layout.Screens[1].Scale);

        // The copies are separate screens, not the same one twice.
        Assert.Empty(workspace.Profiles[0].Layout.Screens.Select(s => s.Id).Intersect(workspace.Profiles[1].Layout.Screens.Select(s => s.Id)));
    }

    [Fact]
    public void NewProfilesGetNamesThatAreNotTaken()
    {
        Workspace workspace = new Workspace().AddCopy().AddCopy();
        Assert.Equal(["Main", "Profile 2", "Profile 3"], workspace.Profiles.Select(p => p.Name));

        // If the next number is already used as a name, it moves on to the one after.
        Workspace clash = workspace.Rename("Profile 4").AddCopy();
        Assert.Equal("Profile 5", clash.Current.Name);
        Assert.Equal(4, clash.Profiles.Select(p => p.Name).Distinct().Count());
    }

    [Fact]
    public void SelectingOutOfRangeChangesNothing()
    {
        Workspace workspace = new Workspace().AddCopy();
        Assert.Same(workspace, workspace.Select(5));
        Assert.Same(workspace, workspace.Select(-1));
        Assert.Equal(0, workspace.Select(0).Active);
    }

    [Fact]
    public void TheLastProfileCannotBeRemoved()
    {
        var one = new Workspace();
        Assert.Same(one, one.RemoveCurrent());

        Workspace two = one.AddCopy("Second");
        Workspace back = two.RemoveCurrent();
        Assert.Equal("Main", Assert.Single(back.Profiles).Name);
        Assert.Equal(0, back.Active);
    }

    [Fact]
    public void RenameIgnoresBlankNames()
    {
        var workspace = new Workspace();
        Assert.Same(workspace, workspace.Rename("  "));
        Assert.Equal("Work", workspace.Rename(" Work ").Current.Name);
    }

    [Fact]
    public void RoundTripsThroughJson()
    {
        Workspace workspace = TwoScreens().Rename("Work").AddCopy("Movie");
        Assert.True(Workspace.TryParse(workspace.ToJson(), out Workspace back));
        Assert.Equal(1, back.Active);
        Assert.Equal(["Work", "Movie"], back.Profiles.Select(p => p.Name));
        Assert.Equal(workspace.Profiles[0].Layout.Screens, back.Profiles[0].Layout.Screens);
        Assert.Equal(workspace.Profiles[1].Layout.Screens, back.Profiles[1].Layout.Screens);
    }

    [Fact]
    public void ReadsTheOlderSingleArrangementFile()
    {
        string old = new Layout().Add(new ScreenSpec { Name = "Laptop" }).Add(new ScreenSpec { Name = "Screen 2", YawDeg = -46 }).ToJson();
        Assert.True(Workspace.TryParse(old, out Workspace workspace));
        Assert.Equal("Main", Assert.Single(workspace.Profiles).Name);
        Assert.Equal(["Laptop", "Screen 2"], workspace.Current.Layout.Screens.Select(s => s.Name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{\"Profiles\": 3}")]
    [InlineData("{\"Screens\": 3}")]
    public void RejectsBrokenFiles(string text) => Assert.False(Workspace.TryParse(text, out _));

    [Fact]
    public void RepairsWhatItCan()
    {
        Assert.True(Workspace.TryParse(
            "{\"Profiles\":[{\"Name\":\"\",\"Layout\":{\"Screens\":[{\"Id\":\"a\",\"YawDeg\":999}]}},{\"Name\":\"B\"}],\"Active\":9}",
            out Workspace workspace));
        Assert.Equal(["Profile", "B"], workspace.Profiles.Select(p => p.Name));
        Assert.Equal(1, workspace.Active);
        Assert.Equal(180f, workspace.Profiles[0].Layout.Screens[0].YawDeg);
        Assert.Empty(workspace.Profiles[1].Layout.Screens);

        Assert.True(Workspace.TryParse("{\"Profiles\":[]}", out Workspace empty));
        Assert.Single(empty.Profiles);
    }
}
