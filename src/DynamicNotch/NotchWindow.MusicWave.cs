using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DynamicNotch;

/// <summary>
/// The music in the closed notch, in one of four styles (Preferences → Appearance, or the chip on the Settings page):
///  • Ribbon: two waves (a bright one in front, a softer one behind, a little out of step) with a glow between them,
///    pinned at the ends like a plucked string, flowing faster when the music is louder;
///  • Pillars: slim capsules growing up and down from the middle, mirrored, each with a soft halo;
///  • Bounce: five beads hopping on the beat, each leaving a faint trail down to the floor;
///  • Pulse: a core that swells with the sound, rings rippling out of it.
/// All in the cover's colours (Neon: angular, yellow to cyan; Dusk: pink to gold). They listen to the same five voices
/// the bars did (the body, the punches, the sparkle of the sound). Paused, each settles still. A few shapes redrawn 30
/// times a second, in a 26 × 16 px corner: nothing for any PC.
/// </summary>
public partial class NotchWindow
{
    Canvas? _wave;
    Path? _waveFront, _waveBack, _waveRibbon;
    double _wavePhase, _waveLoud;
    long _waveAt;
    Color _waveA = Colors.White, _waveB = Colors.White;
    const double WaveW = 26, WaveH = 16;

    /// <summary>The styles, in the order the chip and Preferences show them.</summary>
    public static readonly (string Key, string Name)[] WaveStyles = { ("ribbon", "Ribbon"), ("pillars", "Pillars"), ("bounce", "Bounce"), ("pulse", "Pulse") };

    string WaveStyle => WaveStyles.Any(s => s.Key == _settings.MusicWave) ? _settings.MusicWave : "ribbon";

    void BuildWave()
    {
        if (_wave != null || Eq.Parent is not Grid host) return;
        _wave = new Canvas
        {
            Width = WaveW, Height = WaveH, ClipToBounds = false, IsHitTestVisible = false,
            HorizontalAlignment = Eq.HorizontalAlignment, VerticalAlignment = Eq.VerticalAlignment, Margin = new Thickness(0, 0, 13, 1),
        };
        Grid.SetRow(_wave, Grid.GetRow(Eq)); Grid.SetColumn(_wave, Grid.GetColumn(Eq));
        _waveRibbon = new Path();
        _waveBack = new Path();
        _waveFront = new Path();
        _wave.Children.Add(_waveRibbon);
        _wave.Children.Add(_waveBack);
        _wave.Children.Add(_waveFront);
        host.Children.Insert(host.Children.IndexOf(Eq) + 1, _wave);
        Eq.Visibility = Visibility.Collapsed; // (the bars stay as the levels' keepers; the style shows them)
    }

    /// <summary>The cover's colours (Neon: yellow to cyan, square-ended; Dusk: pink to gold), in the chosen style.</summary>
    void PaintWave(Color a, Color b)
    {
        BuildWave();
        _waveA = a; _waveB = b;
        if (_waveFront == null || _waveBack == null || _waveRibbon == null) return;
        StyleWave(_waveFront, _waveBack, _waveRibbon, WaveStyle, a, b, _cyber);
        DrawWave();
    }

    /// <summary>A new style picked: the paint changes with it, at once.</summary>
    public void SetWaveStyle(string key)
    {
        _settings.MusicWave = key;
        _settings.Save();
        PaintWave(_waveA, _waveB);
        RefreshWaveChip();
    }

    /// <summary>How each of the three layers is drawn in a style: a line, a fill, how strong.</summary>
    static void StyleWave(Path front, Path back, Path ribbon, string style, Color a, Color b, bool angular)
    {
        Brush Along(byte alpha = 0xFF, double white = 0)
        {
            var g = new LinearGradientBrush(Color.FromArgb(alpha, Mix(a, Colors.White, white).R, Mix(a, Colors.White, white).G, Mix(a, Colors.White, white).B),
                Color.FromArgb(alpha, Mix(b, Colors.White, white).R, Mix(b, Colors.White, white).G, Mix(b, Colors.White, white).B), new Point(0, 0.5), new Point(1, 0.5));
            g.Freeze();
            return g;
        }
        var cap = angular ? PenLineCap.Square : PenLineCap.Round;
        var join = angular ? PenLineJoin.Miter : PenLineJoin.Round;
        foreach (var p in new[] { front, back, ribbon })
        {
            p.Stroke = null; p.Fill = null; p.StrokeThickness = 0; p.Opacity = 1;
            p.StrokeStartLineCap = p.StrokeEndLineCap = cap; p.StrokeLineJoin = join;
        }
        switch (style)
        {
            case "pillars":
                front.Fill = Along(white: 0.2);
                back.Fill = Along(0x60); back.Opacity = 0.45; // the halo round each pillar
                break;
            case "bounce":
                front.Fill = Along(white: 0.3);
                back.Fill = Along(0xA0); back.Opacity = 0.35; // the trails
                ribbon.Stroke = Along(0x70); ribbon.StrokeThickness = 0.8; ribbon.Opacity = 0.5; // the floor
                break;
            case "pulse":
                front.Fill = Along(white: 0.3);
                back.Stroke = Along(); back.StrokeThickness = 1.0; back.Opacity = 0.6; // the rings
                ribbon.Fill = Along(0x90); ribbon.Opacity = 0.3; // the soft halo round the core
                break;
            default: // ribbon
                front.Stroke = Along(white: 0.25); front.StrokeThickness = 1.8;
                back.Stroke = Along(); back.StrokeThickness = 1.1; back.Opacity = 0.55;
                ribbon.Fill = Along(0xC0); ribbon.Opacity = 0.32;
                break;
        }
    }

    /// <summary>Draws it from the five levels (moving: it goes on; resting: still).</summary>
    void DrawWave()
    {
        if (_waveFront == null || _waveBack == null || _waveRibbon == null) return;
        long now = Environment.TickCount64;
        double dt = _waveAt == 0 ? 0 : Math.Min(0.1, (now - _waveAt) / 1000.0);
        _waveAt = now;
        if (_eqRunning) _wavePhase += dt * (2.4 + 6.5 * _waveLoud); // it goes faster when the music is louder

        var (f, bk, rb) = WaveShapes(WaveStyle, i => _eqLevel[i], _eqLevel.Length, _wavePhase, _cyber);
        _waveFront.Data = f;
        _waveBack.Data = bk;
        _waveRibbon.Data = rb;
    }

    /// <summary>The three layers' shapes for a style, some levels and a phase.</summary>
    static (Geometry? Front, Geometry? Back, Geometry? Ribbon) WaveShapes(string style, Func<int, double> levelAt, int count, double phase, bool angular) => style switch
    {
        "pillars" => Pillars(levelAt, count, angular),
        "bounce" => Bounce(levelAt, count, phase, angular),
        "pulse" => Pulse(levelAt, count, phase, angular),
        _ => Ribbon(levelAt, count, phase, angular),
    };

    static (Geometry, Geometry, Geometry) Ribbon(Func<int, double> levelAt, int count, double phase, bool angular)
    {
        int n = angular ? 9 : 26; // Neon: fewer points, sharp corners
        double mid = WaveH / 2, room = WaveH / 2 - 1.4;
        var front = new Point[n];
        var back = new Point[n];
        for (int k = 0; k < n; k++)
        {
            double u = k / (double)(n - 1);
            double x = 0.9 + u * (WaveW - 1.8);
            double amp = Level(levelAt, count, u) * Math.Sin(Math.PI * u); // the ends stay pinned, like a plucked string
            if (angular) amp = Math.Round(amp * 5) / 5; // a VU meter's steps
            double y1 = mid - amp * room * Math.Sin(2 * Math.PI * u * 1.55 - phase);
            double y2 = mid - amp * room * 0.7 * Math.Sin(2 * Math.PI * u * 2.1 - phase * 1.3 + 1.9);
            front[k] = new Point(x, y1);
            back[k] = new Point(x, y2);
        }
        // the glow fills between the two waves
        var ribbon = new StreamGeometry();
        using (var c = ribbon.Open())
        {
            c.BeginFigure(front[0], true, true);
            c.PolyLineTo(front.Skip(1).ToList(), false, true);
            c.PolyLineTo(back.Reverse().ToList(), false, true);
        }
        ribbon.Freeze();
        return (Line(front), Line(back), ribbon);
    }

    /// <summary>Six slim capsules, mirrored round the middle line; a wider, fainter one behind each (its halo).</summary>
    static (Geometry, Geometry, Geometry?) Pillars(Func<int, double> levelAt, int count, bool angular)
    {
        const int n = 6;
        double w = 2.4, gap = (WaveW - n * w) / (n - 1), mid = WaveH / 2;
        var pillars = new GeometryGroup();
        var halos = new GeometryGroup();
        for (int k = 0; k < n; k++)
        {
            double u = k / (double)(n - 1);
            double lv = Level(levelAt, count, u) * (0.72 + 0.28 * Math.Sin(Math.PI * (0.15 + u * 0.7))); // the middle stands a little taller
            if (angular) lv = Math.Ceiling(lv * 5) / 5;
            double h = Math.Max(w, lv * WaveH);
            double x = k * (w + gap);
            double r = angular ? 0 : w / 2;
            pillars.Children.Add(new RectangleGeometry(new Rect(x, mid - h / 2, w, h), r, r));
            halos.Children.Add(new RectangleGeometry(new Rect(x - 0.9, mid - h / 2 - 1.2, w + 1.8, h + 2.4), r + 0.9, r + 0.9));
        }
        pillars.Freeze(); halos.Freeze();
        return (pillars, halos, null);
    }

    /// <summary>Five beads hopping on the beat (lower when quiet), each with a faint trail down to the floor line.</summary>
    static (Geometry, Geometry, Geometry) Bounce(Func<int, double> levelAt, int count, double phase, bool angular)
    {
        const int n = 5;
        double d = 3.4, floor = WaveH - 1.6, room = WaveH - d - 2.6;
        double gap = (WaveW - n * d) / (n - 1);
        var beads = new GeometryGroup();
        var trails = new GeometryGroup();
        for (int k = 0; k < n; k++)
        {
            double u = k / (double)(n - 1);
            // the level, with a little hop of its own so quiet passages still bounce a bit
            double hop = 0.08 * Math.Abs(Math.Sin(phase * 1.3 + k * 1.2));
            double lv = Math.Clamp(Level(levelAt, count, u) - 0.12 + hop, 0, 1);
            if (angular) lv = Math.Round(lv * 5) / 5;
            double x = k * (d + gap);
            double y = floor - d / 2 - lv * room;
            var bead = new Rect(x, y - d / 2, d, d);
            beads.Children.Add(angular ? new RectangleGeometry(bead) : new EllipseGeometry(bead));
            double tw = 1.2;
            if (floor - (y + d / 2) > 0.6)
                trails.Children.Add(new RectangleGeometry(new Rect(x + d / 2 - tw / 2, y, tw, floor - y), angular ? 0 : tw / 2, angular ? 0 : tw / 2));
        }
        beads.Freeze(); trails.Freeze();
        var line = new LineGeometry(new Point(0, floor + 0.6), new Point(WaveW, floor + 0.6)); line.Freeze();
        return (beads, trails, line);
    }

    /// <summary>A core that swells with the sound, two rings rippling out of it (faster when it's louder).</summary>
    static (Geometry, Geometry, Geometry) Pulse(Func<int, double> levelAt, int count, double phase, bool angular)
    {
        double loud = 0;
        for (int i = 0; i < count; i++) loud += levelAt(i);
        loud = Math.Clamp(loud / count, 0, 1);
        var c = new Point(WaveW / 2, WaveH / 2);
        double core = 1.8 + loud * 2.6;
        Geometry Shape(double r) => angular ? new RectangleGeometry(new Rect(c.X - r, c.Y - r, 2 * r, 2 * r)) : new EllipseGeometry(c, r, r);
        var rings = new GeometryGroup();
        for (int k = 0; k < 2; k++)
        {
            double t = (phase * 0.32 + k * 0.5) % 1; // each ring grows outward, then starts again from the core
            double r = core + 0.8 + t * (WaveH / 2 - core) * (0.7 + 0.5 * loud);
            if (r < WaveH / 2 + 1) rings.Children.Add(Shape(r));
        }
        var coreG = Shape(core);
        var halo = Shape(core + 2.2);
        rings.Freeze(); coreG.Freeze(); halo.Freeze();
        return (coreG, rings, halo);
    }

    static Geometry Line(Point[] pts)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(pts[0], false, false);
            c.PolyLineTo(pts.Skip(1).ToList(), true, true);
        }
        g.Freeze();
        return g;
    }

    /// <summary>How tall it swells at a place along it (0..1): the five voices, blended smoothly between.</summary>
    static double Level(Func<int, double> levelAt, int count, double u)
    {
        double f = u * (count - 1);
        int i = Math.Min(count - 2, (int)f);
        double t = f - i, s = t * t * (3 - 2 * t); // a smooth step between neighbours
        double v = levelAt(i) + (levelAt(i + 1) - levelAt(i)) * s;
        return Math.Clamp(0.12 + v * 0.95, 0, 1);
    }

    // ───────────── the chip on the Settings page ─────────────

    Button? _waveChip;

    /// <summary>"Music" on the Settings page: the style shown now; a click moves to the next (and last, Still).</summary>
    void AddWaveCell(UniformGrid grid)
    {
        var cell = new Grid { Margin = new Thickness(4, 0, 10, 0) };
        cell.Children.Add(Localized(new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 90, 0) }, "Music"));
        _waveChip = new Button { Height = 22, FontSize = 11, Padding = new Thickness(10, 0, 10, 0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        _waveChip.Style = (Style)FindResource("Capsule");
        _waveChip.Click += (_, _) =>
        {
            // Ribbon › Pillars › Bounce › Pulse › Still › Ribbon
            if (!_settings.AnimatedVisualizer) { _settings.AnimatedVisualizer = true; SetWaveStyle(WaveStyles[0].Key); }
            else
            {
                int i = Array.FindIndex(WaveStyles, s => s.Key == WaveStyle);
                if (i == WaveStyles.Length - 1) { _settings.AnimatedVisualizer = false; _settings.Save(); RefreshWaveChip(); }
                else SetWaveStyle(WaveStyles[i + 1].Key);
            }
            _eqRunning = !EqWanted; UpdateEqualizer(); // (it starts or settles with the choice)
            FeelSound(Services.Sfx.Cue.Tap);
        };
        cell.Children.Add(_waveChip);
        grid.Children.Add(cell);
        RefreshWaveChip();
    }

    void RefreshWaveChip()
    {
        if (_waveChip == null) return;
        string name = !_settings.AnimatedVisualizer ? "Still" : WaveStyles.First(s => s.Key == WaveStyle).Name;
        _waveChip.Content = Services.Loc.T(name) + "  ›";
    }

    /// <summary>For a look without music (DYNAMICNOTCH_WAVESHEET=file.png): each style at a few moments, big, on a dark pill.</summary>
    public static void RenderWaveSheet(string file)
    {
        var looks = new (Color A, Color B, bool Angular)[]
        {
            (Color.FromRgb(0xFF, 0x5E, 0x8A), Color.FromRgb(0x7A, 0x6C, 0xFF), false),
            (Color.FromRgb(0xFE, 0xE8, 0x01), Color.FromRgb(0x00, 0xF0, 0xFF), true),
        };
        double[][] moments = { new[] { .28, .42, .55, .42, .28 }, new[] { .5, .8, 1, .7, .45 }, new[] { .9, .4, .7, .95, .3 }, new[] { .2, .3, .35, .25, .15 } };
        const int k = 6;
        var grid = new UniformGrid { Columns = moments.Length, Background = new SolidColorBrush(Color.FromRgb(0x0C, 0x0C, 0x10)) };
        foreach (var (style, _) in WaveStyles)
            foreach (var look in looks)
                for (int m = 0; m < moments.Length; m++)
                {
                    var lv = moments[m];
                    var (f, bk, rb) = WaveShapes(style, i => lv[i], lv.Length, m * 1.1, look.Angular);
                    var front = new Path { Data = f }; var back = new Path { Data = bk }; var ribbon = new Path { Data = rb };
                    StyleWave(front, back, ribbon, style, look.A, look.B, look.Angular);
                    var c = new Canvas { Width = WaveW, Height = WaveH, Margin = new Thickness(4) };
                    c.Children.Add(ribbon); c.Children.Add(back); c.Children.Add(front);
                    grid.Children.Add(new Viewbox { Width = (WaveW + 8) * k, Height = (WaveH + 8) * k, Child = c });
                }
        grid.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        grid.Arrange(new Rect(grid.DesiredSize));
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)grid.DesiredSize.Width, (int)grid.DesiredSize.Height, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(grid);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = System.IO.File.Create(file);
        enc.Save(fs);
    }
}
