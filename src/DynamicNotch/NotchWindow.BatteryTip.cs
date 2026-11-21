using System.Windows;
using System.Windows.Threading;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// Resting the pointer on the battery (or its percentage) in the open notch says how long it has left, or how long
/// until it's full. The reading runs quietly every 20 s in the background (one small call to Windows) so the figure
/// is already settled when you look; plugging in or out starts it over.
/// </summary>
public partial class NotchWindow
{
    readonly Services.BatteryClock _batClock = new();
    readonly DispatcherTimer _batClockTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(20) };

    void WireBatteryTip()
    {
        // (wired before the battery is first read: a desktop simply never gets the hint, and the clock skips its work)
        _batClockTimer.Tick += (_, _) => { if (_power.HasBattery) _ = Task.Run(_batClock.Sample); };
        _batClockTimer.Start();
        _ = Task.Run(_batClock.Sample);
        _power.PluggedChanged += plugged => _ = Task.Run(_batClock.Sample);
        foreach (var el in new FrameworkElement[] { Battery, BatteryText })
        {
            el.MouseEnter += (_, _) =>
            {
                if (!_power.HasBattery) { el.ToolTip = null; return; }
                el.ToolTip = BatteryTipText(); // the words are ready before the hint shows; a fresh reading follows
                _ = Task.Run(_batClock.Sample).ContinueWith(_ => Dispatcher.BeginInvoke(() => { if (el.IsMouseOver) el.ToolTip = BatteryTipText(); }));
            };
        }
    }

    string BatteryTipText()
    {
        var c = _batClock;
        if (!c.Present) return Loc.T("Battery");
        string watts = c.Watts is { } w ? $"{w:0.#} W" : "";
        string about = c.Settled < 150 ? Loc.T("About") + " " : ""; // the first couple of minutes after a change are rougher
        if (c.Charging)
            return c.ToFull is { } tf
                ? $"{about}{Duration(tf)} {Loc.T("to full")}" + (watts != "" ? $" · {Loc.T("charging at")} {watts}" : "")
                : Loc.T("Charging");
        if (c.Full || _power.PluggedIn && _power.Percent >= 99) return Loc.T("Fully charged");
        if (_power.PluggedIn) return Loc.T("Plugged in, not charging");
        if (c.Left is { } left) return $"{about}{Duration(left)} {Loc.T("left")}" + (watts != "" ? $" · {Loc.T("using")} {watts}" : "");
        return Loc.T("Working out the time left…");
    }

    static string Duration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : $"{Math.Max(1, (int)Math.Round(t.TotalMinutes))} min";
}
