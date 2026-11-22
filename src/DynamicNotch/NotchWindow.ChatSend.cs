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

    /// <summary>A reply to <paramref name="who"/> in <paramref name="app"/> (see the class summary for when it sends by itself).</summary>
    async Task<SendResult> SendToChatAsync(string app, string who, string text)
    {
        string key = ChatAppKey(app);
        var hwnd = FindUiWindow(key);
        if (hwnd == IntPtr.Zero)
        {
            // not open: start it, give it a moment to show its window
            try { await ToolDo("open " + app); } catch { }
            for (int i = 0; i < 30 && hwnd == IntPtr.Zero; i++) { await Task.Delay(250); hwnd = FindUiWindow(key); }
        }
        if (hwnd == IntPtr.Zero || !await Front(hwnd)) return CopyInstead(app, who, text, "Couldn't open " + app);
        await Task.Delay(150);
        ReleaseHeldKeys();

        switch (key)
        {
            case "discord" or "slack":
            {
                // their quick switcher: the name, Enter, and the conversation is open with its message box ready
                PressCombo("ctrl+k");
                await Task.Delay(300);
                if (!StillFront(hwnd)) return SendResult.Failed;
                TypeText(who.TrimStart('@', '#'));
                await Task.Delay(700);
                PressCombo("enter");
                await Task.Delay(650);
                if (!StillFront(hwnd)) return SendResult.Failed;
                if (!TitleNames(WindowTitle(GetForegroundWindow()), who))
                {
                    PressCombo("esc");
                    return CopyInstead(app, who, text, $"Couldn't find {who} in {app}");
                }
                return await TypeAndSend(hwnd, text, send: true);
            }
            case "teams":
                // Teams can't be steered safely: only when the chat with them is the one open
                if (TitleNames(WindowTitle(GetForegroundWindow()), who)) return await TypeAndSend(hwnd, text, send: true);
                return CopyInstead(app, who, text, $"Open the chat with {who}");
            case "whatsapp":
            {
                // its chat search, the first match opened; the message is typed, you press Enter (it can't be checked)
                PressCombo("ctrl+f");
                await Task.Delay(350);
                if (!StillFront(hwnd)) return SendResult.Failed;
                TypeText(who);
                await Task.Delay(900);
                PressCombo("down");
                await Task.Delay(120);
                PressCombo("enter");
                await Task.Delay(700);
                return await TypeAndSend(hwnd, text, send: false);
            }
            default:
                return CopyInstead(app, who, text, $"Open the chat with {who}");
        }
    }

    /// <summary>
    /// A line into the running call's chat, sent (Enter): Zoom and Meet through their chat panel, the others in the call
    /// app's own window (Discord: the chat it shows). It only types and sends while that window is the one in front.
    /// </summary>
    async Task<SendResult> SendToCallChatAsync(string text)
    {
        if (_call is not { } call) return SendResult.Failed;
        string? app = CallKeys.AppOf(call.Key, call.Name);
        if (app is "zoom" or "meet" && await CallShortcutAsync(CallKeys.Act.Chat, keepFocus: true))
        {
            await Task.Delay(450); // the panel opens with its box ready
            return await TypeAndSend(GetForegroundWindow(), text, send: true);
        }
        var hwnd = await Task.Run(() => CallWindow(call, meetOnly: false));
        if (hwnd == IntPtr.Zero || !await Front(hwnd)) return CopyInstead(call.Name, call.Name, text, "Can't find the call's window");
        await Task.Delay(200);
        ReleaseHeldKeys();
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
