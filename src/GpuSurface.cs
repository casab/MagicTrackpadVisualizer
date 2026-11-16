// The pad itself, drawn on the GPU. A child window with a Direct3D 11 swap chain sits in the
// main window; a render thread draws one of the views into it every display refresh. Each
// view is a pair of HLSL shaders from the Shaders folder, and all of them get the same small
// block of data: where the fingers are, how hard they press and the shape of each contact.
// Adding a view means adding a shader file and a line in Views below.
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace TrackpadVisualizer;

public enum ViewMode
{
    Grid,       // a sheet seen at an angle that every finger presses a dent into
    Contours,   // pressure as a height map with contour lines, seen from above
    Glow,       // a soft spot per finger, seen from above
}

public sealed class GpuSurface : HwndHost
{
    const int MaxTouches = 8;                        // must match Common.hlsl
    const int GridColumns = 240, GridRows = 172;     // tessellation of the sheet in the Grid view
    const int ConstantFloats = 160;                  // size of the Frame block in Common.hlsl
    const double StaleMs = 1000;                     // drop the fingers if the reports stop
    const float PulseSeconds = 1.5f;                 // how long a force click's shock wave lasts

    // What every view is built from: (file, vertex shader, pixel shader, uses the sheet mesh)
    static readonly (ViewMode Mode, string File, string Vertex, string Pixel, bool Mesh)[] Views =
    [
        (ViewMode.Grid, "Grid.hlsl", "GridVS", "GridPS", true),
        (ViewMode.Contours, "Contours.hlsl", "FullscreenVS", "ContoursPS", false),
        (ViewMode.Glow, "Glow.hlsl", "FullscreenVS", "GlowPS", false),
    ];

    // A finger as the latest report gave it. Positions are fractions of the pad (y down),
    // pressure is 0..1, the contact semi-axes are fractions of the pad's width.
    struct Target
    {
        public uint Id;
        public float X, Y, Pressure, Rx, Ry, Angle;
        public bool Tip, Palm, Forced;   // Forced: this finger holds a force click
    }

    // A finger as it is drawn: it follows its target smoothly and fades out after lifting,
    // so a dent springs back instead of vanishing. Each finger has its own force click: the
    // shock wave it set off (where and when) and the charge it has built up by holding.
    struct Well
    {
        public bool Used, Seen, Palm, Forced;
        public uint Id;
        public float X, Y, Pressure, Presence, Rx, Ry, Angle;
        public float Charge, WaveX, WaveY;
        public double WaveStart;
    }

    readonly object _sync = new();
    readonly Target[] _targets = new Target[MaxTouches];
    int _targetCount;
    long _targetTime;
    bool _button;
    float _padAspect = (float)TouchpadDevice.Demo.Aspect;
    volatile int _mode;

    IntPtr _hwnd;
    Thread? _thread;
    volatile bool _running;

    /// <summary>Raised (on the render thread) if the GPU could not be set up or a shader did not compile.</summary>
    public event Action<string>? Failed;

    public ViewMode Mode
    {
        get => (ViewMode)_mode;
        set => _mode = (int)value;
    }

    /// <param name="force">Which fingers hold a force click, or null to show none.</param>
    public void SetFrame(TouchFrame frame, ForceClick? force)
    {
        var device = frame.Device;
        lock (_sync)
        {
            _padAspect = (float)device.Aspect;
            _button = frame.Button;
            _targetTime = Stopwatch.GetTimestamp();
            _targetCount = Math.Min(frame.Contacts.Length, MaxTouches);
            for (int i = 0; i < _targetCount; i++)
            {
                ref readonly var c = ref frame.Contacts[i];
                ref var t = ref _targets[i];
                t.Id = c.Id;
                t.X = Math.Clamp((float)c.X / device.XMax, 0, 1);
                t.Y = Math.Clamp((float)c.Y / device.YMax, 0, 1);
                t.Pressure = c.Pressure < 0 ? (c.Tip ? 0.3f : 0) : Math.Clamp(c.Pressure / 255f, 0, 1);
                t.Tip = c.Tip;
                t.Palm = !c.Confident || c.Finger == 6;
                t.Forced = force != null && force.IsEngaged(c.Id);

                // The Apple axes are full lengths in X units once shifted left by 2 (as Linux
                // hid-magicmouse does); orientation 4 means the major axis lies along Y and each
                // step is 22.5 degrees, with the sign hid-magicmouse uses for ABS_MT_ORIENTATION.
                t.Rx = t.Ry = 0.03f;
                t.Angle = 0;
                if (c.TouchMajor >= 0 && c.TouchMinor >= 0)
                {
                    t.Ry = Math.Max(c.TouchMajor, 1) * 2f / device.XMax;
                    t.Rx = Math.Max(c.TouchMinor, 1) * 2f / device.XMax;
                    // A round contact has no meaningful direction.
                    if (t.Ry > t.Rx * 1.2f) t.Angle = -(c.Orientation - 4) * 22.5f * MathF.PI / 180;
                }
                else if (c.Width > 0 && c.Height > 0)
                {
                    t.Rx = c.Width / 2f / device.XMax;
                    t.Ry = c.Height / 2f / device.YMax / (float)device.Aspect;
                }
            }
        }
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        // A STATIC control: it never takes the mouse, so clicks fall through to the window.
        _hwnd = CreateWindowExW(0, "STATIC", "", WS_CHILD | WS_VISIBLE, 0, 0, 16, 16,
            hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        _running = true;
        _thread = new Thread(RenderLoop) { IsBackground = true, Name = "GPU renderer" };
        _thread.Start();
        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        _running = false;
        _thread?.Join();
        DestroyWindow(hwnd.Handle);
    }

    // ---- Render thread --------------------------------------------------------------

    void RenderLoop()
    {
        try
        {
            Render();
        }
        catch (Exception e)
        {
            Failed?.Invoke(e.Message);
        }
    }

    void Render()
    {
        D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0],
            out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
        using var _1 = device;
        using var _2 = context;

        GetClientRect(_hwnd, out RECT client);
        int width = Math.Max(client.Right, 1), height = Math.Max(client.Bottom, 1);
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory2>();
        using var swapChain = factory.CreateSwapChainForHwnd(device, _hwnd, new SwapChainDescription1
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Ignore,
        });
        factory.MakeWindowAssociation(_hwnd, WindowAssociationFlags.IgnoreAll);

        // Shaders: every view is Common.hlsl followed by its own file. The sources are built
        // into the exe (see the project file) and compiled here, when the app starts.
        static string Shader(string file)
        {
            using var stream = typeof(GpuSurface).Assembly.GetManifestResourceStream("Shaders/" + file)
                ?? throw new InvalidOperationException($"{file} is missing from the app");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        string common = Shader("Common.hlsl");
        var vertexShaders = new ID3D11VertexShader[Views.Length];
        var pixelShaders = new ID3D11PixelShader[Views.Length];
        ID3D11InputLayout? meshLayout = null;
        for (int i = 0; i < Views.Length; i++)
        {
            string source = common + "\n" + Shader(Views[i].File);
            using var vertexCode = Compile(source, Views[i].File, Views[i].Vertex, "vs_4_0");
            using var pixelCode = Compile(source, Views[i].File, Views[i].Pixel, "ps_4_0");
            vertexShaders[i] = device.CreateVertexShader(vertexCode, null);
            pixelShaders[i] = device.CreatePixelShader(pixelCode, null);
            if (Views[i].Mesh)
                meshLayout ??= device.CreateInputLayout(
                    [new InputElementDescription("POSITION", 0, Format.R32G32_Float, 0, 0)], vertexCode);
        }

        // The sheet for the Grid view: a flat, finely divided rectangle. Rows are indexed
        // from the far edge to the near one, so nearer parts are drawn over farther ones
        // and a dent hides what lies behind its near wall without a depth buffer.
        var vertices = new float[(GridColumns + 1) * (GridRows + 1) * 2];
        for (int row = 0, v = 0; row <= GridRows; row++)
            for (int column = 0; column <= GridColumns; column++)
            {
                vertices[v++] = (float)column / GridColumns;
                vertices[v++] = (float)row / GridRows;
            }
        var indices = new uint[GridColumns * GridRows * 6];
        for (int row = 0, n = 0; row < GridRows; row++)
            for (int column = 0; column < GridColumns; column++)
            {
                uint a = (uint)(row * (GridColumns + 1) + column), b = a + 1;
                uint c = a + GridColumns + 1, d = c + 1;
                indices[n++] = a; indices[n++] = b; indices[n++] = c;
                indices[n++] = b; indices[n++] = d; indices[n++] = c;
            }
        using var vertexBuffer = device.CreateBuffer(vertices, BindFlags.VertexBuffer);
        using var indexBuffer = device.CreateBuffer(indices, BindFlags.IndexBuffer);
        using var constantBuffer = device.CreateBuffer(ConstantFloats * sizeof(float), BindFlags.ConstantBuffer);
        using var noCulling = device.CreateRasterizerState(RasterizerDescription.CullNone);

        ID3D11RenderTargetView? target = null;
        var constants = new float[ConstantFloats];
        var wells = new Well[MaxTouches];
        var snapshot = new Target[MaxTouches];
        var background = new Color4(0x0F / 255f, 0x11 / 255f, 0x15 / 255f, 1);
        float click = 0, force = 0;
        int stillFrames = 0, drawnMode = -1;
        var clock = Stopwatch.StartNew();
        double last = 0;

        try
        {
            while (_running)
            {
                // Follow the window's size.
                GetClientRect(_hwnd, out client);
                if (client.Right < 1 || client.Bottom < 1)
                {
                    Thread.Sleep(50);
                    continue;
                }
                bool resized = target == null || client.Right != width || client.Bottom != height;
                if (resized)
                {
                    target?.Dispose();
                    context.ClearState();
                    (width, height) = (client.Right, client.Bottom);
                    swapChain.ResizeBuffers(0, (uint)width, (uint)height, Format.Unknown).CheckError();
                    using var backBuffer = swapChain.GetBuffer<ID3D11Texture2D>(0);
                    target = device.CreateRenderTargetView(backBuffer, null);
                }

                double now = clock.Elapsed.TotalSeconds;
                float dt = (float)Math.Min(now - last, 0.1);
                last = now;

                // Latest report
                int count;
                bool button;
                float padAspect;
                lock (_sync)
                {
                    bool stale = Stopwatch.GetElapsedTime(_targetTime).TotalMilliseconds > StaleMs;
                    count = stale ? 0 : _targetCount;
                    Array.Copy(_targets, snapshot, count);
                    button = !stale && _button;
                    padAspect = _padAspect;
                }

                UpdateWells(wells, snapshot, count, dt, now);

                // With no fingers and nothing still moving, the picture does not change:
                // draw it twice (once per buffer) and then just wait.
                bool moving = count > 0 || click > 0.002f || force > 0.002f || _mode != drawnMode;
                for (int i = 0; i < MaxTouches && !moving; i++) moving = wells[i].Used;
                stillFrames = moving || resized ? 0 : stillFrames + 1;
                if (stillFrames > 2)
                {
                    Thread.Sleep(15);
                    continue;
                }
                click += ((button ? 1 : 0) - click) * (1 - MathF.Exp(-dt / 0.05f));
                // The strongest charge among the fingers, for what reacts to a force click as a whole.
                bool anyForced = false;
                float charge = 0;
                for (int i = 0; i < MaxTouches; i++)
                {
                    if (!wells[i].Used) continue;
                    anyForced |= wells[i].Forced;
                    charge = Math.Max(charge, wells[i].Charge);
                }
                force += ((anyForced ? 1 : 0) - force) * (1 - MathF.Exp(-dt / 0.06f));

                // The pad in pixels for the flat views: as large as fits, centred.
                float padWidth = Math.Min(width, height * padAspect), padHeight = padWidth / padAspect;

                var viewProjection = SheetCamera(padAspect, (float)width / height);
                MemoryMarshal.Cast<Matrix4x4, float>(MemoryMarshal.CreateSpan(ref viewProjection, 1)).CopyTo(constants);
                for (int i = 0; i < MaxTouches; i++)
                {
                    ref var w = ref wells[i];
                    Set(constants, 16 + i * 4, w.X, w.Y, w.Used ? w.Pressure : 0, w.Used ? w.Presence : 0);
                    Set(constants, 48 + i * 4, w.Rx, w.Ry, w.Angle, w.Palm ? 1 : 0);
                    float waveAge = w.Used ? (float)(now - w.WaveStart) : PulseSeconds;
                    Set(constants, 80 + i * 4, w.WaveX, w.WaveY, waveAge, Math.Clamp(1 - waveAge / PulseSeconds, 0, 1));
                    Set(constants, 112 + i * 4, w.Used ? w.Charge : 0, 0, 0, 0);
                }
                Set(constants, 144, width, height, (float)(now % 1000), padAspect);   // wrapped: a float loses precision on large times
                Set(constants, 148, click, force, charge, 0);
                Set(constants, 152, background.R, background.G, background.B, 1);
                Set(constants, 156, (width - padWidth) / 2, (height - padHeight) / 2, padWidth, padHeight);
                context.UpdateSubresource(constants, constantBuffer);

                int view = drawnMode = Math.Clamp(_mode, 0, Views.Length - 1);
                context.OMSetRenderTargets(target!, null);
                context.RSSetViewport(0, 0, width, height, 0, 1);
                context.RSSetState(noCulling);
                context.ClearRenderTargetView(target, background);
                context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                context.VSSetShader(vertexShaders[view]);
                context.PSSetShader(pixelShaders[view]);
                context.VSSetConstantBuffer(0, constantBuffer);
                context.PSSetConstantBuffer(0, constantBuffer);
                if (Views[view].Mesh)
                {
                    context.IASetInputLayout(meshLayout);
                    context.IASetVertexBuffer(0, vertexBuffer, 2 * sizeof(float), 0);
                    context.IASetIndexBuffer(indexBuffer, Format.R32_UInt, 0);
                    context.DrawIndexed((uint)indices.Length, 0, 0);
                }
                else
                {
                    context.IASetInputLayout(null);   // one triangle over the whole surface, made in the shader
                    context.Draw(3, 0);
                }
                swapChain.Present(1);
            }
        }
        finally
        {
            context.ClearState();
            target?.Dispose();
            meshLayout?.Dispose();
            foreach (var shader in vertexShaders) shader?.Dispose();
            foreach (var shader in pixelShaders) shader?.Dispose();
        }
    }

    static Blob Compile(string source, string file, string entry, string profile)
    {
        var result = Compiler.Compile(source, entry, file, profile, out Blob code, out Blob errors);
        if (result.Failure)
        {
            // The compiler's first line, e.g. "C:\...\Glow.hlsl(40,31): error X3004: ...", without
            // the folder. Line numbers count from the top of Common.hlsl, which comes first.
            string message = (errors?.AsString() ?? result.ToString()).Trim().Split('\n')[0].Trim();
            errors?.Dispose();
            int name = message.IndexOf(file, StringComparison.OrdinalIgnoreCase);
            throw new InvalidOperationException(name >= 0 ? message[name..] : $"{file}: {message}");
        }
        errors?.Dispose();
        return code;
    }

    // Moves every drawn finger towards its latest report and lets lifted ones fade out.
    static void UpdateWells(Well[] wells, Target[] targets, int count, float dt, double now)
    {
        for (int i = 0; i < wells.Length; i++) wells[i].Seen = false;

        for (int t = 0; t < count; t++)
        {
            ref var target = ref targets[t];
            int slot = -1;
            for (int i = 0; i < wells.Length && slot < 0; i++)
                if (wells[i].Used && wells[i].Id == target.Id) slot = i;
            if (slot < 0)
            {
                for (int i = 0; i < wells.Length && slot < 0; i++)
                    if (!wells[i].Used) slot = i;
                if (slot < 0) continue;
                wells[slot] = new Well
                {
                    Used = true, Id = target.Id, Rx = target.Rx, Ry = target.Ry, Angle = target.Angle,
                    WaveStart = now - PulseSeconds,   // no wave yet
                };
            }

            ref var w = ref wells[slot];
            w.Seen = true;
            w.Palm = target.Palm;
            // Entering the force click sets off this finger's shock wave from where it is now;
            // holding it charges this finger up, wherever it moves.
            if (target.Forced && !w.Forced) (w.WaveX, w.WaveY, w.WaveStart) = (target.X, target.Y, now);
            w.Forced = target.Forced;
            w.Charge = w.Forced
                ? Math.Min(w.Charge + dt / (float)ForceClick.ChargeSeconds, 1)
                : w.Charge * MathF.Exp(-dt / 0.12f);
            (w.X, w.Y) = (target.X, target.Y);   // position is not smoothed: it should track the finger exactly
            w.Pressure += (target.Pressure - w.Pressure) * (1 - MathF.Exp(-dt / 0.045f));
            w.Presence += ((target.Tip ? 1 : 0.15f) - w.Presence) * (1 - MathF.Exp(-dt / 0.05f));
            float shape = 1 - MathF.Exp(-dt / 0.09f);
            w.Rx += (target.Rx - w.Rx) * shape;
            w.Ry += (target.Ry - w.Ry) * shape;
            // An ellipse looks the same turned by half a turn: take the short way round.
            float turn = MathF.IEEERemainder(target.Angle - w.Angle, MathF.PI);
            w.Angle += turn * shape * 0.5f;
        }

        float fade = MathF.Exp(-dt / 0.14f);
        for (int i = 0; i < wells.Length; i++)
        {
            ref var w = ref wells[i];
            if (!w.Used || w.Seen) continue;
            w.Presence *= fade;
            w.Pressure *= fade;
            w.Forced = false;
            w.Charge *= MathF.Exp(-dt / 0.12f);
            // Gone once it has faded, but not before the shock wave it set off has run out.
            if (w.Presence < 0.004f && now - w.WaveStart > PulseSeconds) w.Used = false;
        }
    }

    // The camera of the Grid view: looks at the sheet from in front and above, from just far
    // enough that the whole sheet fits the surface. The sheet lies in the XY plane, one unit
    // deep (Y, away from the viewer) and `padAspect` wide, with Z up.
    static Matrix4x4 SheetCamera(float padAspect, float viewAspect)
    {
        const float elevation = 40 * MathF.PI / 180, fieldOfView = 32 * MathF.PI / 180;
        var lookAt = new Vector3(0, 0.02f, -0.07f);
        var direction = new Vector3(0, -MathF.Cos(elevation), MathF.Sin(elevation));
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(fieldOfView, viewAspect, 0.05f, 50);

        Matrix4x4 At(float distance) =>
            Matrix4x4.CreateLookAt(lookAt + direction * distance, lookAt, Vector3.UnitZ) * projection;

        bool Fits(float distance)
        {
            var matrix = At(distance);
            for (int corner = 0; corner < 4; corner++)
            {
                var point = new Vector4((corner & 1) == 0 ? -padAspect / 2 : padAspect / 2, (corner & 2) == 0 ? -0.5f : 0.5f, 0, 1);
                var clip = Vector4.Transform(point, matrix);
                if (clip.W <= 0 || MathF.Abs(clip.X / clip.W) > 0.95f || MathF.Abs(clip.Y / clip.W) > 0.9f) return false;
            }
            return true;
        }

        float near = 0.5f, far = 20;
        for (int i = 0; i < 24; i++)
        {
            float middle = (near + far) / 2;
            if (Fits(middle)) far = middle; else near = middle;
        }
        return At(far);
    }

    static void Set(float[] values, int index, float x, float y, float z, float w) =>
        (values[index], values[index + 1], values[index + 2], values[index + 3]) = (x, y, z, w);

    // ---- Win32 ----------------------------------------------------------------------

    const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000;

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
}
