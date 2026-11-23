using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DynamicNotch.Services;

/// <summary>
/// Listening: the default microphone, turned into 16 kHz mono speech while you talk, with its level for the
/// picture, ending by itself a moment after you stop talking (or after a minute). Nothing is kept on disk.
/// </summary>
public sealed class VoiceRecorder : IDisposable
{
    WasapiCapture? _capture;
    readonly MemoryStream _pcm = new();
    double _phase;           // where we are between two 16 kHz samples
    double _acc; int _accN;  // the samples since the last 16 kHz one, averaged (a simple low-pass: no aliasing)
    long _written, _firstVoiceAt = -1, _lastVoiceAt = -1; // 16 kHz samples written; where speech began and was last heard
    int _rate, _channels, _bits;
    bool _float;
    DateTime _started, _lastVoice;
    bool _heard, _stopping;
    double _noise = 0.004;   // the room's own level, learnt in the first moments

    /// <summary>0..1, about 30 times a second.</summary>
    public event Action<double>? Level;
    /// <summary>It stopped (by itself: silence after speech, nothing said, or a minute): true if speech was heard.</summary>
    public event Action<bool>? Ended;

    public bool Heard => _heard;
    /// <summary>How long it's been quiet since you last spoke (zero before you've said anything).</summary>
    public TimeSpan SilentFor => _heard ? DateTime.UtcNow - _lastVoice : TimeSpan.Zero;
    /// <summary>Where speech was last heard (changes whenever you speak again).</summary>
    public long LastVoiceAt => Interlocked.Read(ref _lastVoiceAt);
    /// <summary>How much speech there is, from the first word to the last.</summary>
    public TimeSpan SpeechLength => _firstVoiceAt < 0 ? TimeSpan.Zero : TimeSpan.FromSeconds((_lastVoiceAt - _firstVoiceAt) / 16000.0);
    public TimeSpan Length => TimeSpan.FromSeconds(_pcm.Length / 32000.0);

    // Start runs off the screen's thread (opening the mic can take a second), so a stop or a dispose can come while it's
    // still opening: they meet under this lock, and a mic stopped before it opened is never started
    readonly object _gate = new();
    bool _disposed;

    public void Start()
    {
        var device = new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        var capture = new WasapiCapture(device, true, 20);
        lock (_gate)
        {
            if (_stopping || _disposed) { try { capture.Dispose(); } catch { } return; } // stopped while it opened
            _capture = capture;
        }
        var f = capture.WaveFormat;
        _rate = f.SampleRate;
        _channels = f.Channels;
        _bits = f.BitsPerSample;
        _float = f.Encoding == WaveFormatEncoding.IeeeFloat || f is WaveFormatExtensible ext && ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71") /* IEEE float */;
        capture.DataAvailable += OnData;
        capture.RecordingStopped += (_, _) => Ended?.Invoke(_heard);
        _started = _lastVoice = DateTime.UtcNow;
        lock (_gate)
        {
            if (_stopping || _disposed) return; // stopped just now: never started
            capture.StartRecording();
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_stopping) return;
            _stopping = true;
            if (_capture == null) return; // still opening: Start sees it and never starts
            try { _capture.StopRecording(); } catch { Ended?.Invoke(_heard); }
        }
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        int bytesPer = _bits / 8 * _channels;
        if (bytesPer == 0) return;
        int frames = e.BytesRecorded / bytesPer;
        double step = _rate / 16000.0, sum = 0;
        lock (_pcm) {
        var w = new BinaryWriter(_pcm);
        for (int i = 0; i < frames; i++)
        {
            double v = 0;
            for (int c = 0; c < _channels; c++)
            {
                int at = i * bytesPer + c * (_bits / 8);
                v += _float && _bits == 32 ? BitConverter.ToSingle(e.Buffer, at)
                    : _bits == 16 ? BitConverter.ToInt16(e.Buffer, at) / 32768.0
                    : _bits == 24 ? ((e.Buffer[at] | e.Buffer[at + 1] << 8 | (sbyte)e.Buffer[at + 2] << 16) / 8388608.0)
                    : _bits == 32 ? BitConverter.ToInt32(e.Buffer, at) / 2147483648.0 : 0;
            }
            v /= _channels;
            sum += v * v;
            // down to 16 kHz: the average of the samples in each step (keeping only one in three folded the high
            // sounds down into the voice, and consonants came out blurred: wrong words)
            _acc += v; _accN++;
            _phase += 1;
            if (_phase >= step)
            {
                _phase -= step;
                double avg = _acc / Math.Max(1, _accN);
                _acc = 0; _accN = 0;
                w.Write((short)Math.Clamp(avg * 32767, -32768, 32767));
                _written++;
            }
        }
        }
        double rms = frames > 0 ? Math.Sqrt(sum / frames) : 0;
        var now = DateTime.UtcNow;
        if (now - _started < TimeSpan.FromMilliseconds(400)) _noise = Math.Max(_noise, rms * 1.2); // the room, before you speak
        bool voice = rms > Math.Max(0.012, _noise * 2.2);
        if (voice) { _heard = true; _lastVoice = now; if (_firstVoiceAt < 0) _firstVoiceAt = Math.Max(0, _written - frames / Math.Max(1, (long)step)); _lastVoiceAt = _written; }
        Level?.Invoke(Math.Clamp(Math.Sqrt(rms) * 2.4, 0, 1));
        // the end: a pause after speech (2 s: a breath or a "hmm" mid-sentence doesn't end it), nothing said for 7 s, or a minute in all
        if (_heard && now - _lastVoice > TimeSpan.FromMilliseconds(2000)) Stop();
        else if (!_heard && now - _started > TimeSpan.FromSeconds(7)) Stop();
        else if (now - _started > TimeSpan.FromSeconds(60)) Stop();
    }

    /// <summary>
    /// Only the speech: from a moment before the first word to a moment after the last (the silence around it is cut,
    /// and a shorter clip is written down faster).
    /// </summary>
    public float[] SpeechSamples()
    {
        var all = Samples();
        if (_firstVoiceAt < 0) return all;
        int from = (int)Math.Clamp(_firstVoiceAt - 5600, 0, all.Length); // 0.35 s before
        int to = (int)Math.Clamp(_lastVoiceAt + 6400, from, all.Length); // 0.4 s after
        return all[from..to];
    }

    /// <summary>What was said so far, as 16 kHz samples (-1..1): for writing it down on this PC, even while it records.</summary>
    public float[] Samples()
    {
        byte[] data;
        lock (_pcm) data = _pcm.ToArray();
        var f = new float[data.Length / 2];
        for (int i = 0; i < f.Length; i++) f[i] = BitConverter.ToInt16(data, i * 2) / 32768f;
        return f;
    }

    /// <summary>What was said, as a WAV file in memory.</summary>
    public byte[] Wav()
    {
        byte[] data;
        lock (_pcm) data = _pcm.ToArray();
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + data.Length); w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(16000); w.Write(32000); w.Write((short)2); w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data")); w.Write(data.Length); w.Write(data);
        w.Flush();
        return ms.ToArray();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = _stopping = true;
            try { _capture?.Dispose(); } catch { }
            _capture = null;
        }
    }
}

/// <summary>
/// Speaking: an answer read aloud with the Windows voice you picked (or one that speaks the answer's language),
/// code and links skipped, played with the PC's default output. One at a time; Stop ends it at once.
/// </summary>
public static class VoiceOut
{
    static WasapiOut? _player;
    static int _gen;
    public static bool Speaking => _player?.PlaybackState == PlaybackState.Playing;
    public static event Action<bool>? Changed;

    public static IReadOnlyList<(string Id, string Name, string Language)> Voices()
    {
        try { return Windows.Media.SpeechSynthesis.SpeechSynthesizer.AllVoices.Select(v => (v.Id, v.DisplayName, v.Language)).ToList(); }
        catch { return Array.Empty<(string, string, string)>(); }
    }

    /// <summary>Markdown into something worth hearing: no code, no link addresses, no symbols.</summary>
    public static string Speakable(string md)
    {
        string s = Regex.Replace(md, "```.*?```", " (code skipped) ", RegexOptions.Singleline);
        s = Regex.Replace(s, @"!\[([^\]]*)\]\([^)]+\)", "$1");
        s = Regex.Replace(s, @"\[([^\]]+)\]\([^)]+\)", "$1");
        s = Regex.Replace(s, @"https?://\S+", "a link");
        s = Regex.Replace(s, @"[A-Za-z]:\\[^\s]+", m => Path.GetFileName(m.Value.TrimEnd('\\', '.', ',')));
        s = Regex.Replace(s, @"[*_`#>|]+", " ");
        s = Regex.Replace(s, @"^\s*[-•+]\s+", "", RegexOptions.Multiline);
        s = Regex.Replace(s, @"[ \t]+", " ");
        return s.Trim().Length > 2400 ? s.Trim()[..2400] + "…" : s.Trim();
    }

    public static async Task Speak(string text, string voiceId, double rate)
    {
        Stop();
        int gen = ++_gen;
        string say = Speakable(text);
        if (say.Length == 0) return;
        try
        {
            using var synth = new Windows.Media.SpeechSynthesis.SpeechSynthesizer();
            var voices = Windows.Media.SpeechSynthesis.SpeechSynthesizer.AllVoices;
            // the voice you picked; or one in the answer's language (Arabic, French...) when yours doesn't speak it
            var chosen = voices.FirstOrDefault(v => v.Id == voiceId) ?? Windows.Media.SpeechSynthesis.SpeechSynthesizer.DefaultVoice;
            string lang = Regex.IsMatch(say, @"\p{IsArabic}") ? "ar" : Regex.Matches(say, @"\b(le|la|les|des|est|une|pour|avec|dans|vous|je)\b", RegexOptions.IgnoreCase).Count > 4 ? "fr" : "";
            if (lang.Length > 0 && !chosen.Language.StartsWith(lang, StringComparison.OrdinalIgnoreCase) && voices.FirstOrDefault(v => v.Language.StartsWith(lang, StringComparison.OrdinalIgnoreCase)) is { } native) chosen = native;
            synth.Voice = chosen;
            synth.Options.SpeakingRate = Math.Clamp(rate, 0.5, 2.5);
            var stream = await synth.SynthesizeTextToStreamAsync(say);
            if (gen != _gen) return;
            var ms = new MemoryStream();
            using (var s = System.IO.WindowsRuntimeStreamExtensions.AsStreamForRead(stream)) await s.CopyToAsync(ms);
            ms.Position = 0;
            if (gen != _gen) return;
            var reader = new WaveFileReader(ms);
            // opening the speakers can take a second on some PCs: never on the screen's thread (its events come back
            // on the player's own thread; the listeners hop to the screen themselves)
            var player = await Task.Run(() => { var p = new WasapiOut(AudioClientShareMode.Shared, 60); p.Init(reader); return p; });
            if (gen != _gen) { player.Dispose(); reader.Dispose(); return; }
            player.PlaybackStopped += (_, _) => { reader.Dispose(); if (ReferenceEquals(_player, player)) { _player = null; Changed?.Invoke(false); } player.Dispose(); };
            _player = player;
            player.Play();
            Changed?.Invoke(true);
        }
        catch (Exception ex) { AppLog.Write("voice out: " + ex.Message); }
    }

    public static void Stop()
    {
        _gen++;
        var p = _player;
        _player = null;
        if (p != null) { Task.Run(() => { try { p.Stop(); } catch { } }); Changed?.Invoke(false); } // (stopping waits for its thread: not on the screen's)
    }
}
