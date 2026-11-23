using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using DynamicNotch.Controls;

namespace DynamicNotch;

/// <summary>
/// Crystal: the inside of an amethyst geode. A body of deep violet, lit from below by the stone's own glow; its rim
/// splits light like a cut gem (lilac, violet, periwinkle, ice). Open, you look into the geode: wide faceted planes
/// of crystal catching light in turn, clusters of amethyst points growing from the lower corners, glitter on the
/// facets, and now and then a ray of light refracting across it in a thin rainbow. Every colour of the notch is set
/// as a gem: sapphire, emerald, ruby, topaz, aquamarine, amethyst. All of it is drawn here (no picture).
/// </summary>
public partial class NotchWindow
{
    static readonly Color GemAmethyst = Color.FromRgb(0xB9, 0x8C, 0xFF), GemLilac = Color.FromRgb(0xD9, 0xC4, 0xFF), GemViolet = Color.FromRgb(0x8A, 0x5C, 0xF0),
        GemSapphire = Color.FromRgb(0x77, 0x95, 0xFF), GemEmerald = Color.FromRgb(0x52, 0xD6, 0xA4), GemRuby = Color.FromRgb(0xFF, 0x5E, 0x8E),
        GemTopaz = Color.FromRgb(0xFF, 0xC6, 0x6E), GemAqua = Color.FromRgb(0x7F, 0xE3, 0xF0), GemRose = Color.FromRgb(0xFF, 0x8F, 0xC4),
        GemIce = Color.FromRgb(0xF1, 0xEA, 0xFF), GemNight = Color.FromRgb(0x0B, 0x06, 0x14);

    static readonly Dictionary<int, Color> GemPalette = new()
    {
        [0x30D158] = GemEmerald, [0x34C759] = GemEmerald, [0x30D5C8] = GemAqua,
        [0x0A84FF] = GemSapphire, [0x64D2FF] = GemAqua, [0x5E5CE6] = Color.FromRgb(0x96, 0x86, 0xFF),
        [0xFF9F0A] = GemTopaz, [0xFFD60A] = Color.FromRgb(0xF6, 0xD9, 0x7A), [0xBF5AF2] = GemAmethyst,
        [0xFF453A] = GemRuby, [0xFF375F] = GemRose,
    };

    /// <summary>System colours as gems; greys (cards, tiles) become panes of lilac-tinted crystal; menus solid violet night.</summary>
    static Color CrystalMap(Color c)
    {
        if (GemPalette.TryGetValue(c.R << 16 | c.G << 8 | c.B, out var m)) return Color.FromArgb(c.A, m.R, m.G, m.B);
        int max = Math.Max(c.R, Math.Max(c.G, c.B)), min = Math.Min(c.R, Math.Min(c.G, c.B));
        if (max - min > 6 || max < 0x10 || max > 0x44) return c;
        if (c.A is >= 0xE0 and < 0xFF) return Color.FromArgb(0xF4, 0x16, 0x0D, 0x26);
        return Color.FromArgb((byte)(c.A / 255.0 * (0x14 + (max - 0x10) * 0.8)), 0xE4, 0xD6, 0xFF);
    }

    static Look Crystal() => new()
    {
        Key = "crystal", Name = "Crystal",
        Top = Color.FromArgb(0xFC, 0x0A, 0x06, 0x12), Mid = Color.FromArgb(0xF8, 0x16, 0x0C, 0x27), Bottom = Color.FromArgb(0xF4, 0x26, 0x13, 0x42),
        RimColor = GemLilac, RimIdle = 0.28,
        Accent = GemAmethyst, Tint = Color.FromRgb(0x7A, 0x48, 0xD8), TintAlways = true, Clock = GemIce, Fps = GemLilac,
        TileC = Color.FromArgb(0x22, 0xE4, 0xD6, 0xFF), DimC = Color.FromArgb(0xC0, 0xE6, 0xDC, 0xFF),
        LookTile = F(Color.FromArgb(0x20, 0xE4, 0xD6, 0xFF)),
        LookEdge = VGrad((Color.FromArgb(0x9C, 0xF1, 0xEA, 0xFF), 0), (Color.FromArgb(0x18, 0xB9, 0x8C, 0xFF), 0.55), (Color.FromArgb(0x60, 0xB9, 0x8C, 0xFF), 1)),
        LookSheen = VGrad((Color.FromArgb(0x2C, 0xF1, 0xEA, 0xFF), 0), (Color.FromArgb(0x00, 0xF1, 0xEA, 0xFF), 1)),
        LookLabel = F(Color.FromArgb(0xC8, 0xEE, 0xE6, 0xFF)),
        LookSwitchOff = F(Color.FromArgb(0x30, 0xE4, 0xD6, 0xFF)), LookSwitchOn = F(GemViolet),
        Map = CrystalMap, Glint = true, Prism = true, Geode = true,
        Signature = GemAmethyst,
    };

    /// <summary>The rim of a cut gem: light split into lilac, violet, periwinkle and ice.</summary>
    static Brush GemRim()
    {
        var prism = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        foreach (var (c, at) in new[] { (Color.FromArgb(0xB4, 0xFF, 0x9C, 0xD8), 0.0), (Color.FromArgb(0xB4, 0xB9, 0x8C, 0xFF), 0.3), (Color.FromArgb(0xB4, 0x8F, 0xA8, 0xFF), 0.62),
                                         (Color.FromArgb(0xB4, 0xC8, 0xF0, 0xFF), 0.85), (Color.FromArgb(0xB4, 0xF1, 0xEA, 0xFF), 1.0) })
            prism.GradientStops.Add(new GradientStop(c, at));
        prism.Freeze();
        return prism;
    }

    /// <summary>The geode's own light, from below: amethyst at its heart, deep violet, then the night.</summary>
    static Brush GeodeFloor() => new RadialGradientBrush
    {
        Center = new Point(0.5, 1.1), GradientOrigin = new Point(0.5, 1.1), RadiusX = 0.75, RadiusY = 0.95,
        GradientStops =
        {
            new GradientStop(Color.FromArgb(0x48, 0xB9, 0x8C, 0xFF), 0), new GradientStop(Color.FromArgb(0x22, 0x7A, 0x48, 0xD8), 0.45),
            new GradientStop(Colors.Transparent, 1),
        },
    };

    /// <summary>The glint that follows the pointer: light passing through a gem, lilac at its heart.</summary>
    static Brush GemGlint() => new RadialGradientBrush
    {
        Center = new Point(0.5, 0.15), GradientOrigin = new Point(0.5, 0.0), RadiusX = 0.5, RadiusY = 0.9,
        GradientStops = { new GradientStop(Color.FromArgb(0x30, 0xE8, 0xDC, 0xFF), 0), new GradientStop(Colors.Transparent, 1) },
    };

    // ───────────── the open notch: inside the geode ─────────────

    Ambient CrystalAmbient()
    {
        var a = new Ambient(ExpandedSize.Width, ExpandedSize.Height);
        AddFacets(a);
        AddClusters(a);
        // glitter on the facets: two layers twinkling out of step
        a.AddSheet(r => r.Next(4) == 0 ? Ambient.Twinkle(4 + r.NextDouble() * 2.5, GemIce) : Ambient.Speck(1 + r.NextDouble(), r.Next(3) == 0 ? GemLilac : GemIce),
            count: 30, speed: 0, opacity: 0.9, pulseLow: 0.2, pulseSeconds: 2.4);
        a.AddSheet(r => Ambient.Speck(0.9 + r.NextDouble() * 0.9, GemAmethyst), count: 24, speed: 0, opacity: 0.7, pulseLow: 0.12, pulseSeconds: 3.1, pulseDelay: 1.1);
        // a ray of light refracting through the stone: a thin rainbow crossing at an angle, now and then
        var band = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        foreach (var (c, at) in new[] { (Color.FromArgb(0x00, 0xFF, 0x8F, 0xC4), 0.0), (Color.FromArgb(0x1C, 0xFF, 0x8F, 0xC4), 0.3), (Color.FromArgb(0x24, 0xFF, 0xD6, 0x8A), 0.42),
                                         (Color.FromArgb(0x26, 0x9C, 0xF5, 0xC8), 0.52), (Color.FromArgb(0x24, 0x8F, 0xB8, 0xFF), 0.62), (Color.FromArgb(0x1C, 0xB9, 0x8C, 0xFF), 0.74), (Color.FromArgb(0x00, 0xB9, 0x8C, 0xFF), 1.0) })
            band.GradientStops.Add(new GradientStop(c, at));
        band.Freeze();
        a.AddSweep(band, bandWidth: 70, angle: 28, crossSeconds: 1.9, everySeconds: 8);
        // a facet's tip catching the light
        Color[] gems = { GemIce, GemLilac, GemAmethyst, GemRose };
        a.AddFlares(r => { var c = gems[r.Next(gems.Length)]; return (Frozen(new RadialGradientBrush(Colors.White, Color.FromArgb(0xB0, c.R, c.G, c.B))), 8 + r.NextDouble() * 5); }, everySeconds: 3.2);
        return a;
    }

    /// <summary>
    /// The geode's walls: wide planes of crystal across the whole background, each a slightly different light, in
    /// three groups that brighten in turn (as if the light inside moved), very faint behind the cards.
    /// </summary>
    void AddFacets(Ambient a)
    {
        double w = ExpandedSize.Width, h = ExpandedSize.Height;
        var rnd = new Random(11);
        // a loose grid of points, nudged, cut into triangles
        int cols = 6, rows = 3;
        var pts = new Point[cols + 1, rows + 1];
        for (int i = 0; i <= cols; i++)
            for (int j = 0; j <= rows; j++)
            {
                double x = w * i / cols, y = h * j / rows;
                if (i > 0 && i < cols) x += (rnd.NextDouble() - 0.5) * w / cols * 0.7;
                if (j > 0 && j < rows) y += (rnd.NextDouble() - 0.5) * h / rows * 0.7;
                pts[i, j] = new Point(x, y);
            }
        var groups = new[] { new Canvas { Width = w, Height = h }, new Canvas { Width = w, Height = h }, new Canvas { Width = w, Height = h } };
        int n = 0;
        for (int i = 0; i < cols; i++)
            for (int j = 0; j < rows; j++)
            {
                Point p00 = pts[i, j], p10 = pts[i + 1, j], p01 = pts[i, j + 1], p11 = pts[i + 1, j + 1];
                bool flip = (i + j) % 2 == 0;
                foreach (var tri in flip ? new[] { new[] { p00, p10, p11 }, new[] { p00, p11, p01 } } : new[] { new[] { p00, p10, p01 }, new[] { p10, p11, p01 } })
                {
                    double k = rnd.NextDouble();
                    var c = k < 0.4 ? GemIce : k < 0.75 ? GemLilac : GemAmethyst;
                    var fill = new LinearGradientBrush(Color.FromArgb((byte)(0x10 + rnd.Next(0x16)), c.R, c.G, c.B), Color.FromArgb(0x04, c.R, c.G, c.B), rnd.NextDouble() * 360);
                    fill.Freeze();
                    var facet = new Polygon { Points = new PointCollection(tri), Fill = fill, Stroke = Frozen(new SolidColorBrush(Color.FromArgb(0x12, 0xF1, 0xEA, 0xFF))), StrokeThickness = 0.7 };
                    groups[n++ % 3].Children.Add(facet);
                }
            }
        var holder = new Grid { Width = w, Height = h, IsHitTestVisible = false };
        var moves = new List<(IAnimatable, DependencyProperty, AnimationTimeline)>();
        for (int g = 0; g < 3; g++)
        {
            groups[g].CacheMode = new BitmapCache(1.25);
            holder.Children.Add(groups[g]);
            moves.Add((groups[g], OpacityProperty, new DoubleAnimation(1, 0.35, TimeSpan.FromSeconds(5.5)) { BeginTime = TimeSpan.FromSeconds(g * 1.8), AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } }));
        }
        a.AddPiece(holder, moves, fps: 20);
    }

    /// <summary>Clusters of amethyst points growing from the two lower corners, each point three-faceted, catching the light from the left.</summary>
    void AddClusters(Ambient a)
    {
        double w = ExpandedSize.Width, h = ExpandedSize.Height;
        var cv = new Canvas { Width = w, Height = h, IsHitTestVisible = false, Opacity = 0.55, CacheMode = new BitmapCache(1.25) };
        var rnd = new Random(5);
        void Cluster(double x0, double dir)
        {
            for (int i = 0; i < 6; i++)
            {
                double len = 26 + rnd.NextDouble() * 38, wid = 7 + rnd.NextDouble() * 6;
                double tilt = dir * (8 + i * 9 + rnd.NextDouble() * 6);
                double x = x0 + dir * (i * 7 + rnd.NextDouble() * 5);
                cv.Children.Add(GemPoint(x, h + 4, len, wid, tilt));
            }
        }
        Cluster(-4, 1);
        Cluster(w + 4, -1);
        a.AddPiece(cv, Array.Empty<(IAnimatable, DependencyProperty, AnimationTimeline)>());
    }

    /// <summary>One crystal point: a six-sided column ending in a tip, seen as three facets (lit, middle, shade).</summary>
    static FrameworkElement GemPoint(double x, double y, double len, double wid, double tilt)
    {
        double half = wid / 2, shoulder = len - wid * 0.9;
        var g = new Canvas { RenderTransform = new RotateTransform(tilt, 0, 0) };
        Canvas.SetLeft(g, x);
        Canvas.SetTop(g, y);
        Polygon Facet(Point[] p, Color c, byte alpha) => new() { Points = new PointCollection(p), Fill = Frozen(new LinearGradientBrush(Color.FromArgb(alpha, c.R, c.G, c.B), Color.FromArgb((byte)(alpha / 3), c.R, c.G, c.B), 90)) };
        // the column runs up from the base (0,0) to the shoulder, then the tip
        g.Children.Add(Facet(new[] { new Point(-half, 0), new Point(-half * 0.35, 0), new Point(-half * 0.35, -shoulder - wid * 0.25), new Point(0, -len), new Point(-half, -shoulder) }, GemIce, 0xC0));
        g.Children.Add(Facet(new[] { new Point(-half * 0.35, 0), new Point(half * 0.35, 0), new Point(half * 0.35, -shoulder - wid * 0.25), new Point(0, -len), new Point(-half * 0.35, -shoulder - wid * 0.25) }, GemAmethyst, 0xB0));
        g.Children.Add(Facet(new[] { new Point(half * 0.35, 0), new Point(half, 0), new Point(half, -shoulder), new Point(0, -len), new Point(half * 0.35, -shoulder - wid * 0.25) }, GemViolet, 0xA0));
        g.Children.Add(new Polygon
        {
            Points = new PointCollection(new[] { new Point(-half, 0), new Point(-half, -shoulder), new Point(0, -len), new Point(half, -shoulder), new Point(half, 0) }),
            Stroke = Frozen(new SolidColorBrush(Color.FromArgb(0x70, 0xF1, 0xEA, 0xFF))), StrokeThickness = 0.7, StrokeLineJoin = PenLineJoin.Round,
        });
        return g;
    }

    // ───────────── the search, cut like a gem ─────────────

    static LaunchSkin CrystalSkin()
    {
        var panel = new RadialGradientBrush { Center = new Point(0.5, 1.2), GradientOrigin = new Point(0.5, 1.2), RadiusX = 1.0, RadiusY = 1.0 };
        panel.GradientStops.Add(new GradientStop(Color.FromArgb(0xFC, 0x2C, 0x16, 0x4C), 0));
        panel.GradientStops.Add(new GradientStop(Color.FromArgb(0xFC, 0x16, 0x0C, 0x27), 0.55));
        panel.GradientStops.Add(new GradientStop(Color.FromArgb(0xFC, 0x0A, 0x06, 0x12), 1));
        var sel = new LinearGradientBrush(Color.FromArgb(0x36, GemAmethyst.R, GemAmethyst.G, GemAmethyst.B), Color.FromArgb(0x12, GemLilac.R, GemLilac.G, GemLilac.B), 0);
        return new LaunchSkin("crystal", Frozen(panel), GemRim(), 1, GemAmethyst, Frozen(sel), Frozen(Color.FromArgb(0x50, GemLilac.R, GemLilac.G, GemLilac.B)), new Thickness(1), 8,
            GemIce, Color.FromArgb(0xB0, 0xC8, 0xB4, 0xF0), Color.FromArgb(0x48, GemAmethyst.R, GemAmethyst.G, GemAmethyst.B), GemLilac,
            Color.FromArgb(0xE6, 0xE4, 0xD8, 0xFF), GemIce, Arrival.Glint, 8);
    }
}
