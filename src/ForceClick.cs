// Force click: a second, deeper stage past the ordinary click, like on macOS. The trackpad
// clicks by itself at a finger pressure of about 65 and the pressure keeps rising up to 255
// while the finger pushes harder, so the stage is a pressure threshold above the click.
// Crossing it fires the trackpad's haptic actuator through the driver's on-demand interface.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TrackpadVisualizer;

/// <summary>Tracks, for every finger on its own, whether it is in the force-click stage.
/// Each finger engages by its own pressure (or, when the pad's total pressure is at its
/// ceiling, by the size of its contact), so several fingers can hold a force click at the
/// same time and none takes it over from another.</summary>
public sealed class ForceClick
{
    // Engage well above the click (about 65) and let go lower, so a finger hovering around
    // the threshold does not make the stage chatter.
    public const int EngageAt = 170, ReleaseAt = 130;

    // The trackpad's pressures have a ceiling in total. Measured on a Magic Trackpad with
    // three and four fingers pressing as hard as possible, the sum over all fingers stayed
    // between about 340 and 480, and the trackpad split it unevenly and differently from
    // moment to moment: fingers pressing equally hard read e.g. 199, 181 and 0. So with
    // three or more fingers not every one can reach EngageAt, however hard they press.
    // What stays steady is the contact size: 26..40 for every finger pressing hard, against
    // a median of 18 for a resting one. So once the pad is that full, a finger also counts
    // as force-clicking when its contact is as large as a hard press. Without the driver's
    // size field (-1) this never applies.
    const int PadFull = 330, PadFullRelease = 280;
    const int PressingSize = 30, PressingSizeRelease = 26;

    /// <summary>How long a held force click takes to charge up fully, in seconds.</summary>
    public const double ChargeSeconds = 2.5;

    public enum Change { None, Engaged, Released }

    // The fingers that are engaged right now: the trackpad's contact id and when it engaged.
    readonly List<(uint Id, long Since)> _engaged = [];

    /// <summary>Whether any finger holds a force click.</summary>
    public bool Engaged => _engaged.Count > 0;

    /// <summary>Whether this finger holds a force click.</summary>
    public bool IsEngaged(uint id)
    {
        foreach (var finger in _engaged)
            if (finger.Id == id) return true;
        return false;
    }

    /// <summary>How long the longest-held force click has been held, in seconds; 0 when none is.</summary>
    public double HeldSeconds
    {
        get
        {
            double held = 0;
            foreach (var finger in _engaged)
                held = Math.Max(held, Stopwatch.GetElapsedTime(finger.Since).TotalSeconds);
            return held;
        }
    }

    /// <summary>0..1: how far the longest-held force click has charged up.</summary>
    public double Charge => Math.Min(HeldSeconds / ChargeSeconds, 1);

    /// <summary>Feeds one report. Returns Engaged if a finger entered the stage in it, otherwise
    /// Released if one eased back out of it while still touching. A finger that lifts straight
    /// off ends its force click silently; the trackpad's own release click follows anyway.</summary>
    public Change Update(TouchFrame frame)
    {
        var change = Change.None;

        int total = 0;
        foreach (var c in frame.Contacts)
            if (c.Tip && c.Pressure > 0) total += c.Pressure;

        for (int i = _engaged.Count - 1; i >= 0; i--)
        {
            bool touching = false, holding = false;
            foreach (var c in frame.Contacts)
            {
                if (c.Id != _engaged[i].Id || !c.Tip) continue;
                touching = true;
                holding = c.Pressure > ReleaseAt || (total >= PadFullRelease && c.Size >= PressingSizeRelease);
            }
            if (holding) continue;
            _engaged.RemoveAt(i);
            if (touching) change = Change.Released;
        }

        foreach (var c in frame.Contacts)
        {
            if (!c.Tip || IsEngaged(c.Id)) continue;
            if (c.Pressure < EngageAt && !(total >= PadFull && c.Size >= PressingSize)) continue;
            _engaged.Add((c.Id, Stopwatch.GetTimestamp()));
            change = Change.Engaged;
        }
        return change;
    }

    public void Reset() => _engaged.Clear();
}

public enum HapticState
{
    Unknown,       // not probed yet
    Ready,
    AccessDenied,  // the driver only lets administrators in (its AllowNonAdminAccess setting is off)
    Unavailable,   // no driver with on-demand haptics, or no trackpad
}

/// <summary>Fires the Magic Trackpad's haptic actuator: IOCTL_PTPFILTER_ACTUATE on the
/// Bluetooth filter's haptic device, or the actuate command on the USB driver's pipe
/// (see tools/haptic_test.py in the driver's repository). Sends run on a worker thread: one takes about 70 ms over
/// Bluetooth and must not hold up the reports.</summary>
public sealed class HapticFeedback : IDisposable
{
    // Strengths in the encoding of the driver's FeedbackClick setting. The low byte is the
    // main strength; the other two add a sharper and a deeper component (as described in the
    // LinuxMagicForce README, which the driver's actuate command is based on). Light and Firm
    // are the driver's own presets and feel close to each other; Soft is well below both,
    // between them and the faint tick LinuxMagicForce uses as a scrolling texture (0x010108).
    public const uint Soft = 0x02020C, Light = 0x040415, Firm = 0x08081E;

    /// <summary>A strength from Soft (0) to Firm (1).</summary>
    public static uint Strength(double level)
    {
        level = Math.Clamp(level, 0, 1);
        uint main = (uint)Math.Round(0x0C + (0x1E - 0x0C) * level), rest = (uint)Math.Round(2 + 6 * level);
        return rest << 16 | rest << 8 | main;
    }

    const string BluetoothDevice = @"\\.\AmtPtpHapticDeviceUm";
    const string UsbPipe = "AmtPtpControlPanelUsbUmInterface";
    const int RetryMs = 3000;

    readonly BlockingCollection<uint> _queue = new(boundedCapacity: 2);
    readonly Thread _thread;
    SafeFileHandle? _bluetooth;   // kept open: the driver keeps it valid across reconnects
    volatile HapticState _state;

    public HapticState State => _state;

    /// <summary>Raised on the worker thread.</summary>
    public event Action<HapticState>? StateChanged;

    public HapticFeedback()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Haptic feedback" };
        _thread.Start();
    }

    /// <summary>Queues one tick. Dropped if two are already waiting: a late tick is worse than none.</summary>
    public void Fire(uint strength)
    {
        if (!_queue.IsAddingCompleted) _queue.TryAdd(strength);
    }

    void Run()
    {
        Connect();
        while (!_queue.IsCompleted)
        {
            // While not ready, look again every few seconds: the setting may get switched on
            // or the trackpad may connect while the app is running.
            if (!_queue.TryTake(out uint strength, RetryMs))
            {
                if (_state != HapticState.Ready) Connect();
                continue;
            }
            if (_state != HapticState.Ready) Connect();
            if (_state == HapticState.Ready) Send(strength);
        }
        _bluetooth?.Dispose();
    }

    void Connect()
    {
        HapticState state;
        var handle = CreateFileW(BluetoothDevice, GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        int error = Marshal.GetLastWin32Error();
        if (!handle.IsInvalid)
        {
            _bluetooth = handle;
            state = HapticState.Ready;
        }
        else
        {
            handle.Dispose();
            state = error == ERROR_ACCESS_DENIED ? HapticState.AccessDenied : ProbeUsb();
        }

        if (state == _state) return;
        _state = state;
        StateChanged?.Invoke(state);
    }

    static HapticState ProbeUsb()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", UsbPipe, PipeDirection.InOut);
            pipe.Connect(100);
            return HapticState.Ready;
        }
        catch (UnauthorizedAccessException) { return HapticState.AccessDenied; }
        catch (Exception e) when (e is TimeoutException or IOException) { return HapticState.Unavailable; }
    }

    void Send(uint strength)
    {
        if (_bluetooth != null)
        {
            // A failure here (e.g. the trackpad is switched off) is not fatal: the handle
            // stays valid and the next tick may work again.
            DeviceIoControl(_bluetooth, IOCTL_PTPFILTER_ACTUATE, ref strength, sizeof(uint),
                IntPtr.Zero, 0, out _, IntPtr.Zero);
            return;
        }

        try
        {
            // USB: the driver serves one command per connection and answers with two bytes.
            using var pipe = new NamedPipeClientStream(".", UsbPipe, PipeDirection.InOut);
            pipe.Connect(500);
            pipe.Write([0x02, (byte)strength, (byte)(strength >> 8), (byte)(strength >> 16)]);
            var answer = new byte[2];
            pipe.ReadExactly(answer);
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
        {
            _state = HapticState.Unknown;   // probed again before the next tick
        }
    }

    public void Dispose() => _queue.CompleteAdding();

    // ---- Win32 ----------------------------------------------------------------------

    const uint GENERIC_WRITE = 0x40000000, FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
    const int ERROR_ACCESS_DENIED = 5;
    const uint IOCTL_PTPFILTER_ACTUATE = 0x0022A008;   // CTL_CODE(FILE_DEVICE_UNKNOWN, 0x802, METHOD_BUFFERED, FILE_WRITE_ACCESS)

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle device, uint code, ref uint input, int inputSize,
        IntPtr output, int outputSize, out int returned, IntPtr overlapped);
}
