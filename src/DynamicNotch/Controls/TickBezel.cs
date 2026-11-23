using System.Windows;
using System.Windows.Media;

namespace DynamicNotch.Controls;

/// <summary>
/// A watch's bezel round the timer: sixty fine ticks, every fifth a little longer. Lit from the top, clockwise, up to
/// <see cref="Left"/>: for the countdown the time still ahead (the ones gone go faint, a dial being used up), for the
/// stopwatch the seconds of the minute (a hand going round). Untouched, it rests in a quiet white. Drawn once per
/// change (the clock's tick), nothing runs in between.
/// </summary>
public sealed class TickBezel : FrameworkElement
{
    public static readonly DependencyProperty LeftProperty = DependencyProperty.Register(nameof(Left), typeof(double), typeof(TickBezel),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TickBrushProperty = DependencyProperty.Register(nameof(TickBrush), typeof(Brush), typeof(TickBezel),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LitProperty = DependencyProperty.Register(nameof(Lit), typeof(bool), typeof(TickBezel),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The share still to go (1: all of it).</summary>
    public double Left { get => (double)GetValue(LeftProperty); set => SetValue(LeftProperty, value); }
    /// <summary>The timer's colour.</summary>
    public Brush TickBrush { get => (Brush)GetValue(TickBrushProperty); set => SetValue(TickBrushProperty, value); }
    /// <summary>Started: what's left in colour, what's gone faint.</summary>
    public bool Lit { get => (bool)GetValue(LitProperty); set => SetValue(LitProperty, value); }

    static readonly Pen Rest = Make(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF), 1), RestMajor = Make(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF), 1.4);
    static readonly Pen Gone = Make(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF), 1), GoneMajor = Make(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF), 1.4);

    static Pen Make(Color c, double w)
    {
        var p = new Pen(new SolidColorBrush(c), w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        p.Freeze();
        return p;
    }

    Color? _litFor;
    Pen? _lit, _litMajor;

    public TickBezel() => IsHitTestVisible = false;

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 10) return;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        double outer = size / 2 - 1;
        var color = TickBrush is SolidColorBrush sb ? sb.Color : Colors.White;
        if (_litFor != color) { _litFor = color; _lit = Make(Color.FromArgb(0xB0, color.R, color.G, color.B), 1); _litMajor = Make(color, 1.4); }
        Pen lit = _lit!, litMajor = _litMajor!;
        // the ticks run clockwise from the top, like the ring: the time left is the arc from the top to where it has got to
        double ahead = Lit ? Math.Clamp(Left, 0, 1) * 60 : 60;
        for (int i = 0; i < 60; i++)
        {
            bool major = i % 5 == 0;
            double inner = outer - (major ? 5 : 3);
            double a = i / 60.0 * 2 * Math.PI;
            double sin = Math.Sin(a), cos = Math.Cos(a);
            Pen pen = !Lit ? (major ? RestMajor : Rest) : i < ahead - 0.001 ? (major ? litMajor : lit) : (major ? GoneMajor : Gone);
            dc.DrawLine(pen, new Point(c.X + sin * inner, c.Y - cos * inner), new Point(c.X + sin * outer, c.Y - cos * outer));
        }
    }
}
