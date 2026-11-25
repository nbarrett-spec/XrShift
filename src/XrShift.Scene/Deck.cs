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
    /// <summary>The PC a bot runs on when that is not this one; empty for a bot on this PC.</summary>
    public string Pc { get; init; } = "";
    /// <summary>When a bot on another PC last reported. This PC cannot see that bot's process, only how long it has been quiet.</summary>
    public DateTime SeenAt { get; init; }
    /// <summary>A bot on another PC that said it was working and then went quiet: shown as offline, never as still working.</summary>
    public bool Offline { get; init; }
    /// <summary>The last thing a bot said or was asked, for its card. Never more than a couple of lines.</summary>
    public string Note { get; init; } = "";

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

    /// <summary>How long a working bot on another PC may say nothing before it is shown as offline.</summary>
    public static readonly TimeSpan RemoteQuiet = TimeSpan.FromMinutes(10);
    const int MaxNote = 160;

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
    public Deck Report(string id, string hookEvent, string? label = null, int? pid = null, string? pc = null, DateTime now = default, string? note = null)
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
            Pc = pc ?? bot.Pc,
            SeenAt = string.IsNullOrEmpty(pc ?? bot.Pc) ? bot.SeenAt : now,
            Offline = false,
            // What it last said or was asked: shown on its card. A finished bot keeps the note it finished with.
            Note = string.IsNullOrWhiteSpace(note) ? bot.Note : note.Trim() is { Length: > MaxNote } longNote ? longNote[..MaxNote] + "..." : note.Trim(),
        };
        return changed == Find(id) ? this : With(changed);
    }

    /// <summary>
    /// Looks for bots whose process has gone. One that was busy or waiting has failed: it must never sit
    /// there looking as if it were still working. One that had nothing going on is simply removed.
    /// </summary>
    public Deck Sweep(Func<int, bool> alive, DateTime now = default)
    {
        List<SceneObject> next = [];
        bool changed = false;
        foreach (SceneObject o in Objects)
        {
            // shortcut: a bot on another PC is judged by silence alone, so one tool call longer than RemoteQuiet shows as offline until it ends; have each PC report its own process check if that bites.
            if (o.Pc.Length > 0)
            {
                bool quiet = o.State == BotState.Working && !o.Offline && now - o.SeenAt > RemoteQuiet;
                changed |= quiet;
                next.Add(quiet ? o with { Offline = true } : o);
                continue;
            }
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

    /// <summary>What a card says instead of a bot's note when the wearer has chosen not to show them.</summary>
    public const string HiddenNote = "What it was asked is hidden (Bots page in Settings).";

    /// <summary>
    /// The deck as the room should draw it. Without notes, every bot's note is replaced by <see cref="HiddenNote"/>,
    /// for a room others may see; the deck itself keeps the notes, so turning them back on shows them again.
    /// </summary>
    public Deck Shown(bool notes) => notes || Objects.All(o => o.Note.Length == 0)
        ? this
        : new Deck { Objects = Objects.Select(o => o.Note.Length == 0 ? o : o with { Note = HiddenNote }).ToList() };

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
        // One row centred on straight ahead, read left to right: this PC's bots first, then each other PC's in
        // turn, so the bots of one PC always sit together. Yaw + is to the left.
        List<int> row = Enumerable.Range(0, objects.Count).Where(i => !objects[i].Placed)
            .OrderBy(i => objects[i].Pc, StringComparer.OrdinalIgnoreCase).ThenBy(i => i).ToList();
        for (int k = 0; k < row.Count; k++)
            objects[row[k]] = objects[row[k]] with { YawDeg = ((row.Count - 1) / 2f - k) * RowStepDeg, PitchDeg = RowPitchDeg };
        return new Deck { Objects = objects };
    }
}
