// Trackpad Visualizer: shows every finger on the trackpad with its real pressure and
// contact ellipse, read from the PTP reports through Raw Input.
//
//   MagicTrackpadVisualizer [--view grid|contours|glow] [--demo] [--lock] [--log <file.csv>]
//
// --demo  starts with simulated fingers (no trackpad needed; toggle with the D key)
// --view  picks the visualization to start with (switch with the V key)
// --lock  starts with the trackpad kept from driving Windows (toggle with the L key)
// --log   appends every contact of every report to a CSV file, for diagnostics
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace TrackpadVisualizer;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        string? logPath = null;
        bool demo = false, block = false;
        var view = ViewMode.Grid;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--demo") demo = true;
            else if (args[i] == "--lock") block = true;
            else if (args[i] == "--view" && i + 1 < args.Length && Enum.TryParse(args[++i], ignoreCase: true, out ViewMode chosen))
                view = chosen;
            else if (args[i] == "--log" && i + 1 < args.Length) logPath = args[++i];
        }
        new Application().Run(new MainWindow(demo, block, view, logPath));
    }
}

sealed class MainWindow : Window
{
    const int WM_INPUT = 0x00FF, WM_INPUT_DEVICE_CHANGE = 0x00FE;
    const double MaxBuzzSeconds = 8;   // a force click held longer than this stops buzzing (the picture keeps shaking)

    readonly HudView _view = new();
    readonly GpuSurface _surface = new();
    readonly TouchpadInput _input = new();
    readonly ActionBlocker _blocker = new();
    readonly ForceClick _force = new();
    readonly HapticFeedback _haptics = new();
    readonly DispatcherTimer _demoTimer = new() { Interval = TimeSpan.FromMilliseconds(11) };
    readonly DispatcherTimer _blockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    readonly StreamWriter? _log;
    double _nextBuzz;

    public MainWindow(bool demo, bool block, ViewMode view, string? logPath)
    {
        Title = "Magic Trackpad Visualizer";
        Width = 1040;
        Height = 600;
        MinWidth = 780;   // the control row has to fit
        MinHeight = 420;
        Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x11, 0x15));
        // The pad is drawn by the GPU surface, in the space the panel and controls leave free.
        _surface.Margin = HudView.SurfaceMargin;
        _surface.Mode = view;
        _surface.Failed += message => Dispatcher.BeginInvoke(() =>
        {
            _view.RendererError = message;
            _surface.Visibility = Visibility.Collapsed;   // nothing will be drawn into it
        });
        Content = new System.Windows.Controls.Grid { Children = { _view, _surface } };

        if (logPath != null)
        {
            _log = new StreamWriter(logPath, append: true);
            if (_log.BaseStream.Length == 0)
                _log.WriteLine("time_ms,scan_time,button,force,slot,id,tip,confidence,x,y,width,height,pressure,size,touch_major,touch_minor,orientation,finger,state");
        }

        _input.Frame += OnFrame;
        _demoTimer.Tick += (_, _) => Show(Demo.Frame(_clock.Elapsed.TotalSeconds), haptics: false);
        _view.Haptics = _haptics.State;
        _haptics.StateChanged += state => Dispatcher.BeginInvoke(() => _view.Haptics = state);

        _view.Controls =
        [
            new("View", Key.V, () => true, NextView, Value: () => _surface.Mode.ToString()),
            new("Force click", Key.F, () => _view.ForceClickEnabled, () => SetForceClick(!_view.ForceClickEnabled)),
            new("Haptics", Key.H, () => _view.HapticsEnabled, () => _view.HapticsEnabled = !_view.HapticsEnabled),
            new("Block trackpad", Key.L, () => _blocker.Enabled, () => SetBlocked(!_blocker.Enabled), Warn: true),
            new("Demo", Key.D, () => _view.DemoMode, () => SetDemo(!_view.DemoMode)),
            new("On top", Key.P, () => Topmost, () => Topmost = !Topmost),
        ];

        // Blocking changes how Windows treats the trackpad: always put that back, also on a
        // crash, and clean up after an earlier run that was killed before it could.
        WindowsTouchpad.RepairLeftover();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => _blocker.Dispose();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => _blocker.Dispose();
        // A mouse being plugged in or out changes which way of blocking works.
        _blockTimer.Tick += (_, _) => _view.Block = _blocker.Refresh();

        SetDemo(demo);
        SetBlocked(block);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        int dark = 1;
        DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));
        HwndSource.FromHwnd(hwnd).AddHook(WndProc);
        _blocker.Window = hwnd;
        if (!TouchpadInput.Register(hwnd))
            MessageBox.Show(this, "Could not register for touchpad Raw Input.", Title);
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Not marked as handled: Raw Input needs DefWindowProc to run for cleanup.
        if (msg == WM_INPUT) _input.ProcessInput(lParam);
        else if (msg == WM_INPUT_DEVICE_CHANGE) _input.DeviceChanged(wParam, lParam);
        return IntPtr.Zero;
    }

    void OnFrame(TouchFrame frame)
    {
        if (frame.Contacts.Length > 0 || frame.Button) _blocker.NoteTouch();
        if (_log != null)
        {
            long t = _clock.ElapsedMilliseconds;
            for (int i = 0; i < frame.Contacts.Length; i++)
            {
                var c = frame.Contacts[i];
                _log.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{t},{frame.ScanTime},{(frame.Button ? 1 : 0)},{frame.Force},{i},{c.Id},{(c.Tip ? 1 : 0)},{(c.Confident ? 1 : 0)},{c.X},{c.Y},{c.Width},{c.Height},{c.Pressure},{c.Size},{c.TouchMajor},{c.TouchMinor},{c.Orientation},{c.Finger},{c.State}"));
            }
        }
        if (!_view.DemoMode) Show(frame, haptics: true);
    }

    // Displays a frame and runs the force-click stage on it. Only real fingers make the
    // trackpad tick; simulated ones just show the stage.
    void Show(TouchFrame frame, bool haptics)
    {
        haptics &= _view.HapticsEnabled;   // the picture reacts to a force click either way
        if (_view.ForceClickEnabled)
        {
            var change = _force.Update(frame);
            double now = _clock.Elapsed.TotalSeconds;
            if (haptics && change == ForceClick.Change.Engaged)
            {
                _haptics.Fire(HapticFeedback.Firm);
                _nextBuzz = now + 0.25;
            }
            else if (haptics && change == ForceClick.Change.Released)
            {
                _haptics.Fire(HapticFeedback.Light);
            }
            else if (haptics && _force.Engaged && now >= _nextBuzz && _force.HeldSeconds < MaxBuzzSeconds)
            {
                // Held: the trackpad buzzes. The ticks start clearly weaker than the one that
                // set the force click off and grow to its strength, getting closer together as
                // the charge builds. Not faster than the driver can send them over Bluetooth.
                double charge = _force.Charge;
                _haptics.Fire(HapticFeedback.Strength(Math.Pow(charge, 1.3)));
                _nextBuzz = now + 0.22 - 0.13 * charge;
            }
        }
        _view.SetFrame(frame, _force.Engaged);
        _surface.SetFrame(frame, _view.ForceClickEnabled ? _force : null);
    }

    void SetDemo(bool on)
    {
        _view.DemoMode = on;
        _demoTimer.IsEnabled = on;
        _force.Reset();
        if (!on) Show(new TouchFrame { Device = TouchpadDevice.Demo }, haptics: false);
    }

    void NextView()
    {
        var modes = Enum.GetValues<ViewMode>();
        _surface.Mode = modes[(Array.IndexOf(modes, _surface.Mode) + 1) % modes.Length];
    }

    void SetForceClick(bool on)
    {
        _view.ForceClickEnabled = on;
        _force.Reset();
    }

    void SetBlocked(bool on)
    {
        _blocker.Enabled = on;
        _view.Block = _blocker.Mode;
        _blockTimer.IsEnabled = on;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        foreach (var control in _view.Controls)
        {
            if (e.Key != control.Key) continue;
            control.Toggle();
            _view.InvalidateVisual();
        }
        base.OnKeyDown(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _blocker.Dispose();
        _haptics.Dispose();
        _log?.Dispose();
        base.OnClosed(e);
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

/// <summary>Simulated fingers, so the rendering can be tried without a trackpad.</summary>
static class Demo
{
    public static TouchFrame Frame(double t)
    {
        var device = TouchpadDevice.Demo;
        var contacts = new List<Contact>
        {
            // The index finger leans in slowly and holds a force click for about three seconds.
            Finger(0, 2, 0.30 + 0.16 * Math.Cos(t * 0.9), 0.50 + 0.26 * Math.Sin(t * 1.3), 128 + 180 * Math.Sin(t * 0.9), 3),
            Finger(2, 1, 0.80 + 0.03 * Math.Sin(t * 0.6), 0.74 + 0.03 * Math.Cos(t * 0.8), 128 + 180 * Math.Sin(t * 0.9 - 1.6), (int)(t * 1.5) % 8),
        };
        // The middle finger lifts and comes back, hovering just before and after.
        double lift = Math.Sin(t * 0.8);
        if (lift > -0.7)
        {
            var middle = Finger(1, 3, 0.58 + 0.20 * Math.Cos(t * 0.7 + 1), 0.42 + 0.20 * Math.Sin(t * 1.1 + 2), 70 + 55 * Math.Sin(t * 3.0), 5);
            if (lift < -0.5)
            {
                (middle.Tip, middle.Pressure, middle.State) = (false, 0, 2);
            }
            contacts.Insert(1, middle);
        }

        int force = contacts.Sum(c => c.Pressure);
        return new TouchFrame
        {
            Device = device,
            Contacts = contacts.ToArray(),
            ScanTime = (int)(t * 10000) & 0xFFFF,
            Force = force,
            Button = contacts[0].Pressure > 235,
        };

        Contact Finger(uint id, int type, double x, double y, double pressure, int orientation)
        {
            int p = (int)Math.Clamp(pressure, 0, 255);
            int major = 95 + p / 4, minor = major * 3 / 4;
            return new Contact
            {
                Id = id, Tip = true, Confident = true,
                X = (int)(x * device.XMax), Y = (int)(y * device.YMax),
                Width = minor * 4, Height = major * 4,
                Pressure = p, Size = major, TouchMajor = major, TouchMinor = minor,
                Orientation = orientation, Finger = type, State = 4,
            };
        }
    }
}
