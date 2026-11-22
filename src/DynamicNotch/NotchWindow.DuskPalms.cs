using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using DynamicNotch.Controls;

namespace DynamicNotch;

/// <summary>
/// Dusk's palms, drawn the way a real coconut palm is built: a trunk that leans and curves as it rises, thick at the
/// foot and slim at the crown, ringed with the scars of old fronds; a crown of feathered fronds, each a curved midrib
/// carrying dozens of narrow leaflets that grow longest at its middle and droop under their own weight. Backlit by
/// the sunset: dark, with a thin warm rim along their edges. In the open notch's background, behind every card,
/// each frond sways on its own slow breeze and the whole tree leans a touch, never in step (they pause with the rest
/// of the background).
/// </summary>
public partial class NotchWindow
{
    const double PalmW = 120, PalmH = 132;

    static Brush PalmFill => _palmFill ??= Frozen(Color.FromArgb(0xF4, 0x09, 0x03, 0x0E));
    static Brush? _palmFill;

    /// <summary>The thin warm light along the silhouette's edges (from the sun low behind it).</summary>
    static Brush PalmRim()
    {
        var b = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) };
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0x78, 0xFF, 0xA0, 0x70), 0));
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0x30, 0xFF, 0x5A, 0x9A), 0.6));
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0x08, 0xFF, 0x4F, 0x9A), 1));
        b.Freeze();
        return b;
    }

    /// <summary>A palm and its slow motions. Its foot is at the bottom of a <see cref="PalmW"/> × <see cref="PalmH"/> box.</summary>
    static (Canvas Tree, List<(IAnimatable, DependencyProperty, AnimationTimeline)> Moves) RealPalm(int seed, double lean)
    {
        var rnd = new Random(seed);
        var tree = new Canvas { Width = PalmW, Height = PalmH, IsHitTestVisible = false };
        var moves = new List<(IAnimatable, DependencyProperty, AnimationTimeline)>();
        var rim = PalmRim();

        // the trunk: a cubic curve from the foot to the crown, tapering, leaning
        var p0 = new Point(44, PalmH); var p1 = new Point(42 + lean * 4, PalmH - 34); var p2 = new Point(50 + lean * 10, PalmH - 74); var crown = new Point(60 + lean * 12, PalmH - 98);
        Point Bez(double t) { double u = 1 - t; return new Point(u * u * u * p0.X + 3 * u * u * t * p1.X + 3 * u * t * t * p2.X + t * t * t * crown.X, u * u * u * p0.Y + 3 * u * u * t * p1.Y + 3 * u * t * t * p2.Y + t * t * t * crown.Y); }
        var trunk = new StreamGeometry();
        var left = new List<Point>(); var right = new List<Point>();
        const int steps = 28;
        for (int i = 0; i <= steps; i++)
        {
            double t = i / (double)steps;
            var a = Bez(Math.Max(0, t - 0.01)); var b = Bez(Math.Min(1, t + 0.01));
            double dx = b.X - a.X, dy = b.Y - a.Y, len = Math.Max(0.001, Math.Sqrt(dx * dx + dy * dy));
            double nx = -dy / len, ny = dx / len;
            double w = 8.2 - 4.6 * t + (t < 0.08 ? (0.08 - t) * 22 : 0); // a flared foot, a slim neck
            var c = Bez(t);
            left.Add(new Point(c.X + nx * w / 2, c.Y + ny * w / 2));
            right.Add(new Point(c.X - nx * w / 2, c.Y - ny * w / 2));
        }
        using (var ctx = trunk.Open())
        {
            ctx.BeginFigure(left[0], true, true);
            for (int i = 1; i < left.Count; i++) ctx.LineTo(left[i], true, true);
            for (int i = right.Count - 1; i >= 0; i--) ctx.LineTo(right[i], true, true);
        }
        trunk.Freeze();
        tree.Children.Add(new Path { Data = trunk, Fill = PalmFill, Stroke = rim, StrokeThickness = 0.6, StrokeLineJoin = PenLineJoin.Round });
        // the rings: old frond scars, faint
        var rings = new StreamGeometry();
        using (var ctx = rings.Open())
            for (double t = 0.06; t < 0.95; t += 0.055)
            {
                int i = (int)Math.Round(t * steps);
                var l = left[i]; var r = right[i];
                ctx.BeginFigure(new Point(l.X + (r.X - l.X) * 0.12, l.Y + (r.Y - l.Y) * 0.12), false, false);
                ctx.QuadraticBezierTo(new Point((l.X + r.X) / 2, (l.Y + r.Y) / 2 + 1.1), new Point(l.X + (r.X - l.X) * 0.88, l.Y + (r.Y - l.Y) * 0.88), true, true);
            }
        rings.Freeze();
        tree.Children.Add(new Path { Data = rings, Stroke = Frozen(Color.FromArgb(0x40, 0xFF, 0x8A, 0x7A)), StrokeThickness = 0.45 });

        // the crown: fronds all round, the side ones drooping most; drawn back to front
        double[] angles = { -175, -152, -126, -98, -70, -44, -18, 2, -136, -30 };
        foreach (var (deg, i) in angles.Select((d, i) => (d + rnd.NextDouble() * 8 - 4, i)))
        {
            double rad = deg * Math.PI / 180;
            double side = Math.Abs(Math.Cos(rad));                       // 1 at the sides, 0 straight up
            double length = 40 + rnd.NextDouble() * 12 + side * 8;
            double droop = 16 + side * 30 + rnd.NextDouble() * 6;          // how far its tip falls under its weight
            var start = crown;
            var dir = new Vector(Math.Cos(rad), Math.Sin(rad));
            var ctrl = start + dir * length * 0.6 + new Vector(0, -length * 0.28); // it arches up before it falls
            var end = start + dir * length + new Vector(0, droop);
            var frond = new StreamGeometry();
            using (var ctx = frond.Open())
            {
                Point Q(double t) { double u = 1 - t; return new Point(u * u * start.X + 2 * u * t * ctrl.X + t * t * end.X, u * u * start.Y + 2 * u * t * ctrl.Y + t * t * end.Y); }
                Vector Tan(double t) { var v = 2 * (1 - t) * (ctrl - start) + 2 * t * (end - ctrl); v.Normalize(); return v; }
                // the midrib
                const int rib = 16;
                var top = new List<Point>(); var bottom = new List<Point>();
                for (int k = 0; k <= rib; k++)
                {
                    double t = k / (double)rib;
                    var q = Q(t); var tn = Tan(t); var n = new Vector(-tn.Y, tn.X);
                    double w = 1.5 * (1 - t) + 0.25;
                    top.Add(q + n * w / 2); bottom.Add(q - n * w / 2);
                }
                ctx.BeginFigure(top[0], true, true);
                foreach (var pt in top.Skip(1)) ctx.LineTo(pt, true, true);
                for (int k = bottom.Count - 1; k >= 0; k--) ctx.LineTo(bottom[k], true, true);
                // the leaflets: both sides, longest at the middle, hanging down and forward
                int count = 22 + rnd.Next(5);
                for (int k = 4; k < count; k++) // none right at the crown: it stays open
                {
                    double t = k / (double)count;
                    var q = Q(t); var tn = Tan(t);
                    double leaf = (5 + 9 * Math.Sin(Math.PI * Math.Min(1, t * 1.05))) * (1 - t * 0.25) * (0.85 + rnd.NextDouble() * 0.3);
                    foreach (int s in new[] { -1, 1 })
                    {
                        // out from the midrib at ~55°, pulled down by gravity, a little toward the tip
                        var nrm = new Vector(-tn.Y * s, tn.X * s);
                        var d = nrm * 0.62 + tn * 0.38 + new Vector(0, 0.85 + t * 0.35); // they hang
                        d.Normalize();
                        var tip = q + d * leaf;
                        var across = new Vector(-d.Y, d.X) * 0.8;
                        var mid = q + d * leaf * 0.45 + new Vector(0, 0.6);
                        ctx.BeginFigure(q + across, true, true);
                        ctx.QuadraticBezierTo(mid + across * 0.8, tip, true, true);
                        ctx.QuadraticBezierTo(mid - across * 0.8, q - across, true, true);
                    }
                }
            }
            frond.Freeze();
            var rot = new RotateTransform(0, crown.X, crown.Y);
            // the fronds are pure silhouette (a rim on hundreds of fine leaflets would fill them with light)
            var path = new Path { Data = frond, Fill = PalmFill, RenderTransform = rot, CacheMode = new BitmapCache(2) { SnapsToDevicePixels = false }, SnapsToDevicePixels = false };
            tree.Children.Add(path);
            // each frond on its own breeze: a couple of degrees, slow, never in step with the others
            double amp = 1.4 + side * 1.6 + rnd.NextDouble() * 0.8;
            var sway = new DoubleAnimation(-amp, amp, TimeSpan.FromSeconds(3.6 + rnd.NextDouble() * 2.8))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromSeconds(rnd.NextDouble() * 2.5), EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
            moves.Add((rot, RotateTransform.AngleProperty, sway));
        }
        // the crown's heart: a few short stubs and the coconuts' shadow, so the fronds grow from somewhere
        tree.Children.Add(new Ellipse { Width = 7, Height = 5.5, Fill = PalmFill, Stroke = rim, StrokeThickness = 0.4 });
        Canvas.SetLeft(tree.Children[^1], crown.X - 3.5);
        Canvas.SetTop(tree.Children[^1], crown.Y - 1.5);
        return (tree, moves);
    }

    /// <summary>Two palms in the open notch's lower corners, behind everything; the whole tree leans a touch too.</summary>
    void AddDuskPalms(Ambient a)
    {
        double w = ExpandedSize.Width, h = ExpandedSize.Height;
        // no pixel snapping in here: a slowly turning frond snapped to whole pixels crawls in little steps
        var holder = new Canvas { Width = w, Height = h, IsHitTestVisible = false, SnapsToDevicePixels = false, UseLayoutRounding = false };
        var all = new List<(IAnimatable, DependencyProperty, AnimationTimeline)>();
        foreach (var (seed, mirror, scale, x) in new[] { (11, false, 1.0, -26.0), (29, true, 0.84, w - PalmW * 0.84 + 30) })
        {
            var (tree, moves) = RealPalm(seed, lean: mirror ? 0.8 : 1.0);
            all.AddRange(moves);
            var lean = new RotateTransform(0, 44, PalmH);
            var group = new TransformGroup();
            group.Children.Add(new ScaleTransform(mirror ? -scale : scale, scale, PalmW / 2, PalmH));
            group.Children.Add(lean);
            tree.RenderTransform = group;
            Canvas.SetLeft(tree, mirror ? x : x);
            Canvas.SetTop(tree, h - PalmH + 8);
            holder.Children.Add(tree);
            var sway = new DoubleAnimation(-0.5, 0.6, TimeSpan.FromSeconds(7 + (mirror ? 1.6 : 0)))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
            all.Add((lean, RotateTransform.AngleProperty, sway));
        }
        a.AddPiece(holder, all, fps: 60); // the sway is a slow drift: at 60 frames it glides instead of ticking
    }
}
