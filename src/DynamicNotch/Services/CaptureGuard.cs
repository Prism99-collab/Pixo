using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace DynamicNotch.Services;

/// <summary>
/// "Hide Pixo from screen sharing": every Pixo window (the notch, the search, Preferences, its menus and tips) is left
/// out of screen captures, so Teams, Zoom, Discord, OBS and screenshots see what is under it, while you still see it.
/// Windows 10 2004 and later (WDA_EXCLUDEFROMCAPTURE); older Windows can't, and the switch then does nothing.
/// </summary>
public static class CaptureGuard
{
    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    const uint WdaNone = 0, WdaExcludeFromCapture = 0x11;

    static bool _on, _wired, _told;

    public static bool On => _on;

    public static void Set(bool on)
    {
        if (on == _on && _wired) return;
        _on = on;
        if (!_wired)
        {
            if (!on) return; // (off from the start: nothing to undo, nothing to watch)
            _wired = true;
            // windows and pop-ups made later get the same, the moment they appear
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => Apply(s as Window)));
            EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler((s, _) => ApplyPopup(s as Visual)));
            EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.OpenedEvent, new RoutedEventHandler((s, _) => ApplyPopup(s as Visual)));
        }
        if (Application.Current != null) foreach (Window w in Application.Current.Windows) Apply(w);
        AppLog.Write("hide from screen sharing: " + (on ? "on" : "off"));
    }

    static void Apply(Window? w)
    {
        if (w == null) return;
        var h = new WindowInteropHelper(w).Handle;
        if (h != IntPtr.Zero) Affinity(h);
    }

    static void ApplyPopup(Visual? v)
    {
        if (v != null && PresentationSource.FromVisual(v) is HwndSource src) Affinity(src.Handle);
    }

    static void Affinity(IntPtr h)
    {
        if (SetWindowDisplayAffinity(h, _on ? WdaExcludeFromCapture : WdaNone) || !_on || _told) return;
        _told = true;
        AppLog.Write($"hide from screen sharing: not possible on this Windows (error {Marshal.GetLastWin32Error()})");
    }
}
