using System.Text.Json;
using System.Text.Json.Nodes;

namespace XrShift.Scene;

/// <summary>
/// The Control API's protocol: one JSON-RPC 2.0 request per line in, one response line out. It knows nothing
/// about pipes or windows, so it can be tested on its own; the app carries the lines and owns the workspace.
/// </summary>
public static class ControlApi
{
    public const string PipeName = "ArWorkspace.Api";

    const int ParseError = -32700, InvalidRequest = -32600, MethodNotFound = -32601, InvalidParams = -32602;

    /// <param name="line">One request.</param>
    /// <param name="workspace">The workspace in use right now.</param>
    /// <param name="use">Switches the app to a changed workspace.</param>
    /// <param name="subscribe">True if this caller asked for the event stream.</param>
    /// <returns>The response line, or null for a request that wants none (no id).</returns>
    public static string? Handle(string line, Func<Workspace> workspace, Action<Workspace> use, out bool subscribe)
    {
        subscribe = false;
        JsonNode? id = null;
        try
        {
            if (JsonNode.Parse(line) is not JsonObject request) return Error(null, InvalidRequest, "A request must be a JSON object.");
            bool wantsReply = request.TryGetPropertyValue("id", out id);
            id = id?.DeepClone();
            if (request["method"] is not JsonValue methodNode || !methodNode.TryGetValue(out string? method))
                return Error(id, InvalidRequest, "A request needs a method.");

            JsonNode result;
            switch (method)
            {
                case "scene.list":
                    result = SceneList(workspace());
                    break;
                case "layout.load":
                {
                    if (Name(request) is not { } name) return Error(id, InvalidParams, "layout.load needs params.name.");
                    Workspace now = workspace();
                    if (now.IndexOf(name) is not { } at) return Error(id, InvalidParams, $"No layout named '{name}'.");
                    use(now.Select(at));
                    result = new JsonObject { ["name"] = workspace().Current.Name };
                    break;
                }
                case "layout.save":
                {
                    if (Name(request) is not { } name) return Error(id, InvalidParams, "layout.save needs params.name.");
                    use(workspace().SaveAs(name));
                    result = new JsonObject { ["name"] = workspace().Current.Name };
                    break;
                }
                case "events.subscribe":
                    subscribe = true;
                    result = new JsonObject { ["subscribed"] = true };
                    break;
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

    /// <summary>A line telling subscribers that something happened, e.g. <c>layout.loaded</c>.</summary>
    public static string Event(string type, string name) =>
        new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "event",
            ["params"] = new JsonObject { ["type"] = type, ["name"] = name },
        }.ToJsonString();

    static string? Name(JsonObject request) =>
        request["params"] is JsonObject p && p["name"] is JsonValue v && v.TryGetValue(out string? name) && !string.IsNullOrWhiteSpace(name)
            ? name.Trim()
            : null;

    static JsonObject SceneList(Workspace workspace)
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
