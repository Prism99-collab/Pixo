using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DynamicNotch.Controls;
using static DynamicNotch.Interop.Native;

namespace DynamicNotch;

/// <summary>
/// The face in the notch (see <see cref="NotchFace"/>): it lives in the idle notch and gives it a little life.
///  • It blinks and glances around, and follows the pointer when it comes near.
///  • You reach for the notch: it smiles up at you. You type: it looks down, focused.
///  • Away for a few minutes: it dozes off, z's drifting out, and says hello when you're back.
///  • Things happening get a reaction: done (a hop and sparks), an alert (surprised), too loud (squeezed shut),
///    low battery (tired), the charger in (hearts). A reaction while the notch is busy waits for it to be idle again.
/// It shows in the idle notch and the music pill (dancing along), and draws nothing while it sits still.
/// </summary>
public partial class NotchWindow
{
    NotchFace? _face;
    readonly DispatcherTimer _faceTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
    readonly ScaleTransform _faceScale = new(1, 1);
    bool _faceShown, _facePeek;
    NotchFace.Emote? _facePending;
    DateTime _facePendingUntil, _faceNextYawn, _faceHelloOn;
    readonly bool[] _faceKeys = new bool[6];
    int _faceKeyAt;
    uint _faceLastInput;
    POINT _faceLastCursor;

    /// <summary>Its pace, changed only when it really changes (setting it restarts the timer: done every tick, the rhythm stuttered).</summary>
    void FaceInterval(int ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        if (_faceTimer.Interval != t) _faceTimer.Interval = t;
    }

    void WireFace()
    {
        _faceTimer.Tick += (_, _) => FaceTick();
        _faceTimer.Start();
        Services.FaceSound.Warm(_cyber, afterMs: 800); // its sounds made ahead of time (right after start): the first hello is instant
        _media.TrackChanged += FaceWake; // a new song: the notch shows it at full size
        WireFacePokes();
    }

    // (while it's out visiting as the companion, it isn't in the notch)
    bool FaceAllowed() => _settings.NotchFace && !_cOut && (_mode is Mode.Idle or Mode.Media || InCallPill) && !_hidden && !Unseen && !_fgFullscreen && !_dodged;

    NotchFace FaceGet()
    {
        if (_face != null) return _face;
        _face = new NotchFace(15)
        {
            Sideways = true, // inside the notch its sparks and z's drift out to the sides
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 3, 0, 0), Opacity = 0, Visibility = Visibility.Collapsed,
            RenderTransform = new TransformGroup { Children = { _faceScale, _faceShift } }, RenderTransformOrigin = new Point(0.5, 0.3),
        };
        _face.SetOutfit(FaceOutfitNow(), animate: false);
        Stage.Children.Insert(0, _face);
        // in the notch it only speaks for the big moments (hello, a poke, dizzy, hearts): never for every little thing
        _face.Voice += what => { if (what is "hello" or "wave" or "poke" or "dizzy" or "love") Services.FaceSound.Say(what, _cyber); };
        return _face;
    }

    /// <summary>The notch changed: out of the idle notch the face is gone at once (it comes back on the next tick).</summary>
    void FaceMode()
    {
        _restWake = DateTime.UtcNow;
        _rest = false;
        if (_faceGreet && _mode is not (Mode.Idle or Mode.Media)) { _faceGreet = false; FaceGrow(1, 0, 1); }
        if (_faceShown && !FaceAllowed()) FaceShow(false);
    }

    void FaceShow(bool on)
    {
        if (on == _faceShown) return;
        _faceShown = on;
        if (_cyber) After(1, () => CyberShape(_sw.Value, _sh.Value, _sr.Value)); // the island's top notch comes and goes with it
        var face = FaceGet();
        if (on)
        {
            face.Visibility = Visibility.Visible;
            face.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)));
            // it pops in, a little springy
            var pop = new DoubleAnimation(0.78, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut } };
            if (!Eco) { _faceScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop); _faceScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop); }
            // the first time after starting, and the first time each day: it comes out to say hello
            var today = DateTime.Today;
            // Pixo just started: its own quiet welcome (NotchWindow.Welcome); later, the first time each day: the wave
            // once a day, not at every start (Pixo restarting for an update or a crash doesn't say hello again)
            bool greetedToday = _settings.FaceHelloDay == today.ToString("yyyy-MM-dd");
            if (!_faceGreetedStart)
            {
                _faceHelloOn = today; _faceGreetedStart = true;
                if (!greetedToday) { NoteHello(); After(500, () => { if (!StartWelcome()) FaceGreet(); }); }
            }
            else if (_faceHelloOn != today) { _faceHelloOn = today; if (!greetedToday) { NoteHello(); After(700, FaceGreet); } }
            return;
        }
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(110));
        fade.Completed += (_, _) => { if (!_faceShown) face.Visibility = Visibility.Collapsed; };
        face.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>A reaction: now if it's showing, else when it's next back (within a few seconds).</summary>
    void FaceReact(NotchFace.Emote e, double secs = 1.8)
    {
        if (!_settings.NotchFace) return;
        if (_faceShown && _face != null) { _face.Play(e, secs); return; }
        _facePending = e;
        _facePendingUntil = DateTime.UtcNow.AddSeconds(8);
    }

    /// <summary>The notch shows a call (its small pill): the face joins it.</summary>
    bool InCallPill => _mode == Mode.Activity && CurrentActivity == LiveActivity.Call;
    bool _faceInCall;

    /// <summary>
    /// In a call, the face follows it: excited when it starts; talking (its mouth moving with your voice) while you
    /// speak; looking over at the caller and nodding while they speak; zipped and red while you're muted.
    /// </summary>
    void FaceCall(NotchFace face)
    {
        if (!_faceInCall) { _faceInCall = true; face.Play(NotchFace.Emote.Excited, 1.6); }
        float mic = 0, left = 0, right = 0;
        bool muted = CallMuted;
        if (!muted) _audio.TryGetMicPeak(out mic);
        _audio.TryGetPeaks(out left, out right);
        float them = Math.Max(left, right);
        var mood = muted ? NotchFace.Mood.Muted : mic > 0.05f ? NotchFace.Mood.Talk : them > 0.04f ? NotchFace.Mood.Listen : NotchFace.Mood.Idle;
        face.SetMood(mood);
        face.Level = mood == NotchFace.Mood.Talk ? Math.Sqrt(mic) : mood == NotchFace.Mood.Listen ? Math.Sqrt(them) : 0;
    }

    /// <summary>
    /// The notch's colour for the face's soft wash: the song's (its more colourful cover colour) while music plays,
    /// else the theme's; softened (bright, never loud). Neon keeps its own look.
    /// </summary>
    Color? FaceVibe(bool playing)
    {
        if (_cyber) return null;
        Color c = PalAccent();
        if (playing)
        {
            var song = Chroma(_waveA) >= Chroma(_waveB) ? _waveA : _waveB;
            if (Chroma(song) >= 0.18) c = song;
        }
        // softened: bright enough to read on the light body, never fully saturated
        double max = Math.Max(c.R, Math.Max(c.G, c.B)) / 255.0, min = Math.Min(c.R, Math.Min(c.G, c.B)) / 255.0;
        if (max <= 0.01) return null;
        double s = Math.Min(0.62, (max - min) / max), v = Math.Max(0.86, max);
        double r = c.R / 255.0 / max, g = c.G / 255.0 / max, b = c.B / 255.0 / max; // the hue, at full brightness
        double sat0 = (max - min) / max;
        byte Ch(double x) => (byte)Math.Round(Math.Clamp(v * (1 - s + s * (sat0 <= 0 ? 1 : (x - (1 - sat0)) / sat0)), 0, 1) * 255);
        return Color.FromRgb(Ch(r), Ch(g), Ch(b));
        static double Chroma(Color k) { double hi = Math.Max(k.R, Math.Max(k.G, k.B)), lo = Math.Min(k.R, Math.Min(k.G, k.B)); return hi <= 0 ? 0 : (hi - lo) / hi; }
    }

    void FaceTick()
    {
        GetCursorPos(out var cur);
        RestTick(cur);
        bool allowed = FaceAllowed();
        if (!allowed) { if (_faceShown) FaceShow(false); FaceInterval(400); return; }
        var face = FaceGet();
        face.Neon = _cyber;
        face.Visor = false; // Neon too: the whole face (its Neon bun: a dark body, a neon outline, glowing eyes), not the visor's eyes alone
        Services.FaceSound.Enabled = _settings.FaceSounds;
        face.Accent = PalAccent();
        face.Eco = Eco;
        bool playing = _mode == Mode.Media && _media.IsPlaying;
        face.Vibe = FaceVibe(playing);
        // the music's loudness and punch, when the meter has them (else it dances at its own pace)
        if (playing && _eqRunning && !_eqFallback) { face.Groove = _waveLoud; face.Pulse = _eqLevel[1]; }
        else face.Groove = -1;
        if (!_faceShown)
        {
            FaceShow(true);
            if (_facePending is { } waiting && DateTime.UtcNow < _facePendingUntil) face.Play(waiting);
            _facePending = null;
        }
        FaceInterval(Eco ? 400 : 100);
        face.SetOutfit(FaceOutfitNow());
        // typing: input keeps coming while the pointer stays put (which keys is never looked at)
        var li = new LASTINPUTINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<LASTINPUTINFO>() };
        bool input = GetLastInputInfo(ref li) && _faceLastInput != 0 && li.dwTime != _faceLastInput;
        bool still = cur.X == _faceLastCursor.X && cur.Y == _faceLastCursor.Y;
        _faceLastInput = li.dwTime;
        _faceLastCursor = cur;
        _faceKeys[_faceKeyAt++ % _faceKeys.Length] = input && still;
        int keys = _faceKeys.Count(k => k);

        double idle = IdleTime().TotalSeconds;
        if (InCallPill) { FaceCall(face); return; }
        _faceInCall = false;
        // with music on it dances along (and never dozes off)
        bool music = _mode == Mode.Media && _media.IsPlaying;
        bool woke = face.Current == NotchFace.Mood.Sleep && idle < 60;
        var mood = music ? NotchFace.Mood.Music : idle >= 60 ? NotchFace.Mood.Sleep : keys >= 3 ? NotchFace.Mood.Focus : NotchFace.Mood.Idle;
        if (!music && face.Current == NotchFace.Mood.Focus && mood == NotchFace.Mood.Idle && keys > 0) mood = NotchFace.Mood.Focus; // a pause between words
        face.SetMood(mood);
        if (woke) FaceGreet(); // you're back: it comes out to say hello
        if (mood == NotchFace.Mood.Sleep) return;

        // you reach for the notch: it smiles up at you
        if (_peek && !_facePeek) face.Play(NotchFace.Emote.Happy, 1.2);
        _facePeek = _peek;

        // its eyes follow the pointer when it's near
        if (!Eco && !still)
        {
            double fx = _winLeft + _winW / 2.0, fy = _winTop + 13 * _scale;
            double dx = (cur.X - fx) / _scale, dy = (cur.Y - fy) / _scale;
            if (Math.Abs(dx) < 700 && dy < 500) face.LookAt(dx / 260, -dy / 160);
        }

        // late at night, now and then a yawn
        int hour = DateTime.Now.Hour;
        if ((hour >= 23 || hour < 4) && !Eco)
        {
            var now = DateTime.UtcNow;
            if (_faceNextYawn == default) _faceNextYawn = now.AddSeconds(40);
            else if (now >= _faceNextYawn) { _faceNextYawn = now.AddSeconds(70 + Random.Shared.NextDouble() * 80); face.Play(NotchFace.Emote.Yawn, 2.4); }
        }
    }

    /// <summary>The companion's moments, as the face feels them.</summary>
    void FaceFromAct(Companion.Act act)
    {
        NotchFace.Emote? e = act switch
        {
            Companion.Act.Pump => NotchFace.Emote.Done,
            Companion.Act.Point => NotchFace.Emote.Surprised,
            Companion.Act.Ears => NotchFace.Emote.Annoyed,
            Companion.Act.Tired => NotchFace.Emote.Tired,
            Companion.Act.Cheer => NotchFace.Emote.Love,
            Companion.Act.Strain => NotchFace.Emote.Dizzy,
            _ => null,
        };
        if (e is { } emote) FaceReact(emote, emote is NotchFace.Emote.Tired or NotchFace.Emote.Dizzy ? 3 : 1.8);
    }

    // ───────────────────────────── the greeting: it pops out of the notch and waves ─────────────────────────────

    readonly TranslateTransform _faceShift = new();
    bool _faceGreet, _faceGreetedStart;

    /// <summary>The notch opens a little, the face comes out big, waves hello, and tucks back in.</summary>
    void NoteHello() { _settings.FaceHelloDay = DateTime.Today.ToString("yyyy-MM-dd"); _settings.Save(); }

    /// <summary>
    /// Hello: a wave right where it lives, a little springy pop, its voice with it at once (the speakers are woken
    /// first). It no longer steps out below the notch (half of it was cut off by the island's edge).
    /// </summary>
    void FaceGreet()
    {
        if (_face is not { } face || !_faceShown) return;
        if (_settings.FaceSounds) Services.SoundOut.Wake();
        face.Play(NotchFace.Emote.Wave, 2.2);
        if (Eco) return;
        FaceGrow(1.22, 0, 260);
        After(1700, () => FaceGrow(1, 0, 320));
    }

    void FaceGrow(double k, double y, int ms)
    {
        IEasingFunction ease = k > 1 ? new BackEase { Amplitude = 0.2, EasingMode = EasingMode.EaseOut } : new CubicEase { EasingMode = EasingMode.EaseInOut };
        var t = TimeSpan.FromMilliseconds(ms);
        _faceScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(k, t) { EasingFunction = ease });
        _faceScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(k, t) { EasingFunction = ease });
        _faceShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, t) { EasingFunction = ease });
    }

    /// <summary>The notch's shape while the face says hello: a little room for it below.</summary>
    bool FaceGreetShape(ref double w, ref double h, ref double r)
    {
        if (!_faceGreet || _mode is not (Mode.Idle or Mode.Media)) return false;
        w = Math.Max(w, 190); h = 66; r = 26;
        return true;
    }

    // ───────────────────────────── poking it, and its hats ─────────────────────────────

    bool _faceClicks;

    /// <summary>The pointer is right on the face: the notch takes that click (a poke) instead of letting it through.</summary>
    void FacePointer(POINT p)
    {
        bool on = _faceShown && _face != null && !_touchMode && IsBase(_mode) && !_settings.Locked && FaceHit(p);
        if (on == _faceClicks) return;
        _faceClicks = on;
        if (on && _settings.FaceSounds) Services.SoundOut.Wake(); // a poke's sound starts with the click
        if (on) SetClickThrough(false); else RefreshClickThrough();
    }

    bool FaceHit(POINT p)
    {
        double k = _faceScale.ScaleX;
        double cx = _winLeft + _winW / 2.0 + _leanShift.X * _scale;
        double bottom = _winTop + (3 + IslandGap * Isle + _faceShift.Y + 21 * k) * _scale;
        return Math.Abs(p.X - cx) <= 13 * k * _scale && p.Y >= _winTop && p.Y <= bottom;
    }

    void WireFacePokes()
    {
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (!_faceClicks || _face == null) return;
            e.Handled = true;
            FaceWake();
            _face.Poke();
        };
        PreviewMouseLeftButtonUp += (_, e) => { if (_faceClicks) e.Handled = true; };
        PreviewMouseRightButtonDown += (_, e) => { if (_faceClicks) e.Handled = true; };
        PreviewMouseRightButtonUp += (_, e) => { if (!_faceClicks) return; e.Handled = true; FaceNextOutfit(); };
    }

    /// <summary>The hat it wears: yours, or with "auto" the season's (Christmas, New Year, Halloween).</summary>
    string FaceOutfitNow()
    {
        var o = _settings.FaceOutfit ?? "auto";
        if (o != "auto") return o;
        var d = DateTime.Today;
        if (d.Month == 12 && d.Day <= 26) return "santa";
        if (d.Month == 12 && d.Day == 31 || d.Month == 1 && d.Day == 1) return "party";
        if (d.Month == 10 && d.Day >= 24) return "witch";
        return "none";
    }

    /// <summary>A right-click on the face: the next hat.</summary>
    void FaceNextOutfit()
    {
        if (_face == null) return;
        var list = NotchFace.Outfits;
        string next = list[(Array.IndexOf(list, _face.Outfit) + 1) % list.Length];
        _settings.FaceOutfit = next;
        _settings.Save();
        _face.SetOutfit(next);
    }

    // ───────────────────────────── resting: unused for 5 s, the notch tucks in small ─────────────────────────────

    bool _rest;
    DateTime _restWake = DateTime.UtcNow;

    void RestTick(POINT cur)
    {
        var now = DateTime.UtcNow;
        bool can = _settings.CompactWhenResting && _mode is Mode.Idle or Mode.Media && !_faceGreet && _welcomePhase == 0 && !_hidden && !_peek;
        double cx = _winLeft + _winW / 2.0, half = (_sw.Value / 2 + 90) * _scale;
        bool near = Math.Abs(cur.X - cx) < half && cur.Y - _mon.Top < 130 * _scale;
        PointerNear(near && IsBase(_mode));
        if (near || !can) _restWake = now;
        bool rest = can && (now - _restWake).TotalSeconds >= 5;
        if (rest == _rest) return;
        _rest = rest;
        ApplyShape();
        UpdateRim(); // the glow below fits the new size
    }

    /// <summary>Something happened (the pointer came, the song changed, the notch changed): back to full size.</summary>
    void FaceWake()
    {
        _restWake = DateTime.UtcNow;
        if (!_rest) return;
        _rest = false;
        ApplyShape();
        UpdateRim();
    }
}
