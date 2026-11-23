using System.Text.RegularExpressions;
using System.Windows.Media;

namespace DynamicNotch;

/// <summary>
/// While you type on the empty chat, the AI shows what it understood before you send: a PC command it will do at once
/// ("Volume to 30%  ·  instant"), a note, a song, a video; or, for the AI, the kind of help it's about to give (it checks
/// your PC, looks in your files, searches the web, writes it). The same checks the chat runs when you send, so what it
/// says is what happens. Read on the PC, nothing sent anywhere.
/// </summary>
public partial class NotchWindow
{
    /// <summary>What it would do with these words; null for nothing typed.</summary>
    (string Glyph, Color Tone, string Words, bool Instant)? Foresee(string text)
    {
        string t = text.Trim();
        if (t.Length == 0) return null;
        try
        {
            if (NoteRequest(t) != null) return ("", StepTone("notch_data"), "Saves it in your notes", true);
            if (_settings.AiTools && t.Length <= 120 && !t.Contains('?') && WatchRequest(t) != null) return ("", StepTone("youtube"), "Finds it on YouTube", true);
            if (_settings.AiTools && t.Length <= 90 && !t.Contains('?') && PlayRequest(t) is { } play)
                return ("", StepTone("play_music"), play.Where switch { "youtube" => "Plays it on YouTube", "spotify" or "liked" => "Plays it on Spotify", _ => "Plays it" }, true);
            if (InstantPlan(t) is { Count: > 0 } plan)
                return (plan[0].Glyph, Ok, string.Join("  ·  ", plan.Select(v => v.Title)), true);
        }
        catch { } // a half-typed command it can't read yet: the AI's guess below

        string s = t.ToLowerInvariant();
        if (_settings.AiTools)
        {
            if (Regex.IsMatch(s, @"\b(pc|cpu|gpu|ram|memory|slow|lag\w*|using|disk|storage|battery|temp\w*|fps|ordi\w*|batterie)\b"))
                return ("", StepTone("pc_status"), "Checks your PC", false);
            if (Regex.IsMatch(s, @"\b(files?|pdfs?|downloads?|folders?|documents?|fichiers?|dossiers?)\b"))
                return ("", StepTone("find_files"), "Looks in your files", false);
            if (_settings.AiWeb && Regex.IsMatch(s, @"\b(news|weather|price|prices|score|latest|search|google|who won|m[ée]t[ée]o|actu\w*)\b"))
                return ("", StepTone("news"), "Searches the web", false);
        }
        if (Regex.IsMatch(s, @"\b(write|draft|e-?mail|reply|letter|[ée]cri[st]|r[ée]dige)\b")) return ("", StepTone("write_file"), "Writes it for you", false);
        if (Regex.IsMatch(s, @"\b(translate|tradui\w*)\b")) return ("", StepTone("news"), "Translates it", false);
        return ("", ChatAccent, "Thinks it through", false);
    }
}
