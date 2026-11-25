using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XrShift.Scene;

/// <summary>
/// What decides whether a line that arrived over the network is let in as a bot report. A report travels sealed:
/// <c>xr1:</c> then base64 of an 8-byte time (Unix milliseconds), a 12-byte nonce, the 16-byte tag and the
/// AES-256-GCM ciphertext of the request. The key comes from the shared secret; the secret itself never travels.
/// </summary>
public sealed class BotReportGate
{
    public const string Version = "xr1:";
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(120);
    const int TimeBytes = 8, NonceBytes = 12, TagBytes = 16;
    // Plenty for a burst of hooks inside one window; past it reports are refused rather than risk letting a replay in.
    const int MaxRemembered = 4096;

    readonly byte[] _key;
    readonly Dictionary<string, DateTimeOffset> _seen = [];

    public BotReportGate(string secret) => _key = Key(secret);

    // Its own info string, so anything else ever derived from the secret gets an unrelated key.
    static byte[] Key(string secret) => HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(secret), 32, info: Encoding.ASCII.GetBytes("XrShift bot report encryption v1"));

    /// <summary>The one line to send for <paramref name="request"/> (no newline).</summary>
    public static string Seal(string request, string secret, DateTimeOffset now)
    {
        byte[] plain = Encoding.UTF8.GetBytes(request);
        byte[] blob = new byte[TimeBytes + NonceBytes + TagBytes + plain.Length];
        BinaryPrimitives.WriteInt64BigEndian(blob, now.ToUnixTimeMilliseconds());
        RandomNumberGenerator.Fill(blob.AsSpan(TimeBytes, NonceBytes));
        using var aes = new AesGcm(Key(secret), TagBytes);
        aes.Encrypt(blob.AsSpan(TimeBytes, NonceBytes), plain, blob.AsSpan(TimeBytes + NonceBytes + TagBytes), blob.AsSpan(TimeBytes + NonceBytes, TagBytes), Associated(blob));
        return Version + Convert.ToBase64String(blob);
    }

    // The version and the time are bound to the ciphertext, so neither can be changed without the tag failing.
    static byte[] Associated(byte[] blob) => [.. Encoding.ASCII.GetBytes(Version), .. blob.AsSpan(0, TimeBytes)];

    /// <summary>
    /// The request to pass on, or null if the line is not a fresh, unseen, correctly sealed bot report carrying a PC name.
    /// Safe to call from several network threads at once.
    /// </summary>
    public string? Accepted(string line, DateTimeOffset now)
    {
        if (!line.StartsWith(Version, StringComparison.Ordinal)) return null;
        byte[] blob = new byte[line.Length];
        if (!Convert.TryFromBase64String(line[Version.Length..], blob, out int length) || length < TimeBytes + NonceBytes + TagBytes) return null;
        blob = blob[..length];

        DateTimeOffset sent = DateTimeOffset.FromUnixTimeMilliseconds(Math.Clamp(BinaryPrimitives.ReadInt64BigEndian(blob), -62135596800000, 253402300799999));
        if ((now - sent).Duration() > Window) return null;

        byte[] plain = new byte[length - TimeBytes - NonceBytes - TagBytes];
        try
        {
            using var aes = new AesGcm(_key, TagBytes);
            aes.Decrypt(blob.AsSpan(TimeBytes, NonceBytes), blob.AsSpan(TimeBytes + NonceBytes + TagBytes), blob.AsSpan(TimeBytes + NonceBytes, TagBytes), plain, Associated(blob));
        }
        catch (AuthenticationTagMismatchException) { return null; }

        // Only now, with the tag good, is the nonce worth remembering: forgeries cannot fill the set.
        string nonce = Convert.ToBase64String(blob, TimeBytes, NonceBytes);
        lock (_seen)
        {
            foreach (var old in _seen.Where(s => s.Value < now).ToList()) _seen.Remove(old.Key);
            if (_seen.ContainsKey(nonce) || _seen.Count >= MaxRemembered) return null;
            _seen[nonce] = sent + Window; // past this its time alone turns it away
        }

        try
        {
            if (JsonNode.Parse(plain) is not JsonObject request || request["params"] is not JsonObject p) return null;
            if (request["method"] is not JsonValue m || !m.TryGetValue(out string? method) || method != "bot.report") return null;
            if (p["pc"] is not JsonValue c || !c.TryGetValue(out string? pc) || pc is not { Length: > 0 and <= 64 }) return null;
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
