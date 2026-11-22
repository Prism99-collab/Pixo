using System.IO;
using System.Text.Json;

namespace DynamicNotch.Services;

/// <summary>
/// What the AI remembers about you: short facts it saves when you tell it something lasting ("I work in Casablanca",
/// "answer in French", "my project folder is D:\Work"). Kept on this PC only, shown and deletable in Preferences,
/// handed to the AI with each chat. At most 60, each one short.
/// </summary>
public static class AiMemory
{
    const int Max = 150, MaxLength = 220, NoteLength = 1200;

    /// <summary>
    /// Three kinds, told apart by how they start: "Rule: …" (how you want it to act from now on: always followed),
    /// "Note (18 Nov): …" (something to keep and give back later: a code, a list, an idea, a link), or a plain fact.
    /// </summary>
    public static bool IsRule(string m) => m.StartsWith("Rule: ");
    public static bool IsNote(string m) => m.StartsWith("Note (");
    public static List<string> Rules() => All().Where(IsRule).Select(m => m[6..]).ToList();
    public static List<string> Notes() => All().Where(IsNote).ToList();
    public static List<string> Facts() => All().Where(m => !IsRule(m) && !IsNote(m)).ToList();

    /// <summary>Saves a rule, a note or a fact. Returns what was kept.</summary>
    public static string Remember(string text, string kind)
    {
        text = text.Trim();
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"\b(password|passcode|mot de passe|pin code|code pin|cvv|card number|num[ée]ro de carte|seed phrase|private key)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return "Not kept: passwords, card numbers and private keys don't go in memory (it's plain text on this PC). A password manager is the safe place.";
        return kind switch
        {
            "rule" => Remember("Rule: " + text.Replace('\n', ' ')),
            "note" => Keep($"Note ({DateTime.Now:d MMM}): " + (text.Length > NoteLength ? text[..NoteLength] + "…" : text)),
            _ => Remember(text),
        };
    }

    /// <summary>A note is kept as it is (its own lines), never merged with another.</summary>
    static string Keep(string note)
    {
        lock (Gate)
        {
            var list = Load();
            list.RemoveAll(f => f.Equals(note, StringComparison.OrdinalIgnoreCase));
            list.Add(note);
            while (list.Count > Max) list.RemoveAt(list.FindIndex(m => !IsRule(m)) is int i and >= 0 ? i : 0); // rules stay longest
            Save(list);
        }
        return "Remembered: " + note;
    }
    static readonly object Gate = new();
    static List<string>? _facts;
    static string FilePath => Path.Combine(Settings.Folder, "ai-memory.json");

    public static List<string> All()
    {
        lock (Gate) return new List<string>(Load());
    }

    /// <summary>Saves a fact (a near-duplicate replaces the old one). Returns what was kept.</summary>
    public static string Remember(string fact)
    {
        fact = fact.Trim().Replace('\n', ' ');
        if (fact.Length > MaxLength) fact = fact[..MaxLength];
        if (fact.Length < 3) return "Nothing to remember.";
        lock (Gate)
        {
            var list = Load();
            list.RemoveAll(f => f.Equals(fact, StringComparison.OrdinalIgnoreCase) || IsRule(f) == IsRule(fact) && !IsNote(f) && Similar(f, fact));
            list.Add(fact);
            while (list.Count > Max) list.RemoveAt(0);
            Save(list);
        }
        return "Remembered: " + fact;
    }

    /// <summary>Forgets every fact containing the words.</summary>
    public static string Forget(string about)
    {
        about = about.Trim();
        if (about.Length == 0) return "Nothing to forget.";
        lock (Gate)
        {
            var list = Load();
            int n = list.RemoveAll(f => f.Contains(about, StringComparison.OrdinalIgnoreCase));
            Save(list, forgetting: n > 0);
            return n == 0 ? "Nothing matched “" + about + "”." : $"Forgot {n} fact{(n == 1 ? "" : "s")}.";
        }
    }

    public static void Remove(string fact) { lock (Gate) { var list = Load(); list.Remove(fact); Save(list, forgetting: true); } }

    public static void Clear() { lock (Gate) Save(new List<string>(), forgetting: true); }

    // two facts about the same thing ("my editor is VS Code" / "my editor is Rider"): the first words match
    static bool Similar(string a, string b)
    {
        var wa = a.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var wb = b.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int n = Math.Min(4, Math.Min(wa.Length, wb.Length));
        return n >= 3 && wa.Take(n - 1).SequenceEqual(wb.Take(n - 1));
    }

    static bool _unreadable;

    static List<string> Load()
    {
        if (_facts != null) return _facts;
        _facts = SafeFile.Load(FilePath, t => JsonSerializer.Deserialize<List<string>>(t), out _unreadable, "ai memory") ?? new();
        return _facts;
    }

    /// <param name="forgetting">something was forgotten: no old copy of it is kept beside the file</param>
    static void Save(List<string> list, bool forgetting = false)
    {
        _facts = list;
        if (_unreadable) { AppLog.Write("ai memory: not saved (the file couldn't be read; it's kept as it is)"); return; }
        try
        {
            SafeFile.WriteText(FilePath, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }), backup: !forgetting);
            if (forgetting) SafeFile.DeleteBackup(FilePath);
        }
        catch (Exception ex) { AppLog.Write("ai memory: " + ex.Message); }
    }
}
