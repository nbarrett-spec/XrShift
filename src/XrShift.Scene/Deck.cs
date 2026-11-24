namespace XrShift.Scene;

/// <summary>The one vocabulary for what a bot is doing.</summary>
public enum BotState
{
    /// <summary>Exists, not running.</summary>
    Idle,
    Working,
    /// <summary>Waiting for an answer or an approval.</summary>
    NeedsYou,
    /// <summary>Finished since the wearer last looked.</summary>
    Done,
    /// <summary>Stopped with an error, or died without saying it had stopped.</summary>
    Failed,
}

/// <summary>Something in the room that is not a screen: a bot, a cluster label or a widget.</summary>
public sealed record SceneObject
{
    public static readonly string[] Kinds = ["bot", "cluster", "widget"];

    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string Kind { get; init; } = "bot";
    public string Label { get; init; } = "Bot";
    public BotState State { get; init; }
    /// <summary>A short note drawn under the label, e.g. a count.</summary>
    public string Badge { get; init; } = "";
    /// <summary>Direction of the centre: + = to the left of straight ahead, + = above the horizon.</summary>
    public float YawDeg { get; init; }
    public float PitchDeg { get; init; }
    public float Distance { get; init; } = 1.5f;
    /// <summary>The position was given by a caller; otherwise the deck lines the object up itself.</summary>
    public bool Placed { get; init; }
    /// <summary>The process behind a bot, if known: when it is gone, so is the bot's session.</summary>
    public int? Pid { get; init; }

    /// <summary>How wide an object looks, in degrees.</summary>
    public const float SizeDeg = 7f;

    public Panel ToPanel()
    {
        float size = 2f * Distance * MathF.Tan(SizeDeg / 2f * MathF.PI / 180f);
        return new Panel { YawDeg = YawDeg, PitchDeg = PitchDeg, Distance = Distance, Width = size, Height = size };
    }

    public static string StateName(BotState state) => state switch
    {
        BotState.NeedsYou => "needs_you",
        _ => state.ToString().ToLowerInvariant(),
    };

    public static BotState? ParseState(string? name) => name switch
    {
        "idle" => BotState.Idle,
        "working" => BotState.Working,
        "needs_you" => BotState.NeedsYou,
        "done" => BotState.Done,
        "failed" => BotState.Failed,
        _ => null,
    };
}

/// <summary>
/// The objects in the room and the rules for how bots change state. Never changed in place: every change
/// gives a new deck, and the same deck back when nothing changed.
/// </summary>
public sealed record Deck
{
    /// <summary>Where objects that were not given a place line up: a row below eye level, filling outward from the middle.</summary>
    public const float RowPitchDeg = -20f, RowStepDeg = 9f;

    public IReadOnlyList<SceneObject> Objects { get; init; } = [];

    public SceneObject? Find(string id) => Objects.FirstOrDefault(o => o.Id == id);

    /// <summary>Adds an object, or replaces the one with the same id.</summary>
    public Deck With(SceneObject changed)
    {
        bool known = Find(changed.Id) is not null;
        List<SceneObject> next = known ? Objects.Select(o => o.Id == changed.Id ? changed : o).ToList() : [.. Objects, changed];
        return Arranged(next);
    }

    public Deck Remove(string id) => Find(id) is null ? this : Arranged(Objects.Where(o => o.Id != id).ToList());

    /// <summary>
    /// What a Claude Code hook event does to the bot of that session. An event from a session not seen before
    /// makes its bot first, so a session that was already running when the app started still shows up.
    /// </summary>
    public Deck Report(string id, string hookEvent, string? label = null, int? pid = null)
    {
        if (hookEvent == "SessionEnd") return Remove(id);
        SceneObject bot = Find(id) ?? new SceneObject { Id = id };
        BotState? next = hookEvent switch
        {
            "SessionStart" => BotState.Idle,
            "UserPromptSubmit" or "PreToolUse" or "PostToolUse" => BotState.Working,
            // Claude Code also sends this a while after it has finished, to say it is waiting for the next
            // request. That is not a question: a finished bot stays finished.
            "Notification" => bot.State is BotState.Done or BotState.Idle ? bot.State : BotState.NeedsYou,
            "Stop" => BotState.Done,
            _ => null,
        };
        if (next is null && Find(id) is null) return this; // an event we do not know, from a session we do not know
        SceneObject changed = bot with
        {
            State = next ?? bot.State,
            Label = string.IsNullOrWhiteSpace(label) ? bot.Label : label.Trim(),
            Pid = pid ?? bot.Pid,
        };
        return changed == Find(id) ? this : With(changed);
    }

    /// <summary>
    /// Looks for bots whose process has gone. One that was busy or waiting has failed: it must never sit
    /// there looking as if it were still working. One that had nothing going on is simply removed.
    /// </summary>
    public Deck Sweep(Func<int, bool> alive)
    {
        List<SceneObject> next = [];
        bool changed = false;
        foreach (SceneObject o in Objects)
        {
            if (o.Pid is not { } pid || o.State == BotState.Failed || alive(pid)) { next.Add(o); continue; }
            changed = true;
            if (o.State is BotState.Working or BotState.NeedsYou) next.Add(o with { State = BotState.Failed });
        }
        return changed ? Arranged(next) : this;
    }

    /// <summary>The wearer opened this object: a finished bot is no longer news, and a dead one is cleared away.</summary>
    public Deck Opened(string id) => Find(id) switch
    {
        { State: BotState.Done } bot => With(bot with { State = BotState.Idle }),
        { State: BotState.Failed, Pid: not null } => Remove(id),
        _ => this,
    };

    /// <summary>The object nearest a direction of gaze, if one is within reach of it.</summary>
    public SceneObject? Nearest(float yawDeg, float pitchDeg, float withinDeg = SceneObject.SizeDeg) =>
        Objects
            .Select(o => (Object: o, Off: MathF.Sqrt(MathF.Pow(MathF.IEEERemainder(o.YawDeg - yawDeg, 360f), 2) + MathF.Pow(o.PitchDeg - pitchDeg, 2))))
            .Where(x => x.Off <= withinDeg)
            .OrderBy(x => x.Off)
            .Select(x => x.Object)
            .FirstOrDefault();

    static Deck Arranged(List<SceneObject> objects)
    {
        // Slots 0, -1, +1, -2, +2 ... steps from the middle; yaw + is to the left, so the second object goes right.
        int slot = 0;
        for (int i = 0; i < objects.Count; i++)
        {
            if (objects[i].Placed) continue;
            int step = (slot + 1) / 2 * (slot % 2 == 1 ? -1 : 1);
            slot++;
            objects[i] = objects[i] with { YawDeg = step * RowStepDeg, PitchDeg = RowPitchDeg };
        }
        return new Deck { Objects = objects };
    }
}
