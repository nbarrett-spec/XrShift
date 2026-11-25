using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using XrShift.Scene;

namespace XrShift.Scene.Tests;

public class SteamVrTests
{
    // ---- the wire between XrShift and its SteamVR driver ----

    static readonly VrWire.Display Glasses3D = new(1920, 0, 3840, 1080, true, 60, 50.7f, 0.063f, 0.0165f);

    [Fact]
    public void WireBytesAreWhereTheDriverReadsThem()
    {
        var block = new byte[VrWire.Size];
        float h = MathF.Sin(15 * MathF.PI / 180), c = MathF.Cos(15 * MathF.PI / 180);
        VrWire.Write(block, 123456789012, new Quaternion(0, h, 0, c), new Vector3(0.5f, -1.25f, 2), Glasses3D, VrWire.Command.Run);

        Assert.Equal(128, VrWire.Size);
        Assert.Equal("XRSV"u8.ToArray(), block[4..8]); // little-endian magic reads as text in memory
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(8)));
        Assert.Equal(128u, BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(12)));
        Assert.Equal(123456789012, BinaryPrimitives.ReadInt64LittleEndian(block.AsSpan(16)));
        // w, x, y, z as doubles, as OpenVR's HmdQuaternion_t
        Assert.Equal(c, BinaryPrimitives.ReadDoubleLittleEndian(block.AsSpan(24)), 6);
        Assert.Equal(0, BinaryPrimitives.ReadDoubleLittleEndian(block.AsSpan(32)));
        Assert.Equal(h, BinaryPrimitives.ReadDoubleLittleEndian(block.AsSpan(40)), 6);
        Assert.Equal(0, BinaryPrimitives.ReadDoubleLittleEndian(block.AsSpan(48)));
        Assert.Equal(0.5, BinaryPrimitives.ReadDoubleLittleEndian(block.AsSpan(56)));
        Assert.Equal(-1.25, BinaryPrimitives.ReadDoubleLittleEndian(block.AsSpan(64)));
        Assert.Equal(2, BinaryPrimitives.ReadDoubleLittleEndian(block.AsSpan(72)));
        Assert.Equal(1920, BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(80)));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(84)));
        Assert.Equal(3840, BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(88)));
        Assert.Equal(1080, BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(92)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(96)));
        Assert.Equal(60f, BinaryPrimitives.ReadSingleLittleEndian(block.AsSpan(100)));
        Assert.Equal(50.7f, BinaryPrimitives.ReadSingleLittleEndian(block.AsSpan(104)));
        Assert.Equal(0.063f, BinaryPrimitives.ReadSingleLittleEndian(block.AsSpan(108)));
        Assert.Equal(0.0165f, BinaryPrimitives.ReadSingleLittleEndian(block.AsSpan(112)));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(116)));
        // The sequence and the driver's own field are never written by the block writer.
        Assert.All(block[0..4].Concat(block[120..128]), b => Assert.Equal(0, b));

        VrWire.Write(block, 0, Quaternion.Identity, Vector3.Zero, Glasses3D, VrWire.Command.QuitSteamVr);
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(116)));
    }

    [Fact]
    public void DriverSourcePinsTheSameLayout()
    {
        string cpp = File.ReadAllText(Path.Combine(RepoRoot(), "src", "XrShift.SteamVR", "driver.cpp"));
        var expected = new Dictionary<string, int>
        {
            ["sequence"] = VrWire.SequenceAt, ["magic"] = VrWire.MagicAt, ["version"] = VrWire.VersionAt, ["size"] = VrWire.SizeAt,
            ["sampleTicks"] = VrWire.SampleTicksAt, ["rotation"] = VrWire.RotationAt, ["angularVelocity"] = VrWire.AngularVelocityAt,
            ["windowX"] = VrWire.WindowXAt, ["windowY"] = VrWire.WindowYAt, ["windowWidth"] = VrWire.WindowWidthAt,
            ["windowHeight"] = VrWire.WindowHeightAt, ["sideBySide"] = VrWire.SideBySideAt, ["refreshHz"] = VrWire.RefreshHzAt,
            ["fovDeg"] = VrWire.FovDegAt, ["ipdMeters"] = VrWire.IpdMetersAt, ["vsyncToPhotons"] = VrWire.VsyncToPhotonsAt,
            ["command"] = VrWire.CommandAt, ["driverTicks"] = VrWire.DriverTicksAt,
        };
        var pinned = Regex.Matches(cpp, @"static_assert\(offsetof\(WireBlock, (\w+)\) == (\d+)")
            .ToDictionary(m => m.Groups[1].Value, m => int.Parse(m.Groups[2].Value));
        Assert.Equal(expected.OrderBy(p => p.Key), pinned.OrderBy(p => p.Key));
        Assert.Matches($@"static_assert\(sizeof\(WireBlock\) == {VrWire.Size},", cpp);
        Assert.Contains($"WireMagic = 0x{VrWire.Magic:X8};", cpp);
        Assert.Contains($"WireVersion = {VrWire.Version};", cpp);
        Assert.Contains($"CommandRun = {(int)VrWire.Command.Run};", cpp);
        Assert.Contains($"CommandQuitSteamVr = {(int)VrWire.Command.QuitSteamVr};", cpp);
        Assert.Contains($"L\"{VrWire.Name.Replace(@"\", @"\\")}\"", cpp);
    }

    static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "XrShift.slnx"))) return d.FullName;
        throw new DirectoryNotFoundException("XrShift.slnx not found above the test folder.");
    }

    [Fact]
    public void GlassesIn3DModeGetOneHalfPerEye()
    {
        VrWire.Display d = VrWire.DisplayFor(1920, 0, 3840, 1080, 60, 50.7f, 16.5f);
        Assert.True(d.SideBySide);
        Assert.Equal(0.063f, d.IpdMeters);
        Assert.Equal(0.0165f, d.VsyncToPhotonsSeconds, 5);

        VrWire.Display flat = VrWire.DisplayFor(-1920, 0, 1920, 1080, 90, 50.7f, 200);
        Assert.False(flat.SideBySide);
        Assert.Equal(0f, flat.IpdMeters); // both eyes see the same picture: no separation, no doubled view
        Assert.Equal(0.05f, flat.VsyncToPhotonsSeconds); // held to something sensible
        Assert.Equal((-1920, 0, 1920, 1080, 90f), (flat.X, flat.Y, flat.Width, flat.Height, flat.RefreshHz));
    }

    // ---- finding SteamVR ----

    const string LibraryFolders = """
        "libraryfolders"
        {
        	"0"
        	{
        		"path"		"C:\\Program Files (x86)\\Steam"
        		"label"		""
        		"apps"
        		{
        			"228980"		"0"
        		}
        	}
        	"1"
        	{
        		"path"		"D:\\SteamLibrary"
        		"label"		"Games"
        		"apps"
        		{
        			"250820"		"5512345678"
        		}
        	}
        }
        """;

    [Fact]
    public void SteamVrIsFoundInAnotherLibrary()
    {
        IReadOnlyList<string> libraries = SteamVrSetup.Libraries("c:/program files (x86)/steam", LibraryFolders);
        Assert.Equal([@"c:\program files (x86)\steam", @"D:\SteamLibrary"], libraries);

        string vrserver = @"D:\SteamLibrary\steamapps\common\SteamVR\bin\win64\vrserver.exe";
        Assert.Equal(@"D:\SteamLibrary\steamapps\common\SteamVR", SteamVrSetup.FindSteamVr(libraries, p => p == vrserver));
        Assert.Null(SteamVrSetup.FindSteamVr(libraries, _ => false));
    }

    [Fact]
    public void NoLibraryFileMeansSteamsOwnFolderOnly()
    {
        Assert.Equal([@"C:\Steam"], SteamVrSetup.Libraries(@"C:\Steam\", null));
        Assert.Equal([@"C:\Steam"], SteamVrSetup.Libraries(@"C:\Steam", "not a vdf at all\n\"path\""));
    }

    // ---- SteamVR's list of drivers (openvrpaths.vrpath) ----

    const string Mine = @"C:\Users\me\XrShift\steamvr\xrshift";
    const string Old = @"C:\Users\me\Downloads\XrShift-0.3.0\steamvr\xrshift";

    static string VrPaths(string drivers) => $$"""
        {
        	"config" : [ "C:\\Program Files (x86)\\Steam\\config" ],
        	"external_drivers" : {{drivers}},
        	"jsonid" : "vrpathreg",
        	"log" : [ "C:\\Program Files (x86)\\Steam\\logs" ],
        	"runtime" : [ "C:\\Program Files (x86)\\Steam\\steamapps\\common\\SteamVR" ],
        	"version" : 1
        }
        """;

    static string Json(string path) => path.Replace(@"\", @"\\");

    [Fact]
    public void RegistrationIsReadAsItIs()
    {
        Assert.Equal(SteamVrSetup.Registration.NoFile, SteamVrSetup.Check(null, Mine));
        Assert.Equal(SteamVrSetup.Registration.None, SteamVrSetup.Check(VrPaths("null"), Mine));
        Assert.Equal(SteamVrSetup.Registration.None, SteamVrSetup.Check(VrPaths(@"[ ""D:\\Drivers\\other"" ]"), Mine));
        Assert.Equal(SteamVrSetup.Registration.Current, SteamVrSetup.Check(VrPaths($@"[ ""{Json(Mine)}"" ]"), Mine));
        Assert.Equal(SteamVrSetup.Registration.Current, SteamVrSetup.Check(VrPaths($@"[ ""{Json(Mine.ToUpperInvariant())}\\"" ]"), Mine));
        Assert.Equal(SteamVrSetup.Registration.Other, SteamVrSetup.Check(VrPaths($@"[ ""{Json(Old)}"" ]"), Mine));
        Assert.Equal(SteamVrSetup.Registration.Unreadable, SteamVrSetup.Check("{ not json", Mine));
    }

    [Fact]
    public void RegisteringKeepsEverythingElseAndReplacesAnOlderCopy()
    {
        string before = VrPaths($@"[ ""D:\\Drivers\\other"", ""{Json(Old)}"" ]");
        string? after = SteamVrSetup.Register(before, Mine, out _);
        Assert.NotNull(after);
        var root = JsonNode.Parse(after)!.AsObject();
        Assert.Equal([@"D:\Drivers\other", Mine], root["external_drivers"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("vrpathreg", root["jsonid"]!.GetValue<string>());
        Assert.Equal(1, root["version"]!.GetValue<int>());
        Assert.Equal(@"C:\Program Files (x86)\Steam\steamapps\common\SteamVR", root["runtime"]![0]!.GetValue<string>());
        Assert.Equal(SteamVrSetup.Registration.Current, SteamVrSetup.Check(after, Mine));
        Assert.Equal(after, SteamVrSetup.Register(after, Mine, out _)); // twice is the same as once

        Assert.Equal([Mine], JsonNode.Parse(SteamVrSetup.Register(VrPaths("null"), Mine, out _)!)!["external_drivers"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public void UnregisteringTakesOnlyXrShiftOut()
    {
        string? after = SteamVrSetup.Unregister(VrPaths($@"[ ""{Json(Mine)}"", ""D:\\Drivers\\other"", ""{Json(Old)}"" ]"), out _);
        Assert.Equal([@"D:\Drivers\other"], JsonNode.Parse(after!)!["external_drivers"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(SteamVrSetup.Registration.None, SteamVrSetup.Check(after, Mine));
        Assert.NotNull(SteamVrSetup.Unregister(VrPaths("null"), out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{ \"external_drivers\": [ \"C:\\\\a\", ")]
    [InlineData("[ 1, 2 ]")]
    [InlineData("{ \"external_drivers\": \"C:\\\\a\" }")]
    [InlineData("{ \"external_drivers\": [ 3 ] }")]
    public void ARiskyFileIsRefusedAndLeftAlone(string? text)
    {
        Assert.Null(SteamVrSetup.Register(text, Mine, out string why));
        Assert.NotEmpty(why);
        Assert.Null(SteamVrSetup.Unregister(text, out why));
        Assert.NotEmpty(why);
    }
}
