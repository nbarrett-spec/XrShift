using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using XrShift.Tracking;

namespace XrShift.App;

/// <summary>
/// Bots on the wearer's other PCs. Set up by <c>network.json</c> in the settings folder, the same file on every PC:
/// <code>{ "secret": "at least 16 characters", "port": 52777, "peers": ["DESK-PC", "OTHER-PC"] }</code>
/// Without that file nothing here does anything. The PC with the glasses listens; a hook on a PC with no XrShift
/// running sends its report to whichever peer answers.
/// </summary>
internal sealed record BotNetwork(string Secret, int Port, IReadOnlyList<string> Peers)
{
    static string FilePath => Path.Combine(TrackingConfig.Folder, "network.json");

    public static BotNetwork? Load()
    {
        try
        {
            if (!File.Exists(FilePath) || JsonNode.Parse(File.ReadAllText(FilePath)) is not JsonObject file) return null;
            string secret = file["secret"]?.GetValue<string>() ?? "";
            if (secret.Length < 16) return null; // a short secret is no secret: stay off
            int port = file["port"]?.GetValue<int>() ?? 52777;
            List<string> peers = (file["peers"] as JsonArray)?.Select(p => p?.GetValue<string>() ?? "").Where(p => p.Length > 0).ToList() ?? [];
            return port is > 1023 and < 65536 ? new BotNetwork(secret, port, peers) : null;
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException or FormatException) { return null; }
    }

    /// <summary>Sends a hook's report to the first peer that takes it. Quick to give up: this runs inside every hook.</summary>
    public void Forward(JsonObject report)
    {
        var p = (JsonObject)report["params"]!;
        p.Remove("pid"); // this PC's process numbers mean nothing on another
        p["pc"] = Environment.MachineName;
        p["secret"] = Secret;
        byte[] line = Encoding.UTF8.GetBytes(report.ToJsonString() + "\n");

        // A peer that did not answer is left alone for a minute, so hooks do not all pay for a PC that is off.
        string downFile = Path.Combine(Path.GetTempPath(), "xrshift-peers-down.txt");
        Dictionary<string, DateTime> down = [];
        try
        {
            foreach (string row in File.Exists(downFile) ? File.ReadAllLines(downFile) : [])
                if (row.Split('|') is [var host, var until] && DateTime.TryParse(until, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime t)) down[host] = t;
        }
        catch (IOException) { }

        bool changed = false;
        foreach (string peer in Peers.Where(peer => !string.Equals(peer, Environment.MachineName, StringComparison.OrdinalIgnoreCase)))
        {
            if (down.TryGetValue(peer, out DateTime until) && until > DateTime.UtcNow) continue;
            try
            {
                using var client = new TcpClient { NoDelay = true };
                if (!client.ConnectAsync(peer, Port).Wait(300)) throw new IOException("no answer");
                client.GetStream().Write(line);
                if (down.Remove(peer)) changed = true;
                break;
            }
            catch (Exception e) when (e is IOException or SocketException or AggregateException)
            {
                down[peer] = DateTime.UtcNow.AddMinutes(1);
                changed = true;
            }
        }
        if (!changed) return;
        try { File.WriteAllLines(downFile, down.Select(d => $"{d.Key}|{d.Value:O}")); } catch (IOException) { }
    }
}

/// <summary>
/// Takes bot reports from the wearer's other PCs. Deliberately narrow: only from private network addresses, only
/// with the shared secret, only the one request <c>bot.report</c>, one short line per connection. Nothing on this
/// port can list the scene, move screens, type into a session or start one.
/// </summary>
internal sealed class BotNetListener : IDisposable
{
    const int MaxLineBytes = 4096;

    readonly TcpListener _listener;
    readonly string _secret;
    readonly Action<string> _report;

    /// <param name="report">Given each accepted <c>bot.report</c> request line, secret removed. Called from a network thread.</param>
    public BotNetListener(BotNetwork network, Action<string> report)
    {
        _secret = network.Secret;
        _report = report;
        _listener = new TcpListener(IPAddress.Any, network.Port);
        _listener.Start();
        _ = Task.Run(Accept);
        Log.Line($"Listening for bots on other PCs on port {network.Port} (private network, shared secret).");
    }

    async Task Accept()
    {
        try
        {
            while (true)
            {
                TcpClient client = await _listener.AcceptTcpClientAsync();
                _ = Task.Run(() => Take(client));
            }
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException or InvalidOperationException) { }
    }

    void Take(TcpClient client)
    {
        using (client)
        {
            try
            {
                if ((client.Client.RemoteEndPoint as IPEndPoint)?.Address is not { } from || !XrShift.Scene.BotReportGate.IsPrivate(from)) return;
                client.ReceiveTimeout = 2000;
                NetworkStream stream = client.GetStream();
                byte[] buffer = new byte[MaxLineBytes];
                int filled = 0;
                while (filled < buffer.Length && Array.IndexOf(buffer, (byte)'\n', 0, filled) < 0)
                {
                    int read = stream.Read(buffer, filled, buffer.Length - filled);
                    if (read <= 0) break;
                    filled += read;
                }
                int end = Array.IndexOf(buffer, (byte)'\n', 0, filled);
                if (end < 0) return; // too long, or cut short
                if (XrShift.Scene.BotReportGate.Accepted(Encoding.UTF8.GetString(buffer, 0, end), _secret) is { } line) _report(line);
            }
            catch (Exception e) when (e is IOException or SocketException or JsonException or InvalidOperationException) { }
        }
    }

    public void Dispose() => _listener.Stop();
}
