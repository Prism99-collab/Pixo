using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using DynamicNotch.Controls;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// Coffee: the notch as a cup on a café counter. A dark roast body with a ring of crema along its lower edge, the
/// warm light of the counter rising from below; open, a cup on its saucer seen from above, its foam just stirred and
/// turning slowly, steam curling up from it now and then, a few beans drifting by. Its type is the café's own: a warm serif for the clock and the titles (Young Serif),
/// a round, friendly sans for everything else (Nunito), and the page's name written by hand, like the menu board
/// (Caveat). All three are bundled (Assets/Fonts, SIL Open Font License), so every PC shows them.
/// The coffee changes with the day and stays coffee: a milky latte in the morning, a cappuccino at midday, a mocha in
/// the late afternoon, an espresso at night. Each brew has its own roast, crema and foam; the switch happens while the
/// notch is closed, never under your eyes.
/// </summary>
public partial class NotchWindow
{
    // ───────────── the café's type ─────────────

    // made on first use, once the app is running (a "pack:" address only exists inside a WPF app: made with the
    // class, it broke anything that touched the notch's code outside one, the checks among them)
    static FontFamily? _coffeeBody, _coffeeDisplay, _coffeeHand;
    static FontFamily CoffeeFont(ref FontFamily? f, string faces) => f ??= new(new Uri("pack://application:,,,/"), "./Assets/Fonts/#" + faces);
    static FontFamily CoffeeBody => CoffeeFont(ref _coffeeBody, "Nunito, Segoe UI Variable Text, Segoe UI");
    internal static FontFamily CoffeeDisplay => CoffeeFont(ref _coffeeDisplay, "Young Serif, Georgia"); // (the Preferences' Coffee card too)
    static FontFamily CoffeeHand => CoffeeFont(ref _coffeeHand, "Caveat, Segoe Print");

    // ───────────── the brews ─────────────

    sealed record Brew(string Key, Color Top, Color Mid, Color Bottom, Color Crema, Color Foam, Color Accent, Color Tint);

    // the roast darkens through the day; the crema and the foam follow the drink
    static readonly Brew Latte = new("latte", Color.FromArgb(0xFC, 0x16, 0x0D, 0x08), Color.FromArgb(0xFA, 0x29, 0x19, 0x0F), Color.FromArgb(0xF8, 0x44, 0x2A, 0x19),
        Color.FromRgb(0xE4, 0xBC, 0x8C), Color.FromRgb(0xF7, 0xE8, 0xD2), Color.FromRgb(0xEA, 0xBF, 0x8E), Color.FromRgb(0x9C, 0x6C, 0x44));
    static readonly Brew Cappuccino = new("cappuccino", Color.FromArgb(0xFC, 0x13, 0x0B, 0x07), Color.FromArgb(0xFA, 0x23, 0x15, 0x0D), Color.FromArgb(0xF8, 0x3A, 0x23, 0x14),
        Color.FromRgb(0xD8, 0x9E, 0x62), Color.FromRgb(0xF3, 0xDF, 0xC3), Color.FromRgb(0xE0, 0xA8, 0x6E), Color.FromRgb(0x8E, 0x58, 0x31));
    static readonly Brew Mocha = new("mocha", Color.FromArgb(0xFC, 0x12, 0x09, 0x06), Color.FromArgb(0xFA, 0x21, 0x10, 0x0B), Color.FromArgb(0xF8, 0x36, 0x19, 0x10),
        Color.FromRgb(0xC6, 0x7E, 0x4D), Color.FromRgb(0xEE, 0xD1, 0xB6), Color.FromRgb(0xDC, 0x8E, 0x5F), Color.FromRgb(0x80, 0x3C, 0x24));
    static readonly Brew Espresso = new("espresso", Color.FromArgb(0xFC, 0x0F, 0x08, 0x05), Color.FromArgb(0xFA, 0x1C, 0x10, 0x09), Color.FromArgb(0xF8, 0x2C, 0x18, 0x0D),
        Color.FromRgb(0xBC, 0x7A, 0x42), Color.FromRgb(0xEA, 0xD2, 0xB2), Color.FromRgb(0xD4, 0x9C, 0x64), Color.FromRgb(0x7A, 0x44, 0x22));

    /// <summary>The drink of this hour: latte in the morning, cappuccino at midday, mocha late afternoon, espresso at night.</summary>
    static Brew BrewNow(DateTime now) => now.Hour switch
    {
        >= 5 and < 11 => Latte,
        >= 11 and < 16 => Cappuccino,
        >= 16 and < 20 => Mocha,
        _ => Espresso,
    };

    // the café's own colours for the notch's system colours: caramel, matcha, honey, berry, cherry
    static readonly Color CoffeeCaramel = Color.FromRgb(0xDD, 0xA5, 0x6B), CoffeeMatcha = Color.FromRgb(0xA9, 0xC8, 0x7E), CoffeeHoney = Color.FromRgb(0xEE, 0xB8, 0x4F),
        CoffeeBerry = Color.FromRgb(0xD8, 0x72, 0x86), CoffeeCherry = Color.FromRgb(0xE2, 0x63, 0x50), CoffeeCinnamon = Color.FromRgb(0xD9, 0x7C, 0x55),
        CoffeePlum = Color.FromRgb(0xC2, 0x86, 0xB0), CoffeeCream = Color.FromRgb(0xF2, 0xDD, 0xC0), CoffeeCocoa = Color.FromRgb(0x6B, 0x41, 0x2A);

    static readonly Dictionary<int, Color> CoffeePalette = new()
    {
        [0x30D158] = CoffeeMatcha, [0x34C759] = CoffeeMatcha, [0x30D5C8] = Color.FromRgb(0x9C, 0xCB, 0x9A),
        [0x0A84FF] = CoffeeCaramel, [0x64D2FF] = Color.FromRgb(0xE8, 0xCB, 0xA6), [0x5E5CE6] = Color.FromRgb(0xB8, 0x8A, 0x9C),
        [0xFF9F0A] = CoffeeHoney, [0xFFD60A] = Color.FromRgb(0xF2, 0xC9, 0x6E), [0xBF5AF2] = CoffeePlum,
        [0xFF453A] = CoffeeCherry, [0xFF375F] = CoffeeBerry,
    };

    /// <summary>System colours onto the café's; greys (cards, tiles) become warm roasted wood; menus solid dark roast.</summary>
    static Color CoffeeMap(Color c)
    {
        if (CoffeePalette.TryGetValue(c.R << 16 | c.G << 8 | c.B, out var m)) return Color.FromArgb(c.A, m.R, m.G, m.B);
        int max = Math.Max(c.R, Math.Max(c.G, c.B)), min = Math.Min(c.R, Math.Min(c.G, c.B));
        if (max - min > 6 || max < 0x10 || max > 0x44) return c;
        if (c.A is >= 0xE0 and < 0xFF) return Color.FromArgb(0xF6, 0x20, 0x14, 0x0D);
        return Color.FromArgb(c.A, (byte)Math.Min(255, max * 1.22), (byte)(max * 0.86), (byte)(max * 0.62));
    }

    static Look CoffeeLook(Brew b) => new()
    {
        Key = "coffee", Name = "Coffee",
        Top = b.Top, Mid = b.Mid, Bottom = b.Bottom,
        RimColor = b.Crema, RimIdle = 0.3,
        Accent = b.Accent, Tint = b.Tint, TintAlways = true, Clock = b.Foam, Fps = b.Foam,
        TileC = Color.FromArgb(0xEC, 0x33, 0x22, 0x17), DimC = Color.FromArgb(0xD4, b.Foam.R, b.Foam.G, b.Foam.B),
        LookTile = F(Color.FromArgb(0xEC, 0x33, 0x22, 0x17)),
        LookEdge = VGrad((Color.FromArgb(0x4C, b.Foam.R, b.Foam.G, b.Foam.B), 0), (Color.FromArgb(0x14, b.Crema.R, b.Crema.G, b.Crema.B), 0.55), (Color.FromArgb(0x6E, b.Crema.R, b.Crema.G, b.Crema.B), 1)),
        LookSheen = VGrad((Color.FromArgb(0x14, b.Foam.R, b.Foam.G, b.Foam.B), 0), (Color.FromArgb(0x00, b.Foam.R, b.Foam.G, b.Foam.B), 1)),
        LookLabel = F(Color.FromArgb(0xEC, b.Foam.R, b.Foam.G, b.Foam.B)),
        LookSwitchOff = F(Color.FromRgb(0x3E, 0x2A, 0x1D)), LookSwitchOn = F(b.Accent),
        Map = CoffeeMap, Glint = true, Jelly = true, Coffee = b,
        Signature = CoffeeCaramel,
    };

    static Look CoffeeLookNow() => CoffeeLook(BrewNow(DateTime.Now));

    bool CoffeeOn => _look?.Coffee != null;

    // ───────────── its own light: the crema ─────────────

    Border? _crema;

    /// <summary>The ring of crema along the lower edge (drawn once; its colour is the brew's, set when the theme comes in).</summary>
    void BuildCoffeeLayers(Grid stillOver)
    {
        _crema = new Border { Height = 1.6, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(16, 0, 16, 1.4), Visibility = Visibility.Collapsed };
        stillOver.Children.Add(_crema);
    }

    /// <summary>The counter's warm light rising from below: the crema glowing at the heart, fading into the roast.</summary>
    static Brush CoffeeFloor(Brew b) => new RadialGradientBrush
    {
        Center = new Point(0.5, 1.12), GradientOrigin = new Point(0.5, 1.12), RadiusX = 0.78, RadiusY = 1.0,
        GradientStops =
        {
            new GradientStop(Color.FromArgb(0x4C, b.Crema.R, b.Crema.G, b.Crema.B), 0), new GradientStop(Color.FromArgb(0x22, b.Tint.R, b.Tint.G, b.Tint.B), 0.42),
            new GradientStop(Colors.Transparent, 1),
        },
    };

    /// <summary>The glint that follows the pointer: candlelight on the cup.</summary>
    static Brush CoffeeGlint(Brew b) => new RadialGradientBrush
    {
        Center = new Point(0.5, 0.2), GradientOrigin = new Point(0.5, 0.0), RadiusX = 0.5, RadiusY = 1.0,
        GradientStops = { new GradientStop(Color.FromArgb(0x26, b.Foam.R, b.Foam.G, b.Foam.B), 0), new GradientStop(Colors.Transparent, 1) },
    };

    readonly DispatcherTimer _brewTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(5) };
    bool _brewWired;

    /// <summary>Coffee in or out: its crema, its type, and the brew that follows the hour.</summary>
    void ApplyCoffee(Brew? b)
    {
        if (_crema == null) return;
        if (!_brewWired) { _brewWired = true; _brewTimer.Tick += (_, _) => Rebrew(); }
        if (b == null)
        {
            _crema.Visibility = Visibility.Collapsed;
            _brewTimer.Stop();
            if (_themeFont == CoffeeBody) { ForgetThemeFonts(); _themeFont = null; } // (every text it dressed gets its own back)
            return;
        }
        var line = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        foreach (var (c, at) in new[] { (Colors.Transparent, 0.0), (Color.FromArgb(0xA8, b.Crema.R, b.Crema.G, b.Crema.B), 0.2), (Color.FromArgb(0xD0, b.Foam.R, b.Foam.G, b.Foam.B), 0.5),
                                         (Color.FromArgb(0xA8, b.Crema.R, b.Crema.G, b.Crema.B), 0.8), (Colors.Transparent, 1.0) })
            line.GradientStops.Add(new GradientStop(c, at));
        line.Freeze();
        _crema.Background = line;
        _crema.Visibility = Visibility.Visible;
        _brewTimer.Start();

        // the type: the whole notch in Nunito; the clock and the pop-ups' titles in the café's serif; the page's
        // name by hand, like the board above the counter
        _themeFont = CoffeeBody;
        _themeStretch = FontStretches.Normal;
        Swap(this, FontFamilyProperty, CoffeeBody);
        foreach (var tb in new[] { ClockText, OsdTitle })
        {
            Swap(tb, TextBlock.FontFamilyProperty, CoffeeDisplay);
            Swap(tb, TextBlock.FontWeightProperty, FontWeights.Normal); // (one weight, drawn as it is: never thickened)
        }
        Swap(ClockText, TextBlock.FontSizeProperty, 13.5);
        Swap(PageTitle, TextBlock.FontFamilyProperty, CoffeeHand);
        Swap(PageTitle, TextBlock.FontSizeProperty, 15.5);
        Swap(ClockText, TextBlock.ForegroundProperty, F(b.Foam));
    }

    /// <summary>The hour moved on to another drink: the notch takes it while closed (never a jump under your eyes).</summary>
    void Rebrew()
    {
        if (_look?.Coffee is not { } now || ThemeKey != "coffee") { _brewTimer.Stop(); return; }
        var want = BrewNow(DateTime.Now);
        if (want.Key == now.Key || _mode is not (Mode.Idle or Mode.Slim) || _animating || _hidden || _launcherOpen) return; // (not now: the next look, in 5 minutes)
        ApplyLook(null, animate: false);
        ApplyLook(CoffeeLook(want), animate: false);
        ApplyCustomColors();
        SelectPaint(_paint); // the Colors page's "Auto" swatches
        PaintBars();         // the music's bars and wave
        UpdateAmbient();
        AppLog.Write("coffee: " + want.Key);
    }

    // ───────────── the open notch's background: the café ─────────────

    Ambient CoffeeAmbient(Brew b)
    {
        var a = new Ambient(ExpandedSize.Width, ExpandedSize.Height);
        AddCup(a, b);
        // the counter's lamps: two big soft warm lights, breathing like candlelight
        a.AddSheet(r => Ambient.Glow(110 + r.NextDouble() * 60, r.Next(2) == 0 ? b.Crema : b.Tint),
            count: 2, speed: 0, sway: 22, swaySeconds: 15, opacity: 0.16, pulseLow: 0.08, pulseSeconds: 6);
        // a few beans drifting down, turning slowly, far behind
        a.AddSheet(r => Bean(7 + r.NextDouble() * 4, r.NextDouble() * 180), count: 5, speed: 5, sway: 10, swaySeconds: 11, opacity: 0.2);
        // the crema's tiny bubbles, rising slowly
        a.AddSheet(r => Ambient.Speck(1 + r.NextDouble() * 1.2, r.Next(3) == 0 ? b.Crema : b.Foam), count: 22, speed: -4, sway: 4, swaySeconds: 7, opacity: 0.4, pulseLow: 0.18, pulseSeconds: 3.5);
        // steam: now and then a few curls rise from the cup and thin out
        a.AddEvents((layer, r) => Steam(layer, r, b), minSeconds: 4.5, maxSeconds: 8, fps: 30);
        // a grain of sugar catching the light
        a.AddFlares(r => (Frozen(new RadialGradientBrush(Colors.White, Color.FromArgb(0x80, b.Foam.R, b.Foam.G, b.Foam.B))), 6 + r.NextDouble() * 4), everySeconds: 6);
        return a;
    }

    /// <summary>A coffee bean: an oval of dark roast with its crease.</summary>
    static FrameworkElement Bean(double size, double turn)
    {
        var g = new Grid { Width = size, Height = size * 1.35, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(turn) };
        g.Children.Add(new Ellipse { Fill = Frozen(new LinearGradientBrush(Color.FromRgb(0x8A, 0x55, 0x33), Color.FromRgb(0x4A, 0x2A, 0x18), 45)) });
        g.Children.Add(new Path
        {
            Data = Geometry.Parse(FormattableString.Invariant($"M{size * 0.5},{size * 0.12} C{size * 0.28},{size * 0.5} {size * 0.72},{size * 0.85} {size * 0.5},{size * 1.23}")),
            Stroke = Frozen(new SolidColorBrush(Color.FromRgb(0x2A, 0x16, 0x0C))), StrokeThickness = Math.Max(0.8, size * 0.1),
        });
        return g;
    }

    /// <summary>Where the cup stands in the open notch: low on the right, part of its saucer past the edge.</summary>
    static (double X, double Y, double R) CupPlace(double w, double h) => (w - 74, h * 0.6, 80);

    /// <summary>
    /// The cup, seen from above as it stands on the counter: a saucer, a ceramic rim catching the light, the coffee
    /// darker at its heart and golden with crema at its edge, and the foam just stirred into two spiral arms that turn
    /// very slowly (a full turn takes a minute and a half), like a spoon went round it a moment ago. Drawn once into
    /// cached pictures: only the swirl's turn moves, so it costs next to nothing.
    /// </summary>
    void AddCup(Ambient a, Brew b)
    {
        double w = ExpandedSize.Width, h = ExpandedSize.Height;
        var (px, py, rr) = CupPlace(w, h);
        double box = Math.Ceiling(rr * 1.46);              // half the cup's square: saucer, handle and shadow fit
        double cx = box, cy = box;                         // drawn inside that square, placed where the cup stands
        var still = new Canvas { Width = box * 2, Height = box * 2, IsHitTestVisible = false, Opacity = 0.42 };
        Ellipse Disc(double r, Brush fill) { var e = new Ellipse { Width = r * 2, Height = r * 2, Fill = fill }; Canvas.SetLeft(e, cx - r); Canvas.SetTop(e, cy - r); return e; }
        Color Al(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);
        var ceramic = Color.FromRgb(0xF6, 0xEC, 0xDF);

        // the saucer: a soft ring of glaze around it, and the cup's shadow falling on it to the lower left
        still.Children.Add(Disc(rr * 1.42, Frozen(new RadialGradientBrush(new GradientStopCollection
        {
            new(Colors.Transparent, 0.66), new(Al(ceramic, 0x1E), 0.8), new(Al(ceramic, 0x30), 0.9), new(Al(ceramic, 0x0A), 0.98), new(Colors.Transparent, 1),
        }))));
        var shade = Disc(rr * 1.04, Frozen(new RadialGradientBrush(Color.FromArgb(0x70, 0, 0, 0), Colors.Transparent)));
        shade.RenderTransform = new TranslateTransform(-7, 9);
        still.Children.Add(shade);
        // the handle, to the lower left, in the same glaze
        var handle = new Border
        {
            Width = 34, Height = 20, CornerRadius = new CornerRadius(10),
            Background = Frozen(new LinearGradientBrush(Al(ceramic, 0x88), Al(ceramic, 0x40), 90)),
            BorderBrush = Frozen(new SolidColorBrush(Al(ceramic, 0x50))), BorderThickness = new Thickness(1),
            RenderTransform = new RotateTransform(-38, 17, 10),
        };
        Canvas.SetLeft(handle, cx - rr * 0.8 - 26);
        Canvas.SetTop(handle, cy + rr * 0.56 - 8);
        still.Children.Add(handle);
        // the rim: glaze lit from the upper left
        still.Children.Add(Disc(rr, Frozen(new LinearGradientBrush(Al(ceramic, 0xA8), Al(ceramic, 0x48), 45))));
        // inside the lip, the cup's own shadow, then the coffee: dark at its heart, crema gold toward the edge
        still.Children.Add(Disc(rr - 6, Frozen(new SolidColorBrush(Al(b.Top, 0xFF)))));
        still.Children.Add(Disc(rr - 8, Frozen(new RadialGradientBrush(new GradientStopCollection
        {
            new(Al(b.Bottom, 0xFF), 0), new(Al(Mix(b.Bottom, b.Crema, 0.35), 0xFF), 0.62), new(Al(b.Crema, 0xFF), 0.9), new(Al(Mix(b.Crema, b.Top, 0.5), 0xFF), 1),
        }) { GradientOrigin = new Point(0.42, 0.4) })));
        // a glint along the rim, where the light hits it
        var glint = new Path
        {
            Data = Geometry.Parse(FormattableString.Invariant($"M{cx - rr * 0.94},{cy - rr * 0.34} A{rr - 1.5},{rr - 1.5} 0 0 1 {cx - rr * 0.3},{cy - rr * 0.95}")),
            Stroke = Frozen(new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF))), StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        };
        still.Children.Add(glint);
        still.CacheMode = new BitmapCache(1.25);

        // the foam, just stirred: two spiral arms, thin at the heart, widening as they go out, and a soft heart of foam
        double inner = rr - 12;
        // arms: their angle apart, how wide they grow, how far out they reach
        Geometry Arms(double[] offsets, double width, double reach, bool heart)
        {
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                const double turns = 1.35, r0 = 3.5;
                double tMax = turns * 2 * Math.PI, k = Math.Log(inner * reach / r0) / tMax;
                foreach (double off in offsets)
                {
                    var outer = new List<Point>();
                    var back = new List<Point>();
                    for (double t = 0; t <= tMax + 1e-6; t += 0.06)
                    {
                        double r = r0 * Math.Exp(k * t), f = t / tMax;
                        double half = 0.5 + width * Math.Pow(f, 0.9) * (1 - Math.Pow(f, 4) * 0.95); // widest past the middle, drawn out to a fine tail
                        double ang = t + off;
                        outer.Add(new Point(cx + Math.Cos(ang) * (r + half), cy + Math.Sin(ang) * (r + half)));
                        back.Add(new Point(cx + Math.Cos(ang) * Math.Max(0, r - half), cy + Math.Sin(ang) * Math.Max(0, r - half)));
                    }
                    back.Reverse();
                    g.BeginFigure(outer[0], true, true);
                    g.PolyLineTo(outer.Skip(1).Concat(back).ToList(), true, true);
                }
                if (heart)
                {
                    g.BeginFigure(new Point(cx + 7, cy), true, true);
                    g.ArcTo(new Point(cx - 7, cy), new Size(7, 7), 0, false, SweepDirection.Clockwise, true, true);
                    g.ArcTo(new Point(cx + 7, cy), new Size(7, 7), 0, false, SweepDirection.Clockwise, true, true);
                }
            }
            geo.Freeze();
            return geo;
        }
        var swirlArt = new Canvas { Width = box * 2, Height = box * 2, IsHitTestVisible = false, Opacity = 0.42, Clip = new EllipseGeometry(new Point(cx, cy), inner + 2, inner + 2) };
        // the cream's fine veins between the arms first (the stir pulled them thin), then the two arms of foam over them
        swirlArt.Children.Add(new Path { Data = Arms(new[] { Math.PI / 2, Math.PI * 1.5 }, 2.4, 0.9, false), Fill = Frozen(new SolidColorBrush(Color.FromArgb(0x80, b.Foam.R, b.Foam.G, b.Foam.B))) });
        swirlArt.Children.Add(new Path { Data = Arms(new[] { 0.0, Math.PI }, 8.5, 1.0, true), Fill = Frozen(new SolidColorBrush(Color.FromArgb(0xE6, b.Foam.R, b.Foam.G, b.Foam.B))) });
        swirlArt.CacheMode = new BitmapCache(1.25);
        var foam = swirlArt;
        var spin = new RotateTransform(0, cx, cy);
        foam.RenderTransform = spin;

        var cup = new Canvas { Width = w, Height = h, IsHitTestVisible = false };
        foreach (var layer in new FrameworkElement[] { still, swirlArt }) { Canvas.SetLeft(layer, px - box); Canvas.SetTop(layer, py - box); }
        cup.Children.Add(still);
        cup.Children.Add(foam);
        var stir = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(90)) { RepeatBehavior = RepeatBehavior.Forever };
        a.AddPiece(cup, new (IAnimatable, DependencyProperty, AnimationTimeline)[] { (spin, RotateTransform.AngleProperty, stir) }, fps: 15);
    }

    /// <summary>A few curls of steam rising from the cup, swaying, thinning out as they go.</summary>
    static (List<(IAnimatable Target, DependencyProperty Prop, AnimationTimeline Move)> Moves, TimeSpan Life)? Steam(Canvas layer, Random r, Brew b)
    {
        double w = layer.Width, h = layer.Height;
        var moves = new List<(IAnimatable, DependencyProperty, AnimationTimeline)>();
        var (cupX, cupY, cupR) = CupPlace(w, h);
        double x0 = cupX - 14 + r.NextDouble() * 18;
        int curls = 2 + r.Next(2);
        var life = TimeSpan.FromSeconds(4.6);
        for (int i = 0; i < curls; i++)
        {
            double x = x0 + (i - curls / 2.0) * 14 + r.NextDouble() * 6, len = 54 + r.NextDouble() * 26, bend = 9 + r.NextDouble() * 7;
            var stroke = new LinearGradientBrush { StartPoint = new Point(0.5, 1), EndPoint = new Point(0.5, 0) };
            stroke.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, b.Foam.R, b.Foam.G, b.Foam.B), 0));
            stroke.GradientStops.Add(new GradientStop(Color.FromArgb(0x70, b.Foam.R, b.Foam.G, b.Foam.B), 0.35));
            stroke.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, b.Foam.R, b.Foam.G, b.Foam.B), 1));
            stroke.Freeze();
            var curl = new Path
            {
                Data = Geometry.Parse(FormattableString.Invariant($"M{bend},{len} C{-bend},{len * 0.7} {bend * 2.2},{len * 0.42} {bend * 0.6},{len * 0.22} S{-bend * 0.4},{len * 0.04} {bend},0")),
                Stroke = stroke, StrokeThickness = 3.2 + r.NextDouble() * 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                Opacity = 0, IsHitTestVisible = false,
            };
            Canvas.SetLeft(curl, x);
            Canvas.SetTop(curl, cupY - cupR * 0.3 - len);
            var rise = new TranslateTransform();
            var lean = new SkewTransform();
            curl.RenderTransform = new TransformGroup { Children = { lean, rise } };
            curl.CacheMode = new BitmapCache(); // (drawn once; the lean and the rise only move the picture)
            layer.Children.Add(curl);
            var begin = TimeSpan.FromMilliseconds(i * 420 + r.Next(0, 200));
            moves.Add((rise, TranslateTransform.YProperty, new DoubleAnimation(0, -(cupY * 0.8 + r.NextDouble() * 16), life) { BeginTime = begin, EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut } }));
            moves.Add((lean, SkewTransform.AngleXProperty, new DoubleAnimation(-8, 10, TimeSpan.FromSeconds(2.3)) { BeginTime = begin, AutoReverse = true, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } }));
            var fade = new DoubleAnimationUsingKeyFrames { BeginTime = begin, Duration = life };
            fade.KeyFrames.Add(new EasingDoubleKeyFrame(0.55, KeyTime.FromPercent(0.3), new SineEase()));
            fade.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1), new SineEase()));
            moves.Add((curl, OpacityProperty, fade));
        }
        return (moves, life + TimeSpan.FromMilliseconds(curls * 420 + 300));
    }

    // ───────────── the search, dressed for the café ─────────────

    static LaunchSkin CoffeeSkin(Brew b)
    {
        var panel = new RadialGradientBrush { Center = new Point(0.5, 1.2), GradientOrigin = new Point(0.5, 1.2), RadiusX = 1.0, RadiusY = 1.0 };
        panel.GradientStops.Add(new GradientStop(Color.FromArgb(0xFC, b.Bottom.R, b.Bottom.G, b.Bottom.B), 0));
        panel.GradientStops.Add(new GradientStop(Color.FromArgb(0xFC, b.Mid.R, b.Mid.G, b.Mid.B), 0.55));
        panel.GradientStops.Add(new GradientStop(Color.FromArgb(0xFC, b.Top.R, b.Top.G, b.Top.B), 1));
        var rim = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        rim.GradientStops.Add(new GradientStop(Color.FromArgb(0x80, b.Crema.R, b.Crema.G, b.Crema.B), 0));
        rim.GradientStops.Add(new GradientStop(Color.FromArgb(0x90, b.Foam.R, b.Foam.G, b.Foam.B), 0.5));
        rim.GradientStops.Add(new GradientStop(Color.FromArgb(0x80, b.Crema.R, b.Crema.G, b.Crema.B), 1));
        var sel = new LinearGradientBrush(Color.FromArgb(0x36, b.Accent.R, b.Accent.G, b.Accent.B), Color.FromArgb(0x14, b.Crema.R, b.Crema.G, b.Crema.B), 0);
        return new LaunchSkin("coffee", Frozen(panel), Frozen(rim), 1, b.Accent, Frozen(sel), null, new Thickness(0), 12,
            b.Foam, Color.FromArgb(0xA0, b.Foam.R, b.Foam.G, b.Foam.B), Color.FromArgb(0x48, b.Crema.R, b.Crema.G, b.Crema.B), b.Accent,
            Color.FromArgb(0xE0, b.Foam.R, b.Foam.G, b.Foam.B), null, Arrival.Rise, 12);
    }
}
