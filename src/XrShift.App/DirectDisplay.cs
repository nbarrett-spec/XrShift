using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Windows.Devices.Display;
using Windows.Devices.Display.Core;
using Windows.Graphics.DirectX;
using WinRT;

namespace XrShift.App;

/// <summary>
/// Direct mode: XrShift drives the glasses itself, the way VR headsets are driven, instead of showing a window
/// on them through the Windows desktop. Head movement then never waits for the Windows compositor, whatever
/// else the PC is doing. It needs the glasses taken off the desktop first (Settings > System > Display >
/// Advanced display > the glasses > "Remove display from desktop"); while they are on the desktop Windows
/// keeps them and this simply reports that.
/// </summary>
internal sealed class DirectDisplay : IDisposable
{
    readonly DisplayManager _manager;
    readonly DisplayDevice _device;
    readonly DisplayTarget _target;
    readonly DisplaySource _source;
    readonly DisplayTaskPool _tasks;
    readonly DisplaySurface[] _surfaces = new DisplaySurface[2];
    readonly DisplayScanout[] _scanouts = new DisplayScanout[2];
    readonly ID3D11Texture2D[] _textures = new ID3D11Texture2D[2];
    ID3D11Query? _done;
    int _next;

    public int Width { get; }
    public int Height { get; }
    public double RefreshHz { get; }
    /// <summary>The graphics card the glasses are plugged into, as "luid:..." for <see cref="Renderer"/>.</summary>
    public string Adapter { get; }
    public string Name { get; }

    /// <summary>What a look at the glasses found, for the log and the settings window.</summary>
    public static string Status { get; private set; } = "not checked";

    static bool IsGlasses(DisplayTarget t)
    {
        try { return t.IsConnected && t.TryGetMonitor()?.DisplayName?.Contains("XREAL", StringComparison.OrdinalIgnoreCase) == true; }
        catch (Exception) { return false; }
    }

    /// <summary>The glasses are plugged in but not part of the desktop: direct mode can take them.</summary>
    public static bool GlassesOffDesktop()
    {
        try
        {
            using var manager = DisplayManager.Create(DisplayManagerOptions.None);
            return manager.GetCurrentTargets().Any(t => IsGlasses(t) && t.UsageKind != DisplayMonitorUsageKind.Standard);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Lines describing every display output Windows knows of, for <c>--direct-list</c>.</summary>
    public static IEnumerable<string> Describe()
    {
        using var manager = DisplayManager.Create(DisplayManagerOptions.None);
        foreach (DisplayTarget t in manager.GetCurrentTargets())
        {
            string name;
            try { name = t.TryGetMonitor()?.DisplayName ?? "(no monitor)"; } catch (Exception) { name = "(unreadable)"; }
            yield return $"{name}: {t.UsageKind}, connected {t.IsConnected}, adapter luid:{Luid(t.Adapter):x}";
        }
    }

    static long Luid(DisplayAdapter a) => ((long)a.Id.HighPart << 32) | a.Id.LowPart;

    /// <summary>Takes the glasses for drawing, or returns null (and says why in <see cref="Status"/>).</summary>
    public static DirectDisplay? TryOpen(double wantHz)
    {
        DisplayManager? manager = null;
        try
        {
            manager = DisplayManager.Create(DisplayManagerOptions.None);
            DisplayTarget? target = manager.GetCurrentTargets().FirstOrDefault(IsGlasses);
            if (target is null) { Status = "glasses not found"; manager.Dispose(); return null; }
            if (target.UsageKind == DisplayMonitorUsageKind.Standard)
            {
                Status = "glasses are on the desktop (Remove display from desktop is off)";
                manager.Dispose();
                return null;
            }
            var opened = new DirectDisplay(manager, target, wantHz);
            Status = $"direct: {opened.Width}x{opened.Height} at {opened.RefreshHz:F0} Hz";
            return opened;
        }
        catch (Exception e)
        {
            Status = "could not take the glasses: " + e.Message;
            manager?.Dispose();
            return null;
        }
    }

    DirectDisplay(DisplayManager manager, DisplayTarget target, double wantHz)
    {
        _manager = manager;
        _target = target;
        Name = target.TryGetMonitor()?.DisplayName ?? "glasses";
        Adapter = $"luid:{Luid(target.Adapter):x}";

        DisplayManagerResultWithState acquired = manager.TryAcquireTargetsAndCreateEmptyState([target]);
        if (acquired.ErrorCode != DisplayManagerResult.Success)
            throw new InvalidOperationException($"Windows refused the glasses ({acquired.ErrorCode})");
        DisplayState state = acquired.State;
        DisplayPath path = state.ConnectTarget(target);
        path.IsInterlaced = false;
        path.Scaling = DisplayPathScaling.Identity;
        path.SourcePixelFormat = DirectXPixelFormat.B8G8R8A8UIntNormalized;

        // Their own 1920x1080, at the refresh rate closest to the one asked for.
        DisplayModeInfo? best = null;
        double bestMiss = double.MaxValue;
        foreach (DisplayModeInfo mode in path.FindModes(DisplayModeQueryOptions.OnlyPreferredResolution))
        {
            if (mode.IsInterlaced || mode.IsStereo) continue;
            var rate = mode.PresentationRate.VerticalSyncRate;
            double hz = rate.Denominator == 0 ? 0 : (double)rate.Numerator / rate.Denominator;
            double miss = Math.Abs(hz - wantHz);
            if (miss < bestMiss) { best = mode; bestMiss = miss; }
        }
        if (best is null) throw new InvalidOperationException("the glasses offered no usable mode");
        path.ApplyPropertiesFromMode(best);
        DisplayStateOperationResult applied = state.TryApply(DisplayStateApplyOptions.None);
        if (applied.Status != DisplayStateOperationStatus.Success)
            throw new InvalidOperationException($"Windows would not set the glasses' mode ({applied.Status})");

        Width = best.SourceResolution.Width;
        Height = best.SourceResolution.Height;
        var r = best.PresentationRate.VerticalSyncRate;
        RefreshHz = r.Denominator == 0 ? 60 : (double)r.Numerator / r.Denominator;

        _device = manager.CreateDisplayDevice(target.Adapter);
        _source = _device.CreateScanoutSource(target);
        _tasks = _device.CreateTaskPool();
        var description = new DisplayPrimaryDescription((uint)Width, (uint)Height, DirectXPixelFormat.B8G8R8A8UIntNormalized,
            DirectXColorSpace.RgbFullG22NoneP709, false, new Windows.Graphics.DirectX.Direct3D11.Direct3DMultisampleDescription { Count = 1, Quality = 0 });
        for (int i = 0; i < 2; i++)
        {
            _surfaces[i] = _device.CreatePrimary(target, description);
            _scanouts[i] = _device.CreateSimpleScanout(_source, _surfaces[i], 0, 1);
        }
    }

    /// <summary>The glasses are still plugged in.</summary>
    public bool StillConnected
    {
        get
        {
            try { return !_target.IsStale && _target.IsConnected; }
            catch (Exception) { return false; }
        }
    }

    /// <summary>Opens the two scan-out pictures on the drawing device. Call once, on the drawing thread.</summary>
    public void Attach(ID3D11Device device)
    {
        using ID3D11Device1 device1 = device.QueryInterface<ID3D11Device1>();
        IntPtr interop = IntPtr.Zero;
        IntPtr unknown = ((IWinRTObject)_device).NativeObject.ThisPtr;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in s_displayDeviceInterop, out interop));
        try
        {
            for (int i = 0; i < 2; i++)
            {
                IntPtr surface = ((IWinRTObject)_surfaces[i]).NativeObject.ThisPtr;
                IntPtr handle = CreateSharedHandle(interop, surface);
                try
                {
                    _textures[i] = device1.OpenSharedResource1<ID3D11Texture2D>(handle);
                }
                finally
                {
                    CloseHandle(handle);
                }
            }
        }
        finally
        {
            Marshal.Release(interop);
        }
        _done = device.CreateQuery(new QueryDescription(QueryType.Event));
    }

    /// <summary>Waits for the start of the glasses' next refresh.</summary>
    public void WaitForRefresh() => _device.WaitForVBlank(_source);

    /// <summary>Puts the frame just drawn on the glasses at their next refresh.</summary>
    public void Show(Renderer renderer)
    {
        int i = _next;
        _next ^= 1;
        renderer.ResolveTo(_textures[i]);
        ID3D11DeviceContext context = renderer.Device.ImmediateContext;
        context.End(_done!);
        context.Flush();
        // The picture must be finished before it is scanned out; drawing takes well under a millisecond.
        var spin = System.Diagnostics.Stopwatch.StartNew();
        while (!context.IsDataAvailable(_done!) && spin.ElapsedMilliseconds < 20) Thread.SpinWait(50);
        DisplayTask task = _tasks.CreateTask();
        task.SetScanout(_scanouts[i]);
        _tasks.ExecuteTask(task);
    }

    public void Dispose()
    {
        _done?.Dispose();
        foreach (ID3D11Texture2D? t in _textures) t?.Dispose();
        _manager.Dispose(); // lets go of the glasses: Windows takes them back
    }

    static readonly Guid s_displayDeviceInterop = new("64338358-366A-471B-BD56-DD8EF48E439B");

    static unsafe IntPtr CreateSharedHandle(IntPtr interop, IntPtr surface)
    {
        // IDisplayDeviceInterop::CreateSharedHandle(IInspectable*, SECURITY_ATTRIBUTES*, DWORD access, HSTRING name, HANDLE*)
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, uint, IntPtr, IntPtr*, int>)(*(*(IntPtr**)interop + 3));
        IntPtr handle;
        Marshal.ThrowExceptionForHR(fn(interop, surface, IntPtr.Zero, 0x10000000 /* all access */, IntPtr.Zero, &handle));
        return handle;
    }

    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
}
