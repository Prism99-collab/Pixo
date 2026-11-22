using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using DynamicNotch.Controls;

namespace DynamicNotch;

/// <summary>
/// Island styles: how the notch looks while it rests (idle, slim over a window, music, a call or a timer). Classic is
/// the pill; the three others change the shape itself:
///  • Horizon: no pill, a line of light along the top edge with a soft dark veil hanging from it. At rest it breathes,
///    the face on the veil. With music the line becomes the sound wave; the cover and what else is live hang from it
///    on fine threads, swaying with it.
///  • Mercury: a drop of black liquid hanging from the edge, the face inside. Each live thing buds off as its own
///    droplet (the cover, the call's time...), joined by a liquid neck; they wobble with the beat and merge back.
///  • Eclipse: a dark half-moon rising from the edge, ringed by light. With music the rim becomes a round sound meter
///    and the disc a turning record (the face its label); a call draws a green arc that sweeps with the seconds.
/// How it works: while one of these shows, the pill stays where it is as the touch area (hover, peek, a click to open
/// all work as before) but is invisible; the style is drawn on a layer of its own from the same live state, and the
/// face moves onto it. Every size and position is a spring, so whatever changes (music starts, a call comes in, the
/// pointer comes near) glides. Pop-ups and the open notch take over from it with a short cross-fade: the pill grows out
/// of the style's own footprint. All of it follows the theme's colours (and the song's), and Neon's angles.
/// Light: a few shapes redrawn 30 times a second while something moves, 12 while it only breathes, none while hidden.
/// </summary>
public partial class NotchWindow
{
    public static readonly (string Key, string Name)[] IslandStyles = { ("classic", "Classic"), ("horizon", "Horizon"), ("mercury", "Mercury"), ("eclipse", "Eclipse") };

    // for a look (DYNAMICNOTCH_ISLAND=horizon …): a style shown without changing the setting
    static readonly string? DevIsland = Environment.GetEnvironmentVariable("DYNAMICNOTCH_ISLAND");
    string IslandStyle => DevIsland is { } dev && IslandStyles.Any(s => s.Key == dev) ? dev : IslandStyles.Any(s => s.Key == _settings.IslandStyle) ? _settings.IslandStyle : "classic";
    bool StyleChosen => IslandStyle != "classic";

    /// <summary>The style draws now: resting, not in a game, not during the welcome.</summary>
    bool StyleWanted => StyleChosen && IsBase(_mode) && _mode != Mode.Game && _welcomePhase == 0;

    bool _styleOn;
    Canvas? _styleLayer;
    DispatcherTimer? _styleTimer;
    long _styleAt;
    double _styleT; // seconds of motion (paused while nothing moves)

    // the layers (each style uses some of them)
    Path? _sVeil, _sGlow, _sLine, _sBody, _sRim, _sBars, _sArc, _sTrack, _sNecks; // (_sNecks: Horizon's dark edge under the line)
    Ellipse? _sDisc, _sCover;
    Grid? _faceHolder;
    double _faceAway; // 0: the face shows, 1: it has stepped away (set by the style each frame)
    TextBlock? _sTitle;
    readonly List<(Ellipse Bead, Line Thread, Border Chip, TextBlock Text, Spring Show)> _sPend = new();

    // springs: the shape, the music, each live item
    readonly Spring _sLen = new(130), _sDepth = new(30), _sR = new(20), _sMusic = new(0), _sPeek = new(0), _sCall = new(0);
    readonly Spring[] _sItem = { new(0), new(0), new(0) };
    double _sSpin, _sLevel;

    // ───────────── turning it on and off ─────────────

    /// <summary>Called whenever the mode, the hiding or the setting may have changed: the style shows or hands over.</summary>
    void StyleSync()
    {
        bool on = StyleWanted;
        if (on != _styleOn)
        {
            _styleOn = on;
            EnsureStyleLayer();
            if (on) { FaceToStyle(true); StyleKick(); }
            // the pill hands over to the style (and back): one fades as the other comes. Coming to rest, the pill first
            // shrinks a little toward the style, then melts into it; leaving, the pill grows straight out of it.
            static DoubleAnimation Fade(double to, int ms, int after) => new(to, TimeSpan.FromMilliseconds(ms))
                { BeginTime = TimeSpan.FromMilliseconds(after), EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
            bool quick = Eco;
            // (hidden over a full screen, a pop-up still shows: only the resting notch stays away)
            Shell.BeginAnimation(OpacityProperty, Fade(on || _hidden && IsBase(_mode) ? 0 : 1, quick ? 1 : on ? 170 : 90, quick || !on ? 0 : 110));
            _styleLayer!.BeginAnimation(OpacityProperty, Fade(on && !_hidden ? 1 : 0, quick ? 1 : on ? 240 : 140, quick || !on ? 0 : 40));
            if (!on) _styleTimer?.Stop();
            PlacePrivacyBubble();
        }
        if (_styleLayer != null) _styleLayer.Visibility = StyleChosen ? Visibility.Visible : Visibility.Collapsed;
        if (on) StyleKick();
    }

    /// <summary>The style picked in Preferences or on the Settings page: it changes at once.</summary>
    public void SetIslandStyle(string key)
    {
        _settings.IslandStyle = key;
        _settings.Save();
        if (key == "classic") { FaceToStyle(false); _styleOn = true; } // (so the sync below hands back to the pill)
        StyleClear();
        StyleSync();
        ApplyShape();
        UpdateSecond();
        RefreshIslandChip();
    }

    void EnsureStyleLayer()
    {
        if (_styleLayer != null) return;
        _styleLayer = new Canvas { IsHitTestVisible = true, Opacity = 0, ClipToBounds = false, Background = null };
        Panel.SetZIndex(_styleLayer, 5);
        Root.Children.Insert(Root.Children.IndexOf(Shell) + 1, _styleLayer);
    }

    /// <summary>The face lives on the style's layer while a style shows (and back in the pill for Classic).</summary>
    void FaceToStyle(bool on)
    {
        var face = FaceGet();
        if (on && _styleLayer != null && face.Parent != _styleLayer)
        {
            (face.Parent as Panel)?.Children.Remove(face);
            face.Margin = new Thickness(0);
            face.HorizontalAlignment = HorizontalAlignment.Left;
            face.VerticalAlignment = VerticalAlignment.Top;
            if (_faceHolder == null)
            {
                _faceHolder = new Grid { RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform() };
                Panel.SetZIndex(_faceHolder, 50);
                _styleLayer.Children.Add(_faceHolder);
            }
            _faceHolder.Children.Add(face);
        }
        else if (!on && face.Parent != Stage)
        {
            (face.Parent as Panel)?.Children.Remove(face);
            face.Margin = new Thickness(0, 3, 0, 0);
            face.HorizontalAlignment = HorizontalAlignment.Center;
            face.VerticalAlignment = VerticalAlignment.Top;
            Stage.Children.Insert(0, face);
        }
    }

    void StyleClear()
    {
        if (_styleLayer == null) return;
        foreach (var el in _styleLayer.Children.OfType<UIElement>().Where(e => e != _faceHolder).ToList()) _styleLayer.Children.Remove(el);
        _sVeil = _sGlow = _sLine = _sBody = _sRim = _sBars = _sArc = _sTrack = _sNecks = null;
        _sDisc = _sCover = null;
        _sTitle = null;
        _sPend.Clear();
    }

    /// <summary>Something changed: it draws (and keeps drawing while anything moves).</summary>
    void StyleKick()
    {
        if (!_styleOn || _styleLayer == null) return;
        _styleTimer ??= NewStyleTimer();
        if (!_styleTimer.IsEnabled) { _styleAt = 0; _styleTimer.Start(); }
    }

    DispatcherTimer NewStyleTimer()
    {
        var t = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        t.Tick += (_, _) => StyleFrame();
        return t;
    }

    // ───────────── what's live ─────────────

    sealed record LiveBit(LiveActivity Kind, string Glyph, string Text, Color Color);

    /// <summary>Everything live but the music, in the order the notch ranks them (a call first).</summary>
    List<LiveBit> StyleBits()
    {
        var list = new List<LiveBit>();
        foreach (var a in Activities())
            switch (a)
            {
                case LiveActivity.Call when _call is { } call:
                    list.Add(new(a, "", FormatTimer(DateTime.UtcNow - call.Started), ((SolidColorBrush)CallBrush).Color)); break;
                case LiveActivity.Timer:
                    var left = _timerEnd - DateTime.UtcNow;
                    list.Add(new(a, "", FormatTimer(left < TimeSpan.Zero ? TimeSpan.Zero : left), ((SolidColorBrush)TimerBrush).Color)); break;
                case LiveActivity.Focus:
                    int mins = (int)_focus.Elapsed.TotalMinutes;
                    list.Add(new(a, "", mins < 1 ? "Focus" : $"{mins}m", ((SolidColorBrush)FocusPurple).Color)); break;
                case LiveActivity.Download:
                    var share = _files.Items.FirstOrDefault()?.Progress;
                    list.Add(new(a, "", share is { } sh ? $"{sh * 100:0}%" : "…", ((SolidColorBrush)Blue).Color)); break;
            }
        return list.Take(3).ToList();
    }

    bool StyleMusic => _media.HasSession && Activities().Contains(LiveActivity.Media);

    /// <summary>The two colours of the moment: the song's while it plays, else the theme's (Neon: yellow and cyan).</summary>
    (Color A, Color B) StyleColors(bool music)
    {
        if (_cyber) return (CustomAccent ?? CyYellow, CyCyan);
        if (DuskOn) return (Color.FromRgb(0xFF, 0x4F, 0x9A), Color.FromRgb(0xFF, 0xC8, 0x57));
        if (music && _media.Accent != Colors.White) return (Lighten(_media.Accent, 0.1), Lighten(_media.Accent2, 0.15));
        var a = BarAccent();
        if (a == Colors.White) a = Color.FromRgb(0x8E, 0x8C, 0xFF);
        return (a, Lighten(a, 0.35));
    }

    Brush CoverBrush((Color A, Color B) c)
    {
        if (CompactArt.Background is ImageBrush img) return img;
        var g = new LinearGradientBrush(c.A, c.B, 45); g.Freeze();
        return g;
    }

    // ───────────── the footprint (the invisible pill underneath: hover, peek, clicks) ─────────────

    /// <summary>The style's size right now, for the pill underneath (so the pointer finds it where it's drawn).</summary>
    bool StyleFootprint(ref double w, ref double h, ref double r)
    {
        if (!StyleWanted) return false;
        // the size it's drawn at (Slim shows neither the music nor the live things: just the small shape)
        bool slim = _mode == Mode.Slim, music = !slim && StyleMusic;
        int bits = slim ? 0 : StyleBits().Count;
        switch (IslandStyle)
        {
            case "horizon":
                w = slim ? 96 : 120 + (music ? 84 : 0) + bits * 42;
                h = slim ? 8 : music || bits > 0 ? 38 : 28;
                break;
            case "mercury":
                double rr = slim ? 9 : music || bits > 0 ? 20 : 17;
                double left = music ? MercuryCoverGap(rr) + 11 : 0;
                double right = bits > 0 ? MercuryBitGap(rr, bits - 1) + 16 : 0;
                w = Math.Max(rr * 2.6 + 8, 2 * Math.Max(left, right));
                h = slim ? 12 : rr * 1.92 + (music || bits > 0 ? 14 : 2);
                break;
            default: // eclipse
                double R = slim ? 12 : music || bits > 0 ? 28 : 22;
                w = Math.Max((R + (music ? 12 : 3)) * 2, bits > 0 ? 64 : 0);
                h = slim ? 10 : R * 0.82 + (music ? 10 : 3) + (bits > 0 ? 20 : 0);
                break;
        }
        if (_peek) { w += 8; h += 3; }
        r = Math.Min(h / 2, 22);
        return true;
    }

    // ───────────── each frame ─────────────

    void StyleFrame()
    {
        if (!_styleOn || _styleLayer == null || _hidden || Unseen) { _styleTimer?.Stop(); return; }
        long now = Environment.TickCount64;
        double dt = _styleAt == 0 ? 1 / 30.0 : Math.Clamp((now - _styleAt) / 1000.0, 1 / 120.0, 0.1);
        _styleAt = now;
        _styleT += dt;

        bool music = StyleMusic, playing = music && _media.IsPlaying, slim = _mode == Mode.Slim;
        var bits = StyleBits();
        var col = StyleColors(music);
        // the music's loudness (the same five voices the bars hear; a soft swell when there's no meter)
        double lv = _eqRunning ? _eqLevel.Average() : playing ? 0.35 + 0.15 * Math.Sin(_styleT * 3) : 0;
        _sLevel += (lv - _sLevel) * 0.35;

        _sMusic.Target = music && !slim ? 1 : 0;
        _sPeek.Target = _peek ? 1 : 0;
        _sCall.Target = bits.Any(b => b.Kind == LiveActivity.Call) ? 1 : 0;
        for (int i = 0; i < _sItem.Length; i++) _sItem[i].Target = !slim && i < bits.Count ? 1 : 0;
        bool settled = true;
        foreach (var s in new[] { _sLen, _sDepth, _sR, _sMusic, _sPeek, _sCall }.Concat(_sItem)) settled &= s.Step(dt);
        if (playing) _sSpin = (_sSpin + dt * 40) % 360;

        double cx = Root.ActualWidth / 2;
        _faceAway = 0;
        switch (IslandStyle)
        {
            case "horizon": DrawHorizon(cx, music, slim, bits, col); break;
            case "mercury": DrawMercury(cx, music, slim, bits, col); break;
            default: DrawEclipse(cx, music, slim, bits, col); break;
        }

        // the pace: 30 frames while something moves, 12 while it only breathes, none when all is still (Eco: no breath)
        bool moving = !settled || playing || bits.Count > 0;
        var want = moving ? 33 : Eco ? 0 : 83;
        if (want == 0) { _styleTimer?.Stop(); return; }
        if (_styleTimer != null && Math.Abs(_styleTimer.Interval.TotalMilliseconds - want) > 1) _styleTimer.Interval = TimeSpan.FromMilliseconds(want);
    }

    // ───────────── pieces ─────────────

    T Piece<T>(ref T? field, Func<T> make) where T : UIElement
    {
        if (field == null)
        {
            field = make();
            field.IsHitTestVisible = false;
            _styleLayer!.Children.Add(field);
        }
        return field;
    }

    static Brush Along(Color a, Color b, byte alpha = 0xFF, bool fadeEnds = false)
    {
        var g = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        Color A(Color c, byte al) => Color.FromArgb(al, c.R, c.G, c.B);
        if (fadeEnds) g.GradientStops.Add(new GradientStop(A(a, 0), 0));
        g.GradientStops.Add(new GradientStop(A(a, alpha), fadeEnds ? 0.22 : 0));
        g.GradientStops.Add(new GradientStop(A(b, alpha), fadeEnds ? 0.78 : 1));
        if (fadeEnds) g.GradientStops.Add(new GradientStop(A(b, 0), 1));
        g.Freeze();
        return g;
    }

    /// <summary>A hanging chip (the cover, or a live item's glyph and text), made once and reused.</summary>
    (Ellipse Bead, Line Thread, Border Chip, TextBlock Text, Spring Show) Pendant(int i)
    {
        while (_sPend.Count <= i)
        {
            var bead = new Ellipse { Width = 4.4, Height = 4.4, IsHitTestVisible = false };
            var thread = new Line { StrokeThickness = 1, IsHitTestVisible = false };
            var text = new TextBlock { FontSize = 10.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI") };
            System.Windows.Documents.Typography.SetNumeralAlignment(text, FontNumeralAlignment.Tabular);
            var chip = new Border
            {
                Height = 18, Padding = new Thickness(7, 0, 8, 1), BorderThickness = new Thickness(1), IsHitTestVisible = false,
                Background = Frozen(Color.FromArgb(0xF2, 0x05, 0x05, 0x07)), Child = text, RenderTransformOrigin = new Point(0.5, 0), RenderTransform = new ScaleTransform(),
            };
            _styleLayer!.Children.Add(thread);
            _styleLayer.Children.Add(bead);
            _styleLayer.Children.Add(chip);
            _sPend.Add((bead, thread, chip, text, new Spring(0)));
        }
        return _sPend[i];
    }

    void HidePendantsFrom(int i)
    {
        for (int k = i; k < _sPend.Count; k++) { _sPend[k].Bead.Opacity = 0; _sPend[k].Thread.Opacity = 0; _sPend[k].Chip.Opacity = 0; }
    }

    void PlaceFace(double x, double y, double scale = 1)
    {
        if (_face == null || _faceHolder == null || _face.Parent != _faceHolder) return;
        double w = _face.ActualWidth > 0 ? _face.ActualWidth : 22, h = _face.ActualHeight > 0 ? _face.ActualHeight : 18;
        Canvas.SetLeft(_faceHolder, Math.Round(x - w / 2));
        Canvas.SetTop(_faceHolder, Math.Round(y - h / 2));
        // fully there unless a style says otherwise (Eclipse lets it go while the record turns)
        _faceHolder.Opacity = 1 - _faceAway;
        if (_faceHolder.RenderTransform is ScaleTransform st) st.ScaleX = st.ScaleY = 1 - 0.35 * _faceAway;
        _faceHolder.Visibility = _faceAway > 0.99 ? Visibility.Hidden : Visibility.Visible;
    }

    // ───────────── Horizon ─────────────

    void DrawHorizon(double cx, bool music, bool slim, List<LiveBit> bits, (Color A, Color B) c)
    {
        double m = _sMusic.Value, pk = _sPeek.Value;
        _sLen.Target = slim ? 96 : 120 + m * 84 + bits.Count * 42 + pk * 8;
        _sDepth.Target = slim ? 0 : 27 + pk * 3; // the bowl's depth
        double len = _sLen.Value, depth = _sDepth.Value, t = _styleT;
        const int N = 72;
        var ys = new double[N + 1];
        var line = new StreamGeometry();
        using (var g = line.Open())
            for (int i = 0; i <= N; i++)
            {
                double u = i / (double)N, x = cx - len / 2 + u * len;
                double amp = m * (3.6 * _sLevel + 0.9) * Math.Sin(Math.PI * u);
                double y = 2 + amp * Math.Sin(u * Math.PI * 4.6 - t * 5.5) + amp * 0.3 * Math.Sin(u * Math.PI * 10 + t * 7) + amp * 0.8
                    + (1 - m) * 0.35 * Math.Sin(t * 1.3 + u * 5); // at rest: a slow shimmer along it
                ys[i] = y;
                if (i == 0) g.BeginFigure(new Point(x, y), false, false); else g.LineTo(new Point(x, y), true, true);
            }
        line.Freeze();
        double LineY(double u) => ys[(int)Math.Round(Math.Clamp(u, 0, 1) * N)];

        // the bowl: a shallow dark curve hanging from the middle of the line, the face resting in it
        // (crisp, so it reads the same on a dark or a bright wallpaper)
        var veil = Piece(ref _sVeil, () => new Path { Fill = Frozen(Color.FromRgb(0x04, 0x04, 0x06)) });
        double live = Math.Min(1, m + (bits.Count > 0 ? _sItem[0].Value : 0));
        double bw = 66 + 18 * live + 6 * pk, bd = Math.Max(0, depth);
        var bowl = new StreamGeometry();
        using (var g = bowl.Open())
        {
            double l = cx - bw / 2, r = cx + bw / 2;
            g.BeginFigure(new Point(l - 6, -1), true, true);
            g.BezierTo(new Point(l + bw * 0.18, -1), new Point(cx - bw * 0.42, bd), new Point(cx, bd), true, true);
            g.BezierTo(new Point(cx + bw * 0.42, bd), new Point(r - bw * 0.18, -1), new Point(r + 6, -1), true, true);
        }
        bowl.Freeze();
        veil.Data = bowl;
        veil.Opacity = Math.Clamp(bd / 6, 0, 1);

        // a faint dark edge under the line (on a bright wallpaper the colour still reads)
        var edge = Piece(ref _sNecks, () => new Path { StrokeThickness = 4, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        edge.Stroke = Along(Colors.Black, Colors.Black, 0x40, fadeEnds: true);
        edge.Data = line;

        // the line, and its glow under it
        double breath = m > 0.5 || bits.Count > 0 ? 1 : 0.78 + 0.22 * Math.Sin(t * 1.2);
        var glow = Piece(ref _sGlow, () => new Path { StrokeThickness = 5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        glow.Stroke = Along(c.A, c.B, 0x48, fadeEnds: true);
        glow.Data = line;
        glow.Opacity = (0.45 + 0.45 * pk) * breath;
        var stroke = Piece(ref _sLine, () => new Path { StrokeThickness = 1.8, StrokeLineJoin = PenLineJoin.Round });
        stroke.Stroke = Along(Lighten(c.A, 0.2), Lighten(c.B, 0.2), 0xFF, fadeEnds: true);
        stroke.StrokeStartLineCap = stroke.StrokeEndLineCap = _cyber ? PenLineCap.Square : PenLineCap.Round;
        stroke.Data = line;
        stroke.Opacity = 0.75 + 0.25 * breath;

        // the face rests on the veil
        PlaceFace(cx, (slim ? 0 : 14 + 1.5 * live + 1.5 * pk) + 0.6 * Math.Sin(t * 1.2) * (1 - m));
        if (_face != null) _face.Visibility = slim ? Visibility.Hidden : _face.Visibility == Visibility.Hidden ? Visibility.Visible : _face.Visibility;

        // what hangs from it: the cover (left), then each live thing (right), on fine threads
        int p = 0;
        void Hang(double u, double drop, double show, Action<Border, TextBlock> dress, Color tone)
        {
            var (bead, thread, chip, text, _) = Pendant(p++);
            double x0 = cx - len / 2 + u * len, y0 = LineY(u), sway = 1.1 * Math.Sin(t * 1.2 + u * 5);
            dress(chip, text);
            chip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double cw = chip.DesiredSize.Width, x1 = x0 + sway, y1 = y0 + 8 + drop * show;
            Canvas.SetLeft(bead, x0 - 2.2); Canvas.SetTop(bead, y0 - 2.2);
            bead.Fill = Frozen(tone); bead.Opacity = show;
            thread.X1 = x0; thread.Y1 = y0; thread.X2 = x1; thread.Y2 = y1;
            thread.Stroke = Frozen(Color.FromArgb(0x90, tone.R, tone.G, tone.B)); thread.Opacity = show;
            Canvas.SetLeft(chip, x1 - cw / 2); Canvas.SetTop(chip, y1);
            chip.Opacity = show;
            if (chip.RenderTransform is ScaleTransform st) st.ScaleX = st.ScaleY = 0.6 + 0.4 * show;
        }
        if (m > 0.01)
        {
            var brush = CoverBrush(c);
            Hang(0.14, 6, m, (chip, text) =>
            {
                chip.Width = 20; chip.Height = 20; chip.Padding = new Thickness(0); chip.CornerRadius = new CornerRadius(_cyber ? 3 : 10);
                chip.Background = brush; chip.BorderBrush = Frozen(Color.FromRgb(0x05, 0x05, 0x07)); chip.BorderThickness = new Thickness(2); text.Text = "";
            }, c.A);
        }
        for (int i = 0; i < bits.Count; i++)
        {
            var b = bits[i];
            double u = 0.86 - i * (0.18 * 130 / Math.Max(130, len));
            Hang(u, 4, _sItem[i].Value, (chip, text) =>
            {
                chip.Width = double.NaN; chip.Height = 17; chip.Padding = new Thickness(6, 0, 7, 1); chip.CornerRadius = new CornerRadius(_cyber ? 2 : 8.5);
                chip.Background = Frozen(Color.FromArgb(0xF2, 0x05, 0x05, 0x07)); chip.BorderBrush = Frozen(Color.FromArgb(0x90, b.Color.R, b.Color.G, b.Color.B)); chip.BorderThickness = new Thickness(1);
                text.Inlines.Clear();
                text.Inlines.Add(new System.Windows.Documents.Run(b.Glyph + " ") { FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 9.5 });
                text.Inlines.Add(new System.Windows.Documents.Run(b.Text));
                text.Foreground = Frozen(b.Color);
                text.FontSize = 10;
            }, b.Color);
        }
        HidePendantsFrom(p);

        // (no song name: the cover says enough, and the open notch has the rest)
        if (_sTitle != null) _sTitle.Opacity = 0;
    }

    // ───────────── Mercury ─────────────

    void DrawMercury(double cx, bool music, bool slim, List<LiveBit> bits, (Color A, Color B) c)
    {
        double m = _sMusic.Value, pk = _sPeek.Value, t = _styleT;
        double beat = Math.Pow(Math.Max(0, _sLevel - 0.25) / 0.75, 1.5);
        _sR.Target = slim ? 9 : (m > 0.01 || bits.Count > 0 ? 20 : 17) + pk * 2;
        double r = _sR.Value * (1 + 0.015 * Math.Sin(t * 1.8) + 0.035 * beat * m);
        double cy0 = r * 0.92;
        var main = new Point(cx, cy0);

        // the drop hanging from the edge: wide shoulders at the top, round below
        double tw = r * 2.6;
        var drop = new StreamGeometry();
        using (var g = drop.Open())
        {
            g.BeginFigure(new Point(cx - tw / 2 - 4, -1), true, true);
            g.BezierTo(new Point(cx - tw / 2 + r * 0.45, -1), new Point(cx - r, cy0 - r * 0.75), new Point(cx - r, cy0), true, true);
            g.ArcTo(new Point(cx + r, cy0), new Size(r, r), 0, false, SweepDirection.Counterclockwise, true, true);
            g.BezierTo(new Point(cx + r, cy0 - r * 0.75), new Point(cx + tw / 2 - r * 0.45, -1), new Point(cx + tw / 2 + 4, -1), true, true);
        }
        drop.Freeze();

        // the droplets: the cover (lower left), then each live thing (lower right, then further out), each joined by a neck
        var shapes = new GeometryGroup { FillRule = FillRule.Nonzero };
        shapes.Children.Add(drop);
        var spots = new List<(Point C, double R, double Show)>();
        double sway = Math.Sin(t * 1.2);
        if (m > 0.01)
        {
            double d = MercuryCoverGap(r) * m;
            spots.Add((new Point(cx - d - sway, cy0 + 12 * m + 1.5 * beat), (10 + 1.2 * beat) * m, m));
        }
        for (int i = 0; i < bits.Count; i++)
        {
            double s = _sItem[i].Value, d = MercuryBitGap(r, i) * s;
            double fit = Math.Max(12, TextWidth(bits[i].Text, 9.5) / 2 + 4.5); // the droplet is as wide as its words need
            spots.Add((new Point(cx + d + sway, cy0 + 10 * s + 0.8 * Math.Sin(t * 1.4 + i)), fit * s, s));
        }
        // each droplet hangs from the one before it on its side (the first from the drop itself)
        for (int k = 0; k < spots.Count; k++)
        {
            var (pc, pr, _) = spots[k];
            if (pr < 0.5) continue;
            shapes.Children.Add(new EllipseGeometry(pc, pr, pr));
            bool coverHere = m > 0.01 && k == 0, firstBit = k == (m > 0.01 ? 1 : 0);
            var (hc, hr) = coverHere || firstBit ? (main, r) : (spots[k - 1].C, spots[k - 1].R);
            if (Metaball(hc, hr, pc, pr) is { } neck) shapes.Children.Add(neck);
        }
        shapes.Freeze();
        var body = Piece(ref _sBody, () => new Path());
        body.Fill = Frozen(Color.FromRgb(0x04, 0x04, 0x06));
        body.Data = shapes;

        // a breath of light on its rim (Neon: cyan, sharper)
        var rim = Piece(ref _sRim, () => new Path { StrokeThickness = 1.2 });
        rim.Data = drop;
        rim.Stroke = Frozen(_cyber ? Color.FromArgb(0xB0, c.B.R, c.B.G, c.B.B) : Color.FromArgb((byte)(0x22 + 0x30 * pk), 0xFF, 0xFF, 0xFF));

        PlaceFace(cx, cy0 + 1);
        if (_face != null && _face.Visibility == Visibility.Hidden) _face.Visibility = Visibility.Visible;

        // what's in the droplets: the record, the times
        int p = 0;
        for (int k = 0; k < spots.Count; k++)
        {
            var (pc, pr, show) = spots[k];
            var (bead, thread, chip, text, _) = Pendant(p++);
            bead.Opacity = 0; thread.Opacity = 0;
            bool isCover = m > 0.01 && k == 0;
            if (isCover)
            {
                double cr = Math.Max(0, pr - 3.5);
                chip.Width = chip.Height = cr * 2; chip.Padding = new Thickness(0); chip.CornerRadius = new CornerRadius(cr);
                chip.Background = CoverBrush(c); chip.BorderThickness = new Thickness(0); text.Text = "";
                chip.RenderTransformOrigin = new Point(0.5, 0.5);
                chip.RenderTransform = new RotateTransform(_media.IsPlaying ? _sSpin : 0);
                Canvas.SetLeft(chip, pc.X - cr); Canvas.SetTop(chip, pc.Y - cr);
            }
            else
            {
                var b = bits[k - (m > 0.01 ? 1 : 0)];
                chip.Width = double.NaN; chip.Height = double.NaN; chip.Padding = new Thickness(0); chip.Background = null; chip.BorderThickness = new Thickness(0);
                chip.RenderTransform = new ScaleTransform(show, show); chip.RenderTransformOrigin = new Point(0.5, 0.5);
                text.Inlines.Clear();
                text.Text = b.Text;
                text.FontSize = 9.5;
                text.FontWeight = FontWeights.SemiBold;
                text.Foreground = Frozen(b.Color);
                chip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(chip, pc.X - chip.DesiredSize.Width / 2); Canvas.SetTop(chip, pc.Y - chip.DesiredSize.Height / 2);
            }
            chip.Opacity = Math.Clamp(show * 1.4 - 0.4, 0, 1);
        }
        HidePendantsFrom(p);
        if (_sTitle != null) _sTitle.Opacity = 0;
    }

    /// <summary>
    /// The liquid neck between two round drops (the "metaball" bridge): two curves that leave each circle and meet,
    /// thinning as they pull apart, gone once they're too far. Null when there's nothing to draw.
    /// </summary>
    static Geometry? Metaball(Point c1, double r1, Point c2, double r2, double handle = 2.4, double v = 0.5)
    {
        double d = (c2 - c1).Length;
        double maxDist = r1 + r2 * 2.5;
        if (r1 <= 0 || r2 <= 0 || d > maxDist || d <= Math.Abs(r1 - r2)) return null;
        double u1 = 0, u2 = 0;
        if (d < r1 + r2)
        {
            u1 = Math.Acos(Math.Clamp((r1 * r1 + d * d - r2 * r2) / (2 * r1 * d), -1, 1));
            u2 = Math.Acos(Math.Clamp((r2 * r2 + d * d - r1 * r1) / (2 * r2 * d), -1, 1));
        }
        double between = Math.Atan2(c2.Y - c1.Y, c2.X - c1.X);
        double maxSpread = Math.Acos(Math.Clamp((r1 - r2) / d, -1, 1));
        double a1 = between + u1 + (maxSpread - u1) * v, a2 = between - u1 - (maxSpread - u1) * v;
        double a3 = between + Math.PI - u2 - (Math.PI - u2 - maxSpread) * v, a4 = between - Math.PI + u2 + (Math.PI - u2 - maxSpread) * v;
        Point At(Point c, double r, double a) => new(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
        Point p1 = At(c1, r1, a1), p2 = At(c1, r1, a2), p3 = At(c2, r2, a3), p4 = At(c2, r2, a4);
        double total = r1 + r2;
        double d2 = Math.Min(v * handle, (p1 - p3).Length / total) * Math.Min(1, d * 2 / (r1 + r2));
        double h1r = r1 * d2, h2r = r2 * d2;
        Point h1 = At(p1, h1r, a1 - Math.PI / 2), h2 = At(p2, h1r, a2 + Math.PI / 2), h3 = At(p3, h2r, a3 + Math.PI / 2), h4 = At(p4, h2r, a4 - Math.PI / 2);
        var g = new StreamGeometry();
        using (var s = g.Open())
        {
            s.BeginFigure(p1, true, true);
            s.BezierTo(h1, h3, p3, true, true);
            s.LineTo(p4, true, true);
            s.BezierTo(h4, h2, p2, true, true);
        }
        g.Freeze();
        return g;
    }

    static double MercuryCoverGap(double r) => 22 + r * 0.9;
    static double MercuryBitGap(double r, int i) => 24 + r * 0.9 + i * 40;

    static double TextWidth(string s, double size)
    {
        var ft = new FormattedText(s, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), size, Brushes.White, 1.0);
        return ft.WidthIncludingTrailingWhitespace;
    }

    // ───────────── Eclipse ─────────────

    void DrawEclipse(double cx, bool music, bool slim, List<LiveBit> bits, (Color A, Color B) c)
    {
        double m = _sMusic.Value, pk = _sPeek.Value, call = _sCall.Value, t = _styleT;
        _sR.Target = slim ? 12 : (m > 0.01 || bits.Count > 0 ? 28 : 22) + pk * 2;
        double R = _sR.Value, cy0 = -R * 0.18;
        var center = new Point(cx, cy0);

        // the rim of light (a glow, then a fine line), the dark disc (Neon: a hexagon)
        Geometry Shape(double rad)
        {
            if (!_cyber) return new EllipseGeometry(center, rad, rad);
            var g = new StreamGeometry();
            using (var s = g.Open())
            {
                for (int i = 0; i < 6; i++)
                {
                    double a = Math.PI / 6 + i * Math.PI / 3;
                    var pt = new Point(cx + Math.Cos(a) * rad, cy0 + Math.Sin(a) * rad);
                    if (i == 0) s.BeginFigure(pt, true, true); else s.LineTo(pt, true, true);
                }
            }
            g.Freeze();
            return g;
        }
        double breath = m > 0.5 || bits.Count > 0 ? 0.45 : 0.38 + 0.14 * Math.Sin(t * 1.2);
        if (slim) breath *= 0.6;
        var glow = Piece(ref _sGlow, () => new Path { StrokeThickness = 4 });
        glow.Data = Shape(R + 2.5);
        glow.Stroke = Along(c.A, c.B, 0x55);
        glow.Opacity = breath + 0.3 * pk;
        var rim = Piece(ref _sRim, () => new Path { StrokeThickness = 1.3 });
        rim.Data = Shape(R + 2.5);
        rim.Stroke = Along(Lighten(c.A, 0.3), Lighten(c.B, 0.3), 0xD0);
        rim.Opacity = 0.5 + 0.5 * Math.Max(breath, pk);
        var disc = Piece(ref _sBody, () => new Path());
        disc.Data = Shape(R);
        disc.Fill = Frozen(Color.FromRgb(0x04, 0x04, 0x06));

        // with music: the record turning in the disc (the face its label), the rim a round sound meter
        var cover = Piece(ref _sCover, () => new Ellipse { RenderTransformOrigin = new Point(0.5, 0.5) });
        double rr = R * 0.7 * m;
        cover.Width = cover.Height = Math.Max(0.1, rr * 2);
        cover.Fill = CoverBrush(c);
        cover.RenderTransform = new RotateTransform(_sSpin);
        Canvas.SetLeft(cover, cx - rr); Canvas.SetTop(cover, cy0 + R * 0.26 - rr);
        cover.Opacity = m;
        var bars = Piece(ref _sBars, () => new Path { StrokeThickness = 1.8 });
        bars.StrokeStartLineCap = bars.StrokeEndLineCap = _cyber ? PenLineCap.Flat : PenLineCap.Round;
        bars.Stroke = Along(c.A, c.B);
        var bg = new GeometryGroup();
        if (m > 0.01)
        {
            const int n = 20;
            for (int i = 0; i < n; i++)
            {
                double a = Math.PI * (0.08 + 0.84 * i / (n - 1));
                int voice = (int)Math.Round(Math.Abs(i - (n - 1) / 2.0) / ((n - 1) / 2.0) * 4);
                double v = 0.2 + 0.8 * (_eqRunning ? _eqLevel[Math.Clamp(voice, 0, 4)] : 0.3 + 0.2 * Math.Sin(t * 4 + i)) * (0.75 + 0.25 * Math.Sin(t * 5 + i * 0.9));
                double r1 = R + 4.5, r2 = R + 4.5 + 7 * v * m;
                bg.Children.Add(new LineGeometry(new Point(cx + Math.Cos(a) * r1, cy0 + Math.Sin(a) * r1), new Point(cx + Math.Cos(a) * r2, cy0 + Math.Sin(a) * r2)));
            }
        }
        bg.Freeze();
        bars.Data = bg;
        bars.Opacity = m * 0.9;

        // a call (or another live thing): a green arc sweeping with the seconds, and its time under the moon
        var track = Piece(ref _sTrack, () => new Path { StrokeThickness = 1.8, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        var arc = Piece(ref _sArc, () => new Path { StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        var first = bits.FirstOrDefault();
        double show = bits.Count > 0 ? _sItem[0].Value : 0;
        if (first != null && show > 0.01)
        {
            double ar = R + 2.5; // on the rim itself
            double frac = first.Kind == LiveActivity.Call && _call is { } cl ? (DateTime.UtcNow - cl.Started).TotalSeconds % 60 / 60 : 0.62;
            // the track opens from the middle out as it appears; the bright part counts along it
            double mid = Math.PI / 2, half = Math.PI * 0.4 * show;
            double a0 = mid - half, a1 = a0 + 2 * half * Math.Max(0.02, frac);
            Geometry Arc(double from, double to)
            {
                var ag = new StreamGeometry();
                using (var s = ag.Open())
                {
                    s.BeginFigure(new Point(cx + Math.Cos(from) * ar, cy0 + Math.Sin(from) * ar), false, false);
                    s.ArcTo(new Point(cx + Math.Cos(to) * ar, cy0 + Math.Sin(to) * ar), new Size(ar, ar), 0, to - from > Math.PI, SweepDirection.Clockwise, true, false);
                }
                ag.Freeze();
                return ag;
            }
            track.Data = Arc(a0, mid + half);
            track.Stroke = Frozen(Color.FromArgb(0x40, first.Color.R, first.Color.G, first.Color.B));
            track.Opacity = show;
            arc.Data = Arc(a0, a1);
            arc.Stroke = Frozen(first.Color);
            arc.Opacity = show;
        }
        else arc.Opacity = track.Opacity = 0;

        // the face: in the disc at rest, the record's label with music
        _faceAway = Math.Clamp(m * 1.6, 0, 1); // with music the record has the whole disc: the face steps out, and comes back after
        PlaceFace(cx, cy0 + R * 0.52);

        int p = 0;
        for (int i = 0; i < bits.Count; i++)
        {
            var b = bits[i];
            var (bead, thread, chip, text, _) = Pendant(p++);
            bead.Opacity = 0; thread.Opacity = 0;
            chip.Width = double.NaN; chip.Height = 16; chip.Padding = new Thickness(6, 0, 7, 1); chip.CornerRadius = new CornerRadius(_cyber ? 2 : 8);
            chip.Background = Frozen(Color.FromArgb(0xF2, 0x05, 0x05, 0x07)); chip.BorderThickness = new Thickness(1);
            chip.BorderBrush = Frozen(Color.FromArgb(0x55, b.Color.R, b.Color.G, b.Color.B));
            text.Inlines.Clear();
            text.Inlines.Add(new System.Windows.Documents.Run(b.Glyph + " ") { FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 9.5 });
            text.Inlines.Add(new System.Windows.Documents.Run(b.Text));
            text.FontSize = 10;
            text.Foreground = Frozen(b.Color);
            chip.RenderTransform = new ScaleTransform(1, 1);
            chip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double x = cx + (i - (bits.Count - 1) / 2.0) * 58 - chip.DesiredSize.Width / 2;
            Canvas.SetLeft(chip, Math.Round(x)); Canvas.SetTop(chip, Math.Round(cy0 + R + (m > 0.01 ? 12 : 4) + 2 * (1 - _sItem[i].Value)));
            chip.Opacity = _sItem[i].Value;
        }
        HidePendantsFrom(p);
        if (_sTitle != null) _sTitle.Opacity = 0;
        if (_face != null && _face.Visibility == Visibility.Hidden) _face.Visibility = Visibility.Visible;
    }

    // ───────────── the chip on the Settings page ─────────────

    Button? _islandChip;

    /// <summary>"Island" on the Settings page, under Theme: the style now; a click moves to the next.</summary>
    void AddIslandCell(System.Windows.Controls.Primitives.UniformGrid grid)
    {
        var cell = new Grid { Margin = new Thickness(4, 0, 10, 0) };
        cell.Children.Add(Localized(new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 90, 0) }, "Island"));
        _islandChip = new Button { Height = 22, FontSize = 11, Padding = new Thickness(10, 0, 10, 0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        _islandChip.Style = (Style)FindResource("Capsule");
        _islandChip.Click += (_, _) =>
        {
            int i = Array.FindIndex(IslandStyles, s => s.Key == IslandStyle);
            SetIslandStyle(IslandStyles[(i + 1) % IslandStyles.Length].Key);
            FeelSound(Services.Sfx.Cue.Tap);
        };
        cell.Children.Add(_islandChip);
        grid.Children.Add(cell);
        RefreshIslandChip();
    }

    void RefreshIslandChip()
    {
        if (_islandChip != null) _islandChip.Content = Services.Loc.T(IslandStyles.First(s => s.Key == IslandStyle).Name) + "  ›";
    }
}
