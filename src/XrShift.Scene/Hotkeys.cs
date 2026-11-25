namespace XrShift.Scene;

/// <summary>
/// Every Ctrl+Alt key the app takes, and what it does: the one list both the key registration and the settings
/// window's key reference are made from, so the two cannot drift apart.
/// </summary>
public static class Hotkeys
{
    /// <param name="Code">The Windows virtual-key code (the same number as WinForms' Keys).</param>
    /// <param name="Name">The key as printed after Ctrl+Alt+.</param>
    /// <param name="What">What it does. Keys that share this text are shown on one line.</param>
    /// <param name="Recentre">One of the recentre keys: they are tried in turn, as other programs often hold one.</param>
    public sealed record Key(int Code, string Name, string What, bool Recentre = false);

    public static readonly IReadOnlyList<Key> All =
    [
        new(0x30, "0", "Recentre: the way you face now becomes straight ahead", Recentre: true),
        new(0x24, "Home", "Recentre: the way you face now becomes straight ahead", Recentre: true),
        new(0x20, "Space", "Recentre: the way you face now becomes straight ahead", Recentre: true),
        new(0x53, "S", "Open this settings window"),
        new(0x4D, "M", "Put the mouse pointer where you are looking"),
        new(0x57, "W", "Move the window in front to the screen you are looking at"),
        new(0x4C, "L", "Look to point: off, jump, follow"),
        new(0x50, "P", "Pick up the screen you look at; again to set it down"),
        new(0x42, "B", "Hide or show every screen"),
        new(0x21, "PgUp", "Every screen bigger / smaller, or only the screen you carry"),
        new(0x22, "PgDn", "Every screen bigger / smaller, or only the screen you carry"),
        new(0x31, "1", "Switch to profile 1, 2, 3 or 4"),
        new(0x32, "2", "Switch to profile 1, 2, 3 or 4"),
        new(0x33, "3", "Switch to profile 1, 2, 3 or 4"),
        new(0x34, "4", "Switch to profile 1, 2, 3 or 4"),
        new(0x4F, "O", "Open the bot you look at (or the one waiting for you)"),
        new(0x54, "T", "Talk: say one command"),
        new(0x4B, "K", "Hand gestures on or off"),
        new(0x44, "D", "Sharp or 3D (one picture per eye)"),
        new(0x48, "H", "Show or hide the status line in the glasses"),
        new(0x47, "G", "Plain screen (game mode) on or off"),
        new(0x56, "V", "VR on or off"),
        new(0x43, "C", "Glasses setup, guided in the glasses: then Space confirms, Backspace retries, S skips, Esc cancels"),
        new(0xDB, "[", "Glasses delay down / up"),
        new(0xDD, "]", "Glasses delay down / up"),
        new(0xBD, "-", "View angle smaller / bigger"),
        new(0xBB, "=", "View angle smaller / bigger"),
        new(0x51, "Q", "Quit XrShift"),
    ];

    /// <summary>The key that does <paramref name="code"/>'s job, written out: "Ctrl+Alt+M".</summary>
    public static string Text(int code) => All.FirstOrDefault(k => k.Code == code) is { } key ? "Ctrl+Alt+" + key.Name : "";

    /// <summary>The reference, one line per job in the order above: ("Ctrl+Alt+PgUp / PgDn", "Every screen bigger / smaller ...").</summary>
    public static IReadOnlyList<(string Keys, string What)> Reference() =>
        All.GroupBy(k => k.What).Select(g => ("Ctrl+Alt+" + string.Join(" / ", g.Select(k => k.Name)), g.Key)).ToList();
}
