using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

namespace DynamicNotch;

/// <summary>
/// One moment, one pop-up. Things that happen together are told together, on any PC, with any app:
///  • the same app at once (a call ringing, the app's warning, then a message from the caller): the most important one
///    shows (a ringing call, then an app's error, then a message), the others count on it as "+1", "+2" — never a
///    second pop-up. An app's taskbar flash that comes with its own notification is the same news: not counted;
///  • while an app rings (or you're in its call), its other news never pops up: it waits in Notifs &amp; alerts (a call
///    in a browser doesn't hold the whole browser's news);
///  • several apps at once: the first shows, and the ones waiting behind it become one line, "3 more · WhatsApp,
///    Teams", a click away from Notifs &amp; alerts (instead of a chain of pop-ups one after another).
/// What must be seen (a code, something urgent or for you) is never counted away or folded: it shows.
/// </summary>
public partial class NotchWindow
{
    int _osdMerged;

    static readonly Regex CallInBrowser = new(@"chrome|edge|brave|firefox|opera|vivaldi|\barc\b|browser|meet", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The app a pop-up is about (its key: "discord", "teams"…), when it's an app's news.</summary>
    string? Source(OsdRequest r)
    {
        string k = r.Kind;
        string? app = k.StartsWith("toast:", StringComparison.Ordinal) ? k[6..]
            : k.StartsWith("flash:", StringComparison.Ordinal) ? k[6..]
            : k.StartsWith("app-alert:", StringComparison.Ordinal) ? k[10..]
            : k == "ring" ? _ringWho.App
            : k == "call" ? _call?.Name
            : null;
        return app is { Length: > 0 } ? ChatAppKey(app) : null;
    }

    /// <summary>An app's news (a notification, a flash, its warning): what can be told together.</summary>
    static bool IsNews(OsdRequest r) => r.Kind.StartsWith("toast:", StringComparison.Ordinal) || r.Kind.StartsWith("flash:", StringComparison.Ordinal) || r.Kind.StartsWith("app-alert:", StringComparison.Ordinal);

    /// <summary>Which of the same app's news matters most: its ringing call, its error, a message, a flash.</summary>
    static int Rank(OsdRequest r) => Sticky(r) ? 3 : r.Kind.StartsWith("app-alert:", StringComparison.Ordinal) ? 2 : r.Kind.StartsWith("toast:", StringComparison.Ordinal) ? 1 : 0;

    /// <summary>
    /// A new pop-up, before it's shown: true when it's told with what's already there (it joins the one on screen, or
    /// waits for the ringing app). False: it shows; taking the same app's lesser news' place, it carries its count.
    /// </summary>
    bool Together(ref OsdRequest req)
    {
        if (req.Level != null || Source(req) is not { } src) return false;
        // the app that's ringing, or the call you're in: its other news waits in Notifs & alerts
        if (!Sticky(req) && !req.MustShow && HoldsNews(src)) return true;
        if (_mode != Mode.Osd || _osd is not { } shown || shown.Kind == req.Kind) return false; // (the same kind: a run of messages, as before)
        double elapsed = (DateTime.UtcNow - _osdShownAt).TotalSeconds;
        if (Source(shown) != src || elapsed > 4) return false;
        if (req.MustShow && Sticky(shown)) return false; // (a code while it rings: shown after the call card, as any pop-up)
        if (Rank(req) > Rank(shown) || req.MustShow)
        {
            // the more important one takes its place, counting what it replaces (a flash was the same news)
            int merged = Sticky(req) ? 0 : _osdMerged + (shown.Kind.StartsWith("flash:", StringComparison.Ordinal) ? 0 : 1);
            req = req with { Merged = merged, Replaces = shown.Kind };
            _osdQueue.RemoveAll(q => Source(q) == src && !Sticky(q) && !q.MustShow);
            return false;
        }
        if (req.Kind.StartsWith("flash:", StringComparison.Ordinal) || Sticky(shown)) return true; // (the same news, or the call says it all)
        _osdMerged++;
        ShowMerged();
        // told on a pop-up about to leave: it stays a moment longer, so the count is seen
        if (shown.Seconds - elapsed < 1.5 && !_osdHover)
        {
            _osdTimer.Stop();
            _osdTimer.Interval = TimeSpan.FromSeconds(1.6);
            _osdTimer.Start();
            OsdCountdownStart(shown with { Seconds = 1.6 });
        }
        return true;
    }

    /// <summary>The app rings, or you're in its call (a call in a browser doesn't hold the whole browser's news).</summary>
    bool HoldsNews(string src) =>
        Ringing && src == ChatAppKey(_ringWho.App) && !CallInBrowser.IsMatch(_ringWho.App)
        || OnCall && _call is { } c && src == ChatAppKey(c.Name) && !CallInBrowser.IsMatch(c.Name);

    /// <summary>A pop-up was presented: its count of news told with it (a run of the same kind keeps its count).</summary>
    void MergedOnPresent(OsdRequest r, bool sameKind)
    {
        if (!sameKind) _osdMerged = r.Merged;
        if (_osdMerged > 0) ShowMerged();
        else if (_cardTag != null) _cardTag.ToolTip = null;
    }

    /// <summary>"+2" on the side of the pop-up (in place of its tag), and the pop-up refitted.</summary>
    void ShowMerged()
    {
        if (_cardTag == null || _osd == null || _osdMerged <= 0) return;
        OsdRight.Text = "+" + _osdMerged;
        OsdRight.FontSize = 10.5;
        OsdRight.Foreground = Frozen(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF));
        _cardTag.Background = Frozen(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
        _cardTag.BorderThickness = new Thickness(0);
        _cardTag.Padding = new Thickness(6, 1, 6, 2);
        _cardTag.ToolTip = "Also from this app just now: in Notifs & alerts";
        if (!_cardOpen) OsdCompactFit(_osd);
        ApplyShape();
    }

    /// <summary>
    /// Several apps' news waiting behind the pop-up on screen: told as one line ("3 more · WhatsApp, Teams") instead of
    /// one after another; a click opens Notifs &amp; alerts. What must be seen, and anything else waiting, stays as it is.
    /// </summary>
    void BundleWaiting()
    {
        var news = _osdQueue.Where(q => IsNews(q) && !Sticky(q) && !q.MustShow).ToList();
        if (news.Count < 2) return;
        foreach (var q in news) _osdQueue.Remove(q);
        var names = news.Select(q => q.Kind[(q.Kind.IndexOf(':') + 1)..]).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string apps = string.Join(", ", names.Take(3)) + (names.Count > 3 ? "…" : "");
        _osdQueue.Add(new OsdRequest("more", G.Bell, null, $"{news.Count} more", apps, null, null, null, null,
            2.6, false, news.Max(q => q.Priority), OpenNotifsPage, null, Motion.None, DateTime.UtcNow));
    }

    /// <summary>Notifs &amp; alerts, opened from a pop-up.</summary>
    void OpenNotifsPage()
    {
        _inboxView = "all"; _inboxApp = ""; _inboxFind = "";
        _workTab = 2;
        ExpandTo(WorkPage);
    }
}
