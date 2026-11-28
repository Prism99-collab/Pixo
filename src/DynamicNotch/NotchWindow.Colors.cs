using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DynamicNotch.Services;
using static DynamicNotch.Interop.Native;

namespace DynamicNotch;

/// <summary>
/// Colour that belongs to the moment:
///  • opening the notch, it takes the colour of the app you're on (a tiny sample of its window, the notch's own
///    pixels left out): a deep glow of it fades in behind the panel, its rim lights up in it, the sliders wear it;
///  • something live overrides it with a colour that means something: call green (red when muted), download blue,
///    timer orange, Focus purple, or the song's cover when the cover has colour;
///  • the quick toggles each have their own colour, dimmer when they're on but not connected.
/// </summary>
public partial class NotchWindow
{
    Color _accent = Colors.White;          // the colour of the app you were on when the notch opened
    bool _hasAmbient;
    Color? _panelTint;                     // what the open panel is tinted with right now
    RadialGradientBrush? _tintBrush;
    int _ambientVersion;

    static readonly Color WifiBlue = Color.FromRgb(0x0A, 0x84, 0xFF);
    static readonly Color FocusViolet = Color.FromRgb(0xBF, 0x5A, 0xF2);

    void WireColors()
    {
        // the tint lives in a layer behind the whole panel: one brush whose colours glide
        _tintBrush = new RadialGradientBrush
        {
            Center = new Point(0.5, 0), GradientOrigin = new Point(0.5, 0), RadiusX = 0.9, RadiusY = 1.25,
            GradientStops = { new GradientStop(Colors.Transparent, 0), new GradientStop(Colors.Transparent, 0.5), new GradientStop(Colors.Transparent, 1) },
        };
        MediaGlow.Background = _tintBrush;
        MediaGlow.Opacity = 1;

        FocusTile.OnBrush = Frozen(FocusViolet);
        BtTile.OnBrush = Frozen(WifiBlue); // the radios share one blue
    }

    /// <summary>
    /// The notch is opening: read the colour of the app in front (off the UI thread, a few milliseconds),
    /// then let the panel glow in it.
    /// </summary>
    async void SampleAmbient()
    {
        int version = ++_ambientVersion;
        var fg = GetForegroundWindow();
        ScreenColor.Area area;
        if (fg != IntPtr.Zero && fg != _hwnd && GetWindowRect(fg, out var r) && r.Right - r.Left > 200 && r.Bottom - r.Top > 150)
            area = new(Math.Max(r.Left, _mon.Left), Math.Max(r.Top, _mon.Top), Math.Min(r.Right, _mon.Right), Math.Min(r.Bottom, _mon.Bottom));
        else
            area = new(_mon.Left, _mon.Top, _mon.Right, _mon.Bottom); // the desktop itself
        int notchH = (int)Math.Round((OpenSize.Height + 24) * _scale), notchHalf = (int)Math.Round((OpenSize.Width / 2 + 30) * _scale);
        int cx = _winLeft + _winW / 2;
        var skip = new ScreenColor.Area(cx - notchHalf, _mon.Top, cx + notchHalf, _mon.Top + notchH);

        var c = await Task.Run(() => ScreenColor.Sample(area, skip));
        if (version != _ambientVersion) return;
        _hasAmbient = c != null;
        _ambientAccent = c is { } k ? ScreenColor.Glow(k) : Colors.White;
        _accent = CustomAccent ?? _look?.Accent ?? _ambientAccent; // yours, else the theme's, else the app's
        ApplyAccent();
        _panelTint = null; // repaint even if the colour is the same as last time: it fades in again with the panel
        UpdatePanelTint();
        UpdateRim();
    }

    /// <summary>A gentle gradient for slider fills: a little deeper on the left, the colour itself on the right.</summary>
    Brush AccentFill()
    {
        if (_cyber && CustomAccent == null) return CyberFill();
        var c = _accent;
        var g = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        g.GradientStops.Add(new GradientStop(Color.FromRgb((byte)(c.R * 0.72), (byte)(c.G * 0.72), (byte)(c.B * 0.72)), 0));
        g.GradientStops.Add(new GradientStop(c, 1));
        g.Freeze();
        return g;
    }

    void ApplyAccent()
    {
        ButtonColours();
        var fill = AccentFill();
        foreach (var s in new[] { VolSlider, BriSlider, ExtBriSlider, AppVolSlider }) s.FillBrush = fill;
        foreach (var row in _mixerRows.Values) row.Slider.FillBrush = fill;
        UpdateDots(animate: false);
        PaintBars(); // the music bars wear the theme too
    }

    /// <summary>
    /// The buttons' colours follow Pixo's: a thin rim, a glow under the pointer, and the selected tab filled with it
    /// (a colourless moment gets a soft indigo; Neon its cyan). Brushes shared by every button, swapped as one.
    /// </summary>
    void ButtonColours()
    {
        var c = _cyber && CustomAccent == null ? CyCyan : _accent;
        bool grey = Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B)) < 30;
        if (grey) c = Color.FromRgb(0x7B, 0x7A, 0xF0);
        double lum = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;
        // a very light colour is deepened so white words stay readable on it
        var deep = lum > 0.62 ? Color.FromRgb((byte)(c.R * .62), (byte)(c.G * .62), (byte)(c.B * .62)) : c;
        // the same colour as last time: nothing to swap (each swap makes WPF look through every page for who uses it)
        if (_buttonsFor == c) return;
        _buttonsFor = c;
        var res = Application.Current.Resources;
        var rim = new SolidColorBrush(A(c, 0x48)); rim.Freeze();
        var glow = new LinearGradientBrush(A(c, 0x55), A(c, 0x16), 90); glow.Freeze();
        var fill = new LinearGradientBrush(Lighten(deep, 0.12), Color.FromRgb((byte)(deep.R * .7), (byte)(deep.G * .7), (byte)(deep.B * .7)), 45); fill.Freeze();
        res["AccentRim"] = rim;
        res["AccentGlow"] = glow;
        res["AccentFill"] = fill;
    }

    Color? _buttonsFor; // the colour the shared button brushes wear now

    bool ColourfulCover => _media.HasSession && _media.Art != null && _media.Accent != Colors.White;

    /// <summary>What the open panel glows with: a live thing's own colour, else the app you're on.</summary>
    Color? PanelTintColor() => MinimalOn ? (_look!.Key == "mono" ? null : CustomGlow) : OwnColors ? _look!.Tint : CurrentActivity switch // (Minimal: no glow but the one you chose) // Dusk, Coffee, Crystal: always their own light
    {
        LiveActivity.Call => ((SolidColorBrush)CallBrush).Color,
        LiveActivity.Media when ColourfulCover => _media.Accent,
        LiveActivity.Download => WifiBlue,
        LiveActivity.Timer => ((SolidColorBrush)TimerBrush).Color,
        LiveActivity.Focus => FocusViolet,
        _ when CustomGlow is { } glow => glow, // your glow (Colors page)
        _ when _look is { Tint: { } lt } lk && (lk.TintAlways || !_hasAmbient) => lt, // the glass themes' own light
        _ when _cyber => CyTeal, // the cyber skin glows teal from the top, whatever app is in front
        _ => _hasAmbient ? _ambientAccent : ColourfulCover ? _media.Accent : null,
    };

    /// <summary>The panel's rim while open: the same colour, so the glow has an edge.</summary>
    Color? ExpandedRimColor() => CustomEdge ?? PanelTintColor();

    /// <summary>The deep glow behind the open panel, gliding to the new colour.</summary>
    void UpdatePanelTint()
    {
        if (_tintBrush == null) return;
        var c = PanelTintColor();
        if (_skinOn && c is { } pc && pc != CustomGlow) c = _skinMap(pc); // live colours in the theme's palette
        if (c == _panelTint) return;
        bool fresh = _panelTint == null;
        _panelTint = c;
        // music and calls glow from their artwork on the left; the app's colour pours in from the top
        bool fromArt = CurrentActivity is LiveActivity.Call || (CurrentActivity == LiveActivity.Media && ColourfulCover);
        var center = fromArt ? new Point(0.14, 0.32) : new Point(0.5, 0);
        _tintBrush.Center = _tintBrush.GradientOrigin = center;
        var k = c ?? Colors.Black;
        // a colourless app gets a quieter, silvery glow; a colourful one the full colour
        byte[] alpha = c == null ? new byte[] { 0, 0, 0 } : c == ScreenColor.Neutral ? new byte[] { 0x3C, 0x18, 0x08 } : new byte[] { 0x78, 0x34, 0x12 };
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var dur = TimeSpan.FromMilliseconds(fresh ? 800 : 650);
        for (int i = 0; i < 3; i++)
        {
            var stop = _tintBrush.GradientStops[i];
            var to = Color.FromArgb(alpha[i], k.R, k.G, k.B);
            // a fresh glow fades in from nothing in its own colour (not from the last one's)
            var anim = fresh ? new ColorAnimation(Color.FromArgb(0, k.R, k.G, k.B), to, dur) : new ColorAnimation(to, dur);
            anim.EasingFunction = ease;
            stop.BeginAnimation(GradientStop.ColorProperty, anim);
        }
        ProgressSlider.FillBrush = ColourfulCover ? Frozen(_media.Accent) : AccentFill(); // the song's bar keeps the song's colour
        if (_mode == Mode.Expanded) UpdateRim();
    }

    /// <summary>Tiles say more than on / off: Wi-Fi on but not connected, Bluetooth on with nothing connected.</summary>
    void ColorTiles()
    {
        bool wifiConnected = _radio.WifiOn && !string.IsNullOrEmpty(_radio.NetworkName);
        WifiTile.OnBrush = Frozen(wifiConnected ? WifiBlue : Dim(WifiBlue));
        bool btDevice = _radio.BluetoothOn && _bt.Primary != null;
        BtTile.OnBrush = Frozen(btDevice ? WifiBlue : Dim(WifiBlue));
    }

    static Color Dim(Color c) => Color.FromRgb((byte)(c.R * 0.45 + 0x26 * 0.55), (byte)(c.G * 0.45 + 0x26 * 0.55), (byte)(c.B * 0.45 + 0x2A * 0.55));

    /// <summary>The brightness icons glow with the level: dim when the screen is dim.</summary>
    void BrightnessGlyphs()
    {
        BriGlyph.Opacity = 0.5 + 0.5 * Math.Clamp(BriSlider.Value, 0, 1);
        ExtBriGlyph.Opacity = 0.5 + 0.5 * Math.Clamp(ExtBriSlider.Value, 0, 1);
    }
}
