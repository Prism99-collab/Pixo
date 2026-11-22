using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>Page 1: now playing, quick toggles, volume / brightness, sound output picker.</summary>
public partial class NotchWindow
{
    DateTime _lastUserChange;
    DateTime _briTouchedAt; // the laptop brightness slider, moved by you (late readings wait until it's settled)
    bool _devicesOpen;
    ImageSource? _brushFor;
    ImageBrush? _artBrush;

    bool RecentlyTouched => (DateTime.UtcNow - _lastUserChange).TotalMilliseconds < 900;

    void WireHome()
    {
        PlayBtn.Click += async (_, _) => { await _media.TogglePlayPauseAsync(); RefreshMedia(); };
        NextBtn.Click += async (_, _) => await _media.NextAsync();
        PrevBtn.Click += async (_, _) => await _media.PreviousAsync();
        ProgressSlider.ValueCommitted += async v =>
        {
            if (_media.Duration > TimeSpan.Zero && _media.CanSeek) await _media.SeekAsync(TimeSpan.FromTicks((long)(_media.Duration.Ticks * v)));
        };
        // the date, when the header is too full to show it: in the clock's hint
        ClockText.Background = Brushes.Transparent;
        ClockText.MouseEnter += (_, _) => { if (DateText.Visibility != Visibility.Visible) ShowTip(ClockText, DateTime.Now.ToString("dddd d MMMM")); };
        // a click on the cover or the title brings the player itself to the front (the app, or the browser tab)
        foreach (var el in new FrameworkElement[] { ArtBox, ArtDisc, TitleText })
        {
            el.Cursor = Cursors.Hand;
            el.MouseLeftButtonUp += (_, e) => { if (_media.HasSession) { e.Handled = true; OpenPlayer(); } };
        }

        VolSlider.ValueChanged += v => { _lastUserChange = DateTime.UtcNow; _audio.SetVolume((float)v); VolGlyph.Text = VolumeGlyph(); VolumeTick(ref _volTickAt, v); };
        MuteBtn.Click += (_, _) => { _lastUserChange = DateTime.UtcNow; _audio.ToggleMute(); RefreshVolume(); };
        OutputBtn.Click += (_, _) => ShowDevices(!_devicesOpen);
        OutputTab.Click += (_, _) => { _devicesInput = false; _devicesMixer = false; StopMixer(); RenderDevices(); };
        InputTab.Click += (_, _) => { _devicesInput = true; _devicesMixer = false; StopMixer(); RenderDevices(); };
        // the mixer tab follows a device change without being rebuilt from scratch (it rebuilds itself if its apps changed)
        _audio.DevicesChanged += () => { if (!_devicesOpen) return; if (_devicesMixer) _ = LoadMixerAsync(rebuild: false); else RenderDevices(); };
        BriSlider.ValueChanged += v => { _lastUserChange = _briTouchedAt = DateTime.UtcNow; _bri.Set((int)Math.Round(v * 100)); BriText.Text = $"{(int)Math.Round(v * 100)}"; BrightnessGlyphs(); };
        AppVolSlider.ValueChanged += v => { _audio.SetAppVolume((float)v); AppVolGlyph.Text = GlyphForLevel(v); VolumeTick(ref _appVolTickAt, v); };
        ExtBriSlider.ValueChanged +=v => { _lastUserChange = DateTime.UtcNow; _extBri.Set((int)Math.Round(v * 100)); ExtBriText.Text = $"{(int)Math.Round(v * 100)}"; BrightnessGlyphs(); };
        _extBri.Changed += RefreshExtBrightness;

        WifiTile.Click += async (_, _) => { bool on = WifiTile.IsOn = !WifiTile.IsOn; await _radio.SetWifiAsync(on); await ConfirmToggleAsync(() => _radio.WifiOn, on); };
        BtTile.Click += async (_, _) => { bool on = BtTile.IsOn = !BtTile.IsOn; await _radio.SetBluetoothAsync(on); await ConfirmToggleAsync(() => _radio.BluetoothOn, on); };
        AirTile.Click += async (_, _) => { bool on = AirTile.IsOn = !AirTile.IsOn; await _radio.SetAirplaneAsync(on); await ConfirmToggleAsync(() => _radio.AirplaneOn, on); };
        FocusTile.Click += (_, _) => _focus.Toggle();
        MicTile.Click += (_, _) => { _audio.ToggleMic(); RefreshToggles(); };
        PowerTile.Click += (_, _) => { _power.CycleMode(); RefreshToggles(); };

        // right-click a tile → the matching Windows settings page
        WifiTile.MouseRightButtonUp += (_, e) => OpenUri("ms-settings:network-wifi", e);
        BtTile.MouseRightButtonUp += (_, e) => OpenUri("ms-settings:bluetooth", e);
        AirTile.MouseRightButtonUp += (_, e) => OpenUri("ms-settings:network-airplanemode", e);
        FocusTile.MouseRightButtonUp += (_, e) => OpenUri("ms-settings:quietmomentshome", e);
        MicTile.MouseRightButtonUp += (_, e) => OpenUri("ms-settings:privacy-microphone", e);
        PowerTile.MouseRightButtonUp += (_, e) => OpenUri("ms-settings:powersleep", e);
        BatteryPanel.MouseLeftButtonUp += (_, e) => OpenUri("ms-settings:batterysaver", e);
    }

    /// <summary>
    /// The player in front, in front of you: a browser tab through the extension, a Store app by its id, any other
    /// app by its window (Spotify, a browser, VLC...). Then the notch steps aside.
    /// </summary>
    void OpenPlayer()
    {
        if (_media.FocusTab()) { Collapse(); return; }
        string? app = _media.SourceApp;
        if (string.IsNullOrEmpty(app)) return;
        if (app.Contains('!')) { OpenApp(app); return; } // a Store app ("SpotifyAB.SpotifyMusic_…!Spotify")
        string name = System.IO.Path.GetFileNameWithoutExtension(app);
        if (name.Equals("MSEdge", StringComparison.OrdinalIgnoreCase)) name = "msedge";
        try
        {
            foreach (var p in Process.GetProcessesByName(name))
                using (p)
                    if (p.MainWindowHandle != IntPtr.Zero) { FocusWindow(p.MainWindowHandle); Collapse(); return; }
        }
        catch (Exception ex) { AppLog.Write("open player: " + ex.Message); }
    }

    /// <summary>Windows refused to switch a radio (hardware switch, driver, policy): flip the tile back with a small shiver.</summary>
    async Task ConfirmToggleAsync(Func<bool> isOn, bool wanted)
    {
        await Task.Delay(800);
        if (isOn() == wanted) return;
        RefreshToggles();
        Tremble(((SolidColorBrush)Red).Color);
    }

    void OpenUri(string uri, MouseButtonEventArgs? e = null)
    {
        if (e != null) e.Handled = true;
        Launch(uri);
    }

    void Launch(string target, string? args = null)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true, Arguments = args ?? "" }); } catch { }
        Collapse();
    }

    void RefreshExpanded()
    {
        CyberSkinSoon();
        RefreshClockAndProgress();
        RefreshBattery();
        RefreshMedia();
        RefreshToggles();
        RefreshVolume();
        RefreshBrightness();
        RefreshExtBrightness();
        RefreshPrivacyStatus();
        RefreshDesktopBtn(); // changed from the desktop's own menu since?
        UpdateCallUi();
        _ = _bri.RefreshAsync();
        _ = _bt.RefreshBatteriesAsync(); // a controller's battery is fresh whenever you look
        _ = _extBri.RefreshAsync(MonitorInUse()); // picks up changes made with the monitor's own buttons
        _ = RefreshAirplaneAsync();
    }

    async Task RefreshAirplaneAsync()
    {
        await _radio.RefreshAirplaneAsync();
        RefreshToggles();
    }

    void RefreshClockAndProgress()
    {
        CyberSkinSoon();
        var now = DateTime.Now;
        SetDigits(ClockText, now.ToShortTimeString()); // Neon: changed digits decode in
        DateText.Text = now.ToString("ddd d MMM");
        // the progress bar lives on Home; don't redraw it while another page is showing
        if (_mode == Mode.Expanded && _page != 0) return;

        if (!_media.HasSession || _media.Duration <= TimeSpan.Zero)
        {
            ProgressRow.Visibility = Visibility.Collapsed;
            return;
        }
        ProgressRow.Visibility = Visibility.Visible;
        ProgressSlider.IsInteractive = _media.CanSeek; // a player that can't seek: the bar only shows
        if (ProgressSlider.IsDragging) return;
        var pos = _media.Position;
        double total = _media.Duration.TotalSeconds;
        // the bar glides to where the song will be at the next tick (no 1-second steps); a jump snaps
        if (_media.IsPlaying) ProgressSlider.Glide((pos.TotalSeconds + _media.Rate) / total, 1.0);
        else ProgressSlider.Glide(pos.TotalSeconds / total, 0);
        ElapsedText.Text = Format(pos);
        RemainText.Text = "-" + Format(_media.Duration - pos);
    }

    static string Format(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    void RefreshBattery()
    {
        BatteryText.Visibility = Battery.Visibility = _power.HasBattery ? Visibility.Visible : Visibility.Collapsed;
        SetDigits(BatteryText, $"{_power.Percent}%");
        Battery.Set(_power.Percent / 100.0, _power.PluggedIn, _power.Saver);
    }

    void RefreshMedia()
    {
        bool has = _media.HasSession;
        var brush = ArtBrush(_media.Art);
        FlipArt(ArtBox, _media.Art, () =>
        {
            ArtBox.Background = brush ?? (Brush)new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x21));
            ArtPlaceholder.Visibility = brush == null ? Visibility.Visible : Visibility.Collapsed;
        });
        // a player without a title still says what it is ("Brave", "youtube.com"); an empty artist names the app
        string app = _media.Current?.AppName ?? "";
        TitleText.Text = !has ? Loc.T("Nothing playing") : _media.Title.Length > 0 ? _media.Title : app.Length > 0 ? app : Loc.T("Playing");
        ArtistText.Text = !has ? Loc.T("Play something in Spotify, YouTube…") : _media.Artist.Length > 0 ? _media.Artist : _media.Title.Length > 0 ? app : "";
        TitleText.ToolTip = has && _media.Title.Length > 0 ? _media.Title : null; // a long title, in full
        PlayIcon.Visibility = _media.IsPlaying ? Visibility.Collapsed : Visibility.Visible;
        WaveLife();
        PauseIcon.Visibility = _media.IsPlaying ? Visibility.Visible : Visibility.Collapsed;
        PlayBtn.IsEnabled = has;
        NextBtn.IsEnabled = has && _media.CanNext;      // only what the player can really do
        PrevBtn.IsEnabled = has && _media.CanPrevious;
        RefreshAppVolume();
        RefreshClockAndProgress();
        RefreshDisc(); // the neon record: its art, its glow, turning or still
    }

    /// <summary>The song's bar flows while it plays and you can see it (Home, open, not saving power); else it rests flat.</summary>
    void WaveLife() => ProgressSlider.Live = _media.IsPlaying && _mode == Mode.Expanded && _page == 0 && !Eco;

    double _volTickAt = -1, _appVolTickAt = -1;

    /// <summary>The volume sliders: a light tick every few percent as you move them, higher the louder.</summary>
    void VolumeTick(ref double last, double v)
    {
        if (last >= 0 && Math.Abs(v - last) < 0.04) return;
        last = v;
        ScrollSound(v);
    }

    string? _appVolLogged;

    /// <summary>The small slider under the media buttons: volume of only the app that's playing.</summary>
    int _appVolVersion;

    async void RefreshAppVolume()
    {
        if (AppVolSlider.IsDragging) return;
        int version = ++_appVolVersion;
        // looked up off the UI thread; only the newest answer is applied
        float? v = _media.HasSession ? await _audio.FindAppVolumeAsync(_media.SourceApp) : null;
        if (version != _appVolVersion || AppVolSlider.IsDragging) return;
        if (_media.SourceApp != _appVolLogged)
        {
            _appVolLogged = _media.SourceApp;
            if (_appVolLogged != null) AppLog.Write($"media app '{_appVolLogged}': {(v == null ? "no sound session found" : "app volume available")}");
        }
        AppVolRow.Visibility = v != null ? Visibility.Visible : Visibility.Collapsed;
        if (v is not { } level) return;
        AppVolSlider.Value = level;
        AppVolGlyph.Text = GlyphForLevel(level);
    }

    static string GlyphForLevel(double v) => v <= 0.001 ? "" : v < 0.34 ? "" : v < 0.67 ? "" : "";

    void RefreshToggles()
    {
        WifiTile.Visibility = _radio.HasWifi ? Visibility.Visible : Visibility.Collapsed;
        WifiTile.IsOn = _radio.WifiOn;
        WifiTile.Label = !_radio.WifiOn ? "Wi-Fi off" : _radio.NetworkName ?? "Wi-Fi";

        BtTile.Visibility = _radio.HasBluetooth ? Visibility.Visible : Visibility.Collapsed;
        BtTile.IsOn = _radio.BluetoothOn;
        // the connected device, battery first so it survives the label's trimming: "50% · Xbox Wireless…"
        var bt = _radio.BluetoothOn ? _bt.Primary : null;
        BtTile.Label = !_radio.BluetoothOn ? "BT off" : bt == null ? "Bluetooth" : bt.Battery is { } b ? $"{b}% · {bt.Name}" : bt.Name;
        BtTile.ToolTip = bt == null ? "Bluetooth · right-click for settings"
            : $"{bt.Name}{(bt.Battery is { } level ? $" · {level}% battery" : "")} (right-click for settings)";

        AirTile.IsOn = _radio.AirplaneOn;

        FocusTile.IsOn = _focus.IsOn;
        FocusTile.Label = _focus.IsOn ? "Focus on" : "Focus";

        MicTile.Visibility = _audio.HasMic ? Visibility.Visible : Visibility.Collapsed;
        MicTile.IsOn = _audio.MicMuted;
        MicTile.Glyph = _audio.MicMuted ? "" : "";
        MicTile.Label = _audio.MicMuted ? "Muted" : Loc.T("Live");
        MicLive(true);

        var mode = _power.Mode;
        PowerTile.IsOn = mode != PowerMode.Balanced;
        PowerTile.OnBrush = mode == PowerMode.Efficiency
            ? new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58))
            : new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A));
        PowerTile.Glyph = mode switch { PowerMode.Efficiency => "", PowerMode.Performance => "", _ => "" };
        PowerTile.Label = mode switch { PowerMode.Efficiency => "Efficiency", PowerMode.Performance => "Performance", _ => "Balanced" };
        ColorTiles();
    }

    void RefreshVolume()
    {
        if (!VolSlider.IsDragging) VolSlider.Value = _audio.Muted ? 0 : _audio.Volume;
        VolGlyph.Text = VolumeGlyph();
        OutputGlyph.Text = _audio.OutputIsHeadphones ? "" : "";
        OutputBtn.ToolTip = _audio.OutputName;
    }

    void RefreshBrightness()
    {
        BriRow.Visibility = _bri.Supported ? Visibility.Visible : Visibility.Collapsed;
        // under your hand (dragging, or a wheel turn just now) the slider and its number are already right: a late
        // reading must not pull them back
        if (BriSlider.IsDragging || (DateTime.UtcNow - _briTouchedAt).TotalMilliseconds < 900) return;
        BriSlider.Value = _bri.Level / 100.0;
        BriText.Text = $"{_bri.Level}";
    }

    void RefreshExtBrightness()
    {
        bool ext = _extBri.Supported;
        ExtBriRow.Visibility = ext ? Visibility.Visible : Visibility.Collapsed;
        // three sliders only fit with slightly tighter rows; with two, keep the roomier layout
        HomeGap1.Height = new GridLength(ext ? 8 : 14);
        HomeGap2.Height = new GridLength(ext ? 6 : 10);
        VolRow.Height = BriRow.Height = ExtBriRow.Height = ext ? 24 : 30;
        BriRow.Margin = ExtBriRow.Margin = new Thickness(0, ext ? 1 : 2, 0, 0);
        // with two brightness sliders, tell them apart: laptop vs monitor
        BriGlyph.Text = ext ? "" : "";
        BriRow.ToolTip = ext ? "Laptop screen brightness" : null;
        if (!ext) return;
        if (!ExtBriSlider.IsDragging && !RecentlyTouched) ExtBriSlider.Value = _extBri.Level / 100.0;
        BrightnessGlyphs();
        ExtBriText.Text = $"{_extBri.Level}";
    }

    string VolumeGlyph()
    {
        float v = _audio.Muted ? 0 : _audio.Volume;
        return v <= 0.001 ? "" : v < 0.34 ? "" : v < 0.67 ? "" : "";
    }

    void OnMediaChanged()
    {
        var brush = ArtBrush(_media.Art);
        FlipArt(CompactArt, _media.Art, () => CompactArt.Background = brush ?? (Brush)new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2E)));
        RefreshDisc(); // the neon records, big and small
        UpdateRain(); // the idle rain stops when music starts, and comes back when it stops
        PaintBars();
        // the cyber skin answers the music too: a new song or play / pause
        string key = $"{_media.Title}|{_media.IsPlaying}";
        if (key != _mediaGlitchKey) { _mediaGlitchKey = key; CyberPulse(); }

        ApplyMediaColor();
        if (_mode == Mode.Expanded) RefreshMedia();
        RefreshBase();
        UpdateEqualizer();
        UpdateRim();
    }

    /// <summary>The song changed colour (or went away): the open panel's tint follows.</summary>
    void ApplyMediaColor() => UpdatePanelTint();

    ImageBrush? ArtBrush(ImageSource? img)
    {
        if (img == null) return null;
        if (ReferenceEquals(img, _brushFor)) return _artBrush;
        var b = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
        RenderOptions.SetBitmapScalingMode(b, BitmapScalingMode.HighQuality);
        b.Freeze();
        _brushFor = img;
        _artBrush = b;
        return b;
    }

    static ImageBrush? IconBrush(ImageSource? img)
    {
        if (img == null) return null;
        var b = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
        RenderOptions.SetBitmapScalingMode(b, BitmapScalingMode.HighQuality);
        b.Freeze();
        return b;
    }

    // ───────────────────────────── visualizer: follows the real sound ─────────────────────────────

    string _mediaGlitchKey = "";

    /// <summary>
    /// The bars in the cover's colours: from its main colour on the left to its second colour on the right, each
    /// bar deeper at its foot and catching light at its tip. A colourless cover gets white fading to silver.
    /// </summary>
    void PaintBars()
    {
        var bars = Eq.Children.OfType<Rectangle>().ToArray();
        Color a = _media.Accent, b = _media.Accent2;
        // Neon: a VU meter's square steps, yellow to cyan; Dusk: its sunset, pink to gold; else the cover's colours
        if (_cyber) { a = CustomAccent ?? CyYellow; b = CyCyan; }
        else if (DuskOn) { a = Color.FromRgb(0xFF, 0x4F, 0x9A); b = Color.FromRgb(0xFF, 0xC8, 0x57); }
        for (int i = 0; i < bars.Length; i++)
        {
            bars[i].RadiusX = bars[i].RadiusY = _cyber ? 0 : bars[i].Width / 2;
            double t = bars.Length > 1 ? i / (double)(bars.Length - 1) : 0;
            var c = Mix(a, b, t);
            var g = new LinearGradientBrush { StartPoint = new Point(0.5, 1), EndPoint = new Point(0.5, 0) };
            g.GradientStops.Add(new GradientStop(Scale(c, 0.55), 0));
            g.GradientStops.Add(new GradientStop(c, 0.62));
            g.GradientStops.Add(new GradientStop(Mix(c, Colors.White, 0.35), 1));
            g.Freeze();
            bars[i].Fill = g;
        }
        // a colourless cover: white fading to silver (a ribbon of plain white would look flat)
        if (!_cyber && !DuskOn && a == Colors.White) b = Color.FromRgb(0xB8, 0xC4, 0xD8);
        PaintWave(a, b); // what shows: the ribbon (NotchWindow.MusicWave)
    }

    static Color Mix(Color a, Color b, double t) =>
        Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    static Color Scale(Color c, double k) => Color.FromRgb((byte)(c.R * k), (byte)(c.G * k), (byte)(c.B * k));

    // the bars' physics: each listens to a different part of the sound, rises on it fast and falls with gravity
    double _lowEnv, _prevLoud;
    readonly double[] _eqFall = new double[5];
    readonly double[] _eqRaw = new double[5];
    const double EqTall = 15;

    /// <summary>A bar's height for a level (0..1): never below a round dot; in Neon, in LED steps.</summary>
    double EqHeight(double level)
    {
        double w = _eqBars.Length > 0 ? _eqBars[0].Width : 2.6;
        if (_cyber) return Math.Max(w, Math.Ceiling(Math.Clamp(level, 0, 1) * 5) / 5 * EqTall); // a VU meter's steps
        return Math.Max(w, Math.Clamp(level, 0, 1) * EqTall);
    }

    void SetBar(int i, double level)
    {
        double h = EqHeight(level);
        if (Math.Abs(_eqBars[i].Height - h) > 0.35) _eqBars[i].Height = h; // (less than a pixel's step isn't drawn)
    }

    readonly DispatcherTimer _meter = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(25) };
    readonly double[] _eqLevel = { 0.3, 0.3, 0.3, 0.3, 0.3 };
    double _beat;
    bool _meterWired, _eqFallback, _slimBeat;

    // the music moves with the sound only when it's seen (hidden over a full-screen app: nobody sees it, it rests)
    bool EqWanted => _mode == Mode.Media && _media.IsPlaying && _settings.AnimatedVisualizer && !Eco && !_offScreen;

    void UpdateEqualizer()
    {
        bool run = EqWanted;
        if (run == _eqRunning) return;
        _eqRunning = run;
        if (!run)
        {
            _eqFallback = false;
            // at rest: a small, still arc (the middle a touch taller), the way a paused wave settles
            double[] rest = { 0.28, 0.42, 0.55, 0.42, 0.28 };
            for (int i = 0; i < _eqBars.Length; i++)
            {
                _eqBars[i].BeginAnimation(HeightProperty, null);
                _eqLevel[i] = rest[i % rest.Length];
                SetBar(i, _eqLevel[i]);
            }
            _waveLoud = 0;
            DrawWave(); // the ribbon settles into a calm, still curve
        }
        UpdateMeter();
    }

    /// <summary>The meter runs only while something on screen dances to it: the bars, or the slim glow over a window.</summary>
    void UpdateMeter()
    {
        if (!_meterWired) { _meterWired = true; _meter.Tick += (_, _) => OnMeter(); }
        // only the slim glow (a 2.5 px line over a window): 16 steps a second is plenty, and every step redraws the
        // whole window, so the bars' 40 a second would cost the PC for nothing
        var every = TimeSpan.FromMilliseconds(_callBarsOn ? 25 : _eqRunning ? 33 : 62); // the bars: 30 steps a second (their motion is tuned for it)
        if (_meter.Interval != every) _meter.Interval = every;
        if (_eqRunning || _slimBeat || _callBarsOn) { if (!_meter.IsEnabled) _meter.Start(); }
        else _meter.Stop();
    }

    void OnMeter()
    {
        bool callGlow = OnCall && CurrentActivity == LiveActivity.Call;
        if (_callBarsOn || (_slimBeat && callGlow)) OnCallMeter();
        if (callGlow && _slimBeat)
        {
            double v = 0.35 + 0.65 * _voice;
            if (Math.Abs(SlimGlow.Opacity - v) > 0.025) SlimGlow.Opacity = v;
        }
        if (callGlow && _mode == Mode.Activity) BeatAura(_voice * 1.2);
        if (!_eqRunning && !(_slimBeat && !callGlow)) return; // nothing else needs the speakers' level
        if (!_audio.TryGetPeaks(out float left, out float right))
        {
            // no meter on this device: the ribbon breathes on its own, gently (a slow swell travelling across it)
            if (_eqRunning)
            {
                _eqFallback = true;
                double tt = Environment.TickCount64 / 1000.0;
                for (int i = 0; i < _eqLevel.Length; i++) _eqLevel[i] = 0.32 + 0.22 * Math.Sin(tt * 2.1 - i * 0.8);
                _waveLoud = 0.25;
                DrawWave();
                return;
            }
            _meter.Stop();
            return;
        }
        double t = Environment.TickCount64 / 1000.0;
        double loud = Math.Sqrt(Math.Max(left, right)); // perceived loudness, not raw amplitude

        if (_eqRunning && !_eqFallback)
        {
            // Windows gives one loudness per channel, not a spectrum: the bars are split by time instead.
            // the body of the sound (slow), the sound itself, its punches (what rises above the body), its sparkle
            // (how fast it changes). Each leans toward its side's channel.
            _lowEnv += (loud - _lowEnv) * 0.13;
            double punch = Math.Clamp((loud - _lowEnv) * 2.8, 0, 1);
            double sparkle = Math.Clamp(Math.Abs(loud - _prevLoud) * 4.5, 0, 1);
            _prevLoud = loud;
            double l = Math.Sqrt(left), r = Math.Sqrt(right), lr = Math.Max(0.001, Math.Max(l, r));
            // five voices, mirrored round the middle: the sound itself in the centre, its punches beside it, its
            // sparkle and its body at the ends (left channel on the left, right on the right)
            double[] drive = { sparkle * 0.6 + loud * 0.32, punch * 0.8 + loud * 0.42, loud * 1.1, punch * 0.65 + _lowEnv * 0.45, _lowEnv * 0.62 + sparkle * 0.35 };
            for (int i = 0; i < _eqBars.Length && i < drive.Length; i++)
            {
                double side = i < 2 ? l / lr : i > 2 ? r / lr : 1;
                // a soft ripple travels across them, so quiet passages still breathe (it fades as the music gets loud)
                double ripple = 0.08 * (0.5 + 0.5 * Math.Sin(t * 4.6 - i * 0.95)) * (1 - Math.Clamp(loud * 1.4, 0, 1));
                double centre = 1 - 0.1 * Math.Abs(i - 2); // the middle leads, the ends answer
                double target = 0.16 + 0.84 * Math.Clamp(drive[i] * centre * (0.85 + 0.15 * side), 0, 1) + ripple;
                if (target >= _eqRaw[i])
                {
                    _eqRaw[i] += (target - _eqRaw[i]) * 0.72; // up on the beat
                    _eqFall[i] = 0;
                }
                else
                {
                    _eqFall[i] = Math.Min(_eqFall[i] + 0.0145, 0.11); // then down, faster and faster, like a weight
                    _eqRaw[i] = Math.Max(target, _eqRaw[i] - _eqFall[i]);
                }
            }
            // neighbours pull on each other a little: the bars move as one wave, not five strangers
            for (int i = 0; i < _eqBars.Length && i < _eqRaw.Length; i++)
            {
                double left2 = _eqRaw[Math.Max(0, i - 1)], right2 = _eqRaw[Math.Min(_eqRaw.Length - 1, i + 1)];
                _eqLevel[i] = _eqRaw[i] * 0.7 + (left2 + right2) * 0.15;
                SetBar(i, _eqLevel[i]);
            }
            _waveLoud += (Math.Clamp(loud, 0, 1) - _waveLoud) * 0.2;
            DrawWave();
        }
        if (_mode == Mode.Media) BeatAura(loud * 1.4);
        if (_slimBeat && !callGlow)
        {
            _beat += (Math.Clamp(loud * 1.35, 0, 1) - _beat) * (_meter.Interval.TotalMilliseconds > 30 ? 0.58 : 0.3); // the same feel at either pace
            double o = 0.35 + 0.65 * _beat;
            if (Math.Abs(SlimGlow.Opacity - o) > 0.025) SlimGlow.Opacity = o; // a change too small to see isn't drawn
        }
    }

    void StartLoopingBars()
    {
        // no meter on this device: a calm wave instead, rolling across the five
        double[] periods = { 0.62, 0.54, 0.48, 0.56, 0.66 };
        for (int i = 0; i < _eqBars.Length; i++)
        {
            var a = new DoubleAnimation(EqHeight(0.22), EqHeight(0.85 - Math.Abs(i - 2) * 0.12), TimeSpan.FromSeconds(periods[i % periods.Length]))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromMilliseconds(i * 110),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            Timeline.SetDesiredFrameRate(a, 30);
            _eqBars[i].BeginAnimation(HeightProperty, a);
        }
    }

    void ShowDevices(bool open)
    {
        if (open == _devicesOpen) return;
        _devicesOpen = open;
        if (open)
        {
            CloseRadioSheet(instant: true); // one list at a time
            _devicesInput = false; // always opens on speakers / headphones
            _devicesMixer = false;
            StyleDevicesBack(); // the theme and panel colour of right now
            _devicesKey = ""; // drawn fresh on opening (the theme may have changed since)
            RenderDevices();
            _audio.RefreshDevices(); // just in case; the list on screen updates if anything changed
            DevicesPanel.Visibility = Visibility.Visible;
            Fade(DevicesPanel, 1, 160);
            Fade(MediaRow, 0, 120);
            Fade(CallRow, 0, 120);
            Fade(TogglesRow, 0, 120);
            MediaRow.IsHitTestVisible = CallRow.IsHitTestVisible = TogglesRow.IsHitTestVisible = false;
        }
        else
        {
            StopMixer();
            var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120));
            a.Completed += (_, _) => { if (!_devicesOpen) DevicesPanel.Visibility = Visibility.Collapsed; };
            DevicesPanel.BeginAnimation(OpacityProperty, a);
            RestoreHomeRows(); // (the music row stays away while a song card is up)
        }
    }

    bool _devicesInput;
    bool _devicesBackWired;
    string _devicesKey = ""; // what the list shows now (cleared whenever something else fills it)

    /// <summary>
    /// The device list's backdrop: in the neon theme a chamfered plate in the panel's own body (your panel
    /// colour, or the theme's night blue) over an opaque base, so nothing of the cards underneath shows through,
    /// with a cyan rim and a yellow tick on the cut corner. Every other theme keeps the plain rounded one.
    /// </summary>
    void StyleDevicesBack()
    {
        bool cy = _cyber;
        DevicesBack.Visibility = cy ? Visibility.Collapsed : Visibility.Visible;
        DevicesBackCy.Visibility = cy ? Visibility.Visible : Visibility.Collapsed;
        if (!cy) return;
        if (!_devicesBackWired) { _devicesBackWired = true; DevicesBackCy.SizeChanged += (_, _) => ShapeDevicesBack(); }
        DevicesBackBody.Fill = Pill.Background is Brush b && b != Brushes.Transparent ? b : _cyberBody;
        DevicesBackRim.Stroke = Frozen(Color.FromArgb(0x70, CyCyan.R, CyCyan.G, CyCyan.B));
        DevicesBackTick.Stroke = Frozen(CyYellow);
        ShapeDevicesBack();
    }

    void ShapeDevicesBack()
    {
        double w = DevicesBackCy.ActualWidth, h = DevicesBackCy.ActualHeight, c = 12;
        if (w < 3 * c || h < 3 * c) return;
        // the plate: top-left and bottom-right corners cut at 45°
        var plate = Geometry.Parse(FormattableString.Invariant($"M{c},0.5 L{w - 0.5},0.5 L{w - 0.5},{h - c} L{w - c},{h - 0.5} L0.5,{h - 0.5} L0.5,{c} Z"));
        plate.Freeze();
        DevicesBackBase.Data = DevicesBackBody.Data = DevicesBackRim.Data = plate;
        var tick = Geometry.Parse(FormattableString.Invariant($"M1.5,{c + 6} L1.5,{c} L{c},1.5 L{c + 22},1.5"));
        tick.Freeze();
        DevicesBackTick.Data = tick;
    }

    /// <summary>Fills the device panel from the lists the audio service keeps ready (no waiting on Windows).</summary>
    void RenderDevices()
    {
        if (_devicesMixer) { RenderMixer(); return; }
        StyleTab(OutputTab, !_devicesInput);
        StyleTab(InputTab, _devicesInput);
        StyleTab(MixerTab, false);
        var devices = _devicesInput ? _audio.Inputs : _audio.Outputs;
        // opening the list asks Windows for it again, and the answer is almost always the same: rebuilding then would
        // swap the rows under the pointer (a click already pressed on one would be lost, the hover would blink)
        string key = (_devicesInput ? "in|" : "out|") + string.Join("|", devices.Select(d => $"{d.Id}{d.Name}{d.IsHeadphones}{d.IsDefault}"));
        if (key == _devicesKey && DevicesList.Children.Count > 0) return;
        _devicesKey = key;
        DevicesList.Children.Clear();
        if (devices.Count == 0)
        {
            DevicesList.Children.Add(new TextBlock
            {
                Text = _devicesInput ? "No microphone found" : "No sound output found",
                FontSize = 11.5, Foreground = DimBrush, Margin = new Thickness(10, 8, 10, 0),
            });
            return;
        }
        foreach (var d in devices)
        {
            var row = new Grid { Height = 30 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            string glyph = _devicesInput ? (d.IsHeadphones ? "" : "") : d.IsHeadphones ? "" : "";
            row.Children.Add(Glyph(glyph, 14));
            var name = new TextBlock { Text = d.Name, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            if (d.IsDefault)
            {
                var check = Glyph("", 12, Blue);
                Grid.SetColumn(check, 2);
                row.Children.Add(check);
            }
            var btn = new Button { Style = (Style)FindResource("RowButton"), Content = row, Height = 32, ToolTip = d.Name };
            string id = d.Id;
            btn.Click += (_, _) => { _audio.SetDefaultDevice(id); ShowDevices(false); };
            DevicesList.Children.Add(btn);
        }
    }


    // shared little helpers ------------------------------------------------------------

    static readonly Brush Blue = Frozen(Color.FromRgb(0x0A, 0x84, 0xFF));
    static readonly Brush Orange = Frozen(Color.FromRgb(0xFF, 0x9F, 0x0A));
    static readonly Brush Green = Frozen(Color.FromRgb(0x30, 0xD1, 0x58));
    static readonly Brush Red = Frozen(Color.FromRgb(0xFF, 0x45, 0x3A));
    static readonly Brush Purple = Frozen(Color.FromRgb(0x5E, 0x5C, 0xE6));
    static readonly Brush DimBrush = Frozen(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF));

    static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    TextBlock Glyph(string glyph, double size, Brush? brush = null) => new()
    {
        Text = glyph,
        FontFamily = (FontFamily)FindResource("IconFont"),
        FontSize = size,
        Foreground = brush ?? Brushes.White,
        VerticalAlignment = VerticalAlignment.Center,
    };
}
