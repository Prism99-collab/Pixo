using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DynamicNotch.Controls;

namespace DynamicNotch;

/// <summary>
/// The Tools page's timer card, its look in motion: Focus / Break and the minutes are each one track whose highlight
/// slides to the choice (a calm spring, only when the choice changes, never on the clock's tick); the highlight of the
/// minutes fades away once the timer has started (no length is "picked" any more) and comes back on reset; the soft
/// glow behind the ring takes the timer's colour and brightens while it runs.
/// </summary>
public partial class NotchWindow
{
    double _modeThumbTo = double.NaN, _chipThumbTo = double.NaN, _chipThumbShown = -1, _glowTo = -1;
    Color? _glowColor;
    bool _toolsLookWired;

    void WireToolsLook()
    {
        if (_toolsLookWired) return;
        _toolsLookWired = true;
        // the minutes' track: its highlight is a quarter of it, wherever it ends up being laid out
        TimerChips.SizeChanged += (_, _) => { _chipThumbTo = double.NaN; PaintTimerTracks(animate: false); };
    }

    void PaintTimerTracks(bool animate = true)
    {
        WireToolsLook();
        var accent = TimerBrush is SolidColorBrush sb ? sb.Color : Color.FromRgb(0xFF, 0x9F, 0x0A);
        bool started = (_timerRunning ? _timerEnd - DateTime.UtcNow : _timerLeft) < _timerTotal;

        // focus or break
        // the highlight in the timer's colour (orange for focus, green for a break), the picked word dark on it
        FocusTab.FontWeight = _timerBreak ? FontWeights.Normal : FontWeights.SemiBold;
        BreakTab.FontWeight = _timerBreak ? FontWeights.SemiBold : FontWeights.Normal;
        FocusTab.Foreground = _timerBreak ? Brushes.White : Brushes.Black;
        BreakTab.Foreground = _timerBreak ? Brushes.Black : Brushes.White;
        if (ModeThumb.Background is not SolidColorBrush { IsFrozen: false })
            ModeThumb.Background = new SolidColorBrush(accent); // its own brush, so the colour can fade below

        Slide(ModeThumbShift, _timerBreak ? BreakTab.Width : 0, ref _modeThumbTo, animate);

        // the minutes
        var chips = TimerChips.Children.OfType<Button>().ToList();
        int picked = started ? -1 : chips.FindIndex(c => Math.Abs(Convert.ToDouble(c.Tag) - _timerTotal.TotalMinutes) < 0.01);
        for (int i = 0; i < chips.Count; i++)
        {
            bool on = i == picked;
            chips[i].FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            chips[i].Foreground = on ? Brushes.Black : Brushes.White;
        }
        double w = TimerChips.ActualWidth / Math.Max(1, chips.Count);
        if (w > 0) ChipThumb.Width = Math.Max(0, w);
        if (ChipThumb.Background is not SolidColorBrush cb || cb.Color != accent) ChipThumb.Background = Frozen(accent);
        if (ModeThumb.Background is SolidColorBrush mt && !mt.IsFrozen && mt.Color != accent)
            mt.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(accent, TimeSpan.FromMilliseconds(animate && !Eco ? 260 : 1)));
        if (picked >= 0 && w > 0) Slide(ChipThumbShift, picked * w, ref _chipThumbTo, animate && _chipThumbShown > 0.5);
        double show = picked >= 0 ? 1 : 0;
        if (show != _chipThumbShown)
        {
            _chipThumbShown = show;
            ChipThumb.BeginAnimation(OpacityProperty, new DoubleAnimation(show, TimeSpan.FromMilliseconds(animate && !Eco ? 220 : 1)));
        }

        // the glow behind the ring: the timer's colour, brighter while it runs
        if (_glowColor != accent)
        {
            _glowColor = accent;
            var fill = new RadialGradientBrush();
            fill.GradientStops.Add(new GradientStop(Color.FromArgb(0x55, accent.R, accent.G, accent.B), 0));
            fill.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, accent.R, accent.G, accent.B), 1));
            fill.Freeze();
            TimerGlow.Fill = fill;
        }
        double glow = _timerRunning ? 0.8 : 0.3;
        if (glow != _glowTo)
        {
            _glowTo = glow;
            TimerGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(glow, TimeSpan.FromMilliseconds(animate && !Eco ? 600 : 1)) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
        }
    }

    /// <summary>A highlight glides to its place (a calm spring), only when its place changes.</summary>
    void Slide(TranslateTransform t, double x, ref double lastTo, bool animate)
    {
        if (Math.Abs(lastTo - x) < 0.5) return;
        lastTo = x;
        if (!animate || Eco) { t.BeginAnimation(TranslateTransform.XProperty, null); t.X = x; return; }
        t.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, TimeSpan.FromMilliseconds(360)) { EasingFunction = new SpringEase { Damping = 0.82 } });
    }
}
