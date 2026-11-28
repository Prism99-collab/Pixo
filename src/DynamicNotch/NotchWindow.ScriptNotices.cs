using System.Windows.Media;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// Notices from your scripts and builds (Services/ScriptNotices): each one arrives as a pop-up like any other, with a
/// tick, a cross, a warning or a terminal sign, and waits in Notifs &amp; alerts afterwards.
/// </summary>
public partial class NotchWindow
{
    void WireScriptNotices() => ScriptNotices.Listen(_settings.ScriptNotices, n => Dispatcher.BeginInvoke(() => ShowScriptNotice(n)));

    internal void SetScriptNotices(bool on)
    {
        _settings.ScriptNotices = on;
        WireScriptNotices();
    }

    void ShowScriptNotice(ScriptNotices.Notice n)
    {
        var (glyph, color) = n.Icon switch
        {
            "ok" => ("", Color.FromRgb(0x30, 0xD1, 0x58)),
            "fail" => ("", Color.FromRgb(0xFF, 0x45, 0x3A)),
            "warn" => ("", Color.FromRgb(0xFF, 0x9F, 0x0A)),
            _ => ("", Color.FromRgb(0xEC, 0xEB, 0xE8)),
        };
        AppLog.Write($"script notice: {n.Icon}");
        ShowOsd("script", glyph, null, n.Title ?? n.Text, n.Title != null ? n.Text : null, null,
            glyphBrush: Frozen(new SolidColorBrush(color)), seconds: n.Seconds, prio: n.Icon == "fail" ? Prio.High : Prio.Normal);
    }
}
