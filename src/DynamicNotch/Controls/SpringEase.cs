using System.Windows;
using System.Windows.Media.Animation;

namespace DynamicNotch.Controls;

/// <summary>
/// Damped-spring easing (a lively, springy feel). Damping below 1 gives a small, natural overshoot.
/// </summary>
public sealed class SpringEase : EasingFunctionBase
{
    public static readonly DependencyProperty DampingProperty =
        DependencyProperty.Register(nameof(Damping), typeof(double), typeof(SpringEase), new PropertyMetadata(0.75));

    public double Damping
    {
        get => (double)GetValue(DampingProperty);
        set => SetValue(DampingProperty, value);
    }

    public SpringEase() => EasingMode = EasingMode.EaseIn; // use EaseInCore as-is

    protected override double EaseInCore(double t)
    {
        if (t >= 1) return 1;
        // calm, premium settling: every spring keeps its own character, lifted into the 0.78..0.95 range, so it lands with
        // a hint of life instead of a wobble (0.45 → 0.79, 0.6 → 0.85, 0.8 → 0.92). The timing is the same.
        double z = Math.Clamp(0.62 + 0.38 * Math.Clamp(Damping, 0.05, 0.99), 0.05, 0.99);
        double w0 = 6.9 / z;                      // envelope decays to ~0.1% by t = 1
        double wd = w0 * Math.Sqrt(1 - z * z);
        return 1 - Math.Exp(-z * w0 * t) * (Math.Cos(wd * t) + z * w0 / wd * Math.Sin(wd * t));
    }

    protected override Freezable CreateInstanceCore() => new SpringEase();
}
