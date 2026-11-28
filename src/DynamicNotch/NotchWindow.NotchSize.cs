using System.ComponentModel;
using System.Windows.Controls;

namespace DynamicNotch;

/// <summary>
/// Preferences → Appearance → Closed: how the notch looks when it's closed, and how big.
///  • its style: a Notch hanging from the top edge, a Capsule floating just below it, or a Line (a thin bar, nothing in it);
///  • its size: the notch's or capsule's width and height, the capsule's distance from the edge, the line's length and
///    thickness. Each slider moves the real notch while you drag, through its own springs (it glides, never jumps);
///  • how solid its body is (from solid down to 40 %, the text staying full).
/// The four size steps of before still map onto the sliders for anyone who picked one.
/// </summary>
public partial class NotchWindow
{
    internal static readonly (string Key, string Name, double W, double H)[] NotchSizes =
    {
        ("small", "Small", 132, 24), ("standard", "Standard", 150, 26), ("large", "Large", 176, 30), ("xl", "Extra large", 204, 32),
    };

    internal static readonly (string Key, string Name)[] BodyOpacities = { ("100", "Solid"), ("85", "85 %"), ("70", "70 %"), ("55", "55 %"), ("40", "40 %") };

    /// <summary>The ranges of the sliders (Preferences reads them too).</summary>
    internal static readonly (double Min, double Max) WidthRange = (110, 260), HeightRange = (22, 36), GapRange = (0, 20), LineLengthRange = (60, 320), LineThickRange = (3, 8);

    /// <summary>notch, capsule or line (older settings only knew the floating island on or off).</summary>
    internal string ClosedStyleNow => _settings.ClosedStyle is "notch" or "capsule" or "line" ? _settings.ClosedStyle : _settings.IslandLook ? "capsule" : "notch";

    void ApplyNotchSize()
    {
        var preset = NotchSizes.FirstOrDefault(x => x.Key == _settings.NotchSize);
        if (preset.Key == null) preset = NotchSizes[1];
        double w = _settings.NotchWidth > 0 ? _settings.NotchWidth : preset.W, h = _settings.NotchHeight > 0 ? _settings.NotchHeight : preset.H;
        IdleSize = new System.Windows.Size(Math.Clamp(w, WidthRange.Min, WidthRange.Max), Math.Clamp(h, HeightRange.Min, HeightRange.Max));
        SlimSize = new System.Windows.Size(Math.Clamp(_settings.LineLength, LineLengthRange.Min, LineLengthRange.Max), Math.Clamp(_settings.LineThickness, LineThickRange.Min, LineThickRange.Max));
        IslandGap = Math.Clamp(ClosedStyleNow == "line" ? _settings.LineGap : _settings.EdgeGap, GapRange.Min, GapRange.Max);
        // the music and a live activity keep their proportions to yours: as much wider and taller as they always were
        double dw = IdleSize.Width - 150, dh = IdleSize.Height - 26;
        MediaSize = new System.Windows.Size(Math.Clamp(252 + dw, 190, 330), Math.Clamp(30 + dh, 26, 40));
        ActivitySize = new System.Windows.Size(Math.Clamp(230 + dw, 180, 310), Math.Clamp(30 + dh, 26, 40));
    }

    internal void SetNotchSize(string key)
    {
        _settings.NotchSize = key;
        _settings.NotchWidth = _settings.NotchHeight = 0; // (the step's own size)
        _settings.Save();
        ApplyNotchSize();
        ApplyShape();
    }

    /// <summary>A slider moved: the notch glides to it (saved when the slider is let go).</summary>
    internal void SetShapeValue(string key, double v, bool save)
    {
        switch (key)
        {
            case "width": _settings.NotchWidth = Math.Round(v); break;
            case "height": _settings.NotchHeight = Math.Round(v); break;
            case "gap": if (ClosedStyleNow == "line") _settings.LineGap = Math.Round(v); else _settings.EdgeGap = Math.Round(v); break;
            case "x": _settings.PosX = Math.Round(v, 3); break;
            case "length": _settings.LineLength = Math.Round(v); break;
            case "thickness": _settings.LineThickness = Math.Round(v * 2) / 2; break;
        }
        ApplyNotchSize();
        if (key == "x") PositionWindow(); // (moves the window: the notch, its pop-ups and every hover test with it)
        ApplyShape();
        if (save) _settings.Save();
    }

    /// <summary>Back to the sizes it came with.</summary>
    internal void ResetShape()
    {
        _settings.NotchSize = "standard";
        _settings.NotchWidth = _settings.NotchHeight = 0;
        _settings.EdgeGap = 7;
        _settings.LineLength = 118;
        _settings.LineThickness = 6;
        _settings.LineGap = 0;
        _settings.PosX = 0;
        _settings.Save();
        PositionWindow();
        ApplyNotchSize();
        ApplyShape();
    }

    /// <summary>Notch, capsule or line: the closed notch changes to it (gliding, like any change of shape).</summary>
    internal void SetClosedStyle(string style)
    {
        _settings.ClosedStyle = style;
        _settings.IslandLook = style == "capsule";
        _settings.Save();
        ApplyNotchSize(); // (the line and the capsule each keep their own distance from the edge)
        if (IsBase(_mode) && _mode != Mode.Game) SetMode(BaseMode);
        ApplyShape();
    }

    internal void SetBodyOpacity(string key)
    {
        _settings.BodyOpacity = int.TryParse(key, out int v) ? Math.Clamp(v, 40, 100) : 100;
        _settings.Save();
        BodyOpacity();
    }

    bool _bodyWired;

    /// <summary>Whatever paints the body (a theme, your colour, Neon), it keeps the opacity you chose.</summary>
    void WireBodyOpacity()
    {
        if (_bodyWired) return;
        _bodyWired = true;
        DependencyPropertyDescriptor.FromProperty(Border.BackgroundProperty, typeof(Border)).AddValueChanged(Pill, (_, _) => BodyOpacity());
        BodyOpacity();
    }

    void BodyOpacity()
    {
        double o = Math.Clamp(_settings.BodyOpacity, 40, 100) / 100.0;
        if (Pill.Background is not { } b || Math.Abs(b.Opacity - o) < 0.001) return;
        var c = b.CloneCurrentValue(); // (left unfrozen: code that tints the body later still can)
        c.Opacity = o;
        Pill.Background = c;
    }
}
