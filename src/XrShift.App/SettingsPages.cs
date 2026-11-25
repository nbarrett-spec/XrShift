using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using XrShift.Scene;
using XrShift.Tracking;

namespace XrShift.App;

/// <summary>
/// The settings window's newer parts: size and layout presets, zoom steps, steadiness, the status on Home, the
/// Bots, Voice and Other PCs pages, and the key reference.
/// </summary>
internal sealed partial class SettingsWindow
{
    /// <summary>Two rows of buttons under the room: arrangements, and the layout presets above them.</summary>
    const int DockHeight = 108;

    readonly PillButton _keys = new(), _bringNew = new();

    /// <summary>Where Claude Code keeps its settings. A trial run (XRSHIFT_CONFIG set) uses a copy in its own folder, never the real one.</summary>
    public string ClaudeSettingsPath { get; set; } = Environment.GetEnvironmentVariable("XRSHIFT_CONFIG") is { Length: > 0 }
        ? Path.Combine(TrackingConfig.Folder, "claude-settings.json")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    /// <summary>The bots now, read while the Bots page or Home is open. Null when the app has none to give (a picture of the window).</summary>
    public Func<Deck>? BotsSource { get; set; }

    /// <summary>The wearer pressed Try it on the Voice page: listen for one command.</summary>
    public event Action? VoiceTryRequested;

    static string NetworkPath => Path.Combine(TrackingConfig.Folder, "network.json");
    static string ProjectsPath => Path.Combine(TrackingConfig.Folder, "projects.json");
    static string ExePath => Environment.ProcessPath ?? Application.ExecutablePath;
    float Dpi => DeviceDpi / 96f;

    readonly System.Windows.Forms.Timer _pageTimer = new() { Interval = 2000 };

    protected override void Dispose(bool disposing)
    {
        // A timer outlives its window unless stopped: it would go on filling pages that are gone.
        if (disposing) { _pageTimer.Dispose(); _tips.Dispose(); }
        base.Dispose(disposing);
    }

    /// <summary>Called whenever a page comes to the front: pages that show live things fill themselves now.</summary>
    void PageShown(int index)
    {
        _pageTimer.Stop();
        if (index == 0) ShowStatus();
        if (index == BotsIndex) ShowBots();
        if (index == VoiceIndex) ShowVoice();
        if (index == PcsIndex) ShowPcs();
        if (index is 0 or BotsIndex) _pageTimer.Start();
    }

    // ---- Screens: size presets, layout presets, zoom steps ----------------------------------------

    readonly PillButton[] _sizePills = [new(), new(), new()];
    readonly PillButton _sizeAll = new();
    readonly Label _sizeNote = new();

    void BuildSizePresets(int y)
    {
        int[] widths = [78, 86, 86];
        int x = 20;
        foreach (SizePreset size in Enum.GetValues<SizePreset>())
        {
            PillButton pill = _sizePills[(int)size];
            Place(pill, _pageScreen, size.ToString(), x, y, widths[(int)size], 32, PillButton.Looks.Solid, Sizes.About(size) + " For this screen.");
            pill.Click += (_, _) => ApplySize(size);
            x += widths[(int)size] + 6;
        }
        Place(_sizeAll, _pageScreen, "All  ▾", x, y, InspectorWidth - 20 - x, 32, PillButton.Looks.Ghost, "One size for every shown screen at once.");
        _sizeAll.Click += (_, _) =>
        {
            ContextMenuStrip menu = NewMenu();
            menu.ShowItemToolTips = true;
            foreach (SizePreset size in Enum.GetValues<SizePreset>())
            {
                var item = new ToolStripMenuItem($"Every shown screen {size}") { ForeColor = Ui.Ink, ToolTipText = Sizes.About(size) };
                item.Click += (_, _) => Relayout(Sizes.Apply(_layout, size, PictureSize, Optics));
                menu.Items.Add(item);
            }
            menu.Show(_sizeAll, new Point(0, _sizeAll.Height));
        };
        _sizeNote.SetBounds(20, y + 38, InspectorWidth - 40, 40);
        _sizeNote.ForeColor = Ui.Muted;
        _sizeNote.BackColor = Ui.Inspector;
        _sizeNote.Font = Ui.Text(9f);
        _pageScreen.Controls.Add(_sizeNote);
    }

    void ApplySize(SizePreset size)
    {
        if (_selected is null || _layout.Find(_selected) is not { } spec) return;
        (int w, int h) = PictureSize(spec);
        Change(s => Sizes.Apply(s, size, w, h, Optics));
        LoadSelected();
    }

    /// <summary>Which size the selected screen is at, lit, and what that means in a line.</summary>
    void ShowSizePreset(ScreenSpec? spec)
    {
        foreach (PillButton pill in _sizePills) pill.Enabled = spec is not null;
        if (spec is null) { _sizeNote.Text = ""; return; }
        (int w, int h) = PictureSize(spec);
        SizePreset? now = Sizes.Of(spec, w, h, Optics);
        for (int i = 0; i < _sizePills.Length; i++) _sizePills[i].On = now == (SizePreset)i;
        _sizeNote.Text = Sizes.Describe(spec, w, h, Optics);
    }

    void BuildLayoutPresets(int y)
    {
        (LayoutPreset How, string Text, int Width)[] presets =
        [
            (LayoutPreset.Solo, "Solo", 72), (LayoutPreset.MainAndSide, "Main + side", 104), (LayoutPreset.TripleArc, "Triple arc", 96),
            (LayoutPreset.Stack, "Stack", 72), (LayoutPreset.Cockpit, "Cockpit", 84),
        ];
        int x = ViewWidth - 16 - presets.Sum(p => p.Width) - 6 * (presets.Length - 1);
        foreach ((LayoutPreset how, string text, int width) in presets)
        {
            var pill = new PillButton();
            Place(pill, _space, text, x, y, width, 36, PillButton.Looks.Ghost,
                Presets.About(how) + " The selected screen goes in front, every screen sized from its real picture. It rearranges this profile; to keep the old one, make a copy first with +.");
            pill.Click += (_, _) =>
            {
                ScreenSpec? main = _selected is null ? null : _layout.Find(_selected);
                // Screens keep the size they are at (Sharp unless one was chosen), so a layout never blurs them.
                SizePreset size = main is null ? SizePreset.Sharp : Sizes.Of(main, PictureSize(main).Width, PictureSize(main).Height, Optics) ?? SizePreset.Sharp;
                Relayout(Presets.Apply(_layout, how, size, PictureSize, Optics, _selected));
            };
            x += width + 6;
        }
    }

    void Relayout(Layout changed)
    {
        if (changed == _layout) return;
        _layout = changed;
        LayoutChanged?.Invoke(_layout);
        Refill();
    }

    readonly List<(float Zoom, PillButton Pill)> _zoomPills = [];
    readonly Label _zoomNote = new();

    void BuildZoomSteps(ref int y)
    {
        int x = 20, width = (InspectorWidth - 40 - 4 * 6) / Sizes.ZoomSteps.Length;
        foreach (float step in Sizes.ZoomSteps)
        {
            var pill = new PillButton();
            Place(pill, _pageFollow, $"{step * 100:F0}%", x, y, width, 32, PillButton.Looks.Solid,
                $"Zoom {Sizes.DescribeZoom(step)}. Every screen at once, keeping the arrangement. Ctrl+Alt+PgUp / PgDn from anywhere.");
            pill.Click += (_, _) => ChangeView(s => s with { Zoom = step });
            _zoomPills.Add((step, pill));
            x += width + 6;
        }
        _zoomNote.SetBounds(20, y + 38, InspectorWidth - 40, 20);
        _zoomNote.ForeColor = Ui.Muted;
        _zoomNote.BackColor = Ui.Inspector;
        _zoomNote.Font = Ui.Text(9f);
        _pageFollow.Controls.Add(_zoomNote);
        y += 64;
    }

    // ---- Settings: steadiness -------------------------------------------------------------------

    readonly PillButton _steadySwitch = new(), _calibrate = new(), _advancedToggle = new();
    readonly PillButton[] _aheadPills = [new(), new(), new()];
    static readonly (string Name, float Ms, string Hint)[] AheadChoices =
    [
        ("Smoother", ViewSettings.Smoother, "Predicts your head at most 15 ms ahead: the calmest picture when you hold still; screens trail a little on fast turns."),
        ("Balanced", ViewSettings.Balanced, "Predicts your head up to 25 ms ahead: the usual choice."),
        ("Snappier", ViewSettings.Snappier, "Predicts your head up to 40 ms ahead: screens keep up with fast turns, but may shiver as you stop."),
    ];
    readonly Label _steadyNote = new();
    bool _advancedOn;

    void BuildSteadiness(Card steady, int w)
    {
        Place(_steadySwitch, steady, "Reading steady: on", 20, 60, 230, 36, PillButton.Looks.Solid,
            "Holds the picture still while your head is nearly still, so text does not shake as you read. A real movement lets it go at once.");
        _steadySwitch.Click += (_, _) => ChangeView(s => s with { ReadingSteady = !s.ReadingSteady });
        for (int i = 0; i < AheadChoices.Length; i++)
        {
            (string name, float ms, string hint) = AheadChoices[i];
            Place(_aheadPills[i], steady, name, 20 + i * 136, 108, 130, 36, PillButton.Looks.Solid, hint);
            _aheadPills[i].Click += (_, _) => ChangeView(s => s with { LookAheadMs = ms });
        }
        Place(_calibrate, steady, "Calibrate", 20, 156, 130, 36, PillButton.Looks.Ghost, NotReadyHint);
        _calibrate.Click += (_, _) =>
        {
            if (_setupReady || _setupRunning) ShowCalibration();
            else Tell(NotReadyHint);
        };
        _steadyNote.SetBounds(166, 156, w - 186, 80);
        _steadyNote.ForeColor = Ui.Muted;
        _steadyNote.BackColor = Ui.Inspector;
        _steadyNote.Font = Ui.Text(9f);
        steady.Controls.Add(_steadyNote);
        Place(_advancedToggle, steady, "Advanced  ▾", w - 20 - 140, 14, 140, 32, PillButton.Looks.Ghost,
            "The four fine controls: glasses delay, view angle, level and lean. Double-click a slider to put it back.");
        _advancedToggle.Click += (_, _) => ShowAdvanced(!_advancedOn);
    }

    // ---- Settings: glasses setup ------------------------------------------------------------------

    const string NotReadyHint = "Glasses setup needs the glasses connected and sending head motion: plug them in, wait until the top of this window shows fps, then click Calibrate.";
    const string ReadyHint = "Glasses setup, guided in the glasses: the gyro's resting reading, the sensor axes, then level, lean and view angle from upright edges in your room. Ctrl+Alt+C.";

    /// <summary>The wearer asked to start glasses setup from this step.</summary>
    public event Action<SetupStepKind>? CalibrationStartRequested;
    /// <summary>press, retry, skip, cancel or undo: the same as the keys in the glasses.</summary>
    public event Action<string>? CalibrationAction;

    readonly Card _setupCard = new()
    {
        Title = "Glasses setup",
        Subtitle = "Put the glasses on: everything you need is shown in them. Space or Enter confirms, Backspace retries a step, S skips it, Esc cancels and puts everything back.",
        BackColor = Ui.Inspector,
    };
    readonly PillButton[] _setupFrom = [new(), new(), new(), new()];
    readonly PillButton _setupStart = new(), _setupClose = new(), _setupConfirm = new(), _setupRetry = new(), _setupSkip = new(), _setupCancel = new(), _setupUndo = new();
    readonly Label _setupStep = new(), _setupSays = new(), _setupNote = new(), _setupResults = new();
    SetupStepKind _setupStartAt = SetupStepKind.Rest;
    bool _setupReady, _setupRunning;
    Control[] _setupCovers = [];

    /// <summary>The panel sits where <paramref name="covers"/> are, which are hidden while it shows.</summary>
    void BuildSetupPanel(Control[] covers)
    {
        _setupCovers = covers;
        int top = covers.Min(c => c.Top), bottom = covers.Max(c => c.Bottom);
        _setupCard.SetBounds(36, top, ContentWidth - 72, bottom - top);
        int w = _setupCard.Width;

        var from = new Label { Text = "Start from", ForeColor = Ui.Muted, BackColor = Ui.Inspector, Font = Ui.Text(10f) };
        from.SetBounds(20, 114, 100, 30);
        _setupCard.Controls.Add(from);
        string[] names = ["Resting reading", "Axes", "Level and lean", "View angle"];
        int[] widths = [150, 80, 140, 120];
        for (int i = 0, x = 120; i < names.Length; x += widths[i] + 6, i++)
        {
            var step = (SetupStepKind)i;
            Place(_setupFrom[i], _setupCard, names[i], x, 108, widths[i], 34, PillButton.Looks.Solid, $"Start glasses setup at {names[i].ToLowerInvariant()}, keeping what was measured before it.");
            _setupFrom[i].Click += (_, _) => { _setupStartAt = step; ShowSetupButtons(); };
        }
        Place(_setupStart, _setupCard, "Start", w - 20 - 120 - 8 - 100, 108, 120, 34, PillButton.Looks.Accent, "Starts glasses setup. Then follow what the glasses show.");
        _setupStart.Click += (_, _) => CalibrationStartRequested?.Invoke(_setupStartAt);
        Place(_setupClose, _setupCard, "Close", w - 20 - 100, 108, 100, 34, PillButton.Looks.Ghost, "Hides this panel. Calibrate opens it again.");
        _setupClose.Click += (_, _) => ShowCalibration(false);

        foreach ((Label label, int y, int height, Color colour, float size) in new[]
                 {
                     (_setupStep, 154, 22, Ui.Accent, 10.5f), (_setupSays, 178, 46, Ui.Ink, 10f),
                     (_setupNote, 226, 22, Ui.Warn, 10f), (_setupResults, 250, 48, Ui.Muted, 9f),
                 })
        {
            label.SetBounds(20, y, w - 40, height);
            label.ForeColor = colour;
            label.BackColor = Ui.Inspector;
            label.Font = Ui.Text(size);
            _setupCard.Controls.Add(label);
        }

        (PillButton Pill, string Text, string Action, string Hint)[] buttons =
        [
            (_setupConfirm, "Confirm", "press", "The same as Space or Enter: passes the mode check, takes a press with your head as it is now, or closes the finished setup."),
            (_setupRetry, "Retry", "retry", "The same as Backspace: this step again from its start."),
            (_setupSkip, "Skip", "skip", "The same as S: leaves this step as it is and goes on."),
            (_setupCancel, "Cancel", "cancel", "The same as Esc: stops, and puts back everything this setup changed."),
        ];
        int bx = 20;
        foreach ((PillButton pill, string text, string action, string hint) in buttons)
        {
            Place(pill, _setupCard, text, bx, bottom - top - 54, 120, 36, pill == _setupConfirm ? PillButton.Looks.Accent : PillButton.Looks.Solid, hint);
            pill.Click += (_, _) => CalibrationAction?.Invoke(action);
            bx += 126;
        }
        Place(_setupUndo, _setupCard, "Undo calibration", w - 20 - 190, bottom - top - 54, 190, 36, PillButton.Looks.Ghost,
            "Puts view angle, level, lean, the sensor axes and the gyro reading back as they were before the last glasses setup. Offered until XrShift quits.");
        _setupUndo.Click += (_, _) => CalibrationAction?.Invoke("undo");

        _setupCard.Visible = false;
        _settingsPage.Controls.Add(_setupCard);
        _setupCard.BringToFront();
        SetCalibration(null, false, [], "");
    }

    /// <summary>Opens (or hides) the glasses setup panel on the Settings page.</summary>
    public void ShowCalibration(bool on = true)
    {
        if (on) ShowSection(SettingsIndex);
        _setupCard.Visible = on;
        foreach (Control c in _setupCovers) c.Visible = !on;
    }

    /// <summary>Whether the glasses are connected and tracking, so setup can start.</summary>
    public void SetCalibrationReady(bool ready)
    {
        if (ready == _setupReady) return;
        _setupReady = ready;
        _calibrate.Hint = ready ? ReadyHint : NotReadyHint;
        _tips.SetToolTip(_calibrate, _calibrate.Hint);
        ShowSetupButtons();
    }

    /// <summary>Where glasses setup is (null screen: not running), what it found, and why the last request was refused.</summary>
    public void SetCalibration(SetupScreen? screen, bool canUndo, IReadOnlyList<string> results, string message)
    {
        _setupRunning = screen is not null;
        _setupUndo.Enabled = canUndo && !_setupRunning;
        Show(_setupStep, screen?.Progress ?? (_setupReady ? "Not running. Choose where to start, then Start." : "Not running: " + NotReadyHint));
        Show(_setupSays, screen?.Instruction ?? "");
        Show(_setupNote, message.Length > 0 ? message : screen?.Note ?? "");
        Show(_setupResults, string.Join("\n", results.TakeLast(3)));
        ShowSetupButtons();

        static void Show(Label label, string text) { if (label.Text != text) label.Text = text; }
    }

    void ShowSetupButtons()
    {
        for (int i = 0; i < _setupFrom.Length; i++)
        {
            _setupFrom[i].On = (int)_setupStartAt == i;
            _setupFrom[i].Enabled = !_setupRunning;
        }
        _setupStart.Enabled = _setupReady && !_setupRunning;
        foreach (PillButton pill in (PillButton[])[_setupConfirm, _setupRetry, _setupSkip, _setupCancel]) pill.Enabled = _setupRunning;
    }

    void ShowAdvanced(bool on)
    {
        _advancedOn = on;
        Control[] simple = [_steadySwitch, _calibrate, _steadyNote, .. _aheadPills], advanced = [_delay, _fov, _level, _roll];
        foreach (Control c in simple) c.Visible = !on;
        foreach (Control c in advanced) c.Visible = on;
        _advancedToggle.Text = on ? "Simple  ▴" : "Advanced  ▾";
        _advancedToggle.On = on;
    }

    /// <summary>The switches added since the first pages: steadiness, zoom steps and bot notes.</summary>
    void ShowNewViewToggles()
    {
        _steadySwitch.On = _view.ReadingSteady;
        _steadySwitch.Text = _view.ReadingSteady ? "Reading steady: on" : "Reading steady: off";
        int chosen = Array.FindIndex(AheadChoices, c => MathF.Abs(c.Ms - _view.LookAheadMs) < 0.5f);
        for (int i = 0; i < _aheadPills.Length; i++) _aheadPills[i].On = i == chosen;
        _steadyNote.Text = (chosen >= 0 ? AheadChoices[chosen].Name : "Your own setting") +
                           $": screens are predicted up to {_view.LookAheadMs:F0} ms ahead of your head" +
                           (_view.ReadingSteady ? ", and held still while you read." : "; reading steady is off.");
        foreach ((float zoom, PillButton pill) in _zoomPills) pill.On = MathF.Abs(zoom - _view.Zoom) < 0.005f;
        _zoomNote.Text = "Zoom " + Sizes.DescribeZoom(_view.Zoom);
        _botNotes.On = _view.BotNotes;
        _botNotes.Text = _view.BotNotes ? "Show what a bot was asked: on" : "Show what a bot was asked: off";
    }

    // ---- Home: how things are now -----------------------------------------------------------------

    static readonly string[] StatusNames = ["Glasses", "Mode", "Screens", "Bots", "Voice", "Other PCs", "Version"];
    readonly Label[] _statusValues = [.. StatusNames.Select(_ => new Label())];
    string _liveText = "starting", _glassesDisplay = "";
    bool _liveHealthy, _apiRunning, _netRunning;
    bool? _voiceAvailable;

    void BuildStatus(Card card)
    {
        for (int i = 0; i < StatusNames.Length; i++)
        {
            var name = new Label { Text = StatusNames[i], ForeColor = Ui.Muted };
            name.SetBounds(20, 54 + i * 21, 100, 20);
            _statusValues[i].SetBounds(120, 54 + i * 21, card.Width - 140, 20);
            _statusValues[i].ForeColor = Ui.Ink;
            _statusValues[i].AutoEllipsis = true; // the whole line is in its tooltip
            foreach (Label l in new[] { name, _statusValues[i] })
            {
                l.BackColor = Ui.Inspector;
                l.Font = Ui.Text(10f);
                card.Controls.Add(l);
            }
        }
    }

    /// <summary>The display the glasses are on, in words, e.g. "DISPLAY3 (1920 x 1080)".</summary>
    public void SetGlassesDisplay(string display) { _glassesDisplay = display; ShowStatus(); }

    /// <summary>Whether the Control API (bots, scripts) and the listener for other PCs are running in this session.</summary>
    public void SetLinks(bool apiRunning, bool netRunning)
    {
        _apiRunning = apiRunning;
        _netRunning = netRunning;
        ShowStatus();
    }

    bool VoiceAvailable()
    {
        if (_voiceAvailable is { } known) return known;
        try { _voiceAvailable = System.Speech.Recognition.SpeechRecognitionEngine.InstalledRecognizers().Count > 0; }
        catch (Exception e) when (e is InvalidOperationException or PlatformNotSupportedException or System.Runtime.InteropServices.COMException) { _voiceAvailable = false; }
        return _voiceAvailable.Value;
    }

    void ShowStatus()
    {
        if (!_homePage.Visible && IsHandleCreated) return;
        string mode = _vrOn ? "VR" : _gameOn ? "Plain screen" : _glassesStationary ? "Stationary" : "Follow";
        string glasses = _gameOn ? "plain screen: XrShift has stepped aside"
            : _liveHealthy ? $"connected{(_glassesDisplay.Length > 0 ? " on " + _glassesDisplay : "")}, {_liveText}"
            : _liveText == "starting" ? "waiting: plug them in, set Windows to Extend (Win+P)"
            : $"{_liveText}: plug them in, set Windows to Extend (Win+P)";
        int shown = _layout.Screens.Count(s => s.Visible);
        string screens = $"{shown} shown of {_layout.Screens.Count}{(_virtualNote.Text.Length > 0 ? ", " + _virtualNote.Text : "")}";
        List<SceneObject> bots = Bots();
        bool flag = File.Exists(LiveApp.ApiFlag);
        string botLine = _apiRunning
            ? $"on, {bots.Count} now{(bots.Count(b => b.State == BotState.NeedsYou) is > 0 and var waiting ? $", {waiting} waiting for you" : "")}{(flag ? "" : " (off from the next start)")}"
            : flag ? "on from the next start" : "off (switch them on under Bots)";
        string voice = VoiceAvailable() ? $"ready: press {Hotkeys.Text((int)Keys.T)} and say a command" : "not available: Windows has no speech recogniser here";
        NetworkFile.Summary? net = ReadNetwork(out _);
        string pcs = _netRunning ? $"linked, listening on port {net?.Port ?? NetworkFile.DefaultPort}"
            : net is { Valid: true } ? "set up, links from the next start"
            : "not set up (see Other PCs)";
        string[] values = [glasses, mode, screens, botLine, voice, pcs, "XrShift " + Application.ProductVersion.Split('+')[0]];
        for (int i = 0; i < values.Length; i++)
        {
            if (_statusValues[i].Text == values[i]) continue;
            _statusValues[i].Text = values[i];
            _tips.SetToolTip(_statusValues[i], values[i]);
        }
    }

    List<SceneObject> Bots() => BotsSource?.Invoke().Objects.Where(o => o.Kind == "bot").ToList() ?? [];

    // ---- Bots -----------------------------------------------------------------------------------

    readonly PillButton _botsSwitch = new(), _botNotes = new(), _addProject = new(), _hooksInstall = new(), _hooksRemove = new();
    readonly Label _botsState = new(), _hooksState = new();
    readonly System.Windows.Forms.Panel _botList = new(), _projectList = new();
    string _botListShown = "\0";

    void BuildBots()
    {
        PageTitle(_botsPage, "Bots", "Your Claude Code sessions as bots in the room: what each is doing, and the folders new ones start in.");
        int w = (ContentWidth - 72 - 18) / 2, right = 36 + w + 18;

        var on = new Card { Title = "Bots", BackColor = Ui.Inspector };
        on.SetBounds(36, 112, w, 190);
        Place(_botsSwitch, on, "Bots: off", 20, 58, 230, 36, PillButton.Looks.Solid,
            "Lets Claude Code sessions (and scripts) on this PC talk to XrShift, so each session shows as a bot. Read when XrShift starts: a change applies from the next start.");
        _botsSwitch.Click += (_, _) =>
        {
            try
            {
                if (File.Exists(LiveApp.ApiFlag)) File.Delete(LiveApp.ApiFlag);
                else { Directory.CreateDirectory(TrackingConfig.Folder); File.WriteAllText(LiveApp.ApiFlag, ""); }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Tell($"Could not save the choice: {e.Message}"); }
            ShowBots();
        };
        Place(_botNotes, on, "Show what a bot was asked: on", 266, 58, w - 286, 36, PillButton.Looks.Solid,
            "A bot's card in the room shows what it last said or was asked. Switch it off when others can see your room. Nothing is deleted: switch it on and the notes come back.");
        _botNotes.Click += (_, _) => ChangeView(s => s with { BotNotes = !s.BotNotes });
        AddLabel(on, _botsState, 20, 106, w - 40, 70, Ui.Muted, 9.5f);
        _botsPage.Controls.Add(on);

        var now = new Card { Title = "Bots now", BackColor = Ui.Inspector };
        now.SetBounds(36, 320, w, PageHeight - 320 - 22);
        _botList.SetBounds(20, 52, w - 40, now.Height - 66);
        _botList.BackColor = Ui.Inspector;
        _botList.AutoScroll = true;
        now.Controls.Add(_botList);
        _botsPage.Controls.Add(now);

        var projects = new Card { Title = "Projects", Subtitle = "Folders a new bot can start in: say \"new bot in\" and the name.", BackColor = Ui.Inspector };
        projects.SetBounds(right, 112, w, 330);
        _projectList.SetBounds(20, 80, w - 40, 190);
        _projectList.BackColor = Ui.Inspector;
        _projectList.AutoScroll = true;
        projects.Controls.Add(_projectList);
        Place(_addProject, projects, "+  Add a project", 20, 278, 180, 36, PillButton.Looks.Solid, "Pick a folder and give it a short name to say out loud.");
        _addProject.Click += (_, _) => AddProject();
        _botsPage.Controls.Add(projects);

        var hooks = new Card { Title = "Claude Code hooks", Subtitle = "A small command Claude Code runs at each step of a session, which tells XrShift what the bot is doing.", BackColor = Ui.Inspector };
        hooks.SetBounds(right, 460, w, PageHeight - 460 - 22);
        AddLabel(hooks, _hooksState, 20, 96, w - 40, 72, Ui.Ink, 9.5f);
        Place(_hooksInstall, hooks, "Install", 20, hooks.Height - 60, 180, 38, PillButton.Looks.Accent,
            "Adds XrShift's hook to Claude Code's settings for this copy of XrShift, keeping a backup and every other setting.");
        _hooksInstall.Click += (_, _) => ChangeHooks(remove: false);
        Place(_hooksRemove, hooks, "Remove", 212, hooks.Height - 60, 140, 38, PillButton.Looks.Ghost,
            "Takes XrShift's hook out of Claude Code's settings, keeping a backup and every other setting.");
        _hooksRemove.Click += (_, _) => ChangeHooks(remove: true);
        _botsPage.Controls.Add(hooks);

        _pageTimer.Tick += (_, _) =>
        {
            if (_botsPage.Visible) ShowBotList();
            if (_homePage.Visible) ShowStatus();
        };
    }

    void ShowBots()
    {
        bool flag = File.Exists(LiveApp.ApiFlag);
        _botsSwitch.On = flag;
        _botsSwitch.Text = flag ? "Bots: on" : "Bots: off";
        _botsState.Text = (_apiRunning, flag) switch
        {
            (true, true) => "On now. Each Claude Code session with the hooks below shows up as a bot.",
            (true, false) => "Still on now; off from the next start of XrShift.",
            (false, true) => "On from the next start of XrShift (quit it from the tray and start it again).",
            _ => "Off. Switch it on, then start XrShift again.",
        };
        ShowBotList();
        ShowProjects();
        ShowHooks();
    }

    void ShowBotList()
    {
        List<SceneObject> bots = Bots();
        string key = string.Join("|", bots.Select(b => $"{b.Id}{b.Label}{b.State}{b.Pc}{b.Offline}{b.Note}"));
        if (key == _botListShown) return;
        _botListShown = key;
        var rows = bots.Select(b =>
        {
            string state = b.Offline ? "offline" : b.State switch
            {
                BotState.NeedsYou => "needs you", BotState.Working => "working", BotState.Done => "done",
                BotState.Failed => "stopped", _ => "idle",
            };
            Color colour = b.Offline ? Ui.Faint : b.State switch
            {
                BotState.NeedsYou => Ui.Warn, BotState.Working => Ui.Accent, BotState.Done => Ui.Good, BotState.Failed => Ui.Bad, _ => Ui.Faint,
            };
            string where = b.Pc.Length > 0 ? $" · on {b.Pc}" : " · this PC";
            return new Row($"●  {b.Label}", state + where + (b.Note.Length > 0 ? $" · \"{b.Note}\"" : ""), colour, null, "");
        }).ToList();
        FillList(_botList, rows, BotsSource is null || !_apiRunning
            ? "No bots: they show up here once bots are on and a Claude Code session with the hooks runs."
            : "No Claude Code sessions are running.");
    }

    // ---- Bots: projects --------------------------------------------------------------------------

    /// <summary>projects.json, or null with a reason when it is there but cannot be read (then it is not touched).</summary>
    static Dictionary<string, string>? ReadProjects(out string why)
    {
        why = "";
        try
        {
            if (!File.Exists(ProjectsPath)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string text = File.ReadAllText(ProjectsPath);
            if (string.IsNullOrWhiteSpace(text)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return new Dictionary<string, string>(JsonSerializer.Deserialize<Dictionary<string, string>>(text) ?? [], StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or JsonException or ArgumentException or UnauthorizedAccessException)
        {
            why = $"projects.json could not be read ({e.Message}). Fix or delete it in the settings folder; it is left as it is.";
            return null;
        }
    }

    void SaveProjects(Dictionary<string, string> projects)
    {
        try
        {
            Directory.CreateDirectory(TrackingConfig.Folder);
            File.WriteAllText(ProjectsPath, JsonSerializer.Serialize(projects, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Tell($"Could not save projects.json: {e.Message}"); }
        ShowProjects();
        if (_voicePage.Visible) ShowVoice();
    }

    void ShowProjects()
    {
        Dictionary<string, string>? projects = ReadProjects(out string why);
        _addProject.Enabled = projects is not null;
        FillList(_projectList, (projects ?? []).OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p =>
            new Row(p.Key, p.Value + (Directory.Exists(p.Value) ? "" : "   (folder not found)"), Ui.Ink, () =>
            {
                if (Ask($"Take \"{p.Key}\" off the list of projects?\n\nThe folder itself is not touched.", "Remove a project") && ReadProjects(out _) is { } now)
                {
                    now.Remove(p.Key);
                    SaveProjects(now);
                }
            }, $"Takes {p.Key} off the list. The folder is not touched.")).ToList(),
            projects is null ? why : "No projects yet. Add the folders you start Claude Code in.");
    }

    void AddProject()
    {
        if (ReadProjects(out string why) is not { } projects) { Tell(why); return; }
        using var pick = new FolderBrowserDialog { Description = "The folder a new bot starts in", UseDescriptionForTitle = true };
        if (pick.ShowDialog(this) != DialogResult.OK) return;
        if (AskName(Path.GetFileName(pick.SelectedPath.TrimEnd('\\')), "Project name (said as \"new bot in ...\")") is not { Length: > 0 } name) return;
        if (projects.TryGetValue(name, out string? old) && !Ask($"\"{name}\" already starts in\n{old}\n\nStart it in\n{pick.SelectedPath}\ninstead?", "Change a project")) return;
        projects[name] = pick.SelectedPath;
        SaveProjects(projects);
    }

    // ---- Bots: Claude Code hooks ------------------------------------------------------------------

    string? ReadClaudeSettings(out string? problem)
    {
        problem = null;
        try { return File.Exists(ClaudeSettingsPath) ? File.ReadAllText(ClaudeSettingsPath) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { problem = e.Message; return null; }
    }

    void ShowHooks()
    {
        string? text = ReadClaudeSettings(out string? problem);
        ClaudeHooks.State state = problem is not null ? ClaudeHooks.State.Unreadable : ClaudeHooks.Check(text, ExePath);
        _hooksState.Text = state switch
        {
            ClaudeHooks.State.Current => "Installed for this copy of XrShift. New Claude Code sessions show up as bots.",
            ClaudeHooks.State.Other => "Installed, but for another copy of XrShift (or not in every step). Update points them all at this one.",
            ClaudeHooks.State.Unreadable => $"Claude Code's settings could not be read, so nothing will be changed: {problem ?? "it is not plain JSON this can change safely."}",
            _ => "Not installed. Claude Code sessions will not show up as bots until they are.",
        } + $"\n{ClaudeSettingsPath}";
        _hooksInstall.Text = state == ClaudeHooks.State.Other ? "Update" : "Install";
        _hooksInstall.Enabled = state is ClaudeHooks.State.None or ClaudeHooks.State.Other;
        _hooksRemove.Enabled = state is ClaudeHooks.State.Current or ClaudeHooks.State.Other;
    }

    /// <summary>Installs, updates or removes the hooks, after saying plainly what will change. A backup is kept first.</summary>
    void ChangeHooks(bool remove)
    {
        string path = ClaudeSettingsPath;
        string? text = ReadClaudeSettings(out string? problem);
        if (problem is not null) { Tell($"Could not read {path}: {problem}"); return; }
        string? changed = remove ? ClaudeHooks.Remove(text, out string why) : ClaudeHooks.Install(text, ExePath, out why);
        if (changed is null) { Tell($"Nothing was changed. {why}"); return; }
        if (changed == text) { ShowHooks(); return; }
        string backup = BackupName(path);
        string what = remove
            ? "Takes XrShift's bot hook out of Claude Code's settings."
            : $"Makes Claude Code run this command at each step of every session:\n{ClaudeHooks.Command(ExePath)}";
        if (!Ask($"{what}\n\nFile: {path}\nSteps: {string.Join(", ", ClaudeHooks.Events)}\n" +
                 "Every other hook and setting stays as it is." +
                 (text is null ? "\nThe file does not exist yet; it will be made." : $"\nA copy of the file as it is now is saved first:\n{backup}") +
                 "\n\nSessions already running keep the old settings until they restart. Go ahead?",
                remove ? "Remove the Claude Code hooks" : "Install the Claude Code hooks")) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (text is not null) File.Copy(path, backup);
            // Written beside it and then swapped in, so a failure half way never leaves a broken settings file.
            string fresh = path + ".xrshift-new";
            File.WriteAllText(fresh, changed, new UTF8Encoding(false));
            File.Move(fresh, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Tell($"Could not change {path}: {e.Message}\nIt is as it was.");
        }
        ShowHooks();
    }

    /// <summary>The first backup gets the name setup-other-pc.ps1 uses; later ones are dated, so the first is never overwritten.</summary>
    static string BackupName(string path)
    {
        string first = path + ".bak-before-xrshift-hooks";
        return File.Exists(first) ? $"{path}.bak-xrshift-{DateTime.Now:yyyyMMdd-HHmmss}" : first;
    }

    // ---- Voice ---------------------------------------------------------------------------------

    readonly PillButton _tryVoice = new();
    readonly Label _voiceState = new(), _voiceResult = new(), _voiceCommands = new();

    void BuildVoice()
    {
        PageTitle(_voicePage, "Voice", "Say one command at a time. Windows' own speech recogniser listens on this PC; nothing is sent anywhere.");
        int w = (ContentWidth - 72 - 18) / 2;
        var talk = new Card { Title = "Push to talk", BackColor = Ui.Inspector };
        talk.SetBounds(36, 112, w, 300);
        AddLabel(talk, _voiceState, 20, 56, w - 40, 64, Ui.Muted, 10f);
        Place(_tryVoice, talk, "Try it", 20, 130, 160, 40, PillButton.Looks.Accent,
            "Listens for one command now, as the talk key does, and shows below what was heard and what it did.");
        _tryVoice.Click += (_, _) =>
        {
            _voiceResult.Text = "Listening... say one command.";
            VoiceTryRequested?.Invoke();
        };
        AddLabel(talk, _voiceResult, 20, 186, w - 40, 96, Ui.Ink, 10f);
        _voicePage.Controls.Add(talk);

        var say = new Card { Title = "What you can say now", BackColor = Ui.Inspector };
        say.SetBounds(36 + w + 18, 112, w, PageHeight - 112 - 22);
        AddLabel(say, _voiceCommands, 20, 52, w - 40, say.Height - 66, Ui.Ink, 9.75f);
        _voicePage.Controls.Add(say);
    }

    void ShowVoice()
    {
        string key = Hotkeys.Text((int)Keys.T);
        _voiceState.Text = VoiceAvailable()
            ? $"From any program: press {key}, then say one command. It stops listening by itself when you stop talking."
            : $"Not available: Windows has no speech recogniser on this PC. Add one in Windows Settings, Time & language, Speech; then {key} works.";
        _tryVoice.Enabled = VoiceAvailable() && VoiceTryRequested is not null;
        if (_voiceResult.Text.Length == 0) _voiceResult.Text = VoiceTryRequested is null ? "Try it works while XrShift is running with the glasses." : "";

        VoiceCommands.Known known = Known();
        static string Some(IEnumerable<string> names)
        {
            List<string> all = names.Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return all.Count == 0 ? "" : string.Join(", ", all.Take(6)) + (all.Count > 6 ? $" and {all.Count - 6} more" : "");
        }
        string screens = Some(known.Screens.Select(s => s.Name)), bots = Some(known.Objects.Where(o => o.Kind == "bot").Select(o => o.Label));
        var lines = new StringBuilder();
        void Line(string words, string names, string what)
        {
            lines.Append(words).Append(names.Length > 0 ? names : "(none right now)").Append('\n');
            lines.Append("      ").Append(what).Append("\n\n");
        }
        Line("open  ", Some([.. known.Screens.Select(s => s.Name), .. known.Objects.Select(o => o.Label)]), "shows a screen, or brings a bot's window forward");
        Line("hide  ", screens, "hides a screen");
        Line("load  ", Some(known.Layouts), "switches to a profile");
        Line("layout  ", string.Join(", ", VoiceCommands.LayoutNames), "arranges the screens as a layout preset");
        Line("size  ", string.Join(", ", VoiceCommands.SizeNames), "one size for every shown screen");
        Line("new bot in  ", Some(known.Projects), "starts Claude Code in a project's folder (Projects, on the Bots page)");
        Line("tell  ", bots.Length > 0 ? bots + "  to ..." : "", "types the rest into that bot's window, without pressing Enter");
        _voiceCommands.Text = lines.ToString().TrimEnd();
    }

    VoiceCommands.Known Known() =>
        new(_layout.Screens, BotsSource?.Invoke().Objects ?? [], _profiles, [.. (ReadProjects(out _) ?? []).Keys]);

    /// <summary>What push to talk heard, or why it heard nothing; null <paramref name="problem"/> means it was done.</summary>
    public void SetVoiceResult(string? heard, string? problem)
    {
        if (heard is null) { _voiceResult.Text = problem ?? ""; return; }
        string did = VoiceCommands.Parse(heard, Known(), out _) is { } request ? Did(request) : "";
        _voiceResult.Text = problem is null
            ? $"Heard: \"{heard}\"\nDid: {(did.Length > 0 ? did : "done")}."
            : $"Heard: \"{heard}\"\nNothing done: {problem}";
    }

    static string Did(JsonObject request)
    {
        JsonNode? p = request["params"];
        string Of(string name) => p?[name]?.GetValue<string>() ?? "";
        return request["method"]?.GetValue<string>() switch
        {
            "object.focus" => "opened it",
            "object.update" => Of("state") == "hidden" ? "hid the screen" : "showed the screen",
            "layout.load" => $"switched to the profile {Of("name")}",
            "preset.apply" => Of("layout").Length > 0 ? $"applied the {Of("layout")} layout" : $"made every shown screen {Of("size")}",
            "bot.spawn" => $"started a new bot in {Of("project")}",
            "bot.tell" => "typed it into the bot's window (press Enter there to send)",
            _ => "",
        };
    }

    // ---- Other PCs -------------------------------------------------------------------------------

    readonly PillButton _newSecret = new(), _openFolder = new(), _addPeer = new(), _makeKit = new();
    readonly Label _linkState = new(), _kitState = new();
    readonly System.Windows.Forms.Panel _peerList = new();

    void BuildPcs()
    {
        PageTitle(_pcsPage, "Other PCs", "Bots on your other PCs can show up here too. Every PC holds the same network.json with a shared secret.");
        int w = (ContentWidth - 72 - 18) / 2, right = 36 + w + 18;

        var link = new Card { Title = "The link", BackColor = Ui.Inspector };
        link.SetBounds(36, 112, w, 300);
        AddLabel(link, _linkState, 20, 56, w - 40, 150, Ui.Ink, 10f);
        Place(_newSecret, link, "Create a new secret", 20, 230, 200, 40, PillButton.Looks.Solid,
            "Makes a new random shared secret in network.json. Every PC then needs the new file (make a new kit, or copy the file).");
        _newSecret.Click += (_, _) => CreateSecret();
        Place(_openFolder, link, "Open the settings folder", 232, 230, 220, 40, PillButton.Looks.Ghost,
            "Opens the folder with network.json, projects.json and XrShift's other settings.");
        _openFolder.Click += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(TrackingConfig.Folder);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{TrackingConfig.Folder}\"") { UseShellExecute = true })?.Dispose();
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException) { Tell($"Could not open the folder: {e.Message}"); }
        };
        _pcsPage.Controls.Add(link);

        var peers = new Card { Title = "PCs", Subtitle = "The names of the PCs in the link, as Windows calls them (Settings, System, About).", BackColor = Ui.Inspector };
        peers.SetBounds(right, 112, w, 300);
        _peerList.SetBounds(20, 80, w - 40, 150);
        _peerList.BackColor = Ui.Inspector;
        _peerList.AutoScroll = true;
        peers.Controls.Add(_peerList);
        Place(_addPeer, peers, "+  Add a PC", 20, 240, 160, 38, PillButton.Looks.Solid, "Adds a PC by its name, so this PC's bots can report to it.");
        _addPeer.Click += (_, _) =>
        {
            if (AskName("", "PC name") is { Length: > 0 } name && ReadNetwork(out _) is { } now) SavePeers([.. now.Peers, name]);
        };
        _pcsPage.Controls.Add(peers);

        var kit = new Card
        {
            Title = "A kit for another PC",
            Subtitle = "A folder to copy to another PC: XrShift, setup-other-pc.ps1 and this PC's network.json. On that PC, quit XrShift if it runs, then run " +
                       "powershell -ExecutionPolicy Bypass -File setup-other-pc.ps1 in the folder. It installs XrShift there, copies the secret and adds the Claude Code hooks.",
            BackColor = Ui.Inspector,
        };
        kit.SetBounds(36, 430, ContentWidth - 72, PageHeight - 430 - 22);
        Place(_makeKit, kit, "Make a kit for another PC", 20, kit.Height - 64, 260, 42, PillButton.Looks.Accent,
            "Pick where to put the kit folder. Nothing is installed here; the folder holds a copy of the app and the shared secret, so keep it to yourself.");
        _makeKit.Click += (_, _) => MakeKit();
        AddLabel(kit, _kitState, 300, kit.Height - 64, kit.Width - 320, 42, Ui.Muted, 9.5f);
        _kitState.TextAlign = ContentAlignment.MiddleLeft;
        _pcsPage.Controls.Add(kit);
    }

    static NetworkFile.Summary? ReadNetwork(out string? text)
    {
        text = null;
        try { text = File.Exists(NetworkPath) ? File.ReadAllText(NetworkPath) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        return NetworkFile.Read(text);
    }

    void ShowPcs()
    {
        NetworkFile.Summary? net = ReadNetwork(out string? text);
        DateTime? made = net?.Created ?? (File.Exists(NetworkPath) ? File.GetLastWriteTime(NetworkPath) : null);
        _linkState.Text = text is null
            ? "Not set up: there is no network.json yet. Create a new secret to start one, then make a kit for each other PC."
            : net is null
                ? "network.json is there but could not be read. Fix it in the settings folder, or create a new secret (that replaces it)."
                : $"{(net.Valid ? _netRunning ? "Set up and listening for bots on other PCs." : "Set up. This PC listens for other PCs' bots from the next start." : "Not usable: the secret is too short or the port is not one that can be used.")}\n" +
                  $"Port: {net.Port}\n" +
                  $"Secret: {net.SecretLength} characters, made {(made is { } m ? m.ToLocalTime().ToString("d MMM yyyy, HH:mm") : "at an unknown time")}{(net.Created is null ? " (the file's date)" : "")}. It is never shown here.\n" +
                  $"This PC is {Environment.MachineName}.";
        _addPeer.Enabled = net is not null;
        _makeKit.Enabled = !_kitBusy;
        FillList(_peerList, (net?.Peers ?? []).Select(peer => new Row(peer,
            string.Equals(peer, Environment.MachineName, StringComparison.OrdinalIgnoreCase) ? "this PC" : "", Ui.Ink, () =>
            {
                if (Ask($"Take {peer} out of the link?\n\nIts bots stop showing up here once its own network.json is changed too.", "Remove a PC") && ReadNetwork(out _) is { } now)
                    SavePeers(now.Peers.Where(p => !string.Equals(p, peer, StringComparison.OrdinalIgnoreCase)));
            }, $"Takes {peer} out of network.json on this PC.")).ToList(),
            net is null ? "Create a secret first." : "No PCs yet. Add this PC and each other PC by name.");
    }

    void SavePeers(IEnumerable<string> peers)
    {
        ReadNetwork(out string? text);
        if (NetworkFile.WithPeers(text, peers) is not { } changed) { Tell("network.json could not be read, so it was not changed."); return; }
        WriteNetwork(changed);
    }

    void CreateSecret()
    {
        ReadNetwork(out string? text);
        if (!Ask((text is null ? "Make network.json with a new random secret?" : "Replace the secret in network.json with a new random one?") +
                 "\n\nEvery PC in the link then needs the new network.json: make a new kit for each, or copy the file into its settings folder. " +
                 "Until then their bots cannot report here. This PC uses the new secret from the next start of XrShift.", "Create a new secret")) return;
        if (NetworkFile.WithSecret(text, NetworkFile.NewSecret(), DateTime.UtcNow) is not { } changed)
        {
            Tell("network.json could not be read, so it was not changed. Fix or delete it in the settings folder first.");
            return;
        }
        WriteNetwork(changed);
    }

    void WriteNetwork(string json)
    {
        try
        {
            Directory.CreateDirectory(TrackingConfig.Folder);
            File.WriteAllText(NetworkPath, json, new UTF8Encoding(false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Tell($"Could not save network.json: {e.Message}"); }
        ShowPcs();
    }

    bool _kitBusy;

    void MakeKit()
    {
        using var pick = new FolderBrowserDialog { Description = "Where to put the kit folder", UseDescriptionForTitle = true };
        if (pick.ShowDialog(this) != DialogResult.OK) return;
        string app = AppContext.BaseDirectory.TrimEnd('\\');
        string kit = Path.Combine(pick.SelectedPath, "XrShift-kit");
        for (int n = 2; Directory.Exists(kit) || File.Exists(kit); n++) kit = Path.Combine(pick.SelectedPath, $"XrShift-kit {n}"); // never over an earlier kit
        if ((kit + "\\").StartsWith(app + "\\", StringComparison.OrdinalIgnoreCase)) { Tell("Pick a folder outside XrShift's own folder."); return; }
        bool network = File.Exists(NetworkPath);
        _kitBusy = true;
        _makeKit.Enabled = false;
        _kitState.Text = "Copying...";
        // Copying the app takes a few seconds: done off the window's thread so the window keeps answering.
        Task.Run(() =>
        {
            Directory.CreateDirectory(kit);
            foreach (string file in Directory.EnumerateFiles(app, "*", SearchOption.AllDirectories))
            {
                string to = Path.Combine(kit, "XrShift", Path.GetRelativePath(app, file));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(file, to);
            }
            File.Copy(Path.Combine(app, "setup-other-pc.ps1"), Path.Combine(kit, "setup-other-pc.ps1"));
            if (network) File.Copy(NetworkPath, Path.Combine(kit, "network.json"));
        }).ContinueWith(done => BeginInvoke(() =>
        {
            _kitBusy = false;
            _makeKit.Enabled = true;
            if (done.Exception?.GetBaseException() is { } e)
            {
                _kitState.Text = $"The kit could not be made: {e.Message}";
                return;
            }
            _kitState.Text = network ? $"Made: {kit}" : $"Made, without network.json (create a secret first): {kit}";
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{kit}\"") { UseShellExecute = true })?.Dispose(); }
            catch (System.ComponentModel.Win32Exception) { }
        }));
    }

    /// <summary>Puts the short guide away (a picture of the window shows the page under it).</summary>
    internal void HideGuide() => _welcome.Visible = false;

    // ---- the key reference ------------------------------------------------------------------------

    void ShowKeys()
    {
        using Form keys = KeysForm();
        keys.ShowDialog(this);
    }

    /// <summary>Every hotkey and what it does, from the same list the keys are registered from.</summary>
    internal Form KeysForm()
    {
        IReadOnlyList<(string Keys, string What)> rows = Hotkeys.Reference();
        const int RowHeight = 28, Top = 64;
        var box = new Form
        {
            Text = "XrShift keys", Font = Font, BackColor = Ui.Inspector, ForeColor = Ui.Ink, Icon = AppIcon.Value,
            FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false,
            StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(640, Top + rows.Count * RowHeight + 76),
            AutoScaleDimensions = new SizeF(96f, 96f), AutoScaleMode = AutoScaleMode.Dpi,
        };
        var list = new DoubleBufferedPanel { BackColor = Ui.Inspector };
        list.SetBounds(0, 0, 640, Top + rows.Count * RowHeight + 8);
        list.Paint += (_, e) =>
        {
            Graphics g = e.Graphics;
            Ui.Smooth(g);
            float d = list.DeviceDpi / 96f;
            using Font head = Ui.Text(10.5f, bold: true), key = Ui.Mono(10.5f, bold: true), body = Ui.Text(10f);
            using var ink = new SolidBrush(Ui.Ink);
            using var muted = new SolidBrush(Ui.Muted);
            using var accent = new SolidBrush(Ui.Accent);
            using var rule = new Pen(Ui.Line);
            g.DrawString("From any program, hold Ctrl+Alt and press the key.", head, ink, 24 * d, 18 * d);
            g.DrawString("If another program already owns one, it works while the glasses' window is in front.", body, muted, 24 * d, 40 * d);
            for (int i = 0; i < rows.Count; i++)
            {
                float y = (Top + i * RowHeight) * d;
                g.DrawLine(rule, 24 * d, y, (640 - 24) * d, y);
                g.DrawString(rows[i].Keys, key, accent, 24 * d, y + 5 * d);
                g.DrawString(rows[i].What, body, ink, new RectangleF(236 * d, y + 5 * d, (640 - 260) * d, RowHeight * d));
            }
        };
        box.Controls.Add(list);
        var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, FlatStyle = FlatStyle.Flat, BackColor = Ui.Raised, ForeColor = Ui.Ink };
        close.SetBounds(640 - 24 - 100, Top + rows.Count * RowHeight + 24, 100, 34);
        box.Controls.Add(close);
        box.CancelButton = close;
        return box;
    }

    sealed class DoubleBufferedPanel : System.Windows.Forms.Panel
    {
        public DoubleBufferedPanel() => DoubleBuffered = true;
    }

    // ---- building blocks for the pages ----------------------------------------------------------

    sealed record Row(string Main, string Detail, Color Colour, Action? Remove, string RemoveHint);

    /// <summary>Fills a list with one row per item: a name, a line under it, and a Remove button when it can be removed.</summary>
    void FillList(System.Windows.Forms.Panel host, List<Row> rows, string empty)
    {
        host.SuspendLayout();
        foreach (Control old in host.Controls.Cast<Control>().ToList()) old.Dispose();
        host.Controls.Clear();
        float dpi = Dpi;
        int width = (int)(host.Width / dpi) - 20, y = 0;
        var added = new List<Control>();
        if (rows.Count == 0)
        {
            var none = new Label { Text = empty, ForeColor = Ui.Faint, BackColor = Ui.Inspector, Font = Ui.Text(9.5f) };
            none.SetBounds(0, 4, width, 48);
            added.Add(none);
        }
        foreach (Row row in rows)
        {
            var main = new Label { Text = row.Main, ForeColor = row.Colour, BackColor = Ui.Inspector, Font = Ui.Text(10f, bold: true), AutoEllipsis = true };
            var detail = new Label { Text = row.Detail, ForeColor = Ui.Muted, BackColor = Ui.Inspector, Font = Ui.Text(9f), AutoEllipsis = true };
            int textWidth = row.Remove is null ? width : width - 96;
            main.SetBounds(0, y, textWidth, 20);
            detail.SetBounds(0, y + 20, textWidth, 18);
            _tips.SetToolTip(detail, row.Detail);
            added.Add(main);
            added.Add(detail);
            if (row.Remove is { } remove)
            {
                var pill = new PillButton();
                Place(pill, host, "Remove", width - 88, y + 4, 88, 30, PillButton.Looks.Ghost, row.RemoveHint);
                pill.Font = Ui.Text(9f);
                pill.Click += (_, _) => remove();
                host.Controls.Remove(pill);
                added.Add(pill);
            }
            y += 46;
        }
        host.Controls.AddRange(added.ToArray());
        // Controls added after the window exists are not scaled for us.
        if (IsHandleCreated && dpi != 1f) foreach (Control c in added) c.Scale(new SizeF(dpi, dpi));
        host.ResumeLayout();
    }

    static void AddLabel(Control parent, Label label, int x, int y, int width, int height, Color colour, float points)
    {
        label.SetBounds(x, y, width, height);
        label.ForeColor = colour;
        label.BackColor = Ui.Inspector;
        label.Font = Ui.Text(points);
        parent.Controls.Add(label);
    }

    bool Ask(string question, string title) =>
        MessageBox.Show(this, question, "XrShift - " + title, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    void Tell(string text) => MessageBox.Show(this, text, "XrShift", MessageBoxButtons.OK, MessageBoxIcon.Information);
}
