using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// The Converter page: Units (length, weight, temperature, volume, speed, area, data, time) or Currency (the ECB's
/// daily rates, Services/Fx). Two cards side by side with a swap between them; type in either and the other follows.
/// A click on a card's unit opens the list of units (or currencies) right on the page. The line at the bottom says
/// what one unit is worth in the other. The last choices are kept in converter.json.
/// </summary>
public partial class NotchWindow
{
    const int ConverterPage = 10;

    sealed class CvState
    {
        public string Mode { get; set; } = "units";
        public string Cat { get; set; } = "Length";
        public Dictionary<string, string[]> Pairs { get; set; } = new();
        public string[] Cur { get; set; } = { "EUR", "USD" };
        public string Value { get; set; } = "100";
    }

    static readonly string[] CvFirstCurrencies = { "EUR", "USD", "GBP", "JPY", "CHF", "CAD", "AUD", "CNY", "INR", "MAD" };
    static string CvFile => Path.Combine(Settings.Folder, "converter.json");

    CvState? _cv;
    Grid? _cvPage, _cvBody, _cvSheet;
    TextBox? _cvLeft, _cvRight;
    TextBlock? _cvNote;
    string? _cvBuiltFor;
    bool _cvWriting;
    DispatcherTimer? _cvSave;
    Fx.Rates? _cvRates;
    bool _cvAsking;

    Color CvInk => Skin.Lit;
    Color CvSoft => Skin.Label;
    Color CvLine => Skin.Line;
    Color CvAcc => _cyber ? CyYellow : Skin.Accent;
    bool CvMoney => _cv?.Mode == "currency";

    Grid BuildConverterPage()
    {
        _cvPage = new Grid { Width = PageWidth, Height = 216 };
        RebuildConverterPage();
        return _cvPage;
    }

    void OnConverterShown()
    {
        if (_cvBuiltFor != Skin.Key + "|" + _accent) RebuildConverterPage();
        if (CvMoney) CvAskRates();
    }

    CvState CvLoad()
    {
        try
        {
            if (File.Exists(CvFile) && JsonSerializer.Deserialize<CvState>(File.ReadAllText(CvFile)) is { } s)
            {
                // whatever the file says, the page gets choices it can build with
                if (s.Mode is not ("units" or "currency")) s.Mode = "units";
                if (Converter.Find(s.Cat) == null) s.Cat = "Length";
                s.Pairs ??= new();
                if (s.Cur is not { Length: 2 } || s.Cur.Any(c => c is not { Length: 3 })) s.Cur = new[] { "EUR", "USD" };
                s.Value ??= "100";
                return s;
            }
        }
        catch (Exception ex) { AppLog.Write("converter: choices unreadable: " + ex.Message); }
        return new CvState();
    }

    /// <summary>The choices are written a second after the last change (not on every key).</summary>
    void CvSaveSoon()
    {
        _cvSave ??= new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _cvSave.Tick -= CvSaveNow;
        _cvSave.Tick += CvSaveNow;
        _cvSave.Stop();
        _cvSave.Start();
    }

    void CvSaveNow(object? sender, EventArgs e)
    {
        _cvSave?.Stop();
        try { WeatherService.SafeWrite(CvFile, JsonSerializer.Serialize(_cv)); } catch (Exception ex) { AppLog.Write("converter: not saved: " + ex.Message); }
    }

    /// <summary>The two units in use: the category's pair (yours, or its usual one), or the two currencies.</summary>
    string[] CvPair()
    {
        var s = _cv!;
        if (CvMoney) return s.Cur;
        var cat = Converter.Find(s.Cat) ?? Converter.Categories[0];
        if (!s.Pairs.TryGetValue(cat.Key, out var p) || p.Length != 2 || Converter.Find(cat.Key, p[0]) == null || Converter.Find(cat.Key, p[1]) == null)
            s.Pairs[cat.Key] = p = new[] { cat.From, cat.To };
        return p;
    }

    string CvName(string unit) => CvMoney ? unit : Loc.T(Converter.Find(_cv!.Cat, unit)?.Name ?? unit);

    void RebuildConverterPage()
    {
        if (_cvPage == null) return;
        _cv ??= CvLoad();
        if (Converter.Find(_cv.Cat) == null) _cv.Cat = "Length";
        _cvBuiltFor = Skin.Key + "|" + _accent;
        _cvPage.Children.Clear();
        _cvSheet = null;
        var root = new Grid { Margin = new Thickness(14, 2, 14, 8) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(18) });

        // the head: the categories on the left (Units), Units · Currency on the right
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition());
        if (!CvMoney)
        {
            var chips = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            FrameworkElement? picked = null;
            foreach (var c in Converter.Categories)
            {
                var key = c.Key;
                var chip = CvChip(Cap(Loc.T(key)), key == _cv.Cat, () => { _cv.Cat = key; CvSaveSoon(); RebuildConverterPage(); }, framed: false);
                if (key == _cv.Cat) picked = chip;
                chips.Children.Add(chip);
            }
            // more categories than room: the wheel slides them sideways
            var strip = new ScrollViewer { Content = chips, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, Margin = new Thickness(10, 0, 0, 0), ClipToBounds = true };
            // the row fades at its right edge: more categories wait there (the wheel brings them)
            var fade = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
            fade.GradientStops.Add(new GradientStop(Colors.Black, 0.86));
            fade.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
            strip.OpacityMask = fade;
            strip.Loaded += (_, _) => picked?.BringIntoView(); // (the one you're on stays in view, even Time at the far end)
            strip.ScrollChanged += (_, _) => fade.GradientStops[0].Offset = strip.HorizontalOffset >= strip.ScrollableWidth - 1 ? 0.999 : 0.86;
            Grid.SetColumn(strip, 1);
            strip.PreviewMouseWheel += (_, e) => { e.Handled = true; strip.ScrollToHorizontalOffset(strip.HorizontalOffset - e.Delta / 3.0); };
            head.Children.Add(strip);
        }
        var seg = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (mode, label) in new[] { ("units", "Units"), ("currency", "Currency") })
            seg.Children.Add(CvChip(Cap(Loc.T(label)), _cv.Mode == mode, () =>
            {
                if (_cv.Mode == mode) return;
                _cv.Mode = mode;
                CvSaveSoon();
                RebuildConverterPage();
                if (CvMoney) CvAskRates();
            }, framed: false));
        var segFrame = new Border
        {
            Child = seg, Padding = new Thickness(2), CornerRadius = new CornerRadius(_cyber ? 0 : 9), VerticalAlignment = VerticalAlignment.Center,
            Background = Frozen(A(CvInk, 0x0A)), BorderBrush = Frozen(CvLine), BorderThickness = new Thickness(1),
        };
        head.Children.Add(segFrame);
        root.Children.Add(head);

        // the two cards and the swap between them
        _cvBody = new Grid { Margin = new Thickness(0, 8, 0, 4) };
        _cvBody.ColumnDefinitions.Add(new ColumnDefinition());
        _cvBody.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        _cvBody.ColumnDefinitions.Add(new ColumnDefinition());
        var pair = CvPair();
        _cvLeft = null;
        _cvRight = null;
        var left = CvCard(0, pair[0], out _cvLeft);
        var right = CvCard(1, pair[1], out _cvRight);
        Grid.SetColumn(right, 2);
        _cvBody.Children.Add(left);
        _cvBody.Children.Add(right);
        var swapGlyph = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 12, Foreground = Frozen(CvSoft), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var swap = new Border
        {
            Width = 28, Height = 28, CornerRadius = new CornerRadius(_cyber ? 0 : 14), Child = swapGlyph, Cursor = Cursors.Hand, ToolTip = Loc.T("Swap"),
            Background = Frozen(A(CvInk, 0x0A)), BorderBrush = Frozen(CvLine), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        swap.MouseEnter += (_, _) => swapGlyph.Foreground = Frozen(CvInk);
        swap.MouseLeave += (_, _) => swapGlyph.Foreground = Frozen(CvSoft);
        swap.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            FeelSound(Services.Sfx.Cue.Tap);
            var p = CvPair();
            (p[0], p[1]) = (p[1], p[0]);
            _cv.Value = _cvRight?.Text ?? _cv.Value; // (the number you saw on the right is now on the left)
            CvSaveSoon();
            RebuildConverterPage();
        };
        Grid.SetColumn(swap, 1);
        _cvBody.Children.Add(swap);
        Grid.SetRow(_cvBody, 1);
        root.Children.Add(_cvBody);

        _cvNote = new TextBlock { FontFamily = ThemeText, FontSize = 10.5, Foreground = Frozen(CvSoft), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Typography.SetNumeralAlignment(_cvNote, FontNumeralAlignment.Tabular);
        Grid.SetRow(_cvNote, 2);
        root.Children.Add(_cvNote);
        _cvPage.Children.Add(root);

        _cvLeft.Text = _cv.Value;
        CvConvert(fromLeft: true);
    }

    /// <summary>A small choice: a category, or one side of Units · Currency.</summary>
    Border CvChip(string text, bool on, Action pick, bool framed = true)
    {
        var t = new TextBlock { Text = text, FontFamily = ThemeText, FontSize = _cyber ? 9 : 11, FontWeight = on ? FontWeights.Medium : FontWeights.Normal, Foreground = Frozen(on ? CvInk : CvSoft) };
        var b = new Border
        {
            Child = t, Padding = new Thickness(10, 3, 10, 4), Margin = new Thickness(0, 0, framed ? 4 : 2, 0), CornerRadius = new CornerRadius(_cyber ? 0 : 7), Cursor = Cursors.Hand,
            Background = on ? Frozen(A(CvInk, 0x16)) : Brushes.Transparent,
            BorderBrush = on && framed ? Frozen(CvLine) : Brushes.Transparent, BorderThickness = new Thickness(framed ? 1 : 0),
        };
        if (!on)
        {
            b.MouseEnter += (_, _) => t.Foreground = Frozen(CvInk);
            b.MouseLeave += (_, _) => t.Foreground = Frozen(CvSoft);
        }
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; if (on) return; FeelSound(Services.Sfx.Cue.Tap); pick(); };
        return b;
    }

    /// <summary>One side: the number (type in it) and the unit under it (a click opens the list).</summary>
    Border CvCard(int side, string unit, out TextBox box)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var value = new TextBox
        {
            FontFamily = ThemeText, FontSize = 26, FontWeight = FontWeights.Medium, Foreground = Frozen(CvInk), CaretBrush = Frozen(CvAcc), SelectionBrush = Frozen(CvAcc),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), FocusVisualStyle = null, VerticalAlignment = VerticalAlignment.Center,
            MaxLength = 24,
        };
        Typography.SetNumeralAlignment(value, FontNumeralAlignment.Tabular);
        value.PreviewMouseLeftButtonDown += (_, _) => { _maybeSwipe = false; BeginWorkTyping(value); };
        value.TextChanged += (_, _) => { if (!_cvWriting) CvConvert(fromLeft: side == 0); };
        value.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Escape or Key.Enter) { e.Handled = true; EndWorkTyping(giveBack: true); }
        };
        grid.Children.Add(value);
        box = value;

        var name = new TextBlock { Text = CvName(unit), FontFamily = ThemeText, FontSize = 11.5, Foreground = Frozen(CvInk), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var chevron = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 9, Foreground = Frozen(CvSoft), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 1, 0, 0) };
        var pickRow = new Grid();
        pickRow.ColumnDefinitions.Add(new ColumnDefinition());
        pickRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pickRow.Children.Add(name);
        Grid.SetColumn(chevron, 1);
        pickRow.Children.Add(chevron);
        var picker = new Border
        {
            Child = pickRow, Height = 28, Padding = new Thickness(10, 0, 10, 0), CornerRadius = new CornerRadius(_cyber ? 0 : 8), Cursor = Cursors.Hand,
            Background = Frozen(A(CvInk, 0x0C)), BorderBrush = Frozen(CvLine), BorderThickness = new Thickness(1), ToolTip = CvMoney ? null : unit,
        };
        picker.MouseEnter += (_, _) => picker.Background = Frozen(A(CvInk, 0x16));
        picker.MouseLeave += (_, _) => picker.Background = Frozen(A(CvInk, 0x0C));
        picker.MouseLeftButtonUp += (_, e) => { e.Handled = true; FeelSound(Services.Sfx.Cue.Tap); CvOpenSheet(side); };
        Grid.SetRow(picker, 1);
        grid.Children.Add(picker);

        var card = new Border
        {
            Child = grid, Padding = new Thickness(12, 8, 12, 12), CornerRadius = new CornerRadius(_cyber ? 0 : 12), Cursor = Cursors.IBeam,
            Background = Frozen(_cyber ? Color.FromArgb(0x90, 0x00, 0x00, 0x00) : A(CvInk, 0x08)), BorderBrush = Frozen(_cyber ? A(CyCyan, 0x50) : CvLine), BorderThickness = new Thickness(1),
        };
        card.MouseLeftButtonDown += (_, e) => { _maybeSwipe = false; BeginWorkTyping(value); value.CaretIndex = value.Text.Length; e.Handled = true; };
        if (_cyber) NeonCorner(card, 7, 0);
        return card;
    }

    /// <summary>Converts from the side you typed in to the other one, and says what one unit is worth.</summary>
    void CvConvert(bool fromLeft)
    {
        if (_cvLeft == null || _cvRight == null || _cv == null) return;
        var src = fromLeft ? _cvLeft : _cvRight;
        var dst = fromLeft ? _cvRight : _cvLeft;
        var p = CvPair();
        string a = fromLeft ? p[0] : p[1], b = fromLeft ? p[1] : p[0];
        string result = "";
        if (Converter.TryParse(src.Text, out var v))
        {
            double? r = CvMoney ? (_cvRates is { } rates ? Fx.Convert(rates, a, b, v) : null) : Converter.Convert(_cv.Cat, a, b, v);
            if (r is double d && double.IsFinite(d)) result = CvMoney ? CvMoneyText(d) : Converter.Format(d);
            _cv.Value = fromLeft ? src.Text : result;
            CvSaveSoon();
        }
        _cvWriting = true;
        dst.Text = result;
        _cvWriting = false;
        CvShowNote();
    }

    static string CvMoneyText(double d) => Math.Abs(d) >= 1 ? Math.Round(d, 2).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : Converter.Format(d);

    void CvShowNote()
    {
        if (_cvNote == null || _cv == null) return;
        var p = CvPair();
        if (CvMoney)
        {
            if (_cvRates is not { } r) { _cvNote.Text = Loc.T(_cvAsking ? "Getting today's rates…" : "No rates yet: they need the internet once"); return; }
            var one = Fx.Convert(r, p[0], p[1], 1);
            _cvNote.Text = (one is double o ? $"1 {p[0]} = {Converter.Format(o)} {p[1]}  ·  " : "") + string.Format(Loc.T("ECB rates · {0}"), CvDate(r.Date));
            return;
        }
        var unit = Converter.Convert(_cv.Cat, p[0], p[1], 1);
        _cvNote.Text = $"1 {p[0]} = {Converter.Format(unit)} {p[1]}";
    }

    static string CvDate(string iso) => DateTime.TryParse(iso, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d.ToString("d MMM yyyy", CvCulture()) : iso;

    static System.Globalization.CultureInfo CvCulture()
    {
        try { return System.Globalization.CultureInfo.GetCultureInfo(Loc.Lang == "en" ? "en-GB" : Loc.Lang); }
        catch { return System.Globalization.CultureInfo.InvariantCulture; }
    }

    /// <summary>The rates, asked off the screen's thread; the saved ones show at once meanwhile.</summary>
    async void CvAskRates()
    {
        _cvRates ??= Fx.Known;
        if (_cvAsking) return;
        _cvAsking = _cvRates == null;
        CvShowNote();
        var r = await Fx.GetAsync();
        _cvAsking = false;
        if (r != null) _cvRates = r;
        if (CvMoney) CvConvert(fromLeft: true); else CvShowNote();
    }

    // ───────────── the list of units (or currencies), on the page ─────────────

    void CvOpenSheet(int side)
    {
        if (_cvBody?.Parent is not Grid root || _cv == null) return;
        if (_cvSheet != null) { root.Children.Remove(_cvSheet); _cvSheet = null; }
        var p = CvPair();
        IEnumerable<(string Key, string Label)> items = CvMoney
            ? CvFirstCurrencies.Concat((_cvRates?.PerEur.Keys ?? Enumerable.Empty<string>()).OrderBy(k => k)).Distinct()
                .Where(k => _cvRates?.PerEur.ContainsKey(k) ?? true).Select(k => (k, k)) // (only the ones the ECB has a rate for)
            : (Converter.Find(_cv.Cat)?.Units ?? Array.Empty<Converter.Unit>()).Select(u => (u.Key, Loc.T(u.Name)));
        var wrap = new WrapPanel();
        foreach (var (key, label) in items)
        {
            string k = key;
            wrap.Children.Add(CvChip(label, k == p[side], () =>
            {
                if (k == p[1 - side]) p[1 - side] = p[side]; // (the other side had it: they trade places)
                p[side] = k;
                CvSaveSoon();
                RebuildConverterPage();
            }).Also(c => c.Margin = new Thickness(0, 0, 4, 4)));
        }
        var close = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 10, Foreground = Frozen(CvSoft), Cursor = Cursors.Hand, Padding = new Thickness(6, 4, 2, 4), Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Right, ToolTip = Loc.T("Close") };
        var head = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        head.Children.Add(new TextBlock { Text = Cap(Loc.T(CvMoney ? "Pick a currency" : "Pick a unit")), FontFamily = ThemeText, FontSize = _cyber ? 9 : 11.5, FontWeight = FontWeights.Medium, Foreground = Frozen(CvInk), VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(close);
        var list = new ScrollViewer { Content = wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, PanningMode = PanningMode.VerticalOnly };
        var body = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        body.Children.Add(head);
        body.Children.Add(list);
        var sheet = new Grid();
        sheet.Children.Add(new Border
        {
            Child = body, Padding = new Thickness(12, 8, 12, 8), CornerRadius = new CornerRadius(_cyber ? 0 : 12),
            Background = Skin.Panel, BorderBrush = Frozen(CvLine), BorderThickness = new Thickness(1),
        });
        Grid.SetRow(sheet, 1);
        Grid.SetRowSpan(sheet, 2);
        Panel.SetZIndex(sheet, 10);
        close.MouseLeftButtonUp += (_, e) => { e.Handled = true; root.Children.Remove(sheet); _cvSheet = null; };
        root.Children.Add(sheet);
        _cvSheet = sheet;
    }
}
