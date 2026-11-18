using System.Runtime.InteropServices;
using XrShift.Scene;

namespace XrShift.App;

/// <summary>
/// The control panel on the PC's own screen. Most of it is the room: the screens floating around the wearer,
/// seen from just behind the head, moved by dragging them. Beside it a slim inspector in three tabs: the
/// selected screen, the space as a whole (arrangement, zoom, pointer), and fine tuning. Every change applies at once.
/// </summary>
internal sealed class SettingsWindow : Form
{
    sealed record SourceChoice(string Source, string Text)
    {
        public override string ToString() => Text;
    }

    /// <summary>The sizes a virtual monitor can be given. The curve is a starting point for the wide ones.</summary>
    static readonly (string Name, int Width, int Height, float Curve)[] Shapes =
    [
        ("Standard 16:9", 1920, 1080, 0),
        ("Ultrawide 21:9", 2560, 1080, 40),
        ("Super ultrawide 32:9", 3840, 1080, 70),
    ];

    const int TopBar = 60, InspectorWidth = 380, ViewWidth = 960, WindowHeight = 820;

    readonly System.Windows.Forms.Panel _profileBar = new();
    readonly PillButton _vr = new();
    readonly PillButton _live = new(), _game = new(), _stationary = new(), _followMode = new();
    readonly SpatialView _space = new();
    readonly PillButton _add = new(), _pin = new(), _remove = new(), _previewButton = new();
    readonly PictureBox _preview = new();
    readonly WelcomeCard _welcome = new();
    readonly PillButton _guide = new();
    readonly Label _virtualNote = new(), _hint = new();
    readonly PillButton _tabScreen = new(), _tabStationary = new(), _tabSpace = new(), _tabTune = new();
    readonly System.Windows.Forms.Panel _pageScreen = new(), _pageStationary = new(), _pageSpace = new(), _pageTune = new();
    readonly PillButton _fit = new(), _drift = new();
    readonly Label _modeNow = new();
    bool _glassesStationary;

    // Screen page.
    readonly TextBox _name = new();
    readonly PillButton _source = new(), _visible = new(), _shape = new(), _rate = new();
    readonly SliderRow _side, _height, _size, _curve, _tilt, _depth;
    // Space page.
    readonly PillButton _pointerOff = new(), _pointerJump = new(), _pointerFollow = new(), _focus = new(), _toGaze = new();
    readonly SliderRow _zoom;
    readonly PillButton _dim = new(), _blank = new(), _place = new(), _recentre = new(), _break = new(), _hands = new();
    readonly Label _handState = new();
    // Tune page.
    readonly SliderRow _delay, _fov, _level, _roll;
    readonly PillButton _status = new(), _ruler = new(), _autostart = new(), _quit = new();

    readonly List<SourceChoice> _choices = [];
    readonly string _defaultHint;

    Layout _layout;
    ViewSettings _view;
    IReadOnlyList<DisplayInfo> _sources;
    IReadOnlyList<string> _profiles = [];
    int _activeProfile;
    string? _selected;
    bool _loading;

    public event Action<Layout>? LayoutChanged;
    public event Action<ViewSettings>? ViewChanged;
    public event Action<bool>? StatusPinnedChanged;
    public event Action? RecentreRequested;
    public event Action? PlaceRequested;
    public event Action? FitRequested;
    public event Action? MouseToGazeRequested;
    public event Action? QuitRequested;
    public event Action<int>? ProfileSelected;
    public event Action? ProfileAddRequested;
    public event Action<string>? ProfileRenameRequested;
    public event Action? ProfileRemoveRequested;
    public event Action<bool>? GameModeChanged;
    public event Action<bool>? VrModeChanged;
    /// <summary>The wearer asked for one more real (virtual) monitor.</summary>
    public event Action? MonitorAddRequested;
    /// <summary>The wearer asked to remove the screen with this id, which shows a virtual monitor that would have to go with it.</summary>
    public event Action<string>? MonitorRemoveRequested;
    /// <summary>The wearer hid or showed every screen at once.</summary>
    public event Action<bool>? BlankChanged;
    /// <summary>The wearer picked an open window to float as a screen of its own.</summary>
    public event Action<WindowInfo>? WindowPinRequested;

    /// <summary>True when a virtual monitor driver is there, so Add can make real monitors instead of stand-in pictures.</summary>
    public bool CanAddMonitors { get; set; }
    /// <summary>While the app runs, the window's close button quits the app (the window itself is closed by the app).</summary>
    public bool HideOnClose { get; set; } = true;

    public SettingsWindow(Layout layout, ViewSettings view, IReadOnlyList<DisplayInfo> sources, string recentreKey)
    {
        _layout = layout;
        _view = view;
        _sources = sources;
        _defaultHint = $"{recentreKey} recentre   ·   Ctrl+Alt+S this window\nCtrl+Alt+L pointer: off, jump, follow   ·   Ctrl+Alt+P pick up / set down\nCtrl+Alt+PgUp / PgDn zoom   ·   Ctrl+Alt+B hide all\nCtrl+Alt+G plain screen   ·   Ctrl+Alt+V VR   ·   Ctrl+Alt+Q or X quits";

        Text = "XrShift";
        Icon = AppIcon.Value;
        Font = Ui.Text(9.75f);
        BackColor = Ui.Back;
        ForeColor = Ui.Ink;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(ViewWidth + InspectorWidth, WindowHeight);
        DoubleBuffered = true;

        // ---- top bar: wordmark (painted), profiles, live numbers, plain screen -----------------
        _profileBar.SetBounds(150, 13, 580, 34);
        _profileBar.BackColor = Ui.Back;
        Controls.Add(_profileBar);

        Place(_vr, this, "VR", ViewWidth + InspectorWidth - 236, 13, 76, 34, PillButton.Looks.Solid,
            "Play SteamVR games on the glasses, seated: XrShift steps aside and the glasses become a VR headset that follows your head's turns. For depth, switch the glasses to their 3D mode first. Ctrl+Alt+V.");
        _vr.Click += (_, _) => VrModeChanged?.Invoke(!_vr.On);
        Place(_live, this, "starting", ViewWidth + InspectorWidth - 374, 13, 132, 34, PillButton.Looks.Ghost,
            "Frames per second in the glasses, and how long a frame takes from reading your head to reaching the glasses.");
        _live.Font = Ui.Mono(9.5f);
        _live.TabStop = false;
        _live.Cursor = Cursors.Default;
        Place(_game, this, "Plain screen", ViewWidth + InspectorWidth - 154, 13, 138, 34, PillButton.Looks.Solid,
            "Steps aside: the glasses become an ordinary extended screen (for a game, say). Virtual monitors are removed until you switch it off. Ctrl+Alt+G.");
        _game.Click += (_, _) => GameModeChanged?.Invoke(!_game.On);

        // The two ways the screens behave, side by side so it is always clear which one is on.
        // The glasses' own two modes, switched with the glasses' button. XrShift notices which one is on (lit) and
        // keeps separate screens and settings for each; clicking opens that mode's page.
        Place(_stationary, this, "Stationary", 742, 13, 112, 34, PillButton.Looks.Solid,
            "The glasses hold their picture still in the room with their own camera tracking: screens stay put even when you lean or walk. Switch with the glasses' own button. Its screens and hand gestures are on the Stationary page.");
        _stationary.Click += (_, _) => ShowPage(_pageStationary);
        Place(_followMode, this, "Follow", 858, 13, 96, 34, PillButton.Looks.Solid,
            "XrShift tracks your head and places screens all around you, with virtual monitors and look-to-point. Switch with the glasses' own button. Its settings are on the Follow page.");
        _followMode.Click += (_, _) => ShowPage(_pageSpace);
        Place(_guide, this, "?", 698, 13, 34, 34, PillButton.Looks.Ghost, "Shows the short guide again: the two modes and how to add screens.");
        _guide.Click += (_, _) => { _welcome.Visible = true; _welcome.BringToFront(); };

        // ---- the room ---------------------------------------------------------------------------
        _space.SetBounds(0, TopBar, ViewWidth, WindowHeight - TopBar);
        _space.Font = Font;
        _space.Picked += id => { if (_selected != id) { _selected = id; Refill(); } };
        _space.Moved += (id, yaw, pitch) =>
        {
            if (_selected != id) { _selected = id; Refill(); }
            Change(s => s with { YawDeg = yaw, PitchDeg = pitch });
            Quietly(() => { _side.Value = -yaw; _height.Value = pitch; });
        };
        _space.Resized += (id, scale) =>
        {
            if (_selected != id) { _selected = id; Refill(); }
            Change(s => s with { Scale = scale });
            Quietly(() => _size.Value = scale * 100f);
        };
        _space.HintWanted += h => _hint.Text = h;
        _space.MouseLeave += (_, _) => _hint.Text = _defaultHint;
        Controls.Add(_space);

        // The dock along the bottom of the room.
        int dockY = WindowHeight - TopBar - 50;
        Place(_add, _space, "+  Monitor", 16, dockY, 118, 36, PillButton.Looks.Accent, "");
        _add.MouseEnter += (_, _) => _hint.Text = CanAddMonitors
            ? "Adds a real extra monitor to Windows and floats it beside the others. Each one costs Windows some speed: pinned windows cost nothing. Hold Shift for a test picture instead."
            : "No virtual monitor driver found, so this adds a test picture.";
        _add.Click += (_, _) =>
        {
            if (CanAddMonitors && (ModifierKeys & Keys.Shift) == 0) MonitorAddRequested?.Invoke();
            else AddScreen();
        };
        Place(_pin, _space, "Pin window", 140, dockY, 118, 36, PillButton.Looks.Solid,
            "Floats one window (Teams, Chrome, your work desktop...) as a screen of its own. It costs Windows nothing, unlike a monitor.");
        _pin.Click += (_, _) => ShowWindowMenu();
        Place(_remove, _space, "Remove", 264, dockY, 96, 36, PillButton.Looks.Ghost,
            "Removes the selected screen. A virtual monitor goes from Windows too. A real monitor can only be hidden.");
        _remove.Click += (_, _) => RemoveSelected();
        Place(_previewButton, _space, "Glasses view", 372, dockY, 132, 36, PillButton.Looks.Solid,
            "Shows a live picture of what the glasses show, top right of the room. Handy for testing and for showing XrShift to someone.");
        _previewButton.Click += (_, _) =>
        {
            _previewButton.On = !_previewButton.On;
            _preview.Visible = _previewButton.On;
            if (!_previewButton.On) { _preview.Image?.Dispose(); _preview.Image = null; }
        };
        _preview.SetBounds(ViewWidth - 16 - 416, 52, 416, 234);
        _preview.SizeMode = PictureBoxSizeMode.Zoom;
        _preview.BackColor = Color.Black;
        _preview.BorderStyle = BorderStyle.FixedSingle;
        _preview.Visible = false;
        _space.Controls.Add(_preview);
        _welcome.SetBounds((ViewWidth - 600) / 2, 70, 600, 420);
        _welcome.Visible = WelcomeCard.FirstTime;
        _space.Controls.Add(_welcome);
        _welcome.BringToFront();
        _virtualNote.SetBounds(512, dockY + 9, 108, 20);
        _virtualNote.ForeColor = Ui.Faint;
        _virtualNote.BackColor = Ui.Dock;
        _virtualNote.Font = Ui.Mono(8.5f);
        _space.Controls.Add(_virtualNote);

        int px = ViewWidth - 16 - 4 * 82 + 6;
        foreach ((string text, Arrangement how, string hint) in new[]
        {
            ("Row", Arrangement.Row, "All shown screens side by side at eye level."),
            ("Curve", Arrangement.Curve, "Side by side and wrapped around you: one long curved band, every part the same distance away."),
            ("2 rows", Arrangement.TwoRows, "One row at eye level, the rest in a row above."),
            ("Focus", Arrangement.Focus, "The selected screen straight ahead at full size, the others smaller beside it."),
        })
        {
            var preset = new PillButton();
            Place(preset, _space, text, px, dockY, 76, 36, PillButton.Looks.Ghost, hint + " It rearranges this profile; to keep the old one, make a copy first with +.");
            preset.Click += (_, _) => Arrange(how);
            px += 82;
        }

        // ---- the inspector ------------------------------------------------------------------------
        var inspector = new System.Windows.Forms.Panel { BackColor = Ui.Inspector };
        inspector.SetBounds(ViewWidth, TopBar, InspectorWidth, WindowHeight - TopBar);
        Controls.Add(inspector);

        int tabX = 16;
        tabX = 8;
        foreach ((PillButton tab, string text, System.Windows.Forms.Panel page) in new[] { (_tabScreen, "Screen", _pageScreen), (_tabStationary, "Stationary", _pageStationary), (_tabSpace, "Follow", _pageSpace), (_tabTune, "Tune", _pageTune) })
        {
            int wide = text == "Stationary" ? 104 : 82;
            Place(tab, inspector, text, tabX, 10, wide, 36, PillButton.Looks.Tab, "");
            tab.Font = Ui.Text(10.5f, bold: true);
            tab.Click += (_, _) => ShowPage(page);
            tabX += wide + 4;
            page.SetBounds(0, 56, InspectorWidth, 560);
            page.BackColor = Ui.Inspector;
            inspector.Controls.Add(page);
        }

        _hint.SetBounds(20, WindowHeight - TopBar - 128, InspectorWidth - 36, 112);
        _hint.ForeColor = Ui.Muted;
        _hint.BackColor = Ui.Inspector;
        _hint.Font = Ui.Text(9f);
        _hint.Text = _defaultHint;
        inspector.Controls.Add(_hint);
        inspector.Paint += (_, e) =>
        {
            using var rule = new Pen(Ui.Line);
            e.Graphics.DrawLine(rule, 0, 0, 0, inspector.Height);
            float d = DeviceDpi / 96f;
            e.Graphics.DrawLine(rule, 20 * d, _hint.Top - 12 * d, inspector.Width - 20 * d, _hint.Top - 12 * d);
        };

        // Screen page.
        _name.SetBounds(20, 6, InspectorWidth - 40, 34);
        _name.BackColor = Ui.Inspector;
        _name.ForeColor = Ui.Ink;
        _name.BorderStyle = BorderStyle.None;
        _name.Font = Ui.Display(17f);
        _name.TextChanged += (_, _) => Change(s => s with { Name = _name.Text });
        _pageScreen.Controls.Add(_name);
        Place(_source, _pageScreen, "", 20, 46, InspectorWidth - 40, 32, PillButton.Looks.Ghost,
            "What this screen shows: a monitor, a pinned window or a test picture. Click to change it.");
        _source.Click += (_, _) => ShowSourceMenu();
        int y = 92;
        _side = Slider(_pageScreen, "Left / right", -180, 180, 0.5f, 0, 1, "°", true,
            "Where the screen sits around you. 0 is straight ahead after recentring. Or drag it in the room.", ref y, v => Change(s => s with { YawDeg = -v }));
        _height = Slider(_pageScreen, "Down / up", -45, 45, 0.5f, 0, 1, "°", true,
            "How far above or below eye level the screen sits.", ref y, v => Change(s => s with { PitchDeg = v }));
        _size = Slider(_pageScreen, "Size", 30, 250, 1, 85, 0, " %", false,
            "100 % shows the picture pixel for pixel, the sharpest the glasses can do. Smaller fits more in view. Or scroll over it in the room.", ref y, v => Change(s => s with { Scale = v / 100f }));
        _curve = Slider(_pageScreen, "Curve", 0, 120, 1, 0, 0, "°", false,
            "Wraps the screen around you. 0 is flat.", ref y, v => Change(s => s with { CurveDeg = v }));
        _tilt = Slider(_pageScreen, "Tilt", -30, 30, 0.5f, 0, 1, "°", true,
            "Turns the screen clockwise or anticlockwise.", ref y, v => Change(s => s with { TiltDeg = v }));
        _depth = Slider(_pageScreen, "Distance", 1, 6, 0.1f, 2, 1, " m", false,
            "Only decides which screen covers which where they overlap: nearer ones are in front.", ref y, v => Change(s => s with { Distance = v }));
        y += 10;
        Place(_visible, _pageScreen, "Shown", 20, y, 96, 32, PillButton.Looks.Solid, "Switch a screen off without losing its place.");
        _visible.Click += (_, _) => Change(s => s with { Visible = !s.Visible });
        Place(_shape, _pageScreen, "Standard 16:9", 122, y, 150, 32, PillButton.Looks.Ghost,
            "For a virtual monitor: its shape. An ultrawide is one long desktop for two or three windows side by side, and costs Windows less than separate monitors.");
        _shape.Click += (_, _) => ShowShapeMenu();
        Place(_rate, _pageScreen, "60 Hz", 278, y, 82, 32, PillButton.Looks.Solid,
            "For a virtual monitor: 60 Hz for reading, easy on the graphics card. 120 Hz for a screen you play on.");
        _rate.Click += (_, _) => Change(s => s with { RefreshHz = s.RefreshHz >= 100 ? 0 : 120 });

        // Space page.
        y = 6;
        Head(_pageSpace, "Pointer", ref y);
        Place(_pointerOff, _pageSpace, "Off", 20, y, 106, 32, PillButton.Looks.Solid, "The mouse pointer moves only by hand.");
        _pointerOff.Click += (_, _) => ChangeView(s => s with { LookPointer = false, LookFollow = false });
        Place(_pointerJump, _pageSpace, "Jump", 132, y, 106, 32, PillButton.Looks.Solid,
            "Look at another screen for a moment and the pointer jumps to the spot you look at. Moving the mouse by hand always wins.");
        _pointerJump.Click += (_, _) => ChangeView(s => s with { LookPointer = true, LookFollow = false });
        Place(_pointerFollow, _pageSpace, "Follow", 244, y, 116, 32, PillButton.Looks.Solid,
            "The pointer rides the middle of your view: aim with your head, click with the mouse. Nudge it by hand to fine-tune; it waits until your head moves on.");
        _pointerFollow.Click += (_, _) => ChangeView(s => s with { LookPointer = true, LookFollow = true });
        y += 40;
        Place(_focus, _pageSpace, "Keyboard follows", 20, y, 162, 32, PillButton.Looks.Solid,
            "The window the pointer moves to also gets the keyboard, so you can look from a game to your work desktop and type. Some games pause when they lose it.");
        _focus.Click += (_, _) => ChangeView(s => s with { LookFocus = !s.LookFocus });
        Place(_toGaze, _pageSpace, "Mouse here now", 188, y, 172, 32, PillButton.Looks.Ghost, "Puts the pointer where you are looking, once. Ctrl+Alt+M.");
        _toGaze.Click += (_, _) => MouseToGazeRequested?.Invoke();
        y += 52;
        Head(_pageSpace, "All screens", ref y);
        _zoom = Slider(_pageSpace, "Zoom", ViewSettings.MinZoom * 100, ViewSettings.MaxZoom * 100, 5, 100, 0, " %", false,
            "Every screen bigger or smaller at once, keeping the arrangement. Ctrl+Alt+PgUp / PgDn from anywhere.", ref y,
            v => ChangeView(s => s with { Zoom = v / 100f }));
        y += 6;
        Place(_dim, _pageSpace, "Dim others", 20, y, 106, 32, PillButton.Looks.Solid,
            "Screens you are not looking at go darker, so the one you read stands out and the rest are easier on the eyes.");
        _dim.Click += (_, _) => ChangeView(s => s with { Dim = !s.Dim });
        Place(_blank, _pageSpace, "Hide all", 132, y, 106, 32, PillButton.Looks.Solid, "Hides every screen so you see the room. Ctrl+Alt+B.");
        _blank.Click += (_, _) => { _blank.On = !_blank.On; BlankChanged?.Invoke(_blank.On); };
        Place(_place, _pageSpace, "Pick up", 244, y, 116, 32, PillButton.Looks.Ghost,
            "Picks up the screen you are looking at: turn your head to carry it, Ctrl+Alt+PgUp / PgDn to resize it, then Ctrl+Alt+P to set it down. Ctrl+Alt+P to pick up too.");
        _place.Click += (_, _) => PlaceRequested?.Invoke();
        y += 40;
        Place(_recentre, _pageSpace, "Recentre", 20, y, 106, 32, PillButton.Looks.Ghost, $"Makes the way you face now straight ahead. {recentreKey}.");
        _recentre.Click += (_, _) => RecentreRequested?.Invoke();
        Place(_drift, _pageSpace, "Drift after me", 132, y, 140, 32, PillButton.Looks.Solid,
            "Turn far away and the screens drift after you, then settle in front again. Off, they stay where you put them.");
        _drift.Click += (_, _) => ChangeView(s => s with { Follow = !s.Follow });

        // Stationary page.
        y = 6;
        Head(_pageStationary, "Stationary mode", ref y);
        var about = new Label
        {
            Text = "Switch the glasses to stationary with their own button. They then hold their picture still in the room " +
                   "using their camera (6DoF), and XrShift places your screens inside that picture, so all of them stay put " +
                   "even when you lean or walk. Screens and profiles here are separate from Follow.",
            ForeColor = Ui.Muted, BackColor = Ui.Inspector, Font = Ui.Text(9f),
        };
        about.SetBounds(20, y, InspectorWidth - 40, 74);
        _pageStationary.Controls.Add(about);
        y += 78;
        _modeNow.SetBounds(20, y, InspectorWidth - 40, 22);
        _modeNow.ForeColor = Ui.Accent;
        _modeNow.BackColor = Ui.Inspector;
        _modeNow.Font = Ui.Mono(9f);
        _pageStationary.Controls.Add(_modeNow);
        y += 30;
        Place(_fit, _pageStationary, "Fit screens in view", 20, y, 190, 32, PillButton.Looks.Accent,
            "Shrinks and centres the screens so all of them are inside what the glasses hold still. Arrange them with the presets first if you like.");
        _fit.Click += (_, _) => FitRequested?.Invoke();
        y += 52;
        Head(_pageStationary, "Hand gestures", ref y);
        Place(_hands, _pageStationary, "Hand gestures", 20, y, 150, 32, PillButton.Looks.Solid,
            "Your hand steers the pointer like a touchpad: move your palm to move it, pinch thumb and index finger to click, pinch and move to drag. Works while the glasses are in stationary mode, because only then is their camera on. Ctrl+Alt+K.");
        _hands.Click += (_, _) =>
        {
            if (!HandInput.Installed) { OfferHandSetup(); return; }
            ChangeView(s => s with { Hands = !s.Hands });
        };
        _handState.SetBounds(178, y + 7, InspectorWidth - 198, 22);
        _handState.ForeColor = Ui.Muted;
        _handState.BackColor = Ui.Inspector;
        _handState.Font = Ui.Mono(9f);
        _pageStationary.Controls.Add(_handState);
        y += 40;
        var howTo = new Label
        {
            Text = "Hold your hand up in front of the glasses, palm out.\n· move the palm: move the pointer\n· pinch: click\n· pinch and move: drag\nGood light helps; the camera sees in grey.",
            ForeColor = Ui.Muted, BackColor = Ui.Inspector, Font = Ui.Text(9f),
        };
        howTo.SetBounds(20, y, InspectorWidth - 40, 96);
        _pageStationary.Controls.Add(howTo);

        // Tune page.
        y = 6;
        Head(_pageTune, "Steadiness", ref y);
        _delay = Slider(_pageTune, "Glasses delay", ViewSettings.MinDelayMs, ViewSettings.MaxDelayMs, 0.5f, 0, 1, " ms", true,
            "Screens trail behind when you turn: raise it. They overshoot or jitter as you stop: lower it.", ref y,
            v => ChangeView(s => s with { DelayMs = v }));
        _fov = Slider(_pageTune, "View angle", ViewSettings.MinFovDeg, ViewSettings.MaxFovDeg, 0.1f, DisplayOptics.OnePro.HorizontalFovDeg, 1, "°", false,
            "Screens follow your head a little as you turn: lower it. They move against your head: raise it.", ref y,
            v => ChangeView(s => s with { FovDeg = v }));
        _level = Slider(_pageTune, "Level", -ViewSettings.MaxLevelDeg, ViewSettings.MaxLevelDeg, 0.25f, 0, 2, "°", true,
            "A screen off to the side tilts or climbs as you turn away from it: adjust until it stops.", ref y,
            v => ChangeView(s => s with { LevelDeg = v }));
        _roll = Slider(_pageTune, "Lean", -ViewSettings.MaxRollDeg, ViewSettings.MaxRollDeg, 0.25f, 0, 2, "°", true,
            "Everything looks rotated: straighten it here.", ref y, v => ChangeView(s => s with { RollDeg = v }));
        y += 10;
        Place(_status, _pageTune, "Status line", 20, y, 106, 32, PillButton.Looks.Solid, "Keeps the line of numbers showing in the glasses. Ctrl+Alt+H.");
        _status.Click += (_, _) => { _status.On = !_status.On; StatusPinnedChanged?.Invoke(_status.On); };
        Place(_ruler, _pageTune, "Horizon ruler", 132, y, 122, 32, PillButton.Looks.Solid, "A ruler all the way round at eye level, marked in degrees.");
        _ruler.Click += (_, _) => ChangeView(s => s with { Ruler = !s.Ruler });
        y += 52;
        Head(_pageTune, "Comfort", ref y);
        Place(_break, _pageTune, "Break reminder: off", 20, y, 218, 32, PillButton.Looks.Solid,
            "A line in the glasses every so often reminding you to look into the distance for 20 seconds. Click to change how often.");
        _break.Click += (_, _) => ChangeView(s => s with { BreakMinutes = s.BreakMinutes switch { 0 => 20, 20 => 30, 30 => 45, 45 => 60, _ => 0 } });
        y += 52;
        Head(_pageTune, "App", ref y);
        Place(_autostart, _pageTune, "Start with Windows", 20, y, 172, 32, PillButton.Looks.Solid,
            "Starts XrShift quietly when you sign in. It waits in the tray, brings your screens up when the glasses are plugged in, and clears away any virtual monitors left from before.");
        _autostart.On = Autostart.Enabled;
        _autostart.Click += (_, _) =>
        {
            if (Autostart.Set(!_autostart.On)) _autostart.On = !_autostart.On;
        };
        Place(_quit, _pageTune, "Quit XrShift", 198, y, 162, 32, PillButton.Looks.Ghost,
            "Closes XrShift. Virtual monitors are removed, so windows on them come back to your real screen. Ctrl+Alt+Q.");
        _quit.Click += (_, _) => QuitRequested?.Invoke();

        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;

        ShowPage(_pageScreen);
        Refill();
        LoadView();
    }

    DisplayOptics Optics => DisplayOptics.OnePro with { HorizontalFovDeg = _view.FovDeg };

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        Ui.Smooth(g);
        float d = DeviceDpi / 96f;
        // The wordmark: the name, and a small mark of a screen bending around a point.
        using (var mark = new Pen(Ui.Accent, 2f * d))
        {
            g.DrawArc(mark, 22 * d, 20 * d, 24 * d, 24 * d, 200, 140);
            using var dot = new SolidBrush(Ui.Accent);
            g.FillEllipse(dot, 31.5f * d, 34 * d, 5 * d, 5 * d);
        }
        using (Font word = Ui.Display(15f))
        using (var ink = new SolidBrush(Ui.Ink))
            g.DrawString("XrShift", word, ink, 54 * d, 17 * d);
        using var rule = new Pen(Ui.Line);
        g.DrawLine(rule, 0, TopBar * d - 1, Width, TopBar * d - 1);
    }

    void ShowPage(System.Windows.Forms.Panel page)
    {
        foreach ((PillButton tab, System.Windows.Forms.Panel p) in new[] { (_tabScreen, _pageScreen), (_tabStationary, _pageStationary), (_tabSpace, _pageSpace), (_tabTune, _pageTune) })
        {
            tab.On = p == page;
            p.Visible = p == page;
        }
    }

    // ---- called by the app -------------------------------------------------------------------

    /// <summary>The layout or the set of monitors changed outside this window.</summary>
    public void SetLayout(Layout layout, IReadOnlyList<DisplayInfo> sources)
    {
        _layout = layout;
        _sources = sources;
        Refill();
    }

    /// <summary>A hotkey changed a setting.</summary>
    public void SetView(ViewSettings view)
    {
        _view = view;
        LoadView();
        ShowScreens();
    }

    public void SetStatusPinned(bool pinned) => _status.On = pinned;

    public void SetGameMode(bool on) => _game.On = on;

    public void SetVrMode(bool on) => _vr.On = on;

    public void SetBlank(bool on) => _blank.On = on;

    /// <summary>What hand tracking is doing, beside its switch.</summary>
    // Hand tracking needs Google's MediaPipe in a folder of its own; it is set up only when asked for, here.
    void OfferHandSetup()
    {
        string script = Path.Combine(AppContext.BaseDirectory, "hands", "setup.ps1");
        if (!File.Exists(script)) { SetHandStatus("setup file missing: reinstall XrShift"); return; }
        if (MessageBox.Show(this,
                "Hand gestures need Google's MediaPipe hand tracker (open source, about 390 MB), kept in a folder of its own in your user profile.\n\nDownload and set it up now? A window shows the progress; it takes a minute or two.",
                "XrShift - hand gestures", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = true };
            foreach (string a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command",
                         $"& '{script}'; Write-Host ''; Write-Host 'You can close this window and switch Hand gestures on.'; Read-Host 'Press Enter to close'" })
                start.ArgumentList.Add(a);
            System.Diagnostics.Process.Start(start);
            SetHandStatus("setting up...");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            SetHandStatus("could not start the setup");
        }
    }

    /// <summary>Which mode the glasses are in, as XrShift sees it; the page for it comes to the front.</summary>
    public void SetGlassesMode(bool stationary)
    {
        _glassesStationary = stationary;
        _space.HeldFrame = stationary;
        _stationary.Text = stationary ? "●  Stationary" : "Stationary";
        _followMode.Text = stationary ? "Follow" : "●  Follow";
        _modeNow.Text = stationary ? "● the glasses are in stationary mode now" : "○ the glasses are in follow mode now";
        ShowViewToggles();
        if (_pageScreen.Visible) return;
        ShowPage(stationary ? _pageStationary : _pageSpace);
    }

    /// <summary>Brings a page to the front by name: screen, stationary, follow or tune.</summary>
    public void ShowPageNamed(string name) => ShowPage(name.ToLowerInvariant() switch
    {
        "stationary" => _pageStationary,
        "follow" => _pageSpace,
        "tune" => _pageTune,
        _ => _pageScreen,
    });

    /// <summary>The Glasses view button is on.</summary>
    public bool PreviewOn => _previewButton.On;

    /// <summary>A new picture of what the glasses show; this window keeps it and lets go of the last one.</summary>
    public void SetPreview(Bitmap picture)
    {
        if (!_previewButton.On) { picture.Dispose(); return; }
        Image? old = _preview.Image;
        _preview.Image = picture;
        old?.Dispose();
    }

    public void SetHandStatus(string text)
    {
        if (_handState.Text != text) _handState.Text = text;
    }

    /// <summary>The line in the dock, e.g. how many virtual monitors there are.</summary>
    public void SetVirtualNote(string text) => _virtualNote.Text = text;

    /// <summary>The live numbers at the top right.</summary>
    public void SetLive(string text, bool healthy)
    {
        if (_live.Text == text) return;
        _live.Text = text;
        _live.ForeColor = healthy ? Ui.Ink : Ui.Warn;
    }

    /// <summary>Selects a screen, e.g. one the app has just added.</summary>
    public void Select(string id)
    {
        if (_layout.Find(id) is null) return;
        _selected = id;
        Refill();
    }

    public void SetProfiles(IReadOnlyList<string> names, int active)
    {
        _profiles = names;
        _activeProfile = names.Count == 0 ? -1 : Math.Clamp(active, 0, names.Count - 1);
        _profileBar.SuspendLayout();
        foreach (Control old in _profileBar.Controls.Cast<Control>().ToList()) old.Dispose();
        _profileBar.Controls.Clear();

        float dpi = DeviceDpi / 96f;
        int x = 0;
        for (int i = 0; i < names.Count; i++)
        {
            int index = i;
            int width = Math.Clamp((int)(TextRenderer.MeasureText(names[i], Font).Width / dpi) + 28, 60, 160);
            var tab = new PillButton { On = i == _activeProfile };
            Place(tab, _profileBar, names[i], x, 0, width, 34, PillButton.Looks.Tab,
                i < 4 ? $"Profile {names[i]}: its own set of screens. Ctrl+Alt+{i + 1} switches to it from anywhere." : $"Profile {names[i]}.");
            tab.Font = Ui.Text(10.5f, bold: true);
            tab.Click += (_, _) => ProfileSelected?.Invoke(index);
            x += width + 2;
        }
        x += 10;
        AddBarButton("+", ref x, 34, "A new profile, starting as a copy of this one.", () => ProfileAddRequested?.Invoke(), true);
        AddBarButton("Rename", ref x, 74, "Give this profile another name.", () =>
        {
            string current = _activeProfile >= 0 && _activeProfile < _profiles.Count ? _profiles[_activeProfile] : "";
            if (AskName(current) is { Length: > 0 } name) ProfileRenameRequested?.Invoke(name);
        }, true);
        AddBarButton("Delete", ref x, 68, "Removes this profile. The last one cannot be removed.", () => ProfileRemoveRequested?.Invoke(), names.Count > 1);
        _profileBar.ResumeLayout();
        if (IsHandleCreated && dpi != 1f)
        {
            // Controls added after the window exists are not scaled for us.
            foreach (Control c in _profileBar.Controls) c.Scale(new SizeF(dpi, dpi));
        }
    }

    void AddBarButton(string text, ref int x, int width, string hint, Action click, bool enabled)
    {
        var pill = new PillButton { Enabled = enabled };
        Place(pill, _profileBar, text, x, 3, width, 28, PillButton.Looks.Ghost, hint);
        pill.Font = Ui.Text(9f);
        pill.Click += (_, _) => click();
        x += width + 4;
    }

    // ---- editing -----------------------------------------------------------------------------

    void Quietly(Action set)
    {
        _loading = true;
        try { set(); }
        finally { _loading = false; }
    }

    void Change(Func<ScreenSpec, ScreenSpec> edit)
    {
        if (_loading || _selected is null || _layout.Find(_selected) is not { } spec) return;
        ScreenSpec changed = XrShift.Scene.Layout.Clamp(edit(spec));
        if (changed == spec) return;
        _layout = _layout.With(changed);
        LayoutChanged?.Invoke(_layout);
        ShowScreens();
        ShowToggles(changed);
        if (changed.Source != spec.Source) UpdateButtons();
    }

    void ChangeView(Func<ViewSettings, ViewSettings> edit)
    {
        if (_loading) return;
        ViewSettings changed = edit(_view);
        if (changed == _view) return;
        _view = changed;
        ShowViewToggles();
        ViewChanged?.Invoke(_view);
        ShowScreens(); // the view angle changes how big screens are
    }

    /// <summary>The size in pixels of what a screen shows, as far as this window knows.</summary>
    (int Width, int Height) PictureSize(ScreenSpec spec)
    {
        if (spec.DisplayName is { } id && _sources.FirstOrDefault(d => Same(d.Id, id)) is { } display)
            return (display.Bounds.Width, display.Bounds.Height);
        return spec.Window is not null ? (1280, 720) : (1920, 1080);
    }

    void Arrange(Arrangement how)
    {
        Layout arranged = Presets.Arrange(_layout, how, PictureSize, Optics, _selected);
        if (arranged == _layout) return;
        _layout = arranged;
        LayoutChanged?.Invoke(_layout);
        Refill();
    }

    void AddScreen()
    {
        ScreenSpec spec = _layout.PlaceNew($"Screen {_layout.Screens.Count + 1}", ScreenSpec.TestSource, 1920, 1080, Optics);
        _layout = _layout.Add(spec);
        _selected = spec.Id;
        LayoutChanged?.Invoke(_layout);
        Refill();
    }

    void RemoveSelected()
    {
        if (_selected is null) return;
        if (!CanRemove(_selected))
        {
            // The only screen of a connected monitor: possible only by removing the monitor itself, if it is a virtual one.
            if (ShowsVirtual(_selected)) MonitorRemoveRequested?.Invoke(_selected);
            return;
        }
        _layout = _layout.Remove(_selected);
        _selected = null;
        LayoutChanged?.Invoke(_layout);
        Refill();
    }

    /// <summary>A connected monitor always keeps one screen (hide it instead), or it would have no way back.</summary>
    bool CanRemove(string id)
    {
        if (_layout.Find(id) is not { } spec) return false;
        if (spec.DisplayName is not { } display || !_sources.Any(d => Same(d.Id, display))) return true;
        return _layout.Screens.Count(s => Same(s.DisplayName, display)) > 1;
    }

    bool ShowsVirtual(string id) =>
        CanAddMonitors && VirtualMonitors.IsVirtualId(_layout.Find(id)?.DisplayName);

    void ShowSourceMenu()
    {
        if (_selected is null || _layout.Find(_selected) is not { } spec) return;
        ContextMenuStrip menu = NewMenu();
        foreach (SourceChoice choice in _choices)
        {
            var item = new ToolStripMenuItem(choice.Text) { ForeColor = Same(choice.Source, spec.Source) ? Ui.Accent : Ui.Ink };
            item.Click += (_, _) =>
            {
                Change(s => s with { Source = choice.Source });
                LoadSelected();
            };
            menu.Items.Add(item);
        }
        menu.Show(_source, new Point(0, _source.Height));
    }

    void ShowShapeMenu()
    {
        if (_selected is null || _layout.Find(_selected) is not { } spec) return;
        ContextMenuStrip menu = NewMenu();
        foreach ((string name, int width, int height, float curve) in Shapes)
        {
            bool current = spec.PixelWidth == width && spec.PixelHeight == height;
            var item = new ToolStripMenuItem($"{name}   {width} x {height}") { ForeColor = current ? Ui.Accent : Ui.Ink };
            item.Click += (_, _) =>
            {
                // A wide screen reads best wrapped around you; a flat one that long has its ends far off to the sides.
                Change(s => s with { PixelWidth = width, PixelHeight = height, CurveDeg = curve > 0 && s.CurveDeg < 1 ? curve : s.CurveDeg });
                LoadSelected();
            };
            menu.Items.Add(item);
        }
        menu.Show(_shape, new Point(0, _shape.Height));
    }

    void ShowWindowMenu()
    {
        ContextMenuStrip menu = NewMenu();
        List<WindowInfo> windows = WindowFinder.List().OrderBy(w => w.Label, StringComparer.OrdinalIgnoreCase).ToList();
        if (windows.Count == 0) menu.Items.Add(new ToolStripMenuItem("No windows found") { Enabled = false });
        foreach (WindowInfo window in windows)
        {
            bool minimised = WindowFinder.Minimized(window.Handle);
            var item = new ToolStripMenuItem(window.Label + (minimised ? "   (minimised: it will be opened)" : "")) { ForeColor = Ui.Ink };
            item.Click += (_, _) => WindowPinRequested?.Invoke(window);
            menu.Items.Add(item);
        }
        menu.Show(_pin, new Point(0, _pin.Height));
    }

    ContextMenuStrip NewMenu() => new() { BackColor = Ui.Raised, ForeColor = Ui.Ink, ShowImageMargin = false, Font = Font, Renderer = new MenuLook() };

    /// <summary>Menus in the app's own colours instead of Windows' light grey.</summary>
    sealed class MenuLook : ToolStripProfessionalRenderer
    {
        public MenuLook() : base(new Colours()) { RoundedEdges = false; }

        sealed class Colours : ProfessionalColorTable
        {
            public override Color MenuItemSelected => Ui.AccentDeep;
            public override Color MenuItemBorder => Ui.Accent;
            public override Color MenuBorder => Ui.Line;
            public override Color ToolStripDropDownBackground => Ui.Raised;
            public override Color ImageMarginGradientBegin => Ui.Raised;
            public override Color ImageMarginGradientMiddle => Ui.Raised;
            public override Color ImageMarginGradientEnd => Ui.Raised;
        }
    }

    // ---- filling the controls ----------------------------------------------------------------

    void Refill()
    {
        if (_selected is null || _layout.Find(_selected) is null) _selected = _layout.Screens.FirstOrDefault()?.Id;
        ShowScreens();
        LoadSelected();
    }

    /// <summary>Redraws the room from the layout.</summary>
    void ShowScreens()
    {
        var screens = new List<SpatialScreen>();
        foreach (ScreenSpec spec in _layout.Screens)
        {
            (int width, int height) = PictureSize(spec);
            bool missing = spec.DisplayName is { } id && !_sources.Any(d => Same(d.Id, id));
            screens.Add(new SpatialScreen(spec, width, height, SourceLabel(spec), missing));
        }
        _space.Show(screens, _selected, Optics);
    }

    void LoadSelected()
    {
        ScreenSpec? spec = _selected is null ? null : _layout.Find(_selected);
        bool has = spec is not null;
        foreach (Control c in new Control[] { _name, _source, _side, _height, _size, _curve, _tilt, _depth, _visible })
            c.Enabled = has;

        _choices.Clear();
        foreach (DisplayInfo d in _sources)
            _choices.Add(new SourceChoice(ScreenSpec.DisplaySource(d.Id), d.Label));
        _choices.Add(new SourceChoice(ScreenSpec.TestSource, "Test picture"));

        Quietly(() =>
        {
            _source.Text = "";
            if (spec is null)
            {
                _name.Text = "";
                return;
            }
            _name.Text = spec.Name;
            SourceChoice? current = _choices.FirstOrDefault(c => Same(c.Source, spec.Source));
            if (current is null)
            {
                current = new SourceChoice(spec.Source, spec.Window is { } window
                    ? $"Window: {(window.Title.Length > 0 ? window.Title : window.Program)}"
                    : "A monitor that is not connected");
                _choices.Add(current);
            }
            _source.Text = current.Text;
            _side.Value = -spec.YawDeg;
            _height.Value = spec.PitchDeg;
            _size.Value = spec.Scale * 100f;
            _curve.Value = spec.CurveDeg;
            _tilt.Value = spec.TiltDeg;
            _depth.Value = spec.Distance;
        });
        ShowToggles(spec);
        UpdateButtons();
    }

    /// <summary>Shown, shape and refresh of the selected screen. Shape and refresh only mean something for a virtual monitor.</summary>
    void ShowToggles(ScreenSpec? spec)
    {
        _visible.On = spec?.Visible ?? false;
        _visible.Text = spec is { Visible: false } ? "Hidden" : "Shown";
        bool virtualMonitor = spec is not null && CanAddMonitors && VirtualMonitors.IsVirtualId(spec.DisplayName);
        _shape.Enabled = _rate.Enabled = virtualMonitor;
        (string name, _, _, _) = Shapes.FirstOrDefault(s => spec is not null && s.Width == spec.PixelWidth && s.Height == spec.PixelHeight, Shapes[0]);
        _shape.Text = virtualMonitor ? name : "Shape";
        _rate.Text = virtualMonitor && spec!.RefreshHz >= 100 ? "120 Hz" : "60 Hz";
        _rate.On = virtualMonitor && spec!.RefreshHz >= 100;
    }

    void UpdateButtons() => _remove.Enabled = _selected is not null && (CanRemove(_selected) || ShowsVirtual(_selected));

    void LoadView()
    {
        Quietly(() =>
        {
            _delay.Value = _view.DelayMs;
            _fov.Value = _view.FovDeg;
            _level.Value = _view.LevelDeg;
            _roll.Value = _view.RollDeg;
            _zoom.Value = _view.Zoom * 100f;
        });
        ShowViewToggles();
    }

    void ShowViewToggles()
    {
        _ruler.On = _view.Ruler;
        _pointerOff.On = !_view.LookPointer;
        _pointerJump.On = _view.LookPointer && !_view.LookFollow;
        _pointerFollow.On = _view.LookPointer && _view.LookFollow;
        _focus.On = _view.LookFocus;
        _focus.Enabled = _view.LookPointer;
        _dim.On = _view.Dim;
        _stationary.On = _glassesStationary;
        _followMode.On = !_glassesStationary;
        _drift.On = _view.Follow;
        _hands.On = _view.Hands && HandInput.Installed;
        _break.On = _view.BreakMinutes > 0;
        _break.Text = _view.BreakMinutes > 0 ? $"Break reminder: every {_view.BreakMinutes} min" : "Break reminder: off";
    }

    string SourceLabel(ScreenSpec spec)
    {
        if (spec.Window is { } window) return "window · " + WindowFinder.FriendlyName(window.Program);
        if (spec.DisplayName is not { } id) return "test picture";
        DisplayInfo? d = _sources.FirstOrDefault(x => Same(x.Id, id));
        if (d is null) return "not connected";
        string size = spec.PixelWidth > 0 ? $"{spec.PixelWidth}×{spec.PixelHeight}" : $"{d.Bounds.Width}×{d.Bounds.Height}";
        return VirtualMonitors.IsVirtual(d) ? $"virtual · {size}" : $"monitor · {size}";
    }

    // ---- building blocks -------------------------------------------------------------------------

    void Place(PillButton pill, Control parent, string text, int x, int y, int width, int height, PillButton.Looks look, string hint)
    {
        pill.Text = text;
        if (pill.Font == Control.DefaultFont) pill.Font = Font;
        pill.SetBounds(x, y, width, height);
        pill.Look = look;
        pill.Hint = hint;
        pill.HintWanted += h => { if (h.Length > 0) _hint.Text = h; };
        pill.MouseLeave += (_, _) => _hint.Text = _defaultHint;
        parent.Controls.Add(pill);
    }

    void Head(Control page, string text, ref int y)
    {
        var heading = new Heading { Text = text };
        heading.SetBounds(20, y, InspectorWidth - 40, 22);
        page.Controls.Add(heading);
        y += 30;
    }

    SliderRow Slider(Control page, string caption, float min, float max, float step, float reset, int decimals, string unit, bool fromZero,
        string hint, ref int y, Action<float> changed)
    {
        var row = new SliderRow
        {
            Caption = caption, Min = min, Max = max, Step = step, ResetTo = reset, Decimals = decimals, Unit = unit,
            FromZero = fromZero, Hint = hint, Font = Font,
        };
        row.SetBounds(20, y, InspectorWidth - 40, 34);
        row.Changed += changed;
        row.HintWanted += text => _hint.Text = text;
        row.MouseLeave += (_, _) => _hint.Text = _defaultHint;
        page.Controls.Add(row);
        y += 36;
        return row;
    }

    /// <summary>A small box asking for a profile name. Null if cancelled.</summary>
    string? AskName(string current)
    {
        using var box = new Form
        {
            Text = "Profile name", Font = Font, BackColor = Ui.Inspector, ForeColor = Ui.Ink, Icon = AppIcon.Value,
            FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
            StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(340, 110),
            AutoScaleDimensions = new SizeF(96f, 96f), AutoScaleMode = AutoScaleMode.Dpi,
        };
        var name = new TextBox { Text = current, BackColor = Ui.Raised, ForeColor = Ui.Ink, BorderStyle = BorderStyle.FixedSingle };
        name.SetBounds(20, 20, 300, 28);
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, FlatStyle = FlatStyle.Flat, BackColor = Ui.Raised, ForeColor = Ui.Ink };
        ok.SetBounds(140, 62, 84, 32);
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, FlatStyle = FlatStyle.Flat, BackColor = Ui.Raised, ForeColor = Ui.Ink };
        cancel.SetBounds(236, 62, 84, 32);
        box.Controls.AddRange([name, ok, cancel]);
        box.AcceptButton = ok;
        box.CancelButton = cancel;
        return box.ShowDialog(this) == DialogResult.OK ? name.Text.Trim() : null;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Windows 11: a dark title bar in the app's own colour, instead of a white strip on a dark window.
        int dark = 1, caption = ColorRef(Ui.Back), border = ColorRef(Ui.Line), text = ColorRef(Ui.Ink);
        DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
        DwmSetWindowAttribute(Handle, 35, ref caption, sizeof(int));
        DwmSetWindowAttribute(Handle, 34, ref border, sizeof(int));
        DwmSetWindowAttribute(Handle, 36, ref text, sizeof(int));
    }

    static int ColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Closing this window quits XrShift completely, as people expect. (Minimise it to keep XrShift running.)
        if (HideOnClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            QuitRequested?.Invoke();
        }
        base.OnFormClosing(e);
    }

    static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
