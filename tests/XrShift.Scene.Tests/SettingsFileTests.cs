using System.Text.Json.Nodes;
using XrShift.Scene;

namespace XrShift.Scene.Tests;

/// <summary>The files the settings window edits for the wearer: Claude Code's hooks, network.json, and the key list.</summary>
public class SettingsFileTests
{
    const string Exe = @"C:\Users\me\AppData\Local\Programs\XrShift\XrShift.exe";
    const string Mine = "\"C:/Users/me/AppData/Local/Programs/XrShift/XrShift.exe\" --bot-hook";

    static JsonNode Parse(string text) => JsonNode.Parse(text)!;

    static List<string> Commands(string settings, string hookEvent) =>
        (Parse(settings)["hooks"]?[hookEvent] as JsonArray ?? [])
            .SelectMany(g => g!["hooks"]!.AsArray()).Select(h => h!["command"]!.GetValue<string>()).ToList();

    [Fact]
    public void InstallingIntoNoFileAddsTheHookToEverySixEvents()
    {
        string? result = ClaudeHooks.Install(null, Exe, out _);
        Assert.NotNull(result);
        foreach (string e in ClaudeHooks.Events) Assert.Equal([Mine], Commands(result!, e));
        Assert.Equal(ClaudeHooks.State.Current, ClaudeHooks.Check(result, Exe));
        Assert.Equal(ClaudeHooks.State.None, ClaudeHooks.Check(null, Exe));
    }

    [Fact]
    public void InstallingKeepsOtherHooksAndSettingsAndIsIdempotent()
    {
        const string before = """
            {
              "model": "opus",
              "permissions": { "allow": ["Bash(git status)"] },
              "hooks": {
                "Stop": [ { "matcher": "", "hooks": [ { "type": "command", "command": "notify.cmd done+ok" } ] } ],
                "PreToolUse": [ { "matcher": "Bash", "hooks": [ { "type": "command", "command": "guard.cmd" } ] } ]
              }
            }
            """;
        string once = ClaudeHooks.Install(before, Exe, out _)!;
        Assert.Equal(once, ClaudeHooks.Install(once, Exe, out _));
        Assert.Equal(["notify.cmd done+ok", Mine], Commands(once, "Stop"));
        Assert.Equal(["guard.cmd"], Commands(once, "PreToolUse"));
        Assert.Equal("opus", Parse(once)["model"]!.GetValue<string>());
        Assert.Equal("Bash(git status)", Parse(once)["permissions"]!["allow"]![0]!.GetValue<string>());
        Assert.Contains("done+ok", once); // written as typed, not escaped

        string removed = ClaudeHooks.Remove(once, out _)!;
        Assert.Equal(["notify.cmd done+ok"], Commands(removed, "Stop"));
        Assert.Equal(["guard.cmd"], Commands(removed, "PreToolUse"));
        Assert.Null(Parse(removed)["hooks"]!["SessionStart"]);
        Assert.Equal(ClaudeHooks.State.None, ClaudeHooks.Check(removed, Exe));
    }

    [Fact]
    public void AHookForAnotherCopyIsPointedHereNotDoubled()
    {
        string elsewhere = ClaudeHooks.Install(null, @"D:\old\XrShift\XrShift.exe", out _)!;
        Assert.Equal(ClaudeHooks.State.Other, ClaudeHooks.Check(elsewhere, Exe));
        string updated = ClaudeHooks.Install(elsewhere, Exe, out _)!;
        foreach (string e in ClaudeHooks.Events) Assert.Equal([Mine], Commands(updated, e));
        Assert.Equal(ClaudeHooks.State.Current, ClaudeHooks.Check(updated, Exe));
    }

    [Theory]
    [InlineData("{ \"hooks\": { \"Stop\": [ ")]                 // cut short
    [InlineData("{ \"model\": \"opus\", }")]                     // trailing comma: writing back would change more than ours
    [InlineData("// mine\n{ }")]                                 // a comment would be lost
    [InlineData("[1, 2]")]                                       // not an object
    [InlineData("{ \"hooks\": [] }")]                            // hooks of an unknown shape
    [InlineData("{ \"hooks\": { \"Stop\": { \"hooks\": [] } } }")]
    public void ASettingsFileItCannotReadForCertainIsRefusedAndLeftAlone(string text)
    {
        Assert.Null(ClaudeHooks.Install(text, Exe, out string why));
        Assert.NotEmpty(why);
        Assert.Null(ClaudeHooks.Remove(text, out _));
        Assert.Equal(ClaudeHooks.State.Unreadable, ClaudeHooks.Check(text, Exe));
    }

    [Fact]
    public void ANewSecretIsLongRandomBase64()
    {
        string a = NetworkFile.NewSecret(), b = NetworkFile.NewSecret();
        Assert.True(Convert.FromBase64String(a).Length >= 24);
        Assert.True(a.Length >= NetworkFile.MinSecretLength);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void NetworkFileEditsKeepWhatTheyDoNotKnowAndNeverShowTheSecret()
    {
        DateTime when = new(2026, 11, 24, 10, 0, 0, DateTimeKind.Utc);
        string made = NetworkFile.WithSecret(null, "0123456789abcdefXYZ", when)!;
        NetworkFile.Summary summary = NetworkFile.Read(made)!;
        Assert.Equal((19, NetworkFile.DefaultPort, true, when), (summary.SecretLength, summary.Port, summary.Valid, summary.Created!.Value.ToUniversalTime()));

        string withPeers = NetworkFile.WithPeers("{ \"secret\": \"s\", \"port\": 50000, \"extra\": 1 }", [" DESK ", "desk", "", "LAPTOP"])!;
        Assert.Equal(["DESK", "LAPTOP"], NetworkFile.Read(withPeers)!.Peers);
        Assert.Equal(1, Parse(withPeers)["extra"]!.GetValue<int>());
        Assert.False(NetworkFile.Read(withPeers)!.Valid); // a one-letter secret is no secret
        Assert.Null(NetworkFile.WithPeers("not json", ["A"]));
        Assert.Null(NetworkFile.WithSecret("not json", "x", when));
    }

    [Fact]
    public void TheKeyListHasNoKeyTwiceAndEveryKeySaysWhatItDoes()
    {
        Assert.Equal(Hotkeys.All.Count, Hotkeys.All.Select(k => k.Code).Distinct().Count());
        Assert.Equal(Hotkeys.All.Count, Hotkeys.All.Select(k => k.Name).Distinct().Count());
        Assert.All(Hotkeys.All, k => Assert.False(string.IsNullOrWhiteSpace(k.What)));
        Assert.Contains(("Ctrl+Alt+PgUp / PgDn", Hotkeys.All.First(k => k.Name == "PgUp").What), Hotkeys.Reference());
        Assert.Equal("Ctrl+Alt+T", Hotkeys.Text(0x54));
    }

    [Fact]
    public void HidingNotesChangesWhatIsDrawnNotWhatIsKept()
    {
        Deck deck = new Deck().Report("a", "UserPromptSubmit", "abel", 1, note: "fix the invoice export").Report("b", "SessionStart", "quiet", 2);
        Deck shown = deck.Shown(notes: false);
        Assert.Equal(Deck.HiddenNote, shown.Find("a")!.Note);
        Assert.Equal("", shown.Find("b")!.Note);
        Assert.Equal("fix the invoice export", deck.Find("a")!.Note);
        Assert.Equal(deck.Objects.Select(o => (o.YawDeg, o.PitchDeg)), shown.Objects.Select(o => (o.YawDeg, o.PitchDeg)));
        Assert.Same(deck, deck.Shown(notes: true));
    }
}
