// Stops the trackpad from driving Windows, so it can be touched freely while showing the
// visualizer. The raw reports keep arriving, only their effect is dropped.
//
// With a mouse connected, Windows itself is told to ignore the trackpad (see
// WindowsTouchpad): nothing gets through, including the three- and four-finger gestures,
// and the mouse works as usual.
//
// Without a mouse that switch does nothing, so a low-level mouse hook takes over: the
// pointer the trackpad moves is kept inside the visualizer window, where it can still click
// the controls, and clicks or scrolling anywhere else are dropped. The hook cannot tell
// which device an event came from, so it only acts while fingers are on the trackpad (and
// shortly after, for the tap click and scroll inertia). Windows' own three- and four-finger
// gestures are not mouse input and still happen in this mode.
using System.Runtime.InteropServices;

namespace TrackpadVisualizer;

public enum BlockMode
{
    Off,
    Ignored,    // Windows ignores the trackpad entirely
    Confined,   // the trackpad's pointer is kept inside the visualizer window
}

public sealed class ActionBlocker : IDisposable
{
    const long PointerGraceMs = 500;   // a tap's click arrives after the finger has lifted
    const long ScrollGraceMs = 2000;   // scroll inertia keeps going after the fingers lift

    readonly HookProc _hookProc;       // kept in a field so the delegate is not collected
    Thread? _thread;
    uint _threadId;
    long _lastTouch = -1_000_000;
    long _blocked, _passed;
    int _blockedButtons;               // buttons whose "down" was dropped: drop their "up" too
    bool _enabled, _restoreAllowWithMouse;

    public ActionBlocker() => _hookProc = Hook;

    public BlockMode Mode { get; private set; }

    /// <summary>The window the pointer stays usable in while confined.</summary>
    public IntPtr Window { get; set; }

    /// <summary>Whether to ask Windows to ignore the trackpad; off leaves only the hook.</summary>
    public bool UseWindowsSwitch { get; set; } = true;

    /// <summary>Events dropped / let through by the hook since it was installed.</summary>
    public long Blocked => Interlocked.Read(ref _blocked);
    public long Passed => Interlocked.Read(ref _passed);

    /// <summary>Only count what would be dropped, without dropping it (for testing).</summary>
    public bool ObserveOnly { get; set; }

    /// <summary>Call for every trackpad report that has fingers on it or the button down.</summary>
    public void NoteTouch() => Interlocked.Exchange(ref _lastTouch, Environment.TickCount64);

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (value == _enabled) return;
            _enabled = value;
            if (!value)
            {
                HookRunning = false;
                if (_restoreAllowWithMouse) WindowsTouchpad.SetAllowWithMouse(true);
                _restoreAllowWithMouse = false;
            }
            Refresh();
        }
    }

    /// <summary>Picks the mode that fits right now. Call again from time to time while
    /// enabled: plugging a mouse in or out changes which one works.</summary>
    public BlockMode Refresh()
    {
        if (!_enabled) return Mode = BlockMode.Off;

        var state = UseWindowsSwitch ? WindowsTouchpad.Get() : null;
        if (state is { MousePresent: true, AllowWithMouse: true } && WindowsTouchpad.SetAllowWithMouse(false))
        {
            _restoreAllowWithMouse = true;
            state = WindowsTouchpad.Get();
        }

        bool ignored = state is { Active: false };
        HookRunning = !ignored;
        return Mode = ignored ? BlockMode.Ignored : BlockMode.Confined;
    }

    public void Dispose() => Enabled = false;

    // ---- Hook -----------------------------------------------------------------------

    // The hook runs on its own thread: Windows waits for the hook on every mouse event, so
    // it must never sit behind the window's rendering.
    bool HookRunning
    {
        get => _thread != null;
        set
        {
            if (value == HookRunning) return;
            if (value)
            {
                using var ready = new ManualResetEventSlim();
                _thread = new Thread(() =>
                {
                    _threadId = GetCurrentThreadId();
                    // The hook reports physical pixels; make every call on this thread use them
                    // too, or the window test is off on a monitor with different scaling.
                    SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
                    PeekMessageW(out _, IntPtr.Zero, 0, 0, 0);   // creates the thread's message queue
                    IntPtr hook = SetWindowsHookExW(WH_MOUSE_LL, _hookProc, GetModuleHandleW(null), 0);
                    ready.Set();
                    while (GetMessageW(out _, IntPtr.Zero, 0, 0) > 0) { }
                    if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
                })
                { IsBackground = true, Name = "Trackpad action blocker" };
                _thread.Start();
                ready.Wait();
            }
            else
            {
                PostThreadMessageW(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                _thread?.Join();
                _thread = null;
                _blockedButtons = 0;
            }
        }
    }

    IntPtr Hook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && ShouldBlock((int)wParam, Marshal.PtrToStructure<POINT>(lParam)))
        {
            Interlocked.Increment(ref _blocked);
            if (!ObserveOnly) return 1;
        }
        else if (code >= 0)
        {
            Interlocked.Increment(ref _passed);
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    bool ShouldBlock(int message, POINT at)
    {
        long sinceTouch = Environment.TickCount64 - Interlocked.Read(ref _lastTouch);
        bool touching = sinceTouch <= (message is WM_MOUSEWHEEL or WM_MOUSEHWHEEL ? ScrollGraceMs : PointerGraceMs);
        bool drop = touching && !AllowedInWindow(message, at);

        // Keep button presses and releases paired, so no application is left with a button
        // it believes is still held (or a release it never saw the press for).
        int button = message switch
        {
            WM_LBUTTONDOWN or WM_LBUTTONUP => 1,
            WM_RBUTTONDOWN or WM_RBUTTONUP => 2,
            WM_MBUTTONDOWN or WM_MBUTTONUP => 4,
            WM_XBUTTONDOWN or WM_XBUTTONUP => 8,
            _ => 0,
        };
        if (button == 0) return drop;

        bool down = message is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN or WM_XBUTTONDOWN;
        if (down)
        {
            if (drop) _blockedButtons |= button; else _blockedButtons &= ~button;
            return drop;
        }
        bool wasBlocked = (_blockedButtons & button) != 0;
        _blockedButtons &= ~button;
        return wasBlocked;
    }

    // Inside the visualizer window the pointer works normally. A move that would leave it is
    // dropped and the pointer is put at the nearest point inside instead.
    bool AllowedInWindow(int message, POINT at)
    {
        IntPtr window = Window;
        if (window == IntPtr.Zero || !IsWindowVisible(window) || IsIconic(window)) return false;
        if (DwmGetWindowAttribute(window, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) != 0
            && !GetWindowRect(window, out r))
            return false;

        bool inside = at.X >= r.Left && at.X < r.Right && at.Y >= r.Top && at.Y < r.Bottom;
        if (message == WM_MOUSEMOVE)
        {
            if (!inside && !ObserveOnly)
                SetCursorPos(Math.Clamp(at.X, r.Left, r.Right - 1), Math.Clamp(at.Y, r.Top, r.Bottom - 1));
            return inside;
        }

        // Clicks and the wheel go to whatever is under the pointer; another window may be
        // lying on top of this one.
        return inside && GetAncestor(WindowFromPoint(at), GA_ROOT) == window;
    }

    // ---- Win32 ----------------------------------------------------------------------

    const int WH_MOUSE_LL = 14;
    const uint WM_QUIT = 0x0012, GA_ROOT = 2;
    const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    const int WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_RBUTTONDOWN = 0x0204,
        WM_RBUTTONUP = 0x0205, WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208, WM_MOUSEWHEEL = 0x020A,
        WM_XBUTTONDOWN = 0x020B, WM_XBUTTONUP = 0x020C, WM_MOUSEHWHEEL = 0x020E;

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public POINT pt;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookExW(int id, HookProc proc, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern int GetMessageW(out MSG msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    static extern bool PeekMessageW(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    static extern bool PostThreadMessageW(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    static extern bool SetCursorPos(int x, int y);

    static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    [DllImport("user32.dll")]
    static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    [DllImport("dwmapi.dll")]
    static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);

    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandleW(string? name);
}
