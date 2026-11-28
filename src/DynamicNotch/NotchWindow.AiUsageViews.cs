using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// The AI usage page's three views (the switch on its top left, remembered):
///  • Limits: a card per provider; each bar also shows, faintly, where it will be when it resets at the pace so far
///    (red to the end when that pace runs into the limit first);
///  • Pace: one limit as a chart, from the start of its window to its reset: what you used, drawn from your readings,
///    and a dashed line on to the reset at the pace so far, with the point where it would hit the limit. It opens on
///    the limit closest to running out; the chips pick another. Above it, the answer in one line;
///  • Cost: what Claude Code cost, today large, the week under it; the last 7 days as bars (today in the accent) and
///    the week split by model.
/// The charts are drawn in one pass each (no shapes per point), only when the page shows or a reading arrives.
/// </summary>
public partial class NotchWindow
{
    Grid? _aiuAlt;
    StackPanel? _aiuSwitch;
    string? _aiuPaceKey;

    static readonly (string Key, string Name)[] AiuViews = { ("limits", "Limits"), ("pace", "Pace"), ("cost", "Cost") };

    /// <summary>Limits · Pace · Cost: one track, the view you're on lit.</summary>
    FrameworkElement AiuViewSwitch()
    {
        _aiuSwitch = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (key, name) in AiuViews)
        {
            bool on = AiLimits.View == key;
            var t = AiuText(Cap(Loc.T(name)), _cyber ? 9 : 11, on ? AiuInk : AiuSoft, weight: on ? FontWeights.Medium : FontWeights.Normal);
            var b = new Border { Child = t, Padding = new Thickness(10, 2, 10, 3), CornerRadius = new CornerRadius(_cyber ? 0 : 7), Cursor = Cursors.Hand, Background = on ? Frozen(A(AiuInk, 0x16)) : Brushes.Transparent };
            if (!on)
            {
                b.MouseEnter += (_, _) => t.Foreground = Frozen(AiuInk);
                b.MouseLeave += (_, _) => t.Foreground = Frozen(AiuSoft);
            }
            string k = key;
            b.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                if (AiLimits.View == k) return;
                FeelSound(Services.Sfx.Cue.Tap);
                AiLimits.SetView(k);
                RebuildAiUsagePage(); // (the switch, and the view under it)
            };
            _aiuSwitch.Children.Add(b);
        }
        var track = new Border
        {
            Child = _aiuSwitch, Padding = new Thickness(2), CornerRadius = new CornerRadius(_cyber ? 0 : 9), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            Background = Frozen(A(AiuInk, 0x0A)), BorderBrush = Frozen(AiuLine), BorderThickness = new Thickness(1),
        };
        return track;
    }

    /// <summary>The view the switch picked, in place of the cards (Limits are the cards themselves).</summary>
    bool AiuRenderAlt()
    {
        if (_aiuAlt == null || _aiuCards == null) return false;
        var view = AiLimits.View;
        bool alt = view != "limits";
        _aiuAlt.Visibility = alt ? Visibility.Visible : Visibility.Collapsed;
        _aiuCards.Visibility = alt ? Visibility.Collapsed : Visibility.Visible;
        if (!alt) return false;
        _aiuAlt.Children.Clear();
        _aiuAlt.Children.Add(view == "pace" ? AiuPaceView() : AiuCostView());
        return true;
    }

    // ───────────── Pace ─────────────

    /// <summary>Every limit with a reading, the one closest to running out first.</summary>
    List<(string Key, string Name, AiLimits.Reading R, AiLimits.Window W, double Projected)> AiuLimits()
    {
        var now = DateTime.UtcNow;
        var list = new List<(string, string, AiLimits.Reading, AiLimits.Window, double)>();
        foreach (var (id, name) in AiLimits.Providers)
        {
            if (!AiLimits.Shown(id) || AiLimits.Cached(id).Last is not { } r) continue;
            foreach (var w in r.Windows)
            {
                if (w.ResetsAt == null || w.Hours <= 0) continue;
                double proj = AiuProject(w, now) ?? w.Used;
                list.Add(($"{id}|{w.Label}", $"{name} · {Loc.T(w.Label)}", r, w, proj));
            }
        }
        return list.OrderByDescending(x => x.Item5).ToList();
    }

    /// <summary>Where a limit will be when it resets, at the pace since its window began (null: too early to say).</summary>
    static double? AiuProject(AiLimits.Window w, DateTime now)
    {
        if (w.ResetsAt is not { } reset || w.Hours <= 0 || w.Used <= 0) return null;
        var start = reset - TimeSpan.FromHours(w.Hours);
        double elapsed = (now - start).TotalHours;
        if (elapsed < w.Hours * 0.05) return null;
        return w.Used * w.Hours / elapsed;
    }

    FrameworkElement AiuPaceView()
    {
        var limits = AiuLimits();
        if (limits.Count == 0) return AiuEmpty(Loc.T("No limits to chart yet: they show once a provider answers"));
        var pick = limits.FirstOrDefault(l => l.Key == _aiuPaceKey);
        if (pick.Key == null) pick = limits[0];
        var now = DateTime.UtcNow;
        var root = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());

        // the limits to choose from (the riskiest first)
        var chips = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        foreach (var l in limits.Take(4))
        {
            bool on = l.Key == pick.Key;
            var t = AiuText(l.Name, 10.5, on ? AiuInk : AiuSoft, weight: on ? FontWeights.Medium : FontWeights.Normal);
            var c = new Border { Child = t, Padding = new Thickness(8, 2, 8, 3), Margin = new Thickness(0, 0, 4, 0), CornerRadius = new CornerRadius(_cyber ? 0 : 7), Cursor = Cursors.Hand, Background = on ? Frozen(A(AiuInk, 0x14)) : Brushes.Transparent };
            string key = l.Key;
            c.MouseLeftButtonUp += (_, e) => { e.Handled = true; if (_aiuPaceKey == key) return; _aiuPaceKey = key; FeelSound(Services.Sfx.Cue.Tap); AiuRenderAlt(); };
            chips.Children.Add(c);
        }
        root.Children.Add(chips);

        // the answer, in one line: where it ends up, or when it runs out
        var w = pick.W;
        var reset = w.ResetsAt!.Value;
        double? proj = AiuProject(w, now);
        var start = reset - TimeSpan.FromHours(w.Hours);
        DateTime? limitAt = proj > 100 ? start + TimeSpan.FromHours((now - start).TotalHours * 100 / Math.Max(0.01, w.Used)) : null;
        var answer = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        var big = new StackPanel { Orientation = Orientation.Horizontal };
        if (limitAt is { } la && la < reset)
        {
            big.Children.Add(AiuText(AiuWhen(la), 20, AiuHot, mono: true, weight: FontWeights.Light));
            big.Children.Add(AiuText(Loc.T("the limit, at this pace"), 10.5, AiuSoft).Also(t => { t.VerticalAlignment = VerticalAlignment.Bottom; t.Margin = new Thickness(8, 0, 0, 4); }));
        }
        else
        {
            big.Children.Add(AiuText(proj is { } p ? $"{p:0}%" : $"{w.Used:0}%", 20, AiuInk, mono: true, weight: FontWeights.Light));
            big.Children.Add(AiuText(proj != null ? Loc.T("at reset, if you keep this pace") : Loc.T("used so far"), 10.5, AiuSoft).Also(t => { t.VerticalAlignment = VerticalAlignment.Bottom; t.Margin = new Thickness(8, 0, 0, 4); }));
        }
        answer.Children.Add(big);
        var side = AiuText(string.Format(Loc.T("{0:0}% now · resets in {1}"), w.Used, AiuIn(reset - now)), 10.5, AiuSoft, mono: true);
        side.HorizontalAlignment = HorizontalAlignment.Right;
        side.VerticalAlignment = VerticalAlignment.Bottom;
        side.Margin = new Thickness(0, 0, 0, 4);
        answer.Children.Add(side);
        Grid.SetRow(answer, 1);
        root.Children.Add(answer);

        var points = AiLimits.History(pick.Key.Split('|')[0], w.Label, start);
        var chart = new PaceChart
        {
            Start = start, Reset = reset, Now = now, Used = w.Used, Projected = AiLimits.Projection ? proj : null, LimitAt = AiLimits.Projection ? limitAt : null, Points = points,
            Ink = AiuInk, Soft = AiuSoft, Line = AiuLine, Accent = AiuAcc, Hot = AiuHot, Font = ThemeText,
        };
        Grid.SetRow(chart, 2);
        root.Children.Add(chart);
        return root;
    }

    static string AiuWhen(DateTime utc)
    {
        var t = utc.ToLocalTime();
        return t.Date == DateTime.Today ? t.ToString("HH:mm", CultureInfo.CurrentCulture) : t.ToString("ddd HH:mm", CultureInfo.CurrentCulture);
    }

    TextBlock AiuEmpty(string text) => new()
    {
        Text = text, FontFamily = ThemeText, FontSize = 11.5, Foreground = Frozen(AiuSoft), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 24, 40, 0),
    };

    /// <summary>
    /// One limit's window, drawn once: a hairline at 50 %, the limit at 100 % (dashed), what was used as a line with a
    /// light fill under it, the pace on to the reset as a dashed line (red where it passes the limit, a dot where it
    /// meets it), and the times under it: when the window began, now, when it resets.
    /// </summary>
    sealed class PaceChart : FrameworkElement
    {
        public DateTime Start, Reset, Now;
        public double Used;
        public double? Projected;
        public DateTime? LimitAt;
        public List<(DateTime At, double Used)> Points = new();
        public Color Ink, Soft, Line, Accent, Hot;
        public FontFamily Font = new("Segoe UI");

        static Pen P(Color c, double w, bool dash = false) { var p = new Pen(new SolidColorBrush(c), w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }; if (dash) p.DashStyle = new DashStyle(new[] { 3.0, 3.0 }, 0); p.Freeze(); return p; }
        static Color A(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 40 || h < 30) return;
            const double bottom = 14, right = 34;
            double ph = h - bottom, pw = w - right;
            double top = Math.Max(100, Math.Min(Projected ?? Used, 160)) * 1.06;
            double span = Math.Max(1, (Reset - Start).TotalSeconds);
            double X(DateTime t) => Math.Clamp((t - Start).TotalSeconds / span, 0, 1) * pw;
            double Y(double v) => ph - Math.Clamp(v, 0, top) / top * ph;
            double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            FormattedText Txt(string s, Color c, double size = 9.5) => new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(Font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), size, new SolidColorBrush(c), dip);

            // the scale: 50 % a hairline, the limit dashed and named
            dc.DrawLine(P(A(Ink, 0x12), 1), new Point(0, Y(50)), new Point(pw, Y(50)));
            dc.DrawLine(P(A(Ink, 0x40), 1, dash: true), new Point(0, Y(100)), new Point(pw, Y(100)));
            dc.DrawLine(P(A(Ink, 0x18), 1), new Point(0, ph), new Point(pw, ph));
            var lim = Txt("100%", Soft);
            dc.DrawText(lim, new Point(pw + 6, Y(100) - lim.Height / 2));
            var half = Txt("50%", A(Soft, 0x90));
            dc.DrawText(half, new Point(pw + 6, Y(50) - half.Height / 2));

            // what was used: from 0 when the window began, through every reading, to now
            var pts = new List<Point> { new(0, Y(0)) };
            foreach (var (at, u) in Points.OrderBy(p => p.At)) if (at > Start && at < Now) pts.Add(new Point(X(at), Y(u)));
            pts.Add(new Point(X(Now), Y(Used)));
            var area = new StreamGeometry();
            using (var g = area.Open())
            {
                g.BeginFigure(new Point(0, ph), true, true);
                foreach (var p in pts) g.LineTo(p, true, true);
                g.LineTo(new Point(X(Now), ph), true, true);
            }
            area.Freeze();
            dc.DrawGeometry(new SolidColorBrush(A(Accent, 0x1E)), null, area);
            var line = new StreamGeometry();
            using (var g = line.Open())
            {
                g.BeginFigure(pts[0], false, false);
                for (int i = 1; i < pts.Count; i++) g.LineTo(pts[i], true, true);
            }
            line.Freeze();
            dc.DrawGeometry(null, P(Accent, 1.6), line);

            // the pace on to the reset
            var nowPt = new Point(X(Now), Y(Used));
            if (Projected is { } proj)
            {
                if (LimitAt is { } la && la < Reset)
                {
                    var hit = new Point(X(la), Y(100));
                    dc.DrawLine(P(A(Hot, 0xC0), 1.4, dash: true), nowPt, hit);
                    dc.DrawLine(P(A(Hot, 0x50), 1.2, dash: true), hit, new Point(pw, Y(proj)));
                    dc.DrawEllipse(new SolidColorBrush(Hot), null, hit, 3, 3);
                }
                else dc.DrawLine(P(A(Ink, 0x90), 1.4, dash: true), nowPt, new Point(pw, Y(proj)));
            }
            // now: a dot on the line, a hairline down to the times
            dc.DrawLine(P(A(Ink, 0x22), 1), new Point(nowPt.X, nowPt.Y + 4), new Point(nowPt.X, ph));
            dc.DrawEllipse(new SolidColorBrush(Accent), new Pen(new SolidColorBrush(Color.FromArgb(0xFF, 0x0A, 0x0A, 0x0A)), 1.5), nowPt, 3.2, 3.2);

            // the times: when it began, now, when it resets
            string Fmt(DateTime t) { var l = t.ToLocalTime(); return (Reset - Start).TotalHours > 24 ? l.ToString("ddd", CultureInfo.CurrentCulture) : l.ToString("HH:mm", CultureInfo.CurrentCulture); }
            var s0 = Txt(Fmt(Start), Soft); dc.DrawText(s0, new Point(0, ph + 2));
            var s1 = Txt(Fmt(Reset), Soft); dc.DrawText(s1, new Point(pw - s1.Width, ph + 2));
            var sn = Txt(Loc.T("now"), Ink);
            double nx = Math.Clamp(nowPt.X - sn.Width / 2, s0.Width + 6, pw - s1.Width - sn.Width - 6);
            if (nx > s0.Width + 4 && nx + sn.Width < pw - s1.Width - 4) dc.DrawText(sn, new Point(nx, ph + 2));
        }
    }

    // ───────────── Cost ─────────────

    FrameworkElement AiuCostView()
    {
        if (!AiLimits.Shown("claude") || AiLimits.CostNow is not { } c) return AiuEmpty(Loc.T("No Claude Code use on this PC in the last 7 days"));
        static string Usd(double v) => v.ToString("$#,0.00", CultureInfo.InvariantCulture);
        var root = new Grid { Margin = new Thickness(2, 6, 0, 0) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(132) });
        root.ColumnDefinitions.Add(new ColumnDefinition());

        // left: today large, the week under it
        var left = new StackPanel();
        left.Children.Add(AiuText(Loc.T("Today"), 10.5, AiuSoft));
        left.Children.Add(AiuText(Usd(c.TodayUsd), 26, AiuInk, mono: true, weight: FontWeights.Light).Also(t => t.Margin = new Thickness(-1, -2, 0, 0)));
        left.Children.Add(AiuText(string.Format(Loc.T("{0} tokens"), AiuTokens(c.TodayTokens)), 10.5, AiuSoft, mono: true));
        left.Children.Add(AiuText(Loc.T("7 days"), 10.5, AiuSoft).Also(t => t.Margin = new Thickness(0, 14, 0, 0)));
        left.Children.Add(AiuText(Usd(c.WeekUsd), 15, AiuInk, mono: true, weight: FontWeights.Medium));
        left.Children.Add(AiuText(string.Format(Loc.T("{0} tokens"), AiuTokens(c.WeekTokens)), 10.5, AiuSoft, mono: true));
        left.ToolTip = Loc.T("What Claude Code used on this PC, from its own logs, at Anthropic's list prices");
        root.Children.Add(left);

        // right: the 7 days as bars, the week by model under them
        var right = new Grid();
        right.RowDefinitions.Add(new RowDefinition());
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var bars = new CostBars { Days = AiLimits.CostDays(), Ink = AiuInk, Soft = AiuSoft, Accent = AiuAcc, Font = ThemeText };
        right.Children.Add(bars);
        var models = AiLimits.CostModels();
        double total = models.Sum(m => m.Usd);
        if (total > 0)
        {
            var split = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            var stack = new Grid { Height = 4, ClipToBounds = true };
            byte[] steps = { 0xE6, 0x8C, 0x50, 0x30, 0x20 };
            for (int i = 0; i < models.Count; i++)
                stack.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(models[i].Usd / total, GridUnitType.Star) });
            for (int i = 0; i < models.Count; i++)
            {
                var seg = new Border { Background = Frozen(A(AiuInk, steps[Math.Min(i, steps.Length - 1)])), Margin = new Thickness(i == 0 ? 0 : 1, 0, 0, 0), CornerRadius = new CornerRadius(i == 0 ? 2 : 0, i == models.Count - 1 ? 2 : 0, i == models.Count - 1 ? 2 : 0, i == 0 ? 2 : 0) };
                Grid.SetColumn(seg, i);
                stack.Children.Add(seg);
            }
            split.Children.Add(stack);
            split.Children.Add(AiuText(string.Join("  ·  ", models.Take(4).Select(m => $"{m.Family} {m.Usd / total * 100:0}%")), 10, AiuSoft, mono: true).Also(t => t.Margin = new Thickness(0, 4, 0, 0)));
            Grid.SetRow(split, 1);
            right.Children.Add(split);
        }
        Grid.SetColumn(right, 1);
        root.Children.Add(right);
        return root;
    }

    /// <summary>Seven days of cost as bars: today in the accent, the others a quiet step; the day under each, the figure over the highest and over today.</summary>
    sealed class CostBars : FrameworkElement
    {
        public List<(DateTime Day, double Usd, long Tokens)> Days = new();
        public Color Ink, Soft, Accent;
        public FontFamily Font = new("Segoe UI");

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 40 || h < 30 || Days.Count == 0) return;
            double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            FormattedText Txt(string s, Color c, double size = 9.5) => new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(Font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), size, new SolidColorBrush(c), dip);
            const double label = 14, value = 14;
            double max = Math.Max(0.01, Days.Max(d => d.Usd)), slot = w / Days.Count, bw = Math.Min(22, slot * 0.6);
            int peak = Days.FindIndex(d => d.Usd == max);
            for (int i = 0; i < Days.Count; i++)
            {
                var (day, usd, _) = Days[i];
                bool today = i == Days.Count - 1;
                double bh = Math.Max(usd > 0 ? 2 : 1, usd / max * (h - label - value));
                double x = i * slot + (slot - bw) / 2, y = h - label - bh;
                var fill = new SolidColorBrush(today ? Accent : Color.FromArgb(usd > 0 ? (byte)0x38 : (byte)0x14, Ink.R, Ink.G, Ink.B));
                dc.DrawRoundedRectangle(fill, null, new Rect(x, y, bw, bh), Math.Min(3, bw / 2), Math.Min(3, bh / 2));
                var d = Txt(day.ToString("ddd", CultureInfo.CurrentCulture)[..Math.Min(2, day.ToString("ddd", CultureInfo.CurrentCulture).Length)], today ? Ink : Soft);
                dc.DrawText(d, new Point(x + bw / 2 - d.Width / 2, h - label + 2));
                if ((today || i == peak) && usd > 0)
                {
                    var v = Txt("$" + usd.ToString(usd >= 100 ? "#,0" : "0.0", CultureInfo.InvariantCulture), today ? Ink : Soft);
                    dc.DrawText(v, new Point(Math.Clamp(x + bw / 2 - v.Width / 2, 0, w - v.Width), y - v.Height - 1));
                }
            }
        }
    }
}
