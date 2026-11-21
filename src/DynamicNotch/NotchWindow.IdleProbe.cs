namespace DynamicNotch;

// Developer only (DYNAMICNOTCH_IDLELOG=1): every 5 s, writes to app.log what may keep Pixo drawing while it sits
// still (the springs, the music meter), with the CPU it used meanwhile.
public partial class NotchWindow
{
    void WireIdleProbe()
    {
        if (Environment.GetEnvironmentVariable("DYNAMICNOTCH_IDLELOG") != "1") return;
        var me = System.Diagnostics.Process.GetCurrentProcess();
        double last = me.TotalProcessorTime.TotalSeconds;
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        t.Tick += (_, _) =>
        {
            me.Refresh();
            double now = me.TotalProcessorTime.TotalSeconds, used = (now - last) / 5 * 100;
            last = now;
            Services.AppLog.Write($"idle probe: {used:0.0}% of a core | mode {_mode}, springs {(_animating ? "moving" : "still")}, " +
                $"meter {(_meter.IsEnabled ? $"on every {_meter.Interval.TotalMilliseconds:0} ms" : "off")} (bars {_eqRunning}, slim glow {_slimBeat}, call {_callBarsOn}), music {_media.IsPlaying}");
        };
        t.Start();
    }
}
