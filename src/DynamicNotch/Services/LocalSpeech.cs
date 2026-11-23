using System.IO;
using System.Net.Http;
using Whisper.net;

namespace DynamicNotch.Services;

/// <summary>
/// Speech written down on this PC (whisper.cpp, through Whisper.net): free, offline, private (nothing leaves the PC),
/// about 100 languages, no Windows setting needed. The model isn't in the installer: it's downloaded once, the first
/// time you talk, into Pixo's folder (speech\). The fast model (56 MB) turns a few seconds of speech into text in about
/// a second on an ordinary PC; the more accurate one (181 MB) is slower and optional.
/// What makes it quick for commands: the encoder is sized to what was said (not whisper's usual 30 seconds), and a short
/// list of the names people say to Pixo (apps, "rappelle-moi"…) helps it spell them.
/// </summary>
public static class LocalSpeech
{
    public sealed record Model(string File, string Name, long Bytes);

    public static readonly Model Fast = new("ggml-base-q5_1.bin", "Fast", 59_707_625);
    public static readonly Model Accurate = new("ggml-small-q5_1.bin", "More accurate", 190_085_487);

    public static Model Pick(string choice) => choice == "accurate" ? Accurate : Fast;

    public static string Folder => Path.Combine(Settings.Folder, "speech");
    static string PathOf(Model m) => Path.Combine(Folder, m.File);

    /// <summary>The model is on this PC, whole.</summary>
    public static bool Ready(Model m)
    {
        try { var f = new FileInfo(PathOf(m)); return f.Exists && f.Length == m.Bytes; }
        catch { return false; }
    }

    // ───────────── getting the model (once) ─────────────

    static Task<bool>? _download;
    static Model? _downloading;
    static readonly object DownloadGate = new();

    /// <summary>0–1 while a model downloads, else -1.</summary>
    public static double Progress { get; private set; } = -1;

    /// <summary>The download moved on, finished or failed (any thread).</summary>
    public static event Action? Changed;

    /// <summary>Downloads the model in the background (one at a time; already there: done at once). True when it's ready.</summary>
    public static Task<bool> Get(Model m)
    {
        if (Ready(m)) return Task.FromResult(true);
        lock (DownloadGate)
        {
            if (_download is { IsCompleted: false } && _downloading == m) return _download;
            _downloading = m;
            return _download = Task.Run(() => Download(m));
        }
    }

    static async Task<bool> Download(Model m)
    {
        string part = PathOf(m) + ".part";
        try
        {
            Directory.CreateDirectory(Folder);
            Progress = 0;
            Changed?.Invoke();
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Pixo");
            using var res = await http.GetAsync("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/" + m.File, HttpCompletionOption.ResponseHeadersRead);
            res.EnsureSuccessStatusCode();
            await using (var from = await res.Content.ReadAsStreamAsync())
            await using (var to = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                var buf = new byte[1 << 16];
                long got = 0, told = 0;
                int n;
                while ((n = await from.ReadAsync(buf)) > 0)
                {
                    await to.WriteAsync(buf.AsMemory(0, n));
                    got += n;
                    if (got - told > m.Bytes / 50) { told = got; Progress = Math.Min(0.99, got / (double)m.Bytes); Changed?.Invoke(); }
                }
                await to.FlushAsync();
            }
            if (new FileInfo(part).Length != m.Bytes) throw new IOException("the download was cut short");
            File.Move(part, PathOf(m), true);
            AppLog.Write($"voice: the offline model is ready ({m.Name}, {m.Bytes / 1048576} MB)");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Write("voice: the offline model couldn't be downloaded (" + ex.Message + ")");
            try { File.Delete(part); } catch { }
            return false;
        }
        finally
        {
            Progress = -1;
            Changed?.Invoke();
        }
    }

    // ───────────── writing it down ─────────────

    static WhisperFactory? _factory;
    static Model? _loaded;
    static readonly SemaphoreSlim Busy = new(1, 1);
    static Timer? _release;

    /// <summary>The names people say to Pixo, so they come out spelled right ("Discord", not "dis cordes").</summary>
    // (short plain sentences: a comma list made it put commas everywhere, "ouvre, Discord et envoie, bonjour")
    const string Hints = "Open Spotify. Ouvre Discord. Rappelle-moi demain. Mets le volume à 30. WhatsApp Teams Telegram Chrome Brave YouTube Netflix Steam Pixo.";

    /// <summary>
    /// This processor can run it: its build needs AVX2 and FMA on Intel and AMD (most PCs since about 2015; some
    /// low-cost Celeron and Pentium laptops don't have them, and there it would close Pixo instead of failing); ARM PCs can.
    /// </summary>
    public static bool CpuCanRun =>
        System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64
        || System.Runtime.Intrinsics.X86.Avx2.IsSupported && System.Runtime.Intrinsics.X86.Fma.IsSupported;

    static bool _broken;

    /// <summary>The engine can't run here (a processor without AVX2, a failure loading it): the other ways are used.</summary>
    public static bool Broken { get => _broken || !CpuCanRun; private set => _broken = value; }

    /// <summary>
    /// What was said (16 kHz mono samples), with the language it heard and how sure it is (0–1). Null when the model
    /// isn't here, the engine can't run, or (for a <paramref name="partial"/> look while you talk) it's busy.
    /// </summary>
    public static async Task<(string Text, string Language, float Sure)?> Write(Model m, float[] samples, string language, bool partial, CancellationToken cancel)
    {
        if (Broken || !Ready(m) || samples.Length < 1600) return null;
        samples = Level(samples);
        if (partial) { if (!await Busy.WaitAsync(0)) return null; }
        else await Busy.WaitAsync(cancel);
        try
        {
            return await Task.Run(async () =>
            {
                Load(m);
                // the encoder sized to what was said (whisper's 30-second window made a 3-second command take 3 times longer)
                int ctx = Math.Min(1500, (int)Math.Ceiling(samples.Length / 16000.0 * 50) + 64);
                var builder = _factory.CreateBuilder()
                    // the language given (decided once per recording, see Detect): guessing it among ~100 on every look
                    // took 5 times longer than writing the words down, and short speech came out as German or Hebrew
                    .WithLanguage(language is { Length: >= 2 } l && l != "auto" ? l[..2].ToLowerInvariant() : "auto")
                    // the live words use a third of the cores (the notch and your apps stay smooth); the final ones more
                    .WithThreads(partial ? Math.Clamp(Environment.ProcessorCount / 3, 2, 6) : Math.Clamp(Environment.ProcessorCount - 2, 2, 8))
                    .WithAudioContextSize(ctx).WithNoContext().WithSingleSegment().WithProbabilities()
                    .WithPrompt(Hints);
                // the final words: several ways of hearing it weighed (fewer wrong words, a fraction of a second more);
                // the live ones while you talk stay quick
                if (!partial) builder = builder.WithBeamSearchSamplingStrategy(_ => { });
                await using var proc = builder.Build();
                var text = new System.Text.StringBuilder();
                string lang = "";
                float sure = 1;
                await foreach (var seg in proc.ProcessAsync(samples, cancel))
                {
                    text.Append(seg.Text);
                    if (seg.Language is { Length: > 0 } sl) lang = sl;
                    sure = Math.Min(sure, seg.Probability);
                }
                string said = Tidy(text.ToString());
                return said.Length == 0 ? null : ((string, string, float)?)(said, lang, sure);
            }, cancel);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or TypeInitializationException)
        {
            Broken = true; // (this PC can't run it: the next tries go to the other ways at once)
            AppLog.Write("voice: the offline engine can't run here (" + ex.GetType().Name + ": " + ex.Message + ")");
            return null;
        }
        catch (Exception ex) { AppLog.Write("voice: " + ex.Message); return null; }
        finally
        {
            Busy.Release();
            // the model leaves memory after 3 quiet minutes (it's back in a quarter of a second)
            _release ??= new Timer(_ => Unload());
            _release.Change(TimeSpan.FromMinutes(3), Timeout.InfiniteTimeSpan);
        }
    }

    static void Load(Model m)
    {
        if (_factory != null && _loaded == m) return;
        _factory?.Dispose();
        _factory = WhisperFactory.FromPath(PathOf(m));
        _loaded = m;
    }

    /// <summary>
    /// The languages to choose from: the one picked in Preferences, else your own (your keyboard languages and
    /// Windows' languages) and English. Choosing among a few is right far more often than among a hundred.
    /// </summary>
    public static string[] Candidates(string picked)
    {
        if (picked is { Length: >= 2 } p && p != "auto") return new[] { p[..2].ToLowerInvariant() };
        var list = new List<string>();
        void Add(string? tag) { if (tag is { Length: >= 2 } t && t[..2].ToLowerInvariant() is var two && char.IsLetter(two[0]) && !list.Contains(two)) list.Add(two); }
        try { foreach (System.Windows.Forms.InputLanguage il in System.Windows.Forms.InputLanguage.InstalledInputLanguages) Add(il.Culture.Name); } catch { }
        try { foreach (var l in Windows.System.UserProfile.GlobalizationPreferences.Languages) Add(l); } catch { }
        Add(System.Globalization.CultureInfo.CurrentUICulture.Name);
        Add("en");
        return list.Take(6).ToArray();
    }

    /// <summary>Which of these languages is spoken (and how sure), from a moment of speech. One look per recording.</summary>
    public static async Task<(string Language, float Sure)?> Detect(Model m, float[] samples, string[] candidates, CancellationToken cancel)
    {
        if (candidates.Length == 1) return (candidates[0], 1f);
        if (Broken || !Ready(m) || samples.Length < 8000 || candidates.Length == 0) return null;
        samples = Level(samples);
        await Busy.WaitAsync(cancel);
        try
        {
            return await Task.Run(() =>
            {
                Load(m);
                using var proc = _factory!.CreateBuilder().WithThreads(Math.Clamp(Environment.ProcessorCount - 2, 2, 8)).Build();
                var (lang, sure) = proc.DetectLanguageWithProbability(samples.AsSpan(), candidates.AsSpan());
                return lang is { Length: > 0 } ? ((string, float)?)(lang, sure) : null;
            }, cancel);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) { AppLog.Write("voice: language: " + ex.Message); return null; }
        finally { Busy.Release(); }
    }

    static void Unload()
    {
        if (!Busy.Wait(0)) return;
        try { _factory?.Dispose(); _factory = null; _loaded = null; }
        finally { Busy.Release(); }
    }

    /// <summary>A quiet mic brought up to a normal level (at most 8 times louder), so soft speech is heard as well as loud.</summary>
    static float[] Level(float[] s)
    {
        float peak = 0;
        foreach (var x in s) { float a = Math.Abs(x); if (a > peak) peak = a; }
        if (peak <= 0.0005f || peak >= 0.5f) return s;
        float gain = Math.Min(8f, 0.8f / peak);
        var o = new float[s.Length];
        for (int i = 0; i < s.Length; i++) o[i] = s[i] * gain;
        return o;
    }

    /// <summary>What whisper adds around speech: "[BLANK_AUDIO]", "(music)", the hint list echoed back.</summary>
    static string Tidy(string s)
    {
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\[[^\]]*\]|\((?:music|musique|applause|silence|inaudible)[^)]*\)|\*[^*]*\*", " ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
        if (s.StartsWith("Open Spotify. Ouvre Discord", StringComparison.OrdinalIgnoreCase) || s.Contains("WhatsApp Teams Telegram")) return ""; // (silence: it read the hints back)
        return s;
    }
}
