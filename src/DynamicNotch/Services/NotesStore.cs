using System.IO;
using System.Text.Json;

namespace DynamicNotch.Services;

/// <summary>One note: a title, its text, pinned or not, and when it was written and last changed.</summary>
public sealed class Note
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public bool Pinned { get; set; }
    /// <summary>"work", "personal", "ideas" or "" (none): a small coloured dot.</summary>
    public string Tag { get; set; } = "";
    /// <summary>The AI already named it once (it doesn't again, even if you clear the title).</summary>
    public bool AutoTitled { get; set; }
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime Updated { get; set; } = DateTime.Now;

    public Note Copy() => (Note)MemberwiseClone();
}

/// <summary>
/// The notes (Search page → Notes), kept on this PC only, in notes.json next to the settings. Written whole through a
/// temporary file (a write cut short never loses what was there), one write at a time, never on the window's thread.
/// </summary>
public static class NotesStore
{
    static readonly object Gate = new();
    static string FilePath => Path.Combine(Settings.Folder, "notes.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true }; // (readable, if you ever open it)

    /// <summary>The file was there but couldn't be read: nothing is written over it (your notes are never replaced by an empty list).</summary>
    public static bool Unreadable { get; private set; }

    public static List<Note> Load()
    {
        // missing: no notes yet; there but broken: its backup (notes.json.bak); nothing readable: kept as it is
        var list = SafeFile.Load(FilePath, t => JsonSerializer.Deserialize<List<Note>>(t, Json), out bool bad, "notes");
        Unreadable = bad;
        return list ?? new();
    }

    static DateTime _backedUp;

    /// <summary>
    /// Writes the notes safely (Services/SafeFile: a temporary file pushed to the disk, the last version kept as
    /// notes.json.bak). Once a day, and before a write that would leave no notes at all, a copy is also kept as
    /// notes.json.daily (so a mistake found tomorrow can still be undone).
    /// </summary>
    static void Write(List<Note> copy, long stamp)
    {
        if (Unreadable) { AppLog.Write("notes: not saved (the file couldn't be read; it's kept as it is)"); return; }
        try
        {
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 2 && (copy.Count == 0 || _backedUp.Date != DateTime.Today))
            {
                File.Copy(FilePath, FilePath + ".daily", true);
                _backedUp = DateTime.Now;
            }
        }
        catch { }
        SafeFile.WriteText(FilePath, JsonSerializer.Serialize(copy, Json), stamp);
    }

    /// <summary>Saves the notes now, on this thread (Pixo closing, Windows shutting down: no time for the background).</summary>
    public static void SaveNow(IEnumerable<Note> notes)
    {
        var copy = notes.Select(n => n.Copy()).ToList();
        long stamp = SafeFile.Stamp();
        lock (Gate)
            try { Write(copy, stamp); }
            catch (Exception ex) { AppLog.Write("notes: " + ex.Message); }
    }

    /// <summary>Saves a copy of the notes in the background (an older copy never lands after a newer one).</summary>
    public static void Save(IEnumerable<Note> notes)
    {
        var copy = notes.Select(n => n.Copy()).ToList();
        long stamp = SafeFile.Stamp();
        Task.Run(() =>
        {
            lock (Gate)
                try { Write(copy, stamp); }
                catch (Exception ex) { AppLog.Write("notes: " + ex.Message); }
        });
    }
}
