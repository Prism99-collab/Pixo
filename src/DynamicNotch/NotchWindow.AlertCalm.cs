using System.Windows.Media;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// Calm alerts (on by default; Preferences → Alerts, or the switch on the Notifs &amp; alerts page):
///  • what matters still pops up (people, codes, urgent, for you), a little shorter than before;
///  • what doesn't (offers, an app flooding you, apps you set to Quiet) never pops up: Pixo's face only glances up,
///    wearing the app's colour for a moment, and it waits in Notifs &amp; alerts;
///  • the notch's own alerts stay a little shorter too, and the ones worth looking back on (a download, a reminder,
///    low battery…) are kept in Notifs &amp; alerts with the notifications.
/// Apps set to Off are neither shown nor kept.
/// </summary>
public partial class NotchWindow
{
    /// <summary>The app's name as kept in the Quiet list.</summary>
    static string QuietKey(string app) => app.Trim().ToLowerInvariant();

    bool AppQuiet(string app) => _settings.QuietApps.Contains(QuietKey(app));

    /// <summary>How an app's notifications come in: "show" (pop up), "quiet" (kept, the face glances) or "off".</summary>
    string AppAlertMode(string app) => NotifHistory.IsMuted(app) ? "off" : AppQuiet(app) ? "quiet" : "show";

    void SetAppAlertMode(string app, string mode)
    {
        _settings.QuietApps.RemoveAll(a => a == QuietKey(app));
        if (mode == "quiet") _settings.QuietApps.Add(QuietKey(app));
        _settings.Save();
        NotifHistory.Mute(app, mode == "off"); // (off: its kept ones go too, as "Mute" always did)
    }

    /// <summary>
    /// A notification that shouldn't pop up: its app is set to Quiet; or, calm, an offer, or an app (not a person) flooding
    /// you. A code, something urgent or for you always pops up, whatever the app's setting.
    /// </summary>
    bool QuietToast(ToastInfo t, bool rushing)
    {
        string title = Clean(t.Title), body = Clean(t.Body);
        if (FindCode(title + " " + body) != null) return false;
        var s = Sense(t, title, body);
        if (s.Urgent || s.Mention) return false;
        if (AppQuiet(t.App)) return true;
        if (!_settings.CalmAlerts) return false;
        return s.Kind == SenseKind.Promo || rushing && s.Kind is not (SenseKind.Person or SenseKind.Group);
    }

    (DateTime Day, int Count) _quiet;

    /// <summary>How many came in quietly today (said on the Notifs &amp; alerts page).</summary>
    int QuietToday => _quiet.Day == DateTime.Today ? _quiet.Count : 0;

    /// <summary>A quiet notification, as the closed notch's face takes it: a glance up, in the app's colour, nothing more.</summary>
    void FaceGlance(ToastInfo t)
    {
        _quiet = (DateTime.Today, QuietToday + 1);
        if (!_faceShown || _face is not { } face) return;
        var c = BrandColor(t.App) ?? (t.Icon != null && IconHelper.Accent(t.Icon) is { } ic ? Vivid(ic) : (Color?)null);
        if (c is { } col) face.Tint(col, 1.4);
        face.Blink();
        face.LookAt(0, 0.7);
    }

    /// <summary>How long a pop-up stays, calm: the notch's own ones a fifth shorter (levels, calls and forced ones as they are).</summary>
    double CalmSeconds(string kind, double? level, bool force, double seconds)
    {
        if (!_settings.CalmAlerts || level != null || force || kind.StartsWith("toast:", StringComparison.Ordinal) || kind is "ring" or "call" or "call-end") return seconds;
        return Math.Max(1.5, seconds * 0.8); // (never under 1.5 s: the countdown line under it would go)
    }

    /// <summary>The notch's own alerts worth looking back on, kept in Notifs &amp; alerts.</summary>
    static bool KeptAlert(string kind) =>
        kind is "download" or "download-failed" or "battery" or "reminder" or "timer" or "strain" or "full-charge" or "smart-charge" or "smart-drain"
        || kind.StartsWith("app-alert:", StringComparison.Ordinal);

    void KeepAlert(string kind, string? title, string? subtitle)
    {
        if (!KeptAlert(kind) || string.IsNullOrWhiteSpace(title)) return;
        NotifHistory.Add(DateTime.Now, NotifHistory.PixoApp, title!, subtitle ?? "");
        if (_mode == Mode.Expanded && _page == WorkPage && _workTab == 2 && !_workTyping) Dispatcher.BeginInvoke(() => RenderWorkTab(animate: false));
    }
}
