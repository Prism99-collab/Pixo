using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using DynamicNotch.Services;
using static DynamicNotch.Interop.Native;

namespace DynamicNotch;

/// <summary>
/// Sending a message into another app from the notch (a reply from the Work inbox, a line into a call's chat).
/// Windows has no way to post into Discord, WhatsApp or Teams for an app like Pixo, so it does what you would: brings
/// the app forward, goes to the conversation, types the message and presses Enter, then says what it did.
///
/// Safety comes first, a message must never land in the wrong conversation:
///  • it only presses Enter when it's sure where it is: Discord and Slack (their quick switcher, then the window's
///    title must name the person or channel), Teams when its title shows that chat, Zoom and Meet (their own chat
///    panel, opened by their own shortcut);
///  • in other apps it opens the conversation when it can (WhatsApp's search) and types the message without sending:
///    you see it in place and press Enter yourself;
///  • when it can't get to the conversation at all, the message is copied and the app opened (Ctrl+V sends);
///  • keys you're holding are let go first, and it stops the moment the window in front isn't the app any more.
/// </summary>
public partial class NotchWindow
{
    enum SendResult { Sent, Typed, Copied, Failed }

    static string ChatAppKey(string app)
    {
        string a = app.ToLowerInvariant();
        foreach (var k in new[] { "discord", "slack", "teams", "whatsapp", "telegram", "signal", "messenger", "zoom", "skype" })
            if (a.Contains(k)) return k;
        return a;
    }

    /// <summary>Does the window's title name this person or place (its first real word, 3+ letters)?</summary>
    static bool TitleNames(string title, string who)
    {
        string t = title.ToLowerInvariant();
        var words = Regex.Split(who.ToLowerInvariant().TrimStart('@', '#', '~'), @"[^\p{L}\p{N}_.-]+").Where(w => w.Length >= 3).ToList();
        if (words.Count == 0) return t.Contains(who.ToLowerInvariant().Trim());
        return t.Contains(words[0]);
    }

    /// <summary>The app's main window, in front: restored if it was minimized, started (or brought back from the tray) if no window was showing.</summary>
    async Task<IntPtr> AppWindowAsync(string app, string key)
    {
        var hwnd = FindUiWindow(key);
        bool waking = false;
        if (hwnd == IntPtr.Zero)
        {
            // closed to the tray, or not running: starting it brings its window back
            try { await ToolDo("open " + app); } catch { }
            for (int i = 0; i < 40 && hwnd == IntPtr.Zero; i++) { await Task.Delay(250); hwnd = FindUiWindow(key); }
            waking = true;
        }
        if (hwnd == IntPtr.Zero) return IntPtr.Zero;
        waking |= IsIconic(hwnd);
        if (!await Front(hwnd)) return IntPtr.Zero;
        // a window coming back from the taskbar or the tray needs a moment before it takes keys (they'd be lost)
        await Task.Delay(waking ? 1100 : 300);
        return hwnd;
    }

    /// <summary>
    /// What the app's window shows, read through Windows' accessibility (the same thing screen readers use): its message
    /// box (where it is, what it's called: Discord calls it "Message @alex"), and whether the conversation's header names
    /// this person. Nothing is clicked here.
    /// </summary>
    async Task<(Point? Box, string BoxName, bool HeaderNames)> ReadChatAsync(IntPtr hwnd, string? who)
    {
        return await Limited(() =>
        {
            GetWindowRect(hwnd, out var wr);
            double wl = wr.Left, wt = wr.Top, ww = Math.Max(1, wr.Right - wr.Left), wh = Math.Max(1, wr.Bottom - wr.Top);
            var els = Elements(hwnd);
            var boxes = els.Where(e => e.Kind is "edit" or "document")
                .Select(e => (e.Name, R: e.El.Cached.BoundingRectangle))
                .Where(e => e.R.Height < wh * 0.4 && e.R.Width > ww * 0.15 && e.R.Top > wt + wh * 0.35) // a box in the lower part, not the whole page
                .ToList();
            var named = boxes.Where(e => Regex.IsMatch(e.Name, @"message|mensaje|écri|ecri|nachricht|type a|send a|write|tape|saisi|envoy|chat|reply|répond", RegexOptions.IgnoreCase)).ToList();
            var pick = (named.Count > 0 ? named : boxes).OrderByDescending(e => e.R.Top).Cast<(string Name, System.Windows.Rect R)?>().FirstOrDefault();
            bool header = false;
            if (who != null)
            {
                // the conversation's name at the top of its pane (not the list of chats on the left)
                header = els.Any(e => e.Kind is not ("edit" or "document" or "list item" or "tree item") && e.El.Cached.BoundingRectangle is var r
                    && r.Top < wt + wh * 0.18 && r.Left > wl + ww * 0.25 && TitleNames(e.Name, who) && e.Name.Length < who.Length + 30);
            }
            return (pick is { } p ? new Point(p.R.Left + p.R.Width / 2, p.R.Top + p.R.Height / 2) : (Point?)null, pick?.Name ?? "", header);
        }, (null, "", false), 6);
    }

    /// <summary>A reply to <paramref name="who"/> in <paramref name="app"/> (see the class summary for when it sends by itself).</summary>
    async Task<SendResult> SendToChatAsync(string app, string who, string text)
    {
        string key = ChatAppKey(app);
        var hwnd = await AppWindowAsync(app, key);
        if (hwnd == IntPtr.Zero) return CopyInstead(app, who, text, "Couldn't open " + app);
        ReleaseHeldKeys();

        // 1. to the conversation, with the app's own search (where it has one that opens a chat)
        bool jumped = false;
        // every key below is pressed only while the app is still in front: you may click elsewhere during the waits,
        // and an Enter in that other window would send what was typed there
        if (key is "discord" or "slack")
        {
            if (!StillFront(hwnd)) return SendResult.Failed;
            PressCombo("esc");          // whatever was open in it (a menu, a picker) closes first
            await Task.Delay(120);
            if (!StillFront(hwnd)) return SendResult.Failed;
            PressCombo("ctrl+k");
            await Task.Delay(350);
            if (!StillFront(hwnd)) return SendResult.Failed;
            TypeText(who.TrimStart('@', '#'));
            await Task.Delay(750);
            if (!StillFront(hwnd)) return SendResult.Failed;
            PressCombo("enter");
            await Task.Delay(700);
            jumped = true;
        }
        else if (key == "whatsapp")
        {
            if (!StillFront(hwnd)) return SendResult.Failed;
            PressCombo("esc");
            await Task.Delay(120);
            if (!StillFront(hwnd)) return SendResult.Failed;
            PressCombo("ctrl+f");
            await Task.Delay(400);
            if (!StillFront(hwnd)) return SendResult.Failed;
            TypeText(who);
            await Task.Delay(1000);
            if (!StillFront(hwnd)) return SendResult.Failed;
            PressCombo("down");
            await Task.Delay(150);
            if (!StillFront(hwnd)) return SendResult.Failed;
            PressCombo("enter");
            await Task.Delay(800);
            jumped = true;
        }
        if (!StillFront(hwnd)) return SendResult.Failed;

        // 2. its message box: found and clicked, so the words land there whatever had the focus
        var (box, boxName, header) = await ReadChatAsync(hwnd, who);
        if (!StillFront(hwnd)) return SendResult.Failed; // reading it can take seconds: the click must land in the app
        if (box is { } at) { Click((int)at.X, (int)at.Y); await Task.Delay(180); }
        else if (!jumped) return CopyInstead(app, who, text, $"Open the chat with {who}");

        // 3. the right conversation? (its title, its box's name, or its header must name them) → sent; else typed for you to check
        // (WhatsApp's own search opens the chat by its exact name: trusted, as its window never says whose chat it shows)
        bool sure = TitleNames(WindowTitle(hwnd), who) || boxName.Length > 0 && TitleNames(boxName, who) || header || key == "whatsapp" && jumped && who.Trim().Length >= 3;
        if (!sure && key is "discord" or "slack")
        {
            PressCombo("esc");
            return CopyInstead(app, who, text, $"Couldn't find {who} in {app}");
        }
        return await TypeAndSend(hwnd, text, send: sure);
    }

    (string Who, string App, DateTime At) _ringWho;
    string? _callPlace;

    /// <summary>
    /// The conversation the call is in, learned once at its start and kept (the window may show other chats later):
    /// the caller from the ringing notification when it rang just before, else the call window's title in its first
    /// seconds ("@alex - Discord" → "@alex", "Call with Sam | Microsoft Teams" → "Sam").
    /// </summary>
    async void LearnCallPlace(CallState call)
    {
        _callPlace = null;
        if ((DateTime.UtcNow - _ringWho.At).TotalSeconds < 90 && ChatAppKey(_ringWho.App) == ChatAppKey(call.Name) && _ringWho.Who.Length > 1
            && !string.Equals(_ringWho.Who, _ringWho.App, StringComparison.OrdinalIgnoreCase))
            _callPlace = _ringWho.Who;
        foreach (int wait in new[] { 250, 1200, 3000 })
        {
            if (_callPlace != null) break;
            await Task.Delay(wait);
            if (!ReferenceEquals(_call, call)) return;
            var hwnd = await Task.Run(() => CallWindow(call, meetOnly: false));
            if (hwnd != IntPtr.Zero && PlaceFromTitle(WindowTitle(hwnd), ChatAppKey(call.Name)) is { } place) _callPlace = place;
        }
        AppLog.Write("call place: " + (_callPlace != null ? "known" : "unknown"));
    }

    /// <summary>The conversation named in a chat app's window title, or null when the title doesn't name one.</summary>
    static string? PlaceFromTitle(string title, string key)
    {
        title = Regex.Replace(title.Trim(), @"^\(\d+\)\s*", ""); // "(2) …" unread counts
        var parts = Regex.Split(title, @"\s+[|\-–—]\s+").Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        if (key == "discord")
            return parts.FirstOrDefault(p => p.StartsWith('@') || p.StartsWith('#')); // a DM or a channel; "Friends", "Discord" aren't places
        string appWords = @"\b(microsoft teams|teams|whatsapp|telegram|slack|skype|signal|messenger|zoom( meeting)?|google meet|meet|viber)\b";
        string callWords = @"\b(voice call|video call|call with|calling|call|appel( vidéo)?( avec)?|llamada( con)?|anruf( mit)?|huddle|meeting|réunion)\b";
        foreach (var p in parts)
        {
            string q = Regex.Replace(Regex.Replace(p, appWords, "", RegexOptions.IgnoreCase), callWords, "", RegexOptions.IgnoreCase).Trim(' ', ':', '·', '-', '|');
            if (q.Length is >= 2 and <= 40 && !Regex.IsMatch(q, @"^\d+[:.]\d+$")) return q; // (not a timer)
        }
        return null;
    }

    /// <summary>
    /// A line into the running call's chat, sent: Zoom and Meet through their chat panel; Discord in the conversation
    /// the call is in (found by name, like a reply); the others in the call app's own message box. It works with the
    /// app minimized or showing something else, and only presses Enter while that window is the one in front.
    /// </summary>
    async Task<SendResult> SendToCallChatAsync(string text)
    {
        if (_call is not { } call) return SendResult.Failed;
        string? app = CallKeys.AppOf(call.Key, call.Name);
        if (app is "zoom" or "meet" && await CallShortcutAsync(CallKeys.Act.Chat, keepFocus: true))
        {
            await Task.Delay(500); // the panel opens with its box ready
            var fg = GetForegroundWindow();
            // the window in front must still be the call's (its chat may be a window of its own, in the same app; for
            // Meet, the Meet tab): else the words and the Enter would go to whatever you switched to meanwhile
            var callWin = await Task.Run(() => CallWindow(call, meetOnly: app == "meet"));
            if (callWin == IntPtr.Zero || !StillFront(callWin) || app == "meet" && !WindowTitle(fg).Contains("Meet", StringComparison.OrdinalIgnoreCase)) return SendResult.Failed;
            var (zb, _, _) = await ReadChatAsync(fg, null);
            if (!StillFront(fg)) return SendResult.Failed;
            if (zb is { } z) { Click((int)z.X, (int)z.Y); await Task.Delay(150); }
            return await TypeAndSend(fg, text, send: true);
        }
        // the call's own conversation, found by name like a reply (the app may be minimized or showing another chat)
        string chatKey = ChatAppKey(call.Name);
        if (_callPlace is { } place && chatKey is "discord" or "whatsapp" or "slack" or "teams")
            return await SendToChatAsync(call.Name, place, text);

        var hwnd = await Task.Run(() => CallWindow(call, meetOnly: false));
        if (hwnd == IntPtr.Zero) return CopyInstead(call.Name, call.Name, text, "Can't find the call's window");
        bool wasDown = IsIconic(hwnd);
        if (!await Front(hwnd)) return CopyInstead(call.Name, call.Name, text, "Can't find the call's window");
        await Task.Delay(wasDown ? 1100 : 300);
        ReleaseHeldKeys();
        if (!StillFront(hwnd)) return SendResult.Failed; // (the wait above: you may have switched windows)
        PressCombo("esc"); // a menu or picker open in it closes, so the words go to its message box
        await Task.Delay(120);
        var (box, _, _) = await ReadChatAsync(hwnd, null);
        if (!StillFront(hwnd)) return SendResult.Failed; // the click must land in the call's window
        if (box is { } at) { Click((int)at.X, (int)at.Y); await Task.Delay(180); }
        return await TypeAndSend(hwnd, text, send: true); // (Enter only if the call's window is still the one in front)
    }

    async Task<SendResult> TypeAndSend(IntPtr hwnd, string text, bool send)
    {
        if (!StillFront(hwnd)) return SendResult.Failed;
        TypeText(text);
        await Task.Delay(120);
        if (!send) return SendResult.Typed;
        if (!StillFront(hwnd)) return SendResult.Typed;
        PressCombo("enter");
        return SendResult.Sent;
    }

    SendResult CopyInstead(string app, string who, string text, string why)
    {
        _ignoreClipboardUntil = DateTime.UtcNow.AddMilliseconds(600);
        try { Clipboard.SetText(text); } catch { return SendResult.Failed; }
        _ = OpenNotifApp(app);
        After(300, () => ShowOsd("copied", "", null, Loc.T(why), Loc.T("Copied · Ctrl+V to send"), null, glyphBrush: Blue, seconds: 3, prio: Prio.High, force: true));
        return SendResult.Copied;
    }

    static void ReleaseHeldKeys()
    {
        foreach (ushort k in new ushort[] { 0x11, 0x10, 0x12, 0x5B, 0x5C })
            if ((GetAsyncKeyState(k) & 0x8000) != 0) Send(InKey(k, true));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);

    /// <summary>What happened, said in the notch (a sent one with a little "sent" tick).</summary>
    void SaySent(SendResult r, string app, string who)
    {
        switch (r)
        {
            case SendResult.Sent:
                FeelSound(Sfx.Cue.Done);
                ShowOsd("sent", "", null, string.Format(Loc.T("Sent to {0}"), who), app, null, glyphBrush: Green, seconds: 2.2, prio: Prio.High, force: true, motion: Motion.None);
                break;
            case SendResult.Typed:
                ShowOsd("sent", "", null, string.Format(Loc.T("Typed in {0}"), app), Loc.T("Check it, then press Enter"), null, glyphBrush: Blue, seconds: 3.2, prio: Prio.High, force: true, motion: Motion.None);
                break;
            case SendResult.Failed:
                ShowOsd("sent", "", null, Loc.T("Didn't send"), Loc.T("The app wasn't in front"), null, glyphBrush: Red, seconds: 2.6, prio: Prio.High, force: true, motion: Motion.Shake);
                break;
        }
    }

    // ───────────── smart replies ─────────────

    readonly Dictionary<string, List<string>> _replyIdeas = new();

    /// <summary>Three short replies that fit the message (in its language and tone), from the AI; cached per message.</summary>
    readonly Dictionary<string, Task<List<string>>> _replyAsking = new();

    Task<List<string>> ReplyIdeasAsync(string id, string app, string who, string messages, CancellationToken stop)
    {
        if (_replyIdeas.TryGetValue(id, out var have)) return Task.FromResult(have);
        if (_replyAsking.TryGetValue(id, out var running)) return running; // the card was redrawn while they were coming
        var t = AskReplyIdeasAsync(id, app, who, messages, stop);
        _replyAsking[id] = t;
        _ = t.ContinueWith(_ => Dispatcher.BeginInvoke(() => _replyAsking.Remove(id)));
        return t;
    }

    async Task<List<string>> AskReplyIdeasAsync(string id, string app, string who, string messages, CancellationToken stop)
    {
        var list = new List<string>();
        try
        {
            PrepareAi();
            if (!OpenRouter.HasKey || OpenRouter.LoadKey() is not { } key) return list;
            var msgs = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = "You suggest quick replies to a chat message. Give exactly 3 different short replies (2 to 9 words each) the user could send back: in the message's language and tone (casual stays casual), natural, like a person texting, varied (e.g. a yes, a not now, a question back). No numbering, no quotes, no emojis unless the message has them. One reply per line, nothing else." },
                new JsonObject { ["role"] = "user", ["content"] = $"On {app}, {who} wrote:\n{messages}" },
            };
            OpenRouter.ChatRequest.Value = false;
            var turn = await OpenRouter.Stream(key, ChatModel, msgs, null, _ => { }, null, stop, 0.7, "low");
            foreach (var line in turn.Text.Split('\n'))
            {
                string t = Regex.Replace(line, @"^\s*(?:[-*•]|\d+[.)])\s*", "").Trim().Trim('"', '“', '”');
                if (t.Length is > 0 and <= 80) list.Add(t);
                if (list.Count == 3) break;
            }
        }
        catch { }
        if (list.Count > 0) _replyIdeas[id] = list;
        return list;
    }
}
