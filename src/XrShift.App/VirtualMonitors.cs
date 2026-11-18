using System.IO.Pipes;
using System.Text;
using System.Xml.Linq;

namespace XrShift.App;

/// <summary>
/// Talks to the Virtual Display Driver (VirtualDrivers/Virtual-Display-Driver, "VDD by MTT") to add
/// and remove virtual monitors. The driver keeps its settings in an XML file and takes commands on
/// a named pipe, one command per connection, as UTF-16 text.
/// </summary>
internal static class VirtualMonitors
{
    const string PipeName = "MTTVirtualDisplayPipe";
    const string SettingsPath = @"C:\VirtualDisplayDriver\vdd_settings.xml";

    /// <summary>More than this is asking for trouble: every one is a full desktop Windows has to draw.</summary>
    public const int Max = 6;

    /// <summary>The name Windows gives these monitors; how a virtual monitor is told from a real one.</summary>
    public static bool IsVirtual(DisplayInfo display) =>
        IsVirtualId(display.Id) || display.MonitorNames.Any(n => n.Contains("VDD by MTT", StringComparison.OrdinalIgnoreCase));

    /// <summary>The same test for a monitor that may no longer be connected: its device path carries the driver's maker code, e.g. <c>\\?\DISPLAY#MTT1337#...</c>.</summary>
    public static bool IsVirtualId(string? id) =>
        id is not null && id.Contains("#MTT", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// How many virtual monitors a layout needs. The driver numbers its monitors from the first, and a
    /// monitor's number shows in its device path (UID256 is the first), so a screen on the third one
    /// needs three monitors to exist.
    /// </summary>
    public static int Needed(XrShift.Scene.Layout layout)
    {
        int needed = 0;
        foreach (XrShift.Scene.ScreenSpec screen in layout.Screens)
        {
            if (!IsVirtualId(screen.DisplayName)) continue;
            System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(screen.DisplayName!, @"UID(\d+)");
            int number = m.Success && int.TryParse(m.Groups[1].Value, out int uid) && uid >= 256 ? uid - 256 + 1 : 1;
            needed = Math.Max(needed, number);
        }
        return Math.Min(needed, Max);
    }

    /// <summary>True if the driver is installed and listening.</summary>
    public static bool Installed
    {
        get
        {
            try { return File.Exists(SettingsPath) && File.Exists(@"\\.\pipe\" + PipeName); }
            catch (IOException) { return false; }
        }
    }

    /// <summary>How many virtual monitors the driver is set to provide, or null if that cannot be read.</summary>
    public static int? Count()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return null;
            string? text = XDocument.Load(SettingsPath).Root?.Element("monitors")?.Element("count")?.Value;
            return int.TryParse(text, out int count) ? count : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>
    /// Asks the driver for a number of virtual monitors. It rewrites its settings file and rebuilds
    /// its monitors; Windows then takes a second or two to show the change.
    /// </summary>
    public static bool TrySetCount(int count, out string problem)
    {
        count = Math.Clamp(count, 0, Max);
        return TrySend($"SETDISPLAYCOUNT {count}", out _, out problem);
    }

    /// <summary>
    /// Makes the driver offer a monitor size it does not have in its list (it comes with 16:9 sizes only, no
    /// ultrawides): adds the size to its settings file and has it read the file again. All its monitors are
    /// rebuilt, so Windows takes a second or two to settle afterwards.
    /// </summary>
    public static bool TryAddSize(int width, int height, out string problem)
    {
        problem = "";
        try
        {
            XDocument file = XDocument.Load(SettingsPath, LoadOptions.PreserveWhitespace);
            XElement? list = file.Root?.Element("resolutions");
            if (list is null)
            {
                problem = "the driver's settings file has no list of sizes";
                return false;
            }
            static int Number(XElement? e) => int.TryParse(e?.Value.Trim(), out int n) ? n : 0;
            if (!list.Elements("resolution").Any(r => Number(r.Element("width")) == width && Number(r.Element("height")) == height))
            {
                list.Add(new XElement("resolution",
                    new XElement("width", width), new XElement("height", height), new XElement("refresh_rate", 30)));
                file.Save(SettingsPath);
            }
            return TrySend("RELOAD_DRIVER", out _, out problem);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            problem = e.Message;
            return false;
        }
    }

    /// <summary>
    /// Checks the driver takes a command. It is sent a harmless PING; the driver hangs up straight after
    /// answering, usually before the answer can be read, so only the sending is checked.
    /// </summary>
    public static bool Reachable(out string problem) => TrySend("PING", out _, out problem);

    static bool TrySend(string command, out string reply, out string problem, bool wantReply = false)
    {
        reply = "";
        problem = "";
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(1500);
            byte[] bytes = Encoding.Unicode.GetBytes(command + "\0");
            pipe.Write(bytes, 0, bytes.Length);
            pipe.Flush();

            if (wantReply)
            {
                // The answer comes as plain bytes, possibly mixed with log lines; the driver hangs up after one command.
                var buffer = new byte[512];
                using var limit = new CancellationTokenSource(1000);
                try
                {
                    int read = pipe.ReadAsync(buffer, 0, buffer.Length, limit.Token).GetAwaiter().GetResult();
                    reply = Encoding.ASCII.GetString(buffer, 0, read).Replace("\0", "");
                }
                catch (OperationCanceledException)
                {
                    problem = "the driver did not answer";
                    return false;
                }
            }
            return true;
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException)
        {
            problem = e.Message;
            return false;
        }
    }
}
