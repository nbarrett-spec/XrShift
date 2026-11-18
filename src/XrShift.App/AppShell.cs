using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using XrShift.Tracking;

namespace XrShift.App;

/// <summary>
/// Where the app says what it is doing: the terminal if it was started from one, and always
/// <c>last-run.txt</c> in the settings folder, so a run started from the Start menu can be looked at afterwards.
/// </summary>
internal static class Log
{
    static readonly object Gate = new();
    static string? s_path;

    /// <summary>Call once at start. The app has no console window of its own; this borrows the terminal's if there is one.</summary>
    public static void Start() => AttachConsole(-1);

    /// <summary>
    /// For the live app only: from here on every line also goes to last-run.txt. The run before is kept as
    /// last-run-previous.txt, so a crash is not wiped out by starting the app again.
    /// </summary>
    public static void StartFile()
    {
        try
        {
            Directory.CreateDirectory(TrackingConfig.Folder);
            s_path = Path.Combine(TrackingConfig.Folder, "last-run.txt");
            if (File.Exists(s_path)) File.Copy(s_path, Path.Combine(TrackingConfig.Folder, "last-run-previous.txt"), overwrite: true);
            File.WriteAllText(s_path, $"XrShift started {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            s_path = null;
        }
    }

    public static void Line(string text)
    {
        lock (Gate)
        {
            try { Console.WriteLine(text); } catch (IOException) { }
            if (s_path is null) return;
            try { File.AppendAllText(s_path, text + Environment.NewLine); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    [DllImport("kernel32.dll")]
    static extern bool AttachConsole(int processId);
}

/// <summary>The app's icon, drawn rather than shipped as a file: a pair of lenses on a dark tile.</summary>
internal static class AppIcon
{
    static Icon? s_icon;

    public static Icon Value => s_icon ??= Create();

    static Icon Create()
    {
        using Bitmap picture = Draw(64);
        return Icon.FromHandle(picture.GetHicon());
    }

    /// <summary>The icon at any size: the same drawing scaled, so it stays crisp from the tray to the desktop.</summary>
    static Bitmap Draw(int size)
    {
        float k = size / 64f;
        var picture = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using Graphics g = Graphics.FromImage(picture);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);
        using (GraphicsPath tile = Ui.Rounded(new RectangleF(2 * k, 2 * k, 60 * k, 60 * k), 14 * k))
        using (var back = new SolidBrush(Color.FromArgb(10, 13, 20)))
            g.FillPath(back, tile);
        using var lens = new Pen(Ui.Accent, MathF.Max(5f * k, 1.2f));
        using (GraphicsPath left = Ui.Rounded(new RectangleF(9 * k, 23 * k, 20 * k, 18 * k), 7 * k)) g.DrawPath(lens, left);
        using (GraphicsPath right = Ui.Rounded(new RectangleF(35 * k, 23 * k, 20 * k, 18 * k), 7 * k)) g.DrawPath(lens, right);
        g.DrawLine(lens, 29 * k, 30 * k, 35 * k, 30 * k);
        return picture;
    }

    /// <summary>
    /// Writes the icon as a Windows .ico file holding every size Windows asks for. Run once by the developer
    /// (<c>XrShift --make-icon file.ico</c>); the result is built into the program so Explorer, the taskbar
    /// and shortcuts show it.
    /// </summary>
    public static void WriteIco(string path)
    {
        int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
        var images = new List<byte[]>();
        foreach (int size in sizes)
        {
            using Bitmap picture = Draw(size);
            using var png = new MemoryStream();
            picture.Save(png, System.Drawing.Imaging.ImageFormat.Png);
            images.Add(png.ToArray());
        }

        using var file = new BinaryWriter(File.Create(path));
        file.Write((short)0);            // reserved
        file.Write((short)1);            // 1 = icon
        file.Write((short)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            file.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); // width; 0 means 256
            file.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); // height
            file.Write((byte)0);         // no palette
            file.Write((byte)0);         // reserved
            file.Write((short)1);        // colour planes
            file.Write((short)32);       // bits per pixel
            file.Write(images[i].Length);
            file.Write(offset);
            offset += images[i].Length;
        }
        foreach (byte[] image in images) file.Write(image);
    }
}

/// <summary>Lets the app start when Windows does, by a per-user entry Windows reads at sign-in. Only changed when the user flips the switch.</summary>
internal static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "XrShift";

    public static bool Enabled
    {
        get
        {
            try
            {
                using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(Name) is string;
            }
            catch (Exception) { return false; }
        }
    }

    public static bool Set(bool on)
    {
        try
        {
            using Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) key.SetValue(Name, $"\"{Environment.ProcessPath}\" --no-settings");
            else key.DeleteValue(Name, throwOnMissingValue: false);
            return true;
        }
        catch (Exception) { return false; }
    }
}
