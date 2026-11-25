using System.Runtime.InteropServices;
using XrShift.Scene;

namespace XrShift.App;

/// <summary>
/// XrShift's window on the PC, in pages chosen from a side menu:
/// Home: the four ways to use the glasses (what each gives and does not), how things are now, quick actions.
/// Screens: the room with every screen in it (from above in stationary mode), and the selected screen's settings.
/// Hands, Games: gestures, plain screen and VR. Bots, Voice, Other PCs (SettingsPages.cs): Claude Code sessions,
/// push to talk, and the link to the wearer's other PCs. Settings: fine tuning and the app itself.
/// Every change applies at once.
/// </summary>
internal sealed partial class SettingsWindow : Form
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

    /// <summary>TV sizes people know, in inches across the diagonal.</summary>
    static readonly int[] TvSizes = [24, 32, 43, 55, 65, 75, 100, 150];

    const int Rail = 96, TopBar = 64, InspectorWidth = 380, ViewWidth = 960, WindowHeight = 820;
    const int ContentWidth = ViewWidth + InspectorWidth, PageHeight = WindowHeight - TopBar;
    const float FeetPerMetre = 3.2808f;

    // Frame.
    readonly NavRail _rail = new();
    readonly AuroraPanel _homePage = new(), _handsPage = new(), _gamesPage = new(), _botsPage = new(), _voicePage = new(), _pcsPage = new(), _settingsPage = new();
    /// <summary>Every page in the order of the side menu.</summary>
    Control[] Pages => [_homePage, _screensPage, _handsPage, _gamesPage, _botsPage, _voicePage, _pcsPage, _settingsPage];
    static readonly string[] PageNames = ["Home", "Screens", "Hands", "Games", "Bots", "Voice", "Other PCs", "Settings"];
    const int BotsIndex = 4, VoiceIndex = 5, PcsIndex = 6, SettingsIndex = 7;
    /// <summary>Plain-word tips for every button off the Screens page (there the hint panel says the same).</summary>
    readonly ToolTip _tips = new() { AutoPopDelay = 15000, InitialDelay = 400, ReshowDelay = 100 };
    readonly System.Windows.Forms.Panel _screensPage = new();
    readonly System.Windows.Forms.Panel _profileBar = new();
    readonly PillButton _live = new(), _modeChip = new(), _guide = new();
    string _pageTitle = "Home";

    // Home.
    readonly ModeCard _cardFollow = new(), _cardStationary = new(), _cardPlain = new(), _cardVr = new();
    readonly PillButton _game = new(), _vr = new();
    readonly WelcomeCard _welcome = new();

    // Screens: the room and its dock.
    readonly SpatialView _space = new();
    readonly FloorPlan _floor = new();
    readonly PillButton _viewToggle = new();
    bool _floorWanted = true;
    readonly PillButton _add = new(), _pin = new(), _remove = new(), _previewButton = new();
    readonly PictureBox _preview = new();
    readonly Label _virtualNote = new(), _hint = new();

    // Screens: the inspector.
    readonly PillButton _tabScreen = new(), _tabRoom = new();
    readonly System.Windows.Forms.Panel _pageScreen = new(), _pageStationary = new(), _pageFollow = new();
    readonly TextBox _name = new();
    readonly PillButton _source = new(), _visible = new(), _follows = new(), _sizes = new(), _shape = new(), _rate = new();
    readonly SliderRow _side, _height, _size, _depth, _turn, _curve, _tilt, _bright;
    readonly PillButton _pointerOff = new(), _pointerJump = new(), _pointerFollow = new(), _focus = new(), _toGaze = new();
    readonly SliderRow _held;
    readonly PillButton _dim = new(), _blank = new(), _place = new(), _recentre = new(), _drift = new();
    readonly PillButton _fit = new(), _putHere = new();

    // Hands.
    readonly PillButton _hands = new();
    readonly Label _handState = new();

    // Settings.
    SliderRow _delay = null!, _fov = null!, _level = null!, _roll = null!, _easy = null!; // made on the Settings page
    readonly PillButton _bigger = new();
    readonly PillButton _status = new(), _ruler = new(), _arrows = new(), _break = new(), _autostart = new(), _quit = new(), _github = new();

    readonly List<SourceChoice> _choices = [];
    readonly string _defaultHint;

    Layout _layout;
    ViewSettings _view;
    IReadOnlyList<DisplayInfo> _sources;
    IReadOnlyList<string> _profiles = [];
    int _activeProfile;
    string? _selected;
    bool _loading, _glassesStationary, _gameOn, _vrOn;

    public event Action<Layout>? LayoutChanged;
    public event Action<ViewSettings>? ViewChanged;
    public event Action<bool>? StatusPinnedChanged;
    public event Action? RecentreRequested;
    public event Action? PlaceRequested;
    public event Action? FitRequested;
    public event Action? PutHereRequested;
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

    /// <summary>The screen chosen in this window, if any.</summary>
    public string? SelectedId => _selected;
    /// <summary>True when a virtual monitor driver is there, so Add can make real monitors instead of stand-in pictures.</summary>
    public bool CanAddMonitors { get; set; }
    /// <summary>While the app runs, the window's close button quits the app (the window itself is closed by the app).</summary>
    public bool HideOnClose { get; set; } = true;

    public SettingsWindow(Layout layout, ViewSettings view, IReadOnlyList<DisplayInfo> sources, string recentreKey)
    {
        _layout = layout;
        _view = view;
        _sources = sources;
        _defaultHint = "Point at anything here for a word on what it does. Every key XrShift answers to is under Keys.";
        _tips.Popup += (_, e) =>
        {
            // On the Screens page the hint panel already says it.
            for (Control? c = e.AssociatedControl; c is not null; c = c.Parent)
                if (c == _screensPage) { e.Cancel = true; return; }
        };

        Text = "XrShift";
        Icon = AppIcon.Value;
        Font = Ui.Text(9.75f);
        BackColor = Ui.Back;
        ForeColor = Ui.Ink;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(Rail + ContentWidth, WindowHeight);
        DoubleBuffered = true;

        // ---- side menu --------------------------------------------------------------------------
        _rail.SetBounds(0, 0, Rail, WindowHeight);
        _rail.Add("", "Home");
        _rail.Add("", "Screens");
        _rail.Add("", "Hands");
        _rail.Add("", "Games");
        _rail.Add("", "Bots");
        _rail.Add("", "Voice");
        _rail.Add("", "Other PCs");
        _rail.Add("", "Settings");
        _rail.Selected += i => ShowSection(i);
        Controls.Add(_rail);

        // ---- top bar: page title (painted), profiles, glasses mode, live numbers, guide ----------
        _profileBar.SetBounds(Rail + 190, 15, 560, 34);
        _profileBar.BackColor = Ui.Back;
        Controls.Add(_profileBar);
        Place(_modeChip, this, "", Rail + ContentWidth - 16 - 40 - 8 - 150 - 8 - 168, 15, 168, 34, PillButton.Looks.Ghost,
            "Which way the glasses are working now. Follow and stationary are switched with the button on the glasses; XrShift notices by itself.");
        _modeChip.Cursor = Cursors.Default;
        _modeChip.TabStop = false;
        _modeChip.Click += (_, _) => ShowSection(0);
        Place(_live, this, "starting", Rail + ContentWidth - 16 - 40 - 8 - 150, 15, 150, 34, PillButton.Looks.Ghost,
            "Frames per second in the glasses, and how long a frame takes from reading your head to reaching the glasses.");
        _live.Font = Ui.Mono(9.5f);
        _live.TabStop = false;
        _live.Cursor = Cursors.Default;
        Place(_guide, this, "?", Rail + ContentWidth - 16 - 40, 15, 40, 34, PillButton.Looks.Ghost, "The short guide: the modes and how to add screens.");
        _guide.Click += (_, _) => { ShowSection(0); _welcome.Visible = true; _welcome.BringToFront(); };

        foreach (Control page in Pages)
        {
            page.SetBounds(Rail, TopBar, ContentWidth, PageHeight);
            page.Visible = false;
            Controls.Add(page);
        }

        BuildHome();

        // ---- Screens: the room ---------------------------------------------------------------------
        _screensPage.BackColor = Ui.Inspector;
        _space.SetBounds(0, 0, ViewWidth, PageHeight);
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
            LoadSelected();
        };
        _space.HintWanted += h => _hint.Text = h;
        _space.MouseLeave += (_, _) => _hint.Text = _defaultHint;
        _screensPage.Controls.Add(_space);

        // Stationary mode: the room from above, the easy way to put screens on walls and at distances.
        _space.DockHeight = DockHeight;
        _floor.SetBounds(0, 0, ViewWidth, PageHeight - DockHeight);
        _floor.DockHeight = 0;
        _floor.Font = Font;
        _floor.Visible = false;
        _floor.Picked += id => { if (_selected != id) { _selected = id; Refill(); } };
        _floor.Placed += (id, yaw, distance, scale) =>
        {
            if (_selected != id) { _selected = id; Refill(); }
            Change(s => s with { YawDeg = yaw, Distance = distance, Scale = scale });
            LoadSelected();
        };
        _floor.HintWanted += h => _hint.Text = h;
        _space.Controls.Add(_floor);
        Place(_viewToggle, _space, "3D view", ViewWidth - 16 - 120, 14, 120, 32, PillButton.Looks.Solid,
            "Switch between the room from above (easiest for putting screens on walls) and the 3D view.");
        _viewToggle.Click += (_, _) => { _floorWanted = !_floorWanted; ShowFloorOrRoom(); };
        _viewToggle.Visible = false;
        _viewToggle.BringToFront();

        int dockY = PageHeight - 50;
        Place(_add, _space, "+  Add monitor", 16, dockY, 138, 36, PillButton.Looks.Accent, "");
        _add.MouseEnter += (_, _) => _hint.Text = CanAddMonitors || VirtualMonitors.Installed
            ? "Adds a real extra monitor to Windows and puts it in the room. Drag windows onto it like any monitor. Hold Shift for a test picture instead."
            : "Extra monitors need a free display driver. Click to set it up, or add a test picture.";
        _add.Click += (_, _) =>
        {
            if ((CanAddMonitors || VirtualMonitors.Installed) && (ModifierKeys & Keys.Shift) == 0) MonitorAddRequested?.Invoke();
            else ShowNoMonitorMenu();
        };
        Place(_pin, _space, "Pin a window", 160, dockY, 126, 36, PillButton.Looks.Solid,
            "Floats one window (Teams, a browser, your work desktop...) as a screen of its own. It costs Windows nothing.");
        _pin.Click += (_, _) => ShowWindowMenu();
        Place(_remove, _space, "Remove", 292, dockY, 92, 36, PillButton.Looks.Ghost,
            "Removes the selected screen. An extra monitor goes from Windows too. A real monitor can only be hidden.");
        _remove.Click += (_, _) => RemoveSelected();
        Place(_previewButton, _space, "Glasses view", 390, dockY, 120, 36, PillButton.Looks.Solid,
            "Shows a live picture of what the glasses show, top right of the room.");
        _previewButton.Click += (_, _) =>
        {
            _previewButton.On = !_previewButton.On;
            _preview.Visible = _previewButton.On;
            if (!_previewButton.On) { _preview.Image?.Dispose(); _preview.Image = null; }
        };
        _preview.SetBounds(ViewWidth - 16 - 416, 56, 416, 234);
        _preview.SizeMode = PictureBoxSizeMode.Zoom;
        _preview.BackColor = Color.Black;
        _preview.BorderStyle = BorderStyle.FixedSingle;
        _preview.Visible = false;
        _space.Controls.Add(_preview);
        _preview.BringToFront();
        _virtualNote.SetBounds(16, dockY - 24, 300, 20);
        _virtualNote.ForeColor = Ui.Faint;
        _virtualNote.BackColor = Ui.RoomFloor;
        _virtualNote.Font = Ui.Mono(8.5f);
        _virtualNote.Visible = false;

        int px = ViewWidth - 16 - 5 * 78 + 6;
        foreach ((string text, Arrangement how, string hint) in new[]
        {
            ("Row", Arrangement.Row, "All shown screens side by side at eye level."),
            ("Curve", Arrangement.Curve, "Side by side and wrapped around you: one long curved band."),
            ("2 rows", Arrangement.TwoRows, "One row at eye level, the rest in a row above."),
            ("Focus", Arrangement.Focus, "The selected screen straight ahead at full size, the others smaller beside it."),
            ("Cinema", Arrangement.Cinema, "The selected screen big and gently curved straight ahead; the others are hidden (show them again on the Screen tab)."),
        })
        {
            var preset = new PillButton();
            Place(preset, _space, text, px, dockY, 72, 36, PillButton.Looks.Ghost, hint + " It rearranges this profile; to keep the old one, make a copy first with +.");
            preset.Click += (_, _) => Arrange(how);
            px += 78;
        }
        BuildLayoutPresets(dockY - 44);

        // ---- Screens: the inspector ------------------------------------------------------------------
        var inspector = new System.Windows.Forms.Panel { BackColor = Ui.Inspector };
        inspector.SetBounds(ViewWidth, 0, InspectorWidth, PageHeight);
        _screensPage.Controls.Add(inspector);
        Place(_tabScreen, inspector, "Screen", 12, 10, 120, 36, PillButton.Looks.Tab, "The selected screen: where it is, how big, what it shows.");
        _tabScreen.Font = Ui.Text(10.5f, bold: true);
        _tabScreen.Click += (_, _) => ShowInspector(_pageScreen);
        Place(_tabRoom, inspector, "Follow options", 136, 10, 170, 36, PillButton.Looks.Tab, "Options for the way the glasses are working now.");
        _tabRoom.Font = Ui.Text(10.5f, bold: true);
        _tabRoom.Click += (_, _) => ShowInspector(_glassesStationary ? _pageStationary : _pageFollow);
        foreach (System.Windows.Forms.Panel page in new[] { _pageScreen, _pageStationary, _pageFollow })
        {
            page.SetBounds(0, 54, InspectorWidth, 560);
            page.BackColor = Ui.Inspector;
            inspector.Controls.Add(page);
        }
        _hint.SetBounds(20, PageHeight - 120, InspectorWidth - 36 - 96, 108);
        Place(_keys, inspector, "Keys", InspectorWidth - 20 - 84, PageHeight - 52, 84, 34, PillButton.Looks.Ghost,
            "Every Ctrl+Alt key XrShift answers to, from any program, and what each one does.");
        _keys.Click += (_, _) => ShowKeys();
        _hint.ForeColor = Ui.Muted;
        _hint.BackColor = Ui.Inspector;
        _hint.Font = Ui.Text(8.75f);
        _hint.Text = _defaultHint;
        inspector.Controls.Add(_hint);
        inspector.Paint += (_, e) =>
        {
            using var rule = new Pen(Ui.Line);
            e.Graphics.DrawLine(rule, 0, 0, 0, inspector.Height);
            float d = DeviceDpi / 96f;
            e.Graphics.DrawLine(rule, 20 * d, _hint.Top - 10 * d, inspector.Width - 20 * d, _hint.Top - 10 * d);
        };

        // The selected screen.
        _name.SetBounds(20, 4, InspectorWidth - 40, 34);
        _name.BackColor = Ui.Inspector;
        _name.ForeColor = Ui.Ink;
        _name.BorderStyle = BorderStyle.None;
        _name.Font = Ui.Display(16f);
        _name.TextChanged += (_, _) => Change(s => s with { Name = _name.Text });
        _pageScreen.Controls.Add(_name);
        Place(_source, _pageScreen, "", 20, 42, InspectorWidth - 40, 32, PillButton.Looks.Solid,
            "What this screen shows: a monitor, a pinned window or a test picture. Click to change it.");
        _source.Click += (_, _) => ShowSourceMenu();
        int y = 84;
        _side = Slider(_pageScreen, "Left / right", -180, 180, 1, 0, 0, "°", true,
            "Where the screen sits around you. 0 is straight ahead, 90 is to your right, -90 to your left. Or drag it in the room.", ref y, v => Change(s => s with { YawDeg = -v }));
        _height = Slider(_pageScreen, "Down / up", -60, 60, 1, 0, 0, "°", true,
            "How far above or below eye level the screen sits.", ref y, v => Change(s => s with { PitchDeg = v }));
        _size = Slider(_pageScreen, "Size", 10, 300, 1, 55, 0, " in", false,
            "Its size across the diagonal, like a TV. The Sizes button has the common ones.", ref y, v => ChangeSize(v));
        _depth = Slider(_pageScreen, "Distance", 2, 29.5f, 0.5f, 6.5f, 1, " ft", false,
            "How far away it is. It keeps its real size, so further away looks smaller, as a real screen would.", ref y,
            v => Change(s => s.AtDistanceKeepingSize(v / FeetPerMetre)));
        _turn = Slider(_pageScreen, "Turn", -75, 75, 1, 0, 0, "°", true,
            "Turns the screen to face left or right, like a TV flat on a side wall. 0 faces you. Turning a curved screen makes it flat.", ref y,
            // Only a flat screen can be turned. Moving this on a curved one used to do nothing at all, so it flattens it.
            v => Change(s => s with { TurnDeg = v, CurveDeg = MathF.Abs(v) >= 0.5f ? 0 : s.CurveDeg }));
        _curve = Slider(_pageScreen, "Curve", 0, 120, 1, 0, 0, "°", false,
            "Wraps the screen around you. 0 is flat.", ref y, v => Change(s => s with { CurveDeg = v }));
        _tilt = Slider(_pageScreen, "Tilt", -30, 30, 0.5f, 0, 1, "°", true,
            "Turns the screen clockwise or anticlockwise.", ref y, v => Change(s => s with { TiltDeg = v }));
        _bright = Slider(_pageScreen, "Brightness", 20, 100, 5, 100, 0, " %", false,
            "How bright this screen is in the glasses. Lower it for a screen you only glance at.", ref y, v => Change(s => s with { Brightness = v / 100f }));
        y += 8;
        Place(_visible, _pageScreen, "Shown", 20, y, 104, 32, PillButton.Looks.Solid, "Switch a screen off without losing its place.");
        _visible.Click += (_, _) => Change(s => s with { Visible = !s.Visible });
        Place(_follows, _pageScreen, "Follows me", 130, y, 116, 32, PillButton.Looks.Solid,
            "The screen stays in the same spot of your view as you turn, like a heads-up display: good for a chat or a clock. Off: it stays in the room.");
        _follows.Click += (_, _) => Change(s => s with { FollowsMe = !s.FollowsMe });
        Place(_sizes, _pageScreen, "Sizes  ▾", 252, y, 108, 32, PillButton.Looks.Ghost, "Common TV sizes.");
        _sizes.Click += (_, _) => ShowSizesMenu();
        y += 40;
        Place(_shape, _pageScreen, "Standard 16:9", 20, y, 226, 32, PillButton.Looks.Ghost,
            "For an extra monitor: its shape. An ultrawide is one long desktop for two or three windows side by side.");
        _shape.Click += (_, _) => ShowShapeMenu();
        Place(_rate, _pageScreen, "60 Hz", 252, y, 108, 32, PillButton.Looks.Solid,
            "For an extra monitor: 60 Hz for reading, easy on the graphics card. 120 Hz for a screen you play on.");
        _rate.Click += (_, _) => Change(s => s with { RefreshHz = s.RefreshHz >= 100 ? 0 : 120 });
        y += 44;
        BuildSizePresets(y);

        // Follow options.
        y = 6;
        Head(_pageFollow, "Look to point", ref y);
        Place(_pointerOff, _pageFollow, "Off", 20, y, 106, 32, PillButton.Looks.Solid, "The mouse pointer moves only by hand.");
        _pointerOff.Click += (_, _) => ChangeView(s => s with { LookPointer = false, LookFollow = false });
        Place(_pointerJump, _pageFollow, "Jump", 132, y, 106, 32, PillButton.Looks.Solid,
            "Look at another screen for a moment and the pointer jumps to the spot you look at. Moving the mouse by hand always wins. Pauses while a game has the mouse.");
        _pointerJump.Click += (_, _) => ChangeView(s => s with { LookPointer = true, LookFollow = false });
        Place(_pointerFollow, _pageFollow, "Follow", 244, y, 116, 32, PillButton.Looks.Solid,
            "The pointer rides the middle of your view: aim with your head, click with the mouse. Pauses while a game has the mouse.");
        _pointerFollow.Click += (_, _) => ChangeView(s => s with { LookPointer = true, LookFollow = true });
        y += 40;
        Place(_focus, _pageFollow, "Keyboard follows", 20, y, 162, 32, PillButton.Looks.Solid,
            "The window the pointer moves to also gets the keyboard, so you can look from a game to your work desktop and type.");
        _focus.Click += (_, _) => ChangeView(s => s with { LookFocus = !s.LookFocus });
        Place(_toGaze, _pageFollow, "Mouse here now", 188, y, 172, 32, PillButton.Looks.Ghost,
            $"Puts the pointer where you are looking, once. The same key from any program: {Hotkeys.Text((int)Keys.M)}.");
        _toGaze.Click += (_, _) => MouseToGazeRequested?.Invoke();
        y += 40;
        Place(_bringNew, _pageFollow, "Bring new windows to where I'm looking", 20, y, 340, 32, PillButton.Looks.Solid,
            $"A window that opens goes to the monitor of the screen you are looking at, so it never opens out of sight. {Hotkeys.Text((int)Keys.W)} does it for the window in front, once.");
        _bringNew.Click += (_, _) => ChangeView(s => s with { BringNewWindows = !s.BringNewWindows });
        y += 48;
        Head(_pageFollow, "All screens", ref y);
        BuildZoomSteps(ref y);
        y += 6;
        Place(_dim, _pageFollow, "Dim others", 20, y, 106, 32, PillButton.Looks.Solid,
            "Screens you are not looking at go darker, so the one you read stands out.");
        _dim.Click += (_, _) => ChangeView(s => s with { Dim = !s.Dim });
        Place(_blank, _pageFollow, "Hide all", 132, y, 106, 32, PillButton.Looks.Solid, "Hides every screen so you see the room. Ctrl+Alt+B.");
        _blank.Click += (_, _) => { _blank.On = !_blank.On; BlankChanged?.Invoke(_blank.On); };
        Place(_place, _pageFollow, "Pick up", 244, y, 116, 32, PillButton.Looks.Ghost,
            "Picks up the screen you are looking at: turn your head to carry it, then Ctrl+Alt+P to set it down.");
        _place.Click += (_, _) => PlaceRequested?.Invoke();
        y += 40;
        Place(_recentre, _pageFollow, "Recentre", 20, y, 106, 32, PillButton.Looks.Ghost, $"Makes the way you face now straight ahead. {recentreKey}.");
        _recentre.Click += (_, _) => RecentreRequested?.Invoke();
        Place(_drift, _pageFollow, "Drift after me", 132, y, 140, 32, PillButton.Looks.Solid,
            "Turn far away and the screens drift after you, then settle in front again. Off, they stay where you put them.");
        _drift.Click += (_, _) => ChangeView(s => s with { Follow = !s.Follow });

        // Stationary options.
        y = 6;
        Head(_pageStationary, "Stationary mode", ref y);
        var about = new Label
        {
            Text = "Place screens anywhere in the room on the floor plan: in front, on a side wall, behind you. Turn to one and hold " +
                   "still a moment: the glasses glide their picture there and hold it steady, even when you lean or walk. " +
                   "Arrows at the edge of your view point to the screens you are not facing.",
            ForeColor = Ui.Muted, BackColor = Ui.Inspector, Font = Ui.Text(9f),
        };
        about.SetBounds(20, y, InspectorWidth - 40, 96);
        _pageStationary.Controls.Add(about);
        y += 104;
        Place(_putHere, _pageStationary, "Put it where I look", 20, y, 166, 34, PillButton.Looks.Accent,
            "Moves the selected screen (or the newest) to where you are looking now. Ctrl+Alt+P does the same.");
        _putHere.Click += (_, _) => PutHereRequested?.Invoke();
        Place(_fit, _pageStationary, "Gather in one view", 192, y, 168, 34, PillButton.Looks.Solid,
            "Gathers every screen so all of them fit in one view, straight ahead.");
        _fit.Click += (_, _) => FitRequested?.Invoke();
        y += 50;
        _held = Slider(_pageStationary, "Held picture", ViewSettings.MinHeldFovDeg, ViewSettings.MaxHeldFovDeg, 1, DisplayOptics.OnePro.HorizontalFovDeg, 0, "°", false,
            "How wide the picture the glasses hold still is. If you make it bigger in the glasses' own menu, set the same here.", ref y,
            v => ChangeView(s => s with { HeldFovDeg = v }));

        BuildHands();
        BuildGames();
        BuildBots();
        BuildVoice();
        BuildPcs();
        BuildSettings();

        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;

        ShowInspector(_pageScreen);
        ShowSection(0);
        Refill();
        LoadView();
        ShowModes();
    }

    // ---- the pages --------------------------------------------------------------------------------

    void BuildHome()
    {
        var title = new Label { Text = "How do you want to use your glasses?", Font = Ui.Display(20f), ForeColor = Ui.Ink, BackColor = Color.Transparent, AutoSize = true };
        title.Location = new Point(36, 26);
        _homePage.Controls.Add(title);
        var sub = new Label
        {
            Text = "Follow and Stationary are switched with the button on your glasses: XrShift notices by itself. Plain screen and VR are switched here.",
            Font = Ui.Text(10f), ForeColor = Ui.Muted, BackColor = Color.Transparent, AutoSize = true,
        };
        sub.Location = new Point(38, 72);
        _homePage.Controls.Add(sub);

        int w = (ContentWidth - 72 - 3 * 18) / 4, h = 344, top = 112;
        Setup(_cardFollow, "Follow", "", Ui.Accent, 36,
            "XrShift follows your head and keeps your screens all around you.",
            ["Screens in every direction", "Extra monitors and windows", "Look to move the mouse"],
            ["Screens stay put only as you turn", "Hand gestures"],
            "Switch with the button on your glasses.");
        Setup(_cardStationary, "Stationary", "", Ui.Violet, 36 + (w + 18),
            "The glasses hold the picture still in the room. Walk up close.",
            ["Rock steady, even walking", "Screens on any wall", "Hand gestures and quick menu"],
            ["One direction shown at once"],
            "Switch with the button on your glasses.");
        Setup(_cardPlain, "Plain screen", "", Ui.Warn, 36 + 2 * (w + 18),
            "XrShift steps aside: the glasses are an ordinary monitor.",
            ["Best for games", "Nothing in between"],
            ["No extra screens", "No head tracking"], "");
        Setup(_cardVr, "VR", "", Color.FromArgb(240, 171, 252), 36 + 3 * (w + 18),
            "SteamVR games, seated: the glasses become a headset.",
            ["Look around in games", "Works with a gamepad"],
            ["Leaning or walking", "Motion controllers"], "");

        Place(_game, _cardPlain, "Turn on", 18, h - 56, w - 36, 38, PillButton.Looks.Solid,
            "The glasses become an ordinary extended screen, for a game, say. Extra monitors are removed until you switch it off. Ctrl+Alt+G.");
        _game.Click += (_, _) => GameModeChanged?.Invoke(!_gameOn);
        Place(_vr, _cardVr, "Start VR", 18, h - 56, w - 36, 38, PillButton.Looks.Solid,
            "Starts SteamVR with the glasses as the headset. Put the glasses in Follow first; for depth, switch them to their 3D mode. Ctrl+Alt+V.");
        _vr.Click += (_, _) => VrModeChanged?.Invoke(!_vrOn);
        _ = top;

        // The glasses right now, and things you do often.
        var status = new Card { Title = "How things are now", BackColor = Ui.Inspector };
        status.SetBounds(36, top + h + 22, 560, 216);
        BuildStatus(status);
        _homePage.Controls.Add(status);

        var quick = new Card { Title = "Quick actions", BackColor = Ui.Inspector };
        quick.SetBounds(36 + 560 + 18, top + h + 22, ContentWidth - 72 - 560 - 18, 216);
        _homePage.Controls.Add(quick);
        int qx = 20, qy = 58;
        foreach ((string text, string hint, Action act) in new (string, string, Action)[]
        {
            ("Add a monitor", "Opens the Screens page and adds an extra monitor.", () => { ShowSection(1); _add.PerformClick(); }),
            ("Pin a window", "Opens the Screens page to float a window as a screen.", () => { ShowSection(1); ShowWindowMenu(); }),
            ("Recentre", "Makes the way you face now straight ahead.", () => RecentreRequested?.Invoke()),
            ("Hide all screens", "Hides every screen so you see the room. Click again to bring them back.", () => { _blank.On = !_blank.On; BlankChanged?.Invoke(_blank.On); }),
            ("Glasses view", "Opens the Screens page with a live picture of what the glasses show.", () => { ShowSection(1); if (!_previewButton.On) _previewButton.PerformClick(); }),
            ("Show the guide", "The short guide again.", () => { _welcome.Visible = true; _welcome.BringToFront(); }),
        })
        {
            var b = new PillButton();
            Place(b, quick, text, qx, qy, 200, 40, PillButton.Looks.Solid, hint);
            b.Click += (_, _) => act();
            qx += 212;
            if (qx + 200 > quick.Width - 10) { qx = 20; qy += 52; }
        }

        _welcome.SetBounds((ContentWidth - 600) / 2, 90, 600, 420);
        _welcome.Visible = WelcomeCard.FirstTime;
        _homePage.Controls.Add(_welcome);
        _welcome.BringToFront();

        void Setup(ModeCard card, string name, string glyph, Color colour, int x, string tagline, string[] gets, string[] misses, string howTo)
        {
            card.Text = name;
            card.Glyph = glyph;
            card.LitColour = colour;
            card.Tagline = tagline;
            card.Gets = gets;
            card.Misses = misses;
            card.HowTo = howTo;
            card.BackColor = Ui.Inspector;
            card.SetBounds(x, top, w, h);
            _homePage.Controls.Add(card);
        }
    }

    void BuildHands()
    {
        PageTitle(_handsPage, "Hand gestures", "Steer with your hand while the glasses are in stationary mode: their camera only runs then. Nothing it sees leaves this PC or is saved.");
        var main = new Card { Title = "Gestures", Subtitle = "Turn this on once; it works whenever the glasses are in stationary mode.", BackColor = Ui.Inspector };
        main.SetBounds(36, 112, 420, 300);
        Place(_hands, main, "Hand gestures: off", 20, 110, 380, 44, PillButton.Looks.Solid,
            "Your hand steers the pointer like a touchpad. Ctrl+Alt+K.");
        _hands.Click += (_, _) =>
        {
            if (!HandInput.Installed) { OfferHandSetup(); return; }
            ChangeView(s => s with { Hands = !s.Hands });
        };
        _handState.SetBounds(22, 166, 380, 24);
        _handState.ForeColor = Ui.Accent;
        _handState.BackColor = Ui.Inspector;
        _handState.Font = Ui.Mono(10f);
        main.Controls.Add(_handState);
        var note = new Label
        {
            Text = "The first time, XrShift sets up Google's free hand tracker (about 390 MB, in a folder of its own). Good light helps: the camera sees in grey.",
            ForeColor = Ui.Faint, BackColor = Ui.Inspector, Font = Ui.Text(9f),
        };
        note.SetBounds(22, 204, 380, 80);
        main.Controls.Add(note);
        _handsPage.Controls.Add(main);

        int x = 36 + 420 + 18, cw = (ContentWidth - x - 36 - 18) / 2;
        (string Title, string Text)[] gestures =
        [
            ("Move", "Hold your hand up, palm out, and move it: the pointer moves like on a touchpad."),
            ("Click", "Touch your thumb and index finger together, then let go."),
            ("Drag", "Pinch, keep pinching while you move your hand, then let go."),
            ("Quick menu", "Hold a flat hand still for a moment. Move it to choose, pinch to pick, make a fist to close."),
        ];
        for (int i = 0; i < gestures.Length; i++)
        {
            var card = new Card { Title = gestures[i].Title, Subtitle = gestures[i].Text, BackColor = Ui.Inspector };
            card.SetBounds(x + (i % 2) * (cw + 18), 112 + (i / 2) * 150, cw, 132);
            _handsPage.Controls.Add(card);
        }
        var menu = new Card
        {
            Title = "In the quick menu",
            Subtitle = "Hide all  ·  Recentre  ·  Put here (the selected screen where you look)  ·  Next profile  ·  Close",
            BackColor = Ui.Inspector,
        };
        menu.SetBounds(x, 112 + 300, cw * 2 + 18, 110);
        _handsPage.Controls.Add(menu);
    }

    void BuildGames()
    {
        PageTitle(_gamesPage, "Games", "Two ways to play with the glasses, and how to get the best out of each.");
        int w = (ContentWidth - 72 - 18) / 2;
        var plain = new Card
        {
            Title = "Plain screen",
            Subtitle = "XrShift steps aside and the glasses become an ordinary monitor, straight from the graphics card: the smoothest way to play a game on the glasses. " +
                       "Your extra monitors are removed while it is on; they come back when you switch it off. Ctrl+Alt+G.",
            BackColor = Ui.Inspector,
        };
        plain.SetBounds(36, 112, w, 230);
        var plainGo = new PillButton();
        Place(plainGo, plain, "Turn plain screen on", 20, 160, 240, 42, PillButton.Looks.Accent, "Ctrl+Alt+G");
        plainGo.Click += (_, _) => GameModeChanged?.Invoke(!_gameOn);
        _gamesPage.Controls.Add(plain);

        var vr = new Card
        {
            Title = "VR",
            Subtitle = "Play SteamVR games seated: the glasses become a headset that follows your head as you look around (not as you lean or walk). " +
                       "1 Glasses in Follow.  2 Start VR here.  3 Start the game from Steam.  Ctrl+Alt+V.",
            BackColor = Ui.Inspector,
        };
        vr.SetBounds(36 + w + 18, 112, w, 230);
        var vrGo = new PillButton();
        Place(vrGo, vr, "Start VR", 20, 160, 240, 42, PillButton.Looks.Accent, "Ctrl+Alt+V");
        vrGo.Click += (_, _) => VrModeChanged?.Invoke(!_vrOn);
        _gamesPage.Controls.Add(vr);
        _gameButtons = (plainGo, vrGo);

        var tips = new Card
        {
            Title = "Tips",
            Subtitle = "·  Set games to borderless windowed, at the size of the screen they are on (1920 x 1080 for the glasses and extra monitors).\n" +
                       "·  VR games that work with a gamepad are the ones to start with, like No Man's Sky.\n" +
                       "·  For depth in VR, switch the glasses to their 3D mode in their own menu first.\n" +
                       "·  Look to point pauses while a game has the mouse, and picks up again when the pointer shows.\n" +
                       "·  On a laptop with two graphics chips, keep the glasses on the USB-C port wired to the stronger one.",
            BackColor = Ui.Inspector,
        };
        tips.SetBounds(36, 360, ContentWidth - 72, 220);
        _gamesPage.Controls.Add(tips);
    }

    (PillButton Plain, PillButton Vr) _gameButtons;

    void BuildSettings()
    {
        PageTitle(_settingsPage, "Settings", "Fine tuning, what the glasses show besides your screens, and the app itself.");
        int w = (ContentWidth - 72 - 18) / 2;
        var steady = new Card { Title = "Steadiness", BackColor = Ui.Inspector };
        steady.SetBounds(36, 112, w, 250);
        BuildSteadiness(steady, w);
        // Advanced: the four sliders that were all this card had, shown in place of the simple choices when asked for.
        int y = 60;
        _delay = Slider(steady, "Glasses delay", ViewSettings.MinDelayMs, ViewSettings.MaxDelayMs, 0.5f, 0, 1, " ms", true,
            "Screens trail behind when you turn: raise it. They overshoot or jitter as you stop: lower it.", ref y,
            v => ChangeView(s => s with { DelayMs = v }), w - 40);
        _fov = Slider(steady, "View angle", ViewSettings.MinFovDeg, ViewSettings.MaxFovDeg, 0.1f, DisplayOptics.OnePro.HorizontalFovDeg, 1, "°", false,
            "Screens follow your head a little as you turn: lower it. They move against your head: raise it.", ref y,
            v => ChangeView(s => s with { FovDeg = v }), w - 40);
        _level = Slider(steady, "Level", -ViewSettings.MaxLevelDeg, ViewSettings.MaxLevelDeg, 0.25f, 0, 2, "°", true,
            "A screen off to the side tilts or climbs as you turn away from it: adjust until it stops.", ref y,
            v => ChangeView(s => s with { LevelDeg = v }), w - 40);
        _roll = Slider(steady, "Lean", -ViewSettings.MaxRollDeg, ViewSettings.MaxRollDeg, 0.25f, 0, 2, "°", true,
            "Everything looks rotated: straighten it here.", ref y, v => ChangeView(s => s with { RollDeg = v }), w - 40);
        ShowAdvanced(false);
        _settingsPage.Controls.Add(steady);

        var glasses = new Card { Title = "In the glasses", BackColor = Ui.Inspector };
        glasses.SetBounds(36 + w + 18, 112, w, 250);
        Place(_arrows, glasses, "Arrows to other screens", 20, 60, 230, 36, PillButton.Looks.Solid,
            "Arrows at the edge of your view point the way to screens you are not facing.");
        _arrows.Click += (_, _) => ChangeView(s => s with { EdgeArrows = !s.EdgeArrows });
        Place(_status, glasses, "Status line", 20, 104, 230, 36, PillButton.Looks.Solid, "Keeps the line of numbers showing in the glasses. Ctrl+Alt+H.");
        _status.Click += (_, _) => { _status.On = !_status.On; StatusPinnedChanged?.Invoke(_status.On); };
        Place(_ruler, glasses, "Horizon ruler", 20, 148, 230, 36, PillButton.Looks.Solid, "A ruler all the way round at eye level, marked in degrees.");
        _ruler.Click += (_, _) => ChangeView(s => s with { Ruler = !s.Ruler });
        Place(_break, glasses, "Break reminder: off", 20, 192, 230, 36, PillButton.Looks.Solid,
            "A line in the glasses every so often reminding you to look into the distance for 20 seconds. Click to change how often.");
        _break.Click += (_, _) => ChangeView(s => s with { BreakMinutes = s.BreakMinutes switch { 0 => 20, 20 => 30, 30 => 45, 45 => 60, _ => 0 } });
        _settingsPage.Controls.Add(glasses);

        var app = new Card
        {
            Title = $"XrShift {Application.ProductVersion.Split('+')[0]}",
            Subtitle = "Free and open source. Made with Claude (Anthropic's AI), shaped by real testing with XREAL One Pro glasses. Not made by XREAL.",
            BackColor = Ui.Inspector,
        };
        app.SetBounds(36, 380, ContentWidth - 72, 168);
        Place(_autostart, app, "Start with Windows", 20, 108, 200, 40, PillButton.Looks.Solid,
            "Starts XrShift quietly when you sign in. It waits in the tray and brings your screens up when the glasses are plugged in.");
        _autostart.On = Autostart.Enabled;
        _autostart.Click += (_, _) => { if (Autostart.Set(!_autostart.On)) _autostart.On = !_autostart.On; };
        Place(_github, app, "XrShift on GitHub", 232, 108, 200, 40, PillButton.Looks.Ghost, "Opens the project page: updates, the code and a place to report problems.");
        _github.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/skinger3/XrShift") { UseShellExecute = true }); }
            catch (Exception) { }
        };
        Place(_quit, app, "Quit XrShift", 444, 108, 200, 40, PillButton.Looks.Ghost,
            "Closes XrShift. Extra monitors are removed, so windows on them come back to your real screen. Ctrl+Alt+Q.");
        _quit.Click += (_, _) => QuitRequested?.Invoke();
        _settingsPage.Controls.Add(app);

        var eyes = new Card
        {
            Title = "Without your glasses",
            Subtitle = "Not wearing your glasses or contacts? Sharpening and extra contrast make text easier to read, and bigger screens help most. " +
                       "For real sharpness the glasses take prescription lenses (inserts) from XREAL or an optician.",
            BackColor = Ui.Inspector,
        };
        eyes.SetBounds(36, 566, ContentWidth - 72, 168);
        int ey = 112;
        _easy = Slider(eyes, "Sharpen text", 0, 100, 5, 0, 0, " %", false,
            "Sharpens edges and adds contrast on every screen. 0 is off.", ref ey, v => ChangeView(s => s with { EasyReading = v / 100f }), 520);
        Place(_bigger, eyes, "Make every screen bigger", 600, 108, 240, 40, PillButton.Looks.Solid,
            "Every screen one zoom step bigger, keeping the arrangement (Zoom on the Screens page). Ctrl+Alt+PgUp / PgDn from anywhere.");
        _bigger.Click += (_, _) => ChangeView(s => s with { Zoom = Sizes.ZoomSteps.FirstOrDefault(z => z > s.Zoom + 0.01f, Sizes.ZoomSteps[^1]) });
        _settingsPage.Controls.Add(eyes);
        BuildSetupPanel([app, eyes]);
    }

    static void PageTitle(Control page, string title, string sub)
    {
        var t = new Label { Text = title, Font = Ui.Display(20f), ForeColor = Ui.Ink, BackColor = Color.Transparent, AutoSize = true };
        t.Location = new Point(36, 26);
        page.Controls.Add(t);
        var s = new Label { Text = sub, Font = Ui.Text(10f), ForeColor = Ui.Muted, BackColor = Color.Transparent, AutoSize = true };
        s.Location = new Point(38, 72);
        page.Controls.Add(s);
    }

    void ShowSection(int index)
    {
        Control[] pages = Pages;
        index = Math.Clamp(index, 0, pages.Length - 1);
        for (int i = 0; i < pages.Length; i++) pages[i].Visible = i == index;
        PageShown(index);
        _rail.SelectedIndex = index;
        _pageTitle = index == 1 ? "Screens" : "XrShift";
        _profileBar.Visible = index == 1; // profiles are sets of screens: shown where the screens are
        Invalidate(new Rectangle(Rail, 0, ContentWidth, TopBar));
    }

    void ShowInspector(System.Windows.Forms.Panel page)
    {
        _pageScreen.Visible = page == _pageScreen;
        _pageFollow.Visible = page == _pageFollow;
        _pageStationary.Visible = page == _pageStationary;
        _tabScreen.On = page == _pageScreen;
        _tabRoom.On = page != _pageScreen;
    }

    DisplayOptics Optics => DisplayOptics.OnePro with { HorizontalFovDeg = _glassesStationary ? _view.HeldFovDeg : _view.FovDeg };

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        Ui.Smooth(g);
        float d = DeviceDpi / 96f;
        using (Font title = Ui.Display(15f))
        using (var ink = new SolidBrush(Ui.Ink))
            g.DrawString(_pageTitle, title, ink, (Rail + 26) * d, 19 * d);
        // A fine line under the top bar, teal fading into violet.
        var line = new RectangleF(Rail * d, TopBar * d - 2, (Width - Rail * d), 2);
        using var glow = new System.Drawing.Drawing2D.LinearGradientBrush(line, Ui.Fade(Ui.Accent, 160), Ui.Fade(Ui.Violet, 60), 0f);
        g.FillRectangle(glow, line);
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

    public void SetGameMode(bool on) { _gameOn = on; ShowModes(); }

    public void SetVrMode(bool on) { _vrOn = on; ShowModes(); }

    public void SetBlank(bool on) => _blank.On = on;

    /// <summary>Which mode the glasses are in, as XrShift sees it.</summary>
    public void SetGlassesMode(bool stationary)
    {
        _glassesStationary = stationary;
        Refill(); // the room view draws for the angle in use
        _space.HeldFrame = stationary;
        ShowFloorOrRoom();
        _tabRoom.Text = stationary ? "Stationary options" : "Follow options";
        if (!_pageScreen.Visible) ShowInspector(stationary ? _pageStationary : _pageFollow);
        ShowModes();
        ShowViewToggles();
    }

    // The four modes: which one is on, what the buttons say, the chip at the top.
    void ShowModes()
    {
        bool follow = !_gameOn && !_vrOn && !_glassesStationary, stationary = !_gameOn && !_vrOn && _glassesStationary;
        _cardFollow.Lit = follow;
        _cardStationary.Lit = stationary;
        _cardPlain.Lit = _gameOn && !_vrOn;
        _cardVr.Lit = _vrOn;
        foreach (Control c in new Control[] { _cardFollow, _cardStationary, _cardPlain, _cardVr }) c.Invalidate();
        _game.Text = _gameOn && !_vrOn ? "Turn off" : "Turn on";
        _game.Look = _gameOn && !_vrOn ? PillButton.Looks.Solid : PillButton.Looks.Accent;
        _vr.Text = _vrOn ? "Stop VR" : "Start VR";
        _vr.Look = _vrOn ? PillButton.Looks.Solid : PillButton.Looks.Accent;
        if (_gameButtons.Plain is not null)
        {
            _gameButtons.Plain.Text = _gameOn && !_vrOn ? "Turn plain screen off" : "Turn plain screen on";
            _gameButtons.Vr.Text = _vrOn ? "Stop VR" : "Start VR";
        }
        _modeChip.Text = _vrOn ? "●  VR" : _gameOn ? "●  Plain screen" : _glassesStationary ? "●  Stationary" : "●  Follow";
        _modeChip.TextColour = _vrOn ? Color.FromArgb(240, 171, 252) : _gameOn ? Ui.Warn : _glassesStationary ? Ui.Violet : Ui.Accent;
        _modeChip.Invalidate();
    }

    // In stationary mode the floor plan is shown unless the wearer asked for the 3D room.
    void ShowFloorOrRoom()
    {
        _viewToggle.Visible = _glassesStationary;
        _floor.Visible = _glassesStationary && _floorWanted;
        _viewToggle.Text = _floor.Visible ? "3D view" : "Floor plan";
        _viewToggle.BringToFront();
    }

    /// <summary>Brings a page to the front by name: home, screens, stationary, follow, hands, games, bots, voice, pcs, settings (or tune), or advanced.</summary>
    public void ShowPageNamed(string name)
    {
        switch (name.ToLowerInvariant())
        {
            case "home": ShowSection(0); break;
            case "stationary": ShowSection(1); ShowInspector(_pageStationary); break;
            case "follow": ShowSection(1); ShowInspector(_pageFollow); break;
            case "hands": ShowSection(2); break;
            case "games": ShowSection(3); break;
            case "bots": ShowSection(BotsIndex); break;
            case "voice": ShowSection(VoiceIndex); break;
            case "pcs" or "other" or "network": ShowSection(PcsIndex); break;
            case "advanced": ShowSection(SettingsIndex); ShowAdvanced(true); break;
            case "tune" or "settings": ShowSection(SettingsIndex); break;
            case "setup" or "calibrate": ShowCalibration(); break;
            default: ShowSection(1); ShowInspector(_pageScreen); break;
        }
    }

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

    /// <summary>A short line about the extra monitors, e.g. how many there are.</summary>
    public void SetVirtualNote(string text) => _virtualNote.Text = text;

    /// <summary>The live numbers at the top right, and the glasses' card on the home page.</summary>
    public void SetLive(string text, bool healthy)
    {
        _liveText = text;
        _liveHealthy = healthy;
        ShowStatus();
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

    void ChangeSize(float inches)
    {
        if (_selected is null || _layout.Find(_selected) is not { } spec) return;
        (int w, int h) = PictureSize(spec);
        Change(s => s.WithDiagonalInches(inches, w, h, Optics));
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
        (CanAddMonitors || VirtualMonitors.Installed) && VirtualMonitors.IsVirtualId(_layout.Find(id)?.DisplayName);

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

    void ShowSizesMenu()
    {
        if (_selected is null || _layout.Find(_selected) is not { } spec) return;
        ContextMenuStrip menu = NewMenu();
        foreach (int inches in TvSizes)
        {
            var item = new ToolStripMenuItem($"{inches}-inch screen") { ForeColor = Ui.Ink };
            item.Click += (_, _) => { ChangeSize(inches); LoadSelected(); };
            menu.Items.Add(item);
        }
        menu.Show(_sizes, new Point(0, _sizes.Height));
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

    // Extra monitors are real Windows desktops made by Parsec's free, signed Virtual Display Driver. It is set up
    // only when asked for, here, with Windows' own installer (winget); Windows asks for permission.
    void OfferMonitorSetup()
    {
        if (MessageBox.Show(this,
                "Extra monitors are real Windows desktops: drag windows onto them, run your work desktop full screen on one and a game on another.\n\n" +
                "Windows needs a small display driver for them. XrShift uses Parsec's free Virtual Display Driver (signed by Parsec). " +
                "Its monitors exist only while XrShift runs, so none are ever left behind.\n\n" +
                "Install it now? Windows will ask for permission. Afterwards, click Add monitor again.",
                "XrShift - extra monitors", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = true };
            foreach (string a in new[] { "-NoProfile", "-Command",
                         "winget install --id Parsec.ParsecVDD -e --accept-package-agreements --accept-source-agreements; Write-Host ''; Write-Host 'Done. You can close this window and click Add monitor in XrShift.'; Read-Host 'Press Enter to close'" })
                start.ArgumentList.Add(a);
            System.Diagnostics.Process.Start(start);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(this, "Could not start the installer: " + e.Message, "XrShift", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // Without an extra-desktop driver there is no real monitor to add: say so plainly instead of adding a dummy.
    void ShowNoMonitorMenu()
    {
        ContextMenuStrip menu = NewMenu();
        menu.Items.Add(new ToolStripMenuItem("Extra monitors need a free display driver") { Enabled = false });
        var setUp = new ToolStripMenuItem("Set up extra monitors...") { ForeColor = Ui.Accent };
        setUp.Click += (_, _) => OfferMonitorSetup();
        menu.Items.Add(setUp);
        menu.Items.Add(new ToolStripSeparator());
        var test = new ToolStripMenuItem("Add a test picture (for trying out layouts)");
        test.Click += (_, _) => AddScreen();
        menu.Items.Add(test);
        menu.Show(_add, new Point(0, -menu.PreferredSize.Height));
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
            public override Color MenuBorder => Ui.Rim;
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
        _floor.Show(screens, _selected, Optics);
    }

    void LoadSelected()
    {
        ScreenSpec? spec = _selected is null ? null : _layout.Find(_selected);
        bool has = spec is not null;
        foreach (Control c in new Control[] { _name, _source, _side, _height, _size, _depth, _turn, _curve, _tilt, _bright, _visible, _follows, _sizes })
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
                    : VirtualMonitors.IsVirtualId(spec.DisplayName) ? "An extra monitor (off for now)" : "A monitor that is not connected");
                _choices.Add(current);
            }
            _source.Text = current.Text;
            (int w, int h) = PictureSize(spec);
            _side.Value = -spec.YawDeg;
            _height.Value = spec.PitchDeg;
            _size.Value = spec.DiagonalInches(w, h, Optics);
            _depth.Value = spec.Distance * FeetPerMetre;
            _turn.Value = spec.TurnDeg;
            _curve.Value = spec.CurveDeg;
            _tilt.Value = spec.TiltDeg;
            _bright.Value = spec.Brightness * 100f;
        });
        ShowToggles(spec);
        UpdateButtons();
    }

    /// <summary>Shown, follows me, shape and refresh of the selected screen. Shape and refresh only mean something for an extra monitor.</summary>
    void ShowToggles(ScreenSpec? spec)
    {
        _visible.On = spec?.Visible ?? false;
        _visible.Text = spec is { Visible: false } ? "Hidden" : "Shown";
        _follows.On = spec?.FollowsMe ?? false;
        bool virtualMonitor = spec is not null && (CanAddMonitors || VirtualMonitors.Installed) && VirtualMonitors.IsVirtualId(spec.DisplayName);
        _shape.Enabled = _rate.Enabled = virtualMonitor;
        (string name, _, _, _) = Shapes.FirstOrDefault(s => spec is not null && s.Width == spec.PixelWidth && s.Height == spec.PixelHeight, Shapes[0]);
        _shape.Text = virtualMonitor ? name : "Shape (extra monitors)";
        _rate.Text = virtualMonitor && spec!.RefreshHz >= 100 ? "120 Hz" : "60 Hz";
        _rate.On = virtualMonitor && spec!.RefreshHz >= 100;
        ShowSizePreset(spec);
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
            _held.Value = _view.HeldFovDeg;
            _easy.Value = _view.EasyReading * 100f;
        });
        ShowViewToggles();
    }

    void ShowViewToggles()
    {
        _ruler.On = _view.Ruler;
        _arrows.On = _view.EdgeArrows;
        _pointerOff.On = !_view.LookPointer;
        _pointerJump.On = _view.LookPointer && !_view.LookFollow;
        _pointerFollow.On = _view.LookPointer && _view.LookFollow;
        _focus.On = _view.LookFocus;
        _focus.Enabled = _view.LookPointer;
        _dim.On = _view.Dim;
        _drift.On = _view.Follow;
        bool handsOn = _view.Hands && HandInput.Installed;
        _hands.On = handsOn;
        _hands.Text = handsOn ? "Hand gestures: on" : "Hand gestures: off";
        _break.On = _view.BreakMinutes > 0;
        _break.Text = _view.BreakMinutes > 0 ? $"Break reminder: every {_view.BreakMinutes} min" : "Break reminder: off";
        _bringNew.On = _view.BringNewWindows;
        ShowNewViewToggles();
    }

    string SourceLabel(ScreenSpec spec)
    {
        if (spec.Window is { } window) return "window · " + WindowFinder.FriendlyName(window.Program);
        if (spec.DisplayName is not { } id) return "test picture";
        DisplayInfo? d = _sources.FirstOrDefault(x => Same(x.Id, id));
        // Monitors of another desk are kept aside by the layout (Desks), so only an extra monitor that is not made
        // right now (plain screen mode removes them) can get here.
        if (d is null) return VirtualMonitors.IsVirtualId(id) ? "extra monitor · off for now" : "not connected";
        string size = spec.PixelWidth > 0 ? $"{spec.PixelWidth}×{spec.PixelHeight}" : $"{d.Bounds.Width}×{d.Bounds.Height}";
        return VirtualMonitors.IsVirtual(d) ? $"extra monitor · {size}" : $"monitor · {size}";
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
        if (hint.Length > 0) _tips.SetToolTip(pill, hint);
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
        string hint, ref int y, Action<float> changed, int width = InspectorWidth - 40)
    {
        var row = new SliderRow
        {
            Caption = caption, Min = min, Max = max, Step = step, ResetTo = reset, Decimals = decimals, Unit = unit,
            FromZero = fromZero, Hint = hint, Font = Font,
        };
        row.SetBounds(20, y, width, 34);
        row.Changed += changed;
        row.HintWanted += text => _hint.Text = text;
        row.MouseLeave += (_, _) => _hint.Text = _defaultHint;
        _tips.SetToolTip(row, hint);
        page.Controls.Add(row);
        y += 36;
        return row;
    }

    /// <summary>A small box asking for a name (a profile's, by default). Null if cancelled.</summary>
    string? AskName(string current, string title = "Profile name")
    {
        using var box = new Form
        {
            Text = title, Font = Font, BackColor = Ui.Inspector, ForeColor = Ui.Ink, Icon = AppIcon.Value,
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
        // Windows 11: a title bar in the app's own colour, instead of a white strip on a dark window.
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

/// <summary>A page with the aurora as its backdrop. Cards and labels on it show the aurora around their corners.</summary>
internal sealed class AuroraPanel : System.Windows.Forms.Panel
{
    public AuroraPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Ui.Back;
    }

    protected override void OnPaintBackground(PaintEventArgs e) => Ui.PaintAurora(e.Graphics, ClientRectangle);
}
