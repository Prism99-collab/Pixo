using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// The mouse and headset batteries, as two tiny rings beside the date on Home: the ring fills with the level
/// (green, yellow under 50 %, red under 20 %), the device's icon sits inside, a little bolt shows while it charges.
/// A reading too old to trust (a sleeping mouse) or a headset that's off is drawn grey. Hover for the details.
/// Low battery (20 / 10 / 5 %) pops up once per threshold, re-armed after charging; old readings never alert.
/// </summary>
public partial class NotchWindow
{
    readonly DeviceBatteryService _devices;
    readonly Dictionary<string, int> _lowAlerted = new(); // per device: the lowest threshold already announced

    static readonly Color RingGreen = Color.FromRgb(0x30, 0xD1, 0x58), RingYellow = Color.FromRgb(0xFF, 0xD6, 0x0A), RingRed = Color.FromRgb(0xFF, 0x45, 0x3A);

    void WireDevices()
    {
        _devices.Changed += OnDevicesChanged;
        _bt.Changed += OnDevicesChanged; // a Bluetooth controller, headset or mouse with a battery joins the rings
        LiveSwitch.IsVisibleChanged += (_, _) => RenderDeviceRings(); // the chips came or went: the rings re-fit
        _devices.Busy = () => _gameQuiet; // never while a game is in front (NotchWindow.Smoothness)
        _devices.HubEnabled = _settings.DeviceHub;
        _devices.Start();
    }

    string _devicesLogged = "";
    int _devicesCount;

    void OnDevicesChanged()
    {
        string summary = string.Join(", ", AllDeviceBatteries().Select(d => $"{d.Name} {(d.Level is { } l ? l + "%" : "--")} {d.State}{(d.Charging ? " charging" : "")}"));
        if (summary != _devicesLogged) { _devicesLogged = summary; AppLog.Write("device batteries: " + (summary.Length > 0 ? summary : "none")); CyberPulse(); }
        int count = AllDeviceBatteries().Count;
        if (count != _devicesCount)
        {
            // rings appeared or went away: the switcher re-fits its chips to the header space left
            _devicesCount = count;
            _switchKey = "";
            RenderSwitch();
        }
        RenderDeviceRings();
        foreach (var d in _devices.Devices) CheckDeviceLow(d); // (Bluetooth ones have their own low alert)
    }

    /// <summary>Every wireless battery the notch knows: the receivers' and the hub's, then Bluetooth devices that report one.</summary>
    readonly Dictionary<string, (int Level, DateTime At, DateTime RoseAt)> _btTrend = new(StringComparer.OrdinalIgnoreCase);

    List<DeviceBattery> AllDeviceBatteries()
    {
        // a device that's switched off or gone isn't shown (its receiver may still be plugged in); asleep ones stay, dimmed
        var list = _devices.Devices.Where(d => d.State != BatteryState.Off).ToList();
        try
        {
            foreach (var b in _bt.ConnectedDevices)
            {
                if (b.Battery is not { } pct || list.Any(d => d.Name.Equals(b.Name, StringComparison.OrdinalIgnoreCase))) continue;
                string c = b.Category;
                var kind = c.StartsWith("Input.Gaming", StringComparison.OrdinalIgnoreCase) ? DeviceKind.Controller
                    : c.StartsWith("Input.Mouse", StringComparison.OrdinalIgnoreCase) ? DeviceKind.Mouse
                    : c.StartsWith("Input.Keyboard", StringComparison.OrdinalIgnoreCase) ? DeviceKind.Keyboard
                    : c.StartsWith("Audio", StringComparison.OrdinalIgnoreCase) ? (c.Contains("Speaker", StringComparison.OrdinalIgnoreCase) ? DeviceKind.Speaker : DeviceKind.Headset)
                    : DeviceKind.Other;
                list.Add(new DeviceBattery(kind, b.Name, Math.Clamp(pct, 0, 100), BtCharging(b.Name, pct), BatteryState.Live, DateTime.UtcNow));
            }
        }
        catch { }
        // developer look (DYNAMICNOTCH_FAKEDEVICES=N): N made-up devices on top, to see a crowded header
        if (int.TryParse(Environment.GetEnvironmentVariable("DYNAMICNOTCH_FAKEDEVICES"), out int fake))
            for (int i = 0; i < fake; i++)
                list.Add(new DeviceBattery((DeviceKind)(i % 6), $"Test device {i + 1}", new[] { 82, 14, 55, 37, 91, 8 }[i % 6], i == 2, BatteryState.Live, DateTime.UtcNow));
        return list;
    }

    /// <summary>
    /// Bluetooth doesn't say "charging", so it's read from the level: going up means it's on a charger (until it stops
    /// rising for 20 minutes, or drops). At 100 % and still rising lately it reads as charged.
    /// </summary>
    bool BtCharging(string name, int level)
    {
        var now = DateTime.UtcNow;
        if (!_btTrend.TryGetValue(name, out var t)) { _btTrend[name] = (level, now, DateTime.MinValue); return false; }
        if (level > t.Level) t = (level, now, now);
        else if (level < t.Level) t = (level, now, DateTime.MinValue);
        _btTrend[name] = t;
        return t.RoseAt != DateTime.MinValue && now - t.RoseAt < TimeSpan.FromMinutes(20);
    }

    /// <summary>
    /// How many rings fit in the header beside the clock, leaving room for the "what's live" chips when they show and for
    /// the buttons on the right: never more than four, never fewer than one.
    /// </summary>
    int RingsThatFit()
    {
        if (DeviceRings.Parent is not FrameworkElement left || left.Parent is not Grid head || head.ActualWidth <= 0 || head.ColumnDefinitions.Count < 3) return 3;
        double room = head.ActualWidth - ClockText.ActualWidth - DeviceRings.Margin.Left - head.ColumnDefinitions[2].ActualWidth;
        if (LiveSwitch.Visibility == Visibility.Visible) room -= 2 * (ChipW + ChipGap) + 10; // the switch keeps at least two chips
        else room -= 24; // a little air before the buttons
        return Math.Clamp((int)(room / 22), 1, 4);
    }

    string _ringsKey = "";

    /// <summary>
    /// Rings on Home. When the "what to show" switcher also needs the header, they share it: the rings go compact
    /// (the percentage moves into their hint) and the date steps aside, so nothing overlaps.
    /// </summary>
    void RenderDeviceRings()
    {
        var devices = AllDeviceBatteries();
        bool show = devices.Count > 0 && _page == 0;
        // minimal: with rings, the clock stands alone (the date is in its hint) and each ring is just a ring, its
        // level in its fill and colour; the number only shows when it's running low
        bool compact = show;
        DateText.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        if (!show) { DeviceRings.Visibility = Visibility.Collapsed; return; }
        DeviceRings.Visibility = Visibility.Visible;
        // only rebuilt when something on them changes (a ring under the pointer keeps its hint)
        // as many rings as the header has room for: the ones running lowest first, the rest behind a small "+N"
        int fit = RingsThatFit();
        var rest = new List<DeviceBattery>();
        if (devices.Count > fit)
        {
            var ordered = devices.OrderBy(d => d.State == BatteryState.Live ? d.Level ?? 101 : 102).ToList();
            int shown = Math.Max(1, fit - 1); // the "+N" takes a ring's place
            devices = ordered.Take(shown).ToList();
            rest = ordered.Skip(shown).ToList();
        }
        string key = (compact ? "c|" : "f|") + fit + "|" + string.Join("|", devices.Concat(rest).Select(d => $"{d.Kind}{d.Name}{d.Level}{d.State}{d.Charging}"));
        if (key == _ringsKey) return;
        _ringsKey = key;
        DeviceRings.Children.Clear();
        foreach (var d in devices) DeviceRings.Children.Add(DeviceRing(d, compact));
        if (rest.Count > 0)
            DeviceRings.Children.Add(new Border
            {
                Height = 17, MinWidth = 17, CornerRadius = new CornerRadius(8.5), Padding = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center,
                Background = Frozen(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF)), BorderBrush = Frozen(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
                Child = new TextBlock { Text = $"+{rest.Count}", FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = Frozen(Color.FromArgb(0xD8, 0xFF, 0xFF, 0xFF)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                ToolTip = string.Join("\n", rest.Select(d => $"{d.Name} · {(d.Level is { } l ? l + "%" : "--")}")),
            });
    }

    FrameworkElement DeviceRing(DeviceBattery d, bool compact = false)
    {
        const double size = 17, stroke = 1.7;
        bool live = d.State == BatteryState.Live && d.Level != null;
        double level = Math.Clamp((d.Level ?? 0) / 100.0, 0, 1);
        var color = !live ? Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)
            : d.Charging ? RingGreen : level < 0.2 ? RingRed : level < 0.5 ? RingYellow : RingGreen;

        var grid = new Grid { Width = size, Height = size, Background = Brushes.Transparent };
        grid.Children.Add(new Ellipse { Stroke = Frozen(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)), StrokeThickness = stroke });
        if (d.Level != null && level > 0)
            grid.Children.Add(new Path
            {
                Data = Arc(size, stroke, level), Stroke = Frozen(color), StrokeThickness = stroke,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            });
        grid.Children.Add(new TextBlock
        {
            Text = d.Kind.Glyph(),
            FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 8.5,
            Foreground = live ? Brushes.White : DimBrush,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        });
        if (live && d.Charging)
        {
            // a small bolt on the ring's corner, breathing while it charges
            var bolt = new Border
            {
                Width = 9, Height = 9, CornerRadius = new CornerRadius(4.5), Background = Frozen(RingGreen),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -3, -2),
                BorderBrush = Brushes.Black, BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 5.5, Foreground = Brushes.Black,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
            if (!Eco)
                bolt.BeginAnimation(OpacityProperty, Capped(new DoubleAnimation(1, 0.45, TimeSpan.FromMilliseconds(900))
                    { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } }, 20));
            bolt.Unloaded += (_, _) => bolt.BeginAnimation(OpacityProperty, null); // a replaced ring stops breathing
            grid.Children.Add(bolt);
        }
        string state = d.State switch
        {
            BatteryState.Off => "off",
            BatteryState.Stale => d.Level is { } last ? $"no recent report (last {last}%)" : "waiting for the first report",
            _ => d.Charging ? "charging" : "on battery",
        };
        string hint = live ? $"{d.Name} · {d.Level}% · {state}" : $"{d.Name} · {state}";

        // the percentage right beside the ring, so it reads without hovering
        var pct = new TextBlock
        {
            Text = d.Level is { } l ? $"{l}%" : "--", FontSize = 11, FontWeight = FontWeights.SemiBold,
            Foreground = live ? Frozen(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)) : DimBrush,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 0, 0),
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"), Typography = { NumeralAlignment = FontNumeralAlignment.Tabular },
        };
        var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, compact ? 4 : 8, 0), Background = Brushes.Transparent };
        item.Children.Add(grid);
        if (!compact || (live && level < 0.2 && !d.Charging)) { pct.Foreground = compact ? Frozen(RingRed) : pct.Foreground; item.Children.Add(pct); } // low: say it
        // the hint opens the moment the pointer arrives (no tooltip delay), in front of everything
        item.MouseEnter += (_, _) => ShowTip(item, hint);
        return item;
    }

    /// <summary>A ring arc from the top, clockwise, covering <paramref name="share"/> of the circle.</summary>
    static Geometry Arc(double size, double stroke, double share)
    {
        double r = (size - stroke) / 2, c = size / 2;
        if (share >= 0.999)
        {
            var full = new EllipseGeometry(new Point(c, c), r, r);
            full.Freeze();
            return full;
        }
        double a = share * 2 * Math.PI;
        var start = new Point(c, c - r);
        var end = new Point(c + r * Math.Sin(a), c - r * Math.Cos(a));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(r, r), 0, share > 0.5, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }

    /// <summary>20 %, 10 %, 5 %: each announced once on the way down; charging or getting back to 25 % re-arms them.</summary>
    void CheckDeviceLow(DeviceBattery d)
    {
        if (d.State != BatteryState.Live || d.Level is not { } level) return; // old readings never alert
        if (d.Charging || level >= 25) { _lowAlerted.Remove(d.Name); return; }
        int threshold = level <= 5 ? 5 : level <= 10 ? 10 : level <= 20 ? 20 : 0;
        if (threshold == 0) return;
        if (_lowAlerted.TryGetValue(d.Name, out int done) && done <= threshold) return;
        _lowAlerted[d.Name] = threshold;
        string what = d.Kind.Label();
        ShowOsd("battery", d.Kind.Glyph(), level / 100.0, $"{what} battery low · {level}%", d.Name, null,
            glyphBrush: level <= 10 ? Red : Orange, seconds: 3.5, force: true, motion: Motion.Shake);
    }
}
