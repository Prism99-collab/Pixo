using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using DynamicNotch.Controls;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// Dusk: the notch at dusk on a sunny coast. Deep night violet melting into magenta, lit from below by a sun that has
/// just gone under the horizon (gold, then orange, then flamingo pink); a hairline of sunset along its lower edge
/// (gold to pink to turquoise); in the open notch, two palm silhouettes backlit at the corners, warm city lights out of
/// focus drifting, a glint of gold now and then. Flamingo pink is its accent, the ocean's turquoise its second colour,
/// and every colour of the notch is moved onto that palette. Its clock and titles are set condensed and firm, like
/// signage. All of it is original drawing (no logo, no art, no font of anyone else's).
/// </summary>
public partial class NotchWindow
{
    static readonly Color DuskPink = Color.FromRgb(0xFF, 0x4F, 0x9A), DuskCoral = Color.FromRgb(0xFF, 0x6B, 0x7A), DuskOrange = Color.FromRgb(0xFF, 0x9A, 0x3C),
        DuskGold = Color.FromRgb(0xFF, 0xC8, 0x57), DuskTeal = Color.FromRgb(0x22, 0xE1, 0xD1), DuskOrchid = Color.FromRgb(0xC2, 0x6B, 0xFF),
        DuskPeach = Color.FromRgb(0xFF, 0xE3, 0xCF), DuskNight = Color.FromRgb(0x0C, 0x06, 0x13);

    static readonly Dictionary<int, Color> DuskPalette = new()
    {
        [0x0A84FF] = Color.FromRgb(0x2E, 0xC8, 0xFF), [0x64D2FF] = Color.FromRgb(0x5E, 0xEB, 0xF0), [0x30D158] = Color.FromRgb(0x3D, 0xF2, 0xB5),
        [0x34C759] = Color.FromRgb(0x3D, 0xF2, 0xB5), [0xFF9F0A] = DuskOrange, [0xFFD60A] = DuskGold, [0xBF5AF2] = DuskOrchid,
        [0x5E5CE6] = Color.FromRgb(0x8C, 0x6B, 0xFF), [0xFF453A] = Color.FromRgb(0xFF, 0x4D, 0x6D), [0xFF375F] = DuskPink, [0x30D5C8] = DuskTeal,
    };

    /// <summary>System colours onto the dusk palette; greys (cards, tiles) become rose-tinted frosted glass.</summary>
    static Color DuskMap(Color c)
    {
        if (DuskPalette.TryGetValue(c.R << 16 | c.G << 8 | c.B, out var m)) return Color.FromArgb(c.A, m.R, m.G, m.B);
        int max = Math.Max(c.R, Math.Max(c.G, c.B)), min = Math.Min(c.R, Math.Min(c.G, c.B));
        if (max - min > 6 || max < 0x10 || max > 0x44) return c;
        if (c.A is >= 0xE0 and < 0xFF) return Color.FromArgb(0xF6, 0x1A, 0x0B, 0x22); // menus and overlays: solid dusk
        // cards and tiles: solid dusk plum (no see-through glass over a bright sky: calm to read), lighter greys lighter
        return Color.FromArgb(c.A, (byte)Math.Min(255, max * 1.0), (byte)(max * 0.55), (byte)Math.Min(255, max * 1.35));
    }

    static Look DuskLook() => new()
    {
        Key = "g6", Name = "Dusk",
        Top = Color.FromArgb(0xFC, 0x0B, 0x05, 0x16), Mid = Color.FromArgb(0xFA, 0x1E, 0x08, 0x2A), Bottom = Color.FromArgb(0xF8, 0x3C, 0x0C, 0x34),
        RimColor = DuskCoral, RimIdle = 0.3,
        // the open panel's glow: a deep rose (the sun is low; a bright coral all over glared)
        Accent = DuskPink, Tint = Color.FromRgb(0xB8, 0x2E, 0x6A), Clock = DuskPeach, Fps = DuskTeal,
        TileC = Color.FromArgb(0xEC, 0x2B, 0x17, 0x38), DimC = Color.FromArgb(0xE0, 0xFF, 0xE3, 0xEC),
        LookTile = F(Color.FromArgb(0xEC, 0x2B, 0x17, 0x38)),
        LookEdge = VGrad((Color.FromArgb(0x50, 0xFF, 0xB0, 0xD0), 0), (Color.FromArgb(0x16, 0xFF, 0x4F, 0x9A), 0.55), (Color.FromArgb(0x78, 0xFF, 0x9A, 0x3C), 1)),
        LookSheen = VGrad((Color.FromArgb(0x12, 0xFF, 0xD9, 0xC0), 0), (Color.FromArgb(0x00, 0xFF, 0xD9, 0xC0), 1)),
        LookLabel = F(Color.FromArgb(0xE8, 0xFF, 0xE8, 0xEF)),
        LookSwitchOff = F(Color.FromRgb(0x3A, 0x1F, 0x48)), LookSwitchOn = F(DuskPink),
        Map = DuskMap, Sunset = true, Glint = true,
        Signature = DuskPink,
    };

    // ───────────── the dusk's own light ─────────────

    Border? _duskSky, _duskHorizon;

    /// <summary>The sky's two lights, the sunset hairline, and the palms (drawn once, shown only in Dusk).</summary>
    void BuildDuskLayers(Grid still, Grid stillOver)
    {
        // the sun just under the horizon is the floor's glow (set in ApplyLook); above it, the sky: a rose light high on
        // the right, the ocean's turquoise low on the left
        var sky = new Grid();
        sky.Children.Add(new Border
        {
            Background = new RadialGradientBrush
            {
                Center = new Point(0.86, 0.0), GradientOrigin = new Point(0.86, 0.0), RadiusX = 0.55, RadiusY = 1.2,
                GradientStops = { new GradientStop(Color.FromArgb(0x22, 0xC2, 0x6B, 0xFF), 0), new GradientStop(Colors.Transparent, 1) },
            },
        });
        sky.Children.Add(new Border
        {
            Background = new RadialGradientBrush
            {
                Center = new Point(0.06, 1.0), GradientOrigin = new Point(0.06, 1.0), RadiusX = 0.42, RadiusY = 0.9,
                GradientStops = { new GradientStop(Color.FromArgb(0x26, 0x22, 0xE1, 0xD1), 0), new GradientStop(Colors.Transparent, 1) },
            },
        });
        _duskSky = new Border { Child = sky, Visibility = Visibility.Collapsed };
        still.Children.Insert(0, _duskSky);

        // the sunset's hairline along the lower edge: gold, then pink, then the sea
        var line = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        foreach (var (c, at) in new[] { (Colors.Transparent, 0.0), (Color.FromArgb(0xB0, DuskGold.R, DuskGold.G, DuskGold.B), 0.22), (Color.FromArgb(0xC8, DuskPink.R, DuskPink.G, DuskPink.B), 0.5),
                                         (Color.FromArgb(0xA0, DuskTeal.R, DuskTeal.G, DuskTeal.B), 0.8), (Colors.Transparent, 1.0) })
            line.GradientStops.Add(new GradientStop(c, at));
        line.Freeze();
        _duskHorizon = new Border { Height = 1.3, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(22, 0, 22, 1.6), Background = line, Visibility = Visibility.Collapsed };
        stillOver.Children.Add(_duskHorizon);
    }

    /// <summary>The floor's glow: the sun just under the horizon (gold at its heart, then orange, then pink, then gone).</summary>
    static Brush DuskFloor() => new RadialGradientBrush
    {
        Center = new Point(0.5, 1.1), GradientOrigin = new Point(0.5, 1.1), RadiusX = 0.72, RadiusY = 0.98,
        GradientStops =
        {
            new GradientStop(Color.FromArgb(0x52, 0xFF, 0x7A, 0x4A), 0), new GradientStop(Color.FromArgb(0x2C, 0xFF, 0x4F, 0x9A), 0.34),
            new GradientStop(Color.FromArgb(0x16, 0x8B, 0x2F, 0xD0), 0.7), new GradientStop(Colors.Transparent, 1),
        },
    };

    /// <summary>Dusk in or out: its own layers, its type for the clock and the page's title.</summary>
    void ApplyDusk(bool on)
    {
        if (_duskSky == null || _duskHorizon == null) return;
        var v = on ? Visibility.Visible : Visibility.Collapsed;
        _duskSky.Visibility = _duskHorizon.Visibility = v;
        DuskPalms(on && _mode == Mode.Expanded, animate: false);
        Sfx.Theme = on ? "g6" : "";   // its own synth sounds (Services/Sfx)
        DuskHeatFollow(on);             // heat stars (NotchWindow.DuskLife)
        if (!on) { DuskSkyFollow(false); DuskUndress(); return; }
        After(1, () => DuskSkyFollow(true)); // the sky of this hour (once the theme is in place)
        var signage = new FontFamily("Bahnschrift");
        foreach (var tb in new[] { ClockText, PageTitle, OsdTitle })
        {
            Swap(tb, TextBlock.FontFamilyProperty, signage);
            Swap(tb, TextBlock.FontStretchProperty, FontStretches.SemiCondensed);
        }
        Swap(ClockText, TextBlock.ForegroundProperty, F(DuskPeach));
    }

    /// <summary>Opening or closing in Dusk (the palms are in the open notch's background: NotchWindow.DuskPalms).</summary>
    void DuskPalms(bool show, bool animate = true)
    {
        if (_look?.Sunset != true) show = false;
        DuskBeatFollow(show);
    }

    // ───────────── the light that follows the pointer, and the beat ─────────────

    static Brush WhiteGlint() => new RadialGradientBrush
    {
        Center = new Point(0.5, 0.15), GradientOrigin = new Point(0.5, 0.0), RadiusX = 0.5, RadiusY = 0.9,
        GradientStops = { new GradientStop(Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF), 0), new GradientStop(Colors.Transparent, 1) },
    };

    /// <summary>Dusk's glint: warm low sun on the glass, following the pointer.</summary>
    static Brush DuskGlint() => new RadialGradientBrush
    {
        Center = new Point(0.5, 0.2), GradientOrigin = new Point(0.5, 0.0), RadiusX = 0.5, RadiusY = 1.0,
        GradientStops = { new GradientStop(Color.FromArgb(0x2C, 0xFF, 0xC9, 0xA8), 0), new GradientStop(Colors.Transparent, 1) },
    };

    readonly System.Windows.Threading.DispatcherTimer _duskBeat = new(System.Windows.Threading.DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(400) };
    bool _duskBeatWired;
    double _duskLevel = 0.55;

    /// <summary>While Dusk is open, the sky's orbs breathe with what's playing (quick to brighten, slow to settle).</summary>
    void DuskBeatFollow(bool open)
    {
        if (!_duskBeatWired) { _duskBeatWired = true; _duskBeat.Tick += (_, _) => DuskBeatTick(); }
        if (open && _look?.Sunset == true && !Eco) _duskBeat.Start();
        else
        {
            _duskBeat.Stop();
            _duskLevel = 0.55;
            if (_ambients.TryGetValue("g6", out var a) && a.Reacting is { } layer) layer.Opacity = _duskLevel;
        }
    }

    void DuskBeatTick()
    {
        // power saving switched on while it's open: the beat stops too (it starts again with the next opening)
        if (_mode != Mode.Expanded || _look?.Sunset != true || Eco) { DuskBeatFollow(false); return; }
        if (!_ambients.TryGetValue("g6", out var a) || a.Reacting is not { } layer) return;
        // only while the background itself moves: resting, or held still behind the search, it doesn't breathe either
        bool playing = _media.IsPlaying && a.Running;
        var want = TimeSpan.FromMilliseconds(playing ? 33 : 400);
        if (_duskBeat.Interval != want) _duskBeat.Interval = want;
        double target = 0.55;
        if (playing && _audio.TryGetPeaks(out float l, out float r)) target = 0.55 + Math.Min(1, Math.Max(l, r) * 1.6) * 0.45;
        _duskLevel += (target - _duskLevel) * (target > _duskLevel ? 0.45 : 0.1);
        if (Math.Abs(layer.Opacity - _duskLevel) > 0.01) layer.Opacity = _duskLevel;
    }

    // ───────────── the open notch's background ─────────────

    /// <summary>The city at dusk, out of focus: warm lights drifting up slowly, breathing; gold glints; a flare now and then.</summary>
    Ambient DuskAmbient()
    {
        var a = new Ambient(ExpandedSize.Width, ExpandedSize.Height);
        AddDuskPalms(a); // the palms, swaying, behind everything (NotchWindow.DuskPalms)
        AddDuskLife(a);  // the sea, birds, a car on the coast road (NotchWindow.DuskLife)
        Color[] warm = { DuskPink, DuskOrange, DuskCoral, DuskOrchid };
        // the sky's lights: a few large, very soft orbs drifting slowly sideways; with music they breathe with it
        a.AddSheet(r => Ambient.Glow(70 + r.NextDouble() * 50, warm[r.Next(warm.Length)]), count: 4, speed: 0, sway: 28, swaySeconds: 17,
            opacity: 0.3, pulseLow: 0.18, pulseSeconds: 8, reacts: true);
        // embers: fine and few, rising slowly, swaying, never busy
        a.AddSheet(r => Ambient.Glow(2.2 + r.NextDouble() * 1.8, r.Next(3) == 0 ? DuskGold : DuskPeach), count: 12, speed: -7, sway: 9, swaySeconds: 9,
            opacity: 0.6, pulseLow: 0.3, pulseSeconds: 4);
        // a far, slower layer for depth
        a.AddSheet(r => Ambient.Glow(1.6 + r.NextDouble(), DuskCoral), count: 10, speed: -3.5, sway: 5, swaySeconds: 12, opacity: 0.4, pulseLow: 0.2, pulseSeconds: 5, pulseDelay: 2);
        a.AddFlares(r => (Frozen(new RadialGradientBrush(Color.FromRgb(0xFF, 0xF0, 0xE0), Color.FromArgb(0x70, DuskOrange.R, DuskOrange.G, DuskOrange.B))), 7 + r.NextDouble() * 4), everySeconds: 5.5);
        return a;
    }

    // ───────────── the search, dressed for dusk ─────────────

    static LaunchSkin DuskSkin()
    {
        var panel = new RadialGradientBrush { Center = new Point(0.5, 1.2), GradientOrigin = new Point(0.5, 1.2), RadiusX = 1.0, RadiusY = 1.0 };
        panel.GradientStops.Add(new GradientStop(Color.FromArgb(0xFC, 0x3E, 0x12, 0x2C), 0));
        panel.GradientStops.Add(new GradientStop(Color.FromArgb(0xFC, 0x1C, 0x0A, 0x24), 0.55));
        panel.GradientStops.Add(new GradientStop(Color.FromArgb(0xFC, 0x0C, 0x06, 0x13), 1));
        var rim = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        rim.GradientStops.Add(new GradientStop(Color.FromArgb(0x90, DuskGold.R, DuskGold.G, DuskGold.B), 0));
        rim.GradientStops.Add(new GradientStop(Color.FromArgb(0x90, DuskPink.R, DuskPink.G, DuskPink.B), 0.5));
        rim.GradientStops.Add(new GradientStop(Color.FromArgb(0x90, DuskTeal.R, DuskTeal.G, DuskTeal.B), 1));
        var sel = new LinearGradientBrush(Color.FromArgb(0x38, DuskPink.R, DuskPink.G, DuskPink.B), Color.FromArgb(0x1C, DuskOrange.R, DuskOrange.G, DuskOrange.B), 0);
        return new LaunchSkin("g6", Frozen(panel), Frozen(rim), 1, DuskPink, Frozen(sel), null, new Thickness(0), 10,
            DuskPeach, Color.FromArgb(0x9C, 0xFF, 0xC8, 0xD8), Color.FromArgb(0x50, DuskPink.R, DuskPink.G, DuskPink.B), DuskGold,
            Color.FromArgb(0xE0, 0xFF, 0xD6, 0xE2), null, Arrival.Rise, 10);
    }
}
