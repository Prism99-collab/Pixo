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
    static bool? _fullSpeed; // (static: a game's quiet resets it, so the next opening always lifts it)
    DispatcherTimer? _fullSpeedOff;

    /// <summary>
    /// Called on every change of shape: full speed now, back to Windows' choice once it rests. Opened (or a pop-up)
    /// it always gets full speed, even with a game just left behind or still running: the game's "quiet" (low priority,
    /// throttled) is lifted while the notch moves and comes back once it rests again.
    /// </summary>
    void MotionPower(Mode to)
    {
        _fullSpeedOff ??= NewFullSpeedOff();
        _fullSpeedOff.Stop();
        if (_gameQuiet && IsBase(to)) { _fullSpeedOff.Start(); return; } // resting in a game: its quiet returns shortly
        FullSpeed(true);
        if (IsBase(to)) _fullSpeedOff.Start(); // open, a pop-up, a drop: it stays up until the notch is back at rest
    }

    DispatcherTimer NewFullSpeedOff()
    {
        var t = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2.5) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            if (!IsBase(_mode) || _animating) { t.Start(); return; } // still moving or open: later
            if (_gameQuiet) { _fullSpeed = null; GameQuiet(true); } // a game in front: quiet again
            else FullSpeed(false);
        };
        return t;
    }

    /// <summary>
    /// Full speed: never throttled, a step above normal priority (so a busy PC, a heavy file, a game in the
    /// background don't make it stutter), and no full memory clean-up in the middle of a motion. Off: Windows decides
    /// again, normal priority.
    /// </summary>
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
            me.PriorityClass = on ? System.Diagnostics.ProcessPriorityClass.AboveNormal : System.Diagnostics.ProcessPriorityClass.Normal;
        }
        catch { }
        try { System.Runtime.GCSettings.LatencyMode = on ? System.Runtime.GCLatencyMode.SustainedLowLatency : System.Runtime.GCLatencyMode.Interactive; } catch { }
    }

    /// <summary>
    /// Asks Windows to keep a small part of Pixo in memory (a soft floor, not a hard one), so a heavy app filling the
    /// memory doesn't push it all out to the disk, where opening it would have to read it back first. Only on PCs
    /// with room to spare (8 GB or more).
    /// </summary>
    static void KeepResident()
    {
        try
        {
            if (GC.GetGCMemoryInfo().TotalAvailableMemoryBytes < 7_000_000_000L) return;
            using var me = System.Diagnostics.Process.GetCurrentProcess();
            SetProcessWorkingSetSizeEx(me.Handle, (IntPtr)(96L * 1024 * 1024), (IntPtr)(1024L * 1024 * 1024), 0);
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern bool SetProcessWorkingSetSizeEx(IntPtr process, IntPtr min, IntPtr max, uint flags);
}
