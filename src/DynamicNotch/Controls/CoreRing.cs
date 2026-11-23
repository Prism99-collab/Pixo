using System.Windows;
using System.Windows.Media;

namespace DynamicNotch.Controls;

/// <summary>
/// The ring round the AI's face on the empty chat: seventy-two fine ticks, every sixth a little longer, and a faint inner
/// circle. While you type, the ticks light up from the top, clockwise, as far as it has read, in the colour of what it
/// understood. Drawn again only when that changes; the comet that orbits it is the page's own (a turning layer).
/// </summary>
public sealed class CoreRing : FrameworkElement
{
    const int Ticks = 72;
    Color _tone = Colors.White;
    double _read;
    Pen? _lit, _litMajor, _rest, _restMajor, _inner;

    public CoreRing() => IsHitTestVisible = false;

    /// <summary>The colour of the lit ticks (and, faintly, of the resting ones).</summary>
    public Color Tone { get => _tone; set { if (_tone == value) return; _tone = value; _lit = null; InvalidateVisual(); } }

    /// <summary>The share of the ring lit, from the top clockwise (0: none).</summary>
    public double Read { get => _read; set { value = Math.Clamp(value, 0, 1); if (_read == value) return; _read = value; InvalidateVisual(); } }

    static Pen Make(Color c, double w)
    {
        var p = new Pen(new SolidColorBrush(c), w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        p.Freeze();
        return p;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 20) return;
        if (_lit == null)
        {
            var c = _tone;
            _lit = Make(Color.FromArgb(0xC0, c.R, c.G, c.B), 1.1);
            _litMajor = Make(c, 1.5);
            _rest = Make(Color.FromArgb(0x2C, c.R, c.G, c.B), 1);
            _restMajor = Make(Color.FromArgb(0x5A, c.R, c.G, c.B), 1.3);
            _inner = Make(Color.FromArgb(0x16, c.R, c.G, c.B), 1);
        }
        var o = new Point(ActualWidth / 2, ActualHeight / 2);
        double outer = size / 2 - 1;
        dc.DrawEllipse(null, _inner, o, outer - 11, outer - 11);
        double lit = _read * Ticks;
        for (int i = 0; i < Ticks; i++)
        {
            bool major = i % 6 == 0;
            double inner = outer - (major ? 5.5 : 3);
            double a = i / (double)Ticks * 2 * Math.PI;
            double sin = Math.Sin(a), cos = Math.Cos(a);
            Pen pen = i < lit - 0.001 ? (major ? _litMajor! : _lit) : (major ? _restMajor! : _rest!);
            dc.DrawLine(pen, new Point(o.X + sin * inner, o.Y - cos * inner), new Point(o.X + sin * outer, o.Y - cos * outer));
        }
    }
}
