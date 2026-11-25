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
    readonly List<string> _setup = [];
    System.Text.Json.Nodes.JsonObject? IControlHost.Calibrate(string action, string? from, out string? refused)
    {
        _setup.Add(from is null ? action : $"{action} {from}");
        refused = action == "undo" ? "There is no glasses setup to undo." : null;
        return refused is null ? new System.Text.Json.Nodes.JsonObject { ["active"] = action != "cancel" } : null;
    }

    [Fact]
    public void GlassesSetupIsDrivenStepByStep()
    {
        Assert.True(Call("""{"jsonrpc":"2.0","id":1,"method":"calibration.start","params":{"from":"level"}}""").GetProperty("result").GetProperty("active").GetBoolean());
        foreach (string action in new[] { "press", "retry", "skip", "status", "cancel" })
            Assert.True(Call($$"""{"jsonrpc":"2.0","id":2,"method":"calibration.{{action}}"}""").TryGetProperty("result", out _));
        Assert.Equal(["start level", "press", "retry", "skip", "status", "cancel"], _setup);

        JsonElement refused = Call("""{"jsonrpc":"2.0","id":3,"method":"calibration.undo"}""").GetProperty("error");
        Assert.Equal("There is no glasses setup to undo.", refused.GetProperty("message").GetString());
        Assert.Equal(-32602, Call("""{"jsonrpc":"2.0","id":4,"method":"calibration.start","params":{"from":"ears"}}""").GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(7, _setup.Count); // the bad start never reached the app
        Assert.Equal(-32601, Call("""{"jsonrpc":"2.0","id":5,"method":"calibration.explode"}""").GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public void WithoutTheAppGlassesSetupSaysWhatItNeeds()
    {
        string reply = ControlApi.Handle("""{"jsonrpc":"2.0","id":1,"method":"calibration.status"}""", new Bare(), out _)!;
        Assert.Contains("needs the running app", reply);
    }

    sealed class Bare : IControlHost
    {
        public Workspace Workspace => new();
        public void Use(Workspace changed) { }
        public Deck Deck => new();
        public void Use(Deck changed) { }
        public void Focus(string id) { }
        public IReadOnlyList<string> Projects => [];
        public string? Spawn(string project) => null;
        public string? Tell(string id, string text) => null;
        public string? OpenWidget(string label, Uri page) => null;
    }

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
    [InlineData("make me a sandwich", "Try: open, hide, load, layout, size, new bot in, or tell ... to ...")]
    [InlineData("layout tiled", "No layout preset by that name.")]
    [InlineData("size huge", "No size by that name.")]
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
    public void PresetsAreListedWithWhatEachDoes()
    {
        JsonElement result = Call("""{"jsonrpc":"2.0","id":1,"method":"preset.list"}""").GetProperty("result");
        Assert.Equal(["Solo", "MainAndSide", "TripleArc", "Stack", "Cockpit"], result.GetProperty("layouts").EnumerateArray().Select(l => l.GetProperty("name").GetString()));
        Assert.Equal(["Sharp", "Comfort", "Overview"], result.GetProperty("sizes").EnumerateArray().Select(s => s.GetProperty("name").GetString()));
        Assert.All(result.GetProperty("layouts").EnumerateArray(), l => Assert.False(string.IsNullOrEmpty(l.GetProperty("description").GetString())));
    }

    [Fact]
    public void ApplyingAPresetPlacesAndSizesTheScreens()
    {
        _workspace = _workspace.WithLayout(_workspace.Current.Layout.Add(new ScreenSpec { Id = "side", Name = "Logs", YawDeg = -40 }));
        Call("""{"jsonrpc":"2.0","id":1,"method":"preset.apply","params":{"layout":"main and side","size":"comfort"}}""");
        ScreenSpec laptop = _workspace.Current.Layout.Screens[0], side = _workspace.Current.Layout.Find("side")!;
        Assert.Equal((0f, Sizes.ComfortScale), (laptop.YawDeg, laptop.Scale));
        Assert.True(side.Visible && side.YawDeg < 0);

        // A size alone changes every shown screen where it is; with a screen named, only that one.
        Call("""{"jsonrpc":"2.0","id":2,"method":"preset.apply","params":{"size":"Sharp","main":"side"}}""");
        Assert.Equal((Sizes.ComfortScale, 1f), (_workspace.Current.Layout.Screens[0].Scale, _workspace.Current.Layout.Find("side")!.Scale));
        Call("""{"jsonrpc":"2.0","id":3,"method":"preset.apply","params":{"size":"sharp"}}""");
        Assert.Equal(1f, _workspace.Current.Layout.Screens[0].Scale);
    }

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"preset.apply"}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"preset.apply","params":{"layout":"tiled"}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"preset.apply","params":{"layout":"solo","size":"huge"}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"preset.apply","params":{"layout":"solo","main":"missing"}}""")]
    public void BadPresetRequestsAreRefused(string line)
    {
        Workspace before = _workspace;
        Assert.Equal(-32602, Call(line).GetProperty("error").GetProperty("code").GetInt32());
        Assert.Same(before, _workspace);
    }

    [Fact]
    public void SpokenPresetsDoWhatTheMatchingRequestsDo()
    {
        _workspace = _workspace.WithLayout(_workspace.Current.Layout.Add(new ScreenSpec { Id = "side", Name = "Logs", YawDeg = -40 }));
        Say("layout solo");
        Assert.False(_workspace.Current.Layout.Find("side")!.Visible);
        Assert.Equal(1f, _workspace.Current.Layout.Screens[0].Scale); // Sharp unless a size is said
        Say("Size: comfort.");
        Assert.Equal(Sizes.ComfortScale, _workspace.Current.Layout.Screens[0].Scale);
        Assert.Contains("triple arc", VoiceCommands.LayoutNames);
        Assert.Contains("overview", VoiceCommands.SizeNames);
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
