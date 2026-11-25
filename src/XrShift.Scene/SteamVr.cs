using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XrShift.Scene;

/// <summary>
/// The block of shared memory XrShift writes and its SteamVR driver (src/XrShift.SteamVR/driver.cpp, struct
/// WireBlock) reads: the head's orientation and turn rate, where the glasses are on the desktop, and whether VR
/// mode is on. Defined here once; driver.cpp pins the same offsets with static_asserts and a test compares the
/// two, so they cannot drift apart unnoticed. Little-endian, natural alignment, 128 bytes. See docs/VR.md.
/// </summary>
public static class VrWire
{
    public const string Name = @"Local\XrShift.VrPose";
    /// <summary>"XRSV" as bytes in memory.</summary>
    public const uint Magic = 0x56535258;
    /// <summary>Raised whenever the layout changes; the driver refuses any other version.</summary>
    public const uint Version = 1;
    public const int Size = 128;

    // Byte offsets. Sequence: odd while XrShift is writing (a sequence lock). DriverTicks: written by the driver only.
    public const int SequenceAt = 0, MagicAt = 4, VersionAt = 8, SizeAt = 12, SampleTicksAt = 16, RotationAt = 24,
        AngularVelocityAt = 56, WindowXAt = 80, WindowYAt = 84, WindowWidthAt = 88, WindowHeightAt = 92, SideBySideAt = 96,
        RefreshHzAt = 100, FovDegAt = 104, IpdMetersAt = 108, VsyncToPhotonsAt = 112, CommandAt = 116, DriverTicksAt = 120;

    public enum Command { Run = 0, QuitSteamVr = 1 }

    /// <summary>What SteamVR draws into: the glasses' place on the desktop and how each eye sees it.</summary>
    public readonly record struct Display(int X, int Y, int Width, int Height, bool SideBySide, float RefreshHz, float FovDeg, float IpdMeters, float VsyncToPhotonsSeconds);

    /// <summary>
    /// The display for glasses at this place on the desktop. Their 3D mode shows as one display twice as wide
    /// (3840 x 1080): the left half for the left eye, the right half for the right. Flat (1920 x 1080), both eyes
    /// see the same picture, so the eyes are put in one place (no separation) and the picture is not doubled.
    /// </summary>
    public static Display DisplayFor(int x, int y, int width, int height, double refreshHz, float fovDeg, float glassesDelayMs)
    {
        bool sideBySide = width >= height * 3;
        return new Display(x, y, width, height, sideBySide, (float)refreshHz, fovDeg,
            sideBySide ? 0.063f : 0f, Math.Clamp(glassesDelayMs / 1000f, 0f, 0.05f));
    }

    /// <summary>
    /// Fills a block, all but the sequence number and the driver's own field. <paramref name="rotation"/> turns head
    /// axes into the world SteamVR uses (X right, Y up, Z back, forward -Z), which is HeadPose.Orientation as it is;
    /// <paramref name="angularVelocity"/> is in that world, radians per second.
    /// </summary>
    public static void Write(Span<byte> block, long sampleTicks, Quaternion rotation, Vector3 angularVelocity, Display d, Command command)
    {
        if (block.Length < Size) throw new ArgumentException($"A block is {Size} bytes.", nameof(block));
        BinaryPrimitives.WriteUInt32LittleEndian(block[MagicAt..], Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(block[VersionAt..], Version);
        BinaryPrimitives.WriteUInt32LittleEndian(block[SizeAt..], Size);
        BinaryPrimitives.WriteInt64LittleEndian(block[SampleTicksAt..], sampleTicks);
        // w first, as OpenVR's HmdQuaternion_t.
        BinaryPrimitives.WriteDoubleLittleEndian(block[RotationAt..], rotation.W);
        BinaryPrimitives.WriteDoubleLittleEndian(block[(RotationAt + 8)..], rotation.X);
        BinaryPrimitives.WriteDoubleLittleEndian(block[(RotationAt + 16)..], rotation.Y);
        BinaryPrimitives.WriteDoubleLittleEndian(block[(RotationAt + 24)..], rotation.Z);
        BinaryPrimitives.WriteDoubleLittleEndian(block[AngularVelocityAt..], angularVelocity.X);
        BinaryPrimitives.WriteDoubleLittleEndian(block[(AngularVelocityAt + 8)..], angularVelocity.Y);
        BinaryPrimitives.WriteDoubleLittleEndian(block[(AngularVelocityAt + 16)..], angularVelocity.Z);
        BinaryPrimitives.WriteInt32LittleEndian(block[WindowXAt..], d.X);
        BinaryPrimitives.WriteInt32LittleEndian(block[WindowYAt..], d.Y);
        BinaryPrimitives.WriteInt32LittleEndian(block[WindowWidthAt..], d.Width);
        BinaryPrimitives.WriteInt32LittleEndian(block[WindowHeightAt..], d.Height);
        BinaryPrimitives.WriteInt32LittleEndian(block[SideBySideAt..], d.SideBySide ? 1 : 0);
        BinaryPrimitives.WriteSingleLittleEndian(block[RefreshHzAt..], d.RefreshHz);
        BinaryPrimitives.WriteSingleLittleEndian(block[FovDegAt..], d.FovDeg);
        BinaryPrimitives.WriteSingleLittleEndian(block[IpdMetersAt..], d.IpdMeters);
        BinaryPrimitives.WriteSingleLittleEndian(block[VsyncToPhotonsAt..], d.VsyncToPhotonsSeconds);
        BinaryPrimitives.WriteInt32LittleEndian(block[CommandAt..], (int)command);
    }
}

/// <summary>
/// Finding SteamVR and XrShift's driver in SteamVR's list of extra drivers (openvrpaths.vrpath). Text in, text
/// out, as <see cref="ClaudeHooks"/>: the caller reads and writes the file with a backup, and anything this cannot
/// read for certain is refused and left alone.
/// </summary>
public static class SteamVrSetup
{
    /// <summary>
    /// Steam's library folders: Steam's own folder, then every "path" in steamapps\libraryfolders.vdf (games can
    /// be on other drives). The file is Valve's text format, with backslashes doubled.
    /// </summary>
    public static IReadOnlyList<string> Libraries(string steamPath, string? libraryFoldersVdf)
    {
        var found = new List<string> { Normal(steamPath) };
        foreach (string line in (libraryFoldersVdf ?? "").Split('\n'))
        {
            string[] parts = line.Trim().Split('"');
            // "path"		"D:\\SteamLibrary"  splits into: "", path, tabs, D:\\SteamLibrary, ""
            if (parts.Length < 5 || !parts[1].Equals("path", StringComparison.OrdinalIgnoreCase) || parts[3].Length == 0) continue;
            string path = Normal(parts[3].Replace(@"\\", @"\"));
            if (!found.Contains(path, StringComparer.OrdinalIgnoreCase)) found.Add(path);
        }
        return found;
    }

    /// <summary>SteamVR's folder in the first library that has it, or null.</summary>
    public static string? FindSteamVr(IEnumerable<string> libraries, Func<string, bool> fileExists) =>
        libraries.Select(l => Path.Combine(l, "steamapps", "common", "SteamVR"))
            .FirstOrDefault(p => fileExists(Path.Combine(p, "bin", "win64", "vrserver.exe")));

    public enum Registration
    {
        /// <summary>SteamVR has not made openvrpaths.vrpath yet: it has never run for this Windows user.</summary>
        NoFile,
        /// <summary>No XrShift driver in SteamVR's list.</summary>
        None,
        /// <summary>This copy's driver is listed, and no other copy's.</summary>
        Current,
        /// <summary>Another copy of XrShift's driver is listed: registering points SteamVR at this one.</summary>
        Other,
        /// <summary>The file is not JSON this can change safely; nothing will be changed.</summary>
        Unreadable,
    }

    /// <summary>A driver folder of some copy of XrShift: ...\steamvr\xrshift.</summary>
    public static bool IsOurs(string? folder) =>
        folder is not null && Normal(folder).EndsWith(@"\steamvr\xrshift", StringComparison.OrdinalIgnoreCase);

    public static Registration Check(string? vrpathJson, string driverFolder)
    {
        if (vrpathJson is null) return Registration.NoFile;
        if (Read(vrpathJson, out _) is not { } root) return Registration.Unreadable;
        List<string> ours = Drivers(root).Where(IsOurs).ToList();
        if (ours.Count == 0) return Registration.None;
        return ours.All(d => Same(d, driverFolder)) ? Registration.Current : Registration.Other;
    }

    /// <summary>
    /// The file with this driver folder in "external_drivers", and no other copy of XrShift's (two drivers called
    /// xrshift would fight). Null, with <paramref name="why"/>, when the file is missing or cannot be changed safely.
    /// </summary>
    public static string? Register(string? vrpathJson, string driverFolder, out string why)
    {
        if (Read(vrpathJson, out why) is not { } root) return null;
        var list = Drivers(root).Where(d => !IsOurs(d)).ToList();
        list.Add(Normal(driverFolder));
        root["external_drivers"] = new JsonArray(list.Select(d => (JsonNode)JsonValue.Create(d)).ToArray());
        return Write(root);
    }

    /// <summary>The file without any XrShift driver; every other driver and setting stays.</summary>
    public static string? Unregister(string? vrpathJson, out string why)
    {
        if (Read(vrpathJson, out why) is not { } root) return null;
        if (root["external_drivers"] is null) return Write(root);
        root["external_drivers"] = new JsonArray(Drivers(root).Where(d => !IsOurs(d)).Select(d => (JsonNode)JsonValue.Create(d)).ToArray());
        return Write(root);
    }

    static JsonObject? Read(string? text, out string why)
    {
        why = "";
        if (text is null)
        {
            why = "SteamVR has not made its settings file yet. Start SteamVR once from Steam, close it, then try again.";
            return null;
        }
        JsonNode? node;
        try { node = JsonNode.Parse(text); }
        catch (JsonException e) { why = $"SteamVR's openvrpaths.vrpath is not valid JSON ({e.Message})."; return null; }
        if (node is not JsonObject root) { why = "SteamVR's openvrpaths.vrpath does not hold a JSON object."; return null; }
        // SteamVR writes null when there are no extra drivers.
        if (root["external_drivers"] is not (null or JsonArray) ||
            root["external_drivers"] is JsonArray list && list.Any(d => d is not JsonValue v || !v.TryGetValue(out string? _)))
        {
            why = "Its \"external_drivers\" is not a list of folders.";
            return null;
        }
        return root;
    }

    static IEnumerable<string> Drivers(JsonObject root) =>
        root["external_drivers"] is JsonArray list ? list.Select(d => d!.GetValue<string>()) : [];

    static bool Same(string a, string b) => string.Equals(Normal(a), Normal(b), StringComparison.OrdinalIgnoreCase);

    static string Normal(string path) => path.Replace('/', '\\').TrimEnd('\\');

    static string Write(JsonObject root) =>
        root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}
