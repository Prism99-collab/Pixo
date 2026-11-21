using System.Windows.Threading;

namespace DynamicNotch;

/// <summary>
/// Smooth on battery too. On battery Windows puts apps that aren't in front into "efficiency mode" (lower clocks, the
/// slow cores), and the notch is never the window in front, so its motion got choppy unplugged. While the notch is
/// moving or open it asks Windows to keep it at full speed; a couple of seconds after it settles back to rest, Windows
/// decides again (so the battery is spared while nothing moves). A game in front always wins (NotchWindow.Game).
/// </summary>
public partial class NotchWindow
{
    static bool _gameQuiet;
    bool? _fullSpeed;
    DispatcherTimer? _fullSpeedOff;

    /// <summary>Called on every change of shape: full speed now, back to Windows' choice once it rests.</summary>
    void MotionPower(Mode to)
    {
        if (_gameQuiet) { _fullSpeed = null; return; }
        FullSpeed(true);
        _fullSpeedOff ??= NewFullSpeedOff();
        _fullSpeedOff.Stop();
        if (IsBase(to)) _fullSpeedOff.Start(); // open, a pop-up, a drop: it stays up until the notch is back at rest
    }

    DispatcherTimer NewFullSpeedOff()
    {
        var t = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2.5) };
        t.Tick += (_, _) => { t.Stop(); if (!_gameQuiet && IsBase(_mode)) FullSpeed(false); };
        return t;
    }

    void FullSpeed(bool on)
    {
        if (_fullSpeed == on) return;
        _fullSpeed = on;
        try
        {
            using var me = System.Diagnostics.Process.GetCurrentProcess();
            // on: "never throttle me" (control the setting, state off); off: hand the choice back to Windows
            var state = new PROCESS_POWER_THROTTLING_STATE { Version = 1, ControlMask = on ? 1u : 0u, StateMask = 0 };
            SetProcessInformation(me.Handle, 4 /* ProcessPowerThrottling */, ref state, (uint)System.Runtime.InteropServices.Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
        }
        catch { }
    }
}
