using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DynamicNotch.Controls;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// The Colors page: paint the notch yourself.
///  • four things to paint: Accent (sliders, page dots, highlights), Glow (the light behind the open panel),
///    Edge (the rim around the notch, or the cyber frame), Clock (the time in the header);
///  • a hue / saturation wheel and a brightness mixer for the one you picked, previewed live on the notch itself
///    while you drag; "Auto" gives that one back to the notch (the app's colour, the theme's own);
///  • presets that set all four at once, and Shuffle: a random palette built from colour harmony.
/// The page is built the first time it's opened and runs nothing in the background: it costs only while you drag.
/// Live things keep their meaning (a call stays green, a download blue); your colours are the notch's own.
/// The settings page next to it groups everything else into three tabs: Display, Behaviour, Smart.
/// </summary>
public partial class NotchWindow
{
    enum Paint { Accent, Glow, Edge, Clock, Panel, Pills }
    internal static readonly string[] PaintNames = { "Accent", "Glow", "Edge", "Clock", "Panel", "Pills" };
    internal static readonly string[] PaintHints =
    {
        "Sliders, page dots, highlights", "The light behind the open panel", "The rim around the notch", "The time in the header",
        "The notch's own body, kept dark", "The side pills' outline and glow",
    };
    internal const int PaintCount = 6;

    sealed record Preset(string Name, string? A, string? G, string? E, string? C);
    static readonly Preset[] Presets =
    {
        new("Original · the notch decides", null, null, null, null),
        new("Synth", "#FF2E88", "#7B2CFF", "#FF2E88", "#FF8BC4"),
        new("Neon Night", "#FEE801", "#2FE0C6", "#3CD5FF", "#FEE801"),
        new("Ocean", "#3FA9F5", "#0B5ED7", "#64D2FF", "#BFE9FF"),
        new("Sunset", "#FF7A45", "#FF2D55", "#FFB86B", "#FFD6A5"),
        new("Terminal", "#00FF66", "#00B140", "#00FF66", "#7DFFAE"),
        new("Sakura", "#FF8FB1", "#C77DFF", "#FFC2D1", "#FFE5EC"),
        new("Aurora", "#4ADE80", "#22D3EE", "#A78BFA", "#CFFAFE"),
    };

    Color _ambientAccent = Colors.White; // the app's colour, when the accent is left to the notch
    bool _studioBuilt;
    Paint _paint;
    double _h, _s, _v = 1;
    ColorWheel _wheel = null!;
    PillSlider _bright = null!;
    Border _orb = null!;
    TextBlock _hex = null!, _paintHint = null!;
    Button _autoChip = null!;
    readonly Border[] _paintSwatch = new Border[PaintCount];
    readonly TextBlock[] _paintLabel = new TextBlock[PaintCount];
    readonly Random _shuffleRand = new();

    static Color? Hex(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        try { return (Color)ColorConverter.ConvertFromString(s); } catch { return null; }
    }

    static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    // Dusk wears its own colours whole (the dusk is the theme): your colours rest while it is on, and come back after.
    // Coffee takes yours, brewed: each one warmed toward the café's caramel (the panel toward the roast), so it changes
    // with you and still looks like coffee.
    bool DuskOn => _look?.Sunset == true;
    bool OwnColors => DuskOn || _look?.Coffee != null; // (their light under the panel is always their own)
    Color? Yours(string? hex, bool panel = false)
    {
        if (DuskOn || Hex(hex) is not { } c) return null;
        if (_look?.Coffee is not { } b) return c;
        return panel ? Mix(c, b.Mid, 0.55) : Mix(c, CoffeeCaramel, 0.42);
    }
    Color? CustomAccent => Yours(_settings.ColorAccent);
    Color? CustomGlow => Yours(_settings.ColorGlow);
    Color? CustomEdge => Yours(_settings.ColorEdge);
    Color? CustomClock => Yours(_settings.ColorClock);

    string? GetPaint(Paint p) => p switch
    {
        Paint.Accent => _settings.ColorAccent, Paint.Glow => _settings.ColorGlow, Paint.Edge => _settings.ColorEdge,
        Paint.Clock => _settings.ColorClock, Paint.Panel => _settings.ColorPanel, _ => _settings.ColorPills,
    };

    void SetPaint(Paint p, string? v)
    {
        switch (p)
        {
            case Paint.Accent: _settings.ColorAccent = v; break;
            case Paint.Glow: _settings.ColorGlow = v; break;
            case Paint.Edge: _settings.ColorEdge = v; break;
            case Paint.Panel: _settings.ColorPanel = v; break;
            case Paint.Pills: _settings.ColorPills = v; break;
            default: _settings.ColorClock = v; break;
        }
    }

    /// <summary>What each one looks like when it's left to the notch.</summary>
    Color DefaultFor(Paint p) => p switch
    {
        Paint.Accent => _cyber ? CyYellow : _look?.Accent ?? _ambientAccent,
        Paint.Glow => _cyber ? CyTeal : _look?.Tint ?? (_hasAmbient ? _ambientAccent : Color.FromRgb(0x8E, 0x8E, 0x93)),
        Paint.Edge => _cyber ? CyYellow : _look?.RimColor ?? Colors.White,
        Paint.Panel => _cyber ? Color.FromRgb(0x04, 0x13, 0x1D) : _look != null ? Color.FromRgb(_look.Bottom.R, _look.Bottom.G, _look.Bottom.B) : Color.FromRgb(0x0B, 0x0B, 0x0E),
        Paint.Pills => _cyber ? CyYellow : Colors.White,
        _ => _cyber ? CyYellow : Colors.White,
    };

    /// <summary>Your colours, everywhere they belong (also at start, and whenever the theme changes).</summary>
    void ApplyCustomColors()
    {
        _accent = CustomAccent ?? _look?.Accent ?? _ambientAccent;
        ApplyAccent();
        _panelTint = null;
        UpdatePanelTint();
        UpdateRim();
        ApplyClockColor();
        ApplyPanelColor();
        _pillColor = CustomPills;
        if (_cyber) CyberHudForMode();
        UpdateDots(animate: false);
        // the side pills repaint with it (at start-up the services behind them may not be ready yet)
        try { StylePrivacyIsland(); UpdateSecond(); } catch { }
    }

    Color? CustomPanel => Yours(_settings.ColorPanel, panel: true);
    Color? CustomPills => Yours(_settings.ColorPills);
    static Color? _pillColor; // read by the islands' rim (a static helper)

    /// <summary>The notch's body: black where it meets the screen's edge, your colour deepening toward the bottom.</summary>
    void ApplyPanelColor()
    {
        var mine = CustomPanel;
        if (_look != null) { Pill.Background = LookBody(_look, mine); return; } // glass stays glass, in your colour
        if (mine is not { } c)
        {
            Pill.Background = _cyber && _cyberBody != null ? _cyberBody : _pillBgBefore ?? Pill.Background;
            return;
        }
        var g = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) };
        g.GradientStops.Add(new GradientStop(Colors.Black, 0));
        g.GradientStops.Add(new GradientStop(Color.FromRgb((byte)(c.R * 0.45), (byte)(c.G * 0.45), (byte)(c.B * 0.45)), 0.55));
        g.GradientStops.Add(new GradientStop(c, 1));
        g.Freeze();
        Pill.Background = g;
    }

    void ApplyClockColor()
    {
        if (CustomClock is { } c) ClockText.Foreground = Frozen(c);
        else if (_cyber) ClockText.Foreground = CyYellowBrush;
        else if (_look?.Clock is { } lc) ClockText.Foreground = Frozen(lc);
        else ClockText.ClearValue(TextBlock.ForegroundProperty);
    }

    // ───────────── the page ─────────────

    void OnColorsShown()
    {
        if (!_studioBuilt) { _studioBuilt = true; BuildStudio(); }
        SelectPaint(_paint);
    }

    // the redesign's parts: the ring that slides to what you paint, the colour card, the preset chips
    Grid _targetHost = null!;
    System.Windows.Shapes.Ellipse _targetRing = null!;
    readonly TranslateTransform _targetRingAt = new();
    SolidColorBrush _orbFill = null!;
    TextBlock _copyGlyph = null!;
    FrameworkElement _copyHost = null!; // the code and its line: under the pointer, the copy icon shows
    readonly List<(Preset Pr, Border Tile)> _presetTiles = new();
    bool _glide; // this refresh glides (a new target, a preset): the marker, the ring and the colour move instead of jumping
    int _copiedToken;
    static readonly Brush StudioSurface = Frozen(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
    static readonly Brush StudioLine = Frozen(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
    static readonly Brush StudioLineHover = Frozen(Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF));

    void BuildStudio()
    {
        _presetTiles.Clear();
        var grid = new Grid { Margin = new Thickness(22, 4, 22, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        // the page goes away while a preset is being tried on: yours come back
        grid.IsVisibleChanged += (_, e) => { if (!(bool)e.NewValue) EndTryOn(); };

        // ── the mixer: wheel + brightness ──
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var wheelHost = new Grid { Width = 150, Height = 150 };
        _wheel = new ColorWheel { Width = 150, Height = 150 };
        // a click or a drag on a dark colour brings it to light (else nothing would show); a scroll keeps it as it is
        _wheel.Picked += (h, s) => { _h = h; _s = s; if (_v < 0.2 && !_wheel.Scrolling) { _v = 1; _bright.Value = 1; } PaintLive(); };
        _wheel.Committed += () => _settings.Save();
        wheelHost.Children.Add(_wheel);
        // a hairline round the disc: a crisp edge on any wallpaper or theme
        wheelHost.Children.Add(new System.Windows.Shapes.Ellipse { Stroke = Frozen(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)), StrokeThickness = 1, IsHitTestVisible = false });
        left.Children.Add(wheelHost);
        _bright = new PillSlider { Height = 20, Margin = new Thickness(4, 12, 4, 0), TrackHeight = 8 };
        _bright.ValueChanged += v => { _v = Math.Max(0.04, v); PaintLive(); };
        _bright.ValueCommitted += _ => _settings.Save();
        left.Children.Add(_bright);
        grid.Children.Add(left);

        var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(right, 2);

        // ── what to paint: six swatches, one ring that slides to the one you're painting ──
        var targets = new UniformGrid { Columns = PaintCount, Height = 44 };
        for (int i = 0; i < PaintCount; i++)
        {
            var p = (Paint)i;
            var pop = new ScaleTransform(1, 1);
            var cell = new StackPanel { Background = Brushes.Transparent, Cursor = Cursors.Hand };
            _paintSwatch[i] = new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(1), BorderBrush = Frozen(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Center, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = pop,
            };
            _paintLabel[i] = new TextBlock { Text = Loc.T(PaintNames[i]), FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            cell.Children.Add(_paintSwatch[i]);
            cell.Children.Add(_paintLabel[i]);
            cell.MouseLeftButtonUp += (_, e) => { e.Handled = true; SelectPaint(p); };
            cell.MouseEnter += (_, _) => { ShowTip(cell, Loc.T(PaintHints[(int)p])); StudioPop(pop, 1.12); };
            cell.MouseLeave += (_, _) => StudioPop(pop, 1);
            targets.Children.Add(cell);
        }
        _targetRing = new System.Windows.Shapes.Ellipse
        {
            Width = 30, Height = 30, StrokeThickness = 1.5, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -4, 0, 0), RenderTransform = _targetRingAt,
        };
        _targetHost = new Grid();
        _targetHost.Children.Add(targets);
        _targetHost.Children.Add(_targetRing);
        _targetHost.SizeChanged += (_, _) => PlaceTargetRing(false);
        right.Children.Add(_targetHost);

        // ── the colour itself, on a quiet card: its swatch, its code (click to copy), Auto ──
        var card = new Border
        {
            Margin = new Thickness(0, 8, 0, 0), Height = 44, Padding = new Thickness(6, 0, 10, 0), CornerRadius = new CornerRadius(_cyber ? 2 : 12),
            Background = StudioSurface, BorderBrush = StudioLine, BorderThickness = new Thickness(1),
        };
        var readout = new Grid();
        readout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        readout.ColumnDefinitions.Add(new ColumnDefinition());
        readout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _orbFill = new SolidColorBrush(Colors.White);
        _orb = new Border
        {
            Width = 32, Height = 32, CornerRadius = new CornerRadius(_cyber ? 2 : 6), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center,
            BorderBrush = Frozen(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), Background = _orbFill,
        };
        readout.Children.Add(_orb);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 6, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent };
        _copyHost = text;
        var hexRow = new StackPanel { Orientation = Orientation.Horizontal };
        _hex = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, Typography = { NumeralAlignment = FontNumeralAlignment.Tabular } };
        _copyGlyph = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 11, Foreground = DimBrush, Margin = new Thickness(6, 2, 0, 0), VerticalAlignment = VerticalAlignment.Center, Opacity = 0 };
        hexRow.Children.Add(_hex);
        hexRow.Children.Add(_copyGlyph);
        _paintHint = new TextBlock { FontSize = 11, Foreground = DimBrush, TextTrimming = TextTrimming.CharacterEllipsis };
        text.Children.Add(hexRow);
        text.Children.Add(_paintHint);
        text.MouseLeftButtonUp += (_, e) => { e.Handled = true; try { Clipboard.SetText(ToHex(CurrentStudioColor())); FlashCopied(); } catch { } };
        text.MouseEnter += (_, _) => { ShowTip(text, Loc.T("Click to copy the colour code")); _copyGlyph.Opacity = 1; };
        text.MouseLeave += (_, _) => _copyGlyph.Opacity = 0;
        Grid.SetColumn(text, 1);
        readout.Children.Add(text);
        _autoChip = new Button { Content = Loc.T("Auto"), Height = 24, FontSize = 11, Padding = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        _autoChip.Style = (Style)FindResource("Capsule");
        _autoChip.Click += (_, _) => ToggleAuto();
        _autoChip.MouseEnter += (_, _) => ShowTip(_autoChip, Loc.T("Let the notch choose this one (the app's colour, the theme's own)"));
        Grid.SetColumn(_autoChip, 2);
        readout.Children.Add(_autoChip);
        card.Child = readout;
        right.Children.Add(card);

        // ── presets, shuffle, reset ──
        var head = new Grid { Margin = new Thickness(0, 8, 0, 6) };
        head.Children.Add(new TextBlock { Text = Loc.T("Presets"), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = DimBrush, VerticalAlignment = VerticalAlignment.Center });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(SmallAction(Loc.T("Shuffle"), Loc.T("A random palette from colour harmony"), Shuffle, ""));
        actions.Children.Add(SmallAction(Loc.T("Reset"), Loc.T("Back to the notch's own colours"), () => ApplyPreset(Presets[0])));
        head.Children.Add(actions);
        right.Children.Add(head);

        var presets = new UniformGrid { Columns = 4, Rows = 2 };
        foreach (var pr in Presets) presets.Children.Add(PresetTile(pr));
        right.Children.Add(presets);
        grid.Children.Add(right);

        ColorsPage.Children.Add(grid);
    }

    /// <summary>A small scale change on hover or a click: eased, no spring.</summary>
    void StudioPop(ScaleTransform st, double to, double? from = null, int ms = 140)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(Eco ? 1 : ms)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        if (from is { } f) a.From = f;
        st.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    /// <summary>The ring under the swatch you're painting: it slides there (or sits, on the first layout).</summary>
    void PlaceTargetRing(bool animate)
    {
        if (_targetHost == null) return;
        double cell = _targetHost.ActualWidth / PaintCount;
        if (cell <= 0) return;
        double x = (int)_paint * cell + (cell - _targetRing.Width) / 2;
        if (animate && !Eco && Math.Abs(_targetRingAt.X - x) > 0.5)
        {
            double from = _targetRingAt.X;
            _targetRingAt.BeginAnimation(TranslateTransform.XProperty, null);
            _targetRingAt.X = x;
            _targetRingAt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(from, x, TimeSpan.FromMilliseconds(240))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
        }
        else
        {
            _targetRingAt.BeginAnimation(TranslateTransform.XProperty, null);
            _targetRingAt.X = x;
        }
    }

    /// <summary>The code was copied: it says so for a moment, with a tick.</summary>
    void FlashCopied()
    {
        int token = ++_copiedToken;
        _hex.Text = Loc.T("Copied");
        _copyGlyph.Text = "";
        _copyGlyph.Opacity = 1;
        After(1100, () =>
        {
            if (token != _copiedToken) return;
            _copyGlyph.Text = "";
            _copyGlyph.Opacity = _copyHost.IsMouseOver ? 1 : 0;
            RefreshStudio();
        });
    }

    Button SmallAction(string text, string tip, Action act, string? glyph = null)
    {
        object content = text;
        RotateTransform? turn = null;
        if (glyph != null)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            turn = new RotateTransform();
            row.Children.Add(new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 4, 0), RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = turn });
            row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            content = row;
        }
        var b = new Button { Content = content, Height = 20, FontSize = 11, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(4, 0, 0, 0) };
        b.Style = (Style)FindResource("Capsule");
        b.Click += (_, _) =>
        {
            // its icon turns once (Shuffle: the dice roll)
            if (turn != null && !Eco) turn.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(480)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            act();
        };
        b.MouseEnter += (_, _) => ShowTip(b, tip);
        return b;
    }

    /// <summary>
    /// A preset: its accent, glow and edge as three overlapping dots on a quiet chip. Hovering it tries it on: the
    /// whole notch wears it while the pointer stays (nothing saved); a click keeps it; the one you wear is ringed.
    /// </summary>
    FrameworkElement PresetTile(Preset pr)
    {
        var dots = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (pr.A == null)
        {
            dots.Children.Add(new System.Windows.Shapes.Ellipse { Width = 10, Height = 10, Fill = AutoSwatch, VerticalAlignment = VerticalAlignment.Center });
            dots.Children.Add(new TextBlock { Text = Loc.T("Auto"), FontSize = 11, Foreground = Brushes.White, Margin = new Thickness(4, 0, 0, 1), VerticalAlignment = VerticalAlignment.Center });
        }
        else
        {
            var cols = new[] { pr.A, pr.G, pr.E };
            for (int i = 0; i < 3; i++)
                dots.Children.Add(new System.Windows.Shapes.Ellipse
                {
                    Width = 13, Height = 13, Fill = Frozen(Hex(cols[i])!.Value), Stroke = Frozen(Color.FromRgb(0x16, 0x16, 0x1A)), StrokeThickness = 1.5,
                    Margin = new Thickness(i == 0 ? 0 : -4, 0, 0, 0),
                });
        }
        var scale = new ScaleTransform(1, 1);
        var tile = new Border
        {
            Height = 22, Margin = new Thickness(0, 0, 6, 6), CornerRadius = new CornerRadius(_cyber ? 2 : 8), Background = StudioSurface, Cursor = Cursors.Hand,
            BorderBrush = StudioLine, BorderThickness = new Thickness(1), RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = scale, Child = dots,
        };
        _presetTiles.Add((pr, tile));
        tile.MouseEnter += (_, _) =>
        {
            ShowTip(tile, pr.Name);
            if (Hex(pr.A) is { } tryOn) PageFace?.Tint(tryOn, 1.0); // the face tries it on
            StudioPop(scale, 1.05);
            TryOn(pr, tile);
        };
        tile.MouseLeave += (_, _) => { StudioPop(scale, 1); EndTryOn(); };
        tile.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            StudioPop(scale, tile.IsMouseOver ? 1.05 : 1, from: 0.94, ms: 220);
            _tryStash = null; // what's being tried on is kept
            ApplyPreset(pr);
        };
        return tile;
    }

    void ApplyPreset(Preset pr)
    {
        _settings.ColorAccent = pr.A;
        _settings.ColorGlow = pr.G;
        _settings.ColorEdge = pr.E;
        _settings.ColorClock = pr.C;
        _settings.ColorPills = pr.E;
        if (pr.A == null) _settings.ColorPanel = null;
        _settings.Save();
        ApplyCustomColors();
        if (_cyber) CyberGlitch(strong: false, light: true);
        SelectPaint(_paint);
        FeelIt(_shuffling ? Feel.Spin : Feel.Love, Hex(pr.A)); // it wears the new colour
    }

    // ───── trying a preset on ─────

    string?[]? _tryStash; // your colours while a preset is being tried on (null: nothing is); they stay in the settings throughout
    FrameworkElement? _tryTile;
    System.Windows.Threading.DispatcherTimer? _tryWatch;

    void TryOn(Preset pr, FrameworkElement tile)
    {
        // the notch is painted with the preset, then your colours go straight back into the settings: whatever saves
        // meanwhile (or if the app closes mid-hover), yours are what's kept
        var mine = new[] { _settings.ColorAccent, _settings.ColorGlow, _settings.ColorEdge, _settings.ColorClock, _settings.ColorPanel, _settings.ColorPills };
        _tryStash = mine;
        _tryTile = tile;
        _settings.ColorAccent = pr.A;
        _settings.ColorGlow = pr.G;
        _settings.ColorEdge = pr.E;
        _settings.ColorClock = pr.C;
        _settings.ColorPills = pr.E;
        _settings.ColorPanel = pr.A == null ? null : mine[4];
        try { ApplyCustomColors(); }
        finally { RestoreColors(mine); }
        // a safety: if the pointer is gone without a leave (the notch closed under it), yours come back
        _tryWatch ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(300), System.Windows.Threading.DispatcherPriority.Background, (_, _) =>
        {
            if (_tryStash == null) { _tryWatch!.Stop(); return; }
            if (_tryTile?.IsMouseOver != true || _mode != Mode.Expanded) EndTryOn();
        }, Dispatcher);
        _tryWatch.Start();
    }

    void EndTryOn()
    {
        _tryWatch?.Stop();
        if (_tryStash == null) return;
        _tryStash = null;
        _tryTile = null;
        ApplyCustomColorsSoon(); // the notch in your colours again (the settings never changed)
    }

    void RestoreColors(string?[] s)
    {
        _settings.ColorAccent = s[0];
        _settings.ColorGlow = s[1];
        _settings.ColorEdge = s[2];
        _settings.ColorClock = s[3];
        _settings.ColorPanel = s[4];
        _settings.ColorPills = s[5];
    }

    /// <summary>The preset you're wearing (Original: everything left to the notch).</summary>
    bool PresetWorn(Preset pr)
    {
        static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        return Same(_settings.ColorAccent, pr.A) && Same(_settings.ColorGlow, pr.G) && Same(_settings.ColorEdge, pr.E) && Same(_settings.ColorClock, pr.C)
            && Same(_settings.ColorPills, pr.E) && (pr.A != null || _settings.ColorPanel == null);
    }

    bool _shuffling;

    /// <summary>A palette from one random hue: accent, a glow beside it on the wheel, an edge a third of the way round, a clock almost white.</summary>
    void Shuffle()
    {
        double h = _shuffleRand.NextDouble() * 360;
        _shuffling = true;
        try { ApplyPreset(new Preset("", ToHex(ColorWheel.Hsv(h, 0.78, 1)), ToHex(ColorWheel.Hsv(h + 28, 0.85, 0.92)),
            ToHex(ColorWheel.Hsv(h + 120, 0.6, 1)), ToHex(ColorWheel.Hsv(h, 0.18, 1)))); }
        finally { _shuffling = false; }
    }

    Color CurrentStudioColor() => Hex(GetPaint(_paint)) ?? DefaultFor(_paint);

    void SelectPaint(Paint p)
    {
        _paint = p;
        if (!_studioBuilt) return;
        var (h, s, v) = ColorWheel.ToHsv(CurrentStudioColor());
        if (s > 0.01) _h = h; // a grey keeps the last hue (its own is meaningless)
        _s = s;
        _v = v;
        _bright.Value = v;
        _glide = true; // a new target or palette: the marker, the ring and the colour glide to it
        try { RefreshStudio(); } finally { _glide = false; }
    }

    /// <summary>Dragging on the wheel or the mixer: the notch takes the colour as you go.</summary>
    void PaintLive()
    {
        var c = ColorWheel.Hsv(_h, _s, _paint == Paint.Panel ? Math.Min(_v, 0.38) : _v);
        if (GetPaint(_paint) == ToHex(c)) return;
        SetPaint(_paint, ToHex(c));
        ApplyCustomColorsSoon();
        RefreshStudio();
        FeelFace()?.Tint(c, 1.2); // the face wears the colour as you pick it
    }

    bool _colorsQueued;

    /// <summary>
    /// While dragging, the mouse reports far more often than the screen redraws: the notch is repainted once,
    /// after the pending moves are handled, with the latest colour (not once per move).
    /// </summary>
    void ApplyCustomColorsSoon()
    {
        if (_colorsQueued) return;
        _colorsQueued = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            _colorsQueued = false;
            ApplyCustomColors();
        });
    }

    void ToggleAuto()
    {
        SetPaint(_paint, GetPaint(_paint) == null ? ToHex(ColorWheel.Hsv(_h, _s, _v)) : null);
        _settings.Save();
        ApplyCustomColors();
        SelectPaint(_paint);
    }

    void RefreshStudio()
    {
        if (!_studioBuilt) return;
        var c = CurrentStudioColor();
        bool auto = GetPaint(_paint) == null;
        bool glide = _glide && !Eco;
        _wheel.SetMarker(_h, _s, c, glide);
        _wheel.ScrollEnabled = !auto; // a colour left on Auto isn't turned into a fixed one by a stray scroll
        var full = ColorWheel.Hsv(_h, _s, 1);
        var mix = new LinearGradientBrush(Colors.Black, full, 0);
        mix.Freeze();
        _bright.FillBrush = mix;
        // the swatch: glides to a new colour, follows a drag exactly
        if (glide) _orbFill.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(c, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        else { _orbFill.BeginAnimation(SolidColorBrush.ColorProperty, null); _orbFill.Color = c; }
        _hex.Text = auto ? $"{Loc.T("Auto")} · {ToHex(c)}" : ToHex(c);
        _paintHint.Text = Loc.T(PaintHints[(int)_paint]);
        _autoChip.Background = auto ? Brushes.White : (Brush)FindResource("Tile");
        _autoChip.Foreground = auto ? Brushes.Black : Brushes.White;
        var ring = _cyber ? CyYellowBrush : Brushes.White;
        _targetRing.Stroke = ring;
        PlaceTargetRing(glide);
        for (int i = 0; i < PaintCount; i++)
        {
            var p = (Paint)i;
            var k = Hex(GetPaint(p));
            _paintSwatch[i].Background = k is { } kk ? Frozen(kk) : AutoSwatch;
            bool on = p == _paint;
            _paintLabel[i].Foreground = on ? Brushes.White : DimBrush;
            _paintLabel[i].FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }
        foreach (var (pr, tile) in _presetTiles)
            tile.BorderBrush = PresetWorn(pr) ? ring : tile.IsMouseOver ? StudioLineHover : StudioLine;
    }

    /// <summary>"Left to the notch": a little spectrum.</summary>
    static readonly Brush AutoSwatch = MakeAutoSwatch();
    static Brush MakeAutoSwatch()
    {
        var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        string[] cols = { "#FF5E5E", "#FFD60A", "#30D158", "#0A84FF", "#BF5AF2" };
        for (int i = 0; i < cols.Length; i++) g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(cols[i]), i / 4.0));
        g.Freeze();
        return g;
    }

    // ───────────── settings: three tabs on one page ─────────────

    int _settingsTab;
    static readonly string[] SettingsTabNames = { "Display", "Behaviour", "Smart" };

    void BuildSettingsTabs()
    {
        for (int i = 0; i < SettingsTabNames.Length; i++)
        {
            int tab = i;
            var b = new Button { Content = Loc.T(SettingsTabNames[i]), Height = 22, FontSize = 11, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(2, 0, 2, 0) };
            b.Style = (Style)FindResource("Capsule");
            b.Click += (_, _) => SelectSettingsTab(tab);
            SettingsTabs.Children.Add(b);
        }
        // everything else (language, colours, pages, shortcuts): the Preferences window
        var more = new Button { Width = 26, Height = 22, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(0),
            Content = new TextBlock { Text = "\uE713", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 11 } };
        more.Style = (Style)FindResource("Capsule");
        more.Click += (_, _) => { Collapse(); PreferencesWindow.ShowFor(this); };
        more.MouseEnter += (_, _) => ShowTip(more, Loc.T("Preferences…"));
        SettingsTabs.Children.Add(more);
        SelectSettingsTab(0, animate: false);
    }

    void SelectSettingsTab(int tab, bool animate = true)
    {
        _settingsTab = tab;
        FrameworkElement[] panels = { SettingsGrid, BehaviourGrid, SmartPanel };
        for (int i = 0; i < panels.Length; i++)
        {
            bool on = i == tab;
            panels[i].Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (on && animate)
            {
                var shift = panels[i].RenderTransform as TranslateTransform ?? new TranslateTransform();
                panels[i].RenderTransform = shift;
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
                panels[i].BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
            }
        }
        foreach (var (b, i) in SettingsTabs.Children.OfType<Button>().Take(SettingsTabNames.Length).Select((b, i) => (b, i)))
        {
            bool on = i == tab;
            StyleTab(b, on);
        }
        if (_cyber && animate) CyberGlitch(strong: false, light: true);
    }
}
