using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DynamicNotch.Controls;

namespace DynamicNotch;

/// <summary>
/// How the open notch's content arrives and leaves, so it always moves with the shape and never leaves it empty:
///  • opening, it pours out of the notch: a soft edge wipes down from the top while the content unfolds from the
///    top edge (a little short and narrow, springing to full size); the rows then rise in a cascade (Flourish);
///  • closing, it's drawn back up into the notch: the edge wipes up while the content folds toward the top, all
///    quicker than the shape, so the black never shows empty. Neon closes like a tube switching off instead
///    (NotchWindow.CyberFx): the whole shape drops to a band of light, which then pulls in sideways.
/// Everything runs on the compositor; the mask is removed the moment it's done.
/// </summary>
public partial class NotchWindow
{
    int _unfoldId;

    void PourExpanded(int delayMs)
    {
        int id = ++_unfoldId;
        var panel = Expanded;
        panel.Visibility = Visibility.Visible;
        panel.RenderTransformOrigin = new Point(0.5, 0);
        var begin = TimeSpan.FromMilliseconds(delayMs);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        panel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(LightOpen ? 110 : 150)) { BeginTime = begin, EasingFunction = ease });
        if (panel.RenderTransform is ScaleTransform st && !st.IsFrozen)
        {
            // a PC that draws slowly: the content just fades in with the shape (scaling it costs every frame there)
            if (LightOpen) { st.BeginAnimation(ScaleTransform.ScaleXProperty, null); st.BeginAnimation(ScaleTransform.ScaleYProperty, null); st.ScaleX = st.ScaleY = 1; }
            else
            {
                st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.95, 1, TimeSpan.FromMilliseconds(330)) { BeginTime = begin, EasingFunction = new SpringEase { Damping = 0.78 } });
                st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.86, 1, TimeSpan.FromMilliseconds(350)) { BeginTime = begin, EasingFunction = new SpringEase { Damping = 0.74 } });
            }
        }

        panel.OpacityMask = null; // (no soft-edge mask: in a see-through window it doubles the drawing every frame)
    }

    void RetractExpanded()
    {
        int id = ++_unfoldId;
        var panel = Expanded;
        panel.RenderTransformOrigin = new Point(0.5, 0);
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var d = TimeSpan.FromMilliseconds(130);

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(LightOpen ? 90 : 120)) { EasingFunction = ease };
        fade.Completed += (_, _) =>
        {
            if (id != _unfoldId || _activePanel == panel) return;
            panel.Visibility = Visibility.Collapsed;
            panel.OpacityMask = null;
        };
        panel.BeginAnimation(OpacityProperty, fade);
        if (panel.RenderTransform is ScaleTransform st && !st.IsFrozen && !LightOpen)
        {
            st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.93, d) { EasingFunction = ease });
            st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.76, d) { EasingFunction = ease });
        }

        panel.OpacityMask = null;
    }

    // ───────────── light opening on PCs that draw slowly ─────────────

    /// <summary>
    /// This PC draws the opening slowly (an old laptop, weak graphics): the content just fades in and out with the
    /// shape, without the scaling and the rows' cascade, so every frame is light and the shape keeps its pace.
    /// Learned from the opening's own frames (two slow openings in a row), and kept for the session.
    /// </summary>
    bool LightOpen => _lightOpen;
    bool _lightOpen;
    int _slowOpens;
    double _openFrames, _openTime;

    /// <summary>Times the frames of each opening's first 0.4 s; judged once that time has passed.</summary>
    void TimeOpening(double dt)
    {
        if (_lightOpen || _mode != Mode.Expanded) return;
        double since = (DateTime.UtcNow - _expandedAt).TotalSeconds;
        if (since < 0.4) { _openFrames++; _openTime += dt; return; }
        if (_openFrames < 4) { _openFrames = _openTime = 0; return; }
        double avg = _openTime / _openFrames * 1000;
        _openFrames = _openTime = 0;
        _slowOpens = avg > 26 ? _slowOpens + 1 : 0; // under ~38 frames a second while opening
        if (_slowOpens >= 2) { _lightOpen = true; Services.AppLog.Write($"motion: light opening (frames {avg:0} ms)"); }
    }

    /// <summary>The open panel built once while nothing shows (its first opening used to build it, ~170 ms).</summary>
    void WarmExpanded()
    {
        if (_mode == Mode.Expanded || Expanded.Visibility != Visibility.Collapsed) return;
        try
        {
            Expanded.Visibility = Visibility.Hidden; // laid out, never drawn
            Expanded.UpdateLayout();
        }
        catch { }
        finally { if (_mode != Mode.Expanded && _activePanel != Expanded) Expanded.Visibility = Visibility.Collapsed; }
    }

    /// <summary>A top-down mask: shown above the front, fading out over a short band, hidden below it.</summary>
    static (LinearGradientBrush Mask, GradientStop Front, GradientStop Back) Wipe(double at)
    {
        var front = new GradientStop(Colors.Black, at);
        var back = new GradientStop(Colors.Transparent, at + 0.25);
        var mask = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) };
        mask.GradientStops.Add(new GradientStop(Colors.Black, 0));
        mask.GradientStops.Add(front);
        mask.GradientStops.Add(back);
        return (mask, front, back);
    }
}
