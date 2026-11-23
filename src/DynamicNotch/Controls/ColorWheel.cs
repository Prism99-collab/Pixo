using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace DynamicNotch.Controls;

/// <summary>
/// A hue / saturation disc: hue goes round, colour deepens from the white centre to the rim. The disc is one
/// bitmap drawn once for its size (a few thousand pixels, a millisecond), so dragging the marker over it costs
/// nothing but moving one small circle. Drag or click; works with a finger too.
/// </summary>
public sealed class ColorWheel : Grid
{
    readonly Image _disc = new() { IsHitTestVisible = false };
    readonly Ellipse _marker = new()
    {
        Width = 18, Height = 18, Stroke = Brushes.White, StrokeThickness = 2.5, IsHitTestVisible = false,
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, RenderTransformOrigin = new Point(0.5, 0.5),
        Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 1, Opacity = 0.55, Color = Colors.Black },
    };
    readonly TranslateTransform _markerAt = new();
    readonly ScaleTransform _markerGrow = new(1, 1); // the marker grows a little under your finger while you drag
    int _drawnPx;

    public double Hue { get; private set; }
    public double Saturation { get; private set; }
    /// <summary>Scrolling fine-tunes (off: the scroll passes on, as for a colour left on Auto).</summary>
    public bool ScrollEnabled { get; set; } = true;
    /// <summary>True while a scroll is being handled (a nudge, not a pick).</summary>
    public bool Scrolling { get; private set; }
    readonly System.Windows.Threading.DispatcherTimer _commitSoon = new() { Interval = TimeSpan.FromMilliseconds(600) };

    /// <summary>While dragging: hue (0–360), saturation (0–1).</summary>
    public event Action<double, double>? Picked;
    /// <summary>The finger / button was lifted.</summary>
    public event Action? Committed;

    public ColorWheel()
    {
        Background = Brushes.Transparent;
        Cursor = Cursors.Cross;
        _marker.RenderTransform = new TransformGroup { Children = { _markerGrow, _markerAt } };
        _commitSoon.Tick += (_, _) => { _commitSoon.Stop(); Committed?.Invoke(); };
        Children.Add(_disc);
        Children.Add(_marker);
    }

    /// <summary>Where the marker sits and its colour; glide: it travels there (a new target, a preset), else it's put there.</summary>
    public void SetMarker(double hue, double sat, Color fill, bool glide = false)
    {
        Hue = hue;
        Saturation = sat;
        _marker.Fill = new SolidColorBrush(fill);
        PlaceMarker(glide && !IsMouseCaptured);
    }

    void PlaceMarker(bool glide = false)
    {
        double d = Math.Min(ActualWidth, ActualHeight);
        if (d <= 0) return;
        double r = d / 2, a = Hue * Math.PI / 180;
        Move(TranslateTransform.XProperty, r + Math.Cos(a) * Saturation * (r - 2) - _marker.Width / 2);
        Move(TranslateTransform.YProperty, r + Math.Sin(a) * Saturation * (r - 2) - _marker.Height / 2);

        void Move(DependencyProperty prop, double to)
        {
            double from = (double)_markerAt.GetValue(prop);
            _markerAt.BeginAnimation(prop, null);
            _markerAt.SetValue(prop, to);
            if (glide && Math.Abs(from - to) > 0.5)
                _markerAt.BeginAnimation(prop, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(260))
                    { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
        }
    }

    void Grow(double to)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(140)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        _markerGrow.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        _markerGrow.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        Draw();
        PlaceMarker();
    }

    void Draw()
    {
        double d = Math.Min(ActualWidth, ActualHeight);
        if (d <= 0) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        int px = (int)Math.Round(d * dpi.DpiScaleX);
        if (px == _drawnPx || px <= 0) return;
        _drawnPx = px;
        var pixels = new int[px * px];
        double c = (px - 1) / 2.0, rad = px / 2.0;
        for (int y = 0; y < px; y++)
            for (int x = 0; x < px; x++)
            {
                double dx = x - c, dy = y - c, dist = Math.Sqrt(dx * dx + dy * dy);
                double alpha = Math.Clamp(rad - dist, 0, 1); // a smooth, anti-aliased rim
                if (alpha <= 0) continue;
                double hue = (Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360;
                var k = Hsv(hue, Math.Min(1, dist / rad), 1);
                pixels[y * px + x] = (int)(alpha * 255) << 24 | (int)(k.R * alpha) << 16 | (int)(k.G * alpha) << 8 | (int)(k.B * alpha);
            }
        var bmp = new WriteableBitmap(px, px, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32, null);
        bmp.WritePixels(new Int32Rect(0, 0, px, px), pixels, px * 4, 0);
        bmp.Freeze();
        _disc.Source = bmp;
        _disc.Width = _disc.Height = d;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        CaptureMouse();
        Grow(1.25);
        Pick(e.GetPosition(this));
        e.Handled = true;
    }

    /// <summary>Scroll to fine-tune: the hue turns a few degrees a notch (Shift: the colour deepens or fades).</summary>
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (!ScrollEnabled) return;
        double step = e.Delta / 120.0;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) Saturation = Math.Clamp(Saturation + step * 0.04, 0, 1);
        else Hue = ((Hue + step * 4) % 360 + 360) % 360;
        PlaceMarker();
        Scrolling = true;
        try { Picked?.Invoke(Hue, Saturation); } finally { Scrolling = false; }
        // saved once the scrolling stops (a touchpad sends many small turns)
        _commitSoon.Stop();
        _commitSoon.Start();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (IsMouseCaptured) Pick(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!IsMouseCaptured) return;
        ReleaseMouseCapture(); // (commits, in OnLostMouseCapture)
        e.Handled = true;
    }

    /// <summary>
    /// The drag ends however it ends: let go, or the hold taken away (Alt+Tab, the Windows key...). Either way the
    /// colour picked so far is committed, so it's saved.
    /// </summary>
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        Grow(1);
        Committed?.Invoke();
    }

    void Pick(Point p)
    {
        double d = Math.Min(ActualWidth, ActualHeight), r = d / 2;
        double dx = p.X - r, dy = p.Y - r;
        Hue = (Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360;
        Saturation = Math.Min(1, Math.Sqrt(dx * dx + dy * dy) / (r - 2));
        PlaceMarker();
        Picked?.Invoke(Hue, Saturation);
    }

    public static Color Hsv(double h, double s, double v)
    {
        h = (h % 360 + 360) % 360;
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0), < 120 => (x, c, 0.0), < 180 => (0.0, c, x),
            < 240 => (0.0, x, c), < 300 => (x, 0.0, c), _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    public static (double H, double S, double V) ToHsv(Color k)
    {
        double r = k.R / 255.0, g = k.G / 255.0, b = k.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 0 : max == r ? 60 * ((g - b) / d % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        return ((h + 360) % 360, max == 0 ? 0 : d / max, max);
    }
}
