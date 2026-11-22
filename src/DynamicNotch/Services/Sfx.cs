using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DynamicNotch.Services;

/// <summary>
/// Pixo's little interface sounds, warm and soft: felt mallets on wood, music-box bells and breaths of air, all in the
/// middle of the range (no shrill highs, no boom), with a slow-ish attack so nothing clicks, and a small warm room.
///  pick a rising fifth · on / off a soft swell up or down · copy two wood taps · pin a little three-note sparkle ·
///  delete "nom-nom" · clear an airy sweep into a bell · done a warm major chord · oops a gentle minor "bu-bum" ·
///  nothing found a questioning "hm?" · a screenshot a soft shutter · the timer a music-box tune ·
///  the timer's start / pause / reset, a page's press, the wheel's ticks (their pitch follows the value).
/// Made in code once, ahead of time, and played through <see cref="SoundOut"/>: a mixer that is already running
/// while you use Pixo, so a sound starts within a couple of hundredths of a second of the click.
/// </summary>
public static class Sfx
{
    public enum Cue { Tap, Pick, On, Off, Copy, Pin, Unpin, Eat, Clear, Done, Oops, Nothing, Snap, Timer, Love, Spin, Hush, Open, Start, Pause, Reset, Press, Scan }

    /// <summary>Off in the preferences ("Interface sounds"); quiet on its own while presenting and in meeting mode (not on a voice call: they play in your ears).</summary>
    public static bool Enabled { get; set; } = true;
    public static bool Hushed { get; set; }

    const int Rate = SoundOut.Rate;
    static readonly Dictionary<Cue, float[]> Made = new();
    static readonly float[][] Ticks = new float[9][];
    static readonly object Gate = new();
    static readonly Dictionary<Cue, long> Last = new();

    /// <summary>Makes every sound ahead of time (off the screen's thread).</summary>
    /// <summary>Every sound made ahead of time: a while after start, on one quiet low-priority thread.</summary>
    public static void Warm(int afterMs = 9_000) => Services.Quiet.Later(afterMs, () =>
    {
        {
            foreach (var c in Enum.GetValues<Cue>()) Get(c);
            for (int i = 0; i < Ticks.Length; i++) TickOf(i / (double)(Ticks.Length - 1));
            foreach (var k in Enum.GetValues<Key>()) for (int v = 0; v < (k == Key.Letter ? 3 : 1); v++) { var s = MakeKey(k, v); lock (Gate) Keys[(k, v)] = s; }
        }

    });

    /// <param name="always">An alarm (the timer): plays even with interface sounds off.</param>
    public static void Play(Cue cue, bool always = false)
    {
        if (!always && (!Enabled || Hushed)) return;
        long now = Environment.TickCount64;
        lock (Gate)
        {
            // the same sound again within a moment (a key held, a double click): once is enough
            if (Last.TryGetValue(cue, out var at) && now - at < 70) return;
            Last[cue] = now;
        }
        SoundOut.Play(Get(cue));
    }

    /// <summary>A wooden tick whose pitch follows a value (0..1): the wheel on the timer, a level being set.</summary>
    public static void Tick(double level)
    {
        if (!Enabled || Hushed) return;
        SoundOut.Play(TickOf(level));
    }

    static readonly float[][] Soft = new float[9][];
    static long _lastScroll;

    /// <summary>Scrolling a list: the same wooden tick, quieter, at most one every 90 ms, its pitch the place in the list.</summary>
    public static void ScrollTick(double place)
    {
        if (!Enabled || Hushed) return;
        long now = Environment.TickCount64;
        if (now - _lastScroll < 90) return;
        _lastScroll = now;
        int i = (int)Math.Round(Math.Clamp(place, 0, 1) * (Soft.Length - 1));
        var s = Soft[i];
        if (s == null)
        {
            var t = TickOf(i / (double)(Soft.Length - 1));
            s = new float[t.Length];
            for (int k = 0; k < t.Length; k++) s[k] = t[k] * 0.55f;
            Soft[i] = s;
        }
        SoundOut.PlayLight(s);
    }

    public enum Key { Letter, Space, Back, Enter, CapsOn, CapsOff }

    static readonly Dictionary<(Key, int), float[]> Keys = new();
    static int _keyVariant;
    static long _lastKey;

    /// <summary>
    /// A key typed in one of Pixo's boxes: a soft, warm click like a phone's keyboard, very quiet (three slightly
    /// different ones in turn, so fast typing never sounds like a machine); space deeper, backspace lower, Enter its own.
    /// Caps Lock: a tiny tick up when it turns on, down when it turns off.
    /// </summary>
    public static void Type(Key k)
    {
        if (!Typing || Hushed) return;
        long now = Environment.TickCount64;
        if (now - _lastKey < 18) return; // a held key repeats faster than anyone types
        _lastKey = now;
        int v = k == Key.Letter ? (_keyVariant = (_keyVariant + 1) % 3) : 0;
        float[]? s;
        lock (Gate) Keys.TryGetValue((k, v), out s);
        if (s == null) { s = MakeKey(k, v); lock (Gate) Keys[(k, v)] = s; }
        SoundOut.PlayLight(s);
    }

    /// <summary>Off in the preferences ("Typing sounds").</summary>
    public static bool Typing { get; set; } = true;

    static float[] MakeKey(Key k, int v)
    {
        var click = Recorded("tick") ?? Recorded("Press") ?? new float[480];
        (double pitch, float vol) = k switch
        {
            Key.Letter => (new[] { 1.0, 1.045, 0.965 }[v], 0.42f),
            Key.Space => (0.86, 0.48f),
            Key.Back => (0.8, 0.4f),
            Key.Enter => (0.74, 0.5f),
            Key.CapsOn => (1.18, 0.34f),
            _ => (0.92, 0.34f),
        };
        var s = Pitch(click, pitch);
        // warm: the top softened a little more (a phone's key is a soft, round tick)
        double lp = 0, kk = 1 - Math.Exp(-2 * Math.PI * 3600.0 / Rate);
        for (int i = 0; i < s.Length; i++) { lp += (s[i] - lp) * kk; s[i] = (float)lp * vol; }
        return s;
    }

    static float[] TickOf(double level)
    {
        int i = (int)Math.Round(Math.Clamp(level, 0, 1) * (Ticks.Length - 1));
        lock (Gate) if (Ticks[i] != null) return Ticks[i];
        // the soft click, played a little faster (higher) the higher the value: up to an octave and a half
        float[] made;
        if (Recorded("tick") is { } click) made = Pitch(click, Math.Pow(2, i / 12.0 * 1.5) * 0.9);
        else { var b = new Buf(); b.Mallet(0, 620 * Math.Pow(2, i / 12.0 * 1.5), .045, .55, knock: .5); made = SoundOut.FinishF(b.Done(), 0.075); }
        lock (Gate) Ticks[i] = made;
        return made;
    }

    /// <summary>The theme's own sounds: "g6" swaps a few cues for warm 80s synth ones (made here in code).</summary>
    public static string Theme { get; set; } = "";
    static readonly Dictionary<Cue, float[]> DuskMade = new();

    static float[] Get(Cue c)
    {
        if (Theme == "g6" && DuskSound(c) is { } synth) return synth;
        lock (Gate) if (Made.TryGetValue(c, out var have)) return have;
        // a recorded sound (Assets/Sounds: Kenney's Interface Sounds, CC0) when there is one, else made in code
        var made = Recorded(c.ToString()) ?? SoundOut.FinishF(Make(c), c switch { Cue.Tap or Cue.Copy or Cue.Press => 0.085, Cue.Timer or Cue.Done => 0.16, Cue.Scan or Cue.Open => 0.07, _ => 0.12 });
        lock (Gate) Made[c] = made;
        return made;
    }

    /// <summary>An embedded 48 kHz mono 16-bit sound, as samples (null when it isn't there).</summary>
    static float[]? Recorded(string name)
    {
        try
        {
            using var s = typeof(Sfx).Assembly.GetManifestResourceStream("DynamicNotch.Sounds." + name + ".wav");
            if (s == null) return null;
            using var r = new System.IO.BinaryReader(s);
            var all = r.ReadBytes((int)s.Length);
            for (int i = 12; i + 8 <= all.Length;)
            {
                string id = System.Text.Encoding.ASCII.GetString(all, i, 4);
                int len = BitConverter.ToInt32(all, i + 4);
                if (id == "data")
                {
                    int n = Math.Min(len, all.Length - i - 8) / 2;
                    var o = new float[n];
                    for (int k = 0; k < n; k++) o[k] = BitConverter.ToInt16(all, i + 8 + k * 2) / 32768f;
                    return o;
                }
                i += 8 + len + (len & 1);
            }
        }
        catch { }
        return null;
    }

    /// <summary>The same sound, faster or slower (higher or lower), by simple resampling.</summary>
    static float[] Pitch(float[] x, double rate)
    {
        int n = (int)(x.Length / rate);
        var o = new float[n];
        for (int i = 0; i < n; i++)
        {
            double t = i * rate;
            int k = (int)t;
            double f = t - k;
            o[i] = (float)(k + 1 < x.Length ? x[k] * (1 - f) + x[k + 1] * f : k < x.Length ? x[k] : 0);
        }
        return o;
    }

    // notes (Hz): the warm middle of the keyboard
    const double F4 = 349.2, G4 = 392.0, A4 = 440.0, C5 = 523.3, D5 = 587.3, E5 = 659.3, F5 = 698.5, G5 = 784.0, A5 = 880.0, B5 = 987.8, C6 = 1046.5, E6 = 1318.5, G6 = 1568.0;

    static float[] Make(Cue c)
    {
        var b = new Buf();
        switch (c)
        {
            case Cue.Tap: b.Mallet(0, A5, .05, .5, knock: .35); break;
            case Cue.Press: b.Mallet(0, E5, .07, .6, knock: .45); b.Mallet(.012, E5 * 2, .03, .12); break;
            case Cue.Pick: b.Mallet(0, E5, .11, .65); b.Mallet(.06, B5, .2, .7); break;
            case Cue.On: b.Swell(0, C5, G5, .11, .6); b.Mallet(.07, C6, .16, .3); break;
            case Cue.Off: b.Swell(0, G5, C5, .12, .55); break;
            case Cue.Copy: b.Mallet(0, G5, .05, .5, knock: .4); b.Mallet(.055, C6, .06, .45, knock: .4); break;
            case Cue.Pin: b.Bell(0, C6, .35, .4); b.Bell(.06, E6, .35, .35); b.Bell(.12, G6, .5, .32); break;
            case Cue.Unpin: b.Bell(0, G5, .25, .4); b.Bell(.07, D5, .3, .35); break;
            case Cue.Eat: b.Swell(0, A5, D5, .08, .7, round: true); b.Swell(.11, G5, C5, .08, .65, round: true); break; // nom-nom
            case Cue.Clear: b.Air(0, 700, 2600, .26, .45); b.Bell(.22, G5, .7, .45); b.Bell(.22, C6, .6, .2); break;
            case Cue.Done: b.Bell(0, G5, .8, .55); b.Bell(.08, C6, 1.0, .6); b.Bell(.16, E6, 1.0, .35); break; // a warm major chord, rolled
            case Cue.Oops: b.Mallet(0, A4 * 1.5, .14, .55); b.Mallet(.13, F4 * 1.5, .26, .5); break; // a gentle minor fall
            case Cue.Nothing: b.Swell(0, D5, G5, .17, .5, round: true); break; // "hm?"
            case Cue.Snap: b.Air(0, 1800, 2600, .035, .8); b.Air(.065, 1500, 2300, .045, .6); break;
            case Cue.Timer:
                // a little music box: up the chord and home again, twice softer the second time
                foreach (var (t, f, v) in new[] { (0.0, C5, .6), (.16, E5, .6), (.32, G5, .6), (.48, C6, .7), (.9, G5, .4), (1.06, C6, .45) }) b.Bell(t, f, 1.1, v);
                break;
            case Cue.Love: b.Bell(0, E5, .55, .45); b.Bell(.12, A5, .7, .5); b.Bell(.24, C6 * 1.06, .7, .25); break;
            case Cue.Spin: b.Swell(0, G4, G6, .34, .4, round: true); b.Bell(.3, C6, .5, .35); break;
            case Cue.Hush: b.Air(0, 2200, 1200, .24, .35); break; // "shhh"
            case Cue.Open: b.Air(0, 600, 1700, .16, .3); break;
            case Cue.Start: b.Mallet(0, C5, .12, .55); b.Mallet(.07, G5, .2, .6); b.Bell(.13, C6, .4, .2); break; // off it goes
            case Cue.Pause: b.Mallet(0, G5, .1, .5); b.Mallet(.07, D5, .18, .5); break;
            case Cue.Reset: b.Air(0, 1500, 600, .12, .35); b.Mallet(.09, C5, .16, .5); break;
            case Cue.Scan: b.Air(0, 500, 1400, .3, .22); b.Bell(.18, E6, .4, .12); break; // a soft look around
        }
        return b.Done();
    }

    /// <summary>A sound being made: notes are added at their time, then it's finished in <see cref="SoundOut.FinishF"/>.</summary>
    /// <summary>
    /// Dusk's synth: a soft, chorused pluck (two voices a hair apart, harmonics rolled off like an old analog synth), a
    /// warm pad that swells, and a gold sting of rising notes for things done. Only the cues that carry the theme;
    /// the others stay as they are.
    /// </summary>
    static float[]? DuskSound(Cue c)
    {
        lock (Gate) if (DuskMade.TryGetValue(c, out var have)) return have;
        var b = new Buf();
        static double N(int semis) => 440 * Math.Pow(2, semis / 12.0); // semitones from A4
        switch (c)
        {
            case Cue.Tap or Cue.Press: b.Pluck(0, N(7), .16, .5); break;                                   // E5
            case Cue.Pick: b.Pluck(0, N(3), .14, .42); b.Pluck(.06, N(10), .18, .45); break;               // C5 → G5
            case Cue.On: b.Pluck(0, N(0), .14, .4); b.Pluck(.07, N(7), .2, .45); break;                    // up a fifth
            case Cue.Off: b.Pluck(0, N(7), .14, .4); b.Pluck(.07, N(0), .2, .4); break;                    // down
            case Cue.Copy: b.Pluck(0, N(12), .1, .32); b.Pluck(.07, N(12), .14, .36); break;
            case Cue.Pin: b.Pluck(0, N(12), .12, .3); b.Pluck(.06, N(16), .12, .3); b.Pluck(.12, N(19), .22, .34); break;
            case Cue.Open: b.Pad(0, new[] { N(-12), N(-5), N(0), N(4) }, .55, .3, attack: .16); b.Pluck(.05, N(16), .3, .18); break;
            case Cue.Done:
                // the sting: a pad under four rising notes, the last one held, a sparkle on top
                b.Pad(0, new[] { N(-9), N(-5), N(-2) }, .9, .26, attack: .05);
                b.Pluck(0, N(3), .2, .45); b.Pluck(.09, N(7), .2, .45); b.Pluck(.18, N(10), .2, .45); b.Pluck(.27, N(15), .55, .5);
                b.Pluck(.36, N(22), .4, .16);
                break;
            case Cue.Oops: b.Pluck(0, N(3), .16, .4); b.Pluck(.09, N(-1), .26, .4); break;                 // a minor drop
            default: return null;
        }
        var made = SoundOut.FinishF(b.Done(), c is Cue.Done ? 0.16 : c is Cue.Open ? 0.07 : 0.09);
        lock (Gate) DuskMade[c] = made;
        return made;
    }

    sealed class Buf
    {
        float[] _s = new float[Rate * 2];
        int _len;
        static readonly Random Rand = new(7);

        void Add(int i, double v)
        {
            if (i < 0) return;
            if (i >= _s.Length) Array.Resize(ref _s, Math.Max(_s.Length * 2, i + Rate));
            _s[i] += (float)v;
            if (i + 1 > _len) _len = i + 1;
        }

        static double Attack(int i, double ms) => i >= Rate * ms / 1000 ? 1 : Math.Sin(Math.PI / 2 * i / (Rate * ms / 1000)); // a rounded start: no click

        /// <summary>A felt mallet on wood: a round tone, a touch of its octave, and a soft knock that's gone at once.</summary>
        public void Mallet(double at, double f, double secs, double vol, double knock = .25)
        {
            int start = (int)(at * Rate), n = (int)(secs * Rate * 2.5);
            double tau = secs / 2.6;
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / Rate;
                double v = Math.Sin(2 * Math.PI * f * t)
                         + .22 * Math.Sin(4 * Math.PI * f * t) * Math.Exp(-t / (tau * .6))
                         + knock * Math.Sin(2 * Math.PI * f * 3.98 * t) * Math.Exp(-t / .006);
                Add(start + i, vol * .55 * v * Math.Exp(-t / tau) * Attack(i, 3.5));
            }
        }

        /// <summary>A music-box / glass bell, warm: few partials, the high ones gone quickly, a slow gentle shimmer.</summary>
        public void Bell(double at, double f, double secs, double vol)
        {
            double[] ratio = { 1, 2.0, 3.0 }, amp = { 1, .2, .06 }, decay = { 1, .45, .25 };
            int start = (int)(at * Rate), n = (int)(secs * Rate * 1.5);
            for (int k = 0; k < ratio.Length; k++)
            {
                double tau = secs * decay[k] / 3;
                for (int j = 0; j < 2; j++) // two voices a hair apart: a living shimmer
                {
                    double fd = f * ratio[k] * (j == 0 ? 0.9995 : 1.0005);
                    for (int i = 0; i < n; i++)
                    {
                        double t = (double)i / Rate;
                        Add(start + i, vol * .5 * amp[k] * Math.Sin(2 * Math.PI * fd * t) * Math.Exp(-t / tau) * Attack(i, 6));
                    }
                }
            }
        }

        /// <summary>A rounded tone that slides (a swell up for "on", down for "off", a "nom"): soft at both ends.</summary>
        public void Swell(double at, double from, double to, double secs, double vol, bool round = false)
        {
            int start = (int)(at * Rate), n = (int)(secs * Rate), tail = (int)(.05 * Rate);
            double ph = 0;
            for (int i = 0; i < n + tail; i++)
            {
                double t = Math.Min(1, (double)i / n);
                double f = from * Math.Pow(to / from, 1 - Math.Pow(1 - t, 2));
                ph += 2 * Math.PI * f / Rate;
                double env = Attack(i, 8) * (i < n ? 1 - .3 * t : .7 * Math.Pow(1 - (double)(i - n) / tail, 2));
                double v = Math.Sin(ph) + (round ? .14 : .22) * Math.Sin(2 * ph);
                Add(start + i, vol * .5 * v * env);
            }
        }

        /// <summary>Air: soft noise through a sweeping band (a whoosh, a "shh", a shutter).</summary>
        public void Air(double at, double from, double to, double secs, double vol)
        {
            int start = (int)(at * Rate), n = (int)(secs * Rate);
            double y1 = 0, y2 = 0;
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / n;
                double f = from * Math.Pow(to / from, t), q = 1.8;
                double w = 2 * Math.PI * f / Rate, r = Math.Exp(-w / (2 * q));
                double x = Rand.NextDouble() * 2 - 1;
                double y = (1 - r) * x + 2 * r * Math.Cos(w) * y1 - r * r * y2;
                y2 = y1; y1 = y;
                double env = Math.Sin(Math.PI * Math.Min(1, t * 1.1)) * Attack(i, 6);
                Add(start + i, vol * 1.6 * y * Math.Max(0, env));
            }
        }

        /// <summary>An analog-style pluck: a few odd and even harmonics rolled off, two voices detuned for chorus, a quick fall.</summary>
        public void Pluck(double at, double f, double secs, double vol)
        {
            int start = (int)(at * Rate), n = (int)(secs * Rate * 2.2);
            double tau = secs / 2.4;
            for (int v = 0; v < 2; v++)
            {
                double fd = f * (v == 0 ? 0.9965 : 1.0035);
                for (int i = 0; i < n; i++)
                {
                    double t = (double)i / Rate;
                    double bright = Math.Exp(-t / (tau * 0.45)); // the filter closing: bright at first, round after
                    double s = Math.Sin(2 * Math.PI * fd * t) + bright * (.34 * Math.Sin(4 * Math.PI * fd * t) + .16 * Math.Sin(6 * Math.PI * fd * t) + .07 * Math.Sin(8 * Math.PI * fd * t));
                    Add(start + i, vol * .32 * s * Math.Exp(-t / tau) * Attack(i, 2.5));
                }
            }
        }

        /// <summary>A warm pad: a soft chord swelling in, slow chorus, fading away.</summary>
        public void Pad(double at, double[] notes, double secs, double vol, double attack = .2)
        {
            int start = (int)(at * Rate), n = (int)(secs * Rate);
            foreach (var f in notes)
                for (int v = 0; v < 2; v++)
                {
                    double fd = f * (v == 0 ? 0.997 : 1.003);
                    for (int i = 0; i < n; i++)
                    {
                        double t = (double)i / Rate;
                        double env = Math.Min(1, t / attack) * Math.Pow(Math.Max(0, 1 - t / secs), 1.6);
                        double s = Math.Sin(2 * Math.PI * fd * t) + .25 * Math.Sin(4 * Math.PI * fd * t);
                        Add(start + i, vol * .16 * s * env);
                    }
                }
        }

        public float[] Done()
        {
            var o = new float[_len];
            Array.Copy(_s, o, _len);
            return o;
        }
    }
}

/// <summary>
/// The one way Pixo's own sounds reach the speakers: a small mixer on the system's audio engine (WASAPI, a 20 ms
/// buffer) that keeps running while Pixo is in use (<see cref="Wake"/> starts it ahead of a click), mixes any number
/// of sounds at once (the face's voice over a click), and lets the device go after a quiet half-minute.
/// If the audio engine can't be used, the older waveOut path plays them instead.
/// </summary>
public static class SoundOut
{
    public const int Rate = 48000;

    /// <summary>Finishing touches: no rumble (high-pass ~180 Hz), the harsh top softened (~5.5 kHz), a small warm room,
    /// levelled to a quiet peak, never a hard edge.</summary>
    public static float[] FinishF(float[] s, double peak)
    {
        if (s.Length == 0) return Array.Empty<float>();
        int tailN = Rate * 22 / 100;
        var buf = new float[s.Length + tailN];
        Array.Copy(s, buf, s.Length);
        for (int pass = 0; pass < 2; pass++)
        {
            double a = Math.Exp(-2 * Math.PI * 180 / Rate), prevX = 0, prevY = 0;
            for (int i = 0; i < buf.Length; i++) { double y = a * (prevY + buf[i] - prevX); prevX = buf[i]; prevY = y; buf[i] = (float)y; }
        }
        for (int pass = 0; pass < 2; pass++)
        {
            double lp = 0, k = 1 - Math.Exp(-2 * Math.PI * 5500.0 / Rate);
            for (int i = 0; i < buf.Length; i++) { lp += (buf[i] - lp) * k; buf[i] = (float)lp; }
        }
        // a small warm room: early reflections, each one darker than the last
        int[] taps = { Rate * 17 / 1000, Rate * 29 / 1000, Rate * 43 / 1000, Rate * 61 / 1000, Rate * 89 / 1000, Rate * 127 / 1000 };
        double[] gains = { .16, .12, .09, .065, .045, .03 };
        var dry = (float[])buf.Clone();
        for (int t = 0; t < taps.Length; t++)
        {
            double lp = 0, k = 1 - Math.Exp(-2 * Math.PI * (3200.0 - t * 400) / Rate);
            for (int i = taps[t]; i < buf.Length; i++) { lp += (dry[i - taps[t]] - lp) * k; buf[i] += (float)(gains[t] * lp); }
        }
        double max = 0;
        foreach (var x in buf) max = Math.Max(max, Math.Abs(x));
        double vol = peak / Math.Max(1e-6, max);
        var o = new float[buf.Length];
        int fadeN = Rate / 25;
        for (int i = 0; i < buf.Length; i++)
        {
            double x = Math.Tanh(buf[i] * vol * 1.15) / 1.15;
            double fade = i > buf.Length - fadeN ? (buf.Length - i) / (double)fadeN : 1;
            o[i] = (float)(x * fade);
        }
        return o;
    }

    /// <summary>The older 16-bit form (the face's voice uses it too).</summary>
    public static short[] Finish(float[] s, double peak)
    {
        var f = FinishF(s, peak);
        var o = new short[f.Length];
        for (int i = 0; i < f.Length; i++) o[i] = (short)Math.Clamp(f[i] * short.MaxValue, short.MinValue, short.MaxValue);
        return o;
    }

    public static void Play(short[] pcm)
    {
        if (pcm.Length == 0) return;
        var f = new float[pcm.Length];
        for (int i = 0; i < f.Length; i++) f[i] = pcm[i] / 32768f;
        Play(f);
    }

    public static void Play(float[] pcm)
    {
        if (pcm.Length == 0) return;
        // never a pile-up: at most 6 sounds a second, whatever asks for them (and it's written down, to be found)
        long now = Environment.TickCount64;
        lock (Gate)
        {
            while (_recent.Count > 0 && now - _recent.Peek() > 1000) _recent.Dequeue();
            if (_recent.Count >= 6)
            {
                if (now - _spamLoggedAt > 10_000) { _spamLoggedAt = now; AppLog.Write("sounds: too many at once, held back | " + Environment.StackTrace); }
                return;
            }
            _recent.Enqueue(now);
        }
        Legacy.Play(pcm); // a few voices mixed by Windows: a new sound never cuts the one still playing
    }

    /// <summary>A scroll tick: never counted against the others (it has its own pace), never held back by them.</summary>
    public static void PlayLight(float[] pcm) { if (pcm.Length > 0) Legacy.Play(pcm); }

    static readonly Queue<long> _recent = new();
    static long _spamLoggedAt;

    static bool Running { get { lock (Gate) return _out != null && _out.PlaybackState == PlaybackState.Playing; } }

    /// <summary>Gets the device going now (the pointer came near, the notch opened): the next sound has no wait.</summary>
    /// <summary>Opens the device now (the pointer came near, the notch opened): the next sound has no wait.</summary>
    public static void Wake() => Legacy.Wake();

    /// <summary>The speakers or headset in use changed: the streams are opened afresh on the new one.</summary>
    public static void DeviceChanged() => Legacy.Reopen();

    /// <summary>Opens the streams in the background (nothing plays): the first sound has nothing to wait for.</summary>
    public static void Prepare() { if (Environment.GetEnvironmentVariable("DYNAMICNOTCH_WAVEPROBE") == "1") Task.Run(Legacy.Probe); else Legacy.Prepare(); }

    // ───────── the running mixer ─────────

    static readonly Mixer Mix = new();
    static WasapiOut? _out;
    static readonly object Gate = new();
    static Timer? _idle;
    static bool _failed;
    static long _failedAt;

    static bool Ensure()
    {
        lock (Gate)
        {
            if (_out != null && _out.PlaybackState == PlaybackState.Playing) return true;
            if (_failed && Environment.TickCount64 - _failedAt < 60_000) return false; // try the engine again a minute later
            try
            {
                _out?.Dispose();
                _out = new WasapiOut(AudioClientShareMode.Shared, true, 20);
                _out.Init(Mix);
                _out.PlaybackStopped += (_, _) => { lock (Gate) { _out?.Dispose(); _out = null; } }; // a device change: a fresh one next time
                _out.Play();
                _failed = false;
                return true;
            }
            catch (Exception ex)
            {
                _failed = true;
                _failedAt = Environment.TickCount64;
                try { _out?.Dispose(); } catch { }
                _out = null;
                AppLog.Write("sounds: the audio engine said no (" + ex.Message + "), using waveOut");
                return false;
            }
        }
    }

    /// <summary>Quiet for half a minute: the device is let go (nothing keeps the audio engine busy for nothing).</summary>
    static void Touch()
    {
        lock (Gate)
        {
            _idle ??= new Timer(_ =>
            {
                lock (Gate)
                {
                    if (Mix.Busy) { _idle!.Change(5000, Timeout.Infinite); return; }
                    try { _out?.Stop(); _out?.Dispose(); } catch { }
                    _out = null;
                }
            });
            _idle.Change(30_000, Timeout.Infinite);
        }
    }

    /// <summary>Mixes the sounds that are playing into the engine's stream (stereo float): silence when there are none.</summary>
    sealed class Mixer : ISampleProvider
    {
        readonly List<(float[] Data, int Pos)> _voices = new();
        readonly object _gate = new();
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2);
        public bool Busy { get { lock (_gate) return _voices.Count > 0; } }

        public void Add(float[] data) { lock (_gate) { if (_voices.Count < 12) _voices.Add((data, 0)); } }
        public void Clear() { lock (_gate) _voices.Clear(); }

        public int Read(float[] buffer, int offset, int count)
        {
            Array.Clear(buffer, offset, count);
            lock (_gate)
            {
                int frames = count / 2;
                for (int v = _voices.Count - 1; v >= 0; v--)
                {
                    var (data, pos) = _voices[v];
                    int n = Math.Min(frames, data.Length - pos);
                    for (int i = 0; i < n; i++)
                    {
                        float s = data[pos + i];
                        buffer[offset + i * 2] += s;
                        buffer[offset + i * 2 + 1] += s;
                    }
                    pos += n;
                    if (pos >= data.Length) _voices.RemoveAt(v); else _voices[v] = (data, pos);
                }
            }
            // several at once never clip: a soft ceiling
            for (int i = offset; i < offset + count; i++) { float x = buffer[i]; if (x > 0.6f || x < -0.6f) buffer[i] = (float)Math.Tanh(x); }
            return count;
        }
    }

    // ───────── the older path (waveOut), if the audio engine can't be used ─────────

    static class Legacy
    {
        [StructLayout(LayoutKind.Sequential)]
        struct WaveFormatEx { public ushort Tag, Channels; public uint Rate, BytesPerSec; public ushort Align, Bits, Extra; }

        [StructLayout(LayoutKind.Sequential)]
        struct WaveHeader { public IntPtr Data; public uint Length, Recorded; public IntPtr User; public uint Flags, Loops; public IntPtr Next, Reserved; }

        [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr h, uint device, ref WaveFormatEx format, IntPtr callback, IntPtr instance, uint flags);
        [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr h, IntPtr header, uint size);
        [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr h, IntPtr header, uint size);
        [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr h, IntPtr header, uint size);
        [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr h);

        static readonly object Gate = new();
        static readonly uint HdrSize = (uint)Marshal.SizeOf<WaveHeader>();
        const uint WhdrDone = 0x1;

        /// <summary>
        /// One of a few voices, each its own waveOut stream: a new sound takes a free voice and Windows mixes them.
        /// Nothing already playing is ever stopped or re-cut (that cut was the click you could hear when sounds came
        /// close together: page ticks, a burst of notifications).
        /// </summary>
        sealed class Voice { public IntPtr Dev, Hdr, Data; public long At; public int Samples; }
        static readonly Voice[] Voices = { new(), new(), new(), new() };

        static Timer? _close;

        static bool Open(Voice v)
        {
            if (v.Dev != IntPtr.Zero) return true;
            var fmt = new WaveFormatEx { Tag = 1, Channels = 1, Rate = Rate, BytesPerSec = Rate * 2, Align = 2, Bits = 16 };
            if (waveOutOpen(out v.Dev, 0xFFFFFFFF, ref fmt, IntPtr.Zero, IntPtr.Zero, 0) != 0) { v.Dev = IntPtr.Zero; return false; }
            return true;
        }

        // a silent stream that keeps the speakers or the headset awake (a wireless one falls asleep in silence and needs
        // seconds to wake: the first sounds came late or broken for a while after a quiet spell)
        static readonly Voice Warm = new();

        static void StartWarm()
        {
            if (Warm.Hdr != IntPtr.Zero && !Done(Warm)) return; // already humming
            if (!Open(Warm)) return;
            Release(Warm, stop: true);
            int n = (int)Rate; // one second of silence, played over and over
            Warm.Data = Marshal.AllocHGlobal(n * 2);
            Marshal.Copy(new byte[n * 2], 0, Warm.Data, n * 2);
            Warm.Hdr = Marshal.AllocHGlobal((int)HdrSize);
            Marshal.StructureToPtr(new WaveHeader { Data = Warm.Data, Length = (uint)(n * 2), Flags = 0x4 | 0x8 /* begin and end of the loop */, Loops = 0xFFFFFFFF }, Warm.Hdr, false);
            if (waveOutPrepareHeader(Warm.Dev, Warm.Hdr, HdrSize) != 0 || waveOutWrite(Warm.Dev, Warm.Hdr, HdrSize) != 0) Release(Warm, stop: true);
        }

        /// <summary>
        /// Quiet for two minutes: the silent stream stops (nothing keeps the audio engine busy for nothing). The voices
        /// themselves stay open: an open stream with nothing in it costs nothing, and opening them again after a quiet
        /// spell could take seconds (a headset waking up), during which no sound could play.
        /// </summary>
        static void KeepOpen()
        {
            _close ??= new Timer(_ =>
            {
                lock (Gate)
                {
                    Release(Warm, stop: true);
                    // stopped, not closed: an idle stream never keeps the PC awake, and the next sound starts at once
                    foreach (var v in Voices.Append(Warm))
                        try { if (v.Hdr != IntPtr.Zero && Done(v)) Release(v, stop: false); if (v.Dev != IntPtr.Zero) waveOutReset(v.Dev); } catch { }
                }
            });
            _close.Change(120_000, Timeout.Infinite);
        }

        /// <summary>The output changed: every stream is closed, to be opened on the new device by the next sound.</summary>
        public static void Reopen() => Task.Run(() =>
        {
            lock (Gate)
                foreach (var v in Voices.Append(Warm))
                    try
                    {
                        Release(v, stop: true);
                        if (v.Dev != IntPtr.Zero) { waveOutClose(v.Dev); v.Dev = IntPtr.Zero; }
                    }
                    catch { }
            OpenVoices(); // and opened again on the new device, before anything needs them
        });

        /// <summary>The voices opened in the background, one after the other (each outside the lock).</summary>
        public static void Prepare() => Task.Run(OpenVoices);

        /// <summary>One stream opens at a time: two at once slowed each other down (1.8, 3.4, 4.8 s on one PC).</summary>
        static readonly SemaphoreSlim Opening = new(1, 1);

        static void OpenVoices()
        {
            try
            {
                foreach (var v in Voices)
                {
                    Opening.Wait();
                    try
                    {
                        bool open; lock (Gate) open = v.Dev != IntPtr.Zero;
                        if (open) continue;
                        var h = OpenHandle();
                        if (h == IntPtr.Zero) return;
                        lock (Gate) { if (v.Dev == IntPtr.Zero) v.Dev = h; else waveOutClose(h); }
                    }
                    finally { Opening.Release(); }
                }
            }
            catch { }
        }

        static bool AnyOpen() { lock (Gate) return Voices.Any(x => x.Dev != IntPtr.Zero); }

        /// <summary>
        /// Gets everything ready now (the pointer came near, the notch opened): the voices open, the device awake.
        /// Each stream is opened outside the lock, so a sound never waits for all of them (a slow device took seconds).
        /// </summary>
        public static void Wake() => Task.Run(() =>
        {
            try
            {
                OpenVoices();
                lock (Gate) { StartWarm(); KeepOpen(); }
            }
            catch { }
        });

        /// <summary>Dev (DYNAMICNOTCH_WAVEPROBE=1): how long a stream takes to open here, a few ways, in the log.</summary>
        public static void Probe()
        {
            string Try(uint dev)
            {
                var fmt = new WaveFormatEx { Tag = 1, Channels = 1, Rate = Rate, BytesPerSec = Rate * 2, Align = 2, Bits = 16 };
                var t = Stopwatch.StartNew();
                int r = waveOutOpen(out var h, dev, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
                long ms = t.ElapsedMilliseconds;
                if (r == 0) waveOutClose(h);
                return $"{(dev == 0xFFFFFFFF ? "mapper" : "dev" + dev)} {ms} ms (r={r})";
            }
            var pool = Task.Run(() => Try(0xFFFFFFFF) + ", " + Try(0xFFFFFFFF) + ", " + Try(0)).Result;
            string own = "";
            var th = new Thread(() => own = Try(0xFFFFFFFF) + ", " + Try(0)) { IsBackground = true };
            th.Start(); th.Join();
            string hi = "";
            var th2 = new Thread(() => hi = Try(0xFFFFFFFF)) { IsBackground = true, Priority = ThreadPriority.Highest };
            th2.Start(); th2.Join();
            AppLog.Write($"wave probe: pool [{pool}] | own thread [{own}] | highest [{hi}]");
        }

        static IntPtr OpenHandle()
        {
            var fmt = new WaveFormatEx { Tag = 1, Channels = 1, Rate = Rate, BytesPerSec = Rate * 2, Align = 2, Bits = 16 };
            var t = Stopwatch.StartNew();
            int r = waveOutOpen(out var h, 0xFFFFFFFF, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
            if (t.ElapsedMilliseconds > 400) AppLog.Write($"sounds: the device took {t.ElapsedMilliseconds} ms to open");
            return r == 0 ? h : IntPtr.Zero;
        }
        public static bool IsOpen { get { lock (Gate) return Voices[0].Dev != IntPtr.Zero; } }

        static bool Done(Voice v)
        {
            if (v.Hdr == IntPtr.Zero) return true;
            try { return (Marshal.PtrToStructure<WaveHeader>(v.Hdr).Flags & WhdrDone) != 0; } catch { return true; }
        }

        static readonly System.Collections.Concurrent.BlockingCollection<(float[] Pcm, long At)> Queue = new();
        static Thread? _thread;
        static readonly object ThreadGate = new();

        /// <summary>Hands the sound to Pixo's own audio thread (always ready: no wait for the thread pool). Never waits:
        /// the screen's thread calls this, and the device's lock can be held for a while by a slow device.</summary>
        public static void Play(float[] pcm)
        {
            if (_thread == null)
                lock (ThreadGate)
                    if (_thread == null)
                    {
                        _thread = new Thread(() =>
                        {
                            foreach (var (s, at) in Queue.GetConsumingEnumerable())
                            {
                                // a click's sound half a second late is worse than none (the device was slow): let it go
                                if (Environment.TickCount64 - at > 450) continue;
                                Write(s);
                            }
                        }) { IsBackground = true, Name = "Pixo sounds", Priority = ThreadPriority.Highest }; // a sound is never late behind the drawing
                        _thread.Start();
                    }
            Queue.Add((pcm, Environment.TickCount64));
        }

        static void Write(float[] pcm)
        {
            bool opener = false;
            if (!AnyOpen())
            {
                opener = Opening.Wait(2500); // a stream is on its way: wait for it, then use it
                if (opener && AnyOpen()) { Opening.Release(); opener = false; }
            }
            try { WriteNow(pcm); }
            finally { if (opener) Opening.Release(); }
        }

        static void WriteNow(float[] pcm)
        {
            lock (Gate)
            {
                try
                {
                    // a free voice (its sound is over), else the one that started first (nearly over by now)
                    var v = Voices.FirstOrDefault(x => x.Dev != IntPtr.Zero && Done(x))
                        ?? Voices.Where(x => x.Dev != IntPtr.Zero).OrderBy(x => x.At).FirstOrDefault()
                        ?? Voices[0];
                    if (!Open(v)) return;
                    KeepOpen();
                    StartWarm(); // the device stays awake for the sounds that follow
                    Release(v, stop: !Done(v));
                    // a soft edge in and out (2 ms): a sound never starts or ends on a jump
                    int edge = Math.Min(pcm.Length / 4, (int)(Rate / 500));
                    var s = new short[pcm.Length];
                    for (int i = 0; i < s.Length; i++)
                    {
                        double g = 0.9;
                        if (i < edge) g *= i / (double)edge;
                        else if (i >= s.Length - edge) g *= (s.Length - 1 - i) / (double)edge;
                        s[i] = (short)Math.Clamp(Math.Tanh(pcm[i]) * g * short.MaxValue, short.MinValue, short.MaxValue);
                    }
                    v.Data = Marshal.AllocHGlobal(s.Length * 2);
                    Marshal.Copy(s, 0, v.Data, s.Length);
                    v.Hdr = Marshal.AllocHGlobal((int)HdrSize);
                    Marshal.StructureToPtr(new WaveHeader { Data = v.Data, Length = (uint)(s.Length * 2) }, v.Hdr, false);
                    if (waveOutPrepareHeader(v.Dev, v.Hdr, HdrSize) != 0 || waveOutWrite(v.Dev, v.Hdr, HdrSize) != 0)
                    {
                        Release(v, stop: true);
                        waveOutClose(v.Dev);
                        v.Dev = IntPtr.Zero;
                        return;
                    }
                    v.At = Stopwatch.GetTimestamp();
                    v.Samples = s.Length;
                }
                catch { }
            }
        }

        static void Release(Voice v, bool stop)
        {
            if (v.Hdr == IntPtr.Zero) return;
            if (stop) waveOutReset(v.Dev);
            waveOutUnprepareHeader(v.Dev, v.Hdr, HdrSize);
            Marshal.FreeHGlobal(v.Hdr);
            Marshal.FreeHGlobal(v.Data);
            v.Hdr = v.Data = IntPtr.Zero;
        }
    }
}
