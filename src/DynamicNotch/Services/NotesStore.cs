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
    static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static List<Note> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            return JsonSerializer.Deserialize<List<Note>>(File.ReadAllText(FilePath), Json) ?? new();
        }
        catch (Exception ex)
        {
            AppLog.Write("notes: " + ex.Message);
            // a file we can't read is kept aside (never overwritten with an empty list)
            try { File.Copy(FilePath, FilePath + ".unreadable", true); } catch { }
            return new();
        }
    }

    /// <summary>Saves a copy of the notes in the background.</summary>
    public static void Save(IEnumerable<Note> notes)
    {
        var copy = notes.Select(n => n.Copy()).ToList();
        Task.Run(() =>
        {
            lock (Gate)
                try
                {
                    Directory.CreateDirectory(Settings.Folder);
                    string tmp = FilePath + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(copy, Json));
                    File.Move(tmp, FilePath, true);
                }
                catch (Exception ex) { AppLog.Write("notes: " + ex.Message); }
        });
    }
}
