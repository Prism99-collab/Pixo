using System.Runtime.InteropServices;

namespace DynamicNotch;

/// <summary>
/// Nobody can see the notch: the PC is locked, or its screen is off. Like a notch moved off the screen (over a game),
/// everything that only moves to be looked at rests then (the record, the music bars, the face, the companion, the rain,
/// the island styles), so a PC left on all night isn't kept busy drawing for nobody. All of it comes back with you.
/// </summary>
public partial class NotchWindow
{
    bool _pcLocked, _screenOff;

    /// <summary>The notch can't be seen right now (off the screen over a game, the PC locked, or its screen off).</summary>
    bool Unseen => _offScreen || _pcLocked || _screenOff;

    static readonly Guid ConsoleDisplayState = new("6FE69556-704A-47A0-8F24-C28D936FDA47");
    IntPtr _displayNotify;

    [DllImport("user32.dll")] static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid setting, int flags);
    [DllImport("user32.dll")] static extern bool UnregisterPowerSettingNotification(IntPtr handle);

    /// <summary>Asks Windows to say when the screen goes off and on (once the window exists).</summary>
    void WatchScreenState()
    {
        if (_hwnd == IntPtr.Zero || _displayNotify != IntPtr.Zero) return;
        try { var g = ConsoleDisplayState; _displayNotify = RegisterPowerSettingNotification(_hwnd, ref g, 0 /* to this window */); } catch { }
    }

    void UnwatchScreenState()
    {
        if (_displayNotify != IntPtr.Zero) try { UnregisterPowerSettingNotification(_displayNotify); } catch { }
        _displayNotify = IntPtr.Zero;
    }

    /// <summary>WM_POWERBROADCAST: the screen's state (0 off, 1 on, 2 dimmed: dimmed still counts as seen).</summary>
    void OnPowerBroadcast(IntPtr wParam, IntPtr lParam)
    {
        if (wParam.ToInt32() != 0x8013 /* PBT_POWERSETTINGCHANGE */ || lParam == IntPtr.Zero) return;
        try
        {
            var setting = Marshal.PtrToStructure<Guid>(lParam);
            if (setting != ConsoleDisplayState) return;
            int state = Marshal.ReadInt32(lParam, 20); // after the GUID (16) and the data length (4)
            SetAway(locked: _pcLocked, screenOff: state == 0);
        }
        catch { }
    }

    void SetAway(bool locked, bool screenOff)
    {
        bool before = Unseen;
        _pcLocked = locked;
        _screenOff = screenOff;
        if (Unseen == before) return;
        Services.AppLog.Write(Unseen ? $"away ({(locked ? "locked" : "screen off")}): resting" : "back: everything moves again");
        // the same set that rests when the notch leaves the screen
        SpinDisc();
        UpdateRain();
        UpdateAmbient();
        UpdateEqualizer();
        UpdateCallUi(); // (the call's voice bars)
        StyleKick();
        _notifications.SetInterval(NotificationPace(SaveCpu)); // (looked at less often while nobody's there)
    }

    /// <summary>How often notifications are looked for (when Windows can't announce them): 2 s, 5 on battery saving, 20 while away.</summary>
    TimeSpan NotificationPace(bool eco) => TimeSpan.FromSeconds(Unseen ? 20 : eco ? 5 : 2);
}
