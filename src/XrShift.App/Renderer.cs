using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using XrShift.Scene;

namespace XrShift.App;

/// <summary>One thing to draw: a shape and the picture on it.</summary>
internal sealed class SceneItem : IDisposable
{
    public required ID3D11Buffer Vertices { get; set; }
    public required ID3D11Buffer Indices { get; set; }
    public required int IndexCount { get; set; }
    public required ID3D11Texture2D Texture { get; set; }
    public required ID3D11ShaderResourceView View { get; set; }
    public required System.Drawing.Size PictureSize { get; set; }
    /// <summary>Fixed to the view (status text, crosshair) instead of to the room.</summary>
    public bool HeadLocked { get; init; }
    /// <summary>Drawn far to near, so nearer things cover farther ones.</summary>
    public float Distance { get; set; }
    public bool Visible { get; set; } = true;
    /// <summary>Brightness, 1 = as is. Screens not looked at can be dimmed.</summary>
    public float Tint { get; set; } = 1f;
    /// <summary>Reading without glasses: how much edges are sharpened (0 = not at all).</summary>
    public float Sharpen { get; set; }
    /// <summary>Contrast, 1 = as it is.</summary>
    public float Contrast { get; set; } = 1f;
    /// <summary>
    /// Whether the picture keeps smaller copies of itself for when the screen is drawn small. They make
    /// shrunken text readable, but making them costs graphics-card time on every update, so a screen
    /// shown at about full size does without.
    /// </summary>
    public bool Mips { get; set; } = true;

    public void Dispose()
    {
        View.Dispose();
        Texture.Dispose();
        Indices.Dispose();
        Vertices.Dispose();
    }
}

/// <summary>
/// Draws the scene for one head pose into an anti-aliased off-screen frame, which can then go
/// to a window or be read back as a picture. All drawing calls must come from one thread.
/// </summary>
internal sealed class Renderer : IDisposable
{
    const uint Samples = 4;
    const Format ColorFormat = Format.B8G8R8A8_UNorm;

    const string ShaderSource = """
        cbuffer Draw : register(b0)
        {
            row_major float4x4 Transform;
            float4 Tint;
            float4 Clarity; // x: sharpening, y: contrast, zw: one picture pixel in texture units
        };
        Texture2D Picture : register(t0);
        SamplerState Smooth : register(s0);

        struct VertexIn { float3 position : POSITION; float2 uv : TEXCOORD0; };
        struct PixelIn { float4 position : SV_POSITION; float2 uv : TEXCOORD0; };

        PixelIn VS(VertexIn v)
        {
            PixelIn p;
            p.position = mul(float4(v.position, 1), Transform);
            p.uv = v.uv;
            return p;
        }

        float4 PS(PixelIn p) : SV_TARGET
        {
            float4 c = Picture.Sample(Smooth, p.uv);
            if (Clarity.x > 0)
            {
                // Edges stand out: each pixel pushed away from the average of its four neighbours.
                float2 t = Clarity.zw;
                float3 around = (Picture.Sample(Smooth, p.uv + float2(t.x, 0)).rgb + Picture.Sample(Smooth, p.uv - float2(t.x, 0)).rgb
                               + Picture.Sample(Smooth, p.uv + float2(0, t.y)).rgb + Picture.Sample(Smooth, p.uv - float2(0, t.y)).rgb) * 0.25;
                c.rgb = saturate(c.rgb + Clarity.x * (c.rgb - around));
            }
            c.rgb = saturate((c.rgb - 0.5) * Clarity.y + 0.5);
            return c * Tint;
        }
        """;

    [StructLayout(LayoutKind.Sequential)]
    struct DrawConstants
    {
        public Matrix4x4 Transform;
        public Vector4 Tint;
        public Vector4 Clarity;
    }

    readonly ID3D11DeviceContext _context;
    readonly ID3D11Texture2D _color, _depth, _resolved, _staging;
    readonly ID3D11RenderTargetView _colorView;
    readonly ID3D11DepthStencilView _depthView;
    readonly ID3D11VertexShader _vertexShader;
    readonly ID3D11PixelShader _pixelShader;
    readonly ID3D11InputLayout _layout;
    readonly ID3D11Buffer _constants;
    readonly ID3D11SamplerState _sampler;
    readonly ID3D11RasterizerState _rasterizer;
    readonly ID3D11DepthStencilState _depthState;
    readonly ID3D11BlendState _blend;
    readonly List<SceneItem> _items = [];

    public ID3D11Device Device { get; }
    public int Width { get; }
    public int Height { get; }
    /// <summary>True when no graphics card would take the job and Windows' software renderer is drawing.</summary>
    public bool Software { get; }
    /// <summary>The graphics card doing the drawing.</summary>
    public string AdapterName { get; } = "";
    public Color4 Background { get; set; } = new(0.012f, 0.016f, 0.024f, 1f);

    /// <param name="monitor">
    /// The Windows name of the monitor the frames will go to (e.g. "\\.\DISPLAY5"). On a laptop with two
    /// graphics cards the frames must be drawn by the card that monitor is plugged into; otherwise Windows
    /// copies every frame between the cards, which costs speed and adds delay.
    /// </param>
    public Renderer(int width, int height, string? monitor = null)
    {
        Width = width;
        Height = height;

        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];
        ID3D11Device? device = null;
        ID3D11DeviceContext? context = null;

        using (IDXGIAdapter1? adapter = monitor is null ? null : FindAdapterDriving(monitor))
        {
            if (adapter is not null &&
                D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, levels, out device, out context).Failure)
            {
                device = null;
            }
        }
        if (device is null &&
            D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels, out device, out context).Failure)
        {
            D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.BgraSupport, levels, out device, out context).CheckError();
            Software = true;
        }
        Device = device!;
        _context = context!;

        using (IDXGIDevice dxgiDevice = Device.QueryInterface<IDXGIDevice>())
        using (IDXGIAdapter used = dxgiDevice.GetAdapter())
        {
            AdapterName = used.Description.Description;
            // When a game is loading the graphics card, this app's few draw calls should still go first:
            // a late frame here is a floating screen that jerks.
            try { dxgiDevice.SetGPUThreadPriority(7); } catch (Exception) { }
        }
        try { D3DKMTSetProcessSchedulingPriorityClass(System.Diagnostics.Process.GetCurrentProcess().Handle, 4 /* high */); }
        catch (Exception) { }

        _color = Device.CreateTexture2DMultisample(ColorFormat, (uint)width, (uint)height, Samples, 1, BindFlags.RenderTarget);
        _depth = Device.CreateTexture2DMultisample(Format.D32_Float, (uint)width, (uint)height, Samples, 1, BindFlags.DepthStencil);
        _colorView = Device.CreateRenderTargetView(_color);
        _depthView = Device.CreateDepthStencilView(_depth);
        _resolved = Device.CreateTexture2D(ColorFormat, (uint)width, (uint)height, 1, 1, null, BindFlags.RenderTarget);
        _staging = Device.CreateTexture2D(ColorFormat, (uint)width, (uint)height, 1, 1, null, BindFlags.None,
            ResourceOptionFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read);

        ReadOnlyMemory<byte> vertexCode = Compiler.Compile(ShaderSource, "VS", "scene.hlsl", "vs_5_0");
        ReadOnlyMemory<byte> pixelCode = Compiler.Compile(ShaderSource, "PS", "scene.hlsl", "ps_5_0");
        _vertexShader = Device.CreateVertexShader(vertexCode.Span);
        _pixelShader = Device.CreatePixelShader(pixelCode.Span);
        _layout = Device.CreateInputLayout(
        [
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 12, 0),
        ], vertexCode.Span);

        _constants = Device.CreateBuffer((uint)Marshal.SizeOf<DrawConstants>(), BindFlags.ConstantBuffer);

        // Screens are usually seen at an angle and a little smaller than their pixels: mip maps plus
        // 16x anisotropic sampling is what keeps text on them readable.
        // The small negative bias leans toward the sharper mip level: a screen shown a little under
        // its own size keeps crisp text instead of being softened.
        _sampler = Device.CreateSamplerState(new SamplerDescription(Filter.Anisotropic, TextureAddressMode.Clamp, -0.5f, 16));

        // Screen capture hands over frames from Windows' own threads on this same device.
        using (ID3D11Multithread threads = Device.QueryInterface<ID3D11Multithread>())
            threads.SetMultithreadProtected(true);

        RasterizerDescription raster = RasterizerDescription.CullNone;
        raster.MultisampleEnable = true;
        _rasterizer = Device.CreateRasterizerState(raster);
        _depthState = Device.CreateDepthStencilState(DepthStencilDescription.Default);
        _blend = Device.CreateBlendState(BlendDescription.AlphaBlend); // pictures carry premultiplied alpha
    }

    [DllImport("gdi32.dll")]
    static extern int D3DKMTSetProcessSchedulingPriorityClass(IntPtr process, int priorityClass);

    /// <summary>Every graphics card Windows has, with the monitors each one is drawing. For diagnosis.</summary>
    public static List<(string Adapter, string Luid, List<string> Monitors)> AdaptersAndMonitors()
    {
        var result = new List<(string, string, List<string>)>();
        using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out IDXGIAdapter1? adapter).Success && adapter is not null; a++)
        {
            var monitors = new List<string>();
            for (uint o = 0; adapter.EnumOutputs(o, out IDXGIOutput? output).Success && output is not null; o++)
            {
                monitors.Add(output.Description.DeviceName);
                output.Dispose();
            }
            AdapterDescription1 d = adapter.Description1;
            result.Add((d.Description, $"0x{d.Luid.HighPart:x8}_0x{d.Luid.LowPart:x8}", monitors));
            adapter.Dispose();
        }
        return result;
    }

    /// <summary>The graphics card whose outputs include the named monitor, or null if none claims it.</summary>
    static IDXGIAdapter1? FindAdapterDriving(string monitor)
    {
        using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        // "luid:<hex>" names the card itself (direct mode: the glasses are not a desktop monitor then).
        long? luid = monitor.StartsWith("luid:", StringComparison.Ordinal)
            && long.TryParse(monitor[5..], System.Globalization.NumberStyles.HexNumber, null, out long l) ? l : null;
        for (uint a = 0; factory.EnumAdapters1(a, out IDXGIAdapter1? adapter).Success && adapter is not null; a++)
        {
            if (luid is { } want)
            {
                var id = adapter.Description1.Luid;
                if ((((long)id.HighPart << 32) | id.LowPart) == want) return adapter;
                adapter.Dispose();
                continue;
            }
            for (uint o = 0; adapter.EnumOutputs(o, out IDXGIOutput? output).Success && output is not null; o++)
            {
                string name = output.Description.DeviceName;
                output.Dispose();
                if (string.Equals(name, monitor, StringComparison.OrdinalIgnoreCase)) return adapter;
            }
            adapter.Dispose();
        }
        return null;
    }

    /// <summary>Adds a shape with a picture on it. The picture can be swapped later with <see cref="SetPicture"/>.</summary>
    public SceneItem Add(Mesh mesh, Bitmap picture, bool headLocked = false, float distance = 0)
    {
        SceneItem item = Add(mesh, picture.Width, picture.Height, headLocked, distance);
        SetPicture(item, picture);
        return item;
    }

    /// <summary>Adds a shape whose picture will be filled in later, e.g. from a live screen capture.</summary>
    public SceneItem Add(Mesh mesh, int pictureWidth, int pictureHeight, bool headLocked = false, float distance = 0, bool mips = true)
    {
        ID3D11Texture2D texture = CreatePictureTexture(pictureWidth, pictureHeight, mips);
        var item = new SceneItem
        {
            Vertices = Device.CreateBuffer(mesh.Vertices, BindFlags.VertexBuffer),
            Indices = Device.CreateBuffer(mesh.Indices, BindFlags.IndexBuffer),
            IndexCount = mesh.Indices.Length,
            Texture = texture,
            View = Device.CreateShaderResourceView(texture),
            PictureSize = new System.Drawing.Size(pictureWidth, pictureHeight),
            HeadLocked = headLocked,
            Distance = distance,
            Mips = mips,
        };

        _items.Add(item);
        Sort();
        return item;
    }

    /// <summary>Far to near; anything fixed to the view goes on top.</summary>
    void Sort() => _items.Sort((a, b) => a.HeadLocked != b.HeadLocked
        ? a.HeadLocked.CompareTo(b.HeadLocked)
        : b.Distance.CompareTo(a.Distance));

    /// <summary>Takes an item out of the scene and frees what it held.</summary>
    public void Remove(SceneItem item)
    {
        if (_items.Remove(item)) item.Dispose();
    }

    /// <summary>Moves or reshapes an item, keeping its picture.</summary>
    public void SetMesh(SceneItem item, Mesh mesh, float distance)
    {
        item.Vertices.Dispose();
        item.Indices.Dispose();
        item.Vertices = Device.CreateBuffer(mesh.Vertices, BindFlags.VertexBuffer);
        item.Indices = Device.CreateBuffer(mesh.Indices, BindFlags.IndexBuffer);
        item.IndexCount = mesh.Indices.Length;
        if (item.Distance != distance)
        {
            item.Distance = distance;
            Sort();
        }
    }

    ID3D11Texture2D CreatePictureTexture(int width, int height, bool mips)
    {
        var description = new Texture2DDescription(ColorFormat, (uint)width, (uint)height, 1, mips ? 0u : 1u,
            BindFlags.ShaderResource | BindFlags.RenderTarget, ResourceUsage.Default, CpuAccessFlags.None, 1, 0,
            mips ? ResourceOptionFlags.GenerateMips : ResourceOptionFlags.None);
        return Device.CreateTexture2D(in description);
    }

    /// <summary>Gives an item a new shape and a new (empty) picture size, e.g. when a captured monitor changes resolution.</summary>
    public void Reshape(SceneItem item, Mesh mesh, int pictureWidth, int pictureHeight, bool mips = true)
    {
        item.Mips = mips;
        item.Vertices.Dispose();
        item.Indices.Dispose();
        item.View.Dispose();
        item.Texture.Dispose();

        item.Vertices = Device.CreateBuffer(mesh.Vertices, BindFlags.VertexBuffer);
        item.Indices = Device.CreateBuffer(mesh.Indices, BindFlags.IndexBuffer);
        item.IndexCount = mesh.Indices.Length;
        item.Texture = CreatePictureTexture(pictureWidth, pictureHeight, mips);
        item.View = Device.CreateShaderResourceView(item.Texture);
        item.PictureSize = new System.Drawing.Size(pictureWidth, pictureHeight);
    }

    /// <summary>Replaces an item's picture with a texture of the same size that is already on the graphics card.</summary>
    public void SetPicture(SceneItem item, ID3D11Texture2D source)
    {
        _context.CopySubresourceRegion(item.Texture, 0, 0, 0, 0, source, 0);
        if (item.Mips) _context.GenerateMips(item.View);
    }

    /// <summary>Replaces an item's picture with one of the same size.</summary>
    public void SetPicture(SceneItem item, Bitmap picture)
    {
        if (picture.Size != item.PictureSize)
            throw new ArgumentException("The new picture must be the same size as the old one.", nameof(picture));

        BitmapData data = picture.LockBits(new Rectangle(Point.Empty, picture.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            _context.UpdateSubresource(item.Texture, 0, null, data.Scan0, (uint)data.Stride, 0);
        }
        finally
        {
            picture.UnlockBits(data);
        }
        if (item.Mips) _context.GenerateMips(item.View);
    }

    /// <summary>Draws every visible item as seen with the head at <paramref name="head"/>.</summary>
    /// <param name="eyeSeparation">
    /// 0 for one sharp picture. Above 0 (metres between the eyes) for 3D: one picture per eye, side by side,
    /// for the glasses in their 3D side-by-side mode.
    /// </param>
    public void Draw(Quaternion head, DisplayOptics optics, float eyeSeparation = 0)
    {
        _context.OMSetRenderTargets(_colorView, _depthView);
        _context.ClearRenderTargetView(_colorView, Background);
        _context.ClearDepthStencilView(_depthView, DepthStencilClearFlags.Depth, 1f, 0);

        // Keep the picture's shape: fit the display's aspect inside the frame.
        float scale = MathF.Min(Width / (float)optics.Width, Height / (float)optics.Height);
        float viewWidth = optics.Width * scale, viewHeight = optics.Height * scale;
        float left = (Width - viewWidth) / 2f, top = (Height - viewHeight) / 2f;

        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.IASetInputLayout(_layout);
        _context.VSSetShader(_vertexShader);
        _context.PSSetShader(_pixelShader);
        _context.VSSetConstantBuffer(0, _constants);
        _context.PSSetConstantBuffer(0, _constants);
        _context.PSSetSampler(0, _sampler);
        _context.RSSetState(_rasterizer);
        _context.OMSetDepthStencilState(_depthState);
        _context.OMSetBlendState(_blend);

        Matrix4x4 projection = optics.Projection();
        if (eyeSeparation <= 0)
        {
            _context.RSSetViewport(left, top, viewWidth, viewHeight);
            DrawItems(projection, Camera.View(head) * projection);
            return;
        }
        foreach (bool rightEye in new[] { false, true })
        {
            (float x, float y, float w, float h) = Camera.EyeViewport(Width, Height, optics, rightEye);
            _context.RSSetViewport(x, y, w, h);
            float eyeX = (rightEye ? 0.5f : -0.5f) * eyeSeparation;
            // Things fixed to the view (status line, crosshair) are shifted too, so they sit at their own depth
            // instead of impossibly far away.
            DrawItems(Matrix4x4.CreateTranslation(-eyeX, 0, 0) * projection, Camera.EyeView(head, eyeX) * projection);
        }
    }

    void DrawItems(Matrix4x4 projection, Matrix4x4 viewProjection)
    {
        foreach (SceneItem item in _items)
        {
            if (!item.Visible) continue;

            var constants = new DrawConstants
            {
                Transform = item.HeadLocked ? projection : viewProjection,
                Tint = new Vector4(item.Tint, item.Tint, item.Tint, 1f),
                Clarity = new Vector4(item.Sharpen, item.Contrast,
                    1f / Math.Max(item.PictureSize.Width, 1), 1f / Math.Max(item.PictureSize.Height, 1)),
            };
            _context.UpdateSubresource(in constants, _constants);
            _context.IASetVertexBuffer(0, item.Vertices, (uint)Marshal.SizeOf<MeshVertex>());
            _context.IASetIndexBuffer(item.Indices, Format.R16_UInt, 0);
            _context.PSSetShaderResource(0, item.View);
            _context.DrawIndexed((uint)item.IndexCount, 0, 0);
        }
    }

    /// <summary>Copies the finished frame to a window's back buffer of the same size.</summary>
    public void ResolveTo(ID3D11Texture2D target) =>
        _context.ResolveSubresource(target, 0, _color, 0, ColorFormat);

    int _previewAge;

    /// <summary>
    /// A small copy of what the glasses show, for the settings window, without making the drawing wait: one call
    /// starts copying the finished frame, and a call a few frames later (when the card is surely done) reads it
    /// back at a quarter of the size. Returns the picture on that later call, otherwise null.
    /// </summary>
    public Bitmap? PreviewStep(bool start)
    {
        if (_previewAge == 0)
        {
            if (!start) return null;
            _context.ResolveSubresource(_resolved, 0, _color, 0, ColorFormat);
            _context.CopyResource(_staging, _resolved);
            _previewAge = 1;
            return null;
        }
        if (++_previewAge < 4) return null;
        _previewAge = 0;

        const int Step = 4;
        int w = Width / Step, h = Height / Step;
        var picture = new Bitmap(w, h, PixelFormat.Format32bppRgb);
        BitmapData data = picture.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        MappedSubresource mapped = _context.Map(_staging, 0, MapMode.Read);
        try
        {
            unsafe
            {
                for (int y = 0; y < h; y++)
                {
                    uint* from = (uint*)((byte*)mapped.DataPointer + (long)y * Step * mapped.RowPitch);
                    uint* to = (uint*)((byte*)data.Scan0 + (long)y * data.Stride);
                    for (int x = 0; x < w; x++) to[x] = from[x * Step];
                }
            }
        }
        finally
        {
            _context.Unmap(_staging, 0);
            picture.UnlockBits(data);
        }
        return picture;
    }

    /// <summary>Reads the finished frame back as a picture.</summary>
    public Bitmap Capture()
    {
        _context.ResolveSubresource(_resolved, 0, _color, 0, ColorFormat);
        _context.CopyResource(_staging, _resolved);

        var picture = new Bitmap(Width, Height, PixelFormat.Format32bppRgb);
        BitmapData data = picture.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        MappedSubresource mapped = _context.Map(_staging, 0, MapMode.Read);
        try
        {
            unsafe
            {
                for (int y = 0; y < Height; y++)
                {
                    Buffer.MemoryCopy(
                        (byte*)mapped.DataPointer + (long)y * mapped.RowPitch,
                        (byte*)data.Scan0 + (long)y * data.Stride,
                        data.Stride, Width * 4L);
                }
            }
        }
        finally
        {
            _context.Unmap(_staging, 0);
            picture.UnlockBits(data);
        }
        return picture;
    }

    public void Dispose()
    {
        foreach (SceneItem item in _items) item.Dispose();
        _blend.Dispose();
        _depthState.Dispose();
        _rasterizer.Dispose();
        _sampler.Dispose();
        _constants.Dispose();
        _layout.Dispose();
        _pixelShader.Dispose();
        _vertexShader.Dispose();
        _depthView.Dispose();
        _colorView.Dispose();
        _staging.Dispose();
        _resolved.Dispose();
        _depth.Dispose();
        _color.Dispose();
        _context.ClearState();
        _context.Flush();
        _context.Dispose();
        Device.Dispose();
    }
}
