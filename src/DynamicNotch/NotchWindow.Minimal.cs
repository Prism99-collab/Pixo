using System.Windows;
using System.Windows.Media;

namespace DynamicNotch;

/// <summary>
/// The quiet themes. Mono (the default): white on black, nothing else; the notch as plain as a phone's, and the lightest
/// to draw. Minimal: the same notch, pages and layout, with nothing on it that isn't content. One graphite body with the
/// smallest tone shift from the edge down, a hairline rim, no glow behind the open panel, no living background, no
/// sheen, glint or wobble, and motion that arrives without a bounce. Colour is left to what you look at: the cover,
/// the call, the timer, the one accent. Your own colours from the Colors page still apply.
/// It is also the lightest theme to draw: with nothing moving at rest, the processor stays nearly idle.
/// </summary>
public partial class NotchWindow
{
    // the graphite ramp: four steps of one neutral, a hair warm (so it never reads blue on a cold screen)
    static readonly Color MinInk = Color.FromRgb(0xEC, 0xEB, 0xE8), MinSoft = Color.FromArgb(0x99, 0xEC, 0xEB, 0xE8),
        MinLine = Color.FromArgb(0x17, 0xEC, 0xEB, 0xE8), MinStep = Color.FromArgb(0x0F, 0xEC, 0xEB, 0xE8);

    /// <summary>A quiet theme: no glow, no background, no bounce (Mono, Minimal).</summary>
    bool MinimalOn => _look?.Quiet == true;

    /// <summary>A status colour in a quiet theme: toned into its palette (else as it is).</summary>
    Color MinimalMapIf(Color c) => _look is { Quiet: true } l ? l.Map(c) : c;

    // Mono: pure black, pure white, and white at three strengths between
    static readonly Color MonoInk = Colors.White, MonoSoft = Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF),
        MonoLine = Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF), MonoStep = Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF);

    /// <summary>Greys become true neutrals; colours keep two thirds of their saturation, so only warnings stand out.</summary>
    static Color MonoMap(Color c)
    {
        int max = Math.Max(c.R, Math.Max(c.G, c.B)), min = Math.Min(c.R, Math.Min(c.G, c.B));
        byte v = (byte)((c.R + c.G + c.B) / 3);
        if (max - min <= 12) return Color.FromArgb(c.A, v, v, v);
        byte Ease(byte ch) => (byte)Math.Round(ch + (v - ch) * 0.33);
        return Color.FromArgb(c.A, Ease(c.R), Ease(c.G), Ease(c.B));
    }

    static Look MonoLook() => new()
    {
        Key = "mono", Name = "Mono",
        Top = Colors.Black, Mid = Colors.Black, Bottom = Colors.Black,
        RimColor = Colors.White, RimIdle = 0.09,
        Clock = MonoInk, Fps = MonoInk,
        TileC = MonoStep, DimC = MonoSoft,
        LookTile = F(MonoStep), LookEdge = F(MonoLine), LookSheen = F(Colors.Transparent), LookLabel = F(MonoSoft),
        LookSwitchOff = F(Color.FromArgb(0x2C, 0xFF, 0xFF, 0xFF)), LookSwitchOn = F(MonoInk),
        Map = MonoMap, Quiet = true,
        Signature = Colors.White,
    };

    LaunchSkin MonoSkin()
    {
        var a = LaunchAccent;
        return new LaunchSkin("mono",
            Frozen(Color.FromArgb(0xFC, 0x00, 0x00, 0x00)), Frozen(MonoLine), 1, a,
            Frozen(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)), null, new Thickness(0), 8,
            MonoInk, MonoSoft, MonoLine, Color.FromArgb(0xB4, 0xFF, 0xFF, 0xFF),
            Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF), null, Arrival.Ease, 8);
    }

    /// <summary>Greys become steps of the graphite ramp; colours lose a fifth of their saturation, so they sit inside it.</summary>
    static Color MinimalMap(Color c)
    {
        int max = Math.Max(c.R, Math.Max(c.G, c.B)), min = Math.Min(c.R, Math.Min(c.G, c.B));
        if (max - min <= 10)
        {
            byte v = (byte)((c.R + c.G + c.B) / 3);
            return Color.FromArgb(c.A, v, v, (byte)Math.Max(0, v - 1));
        }
        double grey = (c.R + c.G + c.B) / 3.0;
        byte Ease(byte ch) => (byte)Math.Round(ch + (grey - ch) * 0.2);
        return Color.FromArgb(c.A, Ease(c.R), Ease(c.G), Ease(c.B));
    }

    static Look MinimalLook() => new()
    {
        Key = "minimal", Name = "Minimal",
        Top = Color.FromArgb(0xFE, 0x0B, 0x0B, 0x0C), Mid = Color.FromArgb(0xFC, 0x0F, 0x0F, 0x10), Bottom = Color.FromArgb(0xFA, 0x12, 0x12, 0x13),
        RimColor = MinInk, RimIdle = 0.07,
        Clock = MinInk, Fps = MinInk,
        TileC = MinStep, DimC = MinSoft,
        LookTile = F(MinStep),
        LookEdge = F(MinLine),
        LookSheen = F(Colors.Transparent),
        LookLabel = F(MinSoft),
        LookSwitchOff = F(Color.FromArgb(0x24, 0xEC, 0xEB, 0xE8)), LookSwitchOn = F(MinInk), // (on dark: the "on" is the off-white, not a colour)
        Map = MinimalMap, Quiet = true,
        Signature = Color.FromRgb(0xD6, 0xD5, 0xD2),
    };

    /// <summary>The search and the chat in Minimal: a solid graphite sheet, a hairline, the selection a step lighter.</summary>
    LaunchSkin MinimalSkin()
    {
        var a = LaunchAccent;
        return new LaunchSkin("minimal",
            Frozen(Color.FromArgb(0xFC, 0x0F, 0x0F, 0x10)), Frozen(MinLine), 1, a,
            Frozen(Color.FromArgb(0x14, 0xEC, 0xEB, 0xE8)), null, new Thickness(0), 8,
            MinInk, MinSoft, MinLine, Color.FromArgb(0xB0, 0xEC, 0xEB, 0xE8),
            Color.FromArgb(0xC8, 0xEC, 0xEB, 0xE8), null, Arrival.Ease, 8);
    }

    /// <summary>Minimal's motion: the shape goes straight to its size, no overshoot (still quick).</summary>
    void QuietSprings()
    {
        if (!MinimalOn) return;
        _sw.Configure(0.27, 1.0);
        _sh.Configure(0.25, 1.0);
        _sr.Configure(0.27, 1.0);
    }
}
