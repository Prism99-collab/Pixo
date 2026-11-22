using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DynamicNotch.Services;
using Do = DynamicNotch.Services.LauncherIntent.Do;

namespace DynamicNotch;

/// <summary>
/// "Add me a note to buy milk", "take a note: call the bank", "ajoute une note …": a note on your Notes page (the one
/// you see and edit in the notch), not something the AI keeps to itself. With a time in it ("… after 10 min", "… at 6")
/// it's both: the note, and a reminder at that time. Done at once, without asking the AI, and undoable.
/// </summary>
public partial class NotchWindow
{
    static readonly Regex NoteWords = new(
        @"^(?:please\s+|pls\s+|hey\s+|ok\s+)?(?:can you\s+|could you\s+|i want you to\s+)?" +
        @"(?:(?:add|make|create|take|put|save|start|jot|leave)(?:\s+(?:me|down))?\s+(?:a\s+|an\s+|one\s+|new\s+|quick\s+|little\s+)*note" +
        @"|note\s+down|new\s+note" +
        @"|(?:ajoute|ajouter|cr[ée]e|cr[ée]er|fais|faire|prends|prendre|mets|mettre)(?:[- ]moi)?\s+une\s+(?:petite\s+|nouvelle\s+)?note" +
        @"|(?:a[ñn]ade|crea|haz)(?:me)?\s+una\s+nota)\b" +
        @"(?:\s+(?:in|to|on|dans|en)\s+(?:my\s+|mes\s+|the\s+)?notes?)?[\s,:]*" +
        @"(?:to\s+|that\s+|about\s+|saying\s+|of\s+|for\s+|pour\s+|de\s+|que\s+|qu'|para\s+)?(?<what>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>A request for a note on the Notes page, as the step to take; null when it isn't one.</summary>
    (string Tool, string Args, Func<Task<string>> Act)? NoteRequest(string text)
    {
        string t = text.Trim();
        if (t.Length > 2000) return null;
        var m = NoteWords.Match(t);
        if (!m.Success) return null;
        // a note FOR someone ("a note to my landlord", "for the teacher") is a message to write, and a note in another
        // app (Notion, OneNote, Keep…) belongs there: both go to the AI, not to Pixo's notes
        if (Regex.IsMatch(t, @"\bnotes?\s+(?:to|for|pour|à|a|para)\s+(?!me\b|myself\b|self\b|moi\b|mi\b|remember\b|buy\b|call\b|do\b|make\b|get\b|check\b|pay\b|send\b)(?:my|the|your|his|her|our|mon|ma|mes|le|la|les|mi|el|(?-i:\p{Lu}))", RegexOptions.IgnoreCase) && !Regex.IsMatch(t, @"\bnotes?\s+(?:to|for)\s+(?:my)?self\b", RegexOptions.IgnoreCase)) return null;
        if (Regex.IsMatch(t, @"\b(notion|onenote|one note|google keep|keep|obsidian|evernote|apple notes|sticky notes|word|docs)\b", RegexOptions.IgnoreCase)) return null;
        string what = m.Groups["what"].Value.Trim().TrimEnd('.', '!', ' ');
        if (what.Length == 0) return null; // "make a note" alone: the AI asks what it should say
        // a time in it: the words without it go in the note, and a reminder is set for that time
        DateTime? at = null;
        string body = what;
        try
        {
            if (LauncherIntent.Parse("remind me to " + what, DateTime.Now) is { Count: 1 } r && r[0] is { Kind: Do.Remind, At: { } when } && r[0].Text is { Length: > 0 } words)
            { at = when; body = words; }
        }
        catch { }
        body = char.ToUpperInvariant(body[0]) + body[1..];
        var args = new JsonObject { ["text"] = body };
        if (at is { } a) args["remind_at"] = a.ToString("t");
        string whole = what;
        return ("add_note", args.ToJsonString(), () => AddNoteFromChat(body, at != null ? whole : null));
    }

    /// <summary>The note, saved on the Notes page (and the reminder, when the words had a time). Undoable.</summary>
    async Task<string> AddNoteFromChat(string body, string? remindWords)
    {
        var n = new Note { Body = body };
        Notes.Add(n);
        if (!NotesDemo) NotesStore.Save(Notes);
        RenderNotesIfShown();
        string said = "Done: saved to your Notes";
        UndoStep? reminderUndo = null;
        if (remindWords != null)
        {
            _pendingUndo = null;
            string r = await ToolDo("remind me to " + remindWords);
            reminderUndo = _pendingUndo; // the reminder's own undo: it goes with the note's
            said += r.StartsWith("Error") ? " (the reminder couldn't be set: " + r + ")" : ", and " + Lower(r);
        }
        Undoable("the note", () =>
        {
            try { reminderUndo?.Undo(); } catch { }
            Notes.RemoveAll(x => x.Id == n.Id);
            if (!NotesDemo) NotesStore.Save(Notes);
            RenderNotesIfShown();
            return "The note is gone again";
        });
        return said;
        static string Lower(string s) => s.Length > 0 ? char.ToLowerInvariant(s[0]) + s[1..] : s;
    }

    /// <summary>The Notes list, drawn again if it's on screen (the new note shows at once).</summary>
    void RenderNotesIfShown()
    {
        if (_mode == Mode.Expanded && _page == SearchPage) SearchFill();
    }
}
