using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;

namespace DynamicNotch;

/// <summary>
/// A watch on the notch's own thread while it's open: if it stops answering for a moment (a freeze you can feel),
/// what it was doing then, and whether memory work or paging was behind it, is written to the log. Nothing runs
/// while the notch rests.
/// </summary>
public partial class NotchWindow
{
    /// <summary>What the notch is busy with right now (a few words, for the log when it freezes).</summary>
    static volatile string _doing = "";
    static void BusyWith(string what) => _doing = what;

    Thread? _hitchThread;
    volatile bool _hitchWatch;
    readonly ManualResetEventSlim _hitchOn = new(false);

    void HitchWatch(bool on)
    {
        _hitchWatch = on;
        if (on) _hitchOn.Set(); else _hitchOn.Reset();
        if (!on || _hitchThread != null) return;
        var ui = Dispatcher;
        _hitchThread = new Thread(() =>
        {
            while (true)
            {
                if (!_hitchWatch) { _hitchOn.Wait(); continue; } // (asleep until the notch opens: no wake-ups while it rests)
                using var answered = new ManualResetEventSlim();
                long t0 = Stopwatch.GetTimestamp();
                var pause0 = GC.GetTotalPauseDuration();
                int gen2 = GC.CollectionCount(2), gen0 = GC.CollectionCount(0);
                uint faults0 = PageFaults();
                try { ui.BeginInvoke(DispatcherPriority.Send, () => answered.Set()); } catch { return; }
                if (!answered.Wait(300))
                {
                    string during = _doing;
                    answered.Wait(15_000);
                    double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                    Services.AppLog.Write($"freeze: {ms:0} ms, while {(during.Length > 0 ? during : "(nothing marked)")}"
                        + $" | memory cleanup +{(GC.GetTotalPauseDuration() - pause0).TotalMilliseconds:0} ms (gen0 +{GC.CollectionCount(0) - gen0}, gen2 +{GC.CollectionCount(2) - gen2})"
                        + $" | page faults +{PageFaults() - faults0}");
                }
                Thread.Sleep(120);
            }
        }) { IsBackground = true, Name = "Pixo freeze watch", Priority = ThreadPriority.AboveNormal };
        _hitchThread.Start();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessMemoryCounters { public uint Cb, PageFaultCount; public UIntPtr PeakWorkingSet, WorkingSet, QuotaPeakPaged, QuotaPaged, QuotaPeakNonPaged, QuotaNonPaged, Pagefile, PeakPagefile; }

    [DllImport("psapi.dll")] static extern bool GetProcessMemoryInfo(IntPtr process, out ProcessMemoryCounters counters, uint size);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();

    static uint PageFaults()
    {
        try { return GetProcessMemoryInfo(GetCurrentProcess(), out var c, (uint)Marshal.SizeOf<ProcessMemoryCounters>()) ? c.PageFaultCount : 0; }
        catch { return 0; }
    }
}
