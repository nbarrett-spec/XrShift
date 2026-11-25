using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XrShift.Scene;

/// <summary>
/// network.json, which links the wearer's PCs for bots, as the settings window shows and edits it: text in, text
/// out, every field it does not know kept. The secret itself is never handed out, only its length and when it was made.
/// </summary>
public static class NetworkFile
{
    public const int DefaultPort = 52777;
    /// <summary>The app ignores a shorter secret (BotNetwork.Load): a short secret is no secret.</summary>
    public const int MinSecretLength = 16;

    /// <summary>What the settings window may show of the file.</summary>
    public sealed record Summary(int SecretLength, int Port, IReadOnlyList<string> Peers, DateTime? Created)
    {
        /// <summary>The same test the app makes before it listens: a long enough secret and a usable port.</summary>
        public bool Valid => SecretLength >= MinSecretLength && Port is > 1023 and < 65536;
    }

    /// <summary>A new shared secret: 32 random bytes from the system's secure generator, as base64 (44 characters).</summary>
    public static string NewSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>What the file says, or null when it is not a JSON object with fields of the right kinds.</summary>
    public static Summary? Read(string? json)
    {
        if (Parse(json) is not { } file) return null;
        try
        {
            string secret = file["secret"]?.GetValue<string>() ?? "";
            int port = file["port"]?.GetValue<int>() ?? DefaultPort;
            List<string> peers = (file["peers"] as JsonArray)?.Select(p => p?.GetValue<string>() ?? "").Where(p => p.Length > 0).ToList() ?? [];
            DateTime? created = DateTime.TryParse(file["created"]?.GetValue<string>(), null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime when) ? when : null;
            return new Summary(secret.Length, port, peers, created);
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException) { return null; }
    }

    /// <summary>The file with a new secret and the time it was made. A missing file becomes a new one with the usual port.</summary>
    public static string? WithSecret(string? json, string secret, DateTime created)
    {
        if (string.IsNullOrWhiteSpace(json)) json = "{}";
        if (Parse(json) is not { } file) return null;
        file["secret"] = secret;
        file["created"] = created.ToUniversalTime().ToString("O");
        if (file["port"] is null) file["port"] = DefaultPort;
        if (file["peers"] is null) file["peers"] = new JsonArray();
        return Write(file);
    }

    /// <summary>The file with this list of PC names, blanks and repeats dropped. Null if the file cannot be read.</summary>
    public static string? WithPeers(string? json, IEnumerable<string> peers)
    {
        if (Parse(json) is not { } file) return null;
        file["peers"] = new JsonArray(peers.Select(p => p.Trim()).Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).Select(p => (JsonNode)JsonValue.Create(p)).ToArray());
        return Write(file);
    }

    static JsonObject? Parse(string? json)
    {
        try { return string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { return null; }
    }

    static string Write(JsonObject file) =>
        file.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}
