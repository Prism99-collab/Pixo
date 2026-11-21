using System.Windows.Media;
using System.Windows.Media.Effects;

namespace DynamicNotch;

// A touch of motion blur while the panel moves fast (opening, closing, sliding between pages): at most a pixel,
// only at the fastest part of the move, gone the moment it slows. Still, the text is drawn at its sharpest
// (fixed hinting); moving, it's drawn for motion (no shimmer). Off in Eco / on weak graphics.
public partial class NotchWindow
{
    readonly BlurEffect _motionBlur = new() { Radius = 0, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance };
    bool _motionSoft;

    void MotionSoft(bool settled)
    {
        double r = 0;
        if (!settled && !Eco && Expanded.Visibility == System.Windows.Visibility.Visible)
        {
            // px per second: the height opens about a third as far as a page slides
            double speed = Math.Max(Math.Abs(_sh.Velocity) * 1.6, Math.Abs(_pageX.Velocity));
            r = Math.Clamp((speed - 700) / 2600, 0, 1); // 0 .. 1 px
        }
        if (r < 0.2)
        {
            if (!_motionSoft) return;
            _motionSoft = false;
            Expanded.Effect = null;
            TextOptions.SetTextHintingMode(Expanded, TextHintingMode.Fixed);
            return;
        }
        if (!_motionSoft)
        {
            _motionSoft = true;
            Expanded.Effect = _motionBlur;
            TextOptions.SetTextHintingMode(Expanded, TextHintingMode.Animated);
        }
        if (Math.Abs(_motionBlur.Radius - r) > 0.05) _motionBlur.Radius = r;
    }
}
