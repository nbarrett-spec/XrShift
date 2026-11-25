using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace XrShift.Scene;

/// <summary>
/// Turns a spoken sentence into one Control API request. The starting set is small and fixed, so plain rules
/// do it: "open/hide X", "load X", "layout X", "size X", "new bot in X", "tell X to ...".
/// shortcut: rules only understand these shapes; put a language model here if people want to phrase things freely.
/// </summary>
public static partial class VoiceCommands
{
    /// <summary>What can be named out loud right now.</summary>
    public sealed record Known(IReadOnlyList<ScreenSpec> Screens, IReadOnlyList<SceneObject> Objects, IReadOnlyList<string> Layouts, IReadOnlyList<string> Projects);

    public static readonly string[] OpenWords = ["open", "show"], HideWords = ["hide", "close"], LoadWords = ["load", "switch to"], TellWords = ["tell", "ask"];
    public const string SpawnPhrase = "new bot in", LayoutWord = "layout", SizeWord = "size";

    /// <summary>The preset names as they are said: "main and side", "triple arc", "sharp".</summary>
    public static readonly string[] LayoutNames = Enum.GetNames<LayoutPreset>().Select(Spoken).ToArray(), SizeNames = Enum.GetNames<SizePreset>().Select(Spoken).ToArray();

    static string Spoken(string name) => WordStarts().Replace(name, " ").ToLowerInvariant();

    /// <returns>The request to send, or null with <paramref name="why"/> saying what was not understood.</returns>
    public static JsonObject? Parse(string heard, Known known, out string why)
    {
        why = "";
        string said = Spaces().Replace(NotWords().Replace(heard.ToLowerInvariant(), " "), " ").Trim();
        if (said.StartsWith("please ")) said = said[7..];

        if (After(said, TellWords) is { } telling && telling.IndexOf(" to ", StringComparison.Ordinal) is > 0 and var to)
        {
            // The bot's own name may contain "to": try every place the sentence could split.
            for (; to > 0; to = telling.IndexOf(" to ", to + 1, StringComparison.Ordinal))
            {
                if (Best(telling[..to], known.Objects.Where(o => o.Kind == "bot"), o => o.Label) is { } bot)
                    return Request("bot.tell", ("id", bot.Id), ("text", telling[(to + 4)..].Trim()));
            }
            why = "No bot by that name.";
            return null;
        }
        if (said.StartsWith(SpawnPhrase + " "))
        {
            if (Best(said[(SpawnPhrase.Length + 1)..], known.Projects, p => p) is { } project) return Request("bot.spawn", ("project", project));
            why = "No project by that name.";
            return null;
        }
        if (After(said, LoadWords) is { } layoutName)
        {
            if (layoutName.StartsWith("layout ")) layoutName = layoutName[7..];
            if (Best(layoutName, known.Layouts, l => l) is { } layout) return Request("layout.load", ("name", layout));
            why = "No layout by that name.";
            return null;
        }
        if (After(said, [LayoutWord]) is { } layoutPreset)
        {
            if (Presets.TryParse(layoutPreset, out LayoutPreset how)) return Request("preset.apply", ("layout", how.ToString()));
            why = "No layout preset by that name.";
            return null;
        }
        if (After(said, [SizeWord]) is { } sizePreset)
        {
            if (Presets.TryParse(sizePreset, out SizePreset size)) return Request("preset.apply", ("size", size.ToString()));
            why = "No size by that name.";
            return null;
        }
        bool open = After(said, OpenWords) is not null;
        if ((After(said, OpenWords) ?? After(said, HideWords)) is { } name)
        {
            // A bot and a screen can share a name ("Mail"): opening prefers the bot, hiding can only mean the screen.
            if (open && Best(name, known.Objects, o => o.Label) is { } thing) return Request("object.focus", ("id", thing.Id));
            if (Best(name, known.Screens, s => s.Name) is { } screen) return Request("object.update", ("id", screen.Id), ("state", open ? "visible" : "hidden"));
            why = open ? "Nothing by that name." : "No screen by that name.";
            return null;
        }
        why = "Try: open, hide, load, layout, size, new bot in, or tell ... to ...";
        return null;
    }

    static string? After(string said, string[] starts)
    {
        foreach (string start in starts)
        {
            if (!said.StartsWith(start + " ")) continue;
            string rest = said[(start.Length + 1)..];
            return rest.StartsWith("the ") ? rest[4..] : rest;
        }
        return null;
    }

    /// <summary>The thing whose name was said: the same words, or failing that the only name that contains them.</summary>
    static T? Best<T>(string spoken, IEnumerable<T> among, Func<T, string> nameOf) where T : class
    {
        string Plain(string name) => Spaces().Replace(NotWords().Replace(name.ToLowerInvariant(), " "), " ").Trim();
        spoken = Plain(spoken);
        if (spoken.Length == 0) return null;
        List<T> all = among.ToList();
        if (all.FirstOrDefault(x => Plain(nameOf(x)) == spoken) is { } exact) return exact;
        List<T> close = all.Where(x => $" {Plain(nameOf(x))} ".Contains($" {spoken} ")).ToList();
        return close.Count == 1 ? close[0] : null;
    }

    static JsonObject Request(string method, params (string Key, string Value)[] parameters)
    {
        var p = new JsonObject();
        foreach ((string key, string value) in parameters) p[key] = value;
        return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = "voice", ["method"] = method, ["params"] = p };
    }

    [GeneratedRegex(@"[^\p{L}\p{N}]+")] private static partial Regex NotWords();
    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
    [GeneratedRegex(@"(?<=[a-z])(?=[A-Z])")] private static partial Regex WordStarts();
}
