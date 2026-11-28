using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// The AI usage page: the limits of the AI subscriptions signed in on this PC (Services/AiLimits), one card each.
/// Every limit is a row: its name and how much is used, a thin bar (orange from 70 %, red from 90 %), when it resets
/// and what's left, and where it's heading at this pace. Claude's card ends with what Claude Code used today and in the
/// last 7 days, in tokens and in dollars, read from its own logs. "Choose" picks which cards show.
/// It asks once a minute, and only while the page is on screen; the last answer shows at once when it opens.
/// </summary>
public partial class NotchWindow
{
    const int AiUsagePage = 12;

    Grid? _aiuPage;
    UniformGrid? _aiuCards;
    Grid? _aiuChoose;
    string? _aiuBuiltFor;
    readonly DispatcherTimer _aiuTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(60) };
    bool _aiuWired;

    Color AiuInk => Skin.Lit;
    Color AiuSoft => Skin.Label;
    Color AiuLine => Skin.Line;
    Color AiuAcc => _cyber ? CyCyan : Skin.Accent;
    Color AiuWarm => MinimalMapIf(_cyber ? CyYellow : Color.FromRgb(0xFF, 0x9F, 0x0A));
    Color AiuHot => MinimalMapIf(_cyber ? CyRed : Color.FromRgb(0xFF, 0x6B, 0x60));

    Grid BuildAiUsagePage()
    {
        _aiuPage = new Grid { Width = PageWidth, Height = 216 };
        RebuildAiUsagePage();
        return _aiuPage;
    }

    void OnAiUsageShown()
    {
        if (!_aiuWired)
        {
            _aiuWired = true;
            _aiuTimer.Tick += (_, _) =>
            {
                if (_page != AiUsagePage || _mode != Mode.Expanded) { _aiuTimer.Stop(); return; }
                AiuRefresh();
            };
        }
        if (_aiuBuiltFor != Skin.Key + "|" + _accent) RebuildAiUsagePage();
        else AiuRenderCards();
        _aiuTimer.Start();
        AiuRefresh();
    }

    /// <summary>Asks every shown provider (each at most every 5 min, the service decides) and reads the new log lines.</summary>
    void AiuRefresh()
    {
        foreach (var (id, _) in AiLimits.Providers)
        {
            if (!AiLimits.Shown(id)) continue;
            _ = AiLimits.ReadAsync(id).ContinueWith(_ => Dispatcher.BeginInvoke(AiuRenderCards), TaskScheduler.Default);
        }
        if (AiLimits.Shown("claude")) _ = AiLimits.CostAsync().ContinueWith(_ => Dispatcher.BeginInvoke(AiuRenderCards), TaskScheduler.Default);
    }

    void RebuildAiUsagePage()
    {
        if (_aiuPage == null) return;
        _aiuBuiltFor = Skin.Key + "|" + _accent;
        _aiuPage.Children.Clear();
        var root = new Grid { Margin = new Thickness(12, 0, 12, 8) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(22) });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions[0].Height = new GridLength(26);
        root.Children.Add(AiuViewSwitch());
        var head = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        head.Children.Add(AiuButton("", Loc.T("Ask again"), () => { AiuRefresh(); }));
        head.Children.Add(AiuButton("", Loc.T("Choose"), () => AiuShowChoose(_aiuChoose?.Visibility != Visibility.Visible)));
        root.Children.Add(head);
        _aiuCards = new UniformGrid { Rows = 1, Margin = new Thickness(0, 2, 0, 0) };
        Grid.SetRow(_aiuCards, 1);
        root.Children.Add(_aiuCards);
        _aiuAlt = new Grid { Visibility = Visibility.Collapsed };
        Grid.SetRow(_aiuAlt, 1);
        root.Children.Add(_aiuAlt);
        _aiuChoose = new Grid { Visibility = Visibility.Collapsed };
        Grid.SetRow(_aiuChoose, 1);
        root.Children.Add(_aiuChoose);
        _aiuPage.Children.Add(root);
        AiuRenderCards();
    }

    Border AiuButton(string glyph, string tip, Action click)
    {
        var g = new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 12, Foreground = Frozen(AiuSoft), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var b = new Border { Width = 26, Height = 22, CornerRadius = new CornerRadius(_cyber ? 0 : 6), Background = Brushes.Transparent, Child = g, Cursor = Cursors.Hand, ToolTip = tip };
        b.MouseEnter += (_, _) => { b.Background = Frozen(A(AiuInk, 0x12)); g.Foreground = Frozen(AiuInk); };
        b.MouseLeave += (_, _) => { b.Background = Brushes.Transparent; g.Foreground = Frozen(AiuSoft); };
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; FeelSound(Services.Sfx.Cue.Tap); click(); };
        return b;
    }

    TextBlock AiuText(string text, double size, Color c, bool mono = false, FontWeight? weight = null)
    {
        var t = new TextBlock
        {
            Text = text, FontSize = size, Foreground = Frozen(c), FontFamily = ThemeText, FontWeight = weight ?? FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
        };
        if (mono) System.Windows.Documents.Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular); // (figures keep their width)
        return t;
    }

    // ───────────── the cards ─────────────

    void AiuRenderCards()
    {
        if (_aiuCards == null || _aiuChoose?.Visibility == Visibility.Visible) return;
        if (AiuRenderAlt()) return; // (Pace or Cost: NotchWindow.AiUsageViews)
        _aiuCards.Children.Clear();
        var shown = AiLimits.Providers.Where(p => AiLimits.Shown(p.Id)).ToList();
        if (shown.Count == 0)
        {
            _aiuCards.Columns = 1;
            _aiuCards.Children.Add(new TextBlock
            {
                Text = Loc.T("Pixo reads the Claude Code and Codex sign-ins on this PC, and nothing else. None is here yet: sign in to one of them, or pick one in Choose."),
                FontFamily = ThemeText, FontSize = 11.5, Foreground = Frozen(AiuSoft), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 24, 40, 0),
            });
            return;
        }
        _aiuCards.Columns = shown.Count;
        for (int i = 0; i < shown.Count; i++)
        {
            var card = AiuCard(shown[i].Id, shown[i].Name);
            card.Margin = new Thickness(i == 0 ? 0 : 4, 0, i == shown.Count - 1 ? 0 : 4, 0);
            _aiuCards.Children.Add(card);
        }
    }

    Border AiuCard(string id, string name)
    {
        var state = AiLimits.Cached(id);
        var r = state.Last;
        var now = DateTime.UtcNow;
        bool stale = r == null || (now - r.ReadAt).TotalMinutes > 15 || state.Error != null;
        var body = new DockPanel { LastChildFill = false };

        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(AiuText(Cap(name), _cyber ? 9.5 : 12, AiuInk, weight: FontWeights.Medium));
        if (r?.Plan is { } plan) title.Children.Add(AiuText(Cap(plan), _cyber ? 8.5 : 10.5, AiuSoft).Also(t => t.Margin = new Thickness(7, 1, 0, 0)));
        var titleRow = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        titleRow.Children.Add(title);
        if (id == "claude" && r != null && AiLimits.CostNow is { } spent)
        {
            // the limits fill the card: today's cost waits on the title row, the week in its tooltip
            var today = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            today.Children.Add(AiuText(spent.TodayUsd.ToString("$#,0.00", System.Globalization.CultureInfo.InvariantCulture), 12, AiuInk, mono: true, weight: FontWeights.Medium));
            today.Children.Add(AiuText(Loc.T("today"), 10.5, AiuSoft).Also(t => t.Margin = new Thickness(5, 1, 0, 0)));
            today.ToolTip = string.Format(Loc.T("{0} tokens"), AiuTokens(spent.TodayTokens)) + "\n"
                + string.Format(Loc.T("{0} in 7 days · {1} tokens"), spent.WeekUsd.ToString("$#,0.00", System.Globalization.CultureInfo.InvariantCulture), AiuTokens(spent.WeekTokens));
            titleRow.Children.Add(today);
        }
        DockPanel.SetDock(titleRow, Dock.Top);
        body.Children.Add(titleRow);

        if (r != null)
        {
            foreach (var w in r.Windows.Take(id == "claude" ? 2 : 3))
            {
                var row = AiuWindow(w, r.ReadAt, now, stale);
                DockPanel.SetDock(row, Dock.Top);
                body.Children.Add(row);
            }
        }
        // what went wrong, or how old the numbers are (they stay, dimmed, until a new answer comes)
        string? note = state.Error != null && r == null ? Loc.T(state.Error)
            : r != null && stale ? string.Format(Loc.T("Updated {0} ago"), AiuAgo(now - r.ReadAt)) + (state.Error != null ? " · " + Loc.T(state.Error) : "")
            : null;
        if (note != null)
        {
            var t = AiuText(note, 10.5, AiuSoft); // (a note, not an alarm: quiet like the rest)
            t.TextWrapping = TextWrapping.Wrap;
            t.Margin = new Thickness(0, 2, 0, 0);
            DockPanel.SetDock(t, Dock.Top);
            body.Children.Add(t);
        }

        if (id == "claude" && r == null && AiLimits.CostNow is { } c)
        {
            // what it cost: today's figure is the card's one big number (bigger still when there are no limits to show),
            // the week under it in a quiet line
            static string Usd(double v) => v.ToString("$#,0.00", System.Globalization.CultureInfo.InvariantCulture);
            bool hero = r == null;
            var cost = new StackPanel { Margin = new Thickness(0, hero ? 12 : 6, 0, 0), ToolTip = Loc.T("What Claude Code used on this PC, from its own logs, at Anthropic's list prices") };
            var today = new StackPanel { Orientation = Orientation.Horizontal };
            today.Children.Add(AiuText(Usd(c.TodayUsd), hero ? 24 : 15, AiuInk, mono: true, weight: hero ? FontWeights.Light : FontWeights.Medium));
            today.Children.Add(AiuText(Loc.T("today"), 10.5, AiuSoft).Also(t => { t.VerticalAlignment = VerticalAlignment.Bottom; t.Margin = new Thickness(6, 0, 0, hero ? 5 : 2); }));
            cost.Children.Add(today);
            cost.Children.Add(AiuText(string.Format(Loc.T("{0} tokens"), AiuTokens(c.TodayTokens)), 10.5, AiuSoft, mono: true));
            cost.Children.Add(AiuText(string.Format(Loc.T("{0} in 7 days · {1} tokens"), Usd(c.WeekUsd), AiuTokens(c.WeekTokens)), 10.5, AiuSoft, mono: true).Also(t => t.Margin = new Thickness(0, hero ? 8 : 4, 0, 0)));
            DockPanel.SetDock(cost, hero ? Dock.Top : Dock.Bottom);
            body.Children.Add(cost);
        }

        var card = new Border
        {
            Child = body, Padding = new Thickness(12, 9, 12, 9), CornerRadius = new CornerRadius(_cyber ? 0 : 12), BorderThickness = new Thickness(1),
            Background = Frozen(_cyber ? Color.FromArgb(0xA6, 0x02, 0x08, 0x0C) : A(AiuInk, 0x08)), BorderBrush = Frozen(_cyber ? A(CyCyan, 0x50) : AiuLine),
        };
        if (_cyber) NeonCorner(card, 7, 0);
        return card;
    }

    FrameworkElement AiuWindow(AiLimits.Window w, DateTime readAt, DateTime now, bool stale)
    {
        var st = new StackPanel { Margin = new Thickness(0, 0, 0, 5), Opacity = stale ? 0.55 : 1 };
        var top = new Grid();
        top.Children.Add(AiuText(Loc.T(w.Label), 11, AiuInk));
        var pct = AiuText($"{w.Used:0}%", 11, w.Used >= 90 ? AiuHot : w.Used >= 70 ? AiuWarm : AiuInk, mono: true, weight: FontWeights.Medium);
        pct.HorizontalAlignment = HorizontalAlignment.Right;
        top.Children.Add(pct);
        st.Children.Add(top);
        // the bar: accent, orange from 70 %, red from 90 %; faint behind it, where it will be at reset at this pace
        // (red to the end when that pace reaches the limit first)
        double f = Math.Clamp(w.Used / 100, 0, 1);
        var fill = new Rectangle { RadiusX = 1.5, RadiusY = 1.5, HorizontalAlignment = HorizontalAlignment.Stretch, RenderTransform = new ScaleTransform(f, 1), Fill = Frozen(w.Used >= 90 ? AiuHot : w.Used >= 70 ? AiuWarm : AiuAcc) };
        var bar = new Grid { Height = 3, Margin = new Thickness(0, 3, 0, 2), Children = { new Rectangle { RadiusX = 1.5, RadiusY = 1.5, Fill = Frozen(A(AiuInk, 0x1C)) } } };
        var (ahead, runsOut) = AiLimits.Pace(w, readAt, now);
        if (AiLimits.Projection && (ahead is > 0 || runsOut != null))
            bar.Children.Add(new Rectangle
            {
                RadiusX = 1.5, RadiusY = 1.5, HorizontalAlignment = HorizontalAlignment.Stretch,
                RenderTransform = new ScaleTransform(runsOut != null ? 1 : Math.Clamp(ahead!.Value / 100, f, 1), 1),
                Fill = Frozen(runsOut != null ? A(AiuHot, 0x60) : A(AiuInk, 0x40)),
            });
        bar.Children.Add(fill);
        st.Children.Add(bar);
        var parts = new List<string>();
        if (w.ResetsAt is { } reset && reset > now) parts.Add(string.Format(Loc.T("in {0}"), AiuIn(reset - now)));
        parts.Add(string.Format(Loc.T("{0:0}% left"), Math.Max(0, 100 - w.Used)));
        st.Children.Add(AiuText(string.Join(" · ", parts), 10, AiuSoft));
        var (atReset, limitAt) = AiLimits.Pace(w, readAt, now);
        if (atReset is { } p) st.Children.Add(AiuText(string.Format(Loc.T("On pace for {0:0}% at reset"), p), 10, A(AiuSoft, 0xB0)));
        else if (limitAt is { } at) st.Children.Add(AiuText(string.Format(Loc.T("Limit around {0}"), at.ToLocalTime().ToString(at.ToLocalTime().Date == DateTime.Today ? "HH:mm" : "ddd HH:mm")), 10, AiuWarm));
        return st;
    }

    static string AiuTokens(long n) => n switch
    {
        >= 1_000_000_000 => $"{n / 1e9:0.0}B",
        >= 1_000_000 => $"{n / 1e6:0.0}M",
        >= 1_000 => $"{n / 1e3:0.0}K",
        _ => n.ToString(),
    };

    static string AiuIn(TimeSpan t) => t.TotalDays >= 1 ? $"{(int)t.TotalDays} d {t.Hours} h" : t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : $"{Math.Max(1, t.Minutes)} min";
    static string AiuAgo(TimeSpan t) => t.TotalDays >= 1 ? $"{(int)t.TotalDays} d" : t.TotalHours >= 1 ? $"{(int)t.TotalHours} h" : $"{Math.Max(1, (int)t.TotalMinutes)} min";

    // ───────────── choose ─────────────

    void AiuShowChoose(bool on)
    {
        if (_aiuChoose == null || _aiuCards == null) return;
        _aiuChoose.Children.Clear();
        _aiuChoose.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        _aiuCards.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        if (_aiuAlt != null && on) _aiuAlt.Visibility = Visibility.Collapsed;
        if (!on) { AiuRenderCards(); AiuRefresh(); return; }
        var list = new StackPanel { Width = 260, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(4, 6, 0, 0) };
        list.Children.Add(AiuText(Loc.T("Show the limits of"), 11, AiuSoft).Also(t => t.Margin = new Thickness(0, 0, 0, 6)));
        foreach (var (id, name) in AiLimits.Providers)
        {
            bool shown = AiLimits.Shown(id), found = AiLimits.Found(id);
            var check = new TextBlock { Text = shown ? "" : "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 11, Foreground = Frozen(AiuAcc), Width = 20, VerticalAlignment = VerticalAlignment.Center };
            var row = new Grid { Height = 28, Background = Brushes.Transparent, Cursor = Cursors.Hand };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(check);
            var label = AiuText(name, 11.5, AiuInk);
            var sub = AiuText(found ? Loc.T("signed in on this PC") : Loc.T("not found on this PC"), 10, AiuSoft);
            var texts = new StackPanel { Orientation = Orientation.Horizontal, Children = { label, sub.Also(s => s.Margin = new Thickness(8, 1, 0, 0)) } };
            Grid.SetColumn(texts, 1);
            row.Children.Add(texts);
            row.MouseEnter += (_, _) => row.Background = Frozen(A(AiuInk, 0x0C));
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                FeelSound(Services.Sfx.Cue.Tap);
                AiLimits.SetShown(id, !AiLimits.Shown(id));
                check.Text = AiLimits.Shown(id) ? "" : "";
            };
            list.Children.Add(row);
        }
        // the option: the bars and the chart show where each limit is heading
        list.Children.Add(AiuText(Loc.T("Options"), 11, AiuSoft).Also(t => t.Margin = new Thickness(0, 10, 0, 4)));
        var projCheck = new TextBlock { Text = AiLimits.Projection ? "\uE73E" : "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 11, Foreground = Frozen(AiuAcc), Width = 20, VerticalAlignment = VerticalAlignment.Center };
        var projRow = new Grid { Height = 28, Background = Brushes.Transparent, Cursor = Cursors.Hand };
        projRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        projRow.ColumnDefinitions.Add(new ColumnDefinition());
        projRow.Children.Add(projCheck);
        var projText = AiuText(Loc.T("Show where each limit is heading at this pace"), 11.5, AiuInk);
        Grid.SetColumn(projText, 1);
        projRow.Children.Add(projText);
        projRow.MouseEnter += (_, _) => projRow.Background = Frozen(A(AiuInk, 0x0C));
        projRow.MouseLeave += (_, _) => projRow.Background = Brushes.Transparent;
        projRow.MouseLeftButtonUp += (_, e) => { e.Handled = true; FeelSound(Services.Sfx.Cue.Tap); AiLimits.SetProjection(!AiLimits.Projection); projCheck.Text = AiLimits.Projection ? "\uE73E" : ""; };
        list.Children.Add(projRow);
        var done = new Border
        {
            Child = AiuText(Loc.T("Done"), 11, AiuInk, weight: FontWeights.Medium), Padding = new Thickness(12, 4, 12, 5), Margin = new Thickness(0, 8, 0, 0),
            CornerRadius = new CornerRadius(_cyber ? 0 : 8), Background = Frozen(A(AiuInk, 0x14)), HorizontalAlignment = HorizontalAlignment.Left, Cursor = Cursors.Hand,
        };
        done.MouseLeftButtonUp += (_, e) => { e.Handled = true; AiuShowChoose(false); };
        list.Children.Add(done);
        _aiuChoose.Children.Add(list);
    }
}
