using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace DynamicNotch;

/// <summary>
/// The open panel's size (Preferences → Appearance → Panel size): Compact, Standard, Large, Extra large.
/// The whole open panel scales as one (text, icons, gaps, cards, corners), so nothing gets crowded or stretched;
/// the closed notch keeps its own sizes. How:
///  • PanelHost (around Expanded) has one LayoutTransform: inside, every page still lays out at 440 × 272, so no page
///    knows about the size. The pill, the window and the "is the pointer on it" tests use the scaled size (OpenSize).
///  • A change while it's open glides: the new size is laid out once (the picture held as it was by a counter-scale),
///    then the pill springs to its new size and the content's scale rides the pill's width each frame (a GPU scale,
///    no layout per frame). A bigger window is made before the motion starts, a smaller one after it ends, so the
///    one slow step (Windows resizing a see-through window) never lands in the middle of a movement.
///  • On a small screen the larger sizes are capped to what fits (60 % of its height, its width).
/// </summary>
public partial class NotchWindow
{
    internal static readonly (string Key, string Name, double K)[] PanelSizes =
    {
        ("compact", "Compact", 0.9), ("standard", "Standard", 1.0), ("large", "Large", 1.1), ("xl", "Extra large", 1.2),
    };

    /// <summary>The open panel's scale (1: as designed).</summary>
    double _k = 1;
    readonly ScaleTransform _panelZoom = new(1, 1), _panelGlide = new(1, 1);
    bool _gliding;

    /// <summary>The open panel's size on screen (in the window's units).</summary>
    Size OpenSize => new(ExpandedSize.Width * _k, ExpandedSize.Height * _k);

    double FullWidth => Math.Max(560, Math.Ceiling(ExpandedSize.Width * _k) + 120);
    double FullHeight => Math.Max(300, Math.Ceiling(ExpandedSize.Height * _k) + 28);

    /// <summary>The scale a size asks for, capped to what this screen has room for.</summary>
    double PanelKFor(string key)
    {
        double k = PanelSizes.FirstOrDefault(p => p.Key == key).K;
        if (k <= 0) k = 1;
        var screen = System.Windows.Forms.Screen.PrimaryScreen;
        if (screen is { Bounds.Width: > 0, Bounds.Height: > 0 })
        {
            double s = Math.Max(1, _scale), w = screen.Bounds.Width / s, h = screen.Bounds.Height / s;
            double fit = Math.Min((h * 0.6 - 28) / ExpandedSize.Height, (w - 120) / ExpandedSize.Width);
            k = Math.Min(k, Math.Max(0.9, fit));
        }
        return Math.Round(k, 3);
    }

    /// <summary>At start: the saved size, at once (nothing moves).</summary>
    void WirePanelScale()
    {
        PanelHost.LayoutTransform = _panelZoom;
        PanelHost.RenderTransform = _panelGlide;
        PanelHost.RenderTransformOrigin = new Point(0.5, 0);
        SetPanelScale(PanelKFor(_settings.PanelSize), glide: false);
    }

    /// <summary>Preferences: a size picked. Open, it glides there; closed, it opens to show it (and closes again by itself).</summary>
    internal void PrefSetPanelSize(string key)
    {
        _settings.PanelSize = key;
        _settings.Save();
        double k = PanelKFor(key);
        bool open = _mode == Mode.Expanded;
        SetPanelScale(k, glide: open && !Eco);
        if (!open && IsBase(_mode) && _mode != Mode.Game && !_hidden) After(60, () => { if (IsBase(_mode)) Expand(byHotkey: true); }); // (the size, seen: it closes after a few seconds if you don't come over)
    }

    void SetPanelScale(double k, bool glide)
    {
        if (Math.Abs(k - _k) < 0.001) { ApplyWindowSize(); return; }
        double was = _k;
        double shown = _panelZoom.ScaleX * _panelGlide.ScaleX; // (as it looks now, mid-glide too)
        _k = k;
        if (!glide)
        {
            EndGlide();
            _panelZoom.ScaleX = _panelZoom.ScaleY = k;
            ApplyWindowSize();
            if (_mode == Mode.Expanded) ApplyShape();
            return;
        }
        // 1. the new size laid out, held looking as before; a bigger window made now, while nothing moves
        _panelZoom.ScaleX = _panelZoom.ScaleY = k;
        _gliding = true;
        _panelGlide.ScaleX = _panelGlide.ScaleY = shown / k;
        // while it glides the panel is drawn once and that picture scaled (each frame redrawing every letter at a new
        // size cost 20-45 ms frames); sharp enough at its largest, and drawn properly again the moment it lands
        PanelHost.CacheMode = new BitmapCache(Math.Max(1, _scale) * Math.Max(1, shown / k));
        if (k > was) ApplyWindowSize();
        // 2. once that's on screen: the pill springs over, the content rides its width (GlideFrame)
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            if (_mode != Mode.Expanded) { EndGlide(); return; }
            ApplyShape();
        });
        // 3. a smaller window, once the motion is over
        if (k < was) After(900, () => { if (Math.Abs(_k - k) < 0.001) ApplyWindowSize(); });
    }

    /// <summary>Each frame of the pill's spring while it glides: the content as wide as the pill.</summary>
    void GlideFrame()
    {
        if (!_gliding) return;
        if (_mode != Mode.Expanded) { EndGlide(); return; }
        double target = OpenSize.Width;
        double s = Math.Clamp(_sw.Value / target, 0.5, 2);
        if (Math.Abs(_sw.Value - _sw.Target) < 0.3 && Math.Abs(_sw.Target - target) < 0.5) { EndGlide(); return; }
        _panelGlide.ScaleX = _panelGlide.ScaleY = s;
    }

    void EndGlide()
    {
        _gliding = false;
        _panelGlide.ScaleX = _panelGlide.ScaleY = 1;
        PanelHost.CacheMode = null;
    }

    /// <summary>The window: as big as the open panel needs at this size (with the chat's extra room), unless the game pill has it small.</summary>
    void ApplyWindowSize()
    {
        if (_smallWindow) return;
        double w = FullWidth, h = FullHeight + _chatExtra * _k;
        if (Math.Abs(Width - w) < 0.5 && Math.Abs(Height - h) < 0.5) return;
        Width = w;
        Height = h;
        PositionWindow();
    }
}
