using System.Numerics;

namespace XrShift.Tracking;

// Level, lean and view angle are all measured the same way: the app draws an upright line fixed in the picture,
// the wearer lays it over a real upright edge (door frame, window side, wall corner) by moving their head, and
// presses. How a true vertical looks at a spot in the picture depends only on the head's tilt, which gravity
// measures exactly, and on the display's own angles, which are what is being measured:
//  - lean turns every upright line by the same amount;
//  - level and view angle show at the sides of the picture, where verticals lean in as the head looks up or down
//    (the keystone a camera shows when tipped back). How far they lean per degree of look depends on the view angle.
// Direction is the same from any eye position, so unlike lining up a mark on a point in the room, nothing depends
// on how far away the edge is or on the eye moving as the head turns, and heading drift does not enter at all.

/// <summary>How the head should be held for one press.</summary>
public enum HeadPosture { Level, Up, Down }

/// <summary>One line to lay on an upright edge: where it is in the picture and how it is drawn.</summary>
/// <param name="XPx">Pixels right of the picture's middle (negative = left).</param>
/// <param name="DrawnTiltDeg">How the line is drawn: degrees from upright, + = top leans right.</param>
public readonly record struct EdgeTarget(HeadPosture Posture, float XPx, float DrawnTiltDeg);

/// <summary>The wearer said line and edge lie together, with the head here (tracker pose, not the display pose).</summary>
public readonly record struct EdgePress(EdgeTarget Target, Quaternion Head);

/// <summary>Display angles fitted to the presses. Errors are one standard deviation; NaN when that angle was held fixed.</summary>
/// <param name="LevelDeg">The app's "level" trim (+ moves everything up).</param>
/// <param name="LeanDeg">The app's "lean" trim (+ turns everything clockwise).</param>
/// <param name="RmsDeg">How far the presses disagree with the fit, degrees of line tilt.</param>
/// <param name="Rejected">Presses left out as not fitting the others (pressed too early, wrong edge).</param>
public sealed record EdgeFit(
    float FovDeg, float LevelDeg, float LeanDeg,
    float FovErrorDeg, float LevelErrorDeg, float LeanErrorDeg,
    float RmsDeg, int Used, IReadOnlyList<EdgePress> Rejected);

/// <summary>The maths: how an upright edge looks in the picture, and the least-squares fit.</summary>
public static class EdgeFitter
{
    const float DegToRad = MathF.PI / 180f;
    const float RadToDeg = 180f / MathF.PI;

    /// <summary>A press further off than this (degrees) and three times the typical miss is left out.</summary>
    const double OutlierDeg = 1.5;
    /// <summary>Presses further off than this count for less in the first fit.</summary>
    const double HuberDeg = 1.0;
    /// <summary>
    /// Nobody lays a line on an edge better than this, so the error figures never claim more even when
    /// the presses happen to agree perfectly.
    /// </summary>
    const double JudgementDeg = 0.2;

    /// <summary>The app's trim rotation, display axes into head axes, exactly as the app builds it.</summary>
    public static Quaternion Trim(float levelDeg, float leanDeg) =>
        Quaternion.CreateFromYawPitchRoll(0, -levelDeg * DegToRad, leanDeg * DegToRad);

    /// <summary>
    /// How a true vertical looks at <paramref name="xPx"/> on the picture's middle row: degrees from upright, + = top leans right.
    /// </summary>
    /// <param name="head">Head axes (X right, Y up, Z back) into the Y-up world, as <see cref="HeadPose.Orientation"/>.</param>
    public static float VerticalTiltDeg(Quaternion head, float xPx, float fovDeg, float levelDeg, float leanDeg, int widthPx)
    {
        Vector3 up = Vector3.Transform(Vector3.UnitY, Quaternion.Conjugate(head * Trim(levelDeg, leanDeg)));
        float focal = widthPx / 2f / MathF.Tan(fovDeg * DegToRad / 2f);
        // A vertical through (x, 0, -f) heads, in the picture, along (up.X + x·up.Z/f, up.Y).
        return MathF.Atan2(up.X + xPx * up.Z / focal, up.Y) * RadToDeg;
    }

    /// <summary>
    /// Fits level, lean and (if <paramref name="fitFov"/>) the view angle, leaving out presses that do not fit.
    /// Null if the presses cannot pin the angles down at all.
    /// </summary>
    public static EdgeFit? Fit(IReadOnlyList<EdgePress> presses, float fovDeg, int widthPx, bool fitFov)
    {
        int[] free = fitFov ? [0, 1, 2] : [1, 2];
        if (presses.Count < free.Length + 1) return null;

        // First a fit that gives way to presses far off (Huber weights), so a few bad ones cannot drag it
        // and hide among the good; then leave out everything well off that fit, and fit the rest plainly.
        var all = new List<EdgePress>(presses);
        double[] p = [fovDeg, 0, 0];
        var weights = Enumerable.Repeat(1.0, all.Count).ToArray();
        for (int round = 0; round < 8; round++)
        {
            if (!Solve(all, weights, p, free, widthPx, out _)) return null;
            double[] miss = Residuals(all, p, widthPx);
            for (int i = 0; i < miss.Length; i++) weights[i] = Math.Min(1, HuberDeg / Math.Max(1e-9, Math.Abs(miss[i])));
        }

        double[] r = Residuals(all, p, widthPx);
        double typical = Math.Max(JudgementDeg, 1.4826 * Median(r.Select(Math.Abs)));
        double limit = Math.Max(OutlierDeg, 3 * typical);
        int keep = free.Length + 2;
        var order = Enumerable.Range(0, all.Count).OrderBy(i => Math.Abs(r[i])).ToList();
        var used = new List<EdgePress>();
        var rejected = new List<EdgePress>();
        foreach (int i in order)
            (Math.Abs(r[i]) <= limit || used.Count < keep ? used : rejected).Add(all[i]);

        p = [fovDeg, 0, 0];
        if (!Solve(used, Enumerable.Repeat(1.0, used.Count).ToArray(), p, free, widthPx, out double[,] normal)) return null;
        double sumSquares = Residuals(used, p, widthPx).Sum(x => x * x);
        double sigma = Math.Max(JudgementDeg, Math.Sqrt(sumSquares / Math.Max(1, used.Count - free.Length)));
        double[] error = [double.NaN, double.NaN, double.NaN];
        if (!TryInvert(normal, out double[,] inverse)) return null;
        for (int i = 0; i < free.Length; i++) error[free[i]] = sigma * Math.Sqrt(inverse[i, i]);

        return new EdgeFit((float)p[0], (float)p[1], (float)p[2], (float)error[0], (float)error[1], (float)error[2],
            (float)Math.Sqrt(sumSquares / used.Count), used.Count, rejected);
    }

    static double Median(IEnumerable<double> values)
    {
        double[] v = values.Order().ToArray();
        return v.Length % 2 == 1 ? v[v.Length / 2] : (v[v.Length / 2 - 1] + v[v.Length / 2]) / 2;
    }

    /// <summary>How far (degrees) a press is from what the fitted angles say it should have been.</summary>
    public static float MissDeg(EdgePress press, EdgeFit fit, int widthPx) =>
        (float)Residuals([press], [fit.FovDeg, fit.LevelDeg, fit.LeanDeg], widthPx)[0];

    static double[] Residuals(List<EdgePress> presses, double[] p, int widthPx)
    {
        var r = new double[presses.Count];
        for (int i = 0; i < r.Length; i++)
        {
            EdgePress press = presses[i];
            r[i] = Wrap(VerticalTiltDeg(press.Head, press.Target.XPx, (float)p[0], (float)p[1], (float)p[2], widthPx) - press.Target.DrawnTiltDeg);
        }
        return r;
    }

    /// <summary>Gauss-Newton on the free angles, in place. <paramref name="normal"/> is JᵀJ at the answer, for the error figures.</summary>
    static bool Solve(List<EdgePress> presses, double[] weights, double[] p, int[] free, int widthPx, out double[,] normal)
    {
        const double Step = 0.01; // degrees, for the slopes
        int k = free.Length;
        normal = new double[k, k];
        for (int iteration = 0; iteration < 30; iteration++)
        {
            double[] r = Residuals(presses, p, widthPx);
            var jacobian = new double[r.Length, k];
            for (int j = 0; j < k; j++)
            {
                double keep = p[free[j]];
                p[free[j]] = keep + Step;
                double[] plus = Residuals(presses, p, widthPx);
                p[free[j]] = keep - Step;
                double[] minus = Residuals(presses, p, widthPx);
                p[free[j]] = keep;
                for (int i = 0; i < r.Length; i++) jacobian[i, j] = (plus[i] - minus[i]) / (2 * Step);
            }

            var gradient = new double[k];
            normal = new double[k, k];
            for (int i = 0; i < r.Length; i++)
                for (int a = 0; a < k; a++)
                {
                    gradient[a] -= weights[i] * jacobian[i, a] * r[i];
                    for (int b = 0; b < k; b++) normal[a, b] += weights[i] * jacobian[i, a] * jacobian[i, b];
                }

            if (!TryInvert(normal, out double[,] inverse)) return false;
            double largest = 0;
            for (int a = 0; a < k; a++)
            {
                double delta = 0;
                for (int b = 0; b < k; b++) delta += inverse[a, b] * gradient[b];
                p[free[a]] += delta;
                largest = Math.Max(largest, Math.Abs(delta));
            }
            p[0] = Math.Clamp(p[0], 20, 100); // keep the view angle where the tangent behaves
            if (largest < 1e-5) return true;
        }
        return true;
    }

    /// <summary>Gauss-Jordan; false if the presses leave an angle undetermined.</summary>
    static bool TryInvert(double[,] m, out double[,] inverse)
    {
        int n = m.GetLength(0);
        var a = (double[,])m.Clone();
        inverse = new double[n, n];
        for (int i = 0; i < n; i++) inverse[i, i] = 1;
        double scale = 0;
        for (int i = 0; i < n; i++) scale = Math.Max(scale, Math.Abs(a[i, i]));
        for (int col = 0; col < n; col++)
        {
            int pivot = col;
            for (int row = col + 1; row < n; row++)
                if (Math.Abs(a[row, col]) > Math.Abs(a[pivot, col])) pivot = row;
            if (Math.Abs(a[pivot, col]) <= 1e-9 * Math.Max(scale, 1e-12)) return false;
            for (int c = 0; c < n; c++)
            {
                (a[col, c], a[pivot, c]) = (a[pivot, c], a[col, c]);
                (inverse[col, c], inverse[pivot, c]) = (inverse[pivot, c], inverse[col, c]);
            }
            double d = a[col, col];
            for (int c = 0; c < n; c++) { a[col, c] /= d; inverse[col, c] /= d; }
            for (int row = 0; row < n; row++)
            {
                if (row == col) continue;
                double f = a[row, col];
                for (int c = 0; c < n; c++) { a[row, c] -= f * a[col, c]; inverse[row, c] -= f * inverse[col, c]; }
            }
        }
        return true;
    }

    static double Wrap(double deg) => deg - 360 * Math.Round(deg / 360);
}

public enum CalibrationState { WaitingForPress, Done, Failed }

/// <summary>
/// Leads the wearer through the upright-edge presses for one calibration and fits the result.
/// Call <see cref="Press"/> each time the wearer presses the key, with the tracker's head pose at that moment.
/// </summary>
public sealed class EdgeCalibration
{
    const float DegToRad = MathF.PI / 180f;
    const float RadToDeg = 180f / MathF.PI;
    /// <summary>How far to look up or down for the view-angle presses. Further shows the view angle more strongly.</summary>
    const float LookDeg = 35f;
    /// <summary>Side lines sit this far out, as a share of half the picture: near the edge, where the effect is largest.</summary>
    const float SideShare = 0.85f;
    const float MaxRollDeg = 30f;
    /// <summary>Presses that disagree more than this (degrees, rms) are not one consistent set.</summary>
    const float MaxRmsDeg = 1.2f;
    const float MaxErrorDeg = 1f;

    public const float MinFovDeg = 35f, MaxFovDeg = 65f, MaxLevelDeg = 15f, MaxLeanDeg = 8f;

    readonly List<EdgeTarget> _queue;
    readonly List<EdgePress> _presses = [];
    readonly IReadOnlyList<EdgePress> _earlier;
    readonly bool _fitFov;
    readonly float _fovDeg;
    readonly int _widthPx;
    readonly float _halfHeightPx;
    int _next;
    int _extraLeft;

    EdgeCalibration(List<EdgeTarget> targets, bool fitFov, float fovDeg, int widthPx, int heightPx, IReadOnlyList<EdgePress> earlier)
    {
        _queue = targets;
        _fitFov = fitFov;
        _fovDeg = fovDeg;
        _widthPx = widthPx;
        _halfHeightPx = heightPx / 2f;
        _earlier = earlier;
        _extraLeft = targets.Count;
    }

    /// <summary>
    /// Level and lean: six presses with the head level, on lines left, middle and right. The view angle is taken as
    /// <paramref name="fovDeg"/>; with the head level it barely matters.
    /// </summary>
    public static EdgeCalibration LevelLean(float fovDeg, int widthPx = 1920, int heightPx = 1080)
    {
        float side = SideShare * widthPx / 2f;
        var targets = new List<EdgeTarget>();
        for (int round = 0; round < 2; round++)
            foreach (float x in new[] { 0f, side, -side })
                targets.Add(new EdgeTarget(HeadPosture.Level, x, 0));
        return new EdgeCalibration(targets, false, fovDeg, widthPx, heightPx, []);
    }

    /// <summary>
    /// View angle: twelve presses looking up and down about 35°. Level and lean are fitted alongside;
    /// <paramref name="earlier"/> (the level-and-lean presses, if any) adds to that fit.
    /// </summary>
    /// <param name="fovDeg">The angle the app assumes now. Only used to draw the side lines close to how they will look.</param>
    public static EdgeCalibration ViewAngle(float fovDeg, int widthPx = 1920, int heightPx = 1080, IReadOnlyList<EdgePress>? earlier = null)
    {
        float side = SideShare * widthPx / 2f;
        var targets = new List<EdgeTarget>();
        for (int round = 0; round < 2; round++)
            foreach (HeadPosture posture in new[] { HeadPosture.Up, HeadPosture.Down })
                foreach (float x in new[] { side, -side, 0f })
                {
                    // Draw the line already leaning as a vertical would at this look, so only a small head tilt is needed.
                    float pitch = (posture == HeadPosture.Up ? LookDeg : -LookDeg) * DegToRad;
                    float tilt = EdgeFitter.VerticalTiltDeg(Quaternion.CreateFromYawPitchRoll(0, pitch, 0), x, fovDeg, 0, 0, widthPx);
                    targets.Add(new EdgeTarget(posture, x, MathF.Round(tilt, 1)));
                }
        return new EdgeCalibration(targets, true, fovDeg, widthPx, heightPx, earlier ?? []);
    }

    public CalibrationState State { get; private set; }
    public EdgeTarget? Current => State == CalibrationState.WaitingForPress ? _queue[_next] : null;
    public IReadOnlyList<EdgePress> Presses => _presses;
    /// <summary>How many presses are asked for in all, retakes included; <see cref="Presses"/> says how many are taken.</summary>
    public int Asked => _queue.Count;
    public EdgeFit? Result { get; private set; }
    /// <summary>Why it failed, in plain words for the wearer.</summary>
    public string? Failure { get; private set; }
    /// <summary>Why the last press was not taken, or null. Show it until the next press.</summary>
    public string? Note { get; private set; }
    public float Progress => State == CalibrationState.WaitingForPress ? (float)_next / _queue.Count : 1f;

    public string Instruction => Current is not { } t
        ? (Failure ?? "Done.")
        : t.Posture switch
        {
            HeadPosture.Level => $"Keep your head level. Find an upright edge: a door frame, window side or wall corner. Turn until it lies under the {Where(t)} line, tilt your head until the line covers it exactly, hold still and press.",
            HeadPosture.Up => $"Look UP about {LookDeg:F0} degrees at a tall upright edge such as a door frame or wall corner. Lay the {Where(t)} line exactly on it, tilting your head until they match, hold still and press.",
            _ => $"Look DOWN about {LookDeg:F0} degrees at a tall upright edge such as a door frame or wall corner. Lay the {Where(t)} line exactly on it, tilting your head until they match, hold still and press.",
        };

    /// <summary>The one line to lay on the edge, nearly the full height of the picture.</summary>
    public GuideDrawing Drawing
    {
        get
        {
            if (Current is not { } t) return GuideDrawing.None;
            float half = 0.9f * _halfHeightPx, s = MathF.Sin(t.DrawnTiltDeg * DegToRad), c = MathF.Cos(t.DrawnTiltDeg * DegToRad);
            var along = new Vector2(half * s, half * c);
            var at = new Vector2(t.XPx, 0);
            return new GuideDrawing([new GuideLine(GuideSpace.Display, at - along, at + along)], []);
        }
    }

    /// <summary>The key was pressed. False if the head was not held as asked; <see cref="Note"/> says why.</summary>
    public bool Press(Quaternion head)
    {
        if (Current is not { } target) return false;

        Vector3 forward = Vector3.Transform(-Vector3.UnitZ, head);
        Vector3 right = Vector3.Transform(Vector3.UnitX, head);
        Vector3 up = Vector3.Transform(Vector3.UnitY, head);
        float pitch = MathF.Asin(Math.Clamp(forward.Y, -1f, 1f)) * RadToDeg;
        float roll = MathF.Atan2(-right.Y, up.Y) * RadToDeg;

        Note = target.Posture switch
        {
            HeadPosture.Level when pitch > 15 => "That was looking up. Keep your head level and press again.",
            HeadPosture.Level when pitch < -15 => "That was looking down. Keep your head level and press again.",
            HeadPosture.Up when pitch < -10 => $"That was looking DOWN. Look UP about {LookDeg:F0} degrees and press again.",
            HeadPosture.Up when pitch < 20 => $"Look further up, about {LookDeg:F0} degrees, and press again.",
            HeadPosture.Down when pitch > 10 => $"That was looking UP. Look DOWN about {LookDeg:F0} degrees and press again.",
            HeadPosture.Down when pitch > -20 => $"Look further down, about {LookDeg:F0} degrees, and press again.",
            _ when MathF.Abs(pitch) > 60 => "That was too steep. Look less far and press again.",
            _ when MathF.Abs(roll) > MaxRollDeg => "The head was tipped too far sideways. Pick a straighter edge and press again.",
            _ => null,
        };
        if (Note is not null) return false;

        _presses.Add(new EdgePress(target, head));
        if (++_next < _queue.Count) return true;

        Finish();
        return true;
    }

    void Finish()
    {
        EdgeFit? fit = EdgeFitter.Fit([.. _earlier, .. _presses], _fovDeg, _widthPx, _fitFov);

        string? failure = Check(fit);

        // Ask again for presses that did not fit. With only two presses on a line a bad one and a good one look
        // equally wrong, so when the set disagrees without a clear culprit, retake the worst few: a third press
        // tells them apart.
        List<EdgePress> retake = fit?.Rejected.Where(_presses.Contains).ToList() ?? [];
        if (fit is not null && retake.Count == 0 && failure is not null)
            retake = _presses.OrderByDescending(p => MathF.Abs(EdgeFitter.MissDeg(p, fit, _widthPx))).Take(3).ToList();
        if (fit is not null && retake.Count > 0 && _extraLeft > 0)
        {
            foreach (EdgePress bad in retake.Take(_extraLeft))
            {
                _queue.Add(bad.Target);
                _extraLeft--;
            }
            Note = retake.Count == 1 ? "One press did not agree with the others. Once more, please." : "Some presses did not agree with the others. A few more, please.";
            return;
        }

        Result = fit;
        Failure = failure;
        State = Failure is null ? CalibrationState.Done : CalibrationState.Failed;
    }

    string? Check(EdgeFit? fit)
    {
        if (fit is null) return "The presses could not be fitted together. Try again.";

        List<EdgePress> used = [.. _earlier, .. _presses];
        used.RemoveAll(fit.Rejected.Contains);
        bool Has(HeadPosture posture, int side) => used.Any(p => p.Target.Posture == posture && MathF.Sign(p.Target.XPx) == side);
        bool covered = _fitFov
            ? Has(HeadPosture.Up, 1) && Has(HeadPosture.Up, -1) && Has(HeadPosture.Down, 1) && Has(HeadPosture.Down, -1)
            : Has(HeadPosture.Level, 1) && Has(HeadPosture.Level, -1) && Has(HeadPosture.Level, 0);
        if (!covered) return "Too few presses agreed with each other on one of the lines. Try again, laying the line exactly on a straight upright edge.";

        if (fit.RmsDeg > MaxRmsDeg)
            return $"The presses disagree by {fit.RmsDeg:F1} degrees on average. Try again: lay the line exactly on the edge and press only once the head is still.";

        if (_fitFov)
        {
            if (fit.FovDeg < MinFovDeg || fit.FovDeg > MaxFovDeg)
                return $"That gives a view angle of {fit.FovDeg:F1} degrees, outside the believable {MinFovDeg:F0} to {MaxFovDeg:F0}. Check the edge is truly upright and try again.";
            if (fit.FovErrorDeg > MaxErrorDeg)
                return $"The view angle came out {fit.FovDeg:F1} degrees but could be {fit.FovErrorDeg:F1} either way. Try again, looking further up and down.";
        }
        if (MathF.Abs(fit.LevelDeg) > MaxLevelDeg || MathF.Abs(fit.LeanDeg) > MaxLeanDeg)
            return $"That gives level {fit.LevelDeg:F1} and lean {fit.LeanDeg:F1} degrees, more than the glasses can be off. Check the edge is truly upright and try again.";
        if (fit.LevelErrorDeg > MaxErrorDeg || fit.LeanErrorDeg > MaxErrorDeg)
            return $"Level and lean could be {MathF.Max(fit.LevelErrorDeg, fit.LeanErrorDeg):F1} degrees out either way. Try again with a longer, straighter edge.";
        return null;
    }

    static string Where(EdgeTarget t) => t.XPx > 0 ? "right" : t.XPx < 0 ? "left" : "middle";
}
