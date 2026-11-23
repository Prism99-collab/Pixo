using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DynamicNotch;

/// <summary>
/// A thin line along a pop-up's bottom edge shrinks toward its middle while the pop-up waits to close, so you can see
/// how long it stays. Under the pointer it holds, full, and brightens (in Neon it also glows; the pop-up holds too); leaving, it runs out in the
/// same 1.2 s the pop-up then takes. In the pop-up's own colour. Not on level pop-ups (volume, brightness), which come
/// and go as you press keys.
/// </summary>
public partial class NotchWindow
{
    Border? _osdLine;
    bool _osdLineOn; // the pop-up on screen has the line
    readonly ScaleTransform _osdLineScale = new(1, 1);

    Border OsdLine()
    {
        if (_osdLine != null) return _osdLine;
        _osdLine = new Border
        {
            Height = 2, CornerRadius = new CornerRadius(1), IsHitTestVisible = false, Opacity = 0,
            VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(30, 0, 30, 4), RenderTransform = _osdLineScale, RenderTransformOrigin = new Point(0.5, 0.5),
        };
        Osd.Children.Add(_osdLine);
        return _osdLine;
    }

    /// <summary>A pop-up was shown (or replaced): the line starts full and runs out over its time.</summary>
    void OsdCountdownStart(OsdRequest r)
    {
        var line = OsdLine();
        _osdLineOn = !(r.Level.HasValue || r.Seconds < 1.5);
        if (!_osdLineOn)
        {
            line.BeginAnimation(OpacityProperty, null);
            line.Opacity = 0;
            return;
        }
        var c = _osdAccent ?? Colors.White;
        line.Background = Frozen(Color.FromArgb(0xFF, c.R, c.G, c.B));
        line.Effect = null;
        line.BeginAnimation(OpacityProperty, new DoubleAnimation(0.45, TimeSpan.FromMilliseconds(220)));
        OsdLineRun(r.Seconds, from: 1);
    }

    /// <summary>The pointer is on the pop-up: the line holds full and brightens (and glows in Neon). Leaving: it runs out in <paramref name="leftSeconds"/>.</summary>
    void OsdCountdownHold(bool hold, double leftSeconds = 1.2)
    {
        // a pop-up without the line (volume, brightness, a short one) doesn't grow one under the pointer either.
        // Asked of the pop-up, not of the line's opacity: just shown, the line is still fading in from 0.
        if (_osdLine is not { } line || !_osdLineOn) return;
        if (hold)
        {
            var c = _osdAccent ?? Colors.White;
            _osdLineScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(Eco ? 1 : 260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            line.BeginAnimation(OpacityProperty, new DoubleAnimation(0.95, TimeSpan.FromMilliseconds(180)));
            if (!Eco && _cyber) line.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = c, BlurRadius = 6, ShadowDepth = 0, Opacity = 0.9 };
            return;
        }
        line.Effect = null;
        line.BeginAnimation(OpacityProperty, new DoubleAnimation(0.45, TimeSpan.FromMilliseconds(180)));
        OsdLineRun(leftSeconds, from: _osdLineScale.ScaleX);
    }

    void OsdLineRun(double seconds, double from)
    {
        var run = new DoubleAnimation(from, 0, TimeSpan.FromSeconds(Math.Max(0.2, seconds)));
        Timeline.SetDesiredFrameRate(run, Eco ? 15 : 30); // a 2 px line: 30 steps a second look continuous
        _osdLineScale.BeginAnimation(ScaleTransform.ScaleXProperty, run);
    }
}
