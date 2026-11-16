// Reads Precision Touchpad reports through Raw Input and decodes them with the HID parser
// (HidP_*), so the layout comes from the device's own report descriptor instead of
// hard-coded offsets. Any PTP touchpad gives positions; the Magic Trackpad driver also
// gives per-contact pressure, the contact box and the vendor extras (page 0xFF00).
using System.Runtime.InteropServices;

namespace TrackpadVisualizer;

public struct Contact
{
    public uint Id;
    public bool Tip;          // touching the surface (false = hovering)
    public bool Confident;    // false = the driver flagged it as a palm
    public int X, Y;          // logical units, 0..XMax / 0..YMax
    public int Width, Height; // contact bounding box in X/Y units, 0 if not reported
    public int Pressure;      // raw 0..255, -1 if not reported
    // Vendor extras, -1 if not reported
    public int Size, TouchMajor, TouchMinor, Orientation, Finger, State;
}

public sealed class TouchFrame
{
    public required TouchpadDevice Device { get; init; }
    public Contact[] Contacts { get; init; } = [];
    public int ScanTime { get; init; }
    public int Force { get; init; }   // Mechanical Force (sum of contact pressures), -1 if not reported
    public bool Button { get; init; }
}

public sealed class TouchpadDevice
{
    public int XMax { get; init; } = 1;
    public int YMax { get; init; } = 1;
    public double Aspect { get; init; } = 1.4;  // physical width / height
    public double MmPerUnit { get; init; }      // X axis, 0 if unknown
    public bool HasPressure { get; init; }
    public bool HasBox { get; init; }
    public bool HasExtras { get; init; }
    public int MaxContacts => FingerLinks.Length;

    internal IntPtr Preparsed;
    internal ushort[] FingerLinks = [];
    internal bool HasForce;

    public static readonly TouchpadDevice Demo = new()
    {
        XMax = 7612, YMax = 5065, Aspect = 1600 / 1149.0, MmPerUnit = 160.0 / 7612,
        HasPressure = true, HasBox = true, HasExtras = true, FingerLinks = [1, 2, 3, 4, 5],
    };
}

public sealed class TouchpadInput
{
    const ushort PageGeneric = 0x01, PageButton = 0x09, PageDigitizer = 0x0D, PageSensor = 0x20, PageVendor = 0xFF00;
    const ushort UsageTouchpad = 0x05, UsageX = 0x30, UsageY = 0x31;
    const ushort UsageTipPressure = 0x30, UsageTip = 0x42, UsageConfidence = 0x47, UsageWidth = 0x48,
        UsageHeight = 0x49, UsageContactId = 0x51, UsageContactCount = 0x54, UsageScanTime = 0x56;
    const ushort UsageMechanicalForce = 0x494;
    const ushort VendorSize = 0x02, VendorTouchMajor = 0x03, VendorTouchMinor = 0x04,
        VendorOrientation = 0x05, VendorFinger = 0x06, VendorState = 0x07;

    readonly Dictionary<IntPtr, TouchpadDevice?> _devices = [];
    readonly ushort[] _usages = new ushort[16];
    byte[] _buffer = new byte[512];
    byte[] _report = new byte[256];

    public event Action<TouchFrame>? Frame;

    /// <summary>Asks for touchpad reports to be delivered to this window, also while it is in the background.</summary>
    public static bool Register(IntPtr hwnd)
    {
        var rid = new RAWINPUTDEVICE
        {
            usUsagePage = PageDigitizer,
            usUsage = UsageTouchpad,
            dwFlags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY,
            hwndTarget = hwnd,
        };
        return RegisterRawInputDevices([rid], 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
    }

    /// <summary>Handles WM_INPUT: decodes every report in the message and raises <see cref="Frame"/>.</summary>
    public void ProcessInput(IntPtr hRawInput)
    {
        uint headerSize = (uint)(8 + 2 * IntPtr.Size);
        uint size = 0;
        GetRawInputData(hRawInput, RID_INPUT, null, ref size, headerSize);
        if (size == 0) return;
        if (_buffer.Length < size) _buffer = new byte[size];
        if (GetRawInputData(hRawInput, RID_INPUT, _buffer, ref size, headerSize) != size) return;

        if (BitConverter.ToUInt32(_buffer, 0) != RIM_TYPEHID) return;
        IntPtr hDevice = IntPtr.Size == 8
            ? (IntPtr)BitConverter.ToInt64(_buffer, 8)
            : (IntPtr)BitConverter.ToInt32(_buffer, 8);

        var device = GetDevice(hDevice);
        if (device == null) return;

        int sizeHid = BitConverter.ToInt32(_buffer, (int)headerSize);
        int count = BitConverter.ToInt32(_buffer, (int)headerSize + 4);
        int offset = (int)headerSize + 8;
        if (sizeHid <= 0 || offset + (long)sizeHid * count > size) return;
        if (_report.Length < sizeHid) _report = new byte[sizeHid];

        for (int i = 0; i < count; i++)
        {
            Buffer.BlockCopy(_buffer, offset + i * sizeHid, _report, 0, sizeHid);
            var frame = Parse(device, _report, sizeHid);
            if (frame != null) Frame?.Invoke(frame);
        }
    }

    /// <summary>Handles WM_INPUT_DEVICE_CHANGE: forgets a device that went away.</summary>
    public void DeviceChanged(IntPtr wParam, IntPtr hDevice)
    {
        if (wParam != GIDC_REMOVAL || !_devices.Remove(hDevice, out var device)) return;
        if (device != null) Marshal.FreeHGlobal(device.Preparsed);
    }

    /// <summary>Returns the decoded description of a Raw Input device, or null if it is not a usable touchpad.</summary>
    public TouchpadDevice? GetDevice(IntPtr hDevice)
    {
        if (!_devices.TryGetValue(hDevice, out var device))
            _devices[hDevice] = device = BuildDevice(hDevice);
        return device;
    }

    static TouchpadDevice? BuildDevice(IntPtr hDevice)
    {
        uint size = 0;
        GetRawInputDeviceInfoW(hDevice, RIDI_PREPARSEDDATA, IntPtr.Zero, ref size);
        if (size == 0) return null;

        IntPtr preparsed = Marshal.AllocHGlobal((int)size);
        TouchpadDevice? device = null;
        try
        {
            if (GetRawInputDeviceInfoW(hDevice, RIDI_PREPARSEDDATA, preparsed, ref size) == size)
                device = Describe(preparsed);
        }
        finally
        {
            if (device == null) Marshal.FreeHGlobal(preparsed);
        }
        return device;
    }

    static TouchpadDevice? Describe(IntPtr preparsed)
    {
        if (HidP_GetCaps(preparsed, out var caps) != HIDP_STATUS_SUCCESS) return null;
        if (caps.UsagePage != PageDigitizer || caps.Usage != UsageTouchpad || caps.NumberInputValueCaps == 0) return null;

        ushort n = caps.NumberInputValueCaps;
        var valueCaps = new HIDP_VALUE_CAPS[n];
        if (HidP_GetValueCaps(HidP_Input, valueCaps, ref n, preparsed) != HIDP_STATUS_SUCCESS) return null;

        // Each finger is a logical (link) collection; collect what every one of them reports.
        var links = new SortedDictionary<ushort, HashSet<(ushort Page, ushort Usage)>>();
        var top = new HashSet<(ushort, ushort)>();
        int xMax = 0, yMax = 0, xPhys = 0, yPhys = 0;
        for (int i = 0; i < n; i++)
        {
            ref var cap = ref valueCaps[i];
            var set = top;
            if (cap.LinkCollection != 0 && !links.TryGetValue(cap.LinkCollection, out set!))
                links[cap.LinkCollection] = set = [];
            int last = cap.IsRange != 0 ? cap.UsageMax : cap.UsageMin;
            for (int usage = cap.UsageMin; usage <= last; usage++)
                set.Add((cap.UsagePage, (ushort)usage));

            if (cap.UsagePage == PageGeneric && cap.UsageMin == UsageX && xMax == 0)
                (xMax, xPhys) = (cap.LogicalMax, cap.PhysicalMax);
            if (cap.UsagePage == PageGeneric && cap.UsageMin == UsageY && yMax == 0)
                (yMax, yPhys) = (cap.LogicalMax, cap.PhysicalMax);
        }

        var fingers = links
            .Where(l => l.Value.Contains((PageGeneric, UsageX)) && l.Value.Contains((PageGeneric, UsageY)))
            .ToArray();
        if (fingers.Length == 0 || xMax <= 0 || yMax <= 0) return null;

        bool All(ushort page, params ushort[] usages) =>
            fingers.All(f => usages.All(u => f.Value.Contains((page, u))));

        bool extras = All(PageVendor, VendorTouchMajor, VendorTouchMinor, VendorOrientation);
        return new TouchpadDevice
        {
            Preparsed = preparsed,
            FingerLinks = fingers.Select(f => f.Key).ToArray(),
            XMax = xMax,
            YMax = yMax,
            Aspect = xPhys > 0 && yPhys > 0 ? (double)xPhys / yPhys : (double)xMax / yMax,
            // The Magic Trackpad descriptor states the physical size in 0.01 cm.
            MmPerUnit = extras && xPhys > 0 ? xPhys * 0.1 / xMax : 0,
            HasPressure = All(PageDigitizer, UsageTipPressure),
            HasBox = All(PageDigitizer, UsageWidth, UsageHeight),
            HasExtras = extras,
            HasForce = top.Contains((PageSensor, UsageMechanicalForce)),
        };
    }

    /// <summary>Decodes one touchpad input report (report ID included).</summary>
    public TouchFrame? Parse(TouchpadDevice device, byte[] report, int length)
    {
        IntPtr pp = device.Preparsed;
        uint len = (uint)length;

        int Value(ushort page, ushort link, ushort usage) =>
            HidP_GetUsageValue(HidP_Input, page, link, usage, out uint value, pp, report, len) == HIDP_STATUS_SUCCESS
                ? (int)value : -1;

        bool Pressed(ushort page, ushort link, ushort usage, out bool other, ushort otherUsage = 0)
        {
            other = false;
            bool found = false;
            uint n = (uint)_usages.Length;
            if (HidP_GetUsages(HidP_Input, page, link, _usages, ref n, pp, report, len) != HIDP_STATUS_SUCCESS)
                return false;
            for (int i = 0; i < n; i++)
            {
                if (_usages[i] == usage) found = true;
                if (_usages[i] == otherUsage) other = true;
            }
            return found;
        }

        // A report that does not belong to the touchpad collection's multi-touch report
        // (wrong report ID / length) fails here.
        int count = Value(PageDigitizer, 0, UsageContactCount);
        if (count < 0) return null;
        count = Math.Min(count, device.FingerLinks.Length);

        var contacts = new Contact[count];
        for (int i = 0; i < count; i++)
        {
            ushort link = device.FingerLinks[i];
            ref var c = ref contacts[i];
            HidP_GetUsageValue(HidP_Input, PageDigitizer, link, UsageContactId, out c.Id, pp, report, len);
            c.Tip = Pressed(PageDigitizer, link, UsageTip, out c.Confident, UsageConfidence);
            c.X = Math.Max(0, Value(PageGeneric, link, UsageX));
            c.Y = Math.Max(0, Value(PageGeneric, link, UsageY));
            c.Width = device.HasBox ? Math.Max(0, Value(PageDigitizer, link, UsageWidth)) : 0;
            c.Height = device.HasBox ? Math.Max(0, Value(PageDigitizer, link, UsageHeight)) : 0;
            c.Pressure = device.HasPressure ? Value(PageDigitizer, link, UsageTipPressure) : -1;
            c.Size = c.TouchMajor = c.TouchMinor = c.Orientation = c.Finger = c.State = -1;
            if (device.HasExtras)
            {
                c.Size = Value(PageVendor, link, VendorSize);
                c.TouchMajor = Value(PageVendor, link, VendorTouchMajor);
                c.TouchMinor = Value(PageVendor, link, VendorTouchMinor);
                c.Orientation = Value(PageVendor, link, VendorOrientation);
                c.Finger = Value(PageVendor, link, VendorFinger);
                c.State = Value(PageVendor, link, VendorState);
            }
        }

        return new TouchFrame
        {
            Device = device,
            Contacts = contacts,
            ScanTime = Value(PageDigitizer, 0, UsageScanTime),
            Force = device.HasForce ? Value(PageSensor, 0, UsageMechanicalForce) : -1,
            Button = Pressed(PageButton, 0, 1, out _),
        };
    }

    // ---- Win32 ----------------------------------------------------------------------

    const uint RIDEV_INPUTSINK = 0x00000100, RIDEV_DEVNOTIFY = 0x00002000;
    const uint RID_INPUT = 0x10000003, RIM_TYPEHID = 2, RIDI_PREPARSEDDATA = 0x20000005;
    const int GIDC_REMOVAL = 2;
    const int HidP_Input = 0;
    const int HIDP_STATUS_SUCCESS = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTDEVICE
    {
        public ushort usUsagePage, usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    struct HIDP_CAPS
    {
        [FieldOffset(0)] public ushort Usage;
        [FieldOffset(2)] public ushort UsagePage;
        [FieldOffset(4)] public ushort InputReportByteLength;
        [FieldOffset(48)] public ushort NumberInputValueCaps;
    }

    [StructLayout(LayoutKind.Explicit, Size = 72)]
    struct HIDP_VALUE_CAPS
    {
        [FieldOffset(0)] public ushort UsagePage;
        [FieldOffset(6)] public ushort LinkCollection;
        [FieldOffset(12)] public byte IsRange;
        [FieldOffset(44)] public int LogicalMax;
        [FieldOffset(52)] public int PhysicalMax;
        [FieldOffset(56)] public ushort UsageMin;   // "Usage" when IsRange is 0
        [FieldOffset(58)] public ushort UsageMax;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);

    [DllImport("user32.dll")]
    static extern uint GetRawInputData(IntPtr hRawInput, uint command, byte[]? data, ref uint size, uint headerSize);

    [DllImport("user32.dll")]
    static extern uint GetRawInputDeviceInfoW(IntPtr hDevice, uint command, IntPtr data, ref uint size);

    [DllImport("hid.dll")]
    static extern int HidP_GetCaps(IntPtr preparsed, out HIDP_CAPS caps);

    [DllImport("hid.dll")]
    static extern int HidP_GetValueCaps(int reportType, [In, Out] HIDP_VALUE_CAPS[] caps, ref ushort length, IntPtr preparsed);

    [DllImport("hid.dll")]
    static extern int HidP_GetUsageValue(int reportType, ushort usagePage, ushort linkCollection, ushort usage,
        out uint value, IntPtr preparsed, byte[] report, uint reportLength);

    [DllImport("hid.dll")]
    static extern int HidP_GetUsages(int reportType, ushort usagePage, ushort linkCollection,
        [In, Out] ushort[] usages, ref uint usageCount, IntPtr preparsed, byte[] report, uint reportLength);
}
