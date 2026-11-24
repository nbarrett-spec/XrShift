using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XrShift.Scene;

/// <summary>What decides whether a line that arrived over the network is let in as a bot report.</summary>
public static class BotReportGate
{
    /// <summary>The request to pass on, without its secret, or null if it is not a bot report carrying the right secret and a PC name.</summary>
    public static string? Accepted(string text, string secret)
    {
        try
        {
            if (JsonNode.Parse(text) is not JsonObject request || request["params"] is not JsonObject p) return null;
            if (request["method"] is not JsonValue m || !m.TryGetValue(out string? method) || method != "bot.report") return null;
            if (p["secret"] is not JsonValue s || !s.TryGetValue(out string? given) || given is null) return null;
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(secret))) return null;
            if (p["pc"] is not JsonValue c || !c.TryGetValue(out string? pc) || pc is not { Length: > 0 and <= 64 }) return null;
            p.Remove("secret");
            p.Remove("pid");      // another PC's process numbers mean nothing here
            request.Remove("id"); // nothing is sent back
            return request.ToJsonString();
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Home and office ranges, link-local, this PC itself, and the range private mesh networks such as Tailscale use.</summary>
    public static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == AddressFamily.InterNetworkV6) return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal;
        byte[] b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] is >= 64 and <= 127);
    }
}
