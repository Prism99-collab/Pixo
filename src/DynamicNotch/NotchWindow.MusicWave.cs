using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DynamicNotch;

/// <summary>
/// The music in the closed notch, in one of eight styles (Preferences → Appearance, or the chip on the Settings page):
///  • Ribbon: two waves (a bright one in front, a softer one behind, a little out of step) with a glow between them,
///    pinned at the ends like a plucked string, flowing faster when the music is louder;
///  • Pillars: slim capsules growing up and down from the middle, mirrored, each with a soft halo;
///  • Bounce: five beads hopping on the beat, each leaving a faint trail down to the floor;
///  • Pulse: a core that swells with the sound, rings rippling out of it;
///  • Mirror: slim bars standing on a line, their reflection fading below it;
///  • Silk: fine strands weaving round each other, gathered at the ends, spreading with the sound;
///  • Spikes: a bright needle waveform, mirrored on a thin line, flickering with the detail of the sound;
///  • Dots: a little dot matrix, mirrored on a dotted line, warmer towards the middle.
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
    public static readonly (string Key, string Name)[] WaveStyles = { ("ribbon", "Ribbon"), ("pillars", "Pillars"), ("bounce", "Bounce"), ("pulse", "Pulse"), ("mirror", "Mirror"), ("silk", "Silk"), ("spikes", "Spikes"), ("dots", "Dots") };

    string WaveStyle => KnownWave(_settings.MusicWave) ? _settings.MusicWave : "ribbon";

    // (a plain loop: it's asked every meter step, and a lambda over the list made new objects each time)
    static bool KnownWave(string? key)
    {
        foreach (var s in WaveStyles) if (s.Key == key) return true;
        return false;
    }

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
            p.Stroke = null; p.Fill = null; p.StrokeThickness = 0; p.Opacity = 1; p.OpacityMask = null;
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
            case "mirror":
                front.Fill = Along(white: 0.15);
                ribbon.Fill = Along(0x80); ribbon.Opacity = 0.28; // the glow round each bar
                // the reflection, fading downward (a fill of its own: an opacity mask cost a layer every frame)
                var mid = Mix(a, b, 0.5);
                var fade = new LinearGradientBrush(Color.FromArgb(0x70, mid.R, mid.G, mid.B), Color.FromArgb(0, mid.R, mid.G, mid.B), new Point(0, MirrorLine), new Point(0, WaveH)) { MappingMode = BrushMappingMode.Absolute };
                fade.Freeze();
                back.Fill = fade;
                break;
            case "silk":
                front.Stroke = Along(white: 0.2); front.StrokeThickness = 0.75;
                back.Stroke = Along(); back.StrokeThickness = 0.55; back.Opacity = 0.5;
                ribbon.Fill = Along(0x90); ribbon.Opacity = 0.16; // the sheen between the outer strands
                break;
            case "spikes":
                // nearly white, only touched by the cover's colours: the sharp one
                front.Fill = Along(white: 0.78);
                back.Fill = Along(0xA0, 0.4); back.Opacity = 0.3; // the soft glow behind the needles
                ribbon.Stroke = Along(0xC0, 0.6); ribbon.StrokeThickness = 0.5; ribbon.Opacity = 0.55; // the line through
                break;
            case "dots":
            {
                // the cover's first colour at the ends, its second (warmer, brighter) in the middle
                var lit = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(WaveW, 0), MappingMode = BrushMappingMode.Absolute }; // (across the whole width, whichever dots are lit)
                lit.GradientStops.Add(new GradientStop(Mix(a, Colors.White, 0.1), 0));
                lit.GradientStops.Add(new GradientStop(Mix(b, Colors.White, 0.2), 0.5));
                lit.GradientStops.Add(new GradientStop(Mix(a, Colors.White, 0.1), 1));
                lit.Freeze();
                front.Fill = lit;
                back.Fill = Along(0xFF, 0.3); back.Opacity = 0.08; // the dots not lit: the matrix, barely there
                ribbon.Fill = lit; ribbon.Opacity = 0.5; // the dotted middle line
                break;
            }
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

        // the same picture as the last one drawn (the levels moved less than a hundredth, a sixth of a pixel at most,
        // and the style doesn't flow, or the phase stood still): nothing new to draw
        string style = WaveStyle;
        bool flows = style is not ("pillars" or "mirror" or "dots");
        if (style == _waveStyleDrawn && _cyber == _waveAngularDrawn && (!flows || _wavePhase == _wavePhaseDrawn))
        {
            bool moved = false;
            for (int i = 0; i < _eqLevel.Length && !moved; i++) moved = Math.Abs(_eqLevel[i] - _waveDrawn[i]) >= 0.01;
            if (!moved) return;
        }
        _waveStyleDrawn = style; _waveAngularDrawn = _cyber; _wavePhaseDrawn = _wavePhase;
        Array.Copy(_eqLevel, _waveDrawn, Math.Min(_eqLevel.Length, _waveDrawn.Length));

        _waveLevelAt ??= i => _eqLevel[i]; // (made once: a new one each frame was one more object 30 times a second)
        var (f, bk, rb) = WaveShapes(style, _waveLevelAt, _eqLevel.Length, _wavePhase, _cyber);
        _waveFront.Data = f;
        _waveBack.Data = bk;
        _waveRibbon.Data = rb;
    }

    // what the wave was last drawn from
    readonly double[] _waveDrawn = new double[5];
    double _wavePhaseDrawn = double.NaN;
    string? _waveStyleDrawn;
    bool _waveAngularDrawn;
    Func<int, double>? _waveLevelAt;

    /// <summary>The three layers' shapes for a style, some levels and a phase.</summary>
    static (Geometry? Front, Geometry? Back, Geometry? Ribbon) WaveShapes(string style, Func<int, double> levelAt, int count, double phase, bool angular) => style switch
    {
        "pillars" => Pillars(levelAt, count, angular),
        "bounce" => Bounce(levelAt, count, phase, angular),
        "pulse" => Pulse(levelAt, count, phase, angular),
        "mirror" => Mirror(levelAt, count, angular),
        "silk" => Silk(levelAt, count, phase, angular),
        "spikes" => Spikes(levelAt, count, phase, angular),
        "dots" => DotMatrix(levelAt, count, angular),
        _ => Ribbon(levelAt, count, phase, angular),
    };

    static (Geometry, Geometry, Geometry) Ribbon(Func<int, double> levelAt, int count, double phase, bool angular)
    {
        int n = angular ? 9 : 26; // Neon: fewer points, sharp corners
        double mid = WaveH / 2, room = WaveH / 2 - 1.4;
        var front = WaveBuf(ref _bufFront, n);
        var back = WaveBuf(ref _bufBack, n);
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
        using (var c = ribbon.Open()) Band(c, front, back);
        ribbon.Freeze();
        return (Line(front), Line(back), ribbon);
    }

    // the points of a frame, kept between frames (the meter draws 30 times a second); one set per thread, so the
    // style sheet drawn elsewhere never shares them. The shapes copy the points, so the next frame can reuse them.
    [ThreadStatic] static Point[]? _bufFront, _bufBack, _bufTop, _bufBottom;
    [ThreadStatic] static Point[][]? _bufStrands;

    static Point[] WaveBuf(ref Point[]? buf, int n) => buf != null && buf.Length == n ? buf : buf = new Point[n];

    /// <summary>The filled band between two lines: along the first, back along the second (no copies of either).</summary>
    static void Band(StreamGeometryContext c, Point[] along, Point[] back)
    {
        c.BeginFigure(along[0], true, true);
        c.PolyLineTo(new ArraySegment<Point>(along, 1, along.Length - 1), false, true);
        for (int k = back.Length - 1; k >= 0; k--) c.LineTo(back[k], false, true);
    }

    /// <summary>Six slim capsules, mirrored round the middle line; a wider, fainter one behind each (its halo).</summary>
    static (Geometry, Geometry, Geometry?) Pillars(Func<int, double> levelAt, int count, bool angular)
    {
        const int n = 6;
        double w = 2.4, gap = (WaveW - n * w) / (n - 1), mid = WaveH / 2;
        // (one shape for the pillars and one for the halos, not a dozen little ones in groups: the same outlines)
        var pillars = new StreamGeometry();
        var halos = new StreamGeometry();
        using (var pc = pillars.Open())
        using (var hc = halos.Open())
            for (int k = 0; k < n; k++)
            {
                double u = k / (double)(n - 1);
                double lv = Level(levelAt, count, u) * (0.72 + 0.28 * Math.Sin(Math.PI * (0.15 + u * 0.7))); // the middle stands a little taller
                if (angular) lv = Math.Ceiling(lv * 5) / 5;
                double h = Math.Max(w, lv * WaveH);
                double x = k * (w + gap);
                double r = angular ? 0 : w / 2;
                RoundRect(pc, new Rect(x, mid - h / 2, w, h), r);
                RoundRect(hc, new Rect(x - 0.9, mid - h / 2 - 1.2, w + 1.8, h + 2.4), r + 0.9);
            }
        pillars.Freeze(); halos.Freeze();
        return (pillars, halos, null);
    }

    /// <summary>
    /// A rounded rectangle into a shape being drawn, the way a RectangleGeometry draws it (each corner's radius at most
    /// half the side), so many of them make one shape instead of one each.
    /// </summary>
    static void RoundRect(StreamGeometryContext c, Rect b, double radius)
    {
        double rx = Math.Min(radius, b.Width / 2), ry = Math.Min(radius, b.Height / 2);
        if (rx <= 0 || ry <= 0)
        {
            c.BeginFigure(b.TopLeft, true, true);
            c.LineTo(b.TopRight, false, false);
            c.LineTo(b.BottomRight, false, false);
            c.LineTo(b.BottomLeft, false, false);
            return;
        }
        var corner = new Size(rx, ry);
        c.BeginFigure(new Point(b.Left + rx, b.Top), true, true);
        c.LineTo(new Point(b.Right - rx, b.Top), false, false);
        c.ArcTo(new Point(b.Right, b.Top + ry), corner, 0, false, SweepDirection.Clockwise, false, false);
        c.LineTo(new Point(b.Right, b.Bottom - ry), false, false);
        c.ArcTo(new Point(b.Right - rx, b.Bottom), corner, 0, false, SweepDirection.Clockwise, false, false);
        c.LineTo(new Point(b.Left + rx, b.Bottom), false, false);
        c.ArcTo(new Point(b.Left, b.Bottom - ry), corner, 0, false, SweepDirection.Clockwise, false, false);
        c.LineTo(new Point(b.Left, b.Top + ry), false, false);
        c.ArcTo(new Point(b.Left + rx, b.Top), corner, 0, false, SweepDirection.Clockwise, false, false);
    }

    /// <summary>Five beads hopping on the beat (lower when quiet), each with a faint trail down to the floor line.</summary>
    static (Geometry, Geometry, Geometry) Bounce(Func<int, double> levelAt, int count, double phase, bool angular)
    {
        const int n = 5;
        double d = 3.4, floor = WaveH - 1.6, room = WaveH - d - 2.6;
        double gap = (WaveW - n * d) / (n - 1);
        // (one shape for the beads, one for the trails, as Pillars)
        var beads = new StreamGeometry();
        var trails = new StreamGeometry();
        using (var bc = beads.Open())
        using (var tc = trails.Open())
            for (int k = 0; k < n; k++)
            {
                double u = k / (double)(n - 1);
                // the level, with a little hop of its own so quiet passages still bounce a bit
                double hop = 0.08 * Math.Abs(Math.Sin(phase * 1.3 + k * 1.2));
                double lv = Math.Clamp(Level(levelAt, count, u) - 0.12 + hop, 0, 1);
                if (angular) lv = Math.Round(lv * 5) / 5;
                double x = k * (d + gap);
                double y = floor - d / 2 - lv * room;
                // a round bead is a rectangle rounded all the way (Neon: square)
                RoundRect(bc, new Rect(x, y - d / 2, d, d), angular ? 0 : d / 2);
                double tw = 1.2;
                if (floor - (y + d / 2) > 0.6)
                    RoundRect(tc, new Rect(x + d / 2 - tw / 2, y, tw, floor - y), angular ? 0 : tw / 2);
            }
        beads.Freeze(); trails.Freeze();
        return (beads, trails, BounceFloor);
    }

    // the floor never moves: drawn once
    static readonly Geometry BounceFloor = FrozenLine(new Point(0, WaveH - 1.6 + 0.6), new Point(WaveW, WaveH - 1.6 + 0.6));
    static readonly Geometry SpikesLine = FrozenLine(new Point(0, WaveH / 2), new Point(WaveW, WaveH / 2));

    static Geometry FrozenLine(Point a, Point b) { var g = new LineGeometry(a, b); g.Freeze(); return g; }

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

    /// <summary>Where Mirror's bars stand (a little below the middle: the reflection is shorter than the bars).</summary>
    const double MirrorLine = 10.4;

    /// <summary>Nine slim bars standing on a line, a softer glow round each, their reflection below (fading, Neon: stepped).</summary>
    static (Geometry, Geometry, Geometry) Mirror(Func<int, double> levelAt, int count, bool angular)
    {
        const int n = 9;
        double w = 1.6, gap = (WaveW - n * w) / (n - 1), up = MirrorLine - 0.6, down = WaveH - MirrorLine - 0.4;
        // (one shape each for the bars, their glows and their reflections, as Pillars)
        var bars = new StreamGeometry();
        var glows = new StreamGeometry();
        var shadows = new StreamGeometry();
        using (var bc = bars.Open())
        using (var gc = glows.Open())
        using (var sc = shadows.Open())
            for (int k = 0; k < n; k++)
            {
                double u = k / (double)(n - 1);
                double lv = Level(levelAt, count, u) * (0.62 + 0.38 * Math.Sin(Math.PI * u)); // the middle stands taller
                if (angular) lv = Math.Ceiling(lv * 6) / 6;
                double h = Math.Max(w, lv * up);
                double x = k * (w + gap);
                double r = angular ? 0 : w / 2;
                RoundRect(bc, new Rect(x, MirrorLine - h, w, h), r);
                RoundRect(gc, new Rect(x - 0.7, MirrorLine - h - 0.8, w + 1.4, h + 0.8), r + 0.7);
                double s = Math.Max(0.6, h / up * down * 0.9);
                RoundRect(sc, new Rect(x, MirrorLine + 0.6, w, s), r);
            }
        bars.Freeze(); glows.Freeze(); shadows.Freeze();
        return (bars, shadows, glows);
    }

    /// <summary>
    /// Five fine strands, each a slow wave of its own (a little out of step with the others), gathered at the ends and
    /// spreading with the sound, so they weave round each other like silk; a faint sheen between the outermost ones.
    /// </summary>
    static (Geometry, Geometry, Geometry) Silk(Func<int, double> levelAt, int count, double phase, bool angular)
    {
        const int strands = 5;
        int n = angular ? 11 : 30;
        double mid = WaveH / 2, room = WaveH / 2 - 1.2;
        var lines = _bufStrands ??= new Point[strands][];
        for (int s = 0; s < strands; s++) if (lines[s] == null || lines[s].Length != n) lines[s] = new Point[n];
        var top = WaveBuf(ref _bufTop, n);
        var bottom = WaveBuf(ref _bufBottom, n);
        for (int k = 0; k < n; k++)
        {
            double u = k / (double)(n - 1);
            double x = 0.6 + u * (WaveW - 1.2);
            double amp = Level(levelAt, count, u) * Math.Pow(Math.Sin(Math.PI * u), 1.3);
            if (angular) amp = Math.Round(amp * 5) / 5;
            double hi = mid, lo = mid;
            for (int s = 0; s < strands; s++)
            {
                double t = s / (double)(strands - 1) - 0.5; // -0.5 .. 0.5
                double y = mid - amp * room * (0.55 + 0.45 * Math.Cos(t * 2.2)) * Math.Sin(2 * Math.PI * u * (1.15 + 0.18 * t) - phase * (0.8 + 0.25 * t) + t * 2.6);
                lines[s][k] = new Point(x, y);
                hi = Math.Min(hi, y); lo = Math.Max(lo, y);
            }
            top[k] = new Point(x, hi);
            bottom[k] = new Point(x, lo);
        }
        // the brighter strands in front (the middle and both outer ones), the two others softer behind
        var front = new GeometryGroup();
        var back = new GeometryGroup();
        for (int s = 0; s < strands; s++) (s % 2 == 0 ? front : back).Children.Add(Line(lines[s]));
        front.Freeze(); back.Freeze();
        var sheen = new StreamGeometry();
        using (var c = sheen.Open()) Band(c, top, bottom);
        sheen.Freeze();
        return (front, back, sheen);
    }

    /// <summary>
    /// Thin needles, mirrored round a line through the middle: each as tall as the sound there, flickering with its
    /// detail (the needles never all agree, like a real waveform). A soft glow behind the tallest.
    /// </summary>
    static (Geometry, Geometry, Geometry) Spikes(Func<int, double> levelAt, int count, double phase, bool angular)
    {
        const int n = 15;
        double mid = WaveH / 2, room = WaveH / 2 - 0.5, pitch = (WaveW - 1.4) / (n - 1), w = 1.15;
        var needles = new StreamGeometry();
        var glow = new StreamGeometry();
        using (var c = needles.Open())
        using (var g = glow.Open())
        {
            for (int k = 0; k < n; k++)
            {
                double u = k / (double)(n - 1);
                double x = 0.7 + k * pitch;
                // the detail: two slow waves of its own per needle, so the shape keeps changing but never jumps
                double detail = 0.5 + 0.5 * Math.Sin(phase * 1.7 + k * 1.91) * Math.Sin(phase * 0.93 + k * 0.77);
                double lv = Level(levelAt, count, u) * (0.35 + 0.65 * detail) * (0.55 + 0.45 * Math.Sin(Math.PI * u));
                if (angular) lv = Math.Round(lv * 6) / 6;
                double h = Math.Max(0.5, lv * room);
                // (point by point: no little array for each needle)
                c.BeginFigure(new Point(x - w / 2, mid), true, true);
                c.LineTo(new Point(x, mid - h), true, false);
                c.LineTo(new Point(x + w / 2, mid), true, false);
                c.LineTo(new Point(x, mid + h), true, false);
                if (h > room * 0.45)
                {
                    double gw = w * 2.4;
                    g.BeginFigure(new Point(x - gw / 2, mid), true, true);
                    g.LineTo(new Point(x, mid - h - 0.6), true, false);
                    g.LineTo(new Point(x + gw / 2, mid), true, false);
                    g.LineTo(new Point(x, mid + h + 0.6), true, false);
                }
            }
        }
        needles.Freeze(); glow.Freeze();
        return (needles, glow, SpikesLine);
    }

    /// <summary>
    /// A little dot matrix: ten columns, lit from the dotted middle line outward (mirrored) as far as the sound goes there,
    /// the last dot shrinking in between so it moves smoothly; the unlit dots barely there behind. Neon: square dots.
    /// </summary>
    const int DotCols = 10, DotRows = 3; // rows on each side of the middle line
    const double DotSize = 1.55, DotPitchY = 2.3;
    static double DotX(int k) => DotSize / 2 + k * (WaveW - DotSize) / (DotCols - 1);
    static readonly Geometry[] DotLines = { DotLine(false), DotLine(true) }; // the dotted middle line never changes: drawn once

    static Geometry DotLine(bool angular)
    {
        var g = new StreamGeometry();
        using (var c = g.Open()) for (int k = 0; k < DotCols; k++) Dot(c, DotX(k), WaveH / 2, DotSize * 0.8, angular);
        g.Freeze();
        return g;
    }

    /// <summary>One dot into a shape being drawn (a circle, or Neon's square).</summary>
    static void Dot(StreamGeometryContext c, double cx, double cy, double size, bool square)
    {
        double r = size / 2;
        if (square)
        {
            c.BeginFigure(new Point(cx - r, cy - r), true, true);
            c.LineTo(new Point(cx + r, cy - r), false, false);
            c.LineTo(new Point(cx + r, cy + r), false, false);
            c.LineTo(new Point(cx - r, cy + r), false, false);
            return;
        }
        c.BeginFigure(new Point(cx - r, cy), true, true);
        c.ArcTo(new Point(cx + r, cy), new Size(r, r), 0, false, SweepDirection.Clockwise, false, false);
        c.ArcTo(new Point(cx - r, cy), new Size(r, r), 0, false, SweepDirection.Clockwise, false, false);
    }

    /// <summary>Ten columns lit from the middle line outward (mirrored) as far as the sound goes: two shapes a frame, the line kept.</summary>
    static (Geometry, Geometry, Geometry) DotMatrix(Func<int, double> levelAt, int count, bool angular)
    {
        double mid = WaveH / 2;
        var lit = new StreamGeometry();
        var unlit = new StreamGeometry();
        using (var on = lit.Open())
        using (var off = unlit.Open())
            for (int k = 0; k < DotCols; k++)
            {
                double u = k / (double)(DotCols - 1), x = DotX(k);
                double lv = Level(levelAt, count, u) * (0.6 + 0.4 * Math.Sin(Math.PI * u));
                double up = Math.Clamp((lv - 0.1) / 0.9, 0, 1) * DotRows; // how many dots up (and down), with a fraction
                if (angular) up = Math.Round(up);
                for (int r = 1; r <= DotRows; r++)
                {
                    double fill = Math.Clamp(up - (r - 1), 0, 1);
                    for (int side = -1; side <= 1; side += 2)
                    {
                        double y = mid + side * r * DotPitchY;
                        if (fill >= 0.25) Dot(on, x, y, DotSize * (0.55 + 0.45 * fill), angular);
                        if (fill < 1) Dot(off, x, y, DotSize, angular);
                    }
                }
            }
        lit.Freeze(); unlit.Freeze();
        return (lit, unlit, DotLines[angular ? 1 : 0]);
    }

    static Geometry Line(Point[] pts)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(pts[0], false, false);
            c.PolyLineTo(new ArraySegment<Point>(pts, 1, pts.Length - 1), true, true); // (no copy of the points)
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
            // Ribbon › Pillars › Bounce › Pulse › Mirror › Silk › Spikes › Dots › Still › Ribbon
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
