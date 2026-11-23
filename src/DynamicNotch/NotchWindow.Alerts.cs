using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using DynamicNotch.Controls;
using DynamicNotch.Services;
using static DynamicNotch.Interop.Native;

namespace DynamicNotch;

/// <summary>
/// Everything the notch reacts to: live activities, clickable pop-ups (with a priority queue) and the mic/camera bubble.
/// </summary>
public partial class NotchWindow
{
    enum LiveActivity { None, Media, Download, Timer, Focus, Call, Game }
    enum Prio { Low, Normal, High }

    /// <summary>One pop-up. <see cref="OnClick"/> makes it clickable; <see cref="OnWheel"/> lets the wheel adjust it.</summary>
    sealed record OsdRequest(
        string Kind, string? Glyph, double? Level, string? Title, string? Subtitle, string? Right,
        ImageSource? Art, Brush? GlyphBrush, (double Level, bool Charging)? Battery,
        double Seconds, bool Force, Prio Priority, Action? OnClick, Action<int>? OnWheel, Motion Motion = Motion.Auto, DateTime Queued = default, ToastLook? Look = null,
        bool MustShow = false, int Merged = 0, string? Replaces = null);

    static readonly FontFamily SymbolFont = new("Segoe UI Symbol, Segoe UI");

    bool _alertsReady;
    string? _lastNetwork;
    int _batteryAlertedAt = 101;
    bool? _capsLock;
    bool _privacyShown;
    readonly Dictionary<IntPtr, DateTime> _flashSeen = new();
    (string App, DateTime At) _lastToast;
    uint _fmtExclude, _fmtIgnore;

    OsdRequest? _osd;
    DateTime _osdShownAt;
    readonly List<OsdRequest> _osdQueue = new();
    Point _pressAt;

    LiveActivity CurrentActivity => Activities() is { Count: > 0 } a ? a[0] : LiveActivity.None;

    void WireAlerts()
    {
        _notificationSound.Choice = _settings.AlertSound; // the sound picked in Preferences
        _fmtExclude = RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
        _fmtIgnore = RegisterClipboardFormat("Clipboard Viewer Ignore");
        WireOsdInput();

        _media.Changed += OnMediaChanged;
        _media.TrackChanged += () =>
        {
            if (!_media.IsPlaying || !_settings.ShowMediaActivity) return;
            if (PlayerInFront()) return; // you're in the player's own window: you can see the song already
            // over a window you're working in: brief, so it's gone before you reach for a tab
            ShowOsd("media", null, null, _media.Title, _media.Artist, null, art: _media.Art, seconds: _overWindow ? 1.6 : 2.4, prio: Prio.Low,
                onClick: () => ExpandTo(0));
        };

        _audio.VolumeChanged += () =>
        {
            if (_mode == Mode.Expanded) { RefreshVolume(); return; }
            if (!RecentlyTouched) ShowVolumeOsd();
        };
        _audio.OutputChanged += () =>
        {
            RefreshVolume();
            if (_mode == Mode.Expanded) RefreshAppVolume(); // the player's sound moved to the new device
            if (_mode != Mode.Expanded)
                ShowOsd("output", _audio.OutputIsHeadphones ? "" : "", null, _audio.OutputName, "Sound output · click to change", null,
                    onClick: () => { ExpandTo(0); ShowDevices(true); });
        };
        _audio.Refreshed += () => { RefreshVolume(); RefreshToggles(); };
        _audio.MicChanged += () =>
        {
            RefreshToggles();
            if (_mode != Mode.Expanded)
                ShowOsd("mic", _audio.MicMuted ? "" : "", null, _audio.MicMuted ? "Microphone muted" : "Microphone on", null, null,
                    glyphBrush: _audio.MicMuted ? Red : null, prio: Prio.Low, onClick: () => { _audio.ToggleMic(); });
        };

        _bri.Changed += external =>
        {
            if (_mode == Mode.Expanded) { RefreshBrightness(); return; }
            if (external && !RecentlyTouched) ShowBrightnessOsd();
        };

        _radio.Changed += () =>
        {
            // Bluetooth off: nothing is connected; back on: what's connected is read again from Windows
            if (_radio.HasBluetooth && _radio.BluetoothOn != _btWasOn) { _btWasOn = _radio.BluetoothOn; if (!_btWasOn) _bt.RadioOff(); else _ = _bt.ResyncAsync(); }
            RefreshToggles();
            OnNetworkMaybeChanged();
        };
        _focus.Changed += OnFocusChanged;

        _bt.Changed += RefreshToggles;
        _bt.ConnectionChanged += (d, connected) =>
            ShowOsd("bt", BluetoothService.GlyphFor(d), null, d.Name,
                !connected ? "Disconnected" : d.Battery is { } b ? $"Connected · {b}% battery" : "Connected",
                connected && d.Battery is { } pct ? $"{pct}%" : null,
                glyphBrush: connected ? Blue : null, seconds: 2.6, onClick: () => Launch("ms-settings:bluetooth"),
                motion: connected ? Motion.Auto : Motion.Tremble);
        _bt.BatteryLow += d =>
            ShowOsd("bt-battery", BluetoothService.GlyphFor(d), null, $"{d.Name} battery low",
                $"{d.Battery}% left · charge it soon", $"{d.Battery}%", glyphBrush: Orange, seconds: 5, prio: Prio.High,
                onClick: () => Launch("ms-settings:bluetooth"), motion: Motion.Tremble);

        _power.Changed += () => { RefreshBattery(); CheckBatteryLow(); ApplyPowerProfile(); };
        _power.PluggedChanged += plugged =>
        {
            ApplyPowerProfile();
            _batteryAlertedAt = 101;
            ShowOsd("power", null, null, plugged ? "Charging" : "On battery", plugged ? null : RemainingHint(), $"{_power.Percent}%",
                battery: (_power.Percent / 100.0, plugged), seconds: 2.2, onClick: () => ExpandTo(1));
        };

        _privacy.Changed += OnPrivacyChanged;

        _files.DownloadsChanged += RefreshBase;
        _files.DownloadCompleted += async path =>
        {
            RefreshBase();
            if (!_settings.SystemAlerts) return;
            // the shell icon and file size come from disk: fetch them off the UI thread
            var (icon, size) = await Task.Run(() => (IconHelper.ForPath(path), SafeLength(path)));
            PalReact(Controls.Companion.Act.Pump, 2.6); // the companion: a fist pump
            ShowOsd("download", null, null, "Download complete", Path.GetFileName(path), SystemMonitor.Bytes(size),
                art: icon, seconds: 4, prio: Prio.High, onClick: () => OpenPath(path));
        };
        _files.DownloadFailed += name =>
        {
            RefreshBase();
            if (_settings.SystemAlerts)
                ShowOsd("download-failed", "", null, "Download cancelled or failed", name, null, glyphBrush: Red, seconds: 3,
                    onClick: () => { if (_files.DownloadsDir != null) Launch(_files.DownloadsDir); });
        };
        _files.ScreenshotSaved += async path =>
        {
            _lastScreenshotAt = DateTime.UtcNow;
            if (!_settings.SystemAlerts) return;
            // decoding a full-size screenshot takes 50–150 ms: never on the UI thread
            var thumb = await Task.Run(() => IconHelper.Thumbnail(path, 96));
            ShowOsd("screenshot", null, null, Loc.T("Screenshot"), Loc.T("Saved and copied"), null,
                art: thumb, seconds: 3, onClick: () => OpenPath(path));
        };

        _notifications.Received += t =>
        {
            if (NotifHistory.IsMuted(t.App)) return; // set to Off in Notifs & alerts: not shown, not kept (not even for the AI)
            KeepToast(t); // for the AI ("what did I miss?"): the last few, in memory only
            if (!_settings.NotificationAlerts) return;
            if (TryRingFromToast(t)) return; // "Incoming call": shown as ringing, not as a message
            if (RingHolds(t)) return; // the ringing app's other notifications: its ring card stays in front
            // Windows' "screenshot copied" note: Pixo shows the screenshot itself (with its picture), one pop-up is enough
            if (_settings.SystemAlerts && IsScreenshotTool(t)) return;
            if (AppMuted(t.App)) return; // muted for an hour from its card (still kept above for "what did I miss?")
            if (_focus.IsOn) { _focus.Hold(t.App); return; } // kept for the end-of-focus summary
            if (_inMeeting || _hidden || _mode == Mode.Expanded) return;
            var (drop, show) = TidyToast(t); // repeats dropped, wordy ones in a few words (NotchWindow.AlertTidy)
            if (drop) return;
            bool rushing = AppRushing(t.App); // (counted once per notification)
            // calm: an offer, a flood, an app set to Quiet don't pop up; the face glances, and it waits in Notifs & alerts
            if (QuietToast(t, rushing)) { FaceGlance(t); return; }
            // a burst of messages rings once, not ten times in a row
            bool ring = ToastSounds(t, rushing); // not for an offer or an app that floods; always for urgent or for you (NotchWindow.AlertCard)
            if ((DateTime.UtcNow - _lastToast.At).TotalSeconds > 2 && ring) _notificationSound.Play();
            _lastToast = (t.App, DateTime.UtcNow);
            PalReact(Controls.Companion.Act.Point, 2.8); // the companion points up at it
            if (show != null) show(); else ShowToast(t); // told the short way, in the app's colour (NotchWindow.AlertStory)
        };
    }

    /// <summary>The Snipping Tool (any language: known by its app id, not its name).</summary>
    static bool IsScreenshotTool(ToastInfo t) =>
        t.Aumid is { } id && (id.Contains("ScreenSketch", StringComparison.OrdinalIgnoreCase) || id.Contains("SnippingTool", StringComparison.OrdinalIgnoreCase));

    static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    string? RemainingHint()
    {
        if (!GetSystemPowerStatus(out var s) || s.BatteryLifeTime <= 0) return null;
        var t = TimeSpan.FromSeconds(s.BatteryLifeTime);
        return $"About {(t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : $"{t.Minutes} min")} left";
    }

    // ───────────────────────────── actions ─────────────────────────────

    void ExpandTo(int page)
    {
        GoToPage(page, instant: true);
        Expand(byHotkey: false);
    }

    void OpenPath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return;
        // never run a freshly downloaded program from a click on a banner: show it instead
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".exe" or ".msi" or ".bat" or ".cmd" or ".ps1" or ".vbs" or ".js" or ".scr" or ".com")
            Launch("explorer.exe", $"/select,\"{path}\"");
        else Launch(path);
    }

    void OpenApp(string? aumid)
    {
        if (string.IsNullOrEmpty(aumid)) return;
        Launch("explorer.exe", $"shell:AppsFolder\\{aumid}");
    }

    static void FocusWindow(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, 9 /* SW_RESTORE */);
        SwitchToThisWindow(hwnd, true);
    }

    void ShowVolumeOsd()
    {
        double v = _audio.Muted ? 0 : _audio.Volume;
        if (v >= 0.995 && _pal is { Current: not Controls.Companion.Act.Ears }) PalReact(Controls.Companion.Act.Ears, 2.2); // too loud for her
        ShowOsd("volume", VolumeGlyph(), v, null, null, $"{(int)Math.Round(v * 100)}", prio: Prio.Low,
            onClick: () => ExpandTo(0),
            onWheel: d =>
            {
                // already at the end: a little bump says "that's as far as it goes"
                if (d > 0 ? !_audio.Muted && _audio.Volume >= 0.995f : _audio.Muted || _audio.Volume <= 0.005f) { Bump(d); return; }
                _lastUserChange = DateTime.UtcNow; _audio.SetVolume(_audio.Volume + d * 0.04f); ShowVolumeOsd();
            });
    }

    void ShowBrightnessOsd()
    {
        ShowOsd("brightness", "", _bri.Level / 100.0, null, null, $"{_bri.Level}", prio: Prio.Low,
            onClick: () => ExpandTo(0),
            onWheel: d =>
            {
                if (d > 0 ? _bri.Level >= 100 : _bri.Level <= 0) { Bump(d); return; }
                _lastUserChange = DateTime.UtcNow; _bri.Set(_bri.Level + d * 5); ShowBrightnessOsd();
            });
    }

    // ───────────────────────────── live activities ─────────────────────────────

    void UpdateActivityContent()
    {
        CyberSkinSoon();
        if (_secondShown != LiveActivity.None) FillSecond(_secondShown);
        CyberProgress(null, default); // set again below for what has a progress (Neon)
        if (CurrentActivity == LiveActivity.Call) { FillCallPill(); return; }
        ResetCallPill();
        switch (CurrentActivity)
        {
            case LiveActivity.Timer:
                var left = TimerShown;
                if (left < TimeSpan.Zero) left = TimeSpan.Zero;
                ActGlyph.Text = "";
                ActGlyph.Foreground = ActText.Foreground = TimerBrush;
                SetDigits(ActText, FormatTimer(left));
                CyberProgress(!_stopwatch && _timerTotal.TotalSeconds > 0 ? left.TotalSeconds / _timerTotal.TotalSeconds : null, CyYellow);
                break;
            case LiveActivity.Focus:
                ActGlyph.Text = "";
                ActGlyph.Foreground = ActText.Foreground = FocusPurple;
                int mins = (int)_focus.Elapsed.TotalMinutes;
                ActText.Text = mins < 1 ? "Focus" : mins < 60 ? $"Focus {mins} min" : $"Focus {mins / 60} h {mins % 60:00}";
                break;
            case LiveActivity.Download:
                ActGlyph.Text = "";
                ActGlyph.Foreground = ActText.Foreground = Blue;
                string count = _files.ActiveDownloads > 1 ? $"{_files.ActiveDownloads} · " : "";
                if (_files.Items.FirstOrDefault()?.Progress is { } share) count += $"{share * 100:0}% · ";
                ActText.Text = _files.BytesPerSecond > 1024
                    ? $"{count}{SystemMonitor.Bytes(_files.BytesPerSecond)}/s"
                    : $"{count}{SystemMonitor.Bytes(_files.ActiveBytes)}";
                CyberProgress(_files.Items.FirstOrDefault()?.Progress, CyCyan);
                break;
        }
    }

    // ───────────────────────────── pop-ups ─────────────────────────────

    void ShowOsd(string kind, string? glyph, double? level, string? title, string? subtitle, string? right,
        ImageSource? art = null, Brush? glyphBrush = null, (double Level, bool Charging)? battery = null,
        double seconds = 1.8, bool force = false, Prio prio = Prio.Normal, Action? onClick = null, Action<int>? onWheel = null, Motion motion = Motion.Auto,
        ToastLook? look = null, bool mustShow = false)
    {
        KeepAlert(kind, title, subtitle); // the ones worth looking back on, in Notifs & alerts (NotchWindow.AlertCalm)
        seconds = CalmSeconds(kind, level, force, seconds);
        var req = new OsdRequest(kind, glyph, level, title, subtitle, right, art, glyphBrush, battery, seconds, force, prio, onClick, onWheel, motion, Look: look, MustShow: mustShow);
        if (level == null && kind is not ("media" or "volume" or "brightness")) RainFreeze(); // a message: the rain holds its breath
        if (SilencedByMeeting(kind, force)) return;
        if (InGame && kind != "battery") return; // nothing pops up in the middle of a game
        if (_focus.IsOn && !force && !FocusAllows(kind)) return;
        if (_hidden && !force) return;
        if (_presenter && !force && kind is not ("call" or "call-end" or "volume" or "brightness")) return; // presenting: your screen is theirs
        // a file is being dragged into the drop zone: don't yank it away, show this right after
        if (_mode == Mode.Drop) { Enqueue(req); return; }
        if (_mode == Mode.Expanded)
        {
            if (!force) return;
            Collapse();
        }

        if (Together(ref req)) return; // the same app's news at once: told on the one on screen (NotchWindow.OsdTogether)
        if (_mode == Mode.Osd && _osd != null && _osd.Kind != kind && _osdHover && level == null && !force && !Sticky(_osd)) { Enqueue(req); return; } // you're reading this one
        if (_mode == Mode.Osd && _osd != null && _osd.Kind != kind)
        {
            double elapsed = (DateTime.UtcNow - _osdShownAt).TotalSeconds;
            bool fresh = elapsed < _osd.Seconds * 0.7;
            // something more important is on screen: wait in line (level pop-ups never wait — you're pressing keys)
            if (fresh && prio < _osd.Priority && level == null) { Enqueue(req); return; }
            // an important pop-up got interrupted early: show it again afterwards
            // (not when it's the same app's news, now told on the new one)
            if ((fresh || Sticky(_osd)) && _osd.Priority >= Prio.Normal && !_osdHover && req.Replaces != _osd.Kind)
                Enqueue(_osd with { Seconds = Math.Max(Sticky(_osd) ? 3 : 1.6, _osd.Seconds - elapsed), Motion = Motion.None }, front: true);
        }
        Present(req);
    }

    /// <summary>Cards with buttons you need in time: a ringing call (Answer, Decline) and a call that just began (End call).</summary>
    static bool Sticky(OsdRequest r) => r.Kind is "ring" or "call";

    void Enqueue(OsdRequest req, bool front = false)
    {
        if (req.Queued == default) req = req with { Queued = DateTime.UtcNow };
        _osdQueue.RemoveAll(q => q.Kind == req.Kind);
        if (front) _osdQueue.Insert(0, req); else _osdQueue.Add(req);
        while (_osdQueue.Count > 4) _osdQueue.RemoveAt(_osdQueue.FindIndex(q => q.Priority == _osdQueue.Min(x => x.Priority)));
    }

    void Present(OsdRequest r)
    {
        var previous = _mode == Mode.Osd ? _osd : null;
        bool sameKind = previous != null && previous.Kind == r.Kind;
        var shot = CardSnapshot(previous, r, sameKind); // a run of messages: the last card is pushed down (NotchWindow.AlertCard)
        int countBefore = previous?.Look?.Count ?? 0;
        _osd = r;
        _osdShownAt = DateTime.UtcNow;

        bool hasArt = r.Art != null;
        bool symbol = r.Glyph is { Length: > 0 } && r.Glyph[0] < ''; // e.g. ⇪ needs a normal symbol font
        OsdGlyph.Text = r.Glyph ?? "";
        OsdGlyph.FontFamily = symbol ? SymbolFont : (FontFamily)FindResource("IconFont");
        OsdGlyph.FontSize = symbol ? 17 : 15;
        OsdGlyph.Foreground = r.GlyphBrush ?? Brushes.White;
        OsdGlyph.Visibility = !hasArt && r.Battery == null && r.Glyph != null ? Visibility.Visible : Visibility.Collapsed;
        OsdArt.Background = hasArt ? IconBrush(r.Art) : null;
        OsdArt.Visibility = hasArt ? Visibility.Visible : Visibility.Collapsed;
        OsdBattery.Visibility = r.Battery != null ? Visibility.Visible : Visibility.Collapsed;
        if (r.Battery is { } b) OsdBattery.Set(b.Level, b.Charging, false);
        OsdIconHost.Width = OsdIconHost.Height = hasArt ? 30 : r.Battery != null ? 26 : 22;

        bool isLevel = r.Level.HasValue;
        OsdBar.Visibility = isLevel ? Visibility.Visible : Visibility.Collapsed;
        if (isLevel)
        {
            // glide the bar instead of jumping between key presses
            double to = Math.Clamp(r.Level!.Value, 0, 1);
            if (sameKind) OsdBar.BeginAnimation(PillSlider.ValueProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            else { OsdBar.BeginAnimation(PillSlider.ValueProperty, null); OsdBar.Value = to; }
        }
        OsdTextPanel.Visibility = isLevel ? Visibility.Collapsed : Visibility.Visible;
        string titleBefore = OsdTitle.Text;
        OsdTitle.Text = _cyber && r.Kind == "ring" ? Loc.T("INCOMING CALL") : r.Title ?? "";
        bool freshAlert = OsdTitle.Text != titleBefore;
        if (freshAlert) CyberDecode(OsdTitle, cursor: true);
        CyberSkinSoon(); // the pop-up's colours and art in the theme
        OsdSub.Text = r.Subtitle ?? "";
        if (freshAlert) CyberAlertIn(); // the cyber skin's entrance: scan wipe, hologram icon, decoding line
        if (freshAlert && !_cyber && !r.Level.HasValue && shot == null) OsdWordsIn(!string.IsNullOrEmpty(r.Subtitle)); // (a pushed card has its own drop-in)
        OsdSub.Visibility = string.IsNullOrEmpty(r.Subtitle) ? Visibility.Collapsed : Visibility.Visible;
        OsdRight.Text = r.Right ?? "";

        CyberHolo(r.Kind == "ring"); // Neon: a ringing call is a holocall

        // a different pop-up replacing this one: slide the new content in instead of a hard swap
        if (previous != null && !sameKind)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            OsdInner.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            OsdInnerShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
        }

        TintOsd(r.Level.HasValue ? null : AccentFor(r));
        // the number or count on the side wears the pop-up's colour
        OsdRight.Foreground = _osdAccent is { } ac && !r.Level.HasValue ? Frozen(Lighten(ac, 0.25)) : Frozen(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF));
        CardDress(r, sameKind, countBefore); // avatar, count, tag (NotchWindow.AlertCard)
        CardPush(shot);
        DuskDress(r, sameKind); // Dusk: "done" banners, songs tuning in like a radio (NotchWindow.DuskLife)

        FaceInOsd(r, sameKind); // Pixo's face rides along, feeling it (NotchWindow.FaceOsd)
        MergedOnPresent(r, sameKind); // the same app's other news, counted on it (NotchWindow.OsdTogether)
        OsdCompactFit(r);       // one line, or two for a person (NotchWindow.OsdCompact)
        OsdCountdownStart(r);   // the line that shows how long it stays (NotchWindow.OsdCountdown)
        _osdTimer.Stop();
        _osdTimer.Interval = TimeSpan.FromSeconds(r.Seconds);
        if (!_osdHover) _osdTimer.Start();
        if (_hidden) Fade(Shell, 1, 150);
        SetMode(Mode.Osd);
        UpdateAura(_osdAccent);
        // the pointer is already on it (you clicked the one before, or kept reading): it stays open and held
        if (_osdHover) { CardOpen(); ApplyShape(); OsdCountdownHold(true); }
        else if (r.OnClick != null && Pill.IsMouseOver) { _osdDwell.Stop(); _osdDwell.Start(); }
        // a ringing call and a call's End call: their buttons are there from the start, every time it shows
        else if (Sticky(r) && r.Look?.Actions is { Count: > 0 }) After(300, () => { if (_mode == Mode.Osd && ReferenceEquals(_osd, r) && !_osdHover) { CardOpen(); ApplyShape(); } });
        if (sameKind) return;
        PlayMotion(ResolveMotion(r.Kind, r.Motion));
        // good news: once the pop-up has grown, a band of light sweeps across it in its colour
        if (IsGoodNews(r.Kind, r.GlyphBrush, r.Battery))
            After(360, () => { if (_mode == Mode.Osd && ReferenceEquals(_osd, r)) PlayShine(_osdAccent ?? Colors.White); });
    }

    // ───────────────────────────── colour ─────────────────────────────

    static readonly Brush FocusPurple = Frozen(Color.FromRgb(0x8E, 0x8C, 0xFF));

    /// <summary>Each pop-up's own colour: its icon colour (Teams purple, WhatsApp green...) or its glyph colour.</summary>
    static Color? AccentFor(OsdRequest r)
    {
        if (r.Battery is { } b) return b.Charging ? Color.FromRgb(0x30, 0xD1, 0x58) : b.Level <= 0.2 ? Color.FromRgb(0xFF, 0x45, 0x3A) : null;
        if (r.GlyphBrush is SolidColorBrush s && s.Color != Colors.White) return s.Color;
        if (r.Art != null) return IconHelper.Accent(r.Art) is { } ic ? Vivid(ic) : null;
        return null;
    }

    /// <summary>A soft glow behind the pop-up's icon plus a thin coloured rim around the notch.</summary>
    void TintOsd(Color? accent)
    {
        if (accent is { } c)
        {
            var glow = new RadialGradientBrush
            {
                Center = new Point(0.1, 0.5), GradientOrigin = new Point(0.1, 0.5), RadiusX = 0.55, RadiusY = 1.6,
                GradientStops = { new GradientStop(Color.FromArgb(0x5A, c.R, c.G, c.B), 0), new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1) },
            };
            glow.Freeze();
            OsdGlow.Background = glow;
            Fade(OsdGlow, 1, 220);
        }
        else Fade(OsdGlow, 0, 160);
        _osdAccent = accent;
    }

    Color? _osdAccent;

    /// <summary>The thin rim follows whatever is on screen: the pop-up's colour, or the song's colour.</summary>
    void UpdateRim()
    {
        Color? c = _mode switch
        {
            Mode.Osd => _osdAccent,
            Mode.Expanded => ExpandedRimColor(), // the panel's glow has an edge in the same colour
            Mode.Media when _media.HasSession && _media.Art != null && _media.Accent != Colors.White => _media.Accent,
            Mode.Activity when CurrentActivity == LiveActivity.Call => ((SolidColorBrush)CallBrush).Color,
            Mode.Activity when CurrentActivity == LiveActivity.Focus => ((SolidColorBrush)FocusPurple).Color,
            _ => null,
        };
        // a colour: bright along the bottom, fading up the sides into the screen's edge.
        // nothing live: a faint glass bevel so the black shape still reads crisp on a dark wallpaper.
        if (c == null && _mode != Mode.Slim && CustomEdge is { } edge) c = edge; // your edge colour, all the time
        // nothing live: a faint bevel; the glass themes catch more light at their rim
        Color k = c ?? _look?.RimColor ?? Colors.White;
        double strength = c != null ? (_mode == Mode.Expanded ? 0.58 : 0.62) : _mode switch
        {
            Mode.Slim => 0,
            Mode.Expanded => _look != null ? _look.RimIdle + 0.08 : 0.13,
            _ => _look?.RimIdle ?? 0.10,
        };
        Color At(double f) => Color.FromArgb((byte)Math.Round(255 * strength * f), k.R, k.G, k.B);
        if (Rim.BorderBrush is not LinearGradientBrush g || g.IsFrozen)
        {
            g = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1), MappingMode = BrushMappingMode.RelativeToBoundingBox };
            g.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
            g.GradientStops.Add(new GradientStop(Colors.Transparent, 0.5));
            g.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
            Rim.BorderBrush = g;
        }
        double[] f = { 0, 0.45, 1 };
        var dur = TimeSpan.FromMilliseconds(320);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        for (int i = 0; i < 3; i++)
            g.GradientStops[i].BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(At(f[i]), dur) { EasingFunction = ease });
        // exactly one device pixel: a hairline that stays sharp at any display scaling
        double px = Math.Max(1, Math.Round(_scale)) / Math.Max(1, _scale);
        Rim.BorderThickness = new Thickness(px, 0, px, px);
        if (_mode != Mode.Drop) UpdateAura(_mode == Mode.Osd ? _osdAccent : null); // the drop zone sets its own
        CyberTint();
    }

    // ───────────────────────────── focus ─────────────────────────────

    /// <summary>What still gets through while you focus: your own key presses and things that can't wait.</summary>
    static bool FocusAllows(string kind) =>
        kind is "volume" or "brightness" or "caps" or "keyboard" or "call" or "call-end" or "mic" or "output" or "focus" or "meeting" or "timer" or "battery" or "smart-charge";

    void OnFocusChanged()
    {
        RefreshToggles();
        RefreshBase();
        if (!_alertsReady) return;
        if (_focus.IsOn)
        {
            ShowOsd("focus", "", null, "Focus mode on", "Notifications wait until you're done · click to end", null,
                glyphBrush: FocusPurple, seconds: 3, force: true, onClick: () => _focus.Set(false));
            return;
        }
        var spent = DateTime.Now - _focus.StartedAt;
        int held = _focus.Held.Count;
        string duration = spent.TotalMinutes < 1 ? "under a minute" : SystemMonitor.Duration(spent);
        ShowOsd("focus", "", null, $"Focus ended · {duration}",
            held == 0 ? "Nothing came in" : $"{held} held: {_focus.HeldSummary()} · click to see them", null,
            glyphBrush: FocusPurple, seconds: held == 0 ? 3.5 : 8, force: true,
            onClick: held == 0 ? null : OpenNotificationCenter);
    }

    static void OpenNotificationCenter()
    {
        // Win+N: Windows' notification list, where everything that arrived during focus is waiting
        Keybd(0x5B, false); Keybd(0x4E, false); Keybd(0x4E, true); Keybd(0x5B, true);
    }

    /// <summary>Current pop-up is done: show the next queued one, or settle back to the resting shape.</summary>
    void NextOsd()
    {
        _osdTimer.Stop();
        if (Pill.IsMouseCaptured) ReleasePillPress(); // a pop-up that leaves mid-press never keeps the pointer
        // what waited too long (behind a game in full screen, a long hover...) is old news: drop it
        var now = DateTime.UtcNow;
        _osdQueue.RemoveAll(q => (now - q.Queued).TotalSeconds > 12 && !(q.Kind == "ring" && Ringing));
        BundleWaiting(); // several apps' news waiting: one line says how many (NotchWindow.OsdTogether)
        if (_osdQueue.Count > 0 && !_hidden)
        {
            var next = _osdQueue.OrderByDescending(q => q.Priority).First();
            _osdQueue.Remove(next);
            Present(Sticky(next) ? next : next with { Seconds = Math.Min(next.Seconds, 2.6) });
            return;
        }
        _osd = null;
        SetMode(BaseMode);
        if (_hidden) Fade(Shell, 0, 200);
    }

    System.Windows.Threading.DispatcherTimer _osdDwell = null!;

    /// <summary>
    /// The app playing the music is the window you're using (a song changing in the browser you're browsing in):
    /// you can already see it, no pop-up. Store apps can't be told apart this way and keep their pop-up.
    /// </summary>
    bool PlayerInFront()
    {
        string? app = _media.SourceApp;
        if (string.IsNullOrEmpty(app) || app.Contains('!')) return false;
        string name = Path.GetFileNameWithoutExtension(app);
        if (name.Equals("MSEdge", StringComparison.OrdinalIgnoreCase)) name = "msedge";
        try
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
            if (pid == 0) return false;
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return p.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    void UpdateOsdChrome()
    {
        bool clickable = _mode == Mode.Osd && _osd?.OnClick != null;
        double to = !clickable ? 0 : _osdHover ? 0.85 : 0.3;
        OsdChevron.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(160)));
    }

    void WireOsdInput()
    {
        _osdTimer.Tick -= OnOsdTimer;
        _osdTimer.Tick += OnOsdTimer;

        // a pop-up stays while you look at it, but only once the pointer rests on it: passing over it on the way to a
        // browser tab or a window's title bar doesn't hold it (or make it grow under your click)
        _osdDwell = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(380) };
        _osdDwell.Tick += (_, _) =>
        {
            _osdDwell.Stop();
            if (_mode != Mode.Osd || _osd?.OnClick == null || !Pill.IsMouseOver) return;
            _osdHover = true;
            _osdTimer.Stop();
            OsdCountdownHold(true);
            CardOpen(); // a notification opens up: the whole message and its actions
            ApplyShape();
            UpdateOsdChrome();
        };
        Pill.MouseEnter += (_, _) =>
        {
            if (_mode != Mode.Osd || _osd?.OnClick == null) return;
            _osdDwell.Stop();
            _osdDwell.Start();
        };
        Pill.MouseLeave += (_, _) =>
        {
            _osdDwell.Stop();
            if (_mode != Mode.Osd || !_osdHover) return;
            _osdHover = _pressed = false;
            bool sticky = _osd != null && Sticky(_osd);
            if (!sticky) CardClose(); // (a ringing call keeps its Answer and Decline, a call its End call)
            ApplyShape();
            UpdateOsdChrome();
            // gone soon after you look away; a ringing call stays as long as it rings
            double left = sticky ? Math.Max(3, _osd!.Seconds - (DateTime.UtcNow - _osdShownAt).TotalSeconds) : 1.2;
            _osdTimer.Interval = TimeSpan.FromSeconds(left);
            _osdTimer.Start();
            OsdCountdownHold(false, left);
        };
        Pill.MouseLeftButtonDown += (_, e) =>
        {
            if (_mode != Mode.Osd || _osd?.OnClick == null) return;
            _pressed = true;
            _pressAt = e.GetPosition(this);
            // the pressed card shrinks a little: held, its edge can't slip out from under the pointer and drop the click
            Pill.CaptureMouse();
            ApplyShape();
            e.Handled = true;
        };
        Pill.MouseMove += (_, e) =>
        {
            // flick up to dismiss
            if (e.LeftButton != MouseButtonState.Pressed && Pill.IsMouseCaptured) { ReleasePillPress(); ApplyShape(); return; } // let go somewhere we never heard of
            if (!_pressed || _mode != Mode.Osd) return;
            if (_pressAt.Y - e.GetPosition(this).Y > 10) { ReleasePillPress(); NextOsd(); }
        };
        Pill.MouseLeftButtonUp += (_, e) =>
        {
            // the pop-up may have gone while it was held (opened, replaced): the pointer is let go all the same
            if (_mode != Mode.Osd || !_pressed) { if (Pill.IsMouseCaptured) Pill.ReleaseMouseCapture(); return; }
            ReleasePillPress();
            e.Handled = true;
            // let go away from the card (it was held, so the release still comes here): no click, like any button
            var p = e.GetPosition(Pill);
            if (p.X < -12 || p.Y < -12 || p.X > Pill.ActualWidth + 12 || p.Y > Pill.ActualHeight + 12) { ApplyShape(); return; }
            var action = _osd?.OnClick;
            _osdHover = false;
            NextOsd();
            action?.Invoke();
        };
        // the hold was taken away (another window, a menu): the card lets go of its press
        Pill.LostMouseCapture += (_, e) =>
        {
            if (!_pressed || _mode != Mode.Osd || !ReferenceEquals(e.OriginalSource, Pill)) return; // (a chip inside losing its own bubbles here too)
            _pressed = false;
            ApplyShape();
        };
        Pill.MouseRightButtonUp += (_, e) =>
        {
            if (_mode != Mode.Osd) return;
            e.Handled = true;
            _osdHover = false;
            NextOsd();
        };
        Pill.MouseWheel += (_, e) =>
        {
            if (_mode != Mode.Osd || _osd?.OnWheel == null) return;
            _osd.OnWheel(e.Delta > 0 ? 1 : -1);
            e.Handled = true;
        };
    }

    /// <summary>The press on a pop-up ends: no longer pressed, and the pointer is free again.</summary>
    void ReleasePillPress()
    {
        _pressed = false; // first, so the capture's loss isn't taken for a cancelled press
        if (Pill.IsMouseCaptured) Pill.ReleaseMouseCapture();
    }

    void OnOsdTimer(object? sender, EventArgs e)
    {
        if (_mode != Mode.Osd) { _osdTimer.Stop(); return; }
        if (_osdHover) { _osdTimer.Stop(); return; }
        NextOsd();
    }

    // ───────────────────────────── system reactions ─────────────────────────────

    void OnNetworkMaybeChanged()
    {
        if (!_alertsReady) { _lastNetwork = _radio.NetworkName; return; }
        var now = _radio.NetworkName;
        if (now == _lastNetwork) return;
        var before = _lastNetwork;
        _lastNetwork = now;
        if (!_settings.SystemAlerts) return;
        if (now != null) ShowOsd("wifi", "", null, now, "Wi-Fi connected", null, glyphBrush: Blue, seconds: 2, onClick: () => Launch("ms-settings:network-wifi"));
        else if (before != null && _radio.WifiOn)
            ShowOsd("wifi", "", null, "Wi-Fi disconnected", before, null, glyphBrush: Red, seconds: 2.4, motion: Motion.Shake, onClick: () => Launch("ms-settings:network-wifi"));
    }

    void CheckBatteryLow()
    {
        if (!_power.HasBattery || _power.PluggedIn || !_settings.SystemAlerts) return;
        int p = _power.Percent;
        int threshold = p <= 10 ? 10 : p <= 20 ? 20 : 101;
        if (threshold >= _batteryAlertedAt || threshold == 101) return;
        _batteryAlertedAt = threshold;
        PalReact(Controls.Companion.Act.Tired, 6); // the companion is running low too
        ShowOsd("battery", null, null, threshold == 10 ? "Battery critically low" : "Battery low", RemainingHint() ?? "Plug in your charger", $"{p}%",
            battery: (p / 100.0, false), seconds: 5, force: true, prio: Prio.High, onClick: () => Launch("ms-settings:batterysaver"));
    }

    void CheckLockKeys()
    {
        bool caps = (GetKeyState(VK_CAPITAL) & 1) != 0;
        if (_capsLock == null) { _capsLock = caps; return; }
        if (caps == _capsLock) return;
        _capsLock = caps;
        if (_settings.SystemAlerts)
            ShowOsd("caps", "⇪", null, caps ? "Caps Lock on" : "Caps Lock off", null, null, glyphBrush: caps ? Orange : null, seconds: 1.2, prio: Prio.Low);
    }

    DateTime _lastScreenshotAt;
    int _clipVersion;

    /// <summary>
    /// What was copied, read on a thread of its own: the text (its first 20 000 characters), or the kind ("Image",
    /// "2 files") and the picture. Nothing for something marked private (a password manager's "not in clipboard
    /// history"), or for a lone key or token (it's never kept in the history or shown on screen).
    /// </summary>
    static Task<(string? Text, string? Kind, BitmapSource? Picture)> ReadClipboard()
    {
        var done = new TaskCompletionSource<(string?, string?, BitmapSource?)>();
        var t = new Thread(() =>
        {
            (string?, string?, BitmapSource?) result = default;
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    if (Clipboard.ContainsData("CanIncludeInClipboardHistory") && Clipboard.GetData("CanIncludeInClipboardHistory") is System.IO.MemoryStream ms
                        && ms.Length >= 4 && BitConverter.ToInt32(ms.ToArray(), 0) == 0) break; // marked: not for clipboard history
                    if (Clipboard.ContainsText())
                    {
                        string s = Clipboard.GetText();
                        if (s.Length > 20_000) s = s[..20_000];
                        if (!LooksLikeSecret(s)) result = (s, null, null);
                    }
                    else if (Clipboard.ContainsImage())
                    {
                        BitmapSource? pic = null;
                        if (Clipboard.GetImage() is { } img) { var copy = new WriteableBitmap(img); copy.Freeze(); pic = copy; } // (handed to the screen's thread)
                        result = (null, "Image", pic);
                    }
                    else if (Clipboard.ContainsFileDropList()) { var n = Clipboard.GetFileDropList().Count; result = (null, n == 1 ? "1 file" : $"{n} files", null); }
                    break;
                }
                catch (COMException) { Thread.Sleep(40); } // another app still holds the clipboard
                catch { break; }
            }
            done.TrySetResult(result);
        }) { IsBackground = true, Name = "Pixo clipboard" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return done.Task;
    }

    /// <summary>A key, a token or a private key copied on its own (they're never kept in the history or shown).</summary>
    static bool LooksLikeSecret(string s)
    {
        string t = s.Trim();
        if (t.Length < 16 || t.Length > 4000) return false;
        if (t.Contains("PRIVATE KEY-----")) return true;
        if (t.Length > 300 || t.Contains(' ') && !t.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
        return System.Text.RegularExpressions.Regex.IsMatch(t,
            @"(gsk_|sk-or-|sk-proj-|sk-ant-|ghp_|gho_|github_pat_|xox[abpr]-|glpat-)[A-Za-z0-9_\-]{16,}|AIza[0-9A-Za-z_\-]{30,}|\bAKIA[0-9A-Z]{16}\b|\beyJ[A-Za-z0-9_\-]{10,}\.eyJ[A-Za-z0-9_\-]{10,}\.|\bsk-[A-Za-z0-9]{32,}");
    }

    async void OnClipboardChanged()
    {
        if (DateTime.UtcNow < _ignoreClipboardUntil) return;
        int version = ++_clipVersion; // copies in quick succession: only the newest one counts
        // password managers mark secrets so clipboard tools leave them alone
        if (IsClipboardFormatAvailable(_fmtExclude) || IsClipboardFormatAvailable(_fmtIgnore)) return;

        // read off the screen's thread: an app like Excel or Word only makes the data when it's asked for, and a big
        // copy could keep the notch waiting for seconds (given up after 3 s)
        var reading = ReadClipboard();
        if (await Task.WhenAny(reading, Task.Delay(3000)) != reading) return;
        var (text, kind, picture) = reading.Result;
        if (text == null && kind == null) return;

        if (version != _clipVersion) return;
        if (text != null) AddClipboardText(text);
        if (picture != null) AddClipboardImage(picture);
        if (!_settings.ClipboardAlerts || _mode == Mode.Expanded || !_alertsReady) return;
        if (kind == "Image")
        {
            // Win+Shift+S copies first and saves the file a moment later: let the screenshot alert speak alone
            await Task.Delay(900);
            if (version != _clipVersion || _mode == Mode.Expanded) return;
        }
        if (_osd?.Kind == "screenshot" || (DateTime.UtcNow - _lastScreenshotAt).TotalSeconds < 3 || (DateTime.UtcNow - _screenshotToastAt).TotalSeconds < 4) return; // the screenshot alert already says it
        // only the start matters for an 80-character preview (a 10 MB log shouldn't be split on the UI thread)
        string preview = text != null
            ? string.Join(" ", (text.Length > 400 ? text[..400] : text).Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            : kind!;
        ShowOsd("copied", "", null, "Copied", preview.Length > 80 ? preview[..80] : preview, null, glyphBrush: Blue, seconds: 1.4,
            prio: Prio.Low, onClick: () => ExpandTo(3));
    }

    async void OnDeviceChange(int evt, IntPtr lParam)
    {
        if (!_settings.SystemAlerts || lParam == IntPtr.Zero || (evt != DBT_DEVICEARRIVAL && evt != DBT_DEVICEREMOVECOMPLETE)) return;
        if (Marshal.ReadInt32(lParam, 4) != DBT_DEVTYP_VOLUME) return;
        var vol = Marshal.PtrToStructure<DEV_BROADCAST_VOLUME>(lParam);
        if ((vol.dbcv_flags & 0x2) != 0) return; // network drive
        var letters = Enumerable.Range(0, 26).Where(i => (vol.dbcv_unitmask & (1 << i)) != 0).Select(i => (char)('A' + i)).ToList();
        if (letters.Count == 0) return;
        string drive = $"{letters[0]}:";
        if (evt == DBT_DEVICEARRIVAL)
        {
            // a drive that was just plugged in can take a while to answer: ask off the UI thread
            string sub = await Task.Run(() =>
            {
                try
                {
                    var d = new DriveInfo(drive);
                    if (d.IsReady) return $"{drive} {d.VolumeLabel} · {SystemMonitor.Bytes(d.TotalSize)}".Replace("  ", " ");
                }
                catch { }
                return drive;
            });
            ShowOsd("usb", "", null, "USB drive connected", sub + " · click to open", null, glyphBrush: Blue, seconds: 3,
                onClick: () => Launch(drive + "\\"));
        }
        else ShowOsd("usb", "", null, "Drive removed", drive, null, seconds: 1.8, prio: Prio.Low);
    }

    /// <summary>An app flashed its taskbar button (new message in Teams, Discord, WhatsApp, a finished task...).</summary>
    async void OnWindowFlash(IntPtr hwnd)
    {
        if (!_settings.NotificationAlerts || hwnd == IntPtr.Zero || hwnd == GetForegroundWindow()) return;
        var now = DateTime.UtcNow;
        if (_flashSeen.TryGetValue(hwnd, out var at) && (now - at).TotalSeconds < 20) return;
        _flashSeen[hwnd] = now;
        foreach (var k in _flashSeen.Where(kv => (now - kv.Value).TotalMinutes > 5).Select(kv => kv.Key).ToList()) _flashSeen.Remove(k);

        string? path = ProcessPath(hwnd);
        if (await TryRingFromFlash(hwnd, path)) return; // a calling app ringing, not just "needs your attention"
        var (app, icon) = await DescribeAppAsync(path);
        if (_lastToast.App is { Length: > 0 } && (now - _lastToast.At).TotalSeconds < 5 &&
            (app.Contains(_lastToast.App, StringComparison.OrdinalIgnoreCase) || _lastToast.App.Contains(app, StringComparison.OrdinalIgnoreCase))) return;

        if (_focus.IsOn) { _focus.Hold(app); return; }
        string title = WindowTitle(hwnd);
        ShowOsd("flash:" + app, path == null ? "" : null, null, app, string.IsNullOrWhiteSpace(title) || title == app ? "Needs your attention" : title, null,
            art: icon, seconds: 3.5, prio: Prio.High, onClick: () => FocusWindow(hwnd));
    }

    static readonly Dictionary<string, string> AppNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>"Microsoft Edge" rather than "msedge": the exe's description, else its file name (cached).</summary>
    static string AppName(string? path)
    {
        if (path == null) return "App";
        lock (AppNames) if (AppNames.TryGetValue(path, out var known)) return known;
        string name;
        try { name = FileVersionInfo.GetVersionInfo(path).FileDescription is { Length: > 0 } d ? d : Path.GetFileNameWithoutExtension(path); }
        catch { name = Path.GetFileNameWithoutExtension(path); }
        // some apps describe themselves as their file ("Notepad.exe"): show just the name
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        lock (AppNames) AppNames[path] = name;
        return name;
    }

    /// <summary>An app's name and icon both come from its exe on disk: read them off the UI thread.</summary>
    static Task<(string App, ImageSource? Icon)> DescribeAppAsync(string? path) =>
        Task.Run(() => (AppName(path), path != null ? IconHelper.ForPath(path) : null));

    (IntPtr Hwnd, DateTime At) _lastAppAlert;

    /// <summary>
    /// Some app put up an error or warning box ("The password is incorrect", "Access denied"...):
    /// echo it in the notch with a small shiver; clicking brings the dialog to the front.
    /// </summary>
    async void OnAppAlert(IntPtr hwnd, int kind)
    {
        if (kind is not (ALERT_SYSTEM_WARNING or ALERT_SYSTEM_ERROR or ALERT_SYSTEM_CRITICAL)) return;
        if (!_settings.SystemAlerts || !_alertsReady || hwnd == IntPtr.Zero) return;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == Environment.ProcessId) return;
        var now = DateTime.UtcNow;
        if (_lastAppAlert.Hwnd == hwnd && (now - _lastAppAlert.At).TotalSeconds < 3) return; // the same box announced twice
        _lastAppAlert = (hwnd, now);

        string? path = ProcessPathById(pid);
        var (app, icon) = await DescribeAppAsync(path);
        string caption = WindowTitle(hwnd).Trim();
        string message = string.Join(" ", DialogMessage(hwnd).Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries));
        bool warning = kind == ALERT_SYSTEM_WARNING;
        AppLog.Write($"app {(warning ? "warning" : "error")} from {app}: {caption}" +
            (_focus.IsOn ? " (held: focus)" : _inMeeting ? " (held: meeting)" : _hidden ? " (hidden: full screen)" : ""));
        ShowOsd("app-alert:" + app, warning ? "" : "", null,
            caption.Length > 0 ? caption : app, message.Length > 0 ? (message.Length > 90 ? message[..90] + "…" : message) : app, null,
            art: icon, glyphBrush: warning ? Orange : Red,
            seconds: 3.5, prio: Prio.High, onClick: () =>
            {
                // the error text lands in the clipboard, ready to search or send; the dialog comes to the front
                string copy = string.Join(" · ", new[] { caption, message }.Where(x => x.Length > 0));
                if (copy.Length > 0)
                {
                    _ignoreClipboardUntil = DateTime.UtcNow.AddMilliseconds(800); // no "Copied" pop-up for our own copy
                    try { Clipboard.SetText(copy); } catch { }
                }
                FocusWindow(hwnd);
            });
    }

    /// <summary>Words that mean "something went wrong" in a notification (English, French, Spanish).</summary>
    static readonly string[] FailureWords =
    {
        "fail", "error", "incorrect", "invalid", "wrong password", "denied", "couldn't", "could not", "unable to",
        "échec", "échoué", "erreur", "refusé", "impossible de",
        "fallo", "falló", "no se pudo", "denegado", "contraseña incorrecta",
    };

    static bool SoundsLikeFailure(string text) => FailureWords.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));

    // ───────────────────────────── mic / camera bubble ─────────────────────────────

    void OnPrivacyChanged()
    {
        UpdatePrivacyBubble();
        RefreshPrivacyStatus();
        if (!_alertsReady || !_settings.SystemAlerts) return;
        if (_privacy.CamInUse && _privacy.CamApp != null)
            ShowOsd("privacy", "", null, "Camera in use", _privacy.CamApp, null, glyphBrush: Green, seconds: 2.2,
                onClick: () => Launch("ms-settings:privacy-webcam"));
        else if (_privacy.MicInUse && _privacy.MicApp != null)
            ShowOsd("privacy", "", null, "Microphone in use", _privacy.MicApp, null, glyphBrush: Orange, seconds: 2.2,
                onClick: () => Launch("ms-settings:privacy-microphone"));
    }

    void RefreshPrivacyStatus()
    {
        bool mic = _privacy.MicInUse && OnCall; // the mic only shows on a call
        PrivacyText.Text = _privacy.CamInUse ? "" : mic ? "" : "";
        PrivacyText.Foreground = _privacy.CamInUse ? Green : Orange;
    }

    void UpdatePrivacyBubble()
    {
        bool raw = _privacy.CamInUse && _mode is not (Mode.Expanded or Mode.Game) && !_hidden; // the camera only: on a call the mic being on goes without saying
        if (raw && !_privacyRaw) Bud(); // the camera just came on: it shows itself
        _privacyRaw = raw;
        bool show = raw && (IslandsOut || IslandWanted); // the island has room for it: never tucked there
        TuckRight(raw && !show); // tucked: a green light in the notch's edge
        // floating: the camera shows inside the island (which widens for it), and in Dusk the heat stars
        _slotRWant = IslandSlots && (show || _look?.Sunset == true && _duskHeat > 0);
        IslandSlotRight(_slotRWant);
        if (IslandWanted) show = false;
        // the same height as what it sits beside: smaller next to the slim bar over a window
        double h = _mode == Mode.Slim ? 18 : 22; // a bead beside the notch, always clearly smaller than it
        if (PrivacyBubble.Height != h) { PrivacyBubble.Height = h; PrivacyBubble.CornerRadius = PrivacyRim.CornerRadius = new CornerRadius(0, 0, h / 2, h / 2); }
        IslandBubbles(); // floating: round beads beside the island (NotchWindow.Island)
        MicDot.Visibility = Visibility.Collapsed; // (the bead is the camera's)
        CamDot.Visibility = _privacy.CamInUse ? Visibility.Visible : Visibility.Collapsed;
        CamDot.Margin = new Thickness(0);
        if (show == _privacyShown) { if (show) StylePrivacyIsland(); return; }
        _privacyShown = show;
        StylePrivacyIsland();
        if (show)
        {
            PrivacyBubble.Visibility = Visibility.Visible;
            PrivacyBubble.UpdateLayout();
            PlacePrivacyBubble(instant: true);
            IslandNeck(PrivacyNeck, PrivacyBubble.Height, splitting: true);
            var pop = new DoubleAnimation(0.72, 1, TimeSpan.FromMilliseconds(480)) { EasingFunction = new SpringEase { Damping = 0.62 } };
            PrivacyScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            PrivacyScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
            Fade(PrivacyBubble, 1, 180);
        }
        else
        {
            var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
            a.Completed += (_, _) => { if (!_privacyShown) PrivacyBubble.Visibility = Visibility.Collapsed; };
            PrivacyBubble.BeginAnimation(OpacityProperty, a);
        }
    }

    void PlacePrivacyBubble(bool instant = false)
    {
        if (PrivacyBubble.Visibility != Visibility.Visible) return;
        double earW = _mode == Mode.Slim ? 0 : 10;
        if (_styleOn) earW = 2; // (an island style has no ears: its footprint is what's drawn, the bubble sits just beside it)
        IslandFollow(PrivacyShift, Pill.ActualWidth / 2 + earW + 6 + PrivacyBubble.ActualWidth / 2, instant);
    }
}
