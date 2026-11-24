using System.Text.Json;
using XrShift.Scene;

namespace XrShift.Scene.Tests;

public class ControlApiTests
{
    Workspace _workspace = new Workspace()
        .WithLayout(new Layout().Add(new ScreenSpec { Name = "Laptop" }).Add(new ScreenSpec { Name = "Mail", Source = ScreenSpec.WindowSource("outlook", "Inbox"), Visible = false }))
        .Rename("Sales day");

    JsonElement Call(string line, out bool subscribe)
    {
        string? reply = ControlApi.Handle(line, () => _workspace, w => _workspace = w, out subscribe);
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
        Assert.Null(ControlApi.Handle("""{"jsonrpc":"2.0","method":"layout.save","params":{"name":"Quiet"}}""", () => _workspace, w => _workspace = w, out _));
        Assert.Equal("Quiet", _workspace.Current.Name);
    }
}
