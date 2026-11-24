using XrShift.Scene;

namespace XrShift.Scene.Tests;

public class DeckTests
{
    static Deck After(params string[] events) => events.Aggregate(new Deck(), (deck, e) => deck.Report("s1", e, "abel", 100));

    [Theory]
    [InlineData(BotState.Idle, "SessionStart")]
    [InlineData(BotState.Working, "SessionStart", "UserPromptSubmit")]
    [InlineData(BotState.NeedsYou, "UserPromptSubmit", "Notification")]
    [InlineData(BotState.Working, "UserPromptSubmit", "Notification", "PostToolUse")]
    [InlineData(BotState.Working, "UserPromptSubmit", "Notification", "PreToolUse")]
    [InlineData(BotState.Done, "UserPromptSubmit", "Stop")]
    [InlineData(BotState.Done, "UserPromptSubmit", "Notification", "Stop")]
    [InlineData(BotState.Done, "UserPromptSubmit", "Stop", "Notification")] // "waiting for your next request" is not a question
    [InlineData(BotState.Idle, "SessionStart", "Notification")]
    [InlineData(BotState.Working, "UserPromptSubmit", "Stop", "UserPromptSubmit")]
    [InlineData(BotState.Idle, "UserPromptSubmit", "Stop", "SessionStart")]
    [InlineData(BotState.Working, "UserPromptSubmit", "SomethingNew")]
    public void HookEventsMoveABotThroughItsStates(BotState expected, params string[] events) =>
        Assert.Equal(expected, After(events).Find("s1")!.State);

    [Fact]
    public void ASessionAlreadyRunningShowsUpAtItsNextEvent() =>
        Assert.Equal(("abel", BotState.Done), (After("Stop").Find("s1")!.Label, After("Stop").Find("s1")!.State));

    [Fact]
    public void SessionEndRemovesTheBotAndUnknownEventsFromStrangersDoNothing()
    {
        Assert.Empty(After("UserPromptSubmit", "SessionEnd").Objects);
        Assert.Empty(After("SomethingNew").Objects);
        Assert.Empty(After("SessionEnd").Objects);
    }

    [Theory]
    [InlineData("UserPromptSubmit")]
    [InlineData("Notification")]
    public void ASessionThatDiesWithoutStoppingTurnsFailedNotWorking(string last)
    {
        Deck dead = After("UserPromptSubmit", last).Sweep(_ => false);
        Assert.Equal(BotState.Failed, dead.Find("s1")!.State);
        Assert.Same(dead, dead.Sweep(_ => false)); // and stays there, without churning
    }

    [Fact]
    public void ADeadSessionWithNothingGoingOnIsJustRemoved()
    {
        Assert.Empty(After("SessionStart").Sweep(_ => false).Objects);
        Assert.Empty(After("UserPromptSubmit", "Stop").Sweep(_ => false).Objects);
    }

    [Fact]
    public void LiveSessionsAndObjectsWithoutAProcessAreLeftAlone()
    {
        Deck deck = After("UserPromptSubmit").With(new SceneObject { Id = "w", Kind = "widget", State = BotState.Working });
        Assert.Same(deck, deck.Sweep(_ => true));
        Assert.Equal(BotState.Working, deck.Sweep(pid => pid != 100).Find("w")!.State);
    }

    [Fact]
    public void OpeningAFinishedBotClearsTheNewsAndOpeningADeadOneClearsItAway()
    {
        Assert.Equal(BotState.Idle, After("UserPromptSubmit", "Stop").Opened("s1").Find("s1")!.State);
        Assert.Empty(After("UserPromptSubmit").Sweep(_ => false).Opened("s1").Objects);
        Deck working = After("UserPromptSubmit");
        Assert.Same(working, working.Opened("s1"));
    }

    [Fact]
    public void UnplacedObjectsLineUpFromTheMiddleAndPlacedOnesStayPut()
    {
        Deck deck = new Deck()
            .With(new SceneObject { Id = "a" })
            .With(new SceneObject { Id = "p", YawDeg = 40, PitchDeg = 5, Placed = true })
            .With(new SceneObject { Id = "b" })
            .With(new SceneObject { Id = "c" });
        Assert.Equal([0f, 40f, -Deck.RowStepDeg, Deck.RowStepDeg], deck.Objects.Select(o => o.YawDeg));
        Assert.Equal(5f, deck.Find("p")!.PitchDeg);
        // Closing the middle one closes the gap.
        Assert.Equal([40f, 0f, -Deck.RowStepDeg], deck.Remove("a").Objects.Select(o => o.YawDeg));
    }

    [Fact]
    public void FindsTheObjectBeingLookedAt()
    {
        Deck deck = new Deck().With(new SceneObject { Id = "a" }).With(new SceneObject { Id = "b" });
        Assert.Equal("b", deck.Nearest(-Deck.RowStepDeg + 1, Deck.RowPitchDeg - 2)!.Id);
        Assert.Null(deck.Nearest(60, 0));
    }
}
