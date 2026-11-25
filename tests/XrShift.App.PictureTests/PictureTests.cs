using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;

namespace XrShift.App.PictureTests;

/// <summary>
/// Draws every scenario in scenarios\ with the real XrShift.exe and compares each picture with the approved one in
/// approved\&lt;scenario&gt;\&lt;frame&gt;.png. A changed or missing picture is written beside it as .new.png, with a
/// .diff.png showing what changed in magenta; copying the .new.png over the approved one approves it.
/// </summary>
public class PictureTests
{
    /// <summary>A pixel counts as changed when one of its colours moved by more than this (of 255).</summary>
    const int PixelThreshold = 32;
    /// <summary>
    /// A picture fails when more than this share of its pixels changed (about 400 of 1920x1080): room for driver
    /// differences along edges, but not for one wrong letter in a label, which moves some 2000. Windows' software
    /// renderer and an Intel card differed by under 0.002% at this threshold.
    /// </summary>
    const double MaxChangedShare = 0.0002;

    public static IEnumerable<object[]> Frames() =>
        Rendering.ScenarioFiles().SelectMany(file => Rendering.FrameNames(file).Select(frame => new object[] { Path.GetFileNameWithoutExtension(file), frame }));

    [PictureTheory]
    [MemberData(nameof(Frames))]
    public void PictureMatchesTheApprovedOne(string scenario, string frame)
    {
        Rendering.Result rendered = Rendering.All.Value;
        Assert.True(rendered.ExitCodes[scenario] == 0, $"XrShift.exe --scenario {scenario}.json failed ({rendered.ExitCodes[scenario]}):\n{rendered.Output}");

        string drawn = Path.Combine(rendered.Folder, scenario, frame + ".png");
        string approved = Path.Combine(Rendering.TestFolder, "approved", scenario, frame + ".png");
        string fresh = Path.ChangeExtension(approved, ".new.png"), diff = Path.ChangeExtension(approved, ".diff.png");
        Directory.CreateDirectory(Path.GetDirectoryName(approved)!);

        if (!File.Exists(approved))
        {
            File.Copy(drawn, fresh, overwrite: true);
            Assert.Fail($"No approved picture yet. Look at {fresh} and, if it is right, copy it to {approved}.");
        }

        using var want = new Bitmap(approved);
        using var got = new Bitmap(drawn);
        (double share, Bitmap? picture) = Compare(want, got);
        using (picture)
        {
            if (share <= MaxChangedShare)
            {
                File.Delete(fresh);
                File.Delete(diff);
                return;
            }
            File.Copy(drawn, fresh, overwrite: true);
            picture?.Save(diff, ImageFormat.Png);
        }
        Assert.Fail($"{share:P2} of the pixels changed (allowed {MaxChangedShare:P2}). Look at {fresh} and {diff}; " +
            $"if the new picture is right, copy it to {approved}.");
    }

    /// <summary>The share of pixels that changed, and a picture of where: the new frame darkened, changes in magenta.</summary>
    static (double Share, Bitmap? Diff) Compare(Bitmap want, Bitmap got)
    {
        if (want.Size != got.Size) return (1, null);
        int w = got.Width, h = got.Height, changed = 0;
        int[] a = Pixels(want), b = Pixels(got), d = new int[w * h];
        for (int i = 0; i < a.Length; i++)
        {
            int p = a[i], q = b[i];
            int most = Math.Max(Math.Abs((p >> 16 & 255) - (q >> 16 & 255)), Math.Max(Math.Abs((p >> 8 & 255) - (q >> 8 & 255)), Math.Abs((p & 255) - (q & 255))));
            if (most > PixelThreshold)
            {
                changed++;
                d[i] = unchecked((int)0xFFFF00FF);
            }
            else
            {
                d[i] = unchecked((int)0xFF000000) | (q >> 2 & 0x3F3F3F);
            }
        }
        var diff = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        BitmapData data = diff.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        System.Runtime.InteropServices.Marshal.Copy(d, 0, data.Scan0, d.Length);
        diff.UnlockBits(data);
        return (changed / (double)a.Length, diff);
    }

    static int[] Pixels(Bitmap picture)
    {
        var all = new int[picture.Width * picture.Height];
        BitmapData data = picture.LockBits(new Rectangle(0, 0, picture.Width, picture.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        System.Runtime.InteropServices.Marshal.Copy(data.Scan0, all, 0, all.Length);
        picture.UnlockBits(data);
        return all;
    }
}

/// <summary>
/// A picture test that is skipped, saying why, when this machine cannot make even Windows' software Direct3D
/// device (some build servers): there is then nothing to compare.
/// </summary>
public sealed class PictureTheoryAttribute : TheoryAttribute
{
    public PictureTheoryAttribute()
    {
        if (Rendering.All.Value.NoGraphics) Skip = "No Direct3D device, not even the software one, on this machine: " + Rendering.All.Value.Output.Trim();
    }
}

/// <summary>Runs XrShift.exe once per scenario file, into a folder beside the tests, the first time any test asks.</summary>
static class Rendering
{
    public sealed record Result(string Folder, Dictionary<string, int> ExitCodes, string Output, bool NoGraphics);

    /// <summary>The app's exit code when it could make no Direct3D device at all.</summary>
    const int NoGraphicsExit = 4;

    public static readonly Lazy<Result> All = new(RenderAll);

    public static string TestFolder { get; } = FindTestFolder();

    static string FindTestFolder()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "XrShift.slnx"))) return Path.Combine(d.FullName, "tests", "XrShift.App.PictureTests");
        }
        throw new InvalidOperationException("The repository (XrShift.slnx) was not found above " + AppContext.BaseDirectory);
    }

    /// <summary>Scenario files: every .json in scenarios\ except the layouts and decks they use.</summary>
    public static IEnumerable<string> ScenarioFiles() =>
        Directory.GetFiles(Path.Combine(TestFolder, "scenarios"), "*.json")
            .Where(f => !f.EndsWith(".layout.json", StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".deck.json", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal);

    public static IEnumerable<string> FrameNames(string scenarioFile)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(scenarioFile), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        return doc.RootElement.GetProperty("frames").EnumerateArray().Select(f => f.GetProperty("name").GetString()!).ToList();
    }

    static Result RenderAll()
    {
        // bin\<Configuration>\<framework>\ of this test project names the build of the app to use.
        var here = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        string configuration = here.Parent!.Name, framework = here.Name;
        string exe = Path.GetFullPath(Path.Combine(TestFolder, "..", "..", "src", "XrShift.App", "bin", configuration, framework, "XrShift.exe"));
        if (!File.Exists(exe)) throw new FileNotFoundException("Build the app first.", exe);

        string folder = Path.Combine(AppContext.BaseDirectory, "rendered");
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        string config = Path.Combine(folder, "config"); // never the wearer's own settings folder
        Directory.CreateDirectory(config);

        var codes = new Dictionary<string, int>();
        var output = new System.Text.StringBuilder();
        bool noGraphics = false;
        foreach (string file in ScenarioFiles())
        {
            string name = Path.GetFileNameWithoutExtension(file);
            var start = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (string arg in (string[])["--scenario", file, "--out", Path.Combine(folder, name)]) start.ArgumentList.Add(arg);
            start.Environment["XRSHIFT_CONFIG"] = config;
            using Process run = Process.Start(start)!;
            Task<string> said = run.StandardOutput.ReadToEndAsync(), err = run.StandardError.ReadToEndAsync();
            bool finished = run.WaitForExit(120_000);
            if (!finished) run.Kill(); // only the copy this test started, stuck
            output.Append(said.Result).Append(err.Result);
            codes[name] = finished ? run.ExitCode : -1;
            noGraphics |= run.ExitCode == NoGraphicsExit;
        }
        return new Result(folder, codes, output.ToString(), noGraphics);
    }
}
