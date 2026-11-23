using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DynamicNotch.Services;

/// <summary>
/// Every notification the notch saw, kept for two weeks (notifications-history.jsonl, on this PC only): to look back,
/// search, and to know who's still waiting on an answer. A message from a person that asks something (a question, a
/// "can you…") is "waiting on you" until you mark it done (or it's two days old).
/// </summary>
public static class NotifHistory
{
    public sealed record Entry(DateTime At, string App, string Title, string Body)
    {
        // a hash that stays the same from one run to the next: string.GetHashCode changes every time the app starts,
        // so "done" and "snoozed" (saved by this id) were forgotten at each restart and the person showed up again
        public string Id => StableHash(At.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture) + "|" + App + "|" + Title).ToString("x8");

        static uint StableHash(string s)
        {
            uint h = 2166136261; // FNV-1a
            foreach (char c in s) { h ^= c; h *= 16777619; }
            return h;
        }
    }

    static readonly object Gate = new();
    static List<Entry>? _all;
    static HashSet<string> _done = new();
    static Dictionary<string, DateTime> _snoozed = new();
    static string? _folder;

    static string Folder => _folder ?? Settings.Folder;
    static string FilePath => Path.Combine(Folder, "notifications-history.jsonl");
    static string StateFile => Path.Combine(Folder, "notifications-state.json");

    public static void UseFolder(string folder)
    {
        Flush(); // what's still on its way lands in the old folder first (read again, it's all there)
        lock (Gate) { _folder = folder; _all = null; _done = new(); _snoozed = new(); _muted = new(StringComparer.OrdinalIgnoreCase); _mutedNow = null; _imported = false; _unreadable = false; }
    }

    sealed class State
    {
        public HashSet<string> Done { get; set; } = new();
        public Dictionary<string, DateTime> Snoozed { get; set; } = new();
        public HashSet<string> Muted { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public bool Imported { get; set; }
    }

    static bool _imported;

    /// <summary>
    /// The first time only: today's notifications the notch already had (from before the history existed) are put in it,
    /// so it doesn't start empty. Never again after (a history you clear stays clear).
    /// </summary>
    public static void ImportOnce(IEnumerable<(DateTime At, string App, string Title, string Body)> earlier)
    {
        lock (Gate)
        {
            var all = All();
            if (_imported) return;
            _imported = true;
            var have = all.Select(e => (e.At, e.Title)).ToHashSet();
            var add = earlier.Where(t => !have.Contains((t.At, t.Title)) && !_muted.Contains(t.App)).Select(t => new Entry(t.At, t.App, t.Title, t.Body)).ToList();
            if (add.Count > 0)
            {
                all.AddRange(add);
                all.Sort((a, b) => a.At.CompareTo(b.At));
                if (CanRewrite) WriteLater(all);
            }
            SaveState();
        }
    }

    static HashSet<string> _muted = new(StringComparer.OrdinalIgnoreCase);
    // a copy of the muted apps for IsMuted, asked on the UI thread with every notification: read without the lock,
    // so it never waits behind the disk. Null until the history is read (then IsMuted reads it, as before).
    static volatile HashSet<string>? _mutedNow;
    static void MutedChanged() => _mutedNow = new HashSet<string>(_muted, StringComparer.OrdinalIgnoreCase);

    /// <summary>The name the notch's own alerts are kept under (a download, a reminder, low battery…).</summary>
    public const string PixoApp = "Pixo";

    /// <summary>One of the notch's own alerts, not a notification from an app.</summary>
    public static bool IsAlert(Entry e) => e.App == PixoApp;

    /// <summary>Is this app muted here (its notifications neither kept nor shown)?</summary>
    public static bool IsMuted(string app)
    {
        if (_mutedNow is { } now) return now.Contains(app.Trim());
        lock (Gate) { All(); return _muted.Contains(app.Trim()); }
    }

    /// <summary>The apps seen in the last two weeks (the notch's own alerts left out), the busiest first, with when the last came.</summary>
    public static List<(string App, int Count, DateTime Last)> Apps()
    {
        lock (Gate)
            return All().Where(e => e.App != PixoApp && e.App.Length > 0).GroupBy(e => e.App, StringComparer.OrdinalIgnoreCase)
                .Select(g => (g.Key, g.Count(), g.Max(e => e.At))).OrderByDescending(a => a.Item2).ToList();
    }

    /// <summary>Apps whose notifications aren't kept (you muted them here).</summary>
    public static IReadOnlyCollection<string> Muted() { lock (Gate) { All(); return _muted.ToList(); } }

    public static void Mute(string app, bool on)
    {
        lock (Gate)
        {
            All();
            if (on) { _muted.Add(app); RemoveWhere(e => e.App.Equals(app, StringComparison.OrdinalIgnoreCase)); }
            else _muted.Remove(app);
            MutedChanged();
            SaveState();
        }
    }

    /// <summary>Some notifications out of the history (one, a person's, a whole app's).</summary>
    public static void RemoveWhere(Func<Entry, bool> which)
    {
        lock (Gate)
        {
            var all = All();
            if (all.RemoveAll(e => which(e)) == 0) return;
            if (CanRewrite) WriteLater(all);
            else AppLog.Write("notification history: not rewritten (a part of the file couldn't be read; it's kept as it is)");
        }
    }

    /// <summary>A person writing (a chat or mail app), not an app telling you something.</summary>
    public static bool FromPerson(Entry e) => ChatApp.IsMatch(e.App) && !NotPerson.IsMatch(e.Title + " " + e.Body);

    /// <summary>What the two files said, read without touching the shared state (so it can be read outside the lock).</summary>
    sealed class Loaded
    {
        public string Folder = "";
        public List<Entry> Entries = new();
        public bool Read, Unreadable, Failed, StateBad;
        public int Old;
        public State? State;
    }

    static List<Entry> All()
    {
        if (_all != null) return _all;
        Install(ReadFiles(Folder));
        return _all!;
    }

    static Loaded ReadFiles(string folder)
    {
        var d = new Loaded { Folder = folder };
        string file = Path.Combine(folder, "notifications-history.jsonl"), stateFile = Path.Combine(folder, "notifications-state.json");
        if (File.Exists(file))
        {
            List<string>? lines = null;
            for (int attempt = 0; attempt < 4 && lines == null; attempt++)
                try { lines = File.ReadAllLines(file).Where(l => l.Trim().Length > 0).ToList(); }
                catch { Thread.Sleep(150); } // another app has it this moment
            if (lines == null) { d.Failed = true; AppLog.Write("notification history: couldn't be opened (kept as it is)"); }
            else
            {
                d.Read = true;
                var cut = DateTime.Now.AddDays(-14);
                int unreadable = 0;
                for (int i = 0; i < lines.Count; i++)
                {
                    Entry? e = null;
                    try { e = JsonSerializer.Deserialize<Entry>(lines[i]); } catch { }
                    if (e == null) { if (i < lines.Count - 1) unreadable++; } // (only the last line, cut short by a crash: dropped, nothing else lost)
                    else if (e.At < cut) d.Old++;
                    else d.Entries.Add(e);
                }
                d.Unreadable = unreadable > 0;
            }
        }
        if (File.Exists(stateFile))
        {
            d.State = SafeFile.Load(stateFile, t => JsonSerializer.Deserialize<State>(t), out bool stateBad, "notification marks");
            d.StateBad = stateBad;
        }
        return d;
    }

    /// <summary>What was read becomes the history in memory (called inside the lock).</summary>
    static void Install(Loaded d)
    {
        _all = d.Entries;
        if (d.Failed) _loadFailed = true;
        if (d.Read)
        {
            _unreadable = d.Unreadable;
            // older than two weeks: they go. A line that couldn't be read never leads to rewriting (nothing is lost by accident)
            if (d.Old > 0 && CanRewrite) WriteLater(_all);
        }
        if (d.State is { } s)
        { _done = s.Done; _snoozed = s.Snoozed; _muted = new(s.Muted ?? new(), StringComparer.OrdinalIgnoreCase); _imported = s.Imported; }
        if (d.StateBad) _marksFailed = true; // (only the marks are kept as they are: the history itself goes on)
        MutedChanged();
    }

    /// <summary>
    /// The whole history written again (after removing some, or bringing some in): the previous file is kept as a backup
    /// first, and the new one replaces it in one step (never a half-written file). A copy is taken now; it's written
    /// by the writer, in turn.
    /// </summary>
    static void WriteLater(List<Entry> all)
    {
        var copy = all.ToList(); // (the entries never change: copying the list is enough)
        string path = FilePath;
        Later(() =>
        {
            try { SafeFile.WriteText(path, string.Concat(copy.Select(e => JsonSerializer.Serialize(e) + "\n"))); } // (the last version kept as .bak)
            catch (Exception ex) { AppLog.Write("notification history: " + ex.Message); }
        });
    }

    // ───────────────────────────── the disk, in turn ─────────────────────────────
    // Notifications come in on the UI thread: the history in memory changes at once (so everything read from it is
    // right), and the file work (an added line, the daily rewrite, the marks, a clear) waits its turn on one writer,
    // in the order it was asked. The writer is a foreground thread only while it has something to write, so Pixo
    // closing waits for the last line to be on the disk.

    static readonly Queue<Action> Pending = new();
    static readonly object PendingGate = new();
    static bool _writing;

    static void Later(Action write)
    {
        lock (PendingGate)
        {
            Pending.Enqueue(write);
            if (_writing) return;
            _writing = true;
        }
        try { new Thread(Drain) { IsBackground = false, Name = "Notification history", Priority = ThreadPriority.BelowNormal }.Start(); }
        catch (Exception ex) { AppLog.Write("notification history writer: " + ex.Message); Drain(); } // (no thread: written here, as before)
    }

    static void Drain()
    {
        while (true)
        {
            Action next;
            lock (PendingGate)
            {
                if (Pending.Count == 0) { _writing = false; Monitor.PulseAll(PendingGate); return; }
                next = Pending.Dequeue();
            }
            try { next(); } catch (Exception ex) { AppLog.Write("notification history: " + ex.Message); }
        }
    }

    /// <summary>Waits (a few seconds at most) for the writes still on their way: Pixo closing, or the folder changing.</summary>
    public static void Flush(int ms = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        lock (PendingGate)
            while (_writing)
            {
                var left = until - DateTime.UtcNow;
                if (left <= TimeSpan.Zero) { AppLog.Write("notification history: still writing (left to finish on its own)"); return; }
                Monitor.Wait(PendingGate, left);
            }
    }

    public static void Add(DateTime at, string app, string title, string body)
    {
        if (title.Trim().Length == 0 && body.Trim().Length == 0) return;
        var e = new Entry(at, app.Trim(), title.Trim(), body.Length > 600 ? body[..600] : body.Trim());
        lock (Gate)
        {
            var all = All();
            if (_muted.Contains(e.App)) return; // you muted this app here
            if (all.Count > 0 && all[^1].App == e.App && all[^1].Title == e.Title && all[^1].Body == e.Body && (e.At - all[^1].At).TotalSeconds < 30) return; // the same one twice
            all.Add(e);
            if (!_loadFailed)
            {
                string folder = Folder, path = FilePath;
                Later(() => { try { Directory.CreateDirectory(folder); File.AppendAllText(path, JsonSerializer.Serialize(e) + "\n"); } catch { } });
            }
            // the notch can run for weeks without a restart: the two weeks are kept then too (checked against a day more,
            // so the file is written again at most once a day, not with every notification)
            if (all[0].At < DateTime.Now.AddDays(-15))
            {
                var cut = DateTime.Now.AddDays(-14);
                all.RemoveAll(x => x.At < cut);
                if (CanRewrite) WriteLater(all);
            }
        }
    }

    static bool _unreadable; // the file had a line that couldn't be read: it's never written again whole (that line would go)
    static bool _loadFailed; // the history file couldn't be opened at all: nothing is written over it
    static bool _marksFailed; // the marks (done, snoozed, muted) couldn't be read: they're kept as they are
    static bool CanRewrite => !_unreadable && !_loadFailed;

    /// <summary>Read once in the background as Pixo starts (the first notification never waits for the disk).</summary>
    // (the files are read outside the lock, so a notification arriving meanwhile isn't held up behind the disk)
    public static void Preload() => Task.Run(() =>
    {
        string folder;
        lock (Gate) { if (_all != null) return; folder = Folder; }
        var d = ReadFiles(folder);
        lock (Gate) if (_all == null && Folder == folder) Install(d); // (already read by a caller, or the folder changed: this one goes)
    });

    /// <summary>The last two weeks, newest first.</summary>
    public static List<Entry> Recent() { lock (Gate) return All().AsEnumerable().Reverse().ToList(); }

    static readonly Regex ChatApp = new(@"discord|whatsapp|teams|slack|telegram|messenger|signal|skype|outlook|mail|gmail|instagram|viber|wechat|line\b|thunderbird|zoom", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex Asks = new(@"\?|\b(can you|could you|would you|please|pls|plz|send me|call me|let me know|are you|did you|when will|need you|reply|answer|tu peux|vous pouvez|peux[- ]tu|stp|svp|est[- ]ce que|tu es|t'es|réponds|envoie[- ]moi|appelle[- ]moi|wach|3afak|ila b9iti|b3at|sift|kteb)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex NotPerson = new(@"\b(security|sécurité|verification|vérification|code|password|mot de passe|newsletter|promo|offer|offre|sale|soldes|update available|mise à jour|backup|sync|bot)\b|no-?reply", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Is this a person asking you something (in a chat or mail app)?</summary>
    public static bool AsksYou(Entry e) => ChatApp.IsMatch(e.App) && Asks.IsMatch(e.Title + " " + e.Body) && !NotPerson.IsMatch(e.Title + " " + e.Body);

    /// <summary>People still waiting on you: asked within two days, not marked done, not snoozed; one per person (their latest).</summary>
    public static List<Entry> Waiting()
    {
        lock (Gate)
        {
            var now = DateTime.Now;
            return All().Where(e => now - e.At < TimeSpan.FromDays(2) && AsksYou(e) && !_done.Contains(e.Id) && !(_snoozed.TryGetValue(e.Id, out var until) && until > now))
                .GroupBy(e => e.App + "|" + e.Title).Select(g => g.OrderByDescending(e => e.At).First())
                .OrderByDescending(e => e.At).Take(12).ToList();
        }
    }

    public static void Done(Entry e) { lock (Gate) { _done.Add(e.Id); SaveState(); } }
    public static void Snooze(Entry e, TimeSpan forHow) { lock (Gate) { _snoozed[e.Id] = DateTime.Now + forHow; SaveState(); } }

    public static void Clear()
    {
        lock (Gate)
        {
            _all = new();
            // "Clear all" deletes them all, its backup too (nothing you cleared stays behind); in turn, after the
            // lines still on their way (so none of them lands after the clear)
            string path = FilePath;
            Later(() => { try { File.Delete(path); File.Delete(path + ".bak"); } catch { } });
        }
    }

    static void SaveState()
    {
        var cut = DateTime.Now.AddDays(-3);
        foreach (var k in _snoozed.Where(s => s.Value < cut).Select(s => s.Key).ToList()) _snoozed.Remove(k);
        var live = All().Select(e => e.Id).ToHashSet();
        _done.IntersectWith(live);
        if (_marksFailed) return; // (marks that couldn't be read are kept as they are)
        // a copy now (the sets go on changing), written in turn by the writer
        var state = new State { Done = new(_done), Snoozed = new(_snoozed), Muted = new(_muted, StringComparer.OrdinalIgnoreCase), Imported = _imported };
        string path = StateFile;
        Later(() => { try { SafeFile.WriteText(path, JsonSerializer.Serialize(state)); } catch { } });
    }
}
