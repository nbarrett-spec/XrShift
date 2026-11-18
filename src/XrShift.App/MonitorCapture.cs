using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace XrShift.App;

/// <summary>
/// A live copy of what one monitor is showing, handed over as graphics-card textures
/// (Windows.Graphics.Capture). Frames only arrive when something on that monitor changes.
/// </summary>
internal sealed class MonitorCapture : IDisposable
{
    const DirectXPixelFormat PixelFormat = DirectXPixelFormat.B8G8R8A8UIntNormalized;
    const double MaxCopiesPerSecond = 60;
    double _limit = MaxCopiesPerSecond;

    readonly IDirect3DDevice _device;
    readonly GraphicsCaptureItem _item;
    readonly Direct3D11CaptureFramePool _pool;
    readonly GraphicsCaptureSession _session;
    SizeInt32 _size;
    long _frames;

    public int Width => _size.Width;
    public int Height => _size.Height;
    public long Frames => Interlocked.Read(ref _frames);
    /// <summary>False if Windows insisted on drawing its yellow "being captured" border on the monitor.</summary>
    public bool Borderless { get; }
    /// <summary>The Windows handle of the monitor or window being copied. A monitor that is rebuilt gets a new one.</summary>
    public IntPtr Target { get; }
    /// <summary>True once Windows has ended the copy, e.g. because the monitor went away. No more frames will come.</summary>
    public bool Closed => _closed;

    volatile bool _closed;

    /// <summary>
    /// Changes how many copies a second Windows may send: 60 for reading, 120 for a screen that is played on.
    /// (Windows 11 24H2 and later; older versions copy as fast as the picture changes.)
    /// </summary>
    public void LimitTo(double perSecond)
    {
        perSecond = Math.Clamp(perSecond, 15, 240);
        if (perSecond == _limit) return;
        _limit = perSecond;
        try
        {
            if (Windows.Foundation.Metadata.ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "MinUpdateInterval"))
                _session.MinUpdateInterval = TimeSpan.FromMilliseconds(1000.0 / perSecond - 1);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>The handle Windows currently uses for the monitor covering this desktop area.</summary>
    public static IntPtr HandleFor(Rectangle monitorBounds)
    {
        var centre = new POINT { X = monitorBounds.X + monitorBounds.Width / 2, Y = monitorBounds.Y + monitorBounds.Height / 2 };
        return MonitorFromPoint(centre, 2 /* nearest */);
    }

    /// <param name="device">Must be the device that draws the scene, so frames can be copied without leaving the card.</param>
    /// <param name="monitorBounds">Desktop area of the monitor to copy.</param>
    public MonitorCapture(ID3D11Device device, Rectangle monitorBounds)
        : this(device, HandleFor(monitorBounds), isWindow: false)
    {
    }

    /// <summary>
    /// A live copy of one window instead of a whole monitor. It works while the window is behind others,
    /// but not while it is minimised, and its size follows the window's.
    /// </summary>
    public static MonitorCapture OfWindow(ID3D11Device device, IntPtr window) => new(device, window, isWindow: true);

    MonitorCapture(ID3D11Device device, IntPtr target, bool isWindow)
    {
        if (!GraphicsCaptureSession.IsSupported())
            throw new NotSupportedException("This version of Windows cannot capture a monitor.");

        Target = target;
        _item = CreateItem(target, isWindow);
        _item.Closed += (_, _) => _closed = true;
        _size = _item.Size;

        using (IDXGIDevice dxgiDevice = device.QueryInterface<IDXGIDevice>())
        {
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out IntPtr pointer));
            try { _device = MarshalInterface<IDirect3DDevice>.FromAbi(pointer); }
            finally { Marshal.Release(pointer); }
        }

        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, PixelFormat, 2, _size);
        _session = _pool.CreateCaptureSession(_item);
        _session.IsCursorCaptureEnabled = true; // the mouse pointer has to show on the floating screen

        // Ask Windows for at most 60 copies a second. A game can redraw a monitor hundreds of times a second,
        // and every copy of a monitor on the other graphics card has to cross between the cards; the weak
        // card that drives the laptop's own screen chokes on that. (Windows 11 24H2 and later.)
        try
        {
            if (Windows.Foundation.Metadata.ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "MinUpdateInterval"))
                _session.MinUpdateInterval = TimeSpan.FromMilliseconds(1000.0 / MaxCopiesPerSecond - 1);
        }
        catch (Exception)
        {
            // Older Windows: copies come as fast as the monitor changes.
        }

        try
        {
            RequestBorderless();
            _session.IsBorderRequired = false;
            Borderless = true;
        }
        catch (Exception)
        {
            Borderless = false;
        }

        _session.StartCapture();
    }

    static bool s_asked;

    /// <summary>
    /// Asks Windows, once, to let this app copy monitors without the yellow border. Desktop apps are
    /// normally just allowed. Best called from the window thread at start; captures made on other
    /// threads afterwards inherit the answer.
    /// </summary>
    public static void RequestBorderless()
    {
        if (s_asked) return;
        s_asked = true;
        try
        {
            GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless).AsTask().Wait(2000);
        }
        catch (Exception)
        {
            // Older Windows, or refused: captures still work, with the border.
        }
    }

    /// <summary>
    /// Gives the newest frame, if one has arrived since the last call, to <paramref name="use"/>.
    /// The texture is only valid inside the call.
    /// </summary>
    public bool TryTakeLatest(Action<ID3D11Texture2D> use)
    {
        Direct3D11CaptureFrame? newest = null;
        while (_pool.TryGetNextFrame() is { } frame)
        {
            newest?.Dispose();
            newest = frame;
        }
        if (newest is null) return false;

        using (newest)
        {
            SizeInt32 content = newest.ContentSize;
            if (content.Width != _size.Width || content.Height != _size.Height)
            {
                // The monitor changed resolution: this frame is the old shape, the next ones will be the new.
                _size = content;
                _pool.Recreate(_device, PixelFormat, 2, _size);
                return false;
            }

            var access = newest.Surface.As<IDirect3DDxgiInterfaceAccess>();
            Guid iid = typeof(ID3D11Texture2D).GUID;
            IntPtr pointer = access.GetInterface(ref iid);
            using var texture = new ID3D11Texture2D(pointer);
            use(texture);
        }
        Interlocked.Increment(ref _frames);
        return true;
    }

    public void Dispose()
    {
        _session.Dispose();
        _pool.Dispose();
        _device.Dispose();
    }

    static GraphicsCaptureItem CreateItem(IntPtr target, bool isWindow)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        Guid iid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760"); // IGraphicsCaptureItem
        IntPtr pointer = isWindow ? interop.CreateForWindow(target, ref iid) : interop.CreateForMonitor(target, ref iid);
        try { return GraphicsCaptureItem.FromAbi(pointer); }
        finally { Marshal.Release(pointer); }
    }

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
        IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
    }

    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromPoint(POINT point, uint flags);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
}
