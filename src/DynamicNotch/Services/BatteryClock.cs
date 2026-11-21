using System.Runtime.InteropServices;
using Windows.Devices.Power;
using Windows.System.Power;

namespace DynamicNotch.Services;

/// <summary>
/// How long the battery will last (or take to fill), worked out the careful way. The battery's own rate jumps around
/// with every spike of use, so two readings are blended: the energy it really lost (or gained) over the last minutes
/// (a straight line through a reading every 20 s), and its live rate smoothed over a couple of minutes. The longer the
/// line, the more it counts. Charging slows down past 80 %, and the time to full allows for that. Plugging in or out
/// starts over. If the battery gives no figures, Windows' own estimate is used.
/// </summary>
public sealed class BatteryClock
{
    readonly record struct Point(double T, double Mwh);

    readonly List<Point> _points = new();
    readonly DateTime _t0 = DateTime.UtcNow;
    readonly object _gate = new();
    BatteryStatus? _state;
    double? _live;
    double _liveT;
    double _since;

    public bool Present { get; private set; }
    public bool Charging { get; private set; }
    public bool Discharging { get; private set; }
    public bool Full { get; private set; }
    public double? Watts { get; private set; }
    public TimeSpan? Left { get; private set; }
    public TimeSpan? ToFull { get; private set; }
    /// <summary>Seconds of steady readings behind the figure (a fresh one is rougher).</summary>
    public double Settled { get; private set; }

    public void Sample()
    {
        BatteryReport r;
        try { r = Battery.AggregateBattery.GetReport(); } catch { return; }
        lock (_gate)
        {
            if (r.Status == BatteryStatus.NotPresent) { Present = false; return; }
            Present = true;
            double now = (DateTime.UtcNow - _t0).TotalSeconds;
            if (_state != r.Status) { _state = r.Status; _points.Clear(); _live = null; _since = now; }
            Charging = r.Status == BatteryStatus.Charging;
            Discharging = r.Status == BatteryStatus.Discharging;
            double? rem = r.RemainingCapacityInMilliwattHours, full = r.FullChargeCapacityInMilliwattHours;
            Full = !Charging && !Discharging && rem is { } a && full is { } b && b > 0 && a >= b * 0.97;

            if (rem is { } m)
            {
                _points.Add(new Point(now, m));
                while (_points.Count > 0 && now - _points[0].T > 900) _points.RemoveAt(0); // the last 15 minutes
            }
            if (r.ChargeRateInMilliwatts is { } mw && mw != 0)
            {
                double w = Math.Abs(mw) / 1000.0;
                double k = _live == null ? 1 : 1 - Math.Exp(-(now - _liveT) / 150.0); // ~2.5 min of smoothing
                _live = _live is { } e ? e + (w - e) * k : w;
                _liveT = now;
            }

            // the real loss (or gain) over the last minutes: the slope of a straight line through the readings
            double? lineW = null;
            double span = _points.Count >= 3 ? _points[^1].T - _points[0].T : 0;
            if (span >= 120)
            {
                double mt = _points.Average(p => p.T), mm = _points.Average(p => p.Mwh), num = 0, den = 0;
                foreach (var p in _points) { num += (p.T - mt) * (p.Mwh - mm); den += (p.T - mt) * (p.T - mt); }
                double perSec = den > 0 ? num / den : 0; // mWh a second
                bool rightWay = Discharging ? perSec < 0 : Charging && perSec > 0;
                if (rightWay) lineW = Math.Abs(perSec) * 3.6; // mWh/s → W
            }
            double trust = Math.Clamp((span - 120) / 480, 0, 0.8); // after ~10 minutes the line carries 80 %
            Watts = lineW is { } lw && lw > 0.2 ? (_live is { } lv ? lw * trust + lv * (1 - trust) : lw) : _live;
            Settled = now - _since;

            Left = ToFull = null;
            if (Watts is { } use && use > 0.3 && rem is { } r2)
            {
                if (Discharging) Left = TimeSpan.FromHours(r2 / 1000.0 / use);
                else if (Charging && full is { } f2 && f2 > 0) ToFull = TimeToFull(r2, f2, use);
            }
            if (Left == null && Discharging && GetSystemPowerStatus(out var ps) && ps.BatteryLifeTime is > 0 and < 100 * 3600)
                Left = TimeSpan.FromSeconds(ps.BatteryLifeTime);
        }
    }

    /// <summary>Up to 80 % at today's speed; the last stretch slows down (the charger eases off to protect the battery).</summary>
    static TimeSpan TimeToFull(double rem, double full, double watts)
    {
        double knee = full * 0.8, hours;
        if (rem < knee) hours = (knee - rem) / 1000.0 / watts + (full - knee) / 1000.0 / (watts * 0.45);
        else hours = (full - rem) / 1000.0 / (watts * 0.7);
        return TimeSpan.FromHours(Math.Max(0, hours));
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SystemPowerStatus
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
