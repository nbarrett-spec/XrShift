using System.Diagnostics;
using System.Speech.Recognition;
using System.Text.Json;
using System.Text.RegularExpressions;
using XrShift.Scene;
using XrShift.Tracking;

namespace XrShift.App;

/// <summary>
/// Push to talk: listens for one spoken command with the speech recogniser that comes with Windows (on this PC,
/// nothing sent anywhere). It only listens for the commands and names that exist right now, which is what makes
/// a small recogniser reliable; the free part after "tell X to" is the exception and is the least accurate.
/// shortcut: Windows' recogniser, because it needs no download; swap in Whisper here if free speech needs to be better.
/// </summary>
internal sealed partial class Voice : IDisposable
{
    SpeechRecognitionEngine? _engine;
    bool _listening;

    /// <summary>A command was heard clearly. Raised on a recogniser thread.</summary>
    public event Action<string>? Heard;
    /// <summary>Something to tell the wearer instead (nothing heard, not sure, no microphone). Raised on any thread.</summary>
    public event Action<string>? Status;

    /// <summary>Starts listening for one command. It stops by itself when the wearer stops talking.</summary>
    public void Listen(VoiceCommands.Known known)
    {
        if (_listening) return;
        try
        {
            if (_engine is null)
            {
                _engine = new SpeechRecognitionEngine();
                _engine.SetInputToDefaultAudioDevice();
                _engine.InitialSilenceTimeout = TimeSpan.FromSeconds(5);
                _engine.EndSilenceTimeout = TimeSpan.FromSeconds(0.8);
                _engine.SpeechRecognized += (_, e) =>
                {
                    if (e.Result.Confidence >= 0.45f) Heard?.Invoke(e.Result.Text);
                    else Status?.Invoke($"Not sure I heard that: \"{e.Result.Text}\"");
                };
                _engine.RecognizeCompleted += (_, e) =>
                {
                    _listening = false;
                    if (e.Result is null) Status?.Invoke("Heard nothing.");
                };
            }
            _engine.UnloadAllGrammars();
            foreach (Grammar grammar in Grammars(known)) _engine.LoadGrammar(grammar);
            _listening = true;
            _engine.RecognizeAsync(RecognizeMode.Single);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or PlatformNotSupportedException or System.Runtime.InteropServices.COMException)
        {
            _listening = false;
            _engine?.Dispose();
            _engine = null;
            Status?.Invoke($"Voice is not available: {e.Message}");
        }
    }

    /// <summary>One grammar per kind of command, built from what can be named right now.</summary>
    internal static List<Grammar> Grammars(VoiceCommands.Known known)
    {
        // A name as it would be said: "abel-builder-platform" is "abel builder platform".
        static string[] Spoken(IEnumerable<string> names) =>
            names.Select(n => NotWords().Replace(n, " ").Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        var grammars = new List<Grammar>();
        void Add(string[] starts, string[] names, bool thenFreeSpeech = false)
        {
            if (names.Length == 0) return;
            var phrase = new GrammarBuilder(new Choices(starts));
            phrase.Append("the", 0, 1);
            phrase.Append(new Choices(names));
            if (thenFreeSpeech)
            {
                phrase.Append("to");
                phrase.AppendDictation();
            }
            grammars.Add(new Grammar(phrase));
        }
        string[] screens = Spoken(known.Screens.Select(s => s.Name)), objects = Spoken(known.Objects.Select(o => o.Label));
        Add(VoiceCommands.OpenWords, [.. screens, .. objects]);
        Add(VoiceCommands.HideWords, screens);
        Add(VoiceCommands.LoadWords, Spoken(known.Layouts));
        Add([VoiceCommands.SpawnPhrase], Spoken(known.Projects));
        Add([VoiceCommands.LayoutWord], VoiceCommands.LayoutNames);
        Add([VoiceCommands.SizeWord], VoiceCommands.SizeNames);
        Add(VoiceCommands.TellWords, Spoken(known.Objects.Where(o => o.Kind == "bot").Select(o => o.Label)), thenFreeSpeech: true);
        return grammars;
    }

    public void Dispose() => _engine?.Dispose();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")] private static partial Regex NotWords();
}

/// <summary>The folders a new bot can be started in: <c>projects.json</c> in the settings folder, name to path.</summary>
internal static class Projects
{
    static string FilePath => Path.Combine(TrackingConfig.Folder, "projects.json");

    public static IReadOnlyDictionary<string, string> Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath)) is { } read)
                return new Dictionary<string, string>(read, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or JsonException or ArgumentException) { }
        return new Dictionary<string, string>();
    }

    /// <summary>Opens a terminal in the project's folder running Claude Code. Its hooks then make the bot appear. Null when started.</summary>
    public static string? Spawn(string name)
    {
        if (!Load().TryGetValue(name, out string? folder)) return $"No project named '{name}'. Add it to {FilePath}.";
        if (!Directory.Exists(folder)) return $"The folder for '{name}' is not there: {folder}";
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", "/k claude") { WorkingDirectory = folder, UseShellExecute = true })?.Dispose();
            return null;
        }
        catch (System.ComponentModel.Win32Exception e) { return $"Could not start a session: {e.Message}"; }
    }
}
