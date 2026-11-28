using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// What the closed notch shows beside the face, and the song on the line.
///  • Parts (Preferences → Appearance → Closed): the clock, the date, the battery, the weather, the CPU, Claude's
///    session. In your order they sit left of the face, then right, then left again…; with more than two, each side
///    takes turns every five seconds (a soft crossfade). The notch is as wide as the widest part needs, so it never
///    jumps between turns. Only in the plain closed notch (Notch and Capsule): the music, a call or a timer have their own.
///  • Line: the line fills from the left as the song plays, in its colour, gliding from second to second.
/// A one-second tick runs only while one of them is on screen.
/// </summary>
public partial class NotchWindow
{
    internal static readonly (string Key, string Name)[] ClosedPartKinds =
    {
        ("clock", "Clock"), ("date", "Date"), ("battery", "Battery"), ("weather", "Weather"), ("cpu", "CPU"), ("claude", "Claude session"),
    };

    Grid? _partsRow;
    TextBlock? _partL, _partR;
    DispatcherTimer? _partsTimer;
    int _partsTurn;
    DateTime _partsTurnAt;
    double _partsWidth;
    string _partsLeftNow = "", _partsRightNow = "";
    CpuMeter? _partsCpu;
    double? _partsCpuNow;
    DateTime _partsCpuAt, _partsWxAt, _partsAiAt;
    Border? _lineFill;
    ScaleTransform? _lineScale;
    double _lineShown = -1;

    /// <summary>Every part in your order (the ones you haven't placed: in the default order, after).</summary>
    List<string> PartOrder => _settings.ClosedPartOrder.Where(k => ClosedPartKinds.Any(c => c.Key == k)).Distinct()
        .Concat(ClosedPartKinds.Select(c => c.Key)).Distinct().ToList();
    List<string> PartsOn => PartOrder.Where(_settings.ClosedParts.Contains).ToList();
    // (a notch under 20 px tall has no room for words)
    bool PartsShown => _mode == Mode.Idle && ClosedStyleNow != "line" && IdleSize.Height >= 20 && _settings.ClosedParts.Count > 0 && !_hidden;
    bool LineProgressShown => _mode == Mode.Slim && ClosedStyleNow == "line" && _settings.LineProgress && CurrentActivity == LiveActivity.Media
        && _media.HasSession && _media.Duration > TimeSpan.Zero && !_hidden;

    // Preferences → Appearance → Closed
    internal List<(string Key, string Name, bool On)> PrefClosedParts => PartOrder.Select(k => (k, ClosedPartKinds.First(c => c.Key == k).Name, _settings.ClosedParts.Contains(k))).ToList();
    internal void PrefSetPart(string key, bool on)
    {
        _settings.ClosedParts.Remove(key);
        if (on) _settings.ClosedParts.Add(key);
        ClosedPartsChanged();
    }
    internal void PrefMovePart(string key, int dir)
    {
        var order = PartOrder;
        int i = order.IndexOf(key), j = i + dir;
        if (i < 0 || j < 0 || j >= order.Count) return;
        (order[i], order[j]) = (order[j], order[i]);
        _settings.ClosedPartOrder = order;
        ClosedPartsChanged();
    }
    internal void PrefSetLineProgress(bool on) { _settings.LineProgress = on; ClosedPartsChanged(); }
    void ClosedPartsChanged()
    {
        _settings.Save();
        _partsTurn = 0; _partsTurnAt = DateTime.UtcNow;
        _partsWidth = 0; // measured again for what's on now
        PartsSync();
        ApplyShape();
    }

    /// <summary>Starts or stops the tick (called with every change of shape and of the settings).</summary>
    void PartsSync()
    {
        bool parts = PartsShown, line = LineProgressShown;
        if (parts || line) EnsurePartsRow();
        if (_partsRow != null)
        {
            _partsRow.Visibility = parts ? Visibility.Visible : Visibility.Collapsed;
            _partsRow.Height = Math.Min(IdleSize.Height, 26); // (level with the face, whose middle is 13 px down in any notch)
        }
        if (_lineFill != null) _lineFill.Visibility = line ? Visibility.Visible : Visibility.Collapsed;
        SlimGlow.Visibility = line ? Visibility.Collapsed : Visibility.Visible; // (the fill says it: no glow on top of it)
        if (parts || line)
        {
            if (_partsTimer == null) { _partsTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) }; _partsTimer.Tick += (_, _) => PartsTick(); }
            if (!_partsTimer.IsEnabled) _partsTimer.Start();
            PartsTick();
        }
        else _partsTimer?.Stop();
    }

    void EnsurePartsRow()
    {
        if (_partsRow != null) return;
        TextBlock Slot(HorizontalAlignment side) => new()
        {
            FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Frozen(Color.FromArgb(0xEB, 0xFF, 0xFF, 0xFF)), FontFamily = ThemeText,
            HorizontalAlignment = side, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(side == HorizontalAlignment.Left ? 13 : 0, 0, side == HorizontalAlignment.Right ? 13 : 0, 0),
        };
        _partL = Slot(HorizontalAlignment.Left);
        _partR = Slot(HorizontalAlignment.Right);
        Typography.SetNumeralAlignment(_partL, FontNumeralAlignment.Tabular);
        Typography.SetNumeralAlignment(_partR, FontNumeralAlignment.Tabular);
        _partsRow = new Grid { IsHitTestVisible = false, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Top, Children = { _partL, _partR } };
        Stage.Children.Add(_partsRow);
        // the line's fill: a bar along the whole line, scaled from the left to the song's place
        _lineScale = new ScaleTransform(0, 1);
        _lineFill = new Border { IsHitTestVisible = false, Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Stretch, RenderTransform = _lineScale };
        Stage.Children.Add(_lineFill);
    }

    /// <summary>One part's words (null: nothing to show now, it's skipped).</summary>
    (string Glyph, string Text)? PartOf(string key)
    {
        switch (key)
        {
            case "clock": return ("", DateTime.Now.ToString("t"));
            case "date": return ("", DateTime.Now.ToString("ddd d"));
            case "battery":
                if (!_power.HasBattery) return null;
                return (_power.PluggedIn ? "" : "", $"{_power.Percent}%");
            case "weather":
                if (_wx?.Last is not { } f || (DateTime.UtcNow - f.FetchedUtc).TotalHours > 3) return null; // (an old forecast isn't the weather now)
                double t = WeatherService.UsesFahrenheit(_wx) ? WeatherService.ToF(f.Current.Temp) : f.Current.Temp;
                return (f.Current.IsDay ? "" : "", $"{t:0}°");
            case "cpu": return _partsCpuNow is { } cpu ? ("\uE950", $"{cpu:0}%") : null;
            case "claude":
                var session = AiLimits.Cached("claude").Last?.Windows.FirstOrDefault(w => w.Label == "Session");
                return session == null ? null : ("", $"{session.Used:0}%");
        }
        return null;
    }

    void PartsTick()
    {
        bool parts = PartsShown, line = LineProgressShown;
        if (!parts && !line) { PartsSync(); return; } // (the song ended, the notch went somewhere: all of it goes)
        if (_lineFill!.Visibility == Visibility.Visible != line || _partsRow!.Visibility == Visibility.Visible != parts) { PartsSync(); return; } // (one came or went)
        if (line) LineProgressTick();
        if (!parts) return;
        var on = PartsOn;
        var now = DateTime.UtcNow;
        // the readings the parts need, each at its own pace (only when that part is on)
        if (on.Contains("cpu") && (now - _partsCpuAt).TotalSeconds >= 2)
        {
            _partsCpuAt = now;
            try
            {
                if (_partsCpu == null) { _partsCpu = new CpuMeter(); _partsCpu.Read(); } // (the first reading only starts the count)
                else _partsCpuNow = _partsCpu.Read();
            }
            catch { }
        }
        if (on.Contains("weather") && (now - _partsWxAt).TotalMinutes >= 10)
        {
            // the Weather page's place and forecast (a new one when it's over 30 min old; nothing without a place)
            _partsWxAt = now;
            if (_wx == null) _ = Task.Run(WeatherService.Load).ContinueWith(t => { _wx ??= t.Result; _partsWxAt = default; }, TaskScheduler.FromCurrentSynchronizationContext());
            else if (_wx.Place != null && (_wx.Last == null || (now - _wx.Last.FetchedUtc).TotalMinutes > 30)) WxFetch();
        }
        if (on.Contains("claude") && (now - _partsAiAt).TotalMinutes >= 5 && AiLimits.Found("claude"))
        {
            _partsAiAt = now;
            _ = AiLimits.ReadAsync("claude"); // (asked at most every 5 min; the part shows the last answer)
        }

        // left takes the 1st, 3rd, 5th…; right the 2nd, 4th…; each side turns every five seconds
        var all = on.Select(k => (Key: k, Part: PartOf(k))).Where(p => p.Part != null).ToList();
        var left = all.Where((_, i) => i % 2 == 0).ToList();
        var right = all.Where((_, i) => i % 2 == 1).ToList();
        if ((now - _partsTurnAt).TotalSeconds >= 5) { _partsTurnAt = now; _partsTurn++; }
        string Say((string Glyph, string Text)? p) => p is { } x ? x.Glyph + " " + x.Text : "";
        string l = left.Count == 0 ? "" : Say(left[_partsTurn % left.Count].Part), r = right.Count == 0 ? "" : Say(right[_partsTurn % right.Count].Part);
        PartSet(_partL!, left.Count == 0 ? "" : left[_partsTurn % left.Count].Key, l, ref _partsLeftNow);
        PartSet(_partR!, right.Count == 0 ? "" : right[_partsTurn % right.Count].Key, r, ref _partsRightNow);

        // the width: room for the widest part of each side, so the notch stays put while they turn
        double Widest(List<(string Key, (string Glyph, string Text)? Part)> side) => side.Count == 0 ? 0 : side.Max(p => PartWidth(p.Part!.Value));
        double want = Math.Max(Widest(left), Widest(right));
        double middle = FaceAllowed() ? 44 : 16; // (no face: the two sides closer)
        double width = want <= 0 ? 0 : middle + 2 * (want + 13 + 10);
        if (Math.Abs(width - _partsWidth) > 1) { _partsWidth = width; ApplyShape(); }
    }

    void PartSet(TextBlock tb, string key, string text, ref string shown)
    {
        if (text == shown) return;
        shown = text;
        // another part taking its turn fades over; the same part's new value (a new minute) just changes
        bool turning = tb.Tag is string was && was.Length > 0 && was != key;
        tb.Tag = key;
        if (tb == _partL ? _partLFading : _partRFading) return; // (mid-fade: it shows the latest words when it comes back)
        if (!turning || Eco) { PartFill(tb, text); return; }
        if (tb == _partL) _partLFading = true; else _partRFading = true;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(140));
        fade.Completed += (_, _) =>
        {
            if (tb == _partL) _partLFading = false; else _partRFading = false;
            PartFill(tb, tb == _partL ? _partsLeftNow : _partsRightNow);
            tb.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)));
        };
        tb.BeginAnimation(OpacityProperty, fade);
    }
    bool _partLFading, _partRFading;

    void PartFill(TextBlock tb, string text)
    {
        tb.Inlines.Clear();
        int cut = text.IndexOf('\u2009');
        if (cut > 0) tb.Inlines.Add(new Run(text[..cut]) { FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 10.5, BaselineAlignment = BaselineAlignment.Center });
        tb.Inlines.Add(new Run(cut >= 0 ? (cut > 0 ? " " : "") + text[(cut + 1)..] : text));
    }

    readonly Dictionary<string, double> _partWidths = new();
    double PartWidth((string Glyph, string Text) p)
    {
        // the same digits are as wide (tabular), so "12:47" measured once stands for every minute: 0s stand in for them
        string shape = new(p.Text.Select(ch => char.IsDigit(ch) ? '0' : ch).ToArray());
        if (_partWidths.TryGetValue(shape, out double w)) return w;
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var text = new FormattedText(shape, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(ThemeText, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 11.5, Brushes.White, dip);
        if (_partWidths.Count > 64) _partWidths.Clear();
        return _partWidths[shape] = Math.Ceiling(text.WidthIncludingTrailingWhitespace) + (p.Glyph.Length > 0 ? 15 : 0); // (+ the icon and its space)
    }

    /// <summary>The closed notch's width with its parts (0: no parts).</summary>
    double PartsWidthNow => PartsShown ? _partsWidth : 0;

    void LineProgressTick()
    {
        if (_lineFill == null || _lineScale == null) return;
        var c = _media.Accent;
        if (_lineFill.Background is not SolidColorBrush b || b.Color != c) _lineFill.Background = Frozen(new SolidColorBrush(c));
        double total = _media.Duration.TotalSeconds;
        if (total <= 0) return;
        double at = Math.Clamp(_media.Position.TotalSeconds / total, 0, 1);
        double next = _media.IsPlaying ? Math.Clamp((_media.Position.TotalSeconds + _media.Rate) / total, 0, 1) : at;
        // it glides to where the song will be in a second (no steps); a jump (a seek, a new song) snaps there
        if (Math.Abs(at - _lineShown) > 0.05 || !_media.IsPlaying || Eco) { _lineScale.BeginAnimation(ScaleTransform.ScaleXProperty, null); _lineScale.ScaleX = at; }
        if (_media.IsPlaying && !Eco)
        {
            var a = new DoubleAnimation(at, next, TimeSpan.FromSeconds(1));
            Timeline.SetDesiredFrameRate(a, 30);
            _lineScale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        }
        _lineShown = next;
    }
}
