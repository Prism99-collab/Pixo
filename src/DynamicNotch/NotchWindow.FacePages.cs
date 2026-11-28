using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DynamicNotch.Controls;

namespace DynamicNotch;

/// <summary>
/// Pixo's face across the open notch's pages, gently (a glance, a small move; never a show):
///  • pages: it glances the way you swipe; pushed past the first or the last page, it bumps softly against the end;
///  • lists: its eyes follow the way you scroll, and the end of a list gets the same soft bump;
///  • System: it looks around while the PC is first checked, gets focused when the PC works hard, tired when it runs
///    hot, maxed or low on battery, and pleased when all is calm again; plugging in: a wink, in green;
///  • Colors: pointing at a preset, it tries the colour on;
///  • Tools: in a timer's last ten seconds it watches the ring;
///  • Home (off by default, Preferences → Appearance): back after a while, one short line beside it (new messages,
///    someone waiting, a reminder soon, notifs that came in quietly); a click goes there.
/// </summary>
public partial class NotchWindow
{
    DateTime _faceBumpAt, _lastClosedAt;

    /// <summary>The face on screen in the open notch, when there's one and the face is on.</summary>
    NotchFace? PageFace => _settings.NotchFace ? FeelFace() : null;

    /// <summary>A push past an end (dx: -1 left, 1 right; dy: -1 up, 1 down): a soft bump that way, at most every 0.7 s.</summary>
    void FaceBump(int dx, int dy)
    {
        var now = DateTime.UtcNow;
        if ((now - _faceBumpAt).TotalMilliseconds < 700 || PageFace is not { } face) return;
        _faceBumpAt = now;
        face.Bump(dx, dy);
    }

    /// <summary>A list moved under the wheel (dir 1: down): the eyes follow; at its end, a bump.</summary>
    void FaceScrolled(bool moved, int dir)
    {
        if (PageFace is not { } face) return;
        if (moved) face.LookAt(0, dir > 0 ? -.45 : .35); // (up is +y)
        else FaceBump(0, dir);
    }

    // ───────────── Tools ─────────────

    /// <summary>A timer's last ten seconds, on Tools: the face watches the ring.</summary>
    void FaceWatchTimer()
    {
        if (_mode != Mode.Expanded || _page != 2 || !_timerRunning || _stopwatch || PageFace is not { } face) return;
        double left = (_timerEnd - DateTime.UtcNow).TotalSeconds;
        if (left is > 0 and <= 10) face.LookAt(0, -.45);
    }

    // ───────────── Home: a line when you're back ─────────────

    string? _hintShown;
    Action? _hintGo;
    bool _hintWired;

    /// <summary>The notch opened on Home after ten minutes or more: one short line beside the face, if there's something to say.</summary>
    void FaceHomeHint()
    {
        if (!_settings.FaceHints || !_settings.NotchFace || _page != 0 || _launcherOpen || _hintShown != null) return;
        if (_lastClosedAt != default && DateTime.UtcNow - _lastClosedAt < TimeSpan.FromMinutes(10)) return;
        if (HintNow() is not { } hint) return;
        if (!_hintWired)
        {
            _hintWired = true;
            PageTitle.MouseLeftButtonUp += (_, e) => { if (_hintShown == null || _hintGo is not { } go) return; e.Handled = true; EndHint(); go(); };
        }
        _hintShown = hint.Text;
        _hintGo = hint.Go;
        if (_decoding.Remove(PageTitle, out var decoding)) decoding.Stop(); // (Neon: a title still decoding would write over it)
        PageTitle.Text = hint.Text;
        PageTitle.Cursor = Cursors.Hand;
        PageTitle.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(Eco ? 1 : 260)));
        After(320, () => { if (_hintShown != null) _headFace?.LookAt(1, 0); }); // it looks at its words
        After(4600, EndHint);
    }

    void EndHint()
    {
        if (_hintShown is not { } said) return;
        _hintShown = null;
        _hintGo = null;
        PageTitle.Cursor = null;
        if (PageTitle.Text != said) { PageTitle.BeginAnimation(OpacityProperty, null); return; } // (a page change wrote its own name)
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(Eco ? 1 : 300));
        fade.Completed += (_, _) =>
        {
            if (PageTitle.Text == said) PageTitle.Text = Services.Loc.T(PageNames[_page]);
            PageTitle.BeginAnimation(OpacityProperty, null);
        };
        PageTitle.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>What's worth one line now (the most important first), and where a click takes you.</summary>
    (string Text, Action Go)? HintNow()
    {
        if (PageHidden(WorkPage)) return null; // (its lines lead there)
        int chats = NewChats();
        if (chats > 0) return (chats == 1 ? "A new message" : $"{chats} new messages", () => OpenFlowTab(ChatsTab));
        int waiting = Services.NotifHistory.Waiting().Count;
        if (waiting > 0) return (waiting == 1 ? "Someone's waiting on you" : $"{waiting} people waiting on you", () => OpenFlowTab(2, "you"));
        var now = DateTime.Now;
        if (_reminders.Where(r => r.At > now && r.At - now < TimeSpan.FromMinutes(90)).OrderBy(r => r.At).FirstOrDefault() is { } next)
            return ($"{Short(next.Text, 26)} at {next.At:t}", () => { });
        int quiet = QuietToday;
        if (quiet > 0) return (quiet == 1 ? "1 notif came in quietly" : $"{quiet} notifs came in quietly", () => OpenFlowTab(2, "all"));
        return null;
    }

    /// <summary>The Flow page, on one of its tabs (Notifs &amp; alerts: on one of its views).</summary>
    void OpenFlowTab(int tab, string? view = null)
    {
        if (view != null) { _inboxView = view; _inboxApp = ""; _inboxFind = ""; }
        _workTab = tab;
        GoToPage(WorkPage);
    }
}
