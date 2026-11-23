using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DynamicNotch;

/// <summary>
/// Hover hints drawn inside the notch. Windows' own tooltips are white, slow, and open in a separate window that the
/// always-on-top notch covers, so they are cancelled and shown here instead:
///  • quick: the notch watches the pointer itself (no waiting for Windows' hover delay): a hint shows after a short
///    rest, and once one is up, the next one shows at once and the bubble glides over to it (like a dock's labels);
///  • placed where it reads best: under the thing, above it when there's no room, to the right of a side rail's
///    icons, to the left of a small button at the right edge; a small pointer always points at what it's about;
///  • easy to read: the first part ("HyperX Cloud II Core") stands out, the details after a "·" are softer;
///  • drawn once and moved as a picture (its shadow isn't recomputed on every frame), so it never stutters;
///  • it wears the theme: glass with a hint of your colour, or Neon's square yellow frame.
/// It leaves when the pointer does, on a click or a scroll, or by itself after it's had time to be read.
/// </summary>
public partial class NotchWindow
{
    FrameworkElement? _tipOwner;
    static bool _tipHandler;
    readonly System.Windows.Threading.DispatcherTimer _tipLife = new() { Interval = TimeSpan.FromSeconds(3.5) };
    readonly System.Windows.Threading.DispatcherTimer _tipRest = new() { Interval = TimeSpan.FromMilliseconds(260) };
    Grid _tipHost = null!;
    Border _tipCaret = null!;
    readonly ScaleTransform _tipHostScale = new();
    readonly TranslateTransform _tipHostShift = new();
    DateTime _tipGoneAt = DateTime.MinValue;
    enum TipSide { Below, Above, Right, Left }

    void WireTips()
    {
        if (!_tipHandler)
        {
            _tipHandler = true;
            EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.ToolTipOpeningEvent,
                new ToolTipEventHandler((s, e) => { if (s is FrameworkElement el && Window.GetWindow(el) == this) OnTipOpening(el, e); }), true);
            // the pointer arriving on anything: the hint is worked out once it rests (no waiting for Windows)
            EventManager.RegisterClassHandler(typeof(FrameworkElement), Mouse.MouseEnterEvent,
                new MouseEventHandler((s, _) => { if (s is FrameworkElement el && Window.GetWindow(el) == this) TipPointerMoved(); }), true);
        }
        PreviewMouseDown += (_, _) => HideTip();
        PreviewMouseWheel += (_, _) => HideTip();
        _tipLife.Tick += (_, _) => HideTip();
        _tipRest.Tick += (_, _) => { _tipRest.Stop(); TipResolve(); };

        // the bubble and its pointer live in one holder that moves and scales as a single picture
        if (Tip.Parent is Grid root)
        {
            int at = root.Children.IndexOf(Tip);
            root.Children.Remove(Tip);
            Tip.RenderTransform = Transform.Identity;
            Tip.Margin = new Thickness(0);
            Tip.Visibility = Visibility.Visible;
            Tip.Opacity = 1;
            Tip.HorizontalAlignment = HorizontalAlignment.Left;
            Tip.VerticalAlignment = VerticalAlignment.Top;
            _tipCaret = new Border { Width = 9, Height = 9, BorderThickness = new Thickness(1), RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(45),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            _tipHost = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false,
                Visibility = Visibility.Collapsed, Opacity = 0, ClipToBounds = false,
                RenderTransform = new TransformGroup { Children = { _tipHostScale, _tipHostShift } },
                CacheMode = new BitmapCache { SnapsToDevicePixels = true, RenderAtScale = 1 },
            };
            Panel.SetZIndex(_tipHost, 100);
            _tipHost.Children.Add(_tipCaret); // behind the bubble: only its point shows
            _tipHost.Children.Add(Tip);
            root.Children.Insert(at, _tipHost);
        }
    }

    // ───────────── when ─────────────

    /// <summary>The pointer moved onto something: a hint once it rests there (at once if one was just showing).</summary>
    void TipPointerMoved()
    {
        if (_mode != Mode.Expanded) return;
        bool warm = _tipOwner != null || (DateTime.UtcNow - _tipGoneAt).TotalMilliseconds < 600;
        _tipRest.Stop();
        _tipRest.Interval = TimeSpan.FromMilliseconds(warm ? 40 : 260);
        _tipRest.Start();
    }

    /// <summary>The innermost thing under the pointer that has a hint (the one showing stays if the pointer is still on it).</summary>
    void TipResolve()
    {
        if (_mode != Mode.Expanded) return;
        for (var d = Mouse.DirectlyOver as DependencyObject; d != null && d != Root; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (ReferenceEquals(d, _tipOwner)) return; // still on the one showing (a hint set by hand keeps its words)
            if (d is FrameworkElement fe && ToolTipService.GetToolTip(fe) is { } t && t.ToString() is { } text && !string.IsNullOrWhiteSpace(text))
            {
                if (TipWanted(fe)) ShowTip(fe, text);
                return;
            }
        }
    }

    /// <summary>Windows wants to show its own tooltip: never (the notch shows its own, see <see cref="TipResolve"/>).</summary>
    void OnTipOpening(FrameworkElement el, ToolTipEventArgs e)
    {
        e.Handled = true;
        if (_tipOwner == null && !_tipRest.IsEnabled) TipResolve(); // (a hint set while the pointer was already resting)
    }

    bool TipWanted(FrameworkElement el)
    {
        if (_mode != Mode.Expanded || !el.IsMouseOver || !el.IsVisible || _animating) return false;
        if ((DateTime.UtcNow - _expandedAt).TotalMilliseconds < 600) return false; // just opened: things are still arriving
        // faded out, or inside something faded out (a card making way for another)
        for (DependencyObject? d = el; d != null && d != Root; d = VisualTreeHelper.GetParent(d))
            if (d is UIElement u && (u.Opacity < 0.3 || u.Visibility != Visibility.Visible)) return false;
        // a title that fits needs no hint repeating it
        if (el is TextBlock tb && tb.TextWrapping == TextWrapping.NoWrap && tb.TextTrimming != TextTrimming.None && !Trimmed(tb)) return false;
        return true;
    }

    static bool Trimmed(TextBlock tb)
    {
        if (string.IsNullOrEmpty(tb.Text) || tb.ActualWidth <= 0) return false;
        var ft = new FormattedText(tb.Text, System.Globalization.CultureInfo.CurrentCulture, tb.FlowDirection,
            new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch), tb.FontSize, Brushes.White,
            VisualTreeHelper.GetDpi(tb).PixelsPerDip);
        return ft.WidthIncludingTrailingWhitespace > tb.ActualWidth - tb.Padding.Left - tb.Padding.Right + 0.5;
    }

    // ───────────── how ─────────────

    /// <summary>Shows a hint for <paramref name="el"/> now (it hides when the pointer leaves it).</summary>
    void ShowTip(FrameworkElement el, string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || _mode != Mode.Expanded || _tipHost == null) return;
        bool wasShowing = _tipOwner != null && _tipHost.Visibility == Visibility.Visible && _tipHost.Opacity > 0.4;
        var oldAt = new Point(_tipHost.Margin.Left + _tipHostShift.X, _tipHost.Margin.Top + _tipHostShift.Y);
        if (_tipOwner != el)
        {
            if (_tipOwner != null) { _tipOwner.MouseLeave -= OnTipOwnerLeave; _tipOwner.IsVisibleChanged -= OnTipOwnerGone; }
            _tipOwner = el;
            el.MouseLeave += OnTipOwnerLeave;
            el.IsVisibleChanged += OnTipOwnerGone; // its element went (a re-drawn card): so does the hint
        }

        TipDress();
        TipWords(text);
        if (_tipHost.CacheMode is BitmapCache bc && Math.Abs(bc.RenderAtScale - Math.Max(1, _scale)) > 0.01) bc.RenderAtScale = Math.Max(1, _scale); // sharp on any screen scale
        _tipHost.Visibility = Visibility.Visible;
        _tipLife.Stop();
        _tipLife.Interval = TimeSpan.FromSeconds(Math.Clamp(2.4 + text.Length * 0.045, 3, 7)); // time to read it
        _tipLife.Start();
        Tip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Size tip = Tip.DesiredSize;

        // where: under it; above it when there's no room; right of a side rail's icon; left of a small button at the right edge
        Point at = el.TranslatePoint(new Point(0, 0), Root);
        double ew = el.ActualWidth, eh = el.ActualHeight, cx = at.X + ew / 2, cy = at.Y + eh / 2;
        double panelLeft = (Root.ActualWidth - ExpandedSize.Width) / 2 + 6, panelRight = panelLeft + ExpandedSize.Width - 12, panelBottom = ExpandedSize.Height - 4;
        const double gap = 8;
        TipSide side;
        if (at.X - panelLeft < 62 && ew < 70 && at.X + ew + gap + tip.Width < panelRight) side = TipSide.Right;            // a side rail
        else if (ew < 60 && at.X + ew > panelRight - 70 && at.X - gap - tip.Width > panelLeft) side = TipSide.Left;      // a small button at the right edge
        else side = at.Y + eh + gap + tip.Height <= panelBottom ? TipSide.Below : TipSide.Above;

        double x, y;
        switch (side)
        {
            case TipSide.Right: x = at.X + ew + gap; y = Math.Clamp(cy - tip.Height / 2, 2, Math.Max(2, panelBottom - tip.Height)); break;
            case TipSide.Left: x = at.X - gap - tip.Width; y = Math.Clamp(cy - tip.Height / 2, 2, Math.Max(2, panelBottom - tip.Height)); break;
            case TipSide.Below: x = Math.Clamp(cx - tip.Width / 2, panelLeft, Math.Max(panelLeft, panelRight - tip.Width)); y = at.Y + eh + gap; break;
            default: x = Math.Clamp(cx - tip.Width / 2, panelLeft, Math.Max(panelLeft, panelRight - tip.Width)); y = Math.Max(2, at.Y - gap - tip.Height); break;
        }
        x = Math.Round(x); y = Math.Round(y);

        // the pointer: on the side facing the element, lined up with its middle (kept clear of the rounded corners)
        double r = Tip.CornerRadius.TopLeft, cs = _tipCaret.Width;
        double px = Math.Clamp(cx - x - cs / 2, r, Math.Max(r, tip.Width - r - cs)), py = Math.Clamp(cy - y - cs / 2, r, Math.Max(r, tip.Height - r - cs));
        _tipCaret.Margin = side switch
        {
            TipSide.Below => new Thickness(px, -cs / 2 + 0.5, 0, 0),
            TipSide.Above => new Thickness(px, tip.Height - cs / 2 - 0.5, 0, 0),
            TipSide.Right => new Thickness(-cs / 2 + 0.5, py, 0, 0),
            _ => new Thickness(tip.Width - cs / 2 - 0.5, py, 0, 0),
        };
        // it grows out of its pointer
        _tipHost.RenderTransformOrigin = new Point(Math.Clamp((_tipCaret.Margin.Left + cs / 2) / Math.Max(1, tip.Width), 0, 1), Math.Clamp((_tipCaret.Margin.Top + cs / 2) / Math.Max(1, tip.Height), 0, 1));
        _tipHost.Margin = new Thickness(x, y, 0, 0);

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        if (Eco)
        {
            _tipHost.BeginAnimation(OpacityProperty, null); _tipHost.Opacity = 1;
            return;
        }
        if (wasShowing)
        {
            // from one hint to the next: the bubble glides over, its words cross-fade
            var glide = TimeSpan.FromMilliseconds(210);
            _tipHostShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(oldAt.X - x, 0, glide) { EasingFunction = ease });
            _tipHostShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(oldAt.Y - y, 0, glide) { EasingFunction = ease });
            TipText.BeginAnimation(OpacityProperty, new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(180)));
            _tipHostScale.BeginAnimation(ScaleTransform.ScaleXProperty, null); _tipHostScale.ScaleX = 1;
            _tipHostScale.BeginAnimation(ScaleTransform.ScaleYProperty, null); _tipHostScale.ScaleY = 1;
            _tipHost.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(100)));
            return;
        }
        // a fresh one: it pops out of its pointer with a little spring, nudging in from the element's side
        var pop = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut };
        var span = TimeSpan.FromMilliseconds(230);
        _tipHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease });
        _tipHostScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.86, 1, span) { EasingFunction = pop });
        _tipHostScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.86, 1, span) { EasingFunction = pop });
        var (nx, ny) = side switch { TipSide.Below => (0.0, -4.0), TipSide.Above => (0.0, 4.0), TipSide.Right => (-4.0, 0.0), _ => (4.0, 0.0) };
        _tipHostShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(nx, 0, span) { EasingFunction = ease });
        _tipHostShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(ny, 0, span) { EasingFunction = ease });
        TipText.BeginAnimation(OpacityProperty, null);
        TipText.Opacity = 1;
    }

    /// <summary>The words: the first part bright and a touch bolder, the details after "·" (or a new line) softer.</summary>
    void TipWords(string text)
    {
        TipText.Inlines.Clear();
        int dot = text.IndexOf(" · ", StringComparison.Ordinal), nl = text.IndexOf('\n');
        int cut = dot > 0 && (nl < 0 || dot < nl) ? dot : nl;
        if (cut <= 0 || cut > 60) { TipText.Inlines.Add(new Run(text)); return; }
        TipText.Inlines.Add(new Run(text[..cut]) { FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
        TipText.Inlines.Add(new Run(text[cut..]) { Foreground = Frozen(Color.FromArgb(0xA8, 0xFF, 0xFF, 0xFF)) });
    }

    /// <summary>The theme's dress: glass with a touch of your colour, or Neon's square frame.</summary>
    void TipDress()
    {
        Color edge, fill;
        if (_cyber)
        {
            var y = CustomEdge ?? CyYellow;
            edge = Color.FromArgb(0xC8, y.R, y.G, y.B);
            fill = Color.FromArgb(0xFA, 0x05, 0x08, 0x0E);
            Tip.CornerRadius = new CornerRadius(1);
            _tipCaret.CornerRadius = new CornerRadius(0);
        }
        else
        {
            // a plate tinted with the theme's colour (lighter than the panel, so it reads in front of it), a lit edge
            var a = _accent == Colors.White ? Color.FromRgb(0xC8, 0xC8, 0xD0) : _accent;
            edge = Color.FromArgb(0x66, a.R, a.G, a.B);
            var plate = Mix(Color.FromRgb(0x2A, 0x29, 0x31), a, 0.2);
            fill = Color.FromArgb(0xF5, plate.R, plate.G, plate.B);
            Tip.CornerRadius = new CornerRadius(9);
            _tipCaret.CornerRadius = new CornerRadius(1.5);
        }
        var fillBrush = Frozen(fill);
        if (Tip.Background is not SolidColorBrush fb || fb.Color != fill) Tip.Background = fillBrush;
        if (Tip.BorderBrush is not SolidColorBrush eb || eb.Color != edge) Tip.BorderBrush = Frozen(edge);
        _tipCaret.Background = Tip.Background;
        _tipCaret.BorderBrush = Tip.BorderBrush;
        TipText.FontSize = 11.5;
    }

    void OnTipOwnerLeave(object sender, MouseEventArgs e) => HideTip();
    void OnTipOwnerGone(object sender, DependencyPropertyChangedEventArgs e) { if (e.NewValue is false) HideTip(); }

    void HideTip()
    {
        _tipPending = null;
        _tipLife.Stop();
        if (_tipOwner != null) { _tipOwner.MouseLeave -= OnTipOwnerLeave; _tipOwner.IsVisibleChanged -= OnTipOwnerGone; _tipOwner = null; _tipGoneAt = DateTime.UtcNow; }
        if (_tipHost == null || _tipHost.Visibility != Visibility.Visible) return;
        // gone quickly, a touch smaller; the next hint (if the pointer moves on to one) can take over mid-way
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(Eco ? 1 : 120));
        fade.Completed += (_, _) => { if (_tipOwner == null) _tipHost.Visibility = Visibility.Collapsed; };
        _tipHost.BeginAnimation(OpacityProperty, fade);
        if (!Eco)
        {
            _tipHostScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, TimeSpan.FromMilliseconds(120)));
            _tipHostScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, TimeSpan.FromMilliseconds(120)));
        }
    }

    FrameworkElement? _tipPending;
}
