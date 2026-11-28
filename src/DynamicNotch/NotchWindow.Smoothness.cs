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
    internal static bool InGameQuiet => _gameQuiet; // (the start-up step never lifts a game's quiet)

    /// <summary>
    /// Pixo's priority outside a game: a step above normal, all the time. At normal it shared the processor evenly
    /// with a build, a render or an export running on every core, and the face, the music bars and the opening
    /// stuttered (frames held up to 150 ms). It uses so little that the step costs that work nearly nothing; its
    /// heavy background jobs (the file and app indexes) run on their own threads at the lowest level, so they never
    /// take time from yours. A game in front still gets the opposite (NotchWindow.Game).
    /// </summary>
    internal const System.Diagnostics.ProcessPriorityClass RestPriority = System.Diagnostics.ProcessPriorityClass.AboveNormal;
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
    /// Full speed: never throttled, and no full memory clean-up in the middle of a motion. Off: Windows decides the
    /// throttling again. Either way Pixo stays a step above normal priority (<see cref="RestPriority"/>).
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
            me.PriorityClass = RestPriority;
            GpuPriority(me.Handle, above: true);
        }
        catch { }
        try { System.Runtime.GCSettings.LatencyMode = on ? System.Runtime.GCLatencyMode.SustainedLowLatency : System.Runtime.GCLatencyMode.Interactive; } catch { }
    }

    /// <summary>
    /// Keeps Pixo in memory. A render, a build or a game filling the memory made Windows move Pixo's pages to the disk;
    /// the next hover then read them back first, and the notch froze for up to a second and a half (the freezes it
    /// logged came with 6,000 to 10,000 page faults each). On PCs with room to spare (12 GB or more) the floor is a
    /// hard one: the 256 MB Pixo lives in stay in memory, whatever else runs. With less, a soft floor (Windows may
    /// still take the pages back when it needs them more).
    /// </summary>
    static void KeepResident()
    {
        try
        {
            long ram = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            if (ram < 7_000_000_000L) return;
            using var me = System.Diagnostics.Process.GetCurrentProcess();
            bool hard = ram >= 12_000_000_000L;
            long min = (hard ? 256L : 96L) * 1024 * 1024;
            // QUOTA_LIMITS_HARDWS_MIN_ENABLE | QUOTA_LIMITS_HARDWS_MAX_DISABLE: a floor Windows keeps, no ceiling
            bool ok = SetProcessWorkingSetSizeEx(me.Handle, (IntPtr)min, (IntPtr)(2048L * 1024 * 1024), hard ? 0x1u | 0x8u : 0u);
            if (!ok && hard) ok = SetProcessWorkingSetSizeEx(me.Handle, (IntPtr)(96L * 1024 * 1024), (IntPtr)(1024L * 1024 * 1024), 0);
            Services.AppLog.Write($"memory: kept in RAM ({(hard && ok ? "hard" : "soft")} floor {min / 1048576} MB{(ok ? "" : ", refused")})");
        }
        catch { }
    }

    /// <summary>
    /// The same step on the graphics chip: a 3D render or an export filling the GPU used to hold Pixo's frames in
    /// its queue. Above normal it gets its turn first (never more: that's for Windows itself); in a game, normal.
    /// </summary>
    internal static void GpuPriority(IntPtr process, bool above)
    {
        try
        {
            int status = D3DKMTSetProcessSchedulingPriorityClass(process, above ? 3 /* ABOVE_NORMAL */ : 2 /* NORMAL */);
            if (status != 0 && !_gpuPriorityLogged) { _gpuPriorityLogged = true; Services.AppLog.Write($"gpu priority: not set (0x{status:X8})"); }
        }
        catch { }
    }
    static bool _gpuPriorityLogged;

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    static extern int D3DKMTSetProcessSchedulingPriorityClass(IntPtr process, int priority);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern bool SetProcessWorkingSetSizeEx(IntPtr process, IntPtr min, IntPtr max, uint flags);
}
