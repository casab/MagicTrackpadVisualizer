// Everything around the pad: the side panel with the raw values of each finger, the status
// line and the row of clickable controls. The pad itself is drawn by GpuSurface, which sits
// in the area this view leaves free (SurfaceMargin).
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace TrackpadVisualizer;

/// <summary>A toggle shown in the control row: clickable, and switched with its key.</summary>
/// <param name="Value">For a control that steps through choices: the current one, shown after the label.</param>
public sealed record PadControl(string Label, Key Key, Func<bool> IsOn, Action Toggle, bool Warn = false,
    Func<string>? Value = null);

public sealed class HudView : FrameworkElement
{
    const double PanelWidth = 300, Gap = 28;
    const double ControlHeight = 30;                 // the row of clickable controls at the bottom
    const double StatusHeight = 34 + ControlHeight;  // status line + controls, below the pad
    const double StaleMs = 1000;  // drop contacts if the report stream stops (e.g. Bluetooth loss)
    const double MaxPressure = 255;

    static readonly Typeface Font = new("Segoe UI Variable Text, Segoe UI");
    static readonly Typeface FontBold = new(new FontFamily("Segoe UI Variable Text, Segoe UI"),
        FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    static readonly Color Blue = Color.FromRgb(0x0A, 0x84, 0xFF), Amber = Color.FromRgb(0xFF, 0x9F, 0x0A);

    /// <summary>Where the pad goes: the space this view leaves free, as margins of the window.</summary>
    public static readonly Thickness SurfaceMargin = new(Gap, Gap, PanelWidth + Gap, Gap + StatusHeight);

    static readonly Brush Background = Frozen(new SolidColorBrush(Color.FromRgb(0x0F, 0x11, 0x15)));
    static readonly Brush TextBright = Frozen(new SolidColorBrush(Color.FromRgb(0xF2, 0xF4, 0xF7)));
    static readonly Brush TextDim = Frozen(new SolidColorBrush(Color.FromRgb(0x8B, 0x93, 0xA1)));
    static readonly Brush Track = Frozen(new SolidColorBrush(Color.FromRgb(0x2A, 0x2F, 0x39)));
    static readonly Brush ControlHover = Frozen(new SolidColorBrush(Color.FromRgb(0x36, 0x3C, 0x48)));
    static readonly Brush ControlEdge = Frozen(new SolidColorBrush(Color.FromRgb(0x3D, 0x44, 0x51)));
    static readonly Brush KeyFill = Frozen(new SolidColorBrush(Color.FromArgb(0x40, 0x00, 0x00, 0x00)));
    static readonly Brush BlueBrush =Frozen(new SolidColorBrush(Blue));
    static readonly Brush AmberBrush = Frozen(new SolidColorBrush(Amber));

    static readonly string[] FingerNames = ["Finger", "Thumb", "Index", "Middle", "Ring", "Little", "Palm", "Finger"];
    static readonly string[] StateNames =
        ["none", "hover start", "hovering", "touch down", "touching", "lifting", "lingering", "leaving"];

    readonly Stopwatch _clock = Stopwatch.StartNew();
    TouchFrame? _frame;
    long _frameTime, _rateWindowStart;
    int _rateCount, _rate, _forceScale = 510;

    public bool DemoMode { get; set; }

    /// <summary>How the trackpad is currently kept from driving Windows, for the status line.</summary>
    public BlockMode Block
    {
        get => _block;
        set { _block = value; InvalidateVisual(); }
    }
    BlockMode _block;

    /// <summary>Set if the GPU view could not start; shown in the status line.</summary>
    public string? RendererError
    {
        get => _rendererError;
        set { _rendererError = value; InvalidateVisual(); }
    }
    string? _rendererError;

    /// <summary>The clickable controls drawn along the bottom.</summary>
    public IReadOnlyList<PadControl> Controls { get; set; } = [];

    readonly List<(Rect Bounds, PadControl Control)> _controlBounds = [];
    PadControl? _hover;

    /// <summary>Whether the force-click stage is shown at all (the F key).</summary>
    public bool ForceClickEnabled
    {
        get => _forceClickEnabled;
        set { _forceClickEnabled = value; _forceClick = false; InvalidateVisual(); }
    }
    bool _forceClickEnabled = true;

    /// <summary>Whether the trackpad can be made to tick, for the note in the side panel.</summary>
    public HapticState Haptics
    {
        get => _haptics;
        set { _haptics = value; InvalidateVisual(); }
    }
    HapticState _haptics;

    /// <summary>Whether a force click makes the trackpad tick and buzz (the H key). Off leaves the visuals.</summary>
    public bool HapticsEnabled
    {
        get => _hapticsEnabled;
        set { _hapticsEnabled = value; InvalidateVisual(); }
    }
    bool _hapticsEnabled = true;

    bool _forceClick, _changed;

    public HudView()
    {
        // Reports arrive about 90 times a second; the numbers are repainted at most 30 times
        // a second, and not at all while nothing changes.
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        timer.Tick += OnTick;
        timer.Start();
    }

    public void SetFrame(TouchFrame frame, bool forceClick)
    {
        _forceClick = forceClick;
        _frame = frame;
        _frameTime = _clock.ElapsedMilliseconds;
        _rateCount++;
        _changed = true;
    }

    void OnTick(object? sender, EventArgs e)
    {
        long now = _clock.ElapsedMilliseconds;
        bool dirty = _changed;
        _changed = false;

        if (_frame != null && _frame.Contacts.Length > 0 && now - _frameTime > StaleMs)
        {
            _frame = new TouchFrame { Device = _frame.Device };
            _forceClick = false;
            dirty = true;
        }

        if (now - _rateWindowStart >= 1000)
        {
            int rate = (int)Math.Round(_rateCount * 1000.0 / (now - _rateWindowStart));
            dirty |= rate != _rate;
            (_rate, _rateCount, _rateWindowStart) = (rate, 0, now);
        }

        if (dirty) InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = RenderSize;
        dc.DrawRectangle(Background, null, new Rect(size));
        if (size.Width < PanelWidth + 4 * Gap || size.Height < StatusHeight + 4 * Gap) return;

        DrawStatus(dc, new Point(Gap, size.Height - Gap - StatusHeight + 10), size.Width - PanelWidth - 2 * Gap);
        DrawControls(dc, new Point(Gap, size.Height - Gap - ControlHeight));
        DrawPanel(dc, new Rect(size.Width - PanelWidth, Gap, PanelWidth - Gap, size.Height - 2 * Gap - ControlHeight - 8));
    }

    // The row of toggles along the bottom. Each one can be clicked or switched with its key.
    void DrawControls(DrawingContext dc, Point origin)
    {
        _controlBounds.Clear();
        double x = origin.X;
        foreach (var control in Controls)
        {
            bool on = control.IsOn();
            var accent = control.Warn ? Amber : Blue;
            string caption = control.Value != null ? $"{control.Label}: {control.Value()}" : control.Label;
            var label = Text(caption, 12.5, on ? TextBright : TextDim, bold: on);
            var key = Text(control.Key.ToString(), 10.5, TextDim, bold: true);
            double keyWidth = Math.Max(18, key.Width + 10);
            var bounds = new Rect(x, origin.Y, 14 + label.Width + 10 + keyWidth + 7, ControlHeight);

            bool hover = control == _hover;
            var fill = on ? new SolidColorBrush(Alpha(accent, hover ? 0.34 : 0.24)) : hover ? ControlHover : Track;
            var border = new Pen(on ? new SolidColorBrush(Alpha(accent, 0.9)) : ControlEdge, 1);
            dc.DrawRoundedRectangle(fill, border, bounds, ControlHeight / 2, ControlHeight / 2);
            dc.DrawText(label, new Point(x + 14, origin.Y + (ControlHeight - label.Height) / 2));

            var keyBox = new Rect(bounds.Right - 7 - keyWidth, origin.Y + 6, keyWidth, ControlHeight - 12);
            dc.DrawRoundedRectangle(KeyFill, null, keyBox, 4, 4);
            dc.DrawText(key, new Point(keyBox.X + (keyBox.Width - key.Width) / 2, keyBox.Y + (keyBox.Height - key.Height) / 2));

            _controlBounds.Add((bounds, control));
            x = bounds.Right + 8;
        }
    }

    PadControl? ControlAt(Point point)
    {
        foreach (var (bounds, control) in _controlBounds)
            if (bounds.Contains(point)) return control;
        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var hover = ControlAt(e.GetPosition(this));
        if (hover == _hover) return;
        _hover = hover;
        Cursor = hover != null ? Cursors.Hand : null;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_hover == null) return;
        _hover = null;
        Cursor = null;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (ControlAt(e.GetPosition(this)) is not { } control) return;
        control.Toggle();
        e.Handled = true;
        InvalidateVisual();
    }

    static bool IsPalm(in Contact c) => !c.Confident || c.Finger == 6;

    void DrawStatus(DrawingContext dc, Point origin, double width)
    {
        string status;
        if (DemoMode) status = "Demo mode: simulated fingers";
        else if (_frame == null) status = "Waiting for touchpad input…";
        else
        {
            var d = _frame.Device;
            status = d.HasExtras ? "Magic Trackpad driver: pressure, contact size and orientation per finger"
                : d.HasPressure ? "Precision touchpad: positions and pressure"
                : "Precision touchpad: positions only (no per-finger pressure in its reports)";
            if (_rate > 0) status += $"   ·   {_rate} reports/s";
        }
        if (RendererError != null)
            status = "GPU view failed: " + RendererError;
        else if (Block == BlockMode.Ignored)
            status = "Windows is ignoring the trackpad: no pointer, clicks or gestures. Your mouse works as usual.";
        else if (Block == BlockMode.Confined)
            status = "No mouse connected: the trackpad's pointer stays inside this window. Windows gestures still work.";
        var line = Text(status, 12, Block != BlockMode.Off || RendererError != null ? AmberBrush : TextDim);
        line.MaxTextWidth = width;
        line.MaxLineCount = 1;
        line.Trimming = TextTrimming.CharacterEllipsis;
        dc.DrawText(line, origin);
    }

    void DrawPanel(DrawingContext dc, Rect area)
    {
        double y = area.Y;
        dc.DrawText(Text("FINGERS", 11, TextDim, bold: true), new Point(area.X, y));
        y += 26;

        var contacts = _frame?.Contacts ?? [];
        var device = _frame?.Device;
        if (contacts.Length == 0)
        {
            dc.DrawText(Text("No fingers on the trackpad", 13, TextDim), new Point(area.X, y));
            y += 30;
        }

        foreach (var c in contacts)
        {
            bool palm = IsPalm(c);
            var accent = palm ? AmberBrush : BlueBrush;
            string name = palm ? "Palm" : c.Finger >= 0 ? FingerNames[c.Finger & 7] : "Finger";
            if (!c.Tip) name += " (hover)";

            dc.DrawEllipse(accent, null, new Point(area.X + 5, y + 9), 4, 4);
            dc.DrawText(Text($"{name}   #{c.Id}", 13, TextBright, bold: true), new Point(area.X + 18, y));
            var value = Text(c.Pressure >= 0 ? c.Pressure.ToString(CultureInfo.InvariantCulture) : "–", 13, TextBright, bold: true);
            dc.DrawText(value, new Point(area.Right - value.Width, y));
            y += 24;

            // The notch marks where a force click engages.
            DrawBar(dc, new Rect(area.X, y, area.Width, 6), c.Pressure / MaxPressure, accent,
                notch: ForceClickEnabled && c.Pressure >= 0 ? ForceClick.EngageAt / MaxPressure : -1);
            y += 13;

            if (device?.HasExtras == true)
            {
                string size = device.MmPerUnit > 0
                    ? string.Create(CultureInfo.InvariantCulture,
                        $"{c.TouchMajor * 4 * device.MmPerUnit:0.0} × {c.TouchMinor * 4 * device.MmPerUnit:0.0} mm")
                    : $"{c.TouchMajor} × {c.TouchMinor}";
                string detail = string.Create(CultureInfo.InvariantCulture,
                    $"{size}   {(c.Orientation - 4) * 22.5:+0.#;-0.#;0}°   size {c.Size}   {StateNames[c.State & 7]}");
                dc.DrawText(Text(detail, 11, TextDim), new Point(area.X, y));
                y += 18;
            }
            y += 14;
        }

        y += 6;
        if (_frame == null)
        {
            DrawForceClickStatus(dc, area, y);
            return;
        }

        dc.DrawText(Text("TOTAL FORCE", 11, TextDim, bold: true), new Point(area.X, y));
        int force = _frame.Force >= 0 ? _frame.Force : contacts.Sum(c => Math.Max(c.Pressure, 0));
        if (force > _forceScale) _forceScale = force;
        var total = Text(device?.HasPressure == true ? force.ToString(CultureInfo.InvariantCulture) : "–", 13, TextBright, bold: true);
        dc.DrawText(total, new Point(area.Right - total.Width, y - 3));
        y += 24;
        DrawBar(dc, new Rect(area.X, y, area.Width, 6), (double)force / _forceScale, BlueBrush);
        y += 26;

        dc.DrawText(Text("BUTTON", 11, TextDim, bold: true), new Point(area.X, y));
        var button = _forceClick ? Text("Force click", 13, TextBright, bold: true)
            : Text(_frame.Button ? "Pressed" : "Up", 13, _frame.Button ? BlueBrush : TextDim, bold: true);
        dc.DrawText(button, new Point(area.Right - button.Width, y - 3));

        DrawForceClickStatus(dc, area, y + 32);
    }

    // Whether the force-click stage is on and whether it can make the trackpad tick.
    void DrawForceClickStatus(DrawingContext dc, Rect area, double y)
    {
        dc.DrawText(Text("FORCE CLICK", 11, TextDim, bold: true), new Point(area.X, y));
        bool haptic = ForceClickEnabled && HapticsEnabled && Haptics == HapticState.Ready;
        var value = Text(!ForceClickEnabled ? "Off" : haptic ? "Haptic" : "Visual only", 13,
            haptic ? BlueBrush : TextDim, bold: true);
        dc.DrawText(value, new Point(area.Right - value.Width, y - 3));
        if (!ForceClickEnabled) return;

        string note = !HapticsEnabled ? "Haptics are switched off: a force click shows but the trackpad stays still." : Haptics switch
        {
            HapticState.Ready => $"Press past the click, to {ForceClick.EngageAt}. The trackpad ticks when it engages and buzzes while you hold it.",
            HapticState.AccessDenied => "No tick from the trackpad yet: the driver only lets administrators fire it. "
                + "Tick “Let non-admin apps communicate with the drivers” in the control panel, "
                + "then switch the trackpad off and on.",
            HapticState.Unavailable => "No tick from the trackpad: no driver with on-demand haptics was found.",
            _ => "",
        };
        var text = Text(note, 11, TextDim);
        text.MaxTextWidth = area.Width;
        dc.DrawText(text, new Point(area.X, y + 20));
    }

    static void DrawBar(DrawingContext dc, Rect rect, double fraction, Brush fill, double notch = -1)
    {
        dc.DrawRoundedRectangle(Track, null, rect, 3, 3);
        fraction = double.IsFinite(fraction) ? Math.Clamp(fraction, 0, 1) : 0;
        if (fraction > 0)
            dc.DrawRoundedRectangle(fill, null, new Rect(rect.X, rect.Y, Math.Max(6, rect.Width * fraction), rect.Height), 3, 3);
        if (notch >= 0)
            dc.DrawRectangle(TextDim, null, new Rect(rect.X + rect.Width * notch - 1, rect.Y - 3, 2, rect.Height + 6));
    }

    FormattedText Text(string text, double size, Brush brush, bool bold = false) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, bold ? FontBold : Font, size, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    static Color Alpha(Color color, double alpha) =>
        Color.FromArgb((byte)Math.Round(255 * Math.Clamp(alpha, 0, 1)), color.R, color.G, color.B);

    static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
