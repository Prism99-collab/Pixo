using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DynamicNotch.Services;

/// <summary>
/// The AI usage page's numbers: the limits of the AI subscriptions signed in on this PC, and what Claude Code used.
///  • Claude: Claude Code's sign-in (%USERPROFILE%\.claude\.credentials.json, or CLAUDE_CONFIG_DIR), else the Claude
///    desktop app's own (Services/ClaudeDesktopSignIn), asked at
///    api.anthropic.com/api/oauth/usage: the 5-hour session and the week (and the Opus / Sonnet weeks when there are).
///  • ChatGPT: Codex's sign-in (%USERPROFILE%\.codex\auth.json), asked at chatgpt.com/backend-api/wham/usage; when that
///    fails, the last limits Codex itself wrote in its session logs.
///  • Cost: Claude Code's own logs (.claude\projects\**\*.jsonl), read from where the last read stopped, priced with a
///    table of Claude's prices; today and the last 7 days.
/// The sign-ins are only read, never renewed, never written, never logged. Each provider is asked at most every
/// 5 minutes, one request at a time; a "too many requests" answer waits what the server says (else 2, 4, 8, 16, 30 min).
/// The last good answer is kept in ai-usage.json and shown, marked as older, when a new one can't be had.
/// </summary>
public static class AiLimits
{
    public sealed record Window(string Label, double Used, DateTime? ResetsAt, double Hours);
    public sealed record Reading(string Id, string Name, string? Plan, List<Window> Windows, DateTime ReadAt);
    public sealed record State(Reading? Last, string? Error);
    public sealed record Cost(long TodayTokens, double TodayUsd, long WeekTokens, double WeekUsd);

    public static readonly (string Id, string Name)[] Providers = { ("claude", "Claude"), ("chatgpt", "ChatGPT") };

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    static string FilePath => Path.Combine(Settings.Folder, "ai-usage.json");
    static readonly object Gate = new();

    sealed class Saved
    {
        public Dictionary<string, bool> Shown { get; set; } = new();
        public Dictionary<string, Reading> Last { get; set; } = new();
        /// <summary>Each limit's readings over time ("claude|Week" → unix seconds, % used), the last 8 days: the Pace chart's line.</summary>
        public Dictionary<string, List<double[]>> History { get; set; } = new();
        /// <summary>The page's options: the view it opens on (limits, pace, cost), and whether the bars show where they're heading.</summary>
        public string View { get; set; } = "limits";
        public bool Projection { get; set; } = true;
    }

    public static string View { get { lock (Gate) return Store.View is "pace" or "cost" ? Store.View : "limits"; } }
    public static bool Projection { get { lock (Gate) return Store.Projection; } }
    public static void SetView(string v) { lock (Gate) Store.View = v; Save(); }
    public static void SetProjection(bool on) { lock (Gate) Store.Projection = on; Save(); }

    /// <summary>A limit's readings since its window began (oldest first).</summary>
    public static List<(DateTime At, double Used)> History(string id, string label, DateTime since)
    {
        lock (Gate)
            return Store.History.TryGetValue(id + "|" + label, out var list)
                ? list.Where(p => p.Length == 2 && DateTimeOffset.FromUnixTimeSeconds((long)p[0]).UtcDateTime >= since)
                      .Select(p => (DateTimeOffset.FromUnixTimeSeconds((long)p[0]).UtcDateTime, p[1])).ToList()
                : new();
    }

    /// <summary>A fresh reading joins each limit's history (one point every 10 minutes at most, or when it moved).</summary>
    static void Remember(Reading r)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), oldest = now - 8 * 86400;
        foreach (var w in r.Windows)
        {
            var list = Store.History.TryGetValue(r.Id + "|" + w.Label, out var l) ? l : Store.History[r.Id + "|" + w.Label] = new();
            list.RemoveAll(p => p.Length != 2 || p[0] < oldest);
            if (list.Count > 0 && now - list[^1][0] < 600 && Math.Abs(list[^1][1] - w.Used) < 1) continue;
            list.Add(new double[] { now, w.Used });
        }
    }

    sealed class Slot
    {
        public DateTime NextAt;      // not asked again before this (the cache, or a back-off)
        public int Backoff;
        public Task<State>? Running;
        public string? Error;
    }

    static Saved? _saved;
    static readonly Dictionary<string, Slot> Slots = new();

    static Saved Store
    {
        get
        {
            lock (Gate)
            {
                if (_saved != null) return _saved;
                try { _saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(FilePath)); } catch { }
                return _saved ??= new Saved();
            }
        }
    }

    static void Save()
    {
        string json;
        lock (Gate) json = JsonSerializer.Serialize(Store);
        try { Directory.CreateDirectory(Settings.Folder); File.WriteAllText(FilePath + ".tmp", json); File.Move(FilePath + ".tmp", FilePath, true); }
        catch (Exception ex) { AppLog.Write("ai usage: not saved: " + ex.Message); }
    }

    // ───────────── which providers ─────────────

    static string ClaudeDir => Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } d ? d : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
    static string CodexDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    /// <summary>Signed in (or, for Claude, used) on this PC.</summary>
    public static bool Found(string id) => id switch
    {
        "claude" => File.Exists(Path.Combine(ClaudeDir, ".credentials.json")) || Directory.Exists(Path.Combine(ClaudeDir, "projects")) || ClaudeDesktopSignIn.Present,
        "chatgpt" => File.Exists(Path.Combine(CodexDir, "auth.json")),
        _ => false,
    };

    /// <summary>Shown on the page: your choice, else every provider found on this PC.</summary>
    public static bool Shown(string id)
    {
        lock (Gate) if (Store.Shown.TryGetValue(id, out var on)) return on;
        return Found(id);
    }

    public static void SetShown(string id, bool on)
    {
        lock (Gate) Store.Shown[id] = on;
        Save();
    }

    public static State Cached(string id)
    {
        lock (Gate) return new(Store.Last.GetValueOrDefault(id), Slots.GetValueOrDefault(id)?.Error);
    }

    // ───────────── asking ─────────────

    /// <summary>The provider's limits: a fresh answer when the cache allows, else the last one (with what went wrong).</summary>
    public static Task<State> ReadAsync(string id)
    {
        lock (Gate)
        {
            var slot = Slots.TryGetValue(id, out var s) ? s : Slots[id] = new Slot();
            if (slot.Running != null) return slot.Running;
            if (DateTime.UtcNow < slot.NextAt) return Task.FromResult(new State(Store.Last.GetValueOrDefault(id), slot.Error));
            return slot.Running = Task.Run(async () =>
            {
                Reading? fresh = null;
                string? error = null;
                TimeSpan? retryAfter = null;
                bool tooMany = false;
                try { fresh = id == "claude" ? await ClaudeAsync() : await CodexAsync(); }
                catch (LimitsException ex) { error = ex.Message; tooMany = ex.TooMany; retryAfter = ex.RetryAfter; }
                catch (Exception ex) { error = "Couldn't reach it"; AppLog.Write($"ai usage {id}: {ex.GetType().Name}"); }
                lock (Gate)
                {
                    slot.Running = null;
                    slot.Error = error;
                    if (fresh != null) { Store.Last[id] = fresh; Remember(fresh); slot.Backoff = 0; slot.NextAt = DateTime.UtcNow.AddMinutes(5); }
                    else if (tooMany) slot.NextAt = DateTime.UtcNow + (retryAfter is { } ra && ra < TimeSpan.FromDays(7) ? (ra < TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : ra) : Backoff(slot));
                    else slot.NextAt = DateTime.UtcNow.AddMinutes(error is null ? 5 : 2);
                }
                if (fresh != null) Save();
                return Cached(id);
            });
        }
    }

    static TimeSpan Backoff(Slot s)
    {
        int[] steps = { 2, 4, 8, 16, 30 };
        var m = steps[Math.Min(s.Backoff++, steps.Length - 1)];
        return TimeSpan.FromMinutes(m * (1 + Random.Shared.NextDouble() * 0.1));
    }

    sealed class LimitsException(string message, bool tooMany = false, TimeSpan? retryAfter = null) : Exception(message)
    {
        public bool TooMany { get; } = tooMany;
        public TimeSpan? RetryAfter { get; } = retryAfter;
    }

    static async Task<JsonNode?> GetAsync(string url, Action<HttpRequestHeaders> headers, string expiredText)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        headers(req.Headers);
        using var res = await Http.SendAsync(req);
        if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new LimitsException(expiredText);
        if ((int)res.StatusCode == 429) throw new LimitsException("Asked too often · waiting", tooMany: true, retryAfter: res.Headers.RetryAfter?.Delta ?? (res.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow));
        if (!res.IsSuccessStatusCode) throw new LimitsException($"Not available right now ({(int)res.StatusCode})");
        return JsonNode.Parse(await res.Content.ReadAsStringAsync());
    }

    static async Task<Reading> ClaudeAsync()
    {
        // Claude Code's sign-in first (a terminal login), else the desktop app's (most people only have that one)
        var file = Path.Combine(ClaudeDir, ".credentials.json");
        var creds = File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file))?["claudeAiOauth"] : null;
        string? token = (string?)creds?["accessToken"], plan = (string?)creds?["subscriptionType"];
        // a terminal sign-in that has run out is no sign-in (the desktop app's may well be fine)
        if (creds?["expiresAt"] is JsonValue ev && ev.TryGetValue<long>(out long exp) && exp < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) token = null;
        async Task<JsonNode?> Ask(string t, string expired) => await GetAsync("https://api.anthropic.com/api/oauth/usage", h =>
        {
            h.Authorization = new("Bearer", t);
            h.Add("anthropic-beta", "oauth-2025-04-20");
        }, expired);
        if (!string.IsNullOrEmpty(token))
        {
            try { return ParseClaude(await Ask(token, "Sign-in expired · run claude to renew"), plan, DateTime.UtcNow); }
            catch (LimitsException) when (ClaudeDesktopSignIn.Present) { } // (refused: the desktop app's sign-in next)
        }
        if (await Task.Run(ClaudeDesktopSignIn.Read) is not { } desk) throw new LimitsException("Limits need Claude's sign-in · open the Claude app or run claude");
        return ParseClaude(await Ask(desk.Token, "Sign-in expired · open Claude to renew"), desk.Plan, DateTime.UtcNow);
    }

    /// <summary>Claude's answer: five_hour → Session, seven_day → Week, seven_day_opus / seven_day_sonnet when present.</summary>
    public static Reading ParseClaude(JsonNode? json, string? plan, DateTime readAt)
    {
        var list = new List<Window>();
        foreach (var (key, label, hours) in new[] { ("five_hour", "Session", 5.0), ("seven_day", "Week", 168.0), ("seven_day_opus", "Opus week", 168.0), ("seven_day_sonnet", "Sonnet week", 168.0) })
        {
            if (json?[key] is not JsonObject w || w["utilization"] is not JsonValue u || !u.TryGetValue(out double used)) continue;
            DateTime? reset = DateTime.TryParse((string?)w["resets_at"], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var r) ? r : null;
            list.Add(new Window(label, used, reset, hours));
        }
        if (list.Count == 0) throw new LimitsException("No limits in the answer");
        string? badge = plan?.ToLowerInvariant() switch { "max" => "Max", "pro" => "Pro", "team" => "Team", "enterprise" => "Enterprise", { Length: > 0 } p => char.ToUpper(p[0]) + p[1..], _ => null };
        return new Reading("claude", "Claude", badge, list, readAt);
    }

    static bool _codexShapeTold;

    static async Task<Reading> CodexAsync()
    {
        var file = Path.Combine(CodexDir, "auth.json");
        if (!File.Exists(file)) throw new LimitsException("Not signed in to Codex");
        var tokens = JsonNode.Parse(File.ReadAllText(file))?["tokens"];
        var token = (string?)tokens?["access_token"];
        var account = (string?)tokens?["account_id"];
        try
        {
            if (string.IsNullOrEmpty(token)) throw new LimitsException("Not signed in to Codex");
            var json = await GetAsync("https://chatgpt.com/backend-api/wham/usage", h =>
            {
                h.Authorization = new("Bearer", token);
                if (!string.IsNullOrEmpty(account)) h.Add("chatgpt-account-id", account);
                h.UserAgent.ParseAdd("codex_cli_rs");
            }, "Sign-in expired · open Codex to renew");
            var r = ParseCodex(json, DateTime.UtcNow);
            if (r != null) return r;
            if (!_codexShapeTold) { _codexShapeTold = true; AppLog.Write("ai usage chatgpt: unexpected answer, keys: " + string.Join(",", (json as JsonObject)?.Select(p => p.Key) ?? Array.Empty<string>())); }
        }
        catch (LimitsException ex) when (!ex.TooMany && CodexFromLogs() is { } fromLogs) { _ = ex; return fromLogs; }
        return CodexFromLogs() ?? throw new LimitsException("No limits in the answer");
    }

    /// <summary>ChatGPT's answer: rate_limit.primary_window / secondary_window (used_percent, limit_window_seconds, reset_at or reset_after_seconds).</summary>
    public static Reading? ParseCodex(JsonNode? json, DateTime readAt)
    {
        var rl = json?["rate_limit"];
        var list = new List<Window>();
        foreach (var key in new[] { "primary_window", "secondary_window" })
        {
            if (rl?[key] is not JsonObject w || !Num(w["used_percent"], out var used)) continue;
            double secs = Num(w["limit_window_seconds"], out var lw) ? lw : 0;
            DateTime? reset = Num(w["reset_at"], out var at) && at > 0 ? DateTimeOffset.FromUnixTimeSeconds((long)at).UtcDateTime
                : Num(w["reset_after_seconds"], out var after) ? readAt.AddSeconds(after) : null;
            list.Add(new Window(WindowName(secs), used, reset, secs / 3600));
        }
        if (list.Count == 0) return null;
        string? plan = (string?)json?["plan_type"] is { Length: > 0 } p ? char.ToUpper(p[0]) + p[1..] : null;
        return new Reading("chatgpt", "ChatGPT", plan, list, readAt);
    }

    static bool Num(JsonNode? n, out double v)
    {
        v = 0;
        return n is JsonValue j && j.TryGetValue(out v);
    }

    static string WindowName(double secs) => secs switch
    {
        >= 17000 and <= 19000 => "Session",
        >= 600000 and <= 610000 => "Week",
        >= 2400000 and <= 2700000 => "Month",
        > 0 and < 86400 => $"{Math.Round(secs / 3600)} h",
        > 0 => $"{Math.Round(secs / 86400)} days",
        _ => "Limit",
    };

    /// <summary>The last limits Codex wrote in its newest session log (its token_count events carry rate_limits).</summary>
    static Reading? CodexFromLogs()
    {
        try
        {
            var dir = Path.Combine(CodexDir, "sessions");
            if (!Directory.Exists(dir)) return null;
            var newest = new DirectoryInfo(dir).EnumerateFiles("*.jsonl", SearchOption.AllDirectories).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (newest == null) return null;
            string? last = null;
            using (var fs = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs))
                for (string? line; (line = sr.ReadLine()) != null;) if (line.Contains("\"rate_limits\"")) last = line;
            if (last == null) return null;
            var root = JsonNode.Parse(last);
            var rl = root?["payload"]?["rate_limits"] ?? root?["rate_limits"];
            DateTime readAt = DateTime.TryParse((string?)root?["timestamp"], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal, out var ts) ? ts : newest.LastWriteTimeUtc;
            var list = new List<Window>();
            foreach (var key in new[] { "primary", "secondary" })
            {
                if (rl?[key] is not JsonObject w || !Num(w["used_percent"], out var used)) continue;
                double secs = Num(w["window_minutes"], out var wm) ? wm * 60 : 0;
                DateTime? reset = Num(w["resets_at"], out var at) && at > 0 ? DateTimeOffset.FromUnixTimeSeconds((long)at).UtcDateTime
                    : Num(w["resets_in_seconds"], out var after) ? readAt.AddSeconds(after) : null;
                list.Add(new Window(WindowName(secs), used, reset, secs / 3600));
            }
            return list.Count == 0 ? null : new Reading("chatgpt", "ChatGPT", null, list, readAt);
        }
        catch { return null; }
    }

    // ───────────── the pace ─────────────

    /// <summary>
    /// Where the window is heading at this rate: the share used by the reset, or (past 100 %) when the limit is reached.
    /// Nothing when the reading is over 15 min old, under a tenth of the window has passed, or it's at 0 or 100 %.
    /// </summary>
    public static (double? AtReset, DateTime? LimitAt) Pace(Window w, DateTime readAt, DateTime now)
    {
        if (w.ResetsAt is not { } reset || w.Hours <= 0 || w.Used <= 0 || w.Used >= 100 || (now - readAt).TotalMinutes > 15) return (null, null);
        var len = TimeSpan.FromHours(w.Hours);
        var start = reset - len;
        var elapsed = readAt - start;
        if (elapsed.TotalSeconds < len.TotalSeconds * 0.1) return (null, null);
        double projected = w.Used * len.TotalSeconds / elapsed.TotalSeconds;
        return projected <= 100 ? (projected, null) : (null, start + TimeSpan.FromSeconds(elapsed.TotalSeconds * 100 / w.Used));
    }

    // ───────────── what Claude Code cost ─────────────

    /// <summary>USD per million tokens (input, output) for a Claude model; null for one not in the table (its tokens still count).</summary>
    public static (double In, double Out)? Price(string model)
    {
        var m = model.ToLowerInvariant();
        if (m.Contains("opus"))
            return System.Text.RegularExpressions.Regex.IsMatch(m, @"3-opus|opus-4(-0|-1|-20|$)") ? (15, 75) : (5, 25); // (Opus 4.5 and later cost a third of the first ones)
        if (m.Contains("sonnet")) return (3, 15);
        if (m.Contains("haiku")) return m.Contains("3-haiku") ? (0.25, 1.25) : m.Contains("3-5-haiku") ? (0.8, 4) : (1, 5);
        return null;
    }

    /// <summary>One request's price: input, output, cache reads at a tenth, cache writes at 1.25× (2× for the one-hour cache).</summary>
    public static double Usd(string model, long input, long output, long cacheRead, long write5m, long write1h)
    {
        if (Price(model) is not var (pin, pout)) return 0;
        return (input * pin + output * pout + cacheRead * pin * 0.1 + write5m * pin * 1.25 + write1h * pin * 2) / 1_000_000;
    }

    sealed class LogState
    {
        public Dictionary<string, long> Offsets { get; set; } = new();
        /// <summary>day (yyyy-MM-dd, local) → model → [input, output, cache read, cache write 5 min, cache write 1 h]</summary>
        public Dictionary<string, Dictionary<string, long[]>> Days { get; set; } = new();
        /// <summary>day → the requests already counted (a hash of message id + request id), so none counts twice.</summary>
        public Dictionary<string, HashSet<ulong>> Seen { get; set; } = new();
    }

    static string LogsPath => Path.Combine(Settings.Folder, "ai-usage-logs.json");
    static LogState? _logs;
    static int _scanning;
    static Cost? _cost;

    /// <summary>The last totals worked out (instant), or null before the first read.</summary>
    public static Cost? CostNow => _cost;

    /// <summary>
    /// Reads what Claude Code wrote since the last time (on a low-priority thread, line by line, only lines with a
    /// "usage"), then the totals. The first time reads the last 8 days, which can be hundreds of MB: it saves its
    /// place every few seconds, so even an interrupted first read is never done twice.
    /// </summary>
    public static Task<Cost?> CostAsync()
    {
        if (Interlocked.Exchange(ref _scanning, 1) == 1) return Task.FromResult(_cost);
        var done = new TaskCompletionSource<Cost?>();
        var t = new Thread(() =>
        {
            try { ScanLogs(); _cost = Totals(); }
            catch (Exception ex) { AppLog.Write("ai usage cost: " + ex.Message); }
            finally { Interlocked.Exchange(ref _scanning, 0); done.TrySetResult(_cost); }
        }) { IsBackground = true, Priority = ThreadPriority.Lowest, Name = "AI usage logs" };
        t.Start();
        return done.Task;
    }

    static readonly byte[] UsageMark = Encoding.UTF8.GetBytes("\"usage\"");

    static void ScanLogs()
    {
        var root = Path.Combine(ClaudeDir, "projects");
        if (!Directory.Exists(root)) return;
        if (_logs == null) { try { _logs = JsonSerializer.Deserialize<LogState>(File.ReadAllText(LogsPath)); } catch { } _logs ??= new LogState(); }
        var st = _logs;
        var since = DateTime.Now.Date.AddDays(-7);
        string keep = since.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var d in st.Days.Keys.Where(k => string.CompareOrdinal(k, keep) < 0).ToList()) { st.Days.Remove(d); st.Seen.Remove(d); }
        var files = new DirectoryInfo(root).EnumerateFiles("*.jsonl", SearchOption.AllDirectories).Where(f => f.LastWriteTime >= since.AddDays(-1)).ToList();
        var live = new HashSet<string>(files.Select(f => f.FullName), StringComparer.OrdinalIgnoreCase);
        foreach (var gone in st.Offsets.Keys.Where(k => !live.Contains(k)).ToList()) st.Offsets.Remove(gone);
        var lastSave = Stopwatch.StartNew();
        var buf = new byte[1 << 20];
        foreach (var f in files)
        {
            long off = st.Offsets.GetValueOrDefault(f.FullName);
            if (f.Length < off) off = 0; // (rewritten from the start)
            if (f.Length == off) continue;
            try
            {
                using var fs = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                fs.Position = off;
                int carry = 0;
                while (true)
                {
                    int n = fs.Read(buf, carry, buf.Length - carry);
                    if (n <= 0) break;
                    int len = carry + n, start = 0;
                    for (int i = 0; i < len; i++)
                    {
                        if (buf[i] != (byte)'\n') continue;
                        var line = buf.AsSpan(start, i - start);
                        if (line.IndexOf(UsageMark) >= 0) CountLine(st, line, since);
                        start = i + 1;
                    }
                    off += start;
                    carry = len - start;
                    if (carry == buf.Length) { off += carry; carry = 0; continue; } // (a line longer than the buffer: a pasted file, never a usage line)
                    Buffer.BlockCopy(buf, start, buf, 0, carry);
                }
                st.Offsets[f.FullName] = off; // (a half-written last line is read next time)
            }
            catch (IOException) { }
            if (lastSave.ElapsedMilliseconds > 3000) { SaveLogs(st); lastSave.Restart(); }
        }
        SaveLogs(st);
    }

    static void CountLine(LogState st, ReadOnlySpan<byte> line, DateTime since)
    {
        try
        {
            var reader = new Utf8JsonReader(line);
            using var doc = JsonDocument.ParseValue(ref reader);
            var r = doc.RootElement;
            if (!r.TryGetProperty("type", out var type) || type.GetString() != "assistant" || !r.TryGetProperty("message", out var msg) || !msg.TryGetProperty("usage", out var u)) return;
            string model = msg.TryGetProperty("model", out var mm) ? mm.GetString() ?? "" : "";
            if (model.Length == 0 || model.StartsWith('<')) return; // (<synthetic>: no request was made)
            if (!r.TryGetProperty("timestamp", out var tsEl) || !DateTime.TryParse(tsEl.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal, out var ts)) return;
            var local = ts.ToLocalTime();
            if (local < since) return;
            string key = (msg.TryGetProperty("id", out var id) ? id.GetString() : "") + "|" + (r.TryGetProperty("requestId", out var rq) ? rq.GetString() : "");
            string day = local.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            var seen = st.Seen.TryGetValue(day, out var s) ? s : st.Seen[day] = new HashSet<ulong>();
            if (!seen.Add(Fnv(key))) return;
            long L(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
            long write = L(u, "cache_creation_input_tokens"), w1h = 0, w5m = 0;
            if (u.TryGetProperty("cache_creation", out var cc) && cc.ValueKind == JsonValueKind.Object) { w1h = L(cc, "ephemeral_1h_input_tokens"); w5m = L(cc, "ephemeral_5m_input_tokens"); }
            write = Math.Max(write, w1h + w5m);
            var models = st.Days.TryGetValue(day, out var dm) ? dm : st.Days[day] = new Dictionary<string, long[]>();
            var t = models.TryGetValue(model, out var arr) ? arr : models[model] = new long[5];
            t[0] += L(u, "input_tokens"); t[1] += L(u, "output_tokens"); t[2] += L(u, "cache_read_input_tokens"); t[3] += write - w1h; t[4] += w1h;
        }
        catch (JsonException) { }
    }

    static ulong Fnv(string s)
    {
        ulong h = 14695981039346656037;
        foreach (char c in s) { h ^= c; h *= 1099511628211; }
        return h;
    }

    static void SaveLogs(LogState st)
    {
        try { var json = JsonSerializer.Serialize(st); File.WriteAllText(LogsPath + ".tmp", json); File.Move(LogsPath + ".tmp", LogsPath, true); }
        catch (Exception ex) { AppLog.Write("ai usage cost: not saved: " + ex.Message); }
    }

    /// <summary>What Claude Code cost each of the last 7 days (oldest first, today last).</summary>
    public static List<(DateTime Day, double Usd, long Tokens)> CostDays()
    {
        var st = _logs;
        var list = new List<(DateTime, double, long)>();
        for (int k = 6; k >= 0; k--)
        {
            var day = DateTime.Now.Date.AddDays(-k);
            double usd = 0; long tokens = 0;
            if (st != null && st.Days.TryGetValue(day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), out var models))
                foreach (var (model, t) in models) { usd += Usd(model, t[0], t[1], t[2], t[3], t[4]); tokens += t.Sum(); }
            list.Add((day, usd, tokens));
        }
        return list;
    }

    /// <summary>The last 7 days' cost by model family (Opus, Sonnet, Haiku, other), the biggest first.</summary>
    public static List<(string Family, double Usd)> CostModels()
    {
        var st = _logs;
        var by = new Dictionary<string, double>();
        string from = DateTime.Now.Date.AddDays(-6).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (st != null)
            foreach (var (day, models) in st.Days)
            {
                if (string.CompareOrdinal(day, from) < 0) continue;
                foreach (var (model, t) in models)
                {
                    var m = model.ToLowerInvariant();
                    string fam = m.Contains("opus") ? "Opus" : m.Contains("sonnet") ? "Sonnet" : m.Contains("haiku") ? "Haiku" : m.Contains("fable") ? "Fable" : "Other";
                    by[fam] = by.GetValueOrDefault(fam) + Usd(model, t[0], t[1], t[2], t[3], t[4]);
                }
            }
        return by.Where(kv => kv.Value > 0.005).OrderByDescending(kv => kv.Value).Select(kv => (kv.Key, kv.Value)).ToList();
    }

    static Cost? Totals()
    {
        var st = _logs;
        if (st == null) return null;
        string today = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), from = DateTime.Now.Date.AddDays(-6).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        long tt = 0, wt = 0;
        double tu = 0, wu = 0;
        foreach (var (day, models) in st.Days)
        {
            if (string.CompareOrdinal(day, from) < 0) continue;
            foreach (var (model, t) in models)
            {
                long tokens = t.Sum();
                double usd = Usd(model, t[0], t[1], t[2], t[3], t[4]);
                wt += tokens; wu += usd;
                if (day == today) { tt += tokens; tu += usd; }
            }
        }
        return new Cost(tt, tu, wt, wu);
    }
}
