namespace XrShift.Scene;

/// <summary>What is known about one monitor Windows reports (never the glasses).</summary>
/// <param name="Id">Its stable device path, as screens remember it.</param>
/// <param name="ModelName">The name the monitor gives itself ("LG SMART WQHD"), or null when it gives none.</param>
/// <param name="Virtual">Made by the virtual display driver, not a monitor on the desk.</param>
public readonly record struct MonitorFacts(string Id, string? ModelName, bool Virtual, bool Primary, int Width, int Height);

/// <summary>The arrangement remembered for a desk the wearer is not at right now.</summary>
public sealed record DeskLayout
{
    /// <summary>Which desk: see <see cref="Desks.KeyOf"/>. Empty for screens put aside from a file saved before desks were remembered.</summary>
    public string Key { get; init; } = "";
    public IReadOnlyList<ScreenSpec> Screens { get; init; } = [];
}

/// <summary>
/// A desk is the set of real monitors present (not virtual ones, not the glasses). Each desk keeps its own
/// arrangement of screens within a profile, so moving between two desks never piles one's screens onto the other's.
/// </summary>
public static class Desks
{
    /// <summary>
    /// Which desk these real monitors make. Built from the hardware code in each id (BOE0B8B, GSM7817) rather than
    /// the whole path, because the laptop panel's path changes when it moves to the other graphics card.
    /// Two monitors of the same model at two desks therefore count as the same desk.
    /// </summary>
    public static string KeyOf(IEnumerable<string> realMonitorIds) =>
        string.Join("+", realMonitorIds.Select(id => (Hardware(id) ?? id).ToUpperInvariant()).Order(StringComparer.Ordinal));

    /// <summary>The hardware code in a Windows device path (<c>\\?\DISPLAY#GSM7817#...</c>), or null if there is none.</summary>
    public static string? Hardware(string id)
    {
        string[] parts = id.Split('#');
        return parts.Length > 2 ? parts[1] : null;
    }

    /// <summary>
    /// Brings a layout up to date with the monitors present: switches to this desk's own arrangement, gives every
    /// monitor in <paramref name="addable"/> a screen and places new ones within reach. Nothing is ever deleted: the
    /// screens of other desks are kept in <see cref="Layout.Desks"/>. Returns the same layout object when nothing changed,
    /// and the names of screens that were added hidden because there was no room for them.
    /// </summary>
    /// <param name="present">Every monitor connected now, which tells the desk. With no real monitor among them the desk is left as it is.</param>
    /// <param name="addable">The monitors that should have a screen.</param>
    /// <param name="isVirtualId">Whether a monitor id (that may not be connected now) is a virtual monitor's.</param>
    public static (Layout Layout, IReadOnlyList<string> Hidden) Fit(Layout layout, IReadOnlyList<MonitorFacts> present,
        IReadOnlyList<MonitorFacts> addable, DisplayOptics optics, Func<string, bool> isVirtualId, float reachDeg = 60f, int maxShown = 5)
    {
        List<string> ids = present.Concat(addable).Select(m => m.Id).ToList();
        layout = Reconnect(layout, ids);
        List<ScreenSpec> carried = [];
        List<string> real = present.Where(m => !m.Virtual).Select(m => m.Id).ToList();
        if (real.Count > 0)
        {
            layout = Arrive(layout, KeyOf(real), real, isVirtualId, out carried);
            layout = Reconnect(layout, ids);
        }

        var sizes = new Dictionary<string, (int Width, int Height)>(StringComparer.OrdinalIgnoreCase);
        foreach (MonitorFacts m in present.Concat(addable)) sizes[m.Id] = (m.Width, m.Height);
        (int Width, int Height) SizeOf(ScreenSpec s) => s.DisplayName is { } id && sizes.TryGetValue(id, out var size) ? size : (1920, 1080);

        var hidden = new List<string>();
        void Put(ScreenSpec screen)
        {
            (int w, int h) = SizeOf(screen);
            (ScreenSpec placed, bool shown) = layout.PlaceInReach(screen, w, h, optics, SizeOf, reachDeg, maxShown);
            layout = layout.Add(placed);
            if (!shown && screen.Visible) hidden.Add(placed.Name);
        }
        void AddFor(MonitorFacts m)
        {
            if (layout.Shows(m.Id)) return;
            Put(new ScreenSpec
            {
                Name = ScreenNames.For(m, layout.Screens.Select(s => s.Name)),
                Source = ScreenSpec.DisplaySource(m.Id),
                Scale = ScreenSpec.ScaleToFit(m.Height, optics),
            });
        }

        // The desk's own monitors first, main one first, so they take the middle; what came along from the last desk
        // (virtual monitors, pinned windows) goes around them, and brand-new virtual monitors last.
        foreach (MonitorFacts m in addable.Where(m => !m.Virtual).OrderByDescending(m => m.Primary)) AddFor(m);
        foreach (ScreenSpec s in carried) Put(s);
        foreach (MonitorFacts m in addable.Where(m => m.Virtual)) AddFor(m);
        return (layout, hidden);
    }

    /// <summary>
    /// The screens of <paramref name="after"/> that are really new: in neither <paramref name="before"/>'s screens
    /// nor the arrangements it keeps for other desks. Screens brought back by a change of desk are not new.
    /// </summary>
    public static List<ScreenSpec> Added(Layout before, Layout after) =>
        after.Screens.Where(s => before.Find(s.Id) is null && !before.Desks.Any(d => d.Screens.Any(k => k.Id == s.Id))).ToList();

    /// <summary>
    /// Swaps in the arrangement of the desk <paramref name="key"/>. At a desk not seen before, the screens of monitors
    /// it shares with the last desk keep their places, and the rest that are not tied to a desk (virtual monitors,
    /// windows) come back in <paramref name="carried"/> to be placed again around the new desk's monitors.
    /// </summary>
    static Layout Arrive(Layout layout, string key, IReadOnlyList<string> realHere, Func<string, bool> isVirtualId, out List<ScreenSpec> carried)
    {
        carried = [];
        if (layout.Desk == key) return layout;
        var here = new HashSet<string>(realHere, StringComparer.OrdinalIgnoreCase);
        bool OfDesk(ScreenSpec s) => s.DisplayName is { } id && !isVirtualId(id);
        bool Away(ScreenSpec s) => OfDesk(s) && !here.Contains(s.DisplayName!);

        if (layout.Desk is null)
        {
            // Saved before desks were remembered: the arrangement becomes this desk's, and screens of monitors not here
            // are put aside, not deleted.
            List<ScreenSpec> away = layout.Screens.Where(Away).ToList();
            if (away.Count == 0) return layout with { Desk = key };
            List<ScreenSpec> aside = [.. layout.Desks.Where(d => d.Key.Length == 0).SelectMany(d => d.Screens), .. away];
            return layout with
            {
                Desk = key,
                Screens = layout.Screens.Where(s => !Away(s)).ToList(),
                Desks = [.. layout.Desks.Where(d => d.Key.Length > 0), new DeskLayout { Screens = aside }],
            };
        }

        List<DeskLayout> others = [.. layout.Desks.Where(d => d.Key != layout.Desk && d.Key != key), new DeskLayout { Key = layout.Desk, Screens = layout.Screens }];
        if (layout.Desks.FirstOrDefault(d => d.Key == key) is { } known)
            return layout with { Desk = key, Screens = known.Screens, Desks = others };
        carried = layout.Screens.Where(s => !OfDesk(s)).ToList();
        return layout with { Desk = key, Screens = layout.Screens.Where(s => OfDesk(s) && !Away(s)).ToList(), Desks = others };
    }

    /// <summary>
    /// A monitor's Windows path changes when it moves to another graphics card (the laptop panel does with
    /// Advanced Optimus). A screen whose monitor is missing is pointed at the connected monitor with the same
    /// hardware id, so it keeps its place instead of showing "not connected" beside a new copy.
    /// </summary>
    public static Layout Reconnect(Layout layout, IReadOnlyList<string> present)
    {
        foreach (ScreenSpec spec in layout.Screens.ToList())
        {
            if (spec.DisplayName is not { } id || present.Any(p => string.Equals(p, id, StringComparison.OrdinalIgnoreCase))) continue;
            if (Hardware(id) is not { } hardware) continue;
            string? same = present.FirstOrDefault(p => string.Equals(Hardware(p), hardware, StringComparison.OrdinalIgnoreCase) && !layout.Shows(p));
            if (same is not null) layout = layout.With(spec with { Source = ScreenSpec.DisplaySource(same) });
        }
        return layout;
    }
}

/// <summary>Names for new screens that say which monitor they show.</summary>
public static class ScreenNames
{
    /// <summary>
    /// The monitor's own model name when it gives one, "Laptop" for a built-in panel, "Extra 1", "Extra 2" for virtual
    /// monitors, "Monitor" when nothing is known; with a number added when the name is <paramref name="taken"/>.
    /// </summary>
    public static string For(MonitorFacts monitor, IEnumerable<string> taken)
    {
        var used = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        string name = monitor.Virtual ? "Extra"
            : !string.IsNullOrWhiteSpace(monitor.ModelName) ? monitor.ModelName.Trim()
            : IsBuiltIn(monitor) ? "Laptop"
            : "Monitor";
        if (!monitor.Virtual && !used.Contains(name)) return name;
        for (int n = monitor.Virtual ? 1 : 2; ; n++)
            if (!used.Contains($"{name} {n}")) return $"{name} {n}";
    }

    /// <summary>
    /// A laptop's own panel. Windows does not say so directly in what the app keeps, but laptop panels carry no model
    /// name, where nearly every external monitor does; a monitor Windows gave no device path for is not counted.
    /// shortcut: an external monitor that gives no name is called "Laptop" too; read the connection type
    /// (DISPLAYCONFIG outputTechnology INTERNAL) in Displays.cs if that turns up.
    /// </summary>
    public static bool IsBuiltIn(MonitorFacts monitor) =>
        !monitor.Virtual && string.IsNullOrWhiteSpace(monitor.ModelName) && monitor.Id.StartsWith(@"\\?\", StringComparison.Ordinal);
}
