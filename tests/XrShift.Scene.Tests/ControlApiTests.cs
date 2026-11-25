using System.Text.Json;
using XrShift.Scene;

namespace XrShift.Scene.Tests;

public class ControlApiTests : IControlHost
{
    Deck _deck = new();
    readonly List<string> _focused = [];
    Workspace IControlHost.Workspace => _workspace;
    void IControlHost.Use(Workspace changed) => _workspace = changed;
    Deck IControlHost.Deck => _deck;
    void IControlHost.Use(Deck changed) => _deck = changed;
    void IControlHost.Focus(string id) => _focused.Add(id);
    readonly List<string> _spawned = [], _told = [];
    IReadOnlyList<string> IControlHost.Projects => ["Abel platform", "Agritec"];
    string? IControlHost.Spawn(string project) { if (project == "Agritec") return "Folder is missing."; _spawned.Add(project); return null; }
    string? IControlHost.Tell(string id, string text) { _told.Add($"{id}: {text}"); return null; }
    readonly List<string> _widgets = [];
    string? IControlHost.OpenWidget(string label, Uri page) { _widgets.Add($"{label} -> {page}"); return null; }

    [Fact]
    public void AWidgetOpensOnlyForARealWebAddress()
    {
        Call("""{"jsonrpc":"2.0","id":1,"method":"widget.open","params":{"label":"Calendar","url":"https://calendar.google.com/"}}""");
        Assert.Equal(["Calendar -> https://calendar.google.com/"], _widgets);
        foreach (string bad in new[] { "calendar", "file:///C:/Windows/System32/calc.exe", "javascript:alert(1)", "ms-settings:display", "//calendar.google.com" })
            Assert.Equal(-32602, Call($$$"""{"jsonrpc":"2.0","id":2,"method":"widget.open","params":{"label":"x","url":"{{{bad}}}"}}""").GetProperty("error").GetProperty("code").GetInt32());
        Assert.Single(_widgets);
    }

    Workspace _workspace = new Workspace()
        .WithLayout(new Layout().Add(new ScreenSpec { Name = "Laptop" }).Add(new ScreenSpec { Name = "Mail", Source = ScreenSpec.WindowSource("outlook", "Inbox"), Visible = false }))
        .Rename("Sales day");

    JsonElement Call(string line, out bool subscribe)
    {
        string? reply = ControlApi.Handle(line, this, out subscribe);
        return JsonDocument.Parse(reply!).RootElement;
    }

    JsonElement Call(string line) => Call(line, out _);

    [Fact]
    public void ListsTheSceneWithKindAndState()
    {
        JsonElement result = Call("""{"jsonrpc":"2.0","id":1,"method":"scene.list"}""").GetProperty("result");
        Assert.Equal("Sales day", result.GetProperty("layout").GetString());
        JsonElement[] objects = result.GetProperty("objects").EnumerateArray().ToArray();
        Assert.Equal(["screen", "window"], objects.Select(o => o.GetProperty("kind").GetString()));
        Assert.Equal(["visible", "hidden"], objects.Select(o => o.GetProperty("state").GetString()));
        Assert.Equal("Laptop", objects[0].GetProperty("label").GetString());
    }

    [Fact]
    public void SavesALayoutUnderANewNameAndLoadsTheOldOneBack()
    {
        Assert.Equal("Build day", Call("""{"jsonrpc":"2.0","id":"a","method":"layout.save","params":{"name":"Build day"}}""").GetProperty("result").GetProperty("name").GetString());
        Assert.Equal(["Sales day", "Build day"], _workspace.Profiles.Select(p => p.Name));
        Assert.Equal("Build day", _workspace.Current.Name);

        JsonElement reply = Call("""{"jsonrpc":"2.0","id":"b","method":"layout.load","params":{"name":"sales DAY"}}""");
        Assert.Equal("b", reply.GetProperty("id").GetString());
        Assert.Equal("Sales day", _workspace.Current.Name);
    }

    [Fact]
    public void SavingOverAnExistingNameReplacesItsScreens()
    {
        Call("""{"jsonrpc":"2.0","id":1,"method":"layout.save","params":{"name":"Build day"}}""");
        _workspace = _workspace.WithLayout(_workspace.Current.Layout.Add(new ScreenSpec { Name = "Logs" }));
        Call("""{"jsonrpc":"2.0","id":2,"method":"layout.save","params":{"name":"Sales day"}}""");
        Assert.Equal(2, _workspace.Profiles.Count);
        Assert.Equal("Sales day", _workspace.Current.Name);
        Assert.Equal(["Laptop", "Mail", "Logs"], _workspace.Current.Layout.Screens.Select(s => s.Name));
    }

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"layout.load","params":{"name":"Nope"}}""", -32602)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"layout.load"}""", -32602)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"object.explode"}""", -32601)]
    [InlineData("""{"jsonrpc":"2.0","id":1}""", -32600)]
    [InlineData("""[1,2]""", -32600)]
    [InlineData("""{not json""", -32700)]
    public void BadRequestsGetAnErrorAndChangeNothing(string line, int code)
    {
        Workspace before = _workspace;
        Assert.Equal(code, Call(line).GetProperty("error").GetProperty("code").GetInt32());
        Assert.Same(before, _workspace);
    }

    [Fact]
    public void SubscribingIsFlaggedForTheCarrier()
    {
        Call("""{"jsonrpc":"2.0","id":1,"method":"events.subscribe"}""", out bool subscribe);
        Assert.True(subscribe);
        Call("""{"jsonrpc":"2.0","id":2,"method":"scene.list"}""", out subscribe);
        Assert.False(subscribe);
    }

    [Fact]
    public void ARequestWithoutAnIdGetsNoReply()
    {
        Assert.Null(ControlApi.Handle("""{"jsonrpc":"2.0","method":"layout.save","params":{"name":"Quiet"}}""", this, out _));
        Assert.Equal("Quiet", _workspace.Current.Name);
    }

    [Fact]
    public void CreatesUpdatesFocusesAndRemovesAnObject()
    {
        string id = Call("""{"jsonrpc":"2.0","id":1,"method":"object.create","params":{"kind":"bot","label":"Quotes","position":{"yawDeg":30,"pitchDeg":-10}}}""")
            .GetProperty("result").GetProperty("id").GetString()!;
        SceneObject made = Assert.Single(_deck.Objects);
        Assert.Equal(("Quotes", BotState.Idle, 30f, -10f), (made.Label, made.State, made.YawDeg, made.PitchDeg));

        Call($$$"""{"jsonrpc":"2.0","id":2,"method":"object.update","params":{"id":"{{{id}}}","state":"needs_you","badge":"2"}}""");
        Assert.Equal((BotState.NeedsYou, "2", 30f), (_deck.Find(id)!.State, _deck.Find(id)!.Badge, _deck.Find(id)!.YawDeg));

        JsonElement listed = Call("""{"jsonrpc":"2.0","id":3,"method":"scene.list"}""").GetProperty("result").GetProperty("objects").EnumerateArray().Last();
        Assert.Equal(("bot", "needs_you"), (listed.GetProperty("kind").GetString(), listed.GetProperty("state").GetString()));

        Call($$$"""{"jsonrpc":"2.0","id":4,"method":"object.focus","params":{"id":"{{{id}}}"}}""");
        Assert.Equal([id], _focused);

        Call($$$"""{"jsonrpc":"2.0","id":5,"method":"object.remove","params":{"id":"{{{id}}}"}}""");
        Assert.Empty(_deck.Objects);
    }

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"object.create","params":{"kind":"dragon"}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"object.create","params":{"state":"napping"}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"object.update","params":{"id":"missing","state":"done"}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"object.focus","params":{"id":"missing"}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"bot.report","params":{"id":"s1"}}""")]
    public void BadObjectRequestsAreRefused(string line)
    {
        Assert.Equal(-32602, Call(line).GetProperty("error").GetProperty("code").GetInt32());
        Assert.Empty(_deck.Objects);
        Assert.Empty(_focused);
    }

    [Fact]
    public void AHookReportMakesAndMovesABot()
    {
        Assert.Equal("working", Call("""{"jsonrpc":"2.0","id":1,"method":"bot.report","params":{"id":"s1","event":"UserPromptSubmit","label":"abel","pid":4242}}""")
            .GetProperty("result").GetProperty("state").GetString());
        Assert.Equal(("abel", 4242), (_deck.Find("s1")!.Label, _deck.Find("s1")!.Pid));
    }

    string Say(string text) =>
        Call($$$"""{"jsonrpc":"2.0","id":9,"method":"voice.say","params":{"text":"{{{text}}}"}}""").ToString();

    [Fact]
    public void SpokenCommandsDoWhatTheMatchingRequestsDo()
    {
        _deck = _deck.With(new SceneObject { Id = "b1", Label = "abel-builder-platform" }).With(new SceneObject { Id = "b2", Label = "Quotes" });
        Call("""{"jsonrpc":"2.0","id":1,"method":"layout.save","params":{"name":"Build day"}}""");

        Say("Load Sales Day.");
        Assert.Equal("Sales day", _workspace.Current.Name);
        Say("switch to the layout build day");
        Assert.Equal("Build day", _workspace.Current.Name);

        Say("hide the laptop");
        Assert.False(_workspace.Current.Layout.Screens[0].Visible);
        Say("show laptop");
        Assert.True(_workspace.Current.Layout.Screens[0].Visible);

        Say("open quotes");
        Assert.Equal(["b2"], _focused);

        Say("tell abel builder platform to run the tests and fix what fails");
        Say("Ask Quotes to reply to Ben");
        Assert.Equal(["b1: run the tests and fix what fails", "b2: reply to ben"], _told);

        Say("new bot in abel platform");
        Assert.Equal(["Abel platform"], _spawned);
    }

    [Theory]
    [InlineData("open the pod bay doors", "Nothing by that name.")]
    [InlineData("hide quotes", "No screen by that name.")]
    [InlineData("load friday", "No layout by that name.")]
    [InlineData("tell nobody to do it", "No bot by that name.")]
    [InlineData("new bot in narnia", "No project by that name.")]
    [InlineData("new bot in agritec", "Folder is missing.")]
    [InlineData("make me a sandwich", "Try: open, hide, load, new bot in, or tell ... to ...")]
    public void WhatWasNotUnderstoodIsSaidBackAndChangesNothing(string heard, string why)
    {
        _deck = _deck.With(new SceneObject { Id = "b2", Label = "Quotes" });
        Workspace before = _workspace;
        Assert.Contains(why, Say(heard));
        Assert.Same(before, _workspace);
        Assert.Empty(_focused);
        Assert.Empty(_told);
        Assert.Empty(_spawned);
    }

    [Fact]
    public void ANameThatFitsTwoThingsIsNotGuessedAt()
    {
        _deck = _deck.With(new SceneObject { Id = "a", Label = "abel quotes" }).With(new SceneObject { Id = "b", Label = "abel orders" });
        Assert.Contains("Nothing by that name.", Say("open abel"));
        Say("open abel orders");
        Assert.Equal(["b"], _focused);
    }
}
