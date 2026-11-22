using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace DynamicNotch.Services;

/// <summary>
/// How every file of yours is saved and read, so nothing is ever lost:
/// - a save goes to a temporary file first, is pushed all the way to the disk, then takes the old file's place in one
///   step (the old one kept as name.bak): a crash, a power cut or a full disk in the middle leaves the last good file;
/// - saves of the same file wait for each other, and an older snapshot never lands after a newer one;
/// - a file that's there but can't be read is never treated as "empty": it's tried again (another app may hold it for a
///   moment), then its backup is used, and the broken one is set aside (name.unreadable-date) instead of being saved over.
/// </summary>
public static class SafeFile
{
    static readonly ConcurrentDictionary<string, object> Locks = new(StringComparer.OrdinalIgnoreCase);
    static readonly ConcurrentDictionary<string, long> Written = new(StringComparer.OrdinalIgnoreCase);
    static long _stamp;

    static object LockFor(string path) => Locks.GetOrAdd(Path.GetFullPath(path), _ => new object());

    /// <summary>A number that only grows: take it when you copy what's to be saved, pass it to the save.</summary>
    public static long Stamp() => Interlocked.Increment(ref _stamp);

    public static void WriteText(string path, string text, long stamp = 0, bool backup = true) => WriteBytes(path, new UTF8Encoding(false).GetBytes(text), stamp, backup);

    /// <summary>Something was forgotten or cleared: its old version beside it goes too (nothing deleted stays behind).</summary>
    public static void DeleteBackup(string path) { try { File.Delete(Path.GetFullPath(path) + ".bak"); } catch { } }

    static readonly ConcurrentDictionary<string, byte> SetAside = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Files that couldn't be read this session (kept as they are, not saved over): the notch tells you once.</summary>
    public static readonly ConcurrentQueue<string> Troubles = new();

    /// <summary>
    /// Saves the whole file safely (see the class). With a stamp, a save older than the last one written is skipped.
    /// Without a backup (keys, private links): the old version is never kept beside it, so forgetting it really forgets it.
    /// </summary>
    public static void WriteBytes(string path, byte[] data, long stamp = 0, bool backup = true)
    {
        lock (LockFor(path))
        {
            string full = Path.GetFullPath(path);
            if (stamp > 0)
            {
                if (Written.TryGetValue(full, out var last) && stamp < last) return; // a newer copy is already on the disk
                Written[full] = stamp;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            string tmp = full + ".tmp";
            try
            {
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    fs.Write(data, 0, data.Length);
                    fs.Flush(true); // all the way to the disk before it replaces anything
                }
                if (backup && File.Exists(full) && new FileInfo(full).Length > 0)
                {
                    try { File.Replace(tmp, full, full + ".bak", true); return; }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    try { File.Copy(full, full + ".bak", true); } catch { }
                }
                File.Move(tmp, full, true);
            }
            catch
            {
                try { File.Delete(tmp); } catch { }
                throw;
            }
        }
    }

    /// <summary>
    /// Reads and understands a file. Missing: default, and fine to save. There but unreadable: it's tried again, then the
    /// backup; if nothing works, <paramref name="unreadable"/> is true and the caller must not save over it.
    /// </summary>
    public static T? Load<T>(string path, Func<string, T?> parse, out bool unreadable, string what = "")
    {
        unreadable = false;
        string full = Path.GetFullPath(path);
        if (!File.Exists(full)) return default;
        try { if (new FileInfo(full).Length == 0 && !File.Exists(full + ".bak")) return default; } catch { } // (empty, nothing to lose: as if missing)
        string? text = null;
        for (int attempt = 0; attempt < 4 && text == null; attempt++)
        {
            try { text = File.ReadAllText(full); }
            catch (IOException) { Thread.Sleep(150); } // another app (antivirus, a sync tool) has it this moment
            catch (UnauthorizedAccessException) { Thread.Sleep(150); }
        }
        if (text == null)
        {
            AppLog.Write($"{Name(full, what)}: couldn't be opened (kept as it is, not saved over)");
            Troubles.Enqueue(Name(full, what));
            unreadable = true;
            return default;
        }
        if (string.IsNullOrWhiteSpace(text) && !File.Exists(full + ".bak")) return default; // (blank, nothing to lose)
        if (TryParse(text, parse, out var value)) return value;

        // broken (cut short by a crash, or edited by hand): set aside (once), then the backup
        if (SetAside.TryAdd(full, 0))
            try { File.Copy(full, full + ".unreadable-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true); } catch { }
        try
        {
            if (File.Exists(full + ".bak") && TryParse(File.ReadAllText(full + ".bak"), parse, out var saved))
            {
                AppLog.Write($"{Name(full, what)}: couldn't be read, restored from its backup (the broken one is kept beside it)");
                return saved;
            }
        }
        catch { }
        AppLog.Write($"{Name(full, what)}: couldn't be read and has no good backup (kept as it is, not saved over)");
        Troubles.Enqueue(Name(full, what));
        unreadable = true;
        return default;
    }

    static bool TryParse<T>(string text, Func<string, T?> parse, out T? value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        try { value = parse(text); return value != null; }
        catch { return false; }
    }

    static string Name(string full, string what) => what.Length > 0 ? what : Path.GetFileName(full);
}
