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
/// Dusk alive. Everything here is light: nothing runs while Dusk is off, the open notch's life pauses with its background,
/// and battery saving turns the extras off.
///  • The sky follows the real hour: night (deep blue, a neon glow from the city), dawn (pastel), day (the sea's
///    turquoise and a bright sun), golden hour, dusk. The colours glide between them, updated every few minutes.
///  • "Done" banners: a finished focus round, timer, download or found song comes in big, in gold-to-pink, with a sweep of
///    light and a short synth sting.
///  • Radio-style song changes: the new song tunes in (a flicker of signal) on its own frequency.
///  • Life in the open notch: a few birds crossing now and then (by day), a car's lights along the coast road (more at
///    night), the sea shimmering at the bottom.
///  • Heat stars: when the PC works hard, stars fill in the island, one to five, and fade as it calms down.
/// </summary>
public partial class NotchWindow
{
    // ───────────── the sky by the hour ─────────────

    sealed record DuskSky(Color Top, Color Mid, Color Bottom, Color SunCore, Color SunMid, Color LightHigh, Color LightLow, Color Tint, bool Night, bool Day);

    static Color Argb(uint v) => Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);

    // (minute of the day, sky): the colours glide from one to the next
    static readonly (int At, DuskSky Sky)[] DuskHours =
    {
        (0,    new(Argb(0xFC05030F), Argb(0xFA0D0A2A), Argb(0xF8190E3C), Argb(0x46FF2E88), Argb(0x206A2CFF), Argb(0x1E3A5BFF), Argb(0x2022E1D1), Color.FromRgb(0x6A, 0x2E, 0xA8), true, false)),
        (300,  new(Argb(0xFC05030F), Argb(0xFA0D0A2A), Argb(0xF8190E3C), Argb(0x46FF2E88), Argb(0x206A2CFF), Argb(0x1E3A5BFF), Argb(0x2022E1D1), Color.FromRgb(0x6A, 0x2E, 0xA8), true, false)),
        (400,  new(Argb(0xFC140C26), Argb(0xFA2E1B40), Argb(0xF85A3050), Argb(0x58FFB38A), Argb(0x30FF8FB0), Argb(0x22FFD7A8), Argb(0x1C7FD8FF), Color.FromRgb(0xB0, 0x5A, 0x7A), false, false)),
        (540,  new(Argb(0xFC041A26), Argb(0xFA082C3C), Argb(0xF80C4250), Argb(0x48FFE9A8), Argb(0x2A6FE7DA), Argb(0x16FFFFFF), Argb(0x2622E1D1), Color.FromRgb(0x1C, 0x8C, 0x9A), false, true)),
        (960,  new(Argb(0xFC041A26), Argb(0xFA082C3C), Argb(0xF80C4250), Argb(0x48FFE9A8), Argb(0x2A6FE7DA), Argb(0x16FFFFFF), Argb(0x2622E1D1), Color.FromRgb(0x1C, 0x8C, 0x9A), false, true)),
        (1050, new(Argb(0xFC160C1C), Argb(0xFA3A1726), Argb(0xF8662A22), Argb(0x60FFC857), Argb(0x38FF7A3C), Argb(0x22FF9A3C), Argb(0x18FF4F9A), Color.FromRgb(0xB8, 0x52, 0x2E), false, false)),
        (1170, new(Argb(0xFC0B0516), Argb(0xFA1E082A), Argb(0xF83C0C34), Argb(0x52FF7A4A), Argb(0x2CFF4F9A), Argb(0x22C26BFF), Argb(0x2622E1D1), Color.FromRgb(0xB8, 0x2E, 0x6A), false, false)),
        (1290, new(Argb(0xFC05030F), Argb(0xFA0D0A2A), Argb(0xF8190E3C), Argb(0x46FF2E88), Argb(0x206A2CFF), Argb(0x1E3A5BFF), Argb(0x2022E1D1), Color.FromRgb(0x6A, 0x2E, 0xA8), true, false)),
        (1440, new(Argb(0xFC05030F), Argb(0xFA0D0A2A), Argb(0xF8190E3C), Argb(0x46FF2E88), Argb(0x206A2CFF), Argb(0x1E3A5BFF), Argb(0x2022E1D1), Color.FromRgb(0x6A, 0x2E, 0xA8), true, false)),
    };

    static Color SkyMix(Color a, Color b, double k) => Color.FromArgb((byte)(a.A + (b.A - a.A) * k), (byte)(a.R + (b.R - a.R) * k), (byte)(a.G + (b.G - a.G) * k), (byte)(a.B + (b.B - a.B) * k));

    /// <summary>The sky at this moment of the day (between two of the hours above).</summary>
    static DuskSky DuskSkyAt(DateTime now)
    {
        double m = now.Hour * 60 + now.Minute + now.Second / 60.0;
        for (int i = 1; i < DuskHours.Length; i++)
        {
            if (m > DuskHours[i].At) continue;
            var (a0, s0) = DuskHours[i - 1];
            var (a1, s1) = DuskHours[i];
            double k = a1 == a0 ? 0 : (m - a0) / (a1 - a0);
            k = k * k * (3 - 2 * k); // eased: the change is slow at both ends
            return new DuskSky(SkyMix(s0.Top, s1.Top, k), SkyMix(s0.Mid, s1.Mid, k), SkyMix(s0.Bottom, s1.Bottom, k), SkyMix(s0.SunCore, s1.SunCore, k),
                SkyMix(s0.SunMid, s1.SunMid, k), SkyMix(s0.LightHigh, s1.LightHigh, k), SkyMix(s0.LightLow, s1.LightLow, k), SkyMix(s0.Tint, s1.Tint, k),
                k < 0.5 ? s0.Night : s1.Night, k < 0.5 ? s0.Day : s1.Day);
        }
        return DuskHours[0].Sky;
    }

    DuskSky _duskSkyNow = DuskSkyAt(DateTime.Now);
    readonly DispatcherTimer _duskSkyTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(3) };
    bool _duskSkyWired;

    /// <summary>The sky now: the glass, the sun's glow, the sky's lights and the open panel's glow (every few minutes).</summary>
    void DuskSkyFollow(bool on)
    {
        if (!_duskSkyWired) { _duskSkyWired = true; _duskSkyTimer.Tick += (_, _) => DuskPaintSky(); }
        if (!on) { _duskSkyTimer.Stop(); return; }
        DuskPaintSky();
        _duskSkyTimer.Start();
    }

    void DuskPaintSky()
    {
        if (_look?.Sunset != true) { _duskSkyTimer.Stop(); return; }
        var s = _duskSkyNow = DuskSkyAt(DateTime.Now);
        _look.Top = s.Top; _look.Mid = s.Mid; _look.Bottom = s.Bottom; _look.Tint = s.Tint;
        if (_floor != null) _floor.Background = DuskFloorFor(s);
        if (_duskSky != null) _duskSky.Child = DuskSkyLights(s);
        var top = F(s.Top);
        Swap(EarL, Shape.FillProperty, top);
        Swap(EarR, Shape.FillProperty, top);
        Swap(SecondNeck, Border.BackgroundProperty, top);
        Swap(PrivacyNeck, Border.BackgroundProperty, top);
        foreach (var bubble in new[] { SecondBubble, PrivacyBubble }) Swap(bubble, Border.BackgroundProperty, LookBody(_look, null));
        ApplyCustomColors(); // the glass's body in the new colours
        _panelTint = null;
        UpdatePanelTint();
        UpdateRim();
    }

    static Brush DuskFloorFor(DuskSky s)
    {
        var b = new RadialGradientBrush
        {
            Center = new Point(0.5, 1.1), GradientOrigin = new Point(0.5, 1.1), RadiusX = 0.72, RadiusY = 0.98,
            GradientStops = { new GradientStop(s.SunCore, 0), new GradientStop(s.SunMid, 0.34), new GradientStop(Color.FromArgb(0x10, s.SunMid.R, s.SunMid.G, s.SunMid.B), 0.7), new GradientStop(Colors.Transparent, 1) },
        };
        b.Freeze();
        return b;
    }

    static UIElement DuskSkyLights(DuskSky s)
    {
        var g = new Grid();
        g.Children.Add(new Border { Background = Frozen(new RadialGradientBrush { Center = new Point(0.86, 0.0), GradientOrigin = new Point(0.86, 0.0), RadiusX = 0.55, RadiusY = 1.2, GradientStops = { new GradientStop(s.LightHigh, 0), new GradientStop(Colors.Transparent, 1) } }) });
        g.Children.Add(new Border { Background = Frozen(new RadialGradientBrush { Center = new Point(0.06, 1.0), GradientOrigin = new Point(0.06, 1.0), RadiusX = 0.42, RadiusY = 0.9, GradientStops = { new GradientStop(s.LightLow, 0), new GradientStop(Colors.Transparent, 1) } }) });
        return g;
    }

    // ───────────── "done" banners and radio-style song changes ─────────────

    object? _duskTitleBefore;
    bool _duskDressed;

    /// <summary>A pop-up in Dusk: a finished thing comes in as a banner; a new song tunes in like a radio.</summary>
    void DuskDress(OsdRequest r, bool sameKind)
    {
        if (_look?.Sunset != true)
        {
            if (_duskDressed) { DuskUndress(); }
            return;
        }
        DuskUndress();
        bool banner = r.Kind is "timer" or "download" or "song" or "smart-break" && !r.Level.HasValue;
        bool radio = r.Kind == "media";
        if (!banner && !radio) return;
        _duskDressed = true;
        _duskTitleBefore = OsdTitle.ReadLocalValue(TextBlock.ForegroundProperty);
        if (banner)
        {
            // the title in gold running into flamingo pink, a sweep of light, and the sting
            var gold = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
            gold.GradientStops.Add(new GradientStop(DuskGold, 0));
            gold.GradientStops.Add(new GradientStop(Color.FromRgb(0xFF, 0x8A, 0x6A), 0.55));
            gold.GradientStops.Add(new GradientStop(DuskPink, 1));
            gold.Freeze();
            OsdTitle.Foreground = gold;
            if (!sameKind)
            {
                FeelSound(Sfx.Cue.Done);
                After(320, () => { if (_mode == Mode.Osd && ReferenceEquals(_osd, r) && !Eco) PlayShine(DuskGold); });
            }
            return;
        }
        // radio: the song on its own frequency (always the same one for the same song), tuning in
        int hash = 0;
        foreach (char c in (r.Title ?? "") + (r.Subtitle ?? "")) hash = hash * 31 + c;
        double fm = 88.1 + Math.Abs(hash % 199) / 10.0;
        OsdRight.Text = fm.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " FM";
        OsdRight.Foreground = Frozen(DuskGold);
        var peach = new LinearGradientBrush(DuskPeach, Color.FromRgb(0xFF, 0x9C, 0xC4), 0);
        peach.Freeze();
        OsdTitle.Foreground = peach;
        if (!sameKind && !Eco)
        {
            var tune = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(300) };
            foreach (var (v, ms) in new[] { (0.0, 0), (0.7, 50), (0.15, 100), (0.85, 160), (0.4, 210), (1.0, 300) })
                tune.KeyFrames.Add(new DiscreteDoubleKeyFrame(v, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms))));
            tune.FillBehavior = FillBehavior.Stop;
            OsdTextPanel.BeginAnimation(OpacityProperty, tune);
        }
    }

    void DuskUndress()
    {
        if (!_duskDressed) return;
        _duskDressed = false;
        if (_duskTitleBefore is null or DependencyProperty) OsdTitle.ClearValue(TextBlock.ForegroundProperty);
        else if (_duskTitleBefore == DependencyProperty.UnsetValue) OsdTitle.ClearValue(TextBlock.ForegroundProperty);
        else OsdTitle.Foreground = (Brush)_duskTitleBefore;
    }

    // ───────────── life in the open notch ─────────────

    /// <summary>Birds now and then (by day), a car's lights on the coast road (more at night), the sea shimmering.</summary>
    void AddDuskLife(Ambient a)
    {
        double w = ExpandedSize.Width, h = ExpandedSize.Height;
        // the sea, at the bottom: three rows of glints, breathing out of step, drifting a touch
        var sea = new Canvas { Width = w, Height = h, IsHitTestVisible = false };
        var moves = new List<(IAnimatable, DependencyProperty, AnimationTimeline)>();
        var rnd = new Random(5);
        for (int group = 0; group < 3; group++)
        {
            var layer = new Canvas { Width = w, Height = h, CacheMode = new BitmapCache(1.5), Opacity = 0.5 };
            for (int i = 0; i < 9; i++)
            {
                double len = 3 + rnd.NextDouble() * 9, y = h - 15 + rnd.NextDouble() * 9;
                var c = rnd.Next(3) switch { 0 => DuskPeach, 1 => DuskPink, _ => DuskTeal };
                var glint = new Rectangle { Width = len, Height = 0.9, RadiusX = 0.45, RadiusY = 0.45, Fill = Frozen(new LinearGradientBrush(Color.FromArgb(0, c.R, c.G, c.B), Color.FromArgb(0xD0, c.R, c.G, c.B), 0) { SpreadMethod = GradientSpreadMethod.Reflect, EndPoint = new Point(0.5, 0) }) };
                Canvas.SetLeft(glint, 60 + rnd.NextDouble() * (w - 120));
                Canvas.SetTop(glint, y);
                layer.Children.Add(glint);
            }
            sea.Children.Add(layer);
            moves.Add((layer, OpacityProperty, new DoubleAnimation(0.75, 0.1, TimeSpan.FromSeconds(2.2 + group * 0.7)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromSeconds(group * 0.9), EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } }));
            var drift = new TranslateTransform();
            layer.RenderTransform = drift;
            moves.Add((drift, TranslateTransform.XProperty, new DoubleAnimation(-3, 3, TimeSpan.FromSeconds(6 + group * 2)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } }));
        }
        a.AddPiece(sea, moves, fps: 20);

        a.AddEvents(Birds, minSeconds: 24, maxSeconds: 42);
        a.AddEvents(CarLights, minSeconds: 11, maxSeconds: 24);
    }

    /// <summary>A small flight of birds crossing the sky, wings beating out of step, the far ones smaller and fainter.</summary>
    (List<(IAnimatable, DependencyProperty, AnimationTimeline)>, TimeSpan)? Birds(Canvas layer, Random rnd)
    {
        if (_duskSkyNow.Night) return null; // they're asleep
        double w = layer.Width, h = layer.Height;
        var moves = new List<(IAnimatable, DependencyProperty, AnimationTimeline)>();
        bool leftToRight = rnd.Next(2) == 0;
        double altitude = h * (0.14 + rnd.NextDouble() * 0.22);
        double seconds = 10 + rnd.NextDouble() * 4;
        int count = 3 + rnd.Next(4);
        var wing = Geometry.Parse("M-7,0.6 C-5,-2.6 -2.2,-2.8 0,0.4 C2.2,-2.8 5,-2.6 7,0.6");
        wing.Freeze();
        Color ink = _duskSkyNow.Day ? Color.FromRgb(0x0E, 0x22, 0x2E) : Color.FromRgb(0x14, 0x07, 0x18);
        for (int i = 0; i < count; i++)
        {
            double depth = 0.55 + rnd.NextDouble() * 0.5;                  // the far ones smaller, fainter, slower
            double dx = (i * 16 + rnd.NextDouble() * 8) * (leftToRight ? -1 : 1), dy = (i % 2 == 0 ? -1 : 1) * (4 + i * 3) + rnd.NextDouble() * 4;
            var bird = new Path { Data = wing, Stroke = Frozen(Color.FromArgb((byte)(0x80 + depth * 0x60), ink.R, ink.G, ink.B)), StrokeThickness = 1.15, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, Opacity = 0 };
            var flap = new ScaleTransform(depth * (leftToRight ? 1 : -1), depth);
            var bob = new TranslateTransform();
            var fly = new TranslateTransform();
            bird.RenderTransform = new TransformGroup { Children = { flap, bob, fly } };
            layer.Children.Add(bird);
            double from = leftToRight ? -30 + dx : w + 30 + dx, to = leftToRight ? w + 30 + dx : -30 + dx;
            var life = TimeSpan.FromSeconds(seconds / (0.8 + depth * 0.2));
            moves.Add((fly, TranslateTransform.XProperty, new DoubleAnimation(from, to, life)));
            moves.Add((fly, TranslateTransform.YProperty, new DoubleAnimation(altitude + dy, altitude + dy - 8 - rnd.NextDouble() * 8, life) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } }));
            // wings: beats, then a glide, then beats (the way gulls fly)
            double beat = 0.32 + rnd.NextDouble() * 0.1;
            var wings = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(beat * 8), RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromSeconds(rnd.NextDouble() * beat) };
            for (int k = 0; k <= 8; k++)
            {
                bool gliding = k >= 5;
                double v = gliding ? depth * 0.92 : (k % 2 == 0 ? depth : -depth * 0.45);
                wings.KeyFrames.Add(new EasingDoubleKeyFrame(v, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(beat * k)), new SineEase { EasingMode = EasingMode.EaseInOut }));
            }
            moves.Add((flap, ScaleTransform.ScaleYProperty, wings));
            moves.Add((bob, TranslateTransform.YProperty, new DoubleAnimation(-1.2, 1.2, TimeSpan.FromSeconds(beat * 2)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever }));
            var seen = new DoubleAnimationUsingKeyFrames { Duration = life };
            seen.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
            seen.KeyFrames.Add(new LinearDoubleKeyFrame(0.55 + depth * 0.45, KeyTime.FromPercent(0.08)));
            seen.KeyFrames.Add(new LinearDoubleKeyFrame(0.55 + depth * 0.45, KeyTime.FromPercent(0.9)));
            seen.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
            moves.Add((bird, OpacityProperty, seen));
        }
        return (moves, TimeSpan.FromSeconds(seconds * 1.4));
    }

    /// <summary>A car on the coast road far away: its lights slide along the horizon (headlights one way, tail lights the other).</summary>
    (List<(IAnimatable, DependencyProperty, AnimationTimeline)>, TimeSpan)? CarLights(Canvas layer, Random rnd)
    {
        if (_duskSkyNow.Day && rnd.Next(3) != 0) return null; // fewer by day
        double w = layer.Width, h = layer.Height;
        var moves = new List<(IAnimatable, DependencyProperty, AnimationTimeline)>();
        bool right = rnd.Next(2) == 0;
        bool head = rnd.Next(2) == 0;
        Color c = head ? Color.FromRgb(0xFF, 0xF1, 0xD6) : Color.FromRgb(0xFF, 0x3D, 0x4F);
        var car = new Canvas { Width = 40, Height = 10, Opacity = 0, IsHitTestVisible = false };
        car.Children.Add(new Ellipse { Width = 40, Height = 9, Fill = Frozen(new RadialGradientBrush(Color.FromArgb(0x55, c.R, c.G, c.B), Colors.Transparent)) });
        var beam = new LinearGradientBrush { StartPoint = new Point(right ? 0 : 1, 0.5), EndPoint = new Point(right ? 1 : 0, 0.5) };
        beam.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 0));
        beam.GradientStops.Add(new GradientStop(Color.FromArgb(0xC0, c.R, c.G, c.B), 0.75));
        beam.GradientStops.Add(new GradientStop(Colors.White, 1));
        beam.Freeze();
        var light = new Rectangle { Width = 18, Height = 1.6, RadiusX = 0.8, RadiusY = 0.8, Fill = beam };
        Canvas.SetLeft(light, 11);
        Canvas.SetTop(light, 3.7);
        car.Children.Add(light);
        var move = new TranslateTransform(0, h - 11);
        car.RenderTransform = move;
        layer.Children.Add(car);
        double seconds = 3.6 + rnd.NextDouble() * 1.6;
        moves.Add((move, TranslateTransform.XProperty, new DoubleAnimation(right ? 30 : w - 70, right ? w - 70 : 30, TimeSpan.FromSeconds(seconds))));
        var seen = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(seconds) };
        seen.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        seen.KeyFrames.Add(new LinearDoubleKeyFrame(_duskSkyNow.Night ? 1 : 0.75, KeyTime.FromPercent(0.15)));
        seen.KeyFrames.Add(new LinearDoubleKeyFrame(_duskSkyNow.Night ? 1 : 0.75, KeyTime.FromPercent(0.8)));
        seen.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
        moves.Add((car, OpacityProperty, seen));
        return (moves, TimeSpan.FromSeconds(seconds + 0.2));
    }

    // ───────────── heat stars ─────────────

    int _duskHeat, _duskHeatPending, _duskHeatSeen;
    CpuMeter? _duskCpu;
    readonly DispatcherTimer _duskHeatTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3) };
    bool _duskHeatWired;

    void DuskHeatFollow(bool on)
    {
        if (!_duskHeatWired) { _duskHeatWired = true; _duskHeatTimer.Tick += (_, _) => DuskHeatTick(); }
        // the timer runs with Dusk even in power-saving mode (it reads nothing there), so the stars come back when it ends
        if (on) { _duskCpu ??= new CpuMeter(); var first = _duskCpu; _ = Task.Run(() => { try { first.Read(); } catch { } }); _duskHeatTimer.Start(); return; } // (opening the counter can take a moment: not on the screen's thread)
        _duskHeatTimer.Stop();
        if (_duskHeat != 0) { _duskHeat = 0; UpdatePrivacyBubble(); }
    }

    /// <summary>How hard the PC works, as stars (the processor, or the memory running out): two readings in a row to change.</summary>
    bool _duskReading;

    async void DuskHeatTick()
    {
        if (_look?.Sunset != true || _duskCpu == null) { DuskHeatFollow(false); return; }
        // power saving: no readings and no stars (also when it starts while Dusk is already on)
        if (Eco) { _duskHeatPending = 0; if (_duskHeat != 0) { _duskHeat = 0; UpdatePrivacyBubble(); } return; }
        if (_duskReading) return;
        _duskReading = true;
        var meter = _duskCpu;
        double cpu;
        try { cpu = await Task.Run(() => { try { return meter.Read(); } catch { return 0; } }); } // (read off the screen's thread)
        finally { _duskReading = false; }
        if (_look?.Sunset != true) return;
        var mem = new Interop.Native.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Interop.Native.MEMORYSTATUSEX>() };
        double ram = Interop.Native.GlobalMemoryStatusEx(ref mem) ? mem.dwMemoryLoad : 0;
        int byCpu = cpu >= 95 ? 5 : cpu >= 85 ? 4 : cpu >= 72 ? 3 : cpu >= 58 ? 2 : cpu >= 45 ? 1 : 0;
        int byRam = ram >= 96 ? 4 : ram >= 92 ? 3 : ram >= 88 ? 2 : ram >= 84 ? 1 : 0;
        int want = Math.Max(byCpu, byRam);
        if (want == _duskHeat) { _duskHeatPending = want; return; }
        if (want != _duskHeatPending) { _duskHeatPending = want; return; } // once could be a blip
        _duskHeatSeen = _duskHeat;
        _duskHeat = want;
        UpdatePrivacyBubble();
    }

    /// <summary>The stars in the island's right slot: lit ones gold, the rest faint; a new one blinks as it lights.</summary>
    void DuskStars(Panel slot)
    {
        if (_look?.Sunset != true || _duskHeat <= 0) return;
        var icon = (FontFamily)FindResource("IconFont");
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, slot.Children.Count > 0 ? 7 : 0, 0) };
        for (int i = 0; i < 5; i++)
        {
            bool lit = i < _duskHeat;
            var star = new TextBlock
            {
                Text = lit ? "" : "", FontFamily = icon, FontSize = 8.5, Margin = new Thickness(i == 0 ? 0 : 1.5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Foreground = Frozen(lit ? DuskGold : Color.FromArgb(0x48, 0xFF, 0xE3, 0xCF)),
            };
            if (lit && i >= _duskHeatSeen && !Eco)
            {
                var blink = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(1400) };
                foreach (var (v, ms) in new[] { (1.0, 0), (0.15, 230), (1.0, 460), (0.15, 690), (1.0, 920), (0.4, 1150), (1.0, 1400) })
                    blink.KeyFrames.Add(new DiscreteDoubleKeyFrame(v, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms))));
                star.BeginAnimation(OpacityProperty, blink);
            }
            row.Children.Add(star);
        }
        _duskHeatSeen = _duskHeat;
        slot.Children.Insert(0, row);
    }
}
