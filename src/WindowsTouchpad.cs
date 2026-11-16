// Windows' own Precision Touchpad state and settings (SPI_GET/SETTOUCHPADPARAMETERS,
// Windows 11 24H2 and later).
//
// The one setting used here is "leave touchpad on when a mouse is connected". With it off
// and a mouse present, Windows stops acting on the touchpad altogether (pointer, clicks,
// scrolling and the three- and four-finger gestures), while the Magic Trackpad driver keeps
// sending its reports, so the visualizer still sees every finger. The change is made
// without the "save to profile" flag: it lasts for this sign-in session at most and never
// touches the stored preference.
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace TrackpadVisualizer;

public static class WindowsTouchpad
{
    /// <param name="Active">Windows is acting on touchpad input.</param>
    /// <param name="AllowWithMouse">"Leave touchpad on when a mouse is connected".</param>
    public readonly record struct State(bool Present, bool MousePresent, bool Active, bool AllowWithMouse);

    /// <summary>Current state, or null where Windows has no such API (before 24H2).</summary>
    public static State? Get() =>
        Read(out var p)
            ? new State((p.Status & 1) != 0, (p.Status & 4) != 0, (p.Status & 16) != 0, (p.Settings & 1) != 0)
            : null;

    public static bool SetAllowWithMouse(bool allow)
    {
        if (!Read(out var p)) return false;
        p.Settings = allow ? p.Settings | 1 : p.Settings & ~1u;
        return SystemParametersInfoW(SPI_SETTOUCHPADPARAMETERS, (uint)Marshal.SizeOf<Params>(), ref p, 0);
    }

    /// <summary>Undoes a switch-off that an earlier run could not undo (it was killed):
    /// the live setting is off although the stored preference says on.</summary>
    public static void RepairLeftover()
    {
        if (Get() is not { AllowWithMouse: false }) return;
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad");
        bool storedOn = key?.GetValue("LeaveOnWithMouse") is not int value || value != 0;
        if (storedOn) SetAllowWithMouse(true);
    }

    static bool Read(out Params p)
    {
        p = new Params { Version = 1 };
        try
        {
            return SystemParametersInfoW(SPI_GETTOUCHPADPARAMETERS, (uint)Marshal.SizeOf<Params>(), ref p, 0);
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    const uint SPI_GETTOUCHPADPARAMETERS = 0x00AE, SPI_SETTOUCHPADPARAMETERS = 0x00AF;

    // TOUCHPAD_PARAMETERS_V1. Status bits: 0 touchpad present, 2 external mouse present,
    // 4 touchpad active. Settings bit 0: allowActiveWhenMousePresent.
    [StructLayout(LayoutKind.Sequential)]
    struct Params
    {
        public uint Version, MaxContacts, LegacyFeatures, Status, Settings, Sensitivity,
            CursorSpeed, FeedbackIntensity, ClickForce, RightClickZoneWidth, RightClickZoneHeight;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SystemParametersInfoW(uint action, uint param, ref Params value, uint flags);
}
