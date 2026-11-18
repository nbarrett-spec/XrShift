using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using XrShift.Scene;

namespace XrShift.App;

/// <summary>An open window that could be pinned as a floating screen.</summary>
internal sealed record WindowInfo(IntPtr Handle, string Program, string Title, int Width, int Height)
{
    /// <summary>
    /// What to call it in a list: the program first, because that is what people look for ("Teams"), then
    /// the title, which for many programs is only the page or tab that happens to be open.
    /// </summary>
    public string Label
    {
        get
        {
            string name = WindowFinder.FriendlyName(Program);
            string title = Title.Length > 54 ? Title[..51] + "..." : Title;
            return string.Equals(title, name, StringComparison.OrdinalIgnoreCase) ? name : $"{name}  ·  {title}";
        }
    }
}

/// <summary>Lists the windows on the desktop and finds one again by program and title.</summary>
internal static class WindowFinder
{
    /// <summary>Ordinary application windows: visible, titled, not tool windows, not this app's own.</summary>
    public static List<WindowInfo> List()
    {
        var result = new List<WindowInfo>();
        int self = Environment.ProcessId;
        EnumWindows((handle, _) =>
        {
            // The same rule as Alt+Tab: visible, not a tool window, and either unowned or marked as an app window.
            if (!IsWindowVisible(handle)) return true;
            long exStyle = GetWindowLongPtr(handle, -20 /* extended style */).ToInt64();
            if ((exStyle & 0x80 /* tool window */) != 0) return true;
            if (GetWindow(handle, 4 /* owner */) != IntPtr.Zero && (exStyle & 0x40000 /* app window */) == 0) return true;
            // Store apps keep hidden "cloaked" windows around; they are not really on screen.
            if (DwmGetWindowAttribute(handle, 14 /* cloaked */, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;

            int length = GetWindowTextLength(handle);
            if (length == 0) return true;
            var title = new StringBuilder(length + 1);
            GetWindowText(handle, title, title.Capacity);

            GetWindowThreadProcessId(handle, out uint pid);
            if (pid == self) return true;
            if (ProgramOf(pid) is not { } program) return true; // gone already

            // A minimised window reports a tiny stub of itself; its real size is the one it will come back to.
            RECT r;
            if (IsIconic(handle))
            {
                var placement = new WINDOWPLACEMENT { Length = Marshal.SizeOf<WINDOWPLACEMENT>() };
                if (!GetWindowPlacement(handle, ref placement)) return true;
                r = placement.Normal;
            }
            else if (!GetWindowRect(handle, out r)) return true;
            int width = r.Right - r.Left, height = r.Bottom - r.Top;
            if (width < 120 || height < 80) return true;

            result.Add(new WindowInfo(handle, program, title.ToString(), width, height));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    /// <summary>
    /// Finds a pinned window again. The program must match; among its windows the one with the same title
    /// wins, then the one whose title ends the same way (programs put their own name last and the
    /// changing part first), then simply the largest.
    /// </summary>
    public static WindowInfo? Find(string program, string title, IReadOnlyList<WindowInfo>? among = null)
    {
        List<WindowInfo> candidates = (among ?? List())
            .Where(w => string.Equals(w.Program, program, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (candidates.Count <= 1) return candidates.FirstOrDefault();

        return candidates.FirstOrDefault(w => string.Equals(w.Title, title, StringComparison.Ordinal))
            ?? candidates.OrderByDescending(w => CommonEnding(w.Title, title)).ThenByDescending(w => (long)w.Width * w.Height).First();
    }

    /// <summary>
    /// Which of a layout's pinned windows are open right now: source to window handle. Handles already
    /// known are kept while their window lives. Looking for the others means listing every window on the
    /// desktop, which is slow enough to skip while a slider is being dragged: pass <paramref name="search"/> false.
    /// Returns <paramref name="previous"/> itself when nothing changed.
    /// </summary>
    public static IReadOnlyDictionary<string, IntPtr> Resolve(Layout layout, IReadOnlyDictionary<string, IntPtr> previous, bool search)
    {
        var map = new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);
        List<WindowInfo>? all = null;
        foreach (ScreenSpec spec in layout.Screens)
        {
            if (spec.Window is not { } window || map.ContainsKey(spec.Source)) continue;
            if (previous.TryGetValue(spec.Source, out IntPtr known) && StillOpen(known))
            {
                map[spec.Source] = known;
                continue;
            }
            if (!search) continue;
            all ??= List();
            if (Find(window.Program, window.Title, all) is { } found) map[spec.Source] = found.Handle;
        }
        bool same = map.Count == previous.Count && map.All(kv => previous.TryGetValue(kv.Key, out IntPtr handle) && handle == kv.Value);
        return same ? previous : map;
    }

    /// <summary>A short name for a screen that shows this program's window.</summary>
    public static string FriendlyName(string program) =>
        program.Contains("teams", StringComparison.OrdinalIgnoreCase) ? "Teams"
        : program.Length == 0 ? "Window"
        : char.ToUpperInvariant(program[0]) + program[1..];

    public static bool StillOpen(IntPtr handle) => IsWindow(handle);

    static readonly Dictionary<uint, string> s_programs = [];

    /// <summary>
    /// A process's program name, e.g. "ms-teams". Asks for the least access Windows has, which also works for
    /// programs running as administrator (Task Manager); and remembers the answer, because asking the usual
    /// way lists every process on the PC each time and made the list slow enough to stutter the mouse.
    /// </summary>
    static string? ProgramOf(uint pid)
    {
        lock (s_programs)
        {
            if (s_programs.TryGetValue(pid, out string? known)) return known;
        }
        IntPtr process = OpenProcess(0x1000 /* query limited information */, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            var path = new StringBuilder(1024);
            int size = path.Capacity;
            if (!QueryFullProcessImageName(process, 0, path, ref size)) return null;
            string name = Path.GetFileNameWithoutExtension(path.ToString());
            lock (s_programs)
            {
                if (s_programs.Count > 2000) s_programs.Clear(); // process numbers get reused; start afresh now and then
                s_programs[pid] = name;
            }
            return name;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    /// <summary>Brings a minimised window back to the size it had, without giving it the keyboard.</summary>
    public static void Restore(IntPtr handle)
    {
        if (IsIconic(handle)) ShowWindow(handle, 4 /* show, not activated */);
    }

    /// <summary>Where a window is on the desktop, as it is drawn (without the invisible border Windows adds for resizing).</summary>
    public static System.Drawing.Rectangle? Bounds(IntPtr handle)
    {
        if (DwmGetWindowAttribute(handle, 9 /* extended frame bounds */, out RECT r, Marshal.SizeOf<RECT>()) != 0 && !GetWindowRect(handle, out r))
            return null;
        return System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }

    /// <summary>The window that has the keyboard now.</summary>
    public static IntPtr Foreground() => GetForegroundWindow();

    /// <summary>The application window at a point of the desktop, or zero if only the desktop itself or the taskbar is there.</summary>
    public static IntPtr TopLevelAt(System.Drawing.Point point)
    {
        IntPtr root = GetAncestor(WindowFromPoint(new POINT { X = point.X, Y = point.Y }), 2 /* root */);
        if (root == IntPtr.Zero) return IntPtr.Zero;
        var name = new StringBuilder(64);
        GetClassName(root, name, name.Capacity);
        return name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" ? IntPtr.Zero : root;
    }

    /// <summary>
    /// Gives a window the keyboard and brings it to the front. Windows only lets the program in front do that,
    /// so this borrows the front program's say for a moment. If <paramref name="handle"/> is zero the desktop gets it.
    /// </summary>
    public static void Activate(IntPtr handle)
    {
        if (handle == IntPtr.Zero) handle = GetShellWindow();
        IntPtr front = GetForegroundWindow();
        if (handle == IntPtr.Zero || handle == front) return;
        uint mine = GetCurrentThreadId(), theirs = front == IntPtr.Zero ? 0 : GetWindowThreadProcessId(front, out _);
        bool joined = theirs != 0 && theirs != mine && AttachThreadInput(mine, theirs, true);
        try
        {
            SetForegroundWindow(handle);
        }
        finally
        {
            if (joined) AttachThreadInput(mine, theirs, false);
        }
    }

    /// <summary>
    /// Moves every application window whose middle lies in <paramref name="from"/> into <paramref name="to"/>,
    /// keeping its size where it fits and its state (a maximised window comes back maximised, a minimised one
    /// stays minimised). Returns how many were moved.
    /// </summary>
    public static int MoveAll(System.Drawing.Rectangle from, System.Drawing.Rectangle to)
    {
        int moved = 0;
        foreach (WindowInfo w in List())
        {
            var placement = new WINDOWPLACEMENT { Length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (!GetWindowPlacement(w.Handle, ref placement)) continue;
            bool iconic = IsIconic(w.Handle), zoomed = IsZoomed(w.Handle);
            // Where it is now: a minimised or maximised window counts where it will come back to.
            System.Drawing.Rectangle at = !iconic && !zoomed && Bounds(w.Handle) is { } b ? b
                : System.Drawing.Rectangle.FromLTRB(placement.Normal.Left, placement.Normal.Top, placement.Normal.Right, placement.Normal.Bottom);
            if (zoomed && Bounds(w.Handle) is { } z) at = z;
            if (!from.Contains(new System.Drawing.Point(at.Left + at.Width / 2, at.Top + at.Height / 2))) continue;

            RECT n = placement.Normal;
            int width = Math.Min(n.Right - n.Left, to.Width), height = Math.Min(n.Bottom - n.Top, to.Height);
            int left = to.Left + Math.Clamp(n.Left - from.Left, 0, Math.Max(0, to.Width - width));
            int top = to.Top + Math.Clamp(n.Top - from.Top, 0, Math.Max(0, to.Height - height));
            placement.Normal = new RECT { Left = left, Top = top, Right = left + width, Bottom = top + height };
            placement.ShowCommand = iconic ? 7 /* minimised, not activated */ : zoomed ? 3 /* maximised */ : 4 /* normal, not activated */;
            placement.Flags = 0;
            if (SetWindowPlacement(w.Handle, ref placement)) moved++;
        }
        return moved;
    }

    /// <summary>
    /// Keeps a pinned window drawing. Teams, Chrome, Edge and other Chromium-based programs stop drawing while
    /// other windows cover them completely, to save power, so their floating screen froze until clicked. Under
    /// XrShift's picture on the glasses they count as uncovered (the scene is a tool window, which such programs
    /// ignore), so the window is moved to the edge of <paramref name="home"/> that touches the glasses' area
    /// <paramref name="glasses"/>, with a strip of <paramref name="strip"/> pixels reaching under the glasses'
    /// picture. Leaves alone a window that already reaches there, a maximised or full-screen one (games) and a
    /// minimised one. Returns true if it moved the window.
    /// </summary>
    public static bool ReachUnder(IntPtr handle, System.Drawing.Rectangle home, System.Drawing.Rectangle glasses, int strip = 12)
    {
        if (!IsWindow(handle) || IsIconic(handle) || IsZoomed(handle)) return false;
        if (Bounds(handle) is not { } b || !GetWindowRect(handle, out RECT outer)) return false;
        if (b.Width >= home.Width && b.Height >= home.Height) return false; // full screen: a game or a presentation
        var under = System.Drawing.Rectangle.Intersect(b, glasses);
        if (under.Width >= strip / 2 && under.Height >= strip / 2) return false;

        int w = b.Width, h = b.Height, x, y;
        static int Fit(int want, int low, int high) => high < low ? int.MinValue : Math.Clamp(want, low, high);
        if (glasses.Left == home.Right || glasses.Right == home.Left)
        {
            x = glasses.Left == home.Right ? home.Right - w + strip : home.Left - strip;
            y = Fit(b.Y, Math.Max(home.Top, glasses.Top - h + strip), Math.Min(home.Bottom - h, glasses.Bottom - strip));
            if (y == int.MinValue) y = Fit(b.Y, glasses.Top - h + strip, glasses.Bottom - strip);
        }
        else if (glasses.Top == home.Bottom || glasses.Bottom == home.Top)
        {
            y = glasses.Top == home.Bottom ? home.Bottom - h + strip : home.Top - strip;
            x = Fit(b.X, Math.Max(home.Left, glasses.Left - w + strip), Math.Min(home.Right - w, glasses.Right - strip));
            if (x == int.MinValue) x = Fit(b.X, glasses.Left - w + strip, glasses.Right - strip);
        }
        else return false; // the glasses' area does not touch this screen
        if (x == int.MinValue || y == int.MinValue) return false;

        // The invisible resize border sits outside what is drawn: move the outer rectangle by the same amount.
        int dx = x - b.X, dy = y - b.Y;
        return SetWindowPos(handle, IntPtr.Zero, outer.Left + dx, outer.Top + dy, 0, 0,
            0x0001 /* keep size */ | 0x0004 /* keep z-order */ | 0x0010 /* do not activate */);
    }

    /// <summary>True while a window is shrunk to the taskbar. Windows stops drawing it then, so a copy of it freezes.</summary>
    public static bool Minimized(IntPtr handle) => IsIconic(handle);

    static int CommonEnding(string a, string b)
    {
        int n = 0;
        while (n < a.Length && n < b.Length && a[a.Length - 1 - n] == b[b.Length - 1 - n]) n++;
        return n;
    }

    delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct WINDOWPLACEMENT
    {
        public int Length, Flags, ShowCommand;
        public POINT Min, Max;
        public RECT Normal;
    }

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr handle);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr handle);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr handle, uint relation);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr handle, StringBuilder text, int capacity);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr handle, out RECT rect);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out int value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out RECT value, int size);
    [DllImport("user32.dll")] static extern bool GetWindowPlacement(IntPtr handle, ref WINDOWPLACEMENT placement);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr handle, int command);
    [DllImport("user32.dll")] static extern bool SetWindowPlacement(IntPtr handle, ref WINDOWPLACEMENT placement);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr handle);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr handle, uint which);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr handle, StringBuilder name, int capacity);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
}
