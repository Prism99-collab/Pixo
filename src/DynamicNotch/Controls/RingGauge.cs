using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DynamicNotch.Controls;

/// <summary>Vector ring gauge (activity-ring style) whose value glides smoothly between samples.</summary>
public sealed class RingGauge : FrameworkElement
{
    static readonly Pen TrackPen = MakePen(new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(RingGauge), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RingBrushProperty = DependencyProperty.Register(
        nameof(RingBrush), typeof(Brush), typeof(RingGauge), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// The refined look (the Tools page's timer): a slimmer ring on a fainter track, a soft halo of its colour under the
    /// arc, and a small bead of light where the arc ends. Off: the plain ring (the System page's gauges).
    /// </summary>
    public static readonly DependencyProperty SoftProperty = DependencyProperty.Register(
        nameof(Soft), typeof(bool), typeof(RingGauge), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public bool Soft { get => (bool)GetValue(SoftProperty); set => SetValue(SoftProperty, value); }

    static readonly Pen SoftTrack = MakePen(new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)), 4);
    static readonly Brush Bead = MakeBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));
    static Brush MakeBrush(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public Brush RingBrush { get => (Brush)GetValue(RingBrushProperty); set => SetValue(RingBrushProperty, value); }

    public const double Thickness = 6;

    /// <summary>The neon skin: a segmented HUD dial instead of a smooth ring.</summary>
    public static bool Segmented;

    static Pen Dial(Brush b, double r)
    {
        double seg = 2 * Math.PI * r / 40 / Thickness; // 40 segments around, in pen-width units
        return new Pen(b, Thickness) { DashStyle = new DashStyle(new[] { seg * 0.62, seg * 0.38 }, 0), DashCap = PenLineCap.Flat };
    }

    public void AnimateTo(double v)
    {
        v = Math.Clamp(v, 0, 1);
        double delta = Math.Abs(v - Value);
        if (delta < 0.004) return; // nothing visible changed: don't redraw at all
        if (delta < 0.03)
        {
            // small drift: jump straight there (one redraw) instead of a 36-frame animation
            BeginAnimation(ValueProperty, null);
            Value = v;
            return;
        }
        var a = new DoubleAnimation(v, TimeSpan.FromMilliseconds(350)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        // a ring filling slowly looks just as smooth at 30 fps, at half the redraw cost
        Timeline.SetDesiredFrameRate(a, 30);
        BeginAnimation(ValueProperty, a);
    }

    static Pen MakePen(Brush b, double thickness = Thickness)
    {
        var p = new Pen(b, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        p.Freeze();
        return p;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (Soft && !Segmented) { RenderSoft(dc); return; }
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= Thickness) return;
        double r = (size - Thickness) / 2;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        dc.DrawEllipse(null, Segmented ? Dial(TrackPen.Brush, r) : TrackPen, c, r, r);

        double v = Math.Clamp(Value, 0, 1);
        if (v <= 0.002) return;
        var pen = Segmented ? Dial(RingBrush, r) : new Pen(RingBrush, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (v >= 0.999) { dc.DrawEllipse(null, pen, c, r, r); return; }

        double a = v * 2 * Math.PI;
        var start = new Point(c.X, c.Y - r);
        var end = new Point(c.X + r * Math.Sin(a), c.Y - r * Math.Cos(a));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(r, r), 0, v > 0.5, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }

    /// <summary>The refined ring: faint track, halo, slim arc, bead at its end.</summary>
    void RenderSoft(DrawingContext dc)
    {
        const double w = 4, halo = 9;
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= halo) return;
        double r = (size - halo) / 2;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        dc.DrawEllipse(null, SoftTrack, c, r, r);
        double v = Math.Clamp(Value, 0, 1);
        if (v <= 0.002) return;
        var color = RingBrush is SolidColorBrush sb ? sb.Color : Colors.White;
        var haloPen = new Pen(new SolidColorBrush(Color.FromArgb(0x1C, color.R, color.G, color.B)), halo) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var pen = new Pen(RingBrush, w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        // full: one clean line (a halo all round only blurs it); the halo is for the arc that's running down
        if (v >= 0.999) { dc.DrawEllipse(null, pen, c, r, r); return; }
        double a = v * 2 * Math.PI;
        var start = new Point(c.X, c.Y - r);
        var end = new Point(c.X + r * Math.Sin(a), c.Y - r * Math.Cos(a));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(r, r), 0, v > 0.5, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        dc.DrawGeometry(null, haloPen, g);
        dc.DrawGeometry(null, pen, g);
        dc.DrawEllipse(Bead, null, end, w * 0.34, w * 0.34); // the bead of light where the time has got to
    }
}
