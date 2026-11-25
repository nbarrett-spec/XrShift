using System.Text.Json;
using System.Text.Json.Nodes;

namespace XrShift.Scene;

/// <summary>The app, as the Control API sees it: what is in use right now, and how to change it.</summary>
public interface IControlHost
{
    Workspace Workspace { get; }
    void Use(Workspace changed);
    Deck Deck { get; }
    void Use(Deck changed);
    /// <summary>Opens an object for the wearer, as looking at it and pressing the key does.</summary>
    void Focus(string id);
    /// <summary>The projects a new bot can be started in, by name.</summary>
    IReadOnlyList<string> Projects { get; }
    /// <summary>Starts a new agent session in a project. Null when it started; otherwise why not.</summary>
    string? Spawn(string project);
    /// <summary>Hands a bot an instruction. Null when it was handed over; otherwise why not.</summary>
    string? Tell(string id, string text);
    /// <summary>Opens a web page as a panel of its own in the room. Null when it was started; otherwise why not.</summary>
    string? OpenWidget(string label, Uri page);
    /// <summary>The size in pixels of what a screen shows, for the presets. Without the app's help only a virtual monitor's own size is known.</summary>
    (int Width, int Height) PictureSize(ScreenSpec screen) => screen.PixelWidth > 0 ? (screen.PixelWidth, screen.PixelHeight) : (1920, 1080);
    /// <summary>The glasses the presets size screens for.</summary>
    DisplayOptics Optics => DisplayOptics.OnePro;
}

/// <summary>
/// The Control API's protocol: one JSON-RPC 2.0 request per line in, one response line out. It knows nothing
/// about pipes or windows, so it can be tested on its own; the app carries the lines and owns the state.
/// </summary>
public static class ControlApi
{
    public const string PipeName = "ArWorkspace.Api";

    const int ParseError = -32700, InvalidRequest = -32600, MethodNotFound = -32601, InvalidParams = -32602;

    /// <param name="line">One request.</param>
    /// <param name="subscribe">True if this caller asked for the event stream.</param>
    /// <returns>The response line, or null for a request that wants none (no id).</returns>
    public static string? Handle(string line, IControlHost host, out bool subscribe)
    {
        subscribe = false;
        JsonNode? id = null;
        try
        {
            if (JsonNode.Parse(line) is not JsonObject request) return Error(null, InvalidRequest, "A request must be a JSON object.");
            bool wantsReply = request.TryGetPropertyValue("id", out id);
            id = id?.DeepClone();
            if (Text(request, "method") is not { } method) return Error(id, InvalidRequest, "A request needs a method.");
            JsonObject? p = request["params"] as JsonObject;

            JsonNode result;
            switch (method)
            {
                case "scene.list":
                    result = SceneList(host.Workspace, host.Deck);
                    break;
                case "layout.load":
                {
                    if (Text(p, "name") is not { } name) return Error(id, InvalidParams, "layout.load needs params.name.");
                    if (host.Workspace.IndexOf(name) is not { } at) return Error(id, InvalidParams, $"No layout named '{name}'.");
                    host.Use(host.Workspace.Select(at));
                    result = new JsonObject { ["name"] = host.Workspace.Current.Name };
                    break;
                }
                case "layout.save":
                {
                    if (Text(p, "name") is not { } name) return Error(id, InvalidParams, "layout.save needs params.name.");
                    host.Use(host.Workspace.SaveAs(name));
                    result = new JsonObject { ["name"] = host.Workspace.Current.Name };
                    break;
                }
                case "events.subscribe":
                    subscribe = true;
                    result = new JsonObject { ["subscribed"] = true };
                    break;
                case "object.create":
                {
                    string kind = Text(p, "kind") ?? "bot";
                    if (!SceneObject.Kinds.Contains(kind)) return Error(id, InvalidParams, $"kind must be one of: {string.Join(", ", SceneObject.Kinds)}.");
                    var made = new SceneObject { Kind = kind, Label = kind };
                    if (Text(p, "id") is { } wanted) made = made with { Id = wanted };
                    if (!TryChange(ref made, p, out string? why)) return Error(id, InvalidParams, why!);
                    host.Use(host.Deck.With(made));
                    result = new JsonObject { ["id"] = made.Id };
                    break;
                }
                case "object.update" when Text(p, "id") is { } screenId && host.Workspace.Current.Layout.Find(screenId) is { } screen:
                {
                    // A screen can only be shown or hidden from here; placing screens stays with the settings window.
                    bool? show = Text(p, "state") switch { "visible" => true, "hidden" => false, _ => null };
                    if (show is null) return Error(id, InvalidParams, "For a screen, state must be visible or hidden.");
                    host.Use(host.Workspace.WithLayout(host.Workspace.Current.Layout.With(screen with { Visible = show.Value })));
                    result = new JsonObject { ["id"] = screenId };
                    break;
                }
                case "bot.spawn":
                {
                    if (Text(p, "project") is not { } project) return Error(id, InvalidParams, "bot.spawn needs params.project.");
                    if (host.Spawn(project) is { } refused) return Error(id, InvalidParams, refused);
                    result = new JsonObject { ["project"] = project };
                    break;
                }
                case "bot.tell":
                {
                    if (Text(p, "id") is not { } bot || host.Deck.Find(bot) is null) return Error(id, InvalidParams, "No object with that id.");
                    if (Text(p, "text") is not { } text) return Error(id, InvalidParams, "bot.tell needs params.text.");
                    if (host.Tell(bot, text) is { } refused) return Error(id, InvalidParams, refused);
                    result = new JsonObject { ["id"] = bot };
                    break;
                }
                case "widget.open":
                {
                    // A widget is a web page in a window of its own, pinned as a screen: a dashboard, a calendar.
                    if (Text(p, "label") is not { } label || Text(p, "url") is not { } address) return Error(id, InvalidParams, "widget.open needs params.label and params.url.");
                    if (!Uri.TryCreate(address, UriKind.Absolute, out Uri? page) || (page.Scheme != Uri.UriSchemeHttps && page.Scheme != Uri.UriSchemeHttp))
                        return Error(id, InvalidParams, "url must be a full http or https address.");
                    if (host.OpenWidget(label, page) is { } refused) return Error(id, InvalidParams, refused);
                    result = new JsonObject { ["label"] = label };
                    break;
                }
                case "preset.list":
                    result = new JsonObject
                    {
                        ["layouts"] = new JsonArray(Enum.GetValues<LayoutPreset>().Select(l => (JsonNode?)new JsonObject { ["name"] = l.ToString(), ["description"] = Presets.About(l) }).ToArray()),
                        ["sizes"] = new JsonArray(Enum.GetValues<SizePreset>().Select(s => (JsonNode?)new JsonObject { ["name"] = s.ToString(), ["description"] = Sizes.About(s) }).ToArray()),
                    };
                    break;
                case "preset.apply":
                {
                    string? layoutName = Text(p, "layout"), sizeName = Text(p, "size"), mainId = Text(p, "main");
                    if (layoutName is null && sizeName is null) return Error(id, InvalidParams, "preset.apply needs params.layout or params.size.");
                    LayoutPreset how = default;
                    SizePreset size = SizePreset.Sharp;
                    if (layoutName is not null && !Presets.TryParse(layoutName, out how))
                        return Error(id, InvalidParams, $"layout must be one of: {string.Join(", ", Enum.GetNames<LayoutPreset>())}.");
                    if (sizeName is not null && !Presets.TryParse(sizeName, out size))
                        return Error(id, InvalidParams, $"size must be one of: {string.Join(", ", Enum.GetNames<SizePreset>())}.");
                    Layout now = host.Workspace.Current.Layout;
                    ScreenSpec? main = null;
                    if (mainId is not null && (main = now.Find(mainId)) is null) return Error(id, InvalidParams, "No screen with that id.");
                    Layout changed;
                    if (layoutName is not null) changed = Presets.Apply(now, how, size, host.PictureSize, host.Optics, mainId);
                    else if (main is not null)
                    {
                        // A size and a screen, no layout: just that screen changes size, where it is.
                        (int w, int h) = host.PictureSize(main);
                        changed = now.With(Sizes.Apply(main, size, w, h, host.Optics));
                    }
                    else changed = Sizes.Apply(now, size, host.PictureSize, host.Optics);
                    host.Use(host.Workspace.WithLayout(changed));
                    result = new JsonObject { ["layout"] = layoutName is null ? null : how.ToString(), ["size"] = size.ToString() };
                    break;
                }
                case "voice.say":
                {
                    // A sentence as if it had been spoken: what the microphone path sends, and a way to script it.
                    if (Text(p, "text") is not { } heard) return Error(id, InvalidParams, "voice.say needs params.text.");
                    var known = new VoiceCommands.Known(host.Workspace.Current.Layout.Screens, host.Deck.Objects, host.Workspace.Profiles.Select(x => x.Name).ToList(), host.Projects);
                    if (VoiceCommands.Parse(heard, known, out string why) is not { } command) return Error(id, InvalidParams, why);
                    command["id"] = id?.DeepClone();
                    if (!wantsReply) command.Remove("id");
                    return Handle(command.ToJsonString(), host, out subscribe);
                }
                case "object.update":
                {
                    if (Text(p, "id") is not { } target || host.Deck.Find(target) is not { } found) return Error(id, InvalidParams, "No object with that id.");
                    if (!TryChange(ref found, p, out string? why)) return Error(id, InvalidParams, why!);
                    host.Use(host.Deck.With(found));
                    result = new JsonObject { ["id"] = found.Id };
                    break;
                }
                case "object.remove":
                case "object.focus":
                {
                    if (Text(p, "id") is not { } target || host.Deck.Find(target) is null) return Error(id, InvalidParams, "No object with that id.");
                    if (method == "object.remove") host.Use(host.Deck.Remove(target));
                    else host.Focus(target);
                    result = new JsonObject { ["id"] = target };
                    break;
                }
                case "bot.report":
                {
                    // The bot bridge: a Claude Code hook event for one session. The rules live in Deck.Report.
                    if (Text(p, "id") is not { } session || Text(p, "event") is not { } hookEvent) return Error(id, InvalidParams, "bot.report needs params.id and params.event.");
                    int? pid = p!["pid"] is JsonValue v && v.TryGetValue(out int n) && n > 0 ? n : null;
                    // A report that names a PC came over the network: that PC's process numbers mean nothing here.
                    string? pc = Text(p, "pc");
                    host.Use(host.Deck.Report(session, hookEvent, Text(p, "label"), pc is null ? pid : null, pc, DateTime.UtcNow, Text(p, "note")));
                    result = new JsonObject { ["state"] = host.Deck.Find(session) is { } bot ? SceneObject.StateName(bot.State) : null };
                    break;
                }
                default:
                    return Error(id, MethodNotFound, $"Unknown method '{method}'.");
            }
            return wantsReply ? new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }.ToJsonString() : null;
        }
        catch (JsonException)
        {
            return Error(null, ParseError, "Not valid JSON.");
        }
    }

    /// <summary>A line telling subscribers that a layout was loaded.</summary>
    public static string Event(string type, string name) => Event(type, "name", name);

    /// <summary>A line telling subscribers something about one object, e.g. <c>object.activated</c>.</summary>
    public static string ObjectEvent(string type, string id) => Event(type, "id", id);

    static string Event(string type, string key, string value) =>
        new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "event",
            ["params"] = new JsonObject { ["type"] = type, [key] = value },
        }.ToJsonString();

    static string? Text(JsonObject? from, string key) =>
        from?[key] is JsonValue v && v.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

    /// <summary>Applies the optional state, label, badge and position of a create or update request.</summary>
    static bool TryChange(ref SceneObject o, JsonObject? p, out string? why)
    {
        why = null;
        if (p is null) return true;
        if (p["state"] is not null)
        {
            if (SceneObject.ParseState(Text(p, "state")) is not { } state) { why = "state must be one of: idle, working, needs_you, done, failed."; return false; }
            o = o with { State = state };
        }
        if (Text(p, "label") is { } label) o = o with { Label = label };
        if (p["badge"] is JsonValue b && b.TryGetValue(out string? badge)) o = o with { Badge = badge ?? "" };
        if (p["position"] is JsonObject at)
        {
            float Number(string key, float otherwise) => at[key] is JsonValue n && n.TryGetValue(out float f) && float.IsFinite(f) ? f : otherwise;
            o = o with
            {
                YawDeg = Math.Clamp(Number("yawDeg", o.YawDeg), -180f, 180f),
                PitchDeg = Math.Clamp(Number("pitchDeg", o.PitchDeg), -80f, 80f),
                Distance = Math.Clamp(Number("distance", o.Distance), 0.3f, 10f),
                Placed = true,
            };
        }
        return true;
    }

    static JsonObject SceneList(Workspace workspace, Deck deck)
    {
        var objects = new JsonArray();
        foreach (ScreenSpec s in workspace.Current.Layout.Screens)
        {
            objects.Add(new JsonObject
            {
                ["id"] = s.Id,
                ["kind"] = s.Window is null ? "screen" : "window",
                ["label"] = s.Name,
                ["position"] = new JsonObject { ["yawDeg"] = s.YawDeg, ["pitchDeg"] = s.PitchDeg, ["distance"] = s.Distance },
                ["state"] = s.Visible ? "visible" : "hidden",
            });
        }
        foreach (SceneObject o in deck.Objects)
        {
            objects.Add(new JsonObject
            {
                ["id"] = o.Id,
                ["kind"] = o.Kind,
                ["label"] = o.Label,
                ["position"] = new JsonObject { ["yawDeg"] = o.YawDeg, ["pitchDeg"] = o.PitchDeg, ["distance"] = o.Distance },
                ["state"] = o.Offline ? "offline" : SceneObject.StateName(o.State),
                ["pc"] = o.Pc,
                ["note"] = o.Note,
                ["badge"] = o.Badge,
            });
        }
        return new JsonObject
        {
            ["layout"] = workspace.Current.Name,
            ["layouts"] = new JsonArray(workspace.Profiles.Select(p => (JsonNode?)p.Name).ToArray()),
            ["objects"] = objects,
        };
    }

    static string Error(JsonNode? id, int code, string message) =>
        new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        }.ToJsonString();
}
