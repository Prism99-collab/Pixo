using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace DynamicNotch;

/// <summary>
/// The mouse wheel over the notch, on every PC. The notch never takes focus (so it never steals it from what you're
/// doing); Windows then sends the wheel to the window under the pointer only when "Scroll inactive windows when I
/// hover over them" is on (the default, but many PCs have it off): there, scrolling the notch's lists did nothing.
/// So while the notch is on screen a low-level mouse hook on its own thread watches the wheel: a turn with the
/// pointer over the notch is taken (Windows doesn't deliver it a second time) and handed to whatever is under the
/// pointer, exactly as a normal wheel event would be; every other wheel turn goes on untouched in microseconds.
/// Not in a game, not while hidden or locked, and only while the pointer is near the notch: otherwise there's no hook
/// at all (a hook hears every mouse move on the PC, up to 1000 a second with a gaming mouse).
/// </summary>
public partial class NotchWindow
{
    const int WM_MOUSEWHEEL_LL = 0x020A, WM_MOUSEHWHEEL_LL = 0x020E, WM_QUIT = 0x0012;

    /// <summary>One hook thread's own state (an old thread ending can never touch a newer one's).</summary>
    /// (set and read with full barriers on both sides: a stop and a start can never miss each other)
    sealed class WheelRun { public int ThreadId, Stop; }
    long _wheelNearAt;
    WheelRun? _wheelRun;
    static bool _wheelLogged;
    HookProc? _wheelProc; // made once and kept alive for good: Windows may still call it while a hook is being removed
    // the notch on screen, in physical pixels, kept current by the poll (read by the hook thread)
    int _hitL, _hitT, _hitR, _hitB;

    /// <summary>Called on every poll: the hook runs only while the notch is there to scroll, and knows where it is.</summary>
    void WheelPoll()
    {
        bool want = !_hidden && _mode != Mode.Game && !InGame && !_settings.Locked && IsVisible && !Unseen;
        if (want)
        {
            try
            {
                var tl = Pill.PointToScreen(new Point(0, 0));
                var br = Pill.PointToScreen(new Point(Pill.ActualWidth, Pill.ActualHeight));
                Volatile.Write(ref _hitL, (int)tl.X); Volatile.Write(ref _hitT, (int)tl.Y);
                Volatile.Write(ref _hitR, (int)br.X); Volatile.Write(ref _hitB, (int)br.Y);
                // only with the pointer near it: anywhere in the band along the top (where the quick looks happen), a good
                // way to each side, and for 3 s after it leaves (a fast move onto it and a turn at once is never missed)
                double near = 160 * _scale, side = 420 * _scale;
                if (Interop.Native.GetCursorPos(out var c) && c.Y <= br.Y + near && c.X >= tl.X - side && c.X <= br.X + side)
                    _wheelNearAt = Environment.TickCount64;
                want = Environment.TickCount64 - _wheelNearAt < 3000;
            }
            catch { want = false; } // not on screen right now
        }
        ArmWheel(want);
    }

    void ArmWheel(bool on)
    {
        if (on)
        {
            if (_wheelRun != null) return;
            _wheelProc ??= OnWheelHook; // (made here, once, before any hook thread exists)
            var run = new WheelRun();
            _wheelRun = run;
            new Thread(() => WheelLoop(run)) { IsBackground = true, Name = "wheel", Priority = ThreadPriority.AboveNormal }.Start();
        }
        else
        {
            var run = _wheelRun;
            if (run == null) return;
            _wheelRun = null;
            Interlocked.Exchange(ref run.Stop, 1);
            int id = Interlocked.CompareExchange(ref run.ThreadId, 0, 0);
            if (id != 0) PostThreadMessage((uint)id, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }
    }

    void WheelLoop(WheelRun run)
    {
        IntPtr hook = IntPtr.Zero;
        try
        {
            PeekMessage(out _, IntPtr.Zero, 0, 0, 0); // a message queue first, so a stop can always reach this thread
            Interlocked.Exchange(ref run.ThreadId, (int)GetCurrentThreadId());
            if (Interlocked.CompareExchange(ref run.Stop, 0, 0) == 1) return;
            hook = SetWindowsHookEx(14 /* WH_MOUSE_LL */, _wheelProc, GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero) { Services.AppLog.Write("wheel: the hook was refused"); return; }
            if (!_wheelLogged) { _wheelLogged = true; Services.AppLog.Write("wheel: delivered to the notch directly (works whatever Windows' scroll setting)"); }
            while (Volatile.Read(ref run.Stop) == 0 && GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
        }
        catch { }
        finally
        {
            if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
        }
    }

    IntPtr OnWheelHook(int code, IntPtr wParam, IntPtr lParam)
    {
        int msg = wParam.ToInt32();
        if (code >= 0 && (msg == WM_MOUSEWHEEL_LL || msg == WM_MOUSEHWHEEL_LL) && _wheelRun != null)
        {
            var m = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            if (m.X >= Volatile.Read(ref _hitL) && m.X < Volatile.Read(ref _hitR) && m.Y >= Volatile.Read(ref _hitT) && m.Y < Volatile.Read(ref _hitB))
            {
                short delta = (short)((m.MouseData >> 16) & 0xFFFF);
                int x = m.X, y = m.Y;
                Dispatcher.BeginInvoke(DispatcherPriority.Input, () => RouteWheel(msg == WM_MOUSEHWHEEL_LL, delta, x, y));
                return (IntPtr)1; // ours: Windows doesn't deliver it again
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    /// <summary>The wheel turn, delivered to what's under the pointer the way WPF would (preview first, then the event).</summary>
    void RouteWheel(bool horizontal, short delta, int x, int y)
    {
        try
        {
            if (horizontal)
            {
                if (_mode == Mode.Expanded) OnHorizontalWheel(delta);
                return;
            }
            var p = Root.PointFromScreen(new Point(x, y));
            // what's under the pointer: a control, or a piece of text (a run in a message), which routes to what holds it
            var hit = Root.InputHitTest(p);
            Action<RoutedEventArgs> raise = hit switch
            {
                UIElement u => u.RaiseEvent,
                ContentElement c => c.RaiseEvent,
                _ => _mode == Mode.Expanded ? Expanded.RaiseEvent : Pill.RaiseEvent, // nothing there: the open panel takes it
            };
            var preview = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = UIElement.PreviewMouseWheelEvent };
            raise(preview);
            if (preview.Handled) return;
            var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = UIElement.MouseWheelEvent };
            raise(wheel);
        }
        catch (Exception ex) { Services.AppLog.Write("wheel: " + ex.Message); }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PEEKMSG { public IntPtr Hwnd; public uint Message; public IntPtr WParam, LParam; public uint Time; public int X, Y; }

    [DllImport("user32.dll")] static extern bool PeekMessage(out PEEKMSG msg, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
}
