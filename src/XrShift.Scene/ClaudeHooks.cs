using System.Text.Json;
using System.Text.Json.Nodes;

namespace XrShift.Scene;

/// <summary>
/// XrShift's bot hook in Claude Code's settings.json: whether it is there, and adding, updating or removing it,
/// exactly as scripts/setup-other-pc.ps1 does. Text in, text out: the caller reads and writes the file (with a
/// backup), so nothing here can damage it. Anything it cannot read for certain is refused and left alone.
/// </summary>
public static class ClaudeHooks
{
    /// <summary>The hook events a bot needs, as in scripts/claude-hooks.json.</summary>
    public static readonly string[] Events = ["SessionStart", "UserPromptSubmit", "Notification", "PostToolUse", "Stop", "SessionEnd"];

    public enum State
    {
        /// <summary>No XrShift hook in any of the events.</summary>
        None,
        /// <summary>Every event runs this XrShift.</summary>
        Current,
        /// <summary>XrShift hooks are there, but for another copy of the app or not in every event: Update fixes it.</summary>
        Other,
        /// <summary>The file is not JSON this can read for certain; nothing will be changed.</summary>
        Unreadable,
    }

    /// <summary>The command each event runs, as the script writes it: forward slashes, the path in quotes.</summary>
    public static string Command(string exePath) => $"\"{exePath.Replace('\\', '/')}\" --bot-hook";

    /// <summary>A hook command that runs some copy of XrShift's bot hook (the script's own test: *XrShift.exe* --bot-hook).</summary>
    public static bool IsOurs(string? command) =>
        command is not null && command.Contains("XrShift.exe", StringComparison.OrdinalIgnoreCase)
                            && command.TrimEnd().EndsWith("--bot-hook", StringComparison.OrdinalIgnoreCase);

    public static State Check(string? settingsJson, string exePath)
    {
        if (Read(settingsJson, out _) is not { } root) return State.Unreadable;
        string mine = Command(exePath);
        int current = 0;
        bool any = false;
        foreach (string name in Events)
        {
            List<string> ours = Commands(root, name).Where(IsOurs).ToList();
            any |= ours.Count > 0;
            if (ours.Count > 0 && ours.All(c => string.Equals(c, mine, StringComparison.OrdinalIgnoreCase))) current++;
        }
        return !any ? State.None : current == Events.Length ? State.Current : State.Other;
    }

    /// <summary>
    /// The settings with this XrShift's hook in every event: an XrShift hook already there is pointed at this copy,
    /// otherwise one is added beside the event's other hooks. Null, with <paramref name="why"/>, when the text is
    /// not settings this can change safely. Running it twice gives the same text.
    /// </summary>
    public static string? Install(string? settingsJson, string exePath, out string why)
    {
        if (Read(settingsJson, out why) is not { } root) return null;
        string command = Command(exePath);
        if (root["hooks"] is null) root["hooks"] = new JsonObject();
        var hooks = (JsonObject)root["hooks"]!;
        foreach (string name in Events)
        {
            if (hooks[name] is null) hooks[name] = new JsonArray();
            var groups = (JsonArray)hooks[name]!;
            bool found = false;
            foreach (JsonObject hook in Entries(groups))
            {
                if (!IsOurs(Text(hook["command"]))) continue;
                hook["command"] = command;
                found = true;
            }
            if (!found)
                groups.Add(new JsonObject { ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = command, ["timeout"] = 5 }) });
        }
        return Write(root);
    }

    /// <summary>
    /// The settings without any XrShift hook. A group left with no hooks goes, and so does an event left with no
    /// groups; every other hook and setting stays. Null, with <paramref name="why"/>, when the text cannot be changed safely.
    /// </summary>
    public static string? Remove(string? settingsJson, out string why)
    {
        if (Read(settingsJson, out why) is not { } root) return null;
        if (root["hooks"] is not JsonObject hooks) return Write(root);
        foreach (string name in Events)
        {
            if (hooks[name] is not JsonArray groups) continue;
            foreach (JsonObject group in groups.OfType<JsonObject>().ToList())
            {
                if (group["hooks"] is not JsonArray list) continue;
                bool hadOurs = false;
                foreach (JsonObject hook in list.OfType<JsonObject>().Where(h => IsOurs(Text(h["command"]))).ToList())
                {
                    list.Remove(hook);
                    hadOurs = true;
                }
                if (hadOurs && list.Count == 0) groups.Remove(group);
            }
            if (groups.Count == 0) hooks.Remove(name);
        }
        return Write(root);
    }

    /// <summary>
    /// The settings as an object, when every part this touches has the shape Claude Code uses: "hooks" an object,
    /// each event a list of groups, each group's "hooks" a list. A missing or empty file is an empty object.
    /// </summary>
    static JsonObject? Read(string? text, out string why)
    {
        why = "";
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
        JsonNode? node;
        // Comments or trailing commas are refused too: writing the file back would lose them.
        try { node = JsonNode.Parse(text); }
        catch (JsonException e) { why = $"The file is not valid JSON ({e.Message})."; return null; }
        if (node is not JsonObject root) { why = "The file does not hold a JSON object."; return null; }
        if (root["hooks"] is null) return root;
        if (root["hooks"] is not JsonObject hooks) { why = "Its \"hooks\" is not an object."; return null; }
        foreach (string name in Events)
        {
            if (hooks[name] is null) continue;
            if (hooks[name] is not JsonArray groups || groups.Any(g => g is not JsonObject group || group["hooks"] is not (null or JsonArray)))
            {
                why = $"Its \"{name}\" hooks are not in the usual shape.";
                return null;
            }
        }
        return root;
    }

    static IEnumerable<JsonObject> Entries(JsonArray groups) =>
        groups.OfType<JsonObject>().SelectMany(g => g["hooks"] as JsonArray ?? []).OfType<JsonObject>();

    static IEnumerable<string> Commands(JsonObject root, string name) =>
        root["hooks"]?[name] is JsonArray groups ? Entries(groups).Select(h => Text(h["command"])).OfType<string>() : [];

    static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    // Relaxed escaping, so the other settings come back as they were written (a "+" stays a "+", not +).
    static string Write(JsonObject root) =>
        root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}
