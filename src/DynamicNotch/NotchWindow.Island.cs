using System.Windows;
using System.Windows.Media;
using DynamicNotch.Controls;

namespace DynamicNotch;

/// <summary>
/// The island: at rest (idle, slim, music, live activities, pop-ups) the notch lets go of the top edge and floats a
/// few pixels below it as a fully rounded capsule with a hairline edge, like a phone's island. Opening it (or a file
/// dragged onto it) melts it back into the edge: it rises, its top corners square off and the ears grow back, all on
/// one spring, so it never jumps. The open notch and its pages are unchanged. In Neon it floats as a HUD chip: cut
/// corners top and bottom, its yellow frame closed all round (NotchWindow.Cyber). Off in Preferences ("Floating island").
/// </summary>
public partial class NotchWindow
{
    const double IslandGap = 7;
    readonly Spring _isle = new(0);
    readonly ScaleTransform _earLScale = new(1, 1), _earRScale = new(1, 1);
    static readonly SolidColorBrush[] HairlineSteps = Enumerable.Range(0, 11).Select(i => { var b = new SolidColorBrush(Color.FromArgb((byte)(0x1C * i / 10), 0xFF, 0xFF, 0xFF)); b.Freeze(); return b; }).ToArray();

    double Isle => Math.Clamp(_isle.Value, 0, 1);

    void WireIsland()
    {
        _shellShake ??= new System.Windows.Media.TranslateTransform();
        Shell.RenderTransform = _shellShake;
        EarL.RenderTransformOrigin = new Point(1, 0);
        EarR.RenderTransformOrigin = new Point(0, 0);
        EarL.RenderTransform = _earLScale;
        EarR.RenderTransform = _earRScale;
        _isle.Configure(0.42, 0.8);
        _isle.Snap(IslandWanted ? 1 : 0);
        ApplyIsland();
    }

    // over a maximized or full-screen window it hugs the edge instead: the tabs and titles under it stay readable
    /// <summary>The island's side slots: only at rest, where the pill widens for them (a pop-up has no room: they'd sit on it).</summary>
    bool IslandSlots => IslandWanted && IsBase(_mode) && _mode != Mode.Game;

    bool IslandWanted => _settings.IslandLook && _mode is not (Mode.Expanded or Mode.Drop) && !_overWindow && !_fgFullscreen;

    /// <summary>Called with every new shape: floating or attached, on its spring.</summary>
    void IslandTarget()
    {
        double want = IslandWanted ? 1 : 0;
        if (Math.Abs(_isle.Target - want) < 0.001) return;
        // letting go of the edge: a soft, slightly springy drop; attaching: quick and clean
        _isle.Configure(want > 0 ? 0.46 : 0.3, want > 0 ? 0.8 : 1.0);
        _isle.Target = want;
        // floating, what's live shows in the island's slots; attached, it tucks into lights again (and back)
        Dispatcher.BeginInvoke(RefreshIslands, System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>The slim bar as an island: a small capsule (a 6-pixel line floating in the air would look lost).</summary>
    void IslandSize(ref double w, ref double h, ref double r)
    {
        if (!IslandWanted || _mode != Mode.Slim || _peek || _pulling) return;
        w = 92; h = 20; r = 10;
    }

    /// <summary>Each frame: how far below the edge, the ears, the hairline (the corners follow in ApplyRadius).</summary>
    void ApplyIsland()
    {
        double a = Isle;
        if (_shellShake != null) _shellShake.Y = IslandGap * a;
        double ear = 1 - a;
        _earLScale.ScaleX = _earLScale.ScaleY = ear;
        _earRScale.ScaleX = _earRScale.ScaleY = ear;
        var line = HairlineSteps[(int)Math.Round(a * 10)];
        if (!ReferenceEquals(Pill.BorderBrush, line)) Pill.BorderBrush = line;
        var t = new Thickness(a > 0.02 && !_cyber ? 1 : 0); // a whole pixel: crisp (Neon: its own frame is the edge)
        if (Pill.BorderThickness != t) Pill.BorderThickness = t;
        IslandBubbles();
    }

    /// <summary>
    /// The side bubbles (an activity on the left, the camera on the right) float with it: round beads level
    /// with the island's middle, the same hairline edge, no ears or neck (nothing hangs from the edge any more).
    /// </summary>
    void IslandBubbles()
    {
        if (!_bubblesWired)
        {
            _bubblesWired = true;
            // hidden, a bubble isn't kept up frame by frame: it catches up the moment it shows
            SecondBubble.IsVisibleChanged += (_, _) => { if (SecondBubble.IsVisible) IslandBubble(SecondBubble, SecondRim, SecondShift, SecondNeck, SecondAura, ref _secondEars); };
            PrivacyBubble.IsVisibleChanged += (_, _) => { if (PrivacyBubble.IsVisible) IslandBubble(PrivacyBubble, PrivacyRim, PrivacyShift, PrivacyNeck, PrivacyAura, ref _privacyEars); };
        }
        IslandBubble(SecondBubble, SecondRim, SecondShift, SecondNeck, SecondAura, ref _secondEars);
        IslandBubble(PrivacyBubble, PrivacyRim, PrivacyShift, PrivacyNeck, PrivacyAura, ref _privacyEars);
    }

    // each bubble's little ears, found once (looking them up every spring frame made new lists each frame)
    ScaleTransform[]? _secondEars, _privacyEars;
    bool _bubblesWired;

    static ScaleTransform[] BubbleEars(System.Windows.Controls.Border b)
    {
        var ears = new List<ScaleTransform>(2);
        if (b.Child is System.Windows.Controls.Grid g)
            foreach (var ear in g.Children.OfType<System.Windows.Shapes.Path>().Where(x => string.IsNullOrEmpty(x.Name)))
            {
                if (ear.RenderTransform is not ScaleTransform es || es.IsFrozen)
                {
                    ear.RenderTransformOrigin = new Point(ear.HorizontalAlignment == HorizontalAlignment.Left ? 1 : 0, 0);
                    ear.RenderTransform = es = new ScaleTransform(1, 1);
                }
                ears.Add(es);
            }
        return ears.ToArray();
    }

    void IslandBubble(System.Windows.Controls.Border b, System.Windows.Controls.Border rim, TranslateTransform shift, System.Windows.Controls.Border neck, System.Windows.Controls.Border aura, ref ScaleTransform[]? ears)
    {
        if (!b.IsVisible) return; // (not seen: nothing to keep up; it catches up when it shows, see above)
        // floating, the glow under it goes (it belonged to something hanging from the edge); its colour stays in the rim
        var av = Isle > 0.5 ? Visibility.Collapsed : Visibility.Visible;
        if (aura.Visibility != av) aura.Visibility = av;
        double a = Isle, h = double.IsNaN(b.Height) ? 22 : b.Height;
        double top = h / 2 * a;
        var cr = new CornerRadius(top, top, h / 2, h / 2);
        if (b.CornerRadius != cr) { b.CornerRadius = cr; rim.CornerRadius = cr; }
        var rt = new Thickness(1, a > 0.5 ? 1 : 0, 1, 1);
        if (rim.BorderThickness != rt) rim.BorderThickness = rt;
        // level with the island's middle
        shift.Y = (IslandGap + Math.Max(0, _sh.Value - h) / 2) * a;
        var line = HairlineSteps[(int)Math.Round(a * 10)];
        if (!ReferenceEquals(b.BorderBrush, line)) b.BorderBrush = line;
        var bt = new Thickness(a > 0.02 ? 1 : 0);
        if (b.BorderThickness != bt) b.BorderThickness = bt;
        neck.Opacity = 1 - a;
        // its little ears shrink away like the notch's
        ears ??= BubbleEars(b);
        foreach (var es in ears) es.ScaleX = es.ScaleY = 1 - a;
    }

    // ───────── what's live beside it, inside it ─────────

    System.Windows.Controls.StackPanel? _slotL, _slotR;
    double _slotLW, _slotRW;
    LiveActivity _slotLShown = LiveActivity.None;
    bool _slotRShown;

    /// <summary>The room each side of the island keeps for its slot: the wider of the two, so the middle stays centred.</summary>
    double SlotSide => Math.Max(_slotLW, _slotRW);

    /// <summary>
    /// With the music: each side keeps only the room its own slot needs (a call's time on one side, a timer on the
    /// other), so the island stays short; everything stays evenly spaced, the middle just sits a little off the
    /// exact centre while the two differ. Elsewhere both sides keep the same room (the middle exactly centred).
    /// </summary>
    bool SlotsSnug => _mode == Mode.Media && IslandWanted;
    double SlotRoomL => SlotsSnug ? _slotLW : SlotSide;
    double SlotRoomR => SlotsSnug ? _slotRW : SlotSide;

    /// <summary>Each slot in the middle of its side (a short one doesn't hug the edge while the other side is wider).</summary>
    void PlaceSlots()
    {
        double side = SlotsSnug ? 0 : SlotSide; // (snug: each slot simply at its own edge)
        if (_slotL != null) { var m = new Thickness(10 + Math.Max(0, side - _slotLW) / 2, 0, 0, 0); if (_slotL.Margin != m) _slotL.Margin = m; }
        if (_slotR != null) { var m = new Thickness(0, 0, 10 + Math.Max(0, side - _slotRW) / 2, 0); if (_slotR.Margin != m) _slotR.Margin = m; }
    }

    System.Windows.Controls.StackPanel SlotPanel(bool left)
    {
        var p = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, Opacity = 0,
            HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right, Margin = new Thickness(left ? 10 : 0, 0, left ? 0 : 10, 0),
            RenderTransformOrigin = new Point(left ? 0 : 1, 0.5), RenderTransform = new ScaleTransform(1, 1),
        };
        Stage.Children.Add(p);
        return p;
    }

    /// <summary>A timer, a download, a call... beside the music: inside the island's left end (it widens for it).</summary>
    void IslandSlotLeft(LiveActivity a)
    {
        // (its room goes only if it's still gone by then: shown again within the fade, it keeps the room it was just given)
        if (a == LiveActivity.None) { if (_slotLShown != LiveActivity.None) { _slotLShown = LiveActivity.None; SlotOut(_slotL, () => { if (_slotLShown == LiveActivity.None) _slotLW = 0; }); } return; }
        _slotL ??= SlotPanel(left: true);
        _slotL.Children.Clear();
        var icon = (FontFamily)FindResource("IconFont");
        _slotL.Children.Add(new System.Windows.Controls.TextBlock { Text = SecondGlyph.Text, FontFamily = icon, FontSize = 11, Foreground = SecondGlyph.Foreground, VerticalAlignment = VerticalAlignment.Center });
        if (!string.IsNullOrEmpty(SecondText.Text))
            _slotL.Children.Add(new System.Windows.Controls.TextBlock { Text = SecondText.Text, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = SecondText.Foreground, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"), Typography = { NumeralAlignment = FontNumeralAlignment.Tabular } }); // digits of one width: a ticking timer stays put
        _slotL.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = Math.Ceiling(_slotL.DesiredSize.Width) + 14;
        bool fresh = _slotLShown == LiveActivity.None;
        _slotLShown = a;
        if (fresh || Math.Abs(w - _slotLW) > 3) { _slotLW = w; ApplyShape(); }
        if (fresh) SlotIn(_slotL);
    }

    bool _slotRWant;                                  // the camera (or Dusk's heat) wants the right end
    LiveActivity _slotRThird = LiveActivity.None;     // the third live thing shown there

    /// <summary>The third live thing, after the main one and the one at the left end (a timer beside a call and the music).</summary>
    LiveActivity ThirdActivity
    {
        get
        {
            if (_mode is not (Mode.Media or Mode.Activity) || _hidden) return LiveActivity.None;
            var list = Activities();
            return list.Count > 2 ? list[2] : LiveActivity.None;
        }
    }

    /// <summary>A live thing's glyph, its few characters and its colour, for the island's right end.</summary>
    (string Glyph, string Text, Brush Brush)? ActivityChip(LiveActivity a) => a switch
    {
        LiveActivity.Timer => ("\uE916", FormatTimer(TimerShown), TimerBrush),
        LiveActivity.Call when _call is { } c => ("\uE717", FormatTimer(DateTime.UtcNow - c.Started), CallBrush),
        LiveActivity.Focus => ("\uE708", (int)_focus.Elapsed.TotalMinutes < 1 ? Services.Loc.T("Focus") : $"{(int)_focus.Elapsed.TotalMinutes}m", FocusPurple),
        LiveActivity.Download => ("\uE896", _files.Items.FirstOrDefault()?.Progress is { } sh ? $"{sh * 100:0}%" : "…", Blue),
        _ => null,
    };

    /// <summary>The island's right end: a third live thing (the timer beside a call and the music), and the camera's light.</summary>
    void IslandSlotRight(bool show)
    {
        var third = IslandSlots ? ThirdActivity : LiveActivity.None;
        var chip = ActivityChip(third);
        _slotRThird = chip != null ? third : LiveActivity.None;
        if (!show && chip == null) { if (_slotRShown) { _slotRShown = false; SlotOut(_slotR, () => { if (!_slotRShown) _slotRW = 0; }); } return; }
        _slotR ??= SlotPanel(left: false);
        _slotR.Children.Clear();
        if (chip is { } ch)
        {
            _slotR.Children.Add(new System.Windows.Controls.TextBlock { Text = ch.Glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 11, Foreground = ch.Brush, VerticalAlignment = VerticalAlignment.Center });
            _slotR.Children.Add(new System.Windows.Controls.TextBlock { Text = ch.Text, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = ch.Brush, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"), Typography = { NumeralAlignment = FontNumeralAlignment.Tabular } });
        }
        if (_privacy.CamInUse) _slotR.Children.Add(new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = Frozen(SlotTone(Color.FromRgb(0x30, 0xD1, 0x58))), Margin = new Thickness(chip != null ? 6 : 0, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        DuskStars(_slotR); // Dusk: the heat stars, before the lights (NotchWindow.DuskLife)
        _slotR.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = Math.Ceiling(_slotR.DesiredSize.Width) + 14;
        bool fresh = !_slotRShown;
        _slotRShown = true;
        if (fresh || Math.Abs(w - _slotRW) > 3) { _slotRW = w; ApplyShape(); }
        if (fresh) SlotIn(_slotR);
    }

    /// <summary>The slots' lights in the theme's colours (Neon: its own orange and green).</summary>
    Color SlotTone(Color c) => _cyber ? CyMap(c) : c;

    /// <summary>In: the island has started to widen, then the light pops in at its end.</summary>
    void SlotIn(System.Windows.Controls.StackPanel? p)
    {
        if (p == null) return;
        var t = TimeSpan.FromMilliseconds(Eco ? 1 : 260);
        p.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, t) { BeginTime = TimeSpan.FromMilliseconds(Eco ? 0 : 150) }); // once the island has made most of its room
        var st = (ScaleTransform)p.RenderTransform;
        var pop = new System.Windows.Media.Animation.DoubleAnimation(0.92, 1, TimeSpan.FromMilliseconds(Eco ? 1 : 320)) { BeginTime = TimeSpan.FromMilliseconds(Eco ? 0 : 150), EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut } };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
    }

    /// <summary>Out: the light fades and shrinks first, then the island eases back to its own width.</summary>
    void SlotOut(System.Windows.Controls.StackPanel? p, Action done)
    {
        if (p == null) { done(); ApplyShape(); return; }
        var t = TimeSpan.FromMilliseconds(Eco ? 1 : 170);
        p.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, t));
        var st = (ScaleTransform)p.RenderTransform;
        st.BeginAnimation(ScaleTransform.ScaleXProperty, new System.Windows.Media.Animation.DoubleAnimation(0.6, t));
        st.BeginAnimation(ScaleTransform.ScaleYProperty, new System.Windows.Media.Animation.DoubleAnimation(0.6, t));
        After(Eco ? 1 : 130, () => { done(); ApplyShape(); });
    }

    /// <summary>The top corners: square on the edge, as round as the bottom ones once floating.</summary>
    double IslandTopRadius(double r) => r * Isle;
}
