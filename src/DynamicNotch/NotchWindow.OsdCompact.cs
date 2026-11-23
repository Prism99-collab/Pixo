using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DynamicNotch;

/// <summary>
/// The pop-up's size and layout, in one place:
///  • an app's news and the notch's own alerts: one line, 32 px tall: a small icon, the title, then "· what happened"
///    dimmer; only as wide as its words need (210–340);
///  • a person's message: two lines (who, then what), 40 px;
///  • kept as they were: a ringing call, a call, a level (volume, brightness), a battery, and Neon's HUD look.
/// Pointing at it still opens the whole card (it goes back to two lines for that).
/// </summary>
public partial class NotchWindow
{
    const double OneLineH = 32, PersonH = 40;
    bool _osdOneLine;

    /// <summary>Kept as it was: Neon, a level, a battery, a ringing call or a call.</summary>
    bool OsdKeepsItsLook(OsdRequest r) => _cyber || r.Level.HasValue || r.Battery != null || Sticky(r) || r.Kind is "fps" or "pill";

    /// <summary>The pop-up's words, icon, face, line and size (after its words and dress are set).</summary>
    void OsdCompactFit(OsdRequest r)
    {
        bool keep = OsdKeepsItsLook(r);
        bool person = r.Look?.Initials != null;
        bool hasSub = !string.IsNullOrEmpty(r.Subtitle);
        _osdOneLine = !keep && !person;
        bool face = _osdFace is { Visibility: Visibility.Visible };

        // words: side by side on one line, or stacked
        OsdTextPanel.Orientation = _osdOneLine ? Orientation.Horizontal : Orientation.Vertical;
        OsdTitle.FontSize = keep ? 12.5 : 12;
        OsdSub.FontSize = _osdOneLine ? 12 : 11;
        OsdSub.Margin = _osdOneLine ? new Thickness(6, 0, 0, 0) : new Thickness(0, 1, 0, 0);
        OsdTitle.MaxWidth = OsdSub.MaxWidth = double.PositiveInfinity;
        if (_osdOneLine && hasSub) OsdSub.Text = "·  " + r.Subtitle;
        OsdTextPanel.Margin = keep ? new Thickness(12, 0, 10, 0) : new Thickness(9, 0, 6, 0);

        // the icon, the avatar, the face, the line
        OsdGlyph.FontSize = keep ? (OsdGlyph.FontFamily == SymbolFont ? 17 : 15) : 13;
        double avatar = keep ? 30 : 26;
        if (_cardAvatar != null) _cardAvatar.Width = _cardAvatar.Height = avatar;
        if (_cardAvatarFill != null) _cardAvatarFill.CornerRadius = new CornerRadius(avatar / 2);
        OsdIconHost.Width = OsdIconHost.Height = keep
            ? (r.Art != null || person ? 30 : r.Battery != null ? 26 : 22)
            : (person ? 26 : r.Art != null ? 20 : 18);
        if (_osdFace != null)
        {
            _osdFace.RenderTransformOrigin = new Point(0.5, 0.5);
            _osdFace.RenderTransform = keep ? Transform.Identity : new ScaleTransform(0.85, 0.85);
            _osdFace.Margin = keep ? new Thickness(0, 0, 13, 2) : new Thickness(0, 0, 9, 1);
        }
        OsdInner.Margin = keep ? (face ? OsdInnerWithFace : OsdInnerPlain) : new Thickness(12, 0, face ? 32 : 12, 1);
        var line = OsdLine();
        line.Height = keep ? 2 : 1.5;
        line.Margin = keep ? new Thickness(30, 0, 30, 4) : new Thickness(24, 0, 24, 1.5);

        // the size
        double w, h;
        if (keep)
        {
            bool level = r.Level.HasValue, wide = r.Art != null || person;
            w = level ? 300 : wide ? 360 : 330;
            h = level || !hasSub ? 36 : wide ? 50 : 46;
        }
        else if (!_osdOneLine) { w = 330; h = PersonH; }
        else
        {
            // as wide as the words need; the title keeps at least half of the room, the rest goes to what happened
            var inf = new Size(double.PositiveInfinity, double.PositiveInfinity);
            OsdTitle.Measure(inf);
            OsdSub.Measure(inf);
            OsdChevron.Measure(inf);
            _cardTag?.Measure(inf);
            double titleW = OsdTitle.DesiredSize.Width, subW = hasSub ? OsdSub.DesiredSize.Width + 6 : 0;
            double chrome = OsdChrome();
            w = Math.Clamp(Math.Ceiling(chrome + titleW + subW + 2), 210, 340);
            double room = w - chrome - 2;
            double titleMax = hasSub ? Math.Max(room * 0.5, room - subW) : room;
            OsdTitle.MaxWidth = Math.Max(40, titleMax);
            if (hasSub) OsdSub.MaxWidth = Math.Max(30, room - Math.Min(titleW, titleMax) - 6);
            h = OneLineH;
        }
        Osd.Width = w;
        Osd.Height = h;
        _osdSize = new Size(w, h);
    }

    /// <summary>What the pop-up takes around its words: margins, the icon, the tag or count, the chevron (its room is kept, shown or not).</summary>
    double OsdChrome() =>
        OsdInner.Margin.Left + OsdInner.Margin.Right + OsdIconHost.Width + OsdTextPanel.Margin.Left + OsdTextPanel.Margin.Right
        + (_cardTag?.DesiredSize.Width ?? 0) + OsdChevron.DesiredSize.Width;

    /// <summary>The room the words have at the pop-up's width now (for measuring the opened card).</summary>
    double OsdWordsRoom() => Math.Max(120, _osdSize.Width - OsdChrome());

    /// <summary>Opening the whole card from one line: back to stacked words, the message whole (CardOpen grows it). True if it was one line.</summary>
    bool OsdUnfold()
    {
        if (!_osdOneLine || _osd == null) return false;
        _osdOneLine = false;
        OsdTextPanel.Orientation = Orientation.Vertical;
        OsdTitle.MaxWidth = OsdSub.MaxWidth = double.PositiveInfinity;
        OsdSub.Margin = new Thickness(0, 1, 0, 0);
        OsdSub.FontSize = 11;
        OsdSub.Text = _osd.Subtitle ?? "";
        double w = Math.Max(_osdSize.Width, 330);
        Osd.Width = w;
        _osdSize = new Size(w, _osdSize.Height);
        return true;
    }
}
