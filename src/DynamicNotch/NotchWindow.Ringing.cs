using System.IO;
using System.Windows.Media;
using System.Windows.Threading;
using DynamicNotch.Services;
using static DynamicNotch.Interop.Native;

namespace DynamicNotch;

/// <summary>
/// An incoming call, before you answer. Two signals, whichever comes first:
///  • a notification that says it's a call ("Incoming call", "… is calling you", in several languages);
///  • a calling app (Discord, Teams, WhatsApp…) flashing its taskbar button while it plays sound (its ringtone)
///    and you're not on a call yet; a message makes a short ping, a ring keeps sounding.
/// The notch then shows "Incoming call" with the app's icon and gives a soft pulse every second and a half until
/// you answer (the call activity takes over), it stops ringing, or 45 s pass. A click brings the app to the front.
/// </summary>
public partial class NotchWindow
{
    static readonly string[] CallWords =
    {
        "incoming call", "incoming voice call", "incoming video call", "is calling you", "is calling", "calling you",
        "appel entrant", "vous appelle", "llamada entrante", "te está llamando", "chiamata in arrivo", "eingehender anruf",
        "chamada recebida", "مكالمة واردة", "يتصل بك",
        // Discord and others say who started it ("Alex started a call.")
        "started a call", "started a video call", "a démarré un appel", "a lancé un appel", "a commencé un appel",
        "inició una llamada", "ha iniziato una chiamata", "hat einen anruf gestartet", "iniciou uma chamada", "wants to call you",
    };

    // a call that's over or was missed is a message, not a ring
    static readonly string[] NotRinging = { "missed", "manqué", "perdida", "persa", "verpasst", "فائتة", "ended", "terminé", "a duré", "lasted" };

    string? _ringPath;              // the app ringing (exe path), when known
    DateTime _ringSince;
    int _ringQuiet;                 // checks in a row where the ringing app made no sound
    readonly DispatcherTimer _ringTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1.5) };
    bool _ringChecking;

    bool Ringing => _ringTimer.IsEnabled;

    void WireRinging()
    {
        _ringTimer.Tick += async (_, _) => await RingTick();
    }

    static bool LooksLikeCall(string text) =>
        CallWords.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase)) && !NotRinging.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>From a notification: does it announce a call? (Then it's shown as ringing, not as a message.)</summary>
    bool TryRingFromToast(ToastInfo t)
    {
        if (!LooksLikeCall(t.Title + " " + t.Body))
        {
            // "Missed call" from the app that's ringing: it stopped (shown as a message, the ring card goes)
            if (Ringing && ChatAppKey(t.App) == ChatAppKey(_ringWho.App) && NotRinging.Any(w => (t.Title + " " + t.Body).Contains(w, StringComparison.OrdinalIgnoreCase))) StopRinging();
            return false;
        }
        // already ringing, or it's about the call you're in: the card on screen says it all (no second pop-up)
        if (ChatAppKey(t.App) == _ringDeclined.App && (DateTime.UtcNow - _ringDeclined.At).TotalSeconds < 45) return true; // you declined it
        if (Ringing) { if (_ringWho.Who == _ringWho.App && !string.IsNullOrWhiteSpace(t.Title) && !LooksLikeCall(t.Title)) RingNames(t.Title); return true; }
        if (OnCall) return _call is { } c && ChatAppKey(c.Name) == ChatAppKey(t.App);
        string who = !string.IsNullOrWhiteSpace(t.Title) && !LooksLikeCall(t.Title) ? t.Title : t.App;
        StartRinging(RingAppPath(t.App), t.App, who, t.Icon, () => OpenApp(t.Aumid));
        return true;
    }

    /// <summary>The ring was heard before its notification came: now we know who's calling, the card says it.</summary>
    void RingNames(string who)
    {
        _ringWho = (who, _ringWho.App, _ringWho.At);
        if (_mode == Mode.Osd && _osd?.Kind == "ring" && _ringCard is { } make) make();
    }

    (string App, DateTime At) _ringDeclined;
    DateTime _answeredAt;

    /// <summary>
    /// One call, one card: while it rings, its app's other notifications wait (the card stays in front, its buttons
    /// ready); just after you declined, its "call" notices don't ring again; and during the call itself its app's
    /// pop-ups stay quiet (they're still kept: Chats and "what did I miss?" have them).
    /// </summary>
    bool RingHolds(ToastInfo t)
    {
        string app = ChatAppKey(t.App);
        if (Ringing && app == ChatAppKey(_ringWho.App)) return true;
        if (app == _ringDeclined.App && (DateTime.UtcNow - _ringDeclined.At).TotalSeconds < 45) return true;
        if (OnCall && _call is { } c && app == ChatAppKey(c.Name)) return true;
        return false;
    }

    /// <summary>The ringing app's program (for its windows and to hear when it stops), from its name.</summary>
    static string? RingAppPath(string app)
    {
        string key = ChatAppKey(app);
        foreach (var h in RingWindows(null, app))
        {
            GetWindowThreadProcessId(h, out uint pid);
            if (ProcessPathById(pid) is { } p && !IsBrowser(p) && Path.GetFileNameWithoutExtension(p).Contains(key.Split(' ')[0], StringComparison.OrdinalIgnoreCase)) return p;
        }
        return null;
    }

    /// <summary>
    /// The ringing app's windows, smallest first (a call's own pop-up before the main window): by its program when
    /// known, else by name; a browser's only when its title names a calling site (Meet, WhatsApp, Messenger…).
    /// </summary>
    static List<IntPtr> RingWindows(string? path, string app)
    {
        string key = ChatAppKey(app);
        string first = key.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? key;
        string[] sites = { "Meet", "Teams", "Zoom", "Discord", "WhatsApp", "Slack", "Messenger", "Telegram", "Skype", "Jitsi", "Webex", "Signal" };
        var found = new List<(IntPtr H, long Area)>();
        var pathOf = new Dictionary<uint, string?>();
        uint me = (uint)Environment.ProcessId;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == me) return true;
            if (!pathOf.TryGetValue(pid, out var p)) pathOf[pid] = p = ProcessPathById(pid);
            if (p == null) return true;
            string title = WindowTitle(h);
            // its program, or one named after the app (Teams rings through a helper, its windows are ms-teams.exe)
            bool mine = path != null && string.Equals(p, path, StringComparison.OrdinalIgnoreCase)
                || first.Length >= 3 && Path.GetFileNameWithoutExtension(p).Contains(first, StringComparison.OrdinalIgnoreCase);
            if (!mine && IsBrowser(p) && title.Length > 0 && sites.Any(s => title.Contains(s, StringComparison.OrdinalIgnoreCase)
                && (app.Contains(s, StringComparison.OrdinalIgnoreCase) || title.Contains(first, StringComparison.OrdinalIgnoreCase) || s == "Meet" && first is "google" or "meet" or "chrome"))) mine = true;
            if (!mine) return true;
            GetWindowRect(h, out var r);
            long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
            if (area < 80 * 40) return true; // a hidden helper, not a window
            found.Add((h, area));
            return true;
        }, IntPtr.Zero);
        return found.OrderBy(f => f.Area).Select(f => f.H).ToList();
    }

    /// <summary>
    /// From a taskbar flash: a calling app that also plays sound for a moment is ringing.
    /// Returns true when it was taken as a call (so it isn't also shown as "needs your attention").
    /// </summary>
    async Task<bool> TryRingFromFlash(IntPtr hwnd, string? path)
    {
        if (OnCall || Ringing || path == null || !IsCallApp(path) || IsBrowser(path)) return false;
        // a ringtone keeps sounding; a message ping is over in half a second: listen for 2 s (8 samples),
        // and count it only if it's actually audible in at least half of them (an open but silent stream doesn't count)
        int audible = 0;
        for (int i = 0; i < 8; i++)
        {
            if (await AudioService.AppPeakAsync(path) > 0.004f) audible++;
            await Task.Delay(250);
        }
        if (audible < 4) return false;
        if (OnCall || Ringing) return false;
        var (app, icon) = await DescribeAppAsync(path);
        StartRinging(path, app, app, icon, () => FocusWindow(hwnd));
        return true;
    }

    static bool IsBrowser(string path) =>
        Path.GetFileNameWithoutExtension(path).ToLowerInvariant() is "chrome" or "msedge" or "brave" or "firefox" or "opera" or "vivaldi" or "arc";

    static async Task<bool> PlayingSound(string path) => await AudioService.AppPeakAsync(path) > 0.004f;

    Action? _ringCard;   // draws the ring card again (when we learn who's calling)
    bool _ringLoud;      // its ringtone was heard (then a silence means it stopped ringing)

    void StartRinging(string? path, string app, string who, ImageSource? icon, Action onClick)
    {
        _ringPath = path;
        _ringSince = DateTime.UtcNow;
        _ringWho = (who, app, DateTime.UtcNow); // who's calling: the call's chat is theirs (NotchWindow.ChatSend)
        _ringQuiet = 0;
        _ringLoud = false;
        AppLog.Write($"incoming call: {app}");
        _ringCard = () => ShowRingCard(path, app, icon, onClick);
        _ringCard();
        After(320, () => { if (_mode == Mode.Osd && _osd?.Kind == "ring") { CardOpen(); ApplyShape(); } }); // the buttons are there from the start
        _ringTimer.Start();
    }

    void ShowRingCard(string? path, string app, ImageSource? icon, Action onClick)
    {
        string who = _ringWho.Who;
        // like a phone's incoming call: who (their initials, pulsing with each ring), the app, Answer and Decline
        bool person = who != app;
        string? callKey = Services.CallKeys.AppOf(app, app);
        var green = Color.FromRgb(0x30, 0xD1, 0x58); var red = Color.FromRgb(0xFF, 0x45, 0x3A);
        // the buttons only when turned on in Preferences, Calls (otherwise a click brings the app up to answer there)
        var actions = !_settings.CallButtons ? new List<CardAction>() : new List<CardAction>
        {
            new("\uE717", Loc.T("Answer"), () => _ = AnswerCallAsync(app, path, callKey, onClick), Primary: true, Tone: green),
            new("\uE778", Loc.T("Decline"), () => _ = DeclineCallAsync(app, path, callKey), Primary: true, Tone: red),
        };
        var look = new ToastLook(person ? Initials(who) : null, person ? PersonColor(who) : null, person ? icon : null, 0, null, null, null, actions, Controls.NotchFace.Emote.Excited);
        ShowOsd("ring", icon == null ? "\uE717" : null, null, Loc.T("Incoming call"), person ? $"{who} · {app}" : app, null,
            art: icon, glyphBrush: Green, seconds: 45, force: true, prio: Prio.High, motion: Motion.DoublePulse, onClick: () => { StopRinging(); onClick(); }, look: look);
    }

    /// <summary>Every 1.5 s while it rings: a soft pulse; stops when answered, when it goes quiet, or after 45 s.</summary>
    async Task RingTick()
    {
        if (_ringChecking) return;
        if (OnCall || DateTime.UtcNow - _ringSince > TimeSpan.FromSeconds(45)) { StopRinging(); return; }
        OnCallCheck(); // answered in the app itself: seen within a couple of seconds, the card goes
        if (_osd?.Kind != "ring" && _mode != Mode.Osd && _mode != Mode.Expanded && !_hidden && !_osdQueue.Any(q => q.Kind == "ring")) _ringCard?.Invoke(); // still ringing: its card is back
        if (_mode == Mode.Osd && _osd?.Kind == "ring") { Kick(); if (_cardAvatar is { } av && av.Visibility == System.Windows.Visibility.Visible && !Eco) Pop(av, 0.92, 1.1); } // each ring: a beat
        if (_ringPath is not { } path) return; // from a notification: it lasts as long as the alert
        _ringChecking = true;
        try
        {
            // a ringtone has gaps: one sample can land in one. Four, 120 ms apart, and the loudest counts
            bool loud = false;
            for (int i = 0; i < 4 && !loud; i++) { loud = await PlayingSound(path); if (!loud) await Task.Delay(120); }
            // answered, declined or another ring began while listening: what was heard belongs to that ring, not this one
            if (_ringPath != path || !Ringing) return;
            _ringLoud |= loud;
            _ringQuiet = loud ? 0 : _ringQuiet + 1;
        }
        finally { _ringChecking = false; }
        // quiet for ~9 s after it rang (far longer than any gap in a ringtone): declined, or the caller hung up;
        // an app whose ringtone is off never "goes quiet": its card lasts as long as the call rings (up to 45 s)
        if (_ringLoud && _ringQuiet >= 5) StopRinging();
    }

    /// <summary>
    /// Answer, whatever the app:
    ///  1. its own Answer button, pressed through Windows' accessibility (Teams, WhatsApp, Zoom, Skype, Meet…: no window
    ///     needs to come forward);
    ///  2. else its window in front and its shortcut (Discord Ctrl+Enter, Teams Ctrl+Shift+S), or its button now in view;
    ///  3. then a check that the call really began (the app takes the mic); if not, its ringing screen stays in front
    ///     with a word, so one click there does it.
    /// </summary>
    async Task AnswerCallAsync(string app, string? path, string? callKey, Action bringUp)
    {
        _answeredAt = DateTime.UtcNow;
        StopRinging();
        FeelSound(Services.Sfx.Cue.On);
        bool strict = callKey == "discord"; // its main window has other "Accept" buttons (friend requests)
        string? how = null;
        try
        {
            var wins = await Task.Run(() => RingWindows(path, app));
            if (!strict && wins.Count > 0) how = await Limited(() => Services.CallAnswer.Answer(wins, looks: 2), null, 4);
            if (how == null)
            {
                var hwnd = await AppWindowAsync(app, ChatAppKey(app));
                if (hwnd != IntPtr.Zero && Services.CallKeys.For(callKey, Services.CallKeys.Act.Answer) is { } keys && Services.CallKeys.Press(hwnd, keys)) how = "shortcut";
                if (how == null)
                {
                    wins = await Task.Run(() => RingWindows(path, app));
                    how = await Limited(() => Services.CallAnswer.Answer(wins, looks: 3, strict: strict), null, 5);
                }
            }
            AppLog.Write($"answer: {app} by {how ?? "nothing found"}");
            if (how != null && await CallBegins(7000)) return;
            // the shortcut didn't take: the button, now that the window is up
            if (how == "shortcut")
            {
                wins = await Task.Run(() => RingWindows(path, app));
                if (await Limited(() => Services.CallAnswer.Answer(wins, looks: 2, strict: strict), null, 4) is { } b) { AppLog.Write($"answer: {app} by {b} (second try)"); if (await CallBegins(6000)) return; }
            }
        }
        catch (Exception ex) { AppLog.Write("answer: " + ex.Message); }
        if (OnCall) return;
        bringUp();
        After(250, () => ShowOsd("ring-hint", "\uE717", null, string.Format(Loc.T("Answer in {0}"), app), Loc.T("Its call is in front"), null, glyphBrush: Green, seconds: 3, prio: Prio.High, force: true, motion: Motion.None));
    }

    /// <summary>Waits for the call to begin (the app takes the mic), checking quicker than usual. True once it has.</summary>
    async Task<bool> CallBegins(int ms)
    {
        for (int t = 0; t < ms && !OnCall; t += 400)
        {
            OnCallCheck();
            await Task.Delay(400);
        }
        return OnCall;
    }

    /// <summary>Decline: the app's own Decline button, else its shortcut (Teams, Discord); the ringing in Pixo stops either way.</summary>
    async Task DeclineCallAsync(string app, string? path, string? callKey)
    {
        _ringDeclined = (ChatAppKey(app), DateTime.UtcNow); // the app may tell of the same call again: not shown
        StopRinging();
        FeelSound(Services.Sfx.Cue.Off);
        try
        {
            bool strict = callKey == "discord"; // only its shortcut there (its "Decline" buttons are also for friend requests)
            if (!strict)
            {
                var wins = await Task.Run(() => RingWindows(path, app));
                if (wins.Count > 0 && await Limited(() => Services.CallAnswer.Decline(wins, looks: 2), null, 4) is { } b) { AppLog.Write($"decline: {app} by {b}"); return; }
            }
            if (Services.CallKeys.For(callKey, Services.CallKeys.Act.Decline) is { } keys && FindUiWindow(ChatAppKey(app)) is var hwnd && hwnd != IntPtr.Zero)
            {
                var before = GetForegroundWindow();
                if (await Front(hwnd)) { await Task.Delay(250); Services.CallKeys.Press(hwnd, keys); AppLog.Write($"decline: {app} by shortcut"); await Task.Delay(150); if (before != IntPtr.Zero && before != hwnd) FocusWindow(before); }
            }
        }
        catch (Exception ex) { AppLog.Write("decline: " + ex.Message); }
    }

    void StopRinging()
    {
        if (!Ringing) return;
        _ringTimer.Stop();
        _ringPath = null;
        _ringCard = null;
        if (_mode == Mode.Osd && _osd?.Kind == "ring") NextOsd();
    }
}
