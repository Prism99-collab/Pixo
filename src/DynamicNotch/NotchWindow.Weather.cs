using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using DynamicNotch.Services;
using Wx = DynamicNotch.Services.WeatherService;

namespace DynamicNotch;

/// <summary>
/// The Weather page: now on the left (the place, the temperature, the sky in a word, how it feels, the wind, the
/// humidity, today's high and low), the next 24 hours in a strip on the right, and the week under it, each day's low
/// to high drawn on one shared scale, coloured from cold to warm, with a dot where it is right now (Services/WeatherService).
/// The place is typed once. The forecast is asked for only when the page opens and the last one is older than
/// 15 minutes, never on the notch's own thread, and the last one is kept to show at once, offline too.
/// </summary>
public partial class NotchWindow
{
    const int WeatherPage = 11;

    Grid? _wxPage;
    Wx.State? _wx;
    string? _wxBuiltFor;
    bool _wxBusy, _wxSearching, _wxFailed;
    List<Wx.Place>? _wxFound;
    string _wxFindNote = "";
    TextBox? _wxBox;
    CancellationTokenSource? _wxAsk;

    Color WxInk => Skin.Lit;
    Color WxSoft => Skin.Label;
    Color WxLine => Skin.Line;
    Color WxTint(Color c) => MinimalOn ? MinimalMap(c) : c;

    Grid BuildWeatherPage()
    {
        _wxPage = new Grid { Width = PageWidth, Height = 216 };
        return _wxPage; // (drawn the first time it's shown: no file read while Pixo starts)
    }

    async void OnWeatherShown()
    {
        if (_wx == null)
        {
            _wx = await Task.Run(Wx.Load);
            WxRender();
        }
        else if (_wxBuiltFor != Skin.Key + "|" + _accent) WxRender();
        if (_wx.Place != null && (_wx.Last == null || (DateTime.UtcNow - _wx.Last.FetchedUtc).TotalMinutes > 15)) WxFetch();
    }

    async void WxFetch()
    {
        if (_wx?.Place is not { } place || _wxBusy) return;
        _wxBusy = true;
        _wxFailed = false;
        try
        {
            var f = await Task.Run(() => Wx.FetchAsync(place, CancellationToken.None));
            if (_wx.Place != place) { _wxBusy = false; WxFetch(); return; } // (the place changed meanwhile: fetch the new one)
            _wx.Last = f;
            var keep = _wx;
            _ = Task.Run(() => Wx.Save(keep));
        }
        catch (Exception ex) { _wxFailed = true; AppLog.Write("weather: " + ex.GetBaseException().Message); }
        finally { _wxBusy = false; }
        WxRender();
    }

    // ───────────── drawing ─────────────

    void WxRender()
    {
        if (_wxPage == null || _wx == null) return;
        _wxBuiltFor = Skin.Key + "|" + _accent;
        _wxPage.Children.Clear();
        if (_wx.Place == null || _wxFound != null || _wxSearching) { _wxPage.Children.Add(WxFindView()); return; }
        if (_wx.Last is not { } f)
        {
            var msg = WxText(_wxFailed ? Loc.T("No connection · try again") : Loc.T("Getting the forecast…"), 12, WxSoft);
            msg.HorizontalAlignment = HorizontalAlignment.Center;
            msg.VerticalAlignment = VerticalAlignment.Center;
            if (_wxFailed) { msg.Cursor = Cursors.Hand; msg.MouseLeftButtonUp += (_, _) => WxFetch(); }
            _wxPage.Children.Add(msg);
            return;
        }
        bool fh = Wx.UsesFahrenheit(_wx);
        string Deg(double c) => $"{Math.Round(fh ? Wx.ToF(c) : c):0}°";

        var root = new Grid { Margin = new Thickness(16, 4, 16, 10) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(148) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        root.ColumnDefinitions.Add(new ColumnDefinition());

        // now
        var left = new Grid();
        left.RowDefinitions.Add(new RowDefinition());
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var now = new StackPanel();
        var place = WxText(_wx.Place.Name, 11, WxSoft);
        place.TextTrimming = TextTrimming.CharacterEllipsis;
        place.Cursor = Cursors.Hand;
        place.ToolTip = Loc.T("Change the place");
        place.MouseLeftButtonUp += (_, e) => { e.Handled = true; _wxFound = new(); _wxFindNote = ""; WxRender(); };
        now.Children.Add(place);
        var temp = WxText(Deg(f.Current.Temp), 40, WxInk);
        temp.FontWeight = FontWeights.Light;
        temp.Margin = new Thickness(-2, -2, 0, -4);
        System.Windows.Documents.Typography.SetNumeralAlignment(temp, FontNumeralAlignment.Tabular);
        now.Children.Add(temp);
        now.Children.Add(WxText(Loc.T(Wx.Describe(f.Current.Code).Label), 12.5, WxInk).Also(t => t.FontWeight = FontWeights.Medium));
        string wind = fh ? $"{Math.Round(f.Current.Wind * 0.621371):0} mph" : $"{Math.Round(f.Current.Wind):0} km/h";
        var detail = WxText(string.Format(Loc.T("Feels {0} · wind {1} · humidity {2}%"), Deg(f.Current.Feels), wind, f.Current.Humidity), 10.5, WxSoft);
        detail.TextWrapping = TextWrapping.Wrap;
        detail.Margin = new Thickness(0, 3, 0, 0);
        now.Children.Add(detail);
        if (f.Days.Count > 0)
            now.Children.Add(WxText(string.Format(Loc.T("High {0} · low {1}"), Deg(f.Days[0].Max), Deg(f.Days[0].Min)), 10.5, WxInk).Also(t => t.Margin = new Thickness(0, 3, 0, 0)));
        left.Children.Add(now);

        // when it was asked for (a click asks again), and °C / °F
        var foot = new StackPanel { Orientation = Orientation.Horizontal };
        var when = WxText(_wxBusy ? Loc.T("Updating…") : string.Format(Loc.T("Updated {0}"), f.FetchedUtc.ToLocalTime().ToString("t", WxCulture)), 10, A(WxSoft, 0xB0));
        when.Cursor = Cursors.Hand;
        when.ToolTip = Loc.T("Refresh");
        when.VerticalAlignment = VerticalAlignment.Center;
        when.MouseLeftButtonUp += (_, e) => { e.Handled = true; WxFetch(); WxRender(); };
        foot.Children.Add(when);
        var unit = new Border
        {
            Child = WxText(fh ? "°F" : "°C", 10, WxInk), Padding = new Thickness(6, 1, 6, 2), Margin = new Thickness(8, 0, 0, 0), CornerRadius = new CornerRadius(_cyber ? 0 : 6),
            BorderBrush = Frozen(WxLine), BorderThickness = new Thickness(1), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = Loc.T("Celsius or Fahrenheit"),
        };
        unit.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            FeelSound(Services.Sfx.Cue.Tap);
            _wx.Fahrenheit = !fh;
            var keep = _wx;
            _ = Task.Run(() => Wx.Save(keep));
            WxRender();
        };
        foot.Children.Add(unit);
        Grid.SetRow(foot, 1);
        left.Children.Add(foot);
        root.Children.Add(left);

        // the next 24 hours, every 3 hours
        var right = new Grid();
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition());
        var hours = new UniformGrid { Rows = 1, Columns = 8, Margin = new Thickness(0, 2, 0, 0) };
        for (int i = 0; i < f.Hours.Count && hours.Children.Count < 8; i += 3)
        {
            var h = f.Hours[i];
            var cell = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            cell.Children.Add(WxText(i == 0 ? Loc.T("Now") : WxHour(h.At), 10, WxSoft).Also(t => t.HorizontalAlignment = HorizontalAlignment.Center));
            cell.Children.Add(WxIcon(Wx.Describe(h.Code).Kind, h.IsDay, 16).Also(v => v.Margin = new Thickness(0, 3, 0, 2)));
            cell.Children.Add(WxText(Deg(h.Temp), 11.5, WxInk).Also(t => t.HorizontalAlignment = HorizontalAlignment.Center));
            var rain = WxText($"{h.Rain}%", 9.5, WxTint(WxRainC));
            rain.HorizontalAlignment = HorizontalAlignment.Center;
            rain.Visibility = h.Rain > 20 ? Visibility.Visible : Visibility.Hidden; // (the room stays: every cell the same height)
            cell.Children.Add(rain);
            hours.Children.Add(cell);
        }
        right.Children.Add(hours);

        // the week: each day's low to high on one shared scale
        var week = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        if (f.Days.Count > 0)
        {
            double lo = f.Days.Min(d => d.Min), hi = Math.Max(f.Days.Max(d => d.Max), lo + 1);
            for (int i = 0; i < f.Days.Count; i++) week.Children.Add(WxDayRow(f.Days[i], i, lo, hi, i == 0 ? f.Current.Temp : null, Deg));
        }
        Grid.SetRow(week, 1);
        right.Children.Add(week);
        Grid.SetColumn(right, 2);
        root.Children.Add(right);
        _wxPage.Children.Add(root);
    }

    FrameworkElement WxDayRow(Wx.Day d, int index, double lo, double hi, double? now, Func<double, string> deg)
    {
        var row = new Grid { Height = 17 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        var name = WxText(index == 0 ? Loc.T("Today") : Cap(d.Date.ToString("ddd", WxCulture)), 10.5, WxInk);
        name.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(name);
        var icon = WxIcon(Wx.Describe(d.Code).Kind, true, 13);
        icon.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetColumn(icon, 1);
        row.Children.Add(icon);
        var min = WxText(deg(d.Min), 10.5, WxSoft).Also(t => { t.HorizontalAlignment = HorizontalAlignment.Right; t.VerticalAlignment = VerticalAlignment.Center; });
        Grid.SetColumn(min, 2);
        row.Children.Add(min);
        var max = WxText(deg(d.Max), 10.5, WxInk).Also(t => { t.HorizontalAlignment = HorizontalAlignment.Right; t.VerticalAlignment = VerticalAlignment.Center; });
        Grid.SetColumn(max, 4);
        row.Children.Add(max);

        // the bar: the week's span is the track, the day's low to high is lit, warm or cold by its own temperatures
        var bar = new Grid { Height = 4, Margin = new Thickness(8, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        bar.Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = Frozen(A(WxInk, 0x16)) });
        var span = new Grid();
        span.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0, d.Min - lo), GridUnitType.Star) });
        span.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.4, d.Max - d.Min), GridUnitType.Star) });
        span.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0, hi - d.Max), GridUnitType.Star) });
        var lit = new Border { CornerRadius = new CornerRadius(2), Background = Frozen(new LinearGradientBrush(WxTint(TempColor(d.Min)), WxTint(TempColor(d.Max)), 0)) };
        Grid.SetColumn(lit, 1);
        span.Children.Add(lit);
        bar.Children.Add(span);
        if (now is { } t)
        {
            // where it is right now, on today's bar
            double at = Math.Clamp(t, lo, hi);
            var dot = new Grid { Margin = new Thickness(0, -2, 0, -2) };
            dot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0, at - lo), GridUnitType.Star) });
            dot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            dot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0, hi - at), GridUnitType.Star) });
            var e = new Ellipse { Width = 8, Height = 8, Fill = Frozen(WxInk), Stroke = Frozen(Color.FromArgb(0xE0, 0x10, 0x10, 0x12)), StrokeThickness = 1.5, Margin = new Thickness(-4, 0, 4, 0) };
            Grid.SetColumn(e, 1);
            dot.Children.Add(e);
            bar.Children.Add(dot);
        }
        Grid.SetColumn(bar, 3);
        row.Children.Add(bar);
        return row;
    }

    // ───────────── finding the place ─────────────

    FrameworkElement WxFindView()
    {
        var st = new StackPanel { Margin = new Thickness(16, 10, 16, 0) };
        st.Children.Add(WxText(Loc.T("Weather for a place"), 13, WxInk).Also(t => t.FontWeight = FontWeights.Medium));
        st.Children.Add(WxText(Loc.T("Type a city: Pixo keeps it, and asks for its forecast only while this page is open"), 11, WxSoft).Also(t => { t.TextWrapping = TextWrapping.Wrap; t.Margin = new Thickness(0, 3, 0, 10); }));
        _wxBox = new TextBox
        {
            FontFamily = ThemeText, FontSize = 12, Foreground = Frozen(WxInk), CaretBrush = Frozen(Skin.Accent), SelectionBrush = Frozen(Skin.Accent),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(10, 0, 10, 0), VerticalContentAlignment = VerticalAlignment.Center, FocusVisualStyle = null,
        };
        var ghost = WxText(Loc.T("Type a city"), 12, A(WxSoft, 0x70));
        ghost.IsHitTestVisible = false;
        ghost.VerticalAlignment = VerticalAlignment.Center;
        ghost.Margin = new Thickness(12, 0, 0, 1);
        _wxBox.TextChanged += (_, _) => ghost.Visibility = _wxBox.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        _wxBox.PreviewMouseLeftButtonDown += (_, _) => { _maybeSwipe = false; BeginWorkTyping(_wxBox); };
        var box = _wxBox;
        _wxBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; WxSearch(box.Text); }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                EndWorkTyping(giveBack: true);
                if (_wx?.Place != null) { _wxFound = null; WxRender(); }
            }
        };
        var frame = new Border
        {
            Height = 32, Child = new Grid { Children = { _wxBox, ghost } }, CornerRadius = new CornerRadius(_cyber ? 0 : 9), BorderThickness = new Thickness(1),
            Background = Frozen(_cyber ? Color.FromArgb(0x90, 0x00, 0x00, 0x00) : A(WxInk, 0x0C)), BorderBrush = Frozen(_cyber ? A(CyCyan, 0x60) : WxLine), Cursor = Cursors.IBeam,
        };
        frame.MouseLeftButtonDown += (_, e) => { _maybeSwipe = false; BeginWorkTyping(box); e.Handled = true; };
        if (_cyber) NeonCorner(frame, 6, 0);
        st.Children.Add(frame);

        var list = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        if (_wxSearching) list.Children.Add(WxText(Loc.T("Looking…"), 11, WxSoft).Also(t => t.Margin = new Thickness(2, 4, 0, 0)));
        else if (_wxFindNote.Length > 0) list.Children.Add(WxText(_wxFindNote, 11, WxSoft).Also(t => t.Margin = new Thickness(2, 4, 0, 0)));
        foreach (var p in _wxFound ?? new List<Wx.Place>())
        {
            var where = string.Join(", ", new[] { p.Region, p.Country }.Where(s => !string.IsNullOrWhiteSpace(s)));
            var line = new TextBlock { FontFamily = ThemeText, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis };
            line.Inlines.Add(new System.Windows.Documents.Run(p.Name) { Foreground = Frozen(WxInk) });
            if (where.Length > 0) line.Inlines.Add(new System.Windows.Documents.Run("  " + where) { Foreground = Frozen(WxSoft) });
            var row = new Border { Child = line, Padding = new Thickness(10, 4, 10, 5), CornerRadius = new CornerRadius(_cyber ? 0 : 7), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            row.MouseEnter += (_, _) => row.Background = Frozen(A(WxInk, 0x12));
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            var pick = p;
            row.MouseLeftButtonUp += (_, e) => { e.Handled = true; WxChoose(pick); };
            list.Children.Add(row);
        }
        st.Children.Add(list);
        return st;
    }

    async void WxSearch(string text)
    {
        if (text.Trim().Length < 2) return;
        _wxAsk?.Cancel();
        var cts = _wxAsk = new CancellationTokenSource();
        _wxSearching = true;
        _wxFindNote = "";
        string keep = text;
        WxRender();
        WxRefocus(keep);
        try
        {
            _wxFound = await Task.Run(() => Wx.SearchAsync(text, cts.Token));
            if (_wxFound.Count == 0) _wxFindNote = Loc.T("No place found by that name");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { return; }
        catch (Exception ex) { _wxFound = new(); _wxFindNote = Loc.T("No connection · try again"); AppLog.Write("weather: search: " + ex.GetBaseException().Message); }
        finally { if (_wxAsk == cts) _wxSearching = false; }
        WxRender();
        WxRefocus(keep);
    }

    /// <summary>The list was drawn again: the box comes back with what you typed, still taking the keys.</summary>
    void WxRefocus(string text)
    {
        if (_wxBox == null) return;
        _wxBox.Text = text;
        _wxBox.CaretIndex = text.Length;
        if (_workTyping) BeginWorkTyping(_wxBox);
    }

    void WxChoose(Wx.Place p)
    {
        if (_wx == null) return;
        EndWorkTyping(giveBack: true);
        FeelSound(Services.Sfx.Cue.Tap);
        _wx.Place = p;
        _wx.Last = null;
        _wxFound = null;
        var keep = _wx;
        _ = Task.Run(() => Wx.Save(keep));
        WxRender();
        WxFetch();
    }

    // ───────────── small pieces ─────────────

    static readonly Color WxRainC = Color.FromRgb(0x6C, 0xB4, 0xFF), WxSunC = Color.FromRgb(0xF5, 0xC4, 0x51);

    static CultureInfo WxCulture => Loc.Lang == "en" ? CultureInfo.CurrentCulture : CultureInfo.GetCultureInfo(Loc.Lang);

    static string WxHour(DateTime t) => CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains('t') ? t.ToString("h tt", CultureInfo.CurrentCulture) : t.ToString("HH", CultureInfo.CurrentCulture);

    TextBlock WxText(string text, double size, Color c) => new() { Text = text, FontFamily = ThemeText, FontSize = size, Foreground = Frozen(c) };

    /// <summary>Cold to warm: ice blue under 0°, teal at 10°, gold at 20°, orange from 30° (°C, whatever is shown).</summary>
    internal static Color TempColor(double c)
    {
        (double T, Color C)[] stops = { (0, Color.FromRgb(0x7F, 0xB3, 0xFF)), (10, Color.FromRgb(0x5F, 0xD4, 0xC4)), (20, Color.FromRgb(0xF5, 0xC4, 0x51)), (30, Color.FromRgb(0xFF, 0x8A, 0x50)) };
        if (c <= stops[0].T) return stops[0].C;
        for (int i = 1; i < stops.Length; i++)
            if (c <= stops[i].T)
            {
                double k = (c - stops[i - 1].T) / (stops[i].T - stops[i - 1].T);
                Color a = stops[i - 1].C, b = stops[i].C;
                return Color.FromRgb((byte)(a.R + (b.R - a.R) * k), (byte)(a.G + (b.G - a.G) * k), (byte)(a.B + (b.B - a.B) * k));
            }
        return stops[^1].C;
    }

    /// <summary>The sky as a small line drawing (one stroke for all): sun, moon, cloud, rain, snow, storm, fog.</summary>
    FrameworkElement WxIcon(Wx.Kind kind, bool day, double size)
    {
        var c = new Canvas { Width = 24, Height = 24 };
        Color ink = A(WxInk, 0xD8), sun = WxTint(WxSunC), rain = WxTint(WxRainC);
        void P(string data, Color stroke, Color? fill = null) => c.Children.Add(new Path
        {
            Data = Geometry.Parse(data), Stroke = Frozen(stroke), StrokeThickness = 1.7, Fill = fill is { } f ? Frozen(f) : null,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
        });
        const string Cloud = "M7,18 H17 A4,4 0 0 0 17.3,10 A5.5,5.5 0 0 0 6.6,11.2 A3.4,3.4 0 0 0 7,18 Z";
        const string CloudHigh = "M7,14.5 H17 A4,4 0 0 0 17.3,6.5 A5.5,5.5 0 0 0 6.6,7.7 A3.4,3.4 0 0 0 7,14.5 Z";
        void SunAt(double x, double y, double r, double ray)
        {
            P(FormattableString.Invariant($"M{x - r},{y} A{r},{r} 0 1 0 {x + r},{y} A{r},{r} 0 1 0 {x - r},{y} Z"), sun);
            for (int i = 0; i < 8; i++)
            {
                double a = i * Math.PI / 4, c1 = Math.Cos(a), s1 = Math.Sin(a);
                P(FormattableString.Invariant($"M{x + c1 * (r + 2.2):0.##},{y + s1 * (r + 2.2):0.##} L{x + c1 * (r + 2.2 + ray):0.##},{y + s1 * (r + 2.2 + ray):0.##}"), sun);
            }
        }
        const string Moon = "M15.5,4.5 A8,8 0 1 0 19.5,15.5 A6.5,6.5 0 1 1 15.5,4.5 Z";
        switch (kind)
        {
            case Wx.Kind.Clear:
                if (day) SunAt(12, 12, 4, 2.3); else P(Moon, ink);
                break;
            case Wx.Kind.PartlyCloudy:
                if (day) SunAt(9, 8.5, 3, 1.6); else P("M10.5,3 A5.5,5.5 0 1 0 15,10.5 A4.4,4.4 0 1 1 10.5,3 Z", ink);
                P("M9,20 H18 A3.4,3.4 0 0 0 18.2,13.2 A4.6,4.6 0 0 0 9.3,14.2 A2.9,2.9 0 0 0 9,20 Z", ink, Color.FromArgb(0xF0, 0x14, 0x14, 0x16));
                break;
            case Wx.Kind.Cloudy:
                P(Cloud, ink);
                break;
            case Wx.Kind.Fog:
                P("M4,9 H20 M6,13 H18 M4,17 H20", ink);
                break;
            case Wx.Kind.Rain:
                P(CloudHigh, ink);
                P("M8.5,17.5 L7.5,20.5 M12.5,17.5 L11.5,20.5 M16.5,17.5 L15.5,20.5", rain);
                break;
            case Wx.Kind.Snow:
                P(CloudHigh, ink);
                foreach (var (x, y) in new[] { (8.5, 18.5), (12.5, 20.5), (16.5, 18.5) })
                    c.Children.Add(new Ellipse { Width = 2.2, Height = 2.2, Fill = Frozen(ink), Margin = new Thickness(x - 1.1, y - 1.1, 0, 0) });
                break;
            case Wx.Kind.Storm:
                P(CloudHigh, ink);
                P("M12.5,15.5 L10.5,19 H13.5 L11.5,22.5", sun);
                break;
        }
        return new Viewbox { Width = size, Height = size, Child = c, HorizontalAlignment = HorizontalAlignment.Center };
    }
}
