using System.Globalization;
using System.Numerics;

namespace XrShift.Tracking;

/// <summary>
/// Which way the wearer's right, up and back point, expressed in the glasses' sensor axes.
/// The sensor sits at an angle inside the frame, so these are measured, not axis-aligned.
/// </summary>
public sealed record AxisMap(Vector3 Right, Vector3 Up, Vector3 Back)
{
    /// <summary>
    /// Typical mounting on the One Pro+, used until <c>xrprobe axes</c> has measured the real one:
    /// sensor X points to the wearer's right and the sensor is pitched roughly 36° in the frame.
    /// </summary>
    public static AxisMap Default { get; } = Square(Vector3.UnitX, new Vector3(-0.01f, -0.81f, 0.585f));

    /// <summary>
    /// Builds a map from a measured up direction (kept exactly) and a measured right
    /// direction (squared up against it). Fails if the two are close to parallel.
    /// </summary>
    public static bool TryFromRightUp(Vector3 right, Vector3 up, out AxisMap map)
    {
        map = Default;
        float rightLength = right.Length();
        if (rightLength < 1e-4f || up.Length() < 1e-4f) return false;

        up = Vector3.Normalize(up);
        Vector3 level = right - Vector3.Dot(right, up) * up;
        if (level.Length() < 0.5f * rightLength) return false;

        map = Square(level, up);
        return true;
    }

    /// <summary>
    /// Builds a map from two gravity readings: head level, then looking up. Turning the head
    /// between the two readings does not matter, because turning leaves gravity where it was.
    /// </summary>
    /// <param name="pitchDeg">How far up the head went between the readings.</param>
    public static bool TryFromLevelAndLookUp(Vector3 upLevel, Vector3 upLookingUp, out AxisMap map, out float pitchDeg)
    {
        map = Default;
        pitchDeg = 0;
        if (upLevel.Length() < 1e-4f || upLookingUp.Length() < 1e-4f) return false;

        upLevel = Vector3.Normalize(upLevel);
        upLookingUp = Vector3.Normalize(upLookingUp);

        // Looking up by θ swings gravity from Up toward Forward, so (looking up) x (level) = sin θ · Right.
        Vector3 right = Vector3.Cross(upLookingUp, upLevel);
        float sin = right.Length();
        pitchDeg = MathF.Atan2(sin, Vector3.Dot(upLevel, upLookingUp)) * (180f / MathF.PI);
        if (sin < 0.05f) return false; // under about 3 degrees: too little to tell

        return TryFromRightUp(right, upLevel, out map);
    }

    public override string ToString() => $"Right={Format(Right)};Up={Format(Up)};Back={Format(Back)}";

    public static bool TryParse(string text, out AxisMap map)
    {
        map = Default;
        Vector3? right = null, up = null, back = null;
        foreach (string part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] kv = part.Split('=');
            if (kv.Length != 2 || !TryVector(kv[1], out Vector3 v)) return false;
            switch (kv[0].Trim().ToLowerInvariant())
            {
                case "right": right = v; break;
                case "up": up = v; break;
                case "back": back = v; break;
                default: return false;
            }
        }
        if (right is not { } r || up is not { } u || back is not { } b) return false;

        // Must be three perpendicular unit directions, right-handed.
        if (MathF.Abs(r.Length() - 1) > 0.05f || MathF.Abs(u.Length() - 1) > 0.05f || MathF.Abs(b.Length() - 1) > 0.05f) return false;
        if (MathF.Abs(Vector3.Dot(r, u)) > 0.05f) return false;
        if (Vector3.Dot(Vector3.Cross(r, u), b) < 0.95f) return false;
        return TryFromRightUp(r, u, out map);
    }

    static AxisMap Square(Vector3 right, Vector3 up)
    {
        up = Vector3.Normalize(up);
        Vector3 level = Vector3.Normalize(right - Vector3.Dot(right, up) * up);
        return new AxisMap(level, up, Vector3.Cross(level, up));
    }

    static string Format(Vector3 v) => string.Create(CultureInfo.InvariantCulture, $"{v.X:F4},{v.Y:F4},{v.Z:F4}");

    internal static bool TryVector(string text, out Vector3 v)
    {
        v = default;
        string[] parts = text.Split(',');
        if (parts.Length != 3) return false;
        Span<float> c = stackalloc float[3];
        for (int i = 0; i < 3; i++)
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out c[i])) return false;
        v = new Vector3(c[0], c[1], c[2]);
        return true;
    }
}

/// <summary>Per-user tracking settings on disk.</summary>
public static class TrackingConfig
{
    /// <summary>A resting gyro reading larger than this (rad/s, about 3 deg/s) is not a believable offset.</summary>
    internal const float MaxGyroBias = 0.05f;

    /// <summary>Settings folder. The XRSHIFT_CONFIG environment variable overrides it, for trial runs.</summary>
    public static string Folder =>
        Environment.GetEnvironmentVariable("XRSHIFT_CONFIG") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XrShift");

    public static string AxesPath => Path.Combine(Folder, "axes.txt");
    public static string GyroPath => Path.Combine(Folder, "gyro.txt");

    public static AxisMap LoadAxes()
    {
        try
        {
            if (File.Exists(AxesPath) && AxisMap.TryParse(File.ReadAllText(AxesPath), out AxisMap map))
                return map;
        }
        catch (IOException) { }
        return AxisMap.Default;
    }

    /// <summary>A tracker set up from what has been measured on this PC: sensor mounting and resting gyro offset.</summary>
    public static HeadTracker CreateTracker()
    {
        var tracker = new HeadTracker { Axes = LoadAxes() };
        if (TryLoadGyroBias(out Vector3 bias)) tracker.SetGyroBias(bias);
        return tracker;
    }

    public static void SaveAxes(AxisMap map)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(AxesPath, map.ToString());
    }

    /// <summary>The gyro's resting offset (rad/s) from the last time it was measured, if any.</summary>
    public static bool TryLoadGyroBias(out Vector3 bias)
    {
        bias = default;
        try
        {
            return File.Exists(GyroPath) && TryParseGyroBias(File.ReadAllText(GyroPath), out bias);
        }
        catch (IOException) { return false; }
    }

    public static void SaveGyroBias(Vector3 bias)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(GyroPath, FormatGyroBias(bias));
    }

    public static string FormatGyroBias(Vector3 bias) =>
        string.Create(CultureInfo.InvariantCulture, $"{bias.X:G9},{bias.Y:G9},{bias.Z:G9}");

    public static bool TryParseGyroBias(string text, out Vector3 bias)
    {
        if (AxisMap.TryVector(text.Trim(), out bias) &&
            float.IsFinite(bias.X) && float.IsFinite(bias.Y) && float.IsFinite(bias.Z) &&
            bias.Length() <= MaxGyroBias)
            return true;
        bias = default;
        return false;
    }
}
