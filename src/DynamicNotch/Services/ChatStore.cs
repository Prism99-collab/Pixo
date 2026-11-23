using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DynamicNotch.Services;

/// <summary>One conversation with the AI: its messages as sent to the model (the system prompt is added at send time).</summary>
public sealed class ChatSession
{
    public string Id { get; set; } = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
    public string Title { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime Updated { get; set; } = DateTime.Now;
    public string Model { get; set; } = "";
    public JsonArray Messages { get; set; } = new();

    public int UserTurns => Messages.Count(m => m?["role"]?.ToString() == "user" && m?["auto"] == null);
}

/// <summary>
/// The chats kept on this PC (when Preferences allow it): one small file each in %APPDATA%\DynamicNotch\chats, the
/// newest 60 kept. Nothing leaves the PC from here.
/// </summary>
public static class ChatStore
{
    public sealed record Info(string Id, string Title, DateTime Updated, int Turns, string Model);

    const int Keep = 60;
    /// <summary>Chats older than this many days are deleted (0: only the newest 60 are kept).</summary>
    public static int KeepDays { get; set; }
    public static string Folder => Path.Combine(Settings.Folder, "chats");

    /// <summary>How many chats are kept and how much room they take.</summary>
    public static (int Count, long Bytes) Usage()
    {
        try
        {
            if (!Directory.Exists(Folder)) return (0, 0);
            var files = new DirectoryInfo(Folder).GetFiles("*.json");
            return (files.Length, files.Sum(f => f.Length));
        }
        catch { return (0, 0); }
    }

    /// <summary>
    /// What a chat keeps on disk: the words, not the bulk. Long results of older steps (a web page it read, a folder
    /// listing) are cut to their start: the answer that used them is kept whole.
    /// </summary>
    static JsonArray Slim(JsonArray messages)
    {
        var copy = messages.DeepClone().AsArray();
        for (int i = 0; i < copy.Count - 6; i++)
            if (copy[i] is JsonObject m && m["role"]?.ToString() == "tool" && m["content"]?.ToString() is { Length: > 1200 } long_)
                m["content"] = long_[..1200] + "\n…(shortened when saved)";
        return copy;
    }
    static string FileOf(string id) => Path.Combine(Folder, id + ".json");

    static readonly object SaveGate = new();

    // saved from background threads: an answer and a rename close together would write the same temp file at once
    // (one failed, and the chat on disk missed its last message)
    public static void Save(ChatSession s)
    {
        lock (SaveGate)
        try
        {
            Directory.CreateDirectory(Folder);
            // what's known in memory before this save (it's moved on below only if nothing else changed meanwhile)
            var before = _summary;
            var stampBefore = FolderStamp();
            bool existed = File.Exists(FileOf(s.Id));
            var messages = Slim(s.Messages);
            int turns = messages.Count(m => m?["role"]?.ToString() == "user" && m?["auto"] == null);
            var o = new JsonObject
            {
                ["id"] = s.Id, ["title"] = s.Title, ["created"] = s.Created.ToString("o"), ["updated"] = s.Updated.ToString("o"),
                ["model"] = s.Model, ["messages"] = messages,
            };
            SafeFile.WriteText(FileOf(s.Id), o.ToJsonString()); // (a write cut short keeps the last good one: id.json.bak)
            int trimmed = TrimFiles();
            // this chat is now the newest: the search shows it without reading the folder again
            if (before != null && before.Stamp == stampBefore && trimmed == 0)
            {
                var after = new Summary(new Info(s.Id, s.Title, s.Updated, turns, s.Model), before.Count + (existed ? 0 : 1), FolderStamp());
                lock (SummaryGate) _summary = ReferenceEquals(_summary, before) ? after : null; // (worked out meanwhile from an older look: worked out again)
            }
            else Forget();
        }
        catch (Exception ex) { AppLog.Write("chats: " + ex.Message); Forget(); }
    }

    // ───────────────────────────── the newest chat and how many there are, in memory ─────────────────────────────
    // The search asks for both each time it opens and whenever its box is emptied: listing the folder and reading the
    // newest chat each time was wasted work. They're worked out once and kept with the folder's own date (it changes
    // when a chat is added or deleted, even by hand in the folder, so a change is never missed); a save puts its
    // chat in front at once, a delete has them worked out again the next time.

    sealed record Summary(Info? Latest, int Count, DateTime Stamp);
    static volatile Summary? _summary;
    static readonly object SummaryGate = new();

    /// <summary>The folder's own date (one look at its entry, nothing listed or read); a missing folder has the same one each time.</summary>
    static DateTime FolderStamp()
    {
        try { return Directory.GetLastWriteTimeUtc(Folder); } catch { return DateTime.MinValue; }
    }

    static void Forget() { lock (SummaryGate) _summary = null; }

    static Summary Current()
    {
        var known = _summary;
        var stamp = FolderStamp(); // (taken before reading: a change while reading has it worked out again next time)
        if (known != null && known.Stamp == stamp) return known;
        var fresh = new Summary(ReadLatest(), CountFiles(), stamp);
        lock (SummaryGate) if (ReferenceEquals(_summary, known)) _summary = fresh;
        return fresh;
    }

    public static ChatSession? Load(string id)
    {
        try
        {
            var o = SafeFile.Load(FileOf(id), t => JsonNode.Parse(t), out _, "chat");
            if (o == null) return null;
            return new ChatSession
            {
                Id = id, Title = o["title"]?.ToString() ?? "", Model = o["model"]?.ToString() ?? "",
                Created = DateTime.TryParse(o["created"]?.ToString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var c) ? c : DateTime.Now,
                Updated = DateTime.TryParse(o["updated"]?.ToString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var u) ? u : DateTime.Now,
                Messages = o["messages"]?.AsArray().DeepClone().AsArray() ?? new JsonArray(),
            };
        }
        catch { return null; }
    }

    /// <summary>Every kept chat, newest first (read from the files' own headers).</summary>
    public static List<Info> List() => Recent(int.MaxValue);

    /// <summary>
    /// The newest few kept chats: only their files are read (newest first by the file's own date), so showing the last
    /// two doesn't parse sixty chats, pictures and all.
    /// </summary>
    public static List<Info> Recent(int count)
    {
        var list = new List<Info>();
        try
        {
            if (!Directory.Exists(Folder)) return list;
            var files = new DirectoryInfo(Folder).EnumerateFiles("*.json").OrderByDescending(fi => fi.LastWriteTimeUtc).Take(count).Select(fi => fi.FullName);
            foreach (var f in files)
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(f));
                    var r = doc.RootElement;
                    int turns = 0;
                    if (r.TryGetProperty("messages", out var ms))
                        foreach (var m in ms.EnumerateArray()) if (m.TryGetProperty("role", out var role) && role.GetString() == "user") turns++;
                    list.Add(new Info(Path.GetFileNameWithoutExtension(f), r.GetProperty("title").GetString() ?? "",
                        DateTime.TryParse(r.GetProperty("updated").GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var u) ? u : File.GetLastWriteTime(f),
                        turns, r.TryGetProperty("model", out var mo) ? mo.GetString() ?? "" : ""));
                }
                catch { }
            }
        }
        catch { }
        return list.OrderByDescending(i => i.Updated).ToList();
    }

    /// <summary>The newest kept chat (kept in memory: the folder is read again only after it changed).</summary>
    public static Info? Latest() => Current().Latest;

    /// <summary>The newest kept chat, read from that one file only.</summary>
    static Info? ReadLatest()
    {
        try
        {
            if (!Directory.Exists(Folder)) return null;
            var newest = new DirectoryInfo(Folder).EnumerateFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (newest == null) return null;
            var s = Load(Path.GetFileNameWithoutExtension(newest.Name));
            return s == null ? null : new Info(s.Id, s.Title, s.Updated, s.UserTurns, s.Model);
        }
        catch { return null; }
    }

    /// <summary>Chats whose name or messages contain the words, newest first, with the bit that matched.</summary>
    public static List<(Info Info, string Snippet)> Search(string text)
    {
        var found = new List<(Info, string)>();
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var info in List())
        {
            if (words.All(w => info.Title.Contains(w, StringComparison.OrdinalIgnoreCase))) { found.Add((info, "")); continue; }
            var s = Load(info.Id);
            if (s == null) continue;
            var texts = s.Messages.Select(m => m?["shown"]?.ToString() ?? (m?["role"]?.ToString() is "user" or "assistant" ? m?["content"]?.ToString() : null)).Where(t => t != null).Cast<string>().ToList();
            string all = string.Join("\n", texts);
            if (!words.All(w => all.Contains(w, StringComparison.OrdinalIgnoreCase))) continue;
            int at = all.IndexOf(words[0], StringComparison.OrdinalIgnoreCase);
            int from = Math.Max(0, at - 24);
            string snip = all.Substring(from, Math.Min(80, all.Length - from)).Replace('\n', ' ').Trim();
            found.Add((info, snip));
        }
        return found;
    }

    /// <summary>How many chats are kept (kept in memory, like the newest one).</summary>
    public static int Count => Current().Count;

    static int CountFiles()
    {
        try { return Directory.Exists(Folder) ? Directory.EnumerateFiles(Folder, "*.json").Count() : 0; } catch { return 0; }
    }

    public static void Delete(string id) { try { File.Delete(FileOf(id)); File.Delete(FileOf(id) + ".bak"); } catch { } Forget(); } // (its backup goes too)

    public static void DeleteAll()
    {
        try { if (Directory.Exists(Folder)) foreach (var f in Directory.EnumerateFiles(Folder)) File.Delete(f); } catch { }
        Forget();
    }

    /// <summary>The newest 60 stay (and none older than KeepDays): judged by the files' dates, nothing is read.</summary>
    public static void Trim() { if (TrimFiles() > 0) Forget(); }

    /// <summary>Trim, telling how many chats went.</summary>
    static int TrimFiles()
    {
        int gone = 0;
        try
        {
            if (!Directory.Exists(Folder)) return 0;
            var files = new DirectoryInfo(Folder).GetFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            var cutoff = KeepDays > 0 ? DateTime.UtcNow.AddDays(-KeepDays) : DateTime.MinValue;
            for (int i = 0; i < files.Count; i++)
                if (i >= Keep || files[i].LastWriteTimeUtc < cutoff) { try { files[i].Delete(); File.Delete(files[i].FullName + ".bak"); gone++; } catch { } }
        }
        catch { }
        return gone;
    }
}
