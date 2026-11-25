using System.Globalization;
using System.Numerics;

namespace XrShift.Tracking;

/// <summary>What glasses setup can change, as it is in use. A null mounting or gyro offset means none is saved.</summary>
public sealed record SetupValues(AxisMap? Axes, Vector3? GyroBias, float FovDeg, float LevelDeg, float LeanDeg);

/// <summary>Everything the glasses show during setup at one moment, as data: the app only turns it into pixels.</summary>
public sealed record SetupScreen(GuideDrawing Drawing, string Instruction, string Progress, string? Note, IReadOnlyList<string> Results);

/// <summary>
/// Glasses setup as the app runs it: first a check that the glasses are in their own Follow mode (nothing is measured;
/// they cannot say which mode they are in, and in Anchor their own steadying fights the app's), then
/// <see cref="GlassesSetup"/>. After each step it says in plain words what changed and gives the values to apply.
/// Not thread-safe: call from one thread, or under one lock.
/// </summary>
public sealed class SetupRun
{
    const float RadToDeg = 180f / MathF.PI;

    public const string ModeCheckText = "Turn your head. If this box stays in front of your eyes, press SPACE on the keyboard. If it stays behind in the room, click the X button on the glasses once and try again.";
    public const string FinishedText = "Setup is finished. Press Confirm to go back to your screens.";
    public const string RefusedNote = "Refused: nothing was changed. Backspace tries this step again, S skips it.";

    /// <summary>The mode check's box, fixed to the picture: it stays in front of the eyes only if the glasses are in Follow.</summary>
    public static GuideDrawing ModeBox { get; } = Box(320, 180);

    readonly GlassesSetup _setup;
    readonly List<string> _results = [];
    readonly HashSet<SetupStepKind> _applied = [];
    readonly string?[] _refusalSaid = new string?[4];
    string? _latest;

    /// <param name="before">The values in use now: what "was" means in the results, and what cancelling puts back.</param>
    public SetupRun(SetupOptions options, SetupValues before, bool checkMode = true)
    {
        _setup = new GlassesSetup(options);
        Before = Now = before;
        CheckingMode = checkMode;
    }

    public GlassesSetup Setup => _setup;
    /// <summary>The first screen, before anything is measured: are the glasses in Follow?</summary>
    public bool CheckingMode { get; private set; }
    public SetupValues Before { get; }
    /// <summary>The values with every finished step applied.</summary>
    public SetupValues Now { get; private set; }
    public bool Finished => !CheckingMode && _setup.Finished;
    /// <summary>One plain line per step that finished or was refused, in order.</summary>
    public IReadOnlyList<string> Results => _results;

    public static string Name(SetupStepKind step) => step switch
    {
        SetupStepKind.Rest => "Resting reading",
        SetupStepKind.Axes => "Sensor axes",
        SetupStepKind.LevelLean => "Level and lean",
        _ => "View angle",
    };

    /// <summary>rest, axes, level (or lean) and view, as the probe and the Control API write them.</summary>
    public static SetupStepKind? ParseStep(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "rest" => SetupStepKind.Rest,
        "axes" => SetupStepKind.Axes,
        "level" or "lean" => SetupStepKind.LevelLean,
        "view" => SetupStepKind.ViewAngle,
        _ => null,
    };

    bool Refused => !CheckingMode && !_setup.Finished && _setup.Waiting == SetupWait.Nothing;

    public void Add(ImuSample s)
    {
        if (!CheckingMode) _setup.Add(s);
    }

    /// <summary>
    /// Confirm: passes the mode check, or is the press of an edge step with the head at <paramref name="head"/>
    /// (the tracker's pose, not the display pose). False when nothing took it; <see cref="SetupScreen.Note"/> says why.
    /// </summary>
    public bool Confirm(Quaternion head)
    {
        if (!CheckingMode) return _setup.Press(head);
        CheckingMode = false;
        return true;
    }

    public void Retry()
    {
        if (CheckingMode) return;
        _refusalSaid[(int)_setup.Step] = null; // a refusal on the next try is news again
        _setup.Retry();
    }

    /// <summary>Leaves the mode check, or the step in hand, and goes on.</summary>
    public void Skip()
    {
        if (CheckingMode) CheckingMode = false;
        else _setup.Skip();
    }

    /// <summary>
    /// The values to apply now that steps have finished since the last call, or null if nothing new was measured.
    /// Adds a line to <see cref="Results"/> for every step that finished or was refused.
    /// </summary>
    public SetupValues? TakeChanges()
    {
        SetupValues was = Now;
        SetupResults r = _setup.Results;
        foreach (SetupStepKind step in Enum.GetValues<SetupStepKind>())
        {
            SetupStepStatus status = _setup.StatusOf(step);
            if (status == SetupStepStatus.Failed && _setup.FailureOf(step) is { } why && _refusalSaid[(int)step] != why)
            {
                _refusalSaid[(int)step] = why;
                _results.Add($"{Name(step)}: refused, nothing changed. {why}");
            }
            if (status != SetupStepStatus.Done || !_applied.Add(step)) continue;

            SetupValues before = Now;
            Now = step switch
            {
                SetupStepKind.Rest => Now with { GyroBias = r.GyroBias },
                SetupStepKind.Axes => Now with { Axes = r.Axes!.Map },
                SetupStepKind.LevelLean => Now with { LevelDeg = r.LevelLean!.LevelDeg, LeanDeg = r.LevelLean.LeanDeg },
                _ => Now with { FovDeg = r.ViewAngle!.FovDeg, LevelDeg = r.ViewAngle.LevelDeg, LeanDeg = r.ViewAngle.LeanDeg },
            };
            _latest = Describe(step, r, before);
            _results.Add(_latest);
        }
        return Now == was ? null : Now;
    }

    public SetupScreen Screen => new(
        CheckingMode ? ModeBox : _setup.Drawing,
        CheckingMode ? ModeCheckText : _setup.Finished ? FinishedText : _setup.Instruction,
        Progress,
        CheckingMode ? null : Refused ? RefusedNote : _setup.Note,
        // During the run only the newest finding, so the view stays clear; at the end all of them.
        Finished ? _results : _latest is { } latest ? [latest] : []);

    /// <summary>Where the run is, in a few words: "Level and lean: 3 of 6".</summary>
    public string Progress
    {
        get
        {
            if (CheckingMode) return "Before you start: the glasses' mode";
            if (_setup.Finished) return "Glasses setup: done";
            string name = Name(_setup.Step);
            return _setup.Waiting switch
            {
                SetupWait.Nothing => $"{name}: refused",
                SetupWait.DeskStill => $"{name}: hold still 5 seconds",
                SetupWait.Neutral => $"{name}: look straight ahead first",
                SetupWait.Hold => $"{name}: {(_setup.Progress < 0.5f ? 1 : 2)} of 2",
                _ => _setup.PressCount is var (taken, asked) ? $"{name}: {taken + 1} of {asked}" : name,
            };
        }
    }

    static string Describe(SetupStepKind step, SetupResults r, SetupValues was) => step switch
    {
        SetupStepKind.Rest => Invariant($"Resting gyro reading {Rates(r.GyroBias!.Value)} degrees a second, {(was.GyroBias is { } b ? $"was {Rates(b)}" : "none saved before")}."),
        SetupStepKind.Axes => Axes(r.Axes!),
        SetupStepKind.LevelLean => Invariant($"Level {r.LevelLean!.LevelDeg:F1} degrees, was {was.LevelDeg:F1}. Lean {r.LevelLean.LeanDeg:F1}, was {was.LeanDeg:F1}. {Agreement(r.LevelLean)}"),
        _ => Invariant($"View angle {r.ViewAngle!.FovDeg:F1} degrees, was {was.FovDeg:F1} (give or take {r.ViewAngle.FovErrorDeg:F1}). Level {r.ViewAngle.LevelDeg:F1}, was {was.LevelDeg:F1}. Lean {r.ViewAngle.LeanDeg:F1}, was {was.LeanDeg:F1}. {Agreement(r.ViewAngle)}"),
    };

    static string Axes(AxisCheckResult a) => float.IsNaN(a.AgreeDeg)
        ? Invariant($"Sensor axes measured from a nod of {a.PitchDeg:F0} degrees.")
        : Invariant($"Sensor axes measured from a nod of {a.PitchDeg:F0} degrees; the gyro agrees within {a.AgreeDeg:F1}.");

    /// <summary>The quality figure: how closely the presses agree with the fitted angles.</summary>
    static string Agreement(EdgeFit f) =>
        Invariant($"The {f.Used} presses agree within {f.RmsDeg:F1} degrees{(f.Rejected.Count > 0 ? $", {f.Rejected.Count} left out" : "")}.");

    static string Rates(Vector3 radPerSecond) =>
        Invariant($"{radPerSecond.X * RadToDeg:F2} {radPerSecond.Y * RadToDeg:F2} {radPerSecond.Z * RadToDeg:F2}");

    static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    static GuideDrawing Box(float halfWidth, float halfHeight)
    {
        Vector2 a = new(-halfWidth, halfHeight), b = new(halfWidth, halfHeight), c = new(halfWidth, -halfHeight), d = new(-halfWidth, -halfHeight);
        return new GuideDrawing(
            [new(GuideSpace.Display, a, b), new(GuideSpace.Display, b, c), new(GuideSpace.Display, c, d), new(GuideSpace.Display, d, a)], []);
    }
}
