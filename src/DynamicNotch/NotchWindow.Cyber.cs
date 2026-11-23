using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace DynamicNotch;

/// <summary>
/// Neon theme (the yellow button beside the lock, or Settings): the notch becomes a piece of street-tech HUD.
///  • shape: hard 45° cuts instead of round corners, asymmetric, with a step in its bottom edge; the ears go angular;
///  • a yellow frame along its sides and bottom, cyan circuit traces, hazard stripes, a ruler, a part-number tag;
///  • the palette of the neon title card: night #00060E, yellow #FEE801, olive #9A9F17, cyan #3CD5FF, teal #2FE0C6;
///    the condensed Bahnschrift that ships with Windows for all text, the clock in yellow;
///  • motion: a glitch burst on every change (signal slices, a cyan ghost of the frame, a jolt), a scan line
///    sweeping down when it opens, titles that decode letter by letter.
/// Nothing runs continuously except a slow trace pulse while the notch is open; in a game it's static, and
/// battery mode keeps only the essential motion. Off, everything is exactly as before.
/// </summary>
public partial class NotchWindow
{
    static readonly Color CyYellow = Color.FromRgb(0xFE, 0xE8, 0x01), CyOlive = Color.FromRgb(0x9A, 0x9F, 0x17),
        CyCyan = Color.FromRgb(0x3C, 0xD5, 0xFF), CyTeal = Color.FromRgb(0x2F, 0xE0, 0xC6), CyNight = Color.FromRgb(0x00, 0x06, 0x0E);
    static readonly FontFamily CyFont = new("Bahnschrift, Segoe UI");
    static readonly Brush CyYellowBrush = Frozen(CyYellow), CyCyanBrush = Frozen(CyCyan);
    const string Scramble = "#$%&@<>/\\|=+*01?!ZX";

    // fonts: every text in the notch, including ones created later (list rows, chips, tips)
    // the theme's font for every text with a font of its own (Neon: Bahnschrift; Coffee: Nunito), null: their own
    static FontFamily? _themeFont;
    static FontStretch _themeStretch = FontStretches.Normal;
    static bool _cyberFonts => _themeFont != null;
    static bool _cyberHandler;
    sealed class FontBefore { public FontFamily Family = null!; public FontStretch Stretch; }
    static readonly ConditionalWeakTable<TextBlock, FontBefore> _fontBefore = new();

    bool _cyber;
    TranslateTransform? _stageTear;
    static bool _pressHandler;
    Canvas _hud = null!;
    Path _cyFrame = null!, _cyGhost = null!, _cyTrace = null!, _cyTicks = null!, _cyCorners = null!, _cyHalo = null!, _cyRun = null!;
    double _cyPerim;
    Rectangle _cyScan = null!, _cyLines = null!, _cyStripes = null!;
    Rectangle[] _cyBars = null!;
    TextBlock _cyTag = null!;
    readonly TranslateTransform _cyGhostShift = new(), _cyScanShift = new();
    Brush? _pillBgBefore, _gameFpsBefore, _cyberBody;
    Geometry? _earLBefore, _earRBefore;
    FontFamily? _windowFontBefore;
    Color? _dimBefore, _tileBefore;
    readonly Random _cyRand = new();
    readonly Dictionary<TextBlock, DispatcherTimer> _decoding = new();

    void WireCyber()
    {
        _pillBgBefore = Pill.Background;
        _gameFpsBefore = GameFps.Foreground;
        _earLBefore = EarL.Data;
        _earRBefore = EarR.Data;
        _windowFontBefore = FontFamily;
        BuildHud();
        BuildRain();   // the data rain (NotchWindow.Rain)
        BuildTarget(); // the hover scanner (NotchWindow.Target)
        BuildCyberPills();
        WireCyberSkin();
        CyberBtn.Click += (_, _) => NextTheme();
        // a press in the cyber skin: a tiny glitch answers it (light, once per press)
        if (!_pressHandler)
        {
            _pressHandler = true;
            EventManager.RegisterClassHandler(typeof(System.Windows.Controls.Primitives.ButtonBase), System.Windows.Controls.Primitives.ButtonBase.ClickEvent,
                new RoutedEventHandler((s, _) => { if (_cyber && !Eco && s is DependencyObject d && Window.GetWindow(d) == this) CyberGlitch(strong: false, light: true); }));
        }
        if (!_cyberHandler)
        {
            _cyberHandler = true;
            EventManager.RegisterClassHandler(typeof(TextBlock), LoadedEvent, new RoutedEventHandler((s, _) => { if (s is TextBlock tb) Retype(tb, _cyberFonts); }));
        }
        // the theme from last time (older settings only knew Neon on / off)
        if (_settings.Neon && _settings.Theme == "original") _settings.Theme = "neon";
        if (ThemeKey == "neon") ApplyCyber(true, animate: false);
        else if (LookFor(ThemeKey) is { } look) ApplyLook(look, animate: false);
        StyleCyberBtn();
        // development: open the control center on start (for screenshots), no input needed
        // DYNAMICNOTCH_OPEN=1 opens it; =p2 opens it on page 2
        if (Environment.GetEnvironmentVariable("DYNAMICNOTCH_OPEN") is { Length: > 0 } open)
            After(3000, () =>
            {
                if (open == "pill")
                {
                    // the game pill with sample readings (development only: nothing is saved)
                    _settings.FpsCpu = _settings.FpsGpu = _settings.FpsGpuTemp = _settings.FpsCpuTemp = true;
                    _settings.FpsGpuColor = "#3CD5FF";
                    GameFps.Text = "144";
                    SetMode(Mode.Game);
                    ShowGameStats(new Services.StrainService.Reading(32, 97, 50, 71, 64));
                    return;
                }
                if (open.StartsWith("prefs")) { PreferencesWindow.ShowFor(this, int.TryParse(open[5..], out int sec) ? sec : 0); return; }
                if (open.StartsWith("pal")) { PalDevShow(open); return; }
                if (open.StartsWith("wheeltest:"))
                {
                    // =wheeltest:7:1 — page 7 (tab 1 on Work): a wheel turn routed like the mouse hook does, the list's offset logged
                    var parts = open.Split(':');
                    int pgN = int.Parse(parts[1]);
                    Expand(byHotkey: true);
                    _expandedAt = DateTime.UtcNow.AddSeconds(25);
                    GoToPage(pgN, instant: true);
                    if (pgN == WorkPage && parts.Length > 2) ShowWorkTab(int.Parse(parts[2]));
                    After(1500, () =>
                    {
                        var svs = new List<ScrollViewer>();
                        void Walk(DependencyObject d) { for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) { var c = VisualTreeHelper.GetChild(d, i); if (c is ScrollViewer s && s.IsVisible) svs.Add(s); Walk(c); } }
                        Walk(PageElement(pgN));
                        foreach (var sv in svs)
                        {
                            var at = sv.PointToScreen(new Point(sv.ActualWidth / 2, Math.Min(40, sv.ActualHeight / 2)));
                            double before = sv.VerticalOffset;
                            Services.AppLog.Write($"wheeltest: viewer {sv.ActualWidth:0}x{sv.ActualHeight:0} scrollable={sv.ScrollableHeight:0} hookArea=({_hitL},{_hitT})-({_hitR},{_hitB}) at=({at.X:0},{at.Y:0})");
                            RouteWheel(false, -240, (int)at.X, (int)at.Y);
                            After(700, () => Services.AppLog.Write($"wheeltest: offset {before:0} -> {sv.VerticalOffset:0}"));
                        }
                    });
                    return;
                }
                if (open.StartsWith("ambient-sheet:"))
                {
                    // Crystal's and every Coffee brew's open background, drawn off screen on the theme's body into one
                    // picture (DYNAMICNOTCH_OPEN=ambient-sheet:<file.png>); nothing shows on screen
                    string file = open[14..];
                    var sheets = new List<(Brush Body, Controls.Ambient Art, Brush Floor)>
                    {
                        (LookBody(Crystal(), null), CrystalAmbient(), GeodeFloor()),
                    };
                    foreach (var b in new[] { Latte, Cappuccino, Mocha, Espresso }) sheets.Add((LookBody(CoffeeLook(b), null), CoffeeAmbient(b), CoffeeFloor(b)));
                    double sw = ExpandedSize.Width, sh = ExpandedSize.Height;
                    var col = new StackPanel { Background = Brushes.Black };
                    foreach (var (body, art, floor) in sheets)
                    {
                        var g = new Grid { Width = sw, Height = sh, Margin = new Thickness(0, 0, 0, 6), Background = body };
                        g.Children.Add(new Border { Background = floor });
                        g.Children.Add(art);
                        col.Children.Add(g);
                    }
                    col.Measure(new Size(sw, double.PositiveInfinity));
                    col.Arrange(new Rect(col.DesiredSize));
                    col.UpdateLayout();
                    var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)sw * 2, (int)col.DesiredSize.Height * 2, 192, 192, PixelFormats.Pbgra32);
                    rtb.Render(col);
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                    using (var fs = System.IO.File.Create(file)) enc.Save(fs);
                    Services.AppLog.Write("ambient sheet: " + file);
                    return;
                }
                if (open.StartsWith("theme-look:"))
                {
                    // one theme on the island and on every page, a picture of each, then yours back
                    // (DYNAMICNOTCH_OPEN=theme-look:<folder>|<theme>); never takes the keyboard
                    var parts = open[11..].Split('|');
                    string dir = parts[0], original = ThemeKey;
                    System.IO.Directory.CreateDirectory(dir);
                    if (parts.Length > 1) SetTheme(parts[1]);
                    After(1800, () => SurveyShot(System.IO.Path.Combine(dir, "island.png")));
                    After(2400, () => Expand(byHotkey: true));
                    int t = 3600;
                    foreach (var tp in PageOrder.Where(q => !PageHidden(q)))
                    {
                        int p = tp;
                        After(t, () => { _expandedAt = DateTime.UtcNow.AddSeconds(30); GoToPage(p, instant: true); });
                        After(t + 1300, () => { SurveyShot(System.IO.Path.Combine(dir, $"p{p}.png")); Services.AppLog.Write($"theme look p{p}: ambient {_ambientWant ?? "-"} eco={Eco} unseen={Unseen} off={_offScreen} locked={_pcLocked} screenoff={_screenOff} hidden={_hidden} rest={_openResting}"); });
                        t += 1500;
                    }
                    After(t + 200, () => { GoToPage(0, instant: true); Collapse(); if (ThemeKey != original) SetTheme(original); Services.AppLog.Write("theme look done"); });
                    return;
                }
                if (open.StartsWith("chat-look:"))
                {
                    // the AI page's welcome in the theme you have: arriving, settled, and with words typed, a picture of
                    // each (DYNAMICNOTCH_OPEN=chat-look:<folder>); the keyboard goes straight back, nothing is sent
                    string dir = open[10..], original = ThemeKey;
                    if (dir.Split('|') is [var d, var th]) { dir = d; SetTheme(th); } // (<folder>|<theme>: in that theme, yours back after)
                    System.IO.Directory.CreateDirectory(dir);
                    var prev = Interop.Native.GetForegroundWindow();
                    _devLook = true;
                    OpenLauncher(null);
                    StartSession(null);
                    EnterChat(null);
                    _expandedAt = DateTime.UtcNow.AddSeconds(25);
                    if (prev != IntPtr.Zero && prev != _hwnd) { SetActivatable(false); SetForegroundWindow(prev); }
                    After(700, () => SurveyShot(System.IO.Path.Combine(dir, "chat-arrive.png")));
                    After(3200, () => SurveyShot(System.IO.Path.Combine(dir, "chat.png")));
                    After(3400, () => { if (_launchBox != null) { _launchBox.Text = "What's using my PC?"; _launchBox.CaretIndex = _launchBox.Text.Length; } });
                    After(4300, () => SurveyShot(System.IO.Path.Combine(dir, "chat-typing.png")));
                    After(4400, () => { if (_launchBox != null) { _launchBox.Text = "volume 30"; _launchBox.CaretIndex = _launchBox.Text.Length; } });
                    After(5300, () => SurveyShot(System.IO.Path.Combine(dir, "chat-command.png")));
                    After(5400, () => { if (_launchBox != null) _launchBox.Text = ""; });
                    After(7000, () => SurveyShot(System.IO.Path.Combine(dir, "chat-later.png")));
                    After(7300, () => { _devLook = false; if (_launcherOpen) CloseLauncher(false); After(400, Collapse); if (ThemeKey != original) SetTheme(original); Services.AppLog.Write("chat look done"); });
                    return;
                }
                if (open.StartsWith("chat"))
                {
                    // =chat, =chat:usage, =chat:ideas — the AI page for a look (the keyboard goes straight back)
                    var prev = Interop.Native.GetForegroundWindow();
                    _devLook = true; // stays open without the keyboard
                    OpenLauncher(null);
                    StartSession(null);
                    EnterChat(null);
                    _expandedAt = DateTime.UtcNow.AddSeconds(25);
                    if (prev != IntPtr.Zero && prev != _hwnd) { SetActivatable(false); SetForegroundWindow(prev); }
                    if (open.EndsWith(":usage")) After(1500, () => OpenDockSheet("usage"));
                    if (open.EndsWith(":ideas")) After(1500, () => OpenDockSheet("ideas"));
                    return;
                }
                if (open.StartsWith("survey:"))
                {
                    // for a look at everything (DYNAMICNOTCH_OPEN=survey:<folder>): every theme, every page, a picture of
                    // each; the theme you had comes back at the end. Never takes the keyboard.
                    string dir = open[7..];
                    System.IO.Directory.CreateDirectory(dir);
                    string original = ThemeKey;
                    var pages = PageOrder.Where(q => !PageHidden(q)).ToArray();
                    Expand(byHotkey: true);
                    int t = 800;
                    foreach (var theme in ThemeOrder)
                    {
                        string th = theme;
                        After(t, () => { SetTheme(th); _expandedAt = DateTime.UtcNow.AddSeconds(30); });
                        t += 1200;
                        foreach (var spg in pages)
                        {
                            int sp = spg;
                            After(t, () => { if (_mode != Mode.Expanded) Expand(byHotkey: true); GoToPage(sp, instant: true); _expandedAt = DateTime.UtcNow.AddSeconds(30); });
                            After(t + 900, () => SurveyShot(System.IO.Path.Combine(dir, $"{th}-p{sp}.png")));
                            t += 1150;
                        }
                    }
                    After(t + 300, () => { SetTheme(original); Collapse(); Services.AppLog.Write("survey done"); });
                    return;
                }
                if (open.StartsWith("audio-look:"))
                {
                    // the sound panel on Home (its list, its button's hint) and the Calls page's sound sheet, in a few
                    // themes, a picture of each (DYNAMICNOTCH_OPEN=audio-look:<folder>); the theme you had comes back
                    string dir = open[11..];
                    System.IO.Directory.CreateDirectory(dir);
                    string original = ThemeKey;
                    var themes = new[] { original }.Concat(ThemeOrder.Where(x => x != original).Take(2)).ToArray();
                    Expand(byHotkey: true);
                    int t = 4000; // (the sound devices are listed a few seconds after the start)
                    foreach (var theme in themes)
                    {
                        string th = theme;
                        After(t, () => { SetTheme(th); _expandedAt = DateTime.UtcNow.AddSeconds(30); GoToPage(0, instant: true); });
                        After(t + 900, () => ShowDevices(true));
                        After(t + 1600, () => SurveyShot(System.IO.Path.Combine(dir, $"{th}-list.png")));
                        After(t + 1800, () => { ShowDevices(false); ShowTip(OutputBtn, _audio.OutputName); });
                        After(t + 2500, () => SurveyShot(System.IO.Path.Combine(dir, $"{th}-tip.png")));
                        After(t + 2700, () => { HideTip(); GoToPage(CallsPage, instant: true); });
                        After(t + 3300, () => ToggleCallSheet(Sheet.Audio));
                        After(t + 4100, () => SurveyShot(System.IO.Path.Combine(dir, $"{th}-calls.png")));
                        After(t + 4300, () => CloseCallSheet(instant: true));
                        t += 4700;
                    }
                    After(t, () => { SetTheme(original); GoToPage(0, instant: true); Collapse(); Services.AppLog.Write("audio look done"); });
                    return;
                }
                if (open.StartsWith("home-look:"))
                {
                    // the Home page in a few themes, a picture of each (DYNAMICNOTCH_OPEN=home-look:<folder>); the theme you had comes back
                    string dir = open[10..];
                    System.IO.Directory.CreateDirectory(dir);
                    string original = ThemeKey;
                    var themes = new[] { original }.Concat(ThemeOrder.Where(x => x != original).Take(3)).ToArray();
                    Expand(byHotkey: true);
                    int t = 3000;
                    foreach (var theme in themes)
                    {
                        string th = theme;
                        After(t, () => { SetTheme(th); _expandedAt = DateTime.UtcNow.AddSeconds(30); GoToPage(0, instant: true); });
                        After(t + 1200, () => SurveyShot(System.IO.Path.Combine(dir, $"{th}-home.png")));
                        t += 1500;
                    }
                    After(t, () => { SetTheme(original); GoToPage(0, instant: true); Collapse(); Services.AppLog.Write("home look done"); });
                    return;
                }
                if (open.StartsWith("colors-look:"))
                {
                    // the Colors page in a few themes, each target picked once (DYNAMICNOTCH_OPEN=colors-look:<folder>); your theme comes back
                    string dir = open[12..];
                    System.IO.Directory.CreateDirectory(dir);
                    string original = ThemeKey;
                    var themes = new[] { original }.Concat(ThemeOrder.Where(x => x != original).Take(2)).ToArray();
                    var paintWas = _paint;
                    Expand(byHotkey: true);
                    int t = 3000;
                    foreach (var theme in themes)
                    {
                        string th = theme;
                        After(t, () => { SetTheme(th); _expandedAt = DateTime.UtcNow.AddSeconds(30); GoToPage(4, instant: true); });
                        After(t + 1200, () => SurveyShot(System.IO.Path.Combine(dir, $"{th}-colors.png")));
                        After(t + 1400, () => SelectPaint(Paint.Edge));
                        After(t + 2000, () => SurveyShot(System.IO.Path.Combine(dir, $"{th}-colors-edge.png")));
                        After(t + 2200, () => SelectPaint(Paint.Accent));
                        t += 2800;
                    }
                    After(t, () => { SetTheme(original); SelectPaint(paintWas); GoToPage(0, instant: true); Collapse(); Services.AppLog.Write("colors look done"); });
                    return;
                }
                if (open.StartsWith("island-look:"))
                {
                    // the resting island in Neon (DYNAMICNOTCH_OPEN=island-look:<folder>): as it is, then with a timer beside
                    // it (shown as running, quietly: no tick, no sound); the timer and your theme are put back after
                    string dir = open[12..];
                    System.IO.Directory.CreateDirectory(dir);
                    if (_timerRunning) { Services.AppLog.Write("island look: skipped, a timer is running"); return; } // (it fakes one: never over yours)
                    string original = ThemeKey;
                    var (total, left) = (_timerTotal, _timerLeft);
                    After(2500, () => SetTheme("neon"));
                    After(4500, () => IslandShot(System.IO.Path.Combine(dir, "1-rest.png")));
                    After(4700, () => { _timerTotal = TimeSpan.FromMinutes(25); _timerLeft = TimeSpan.FromMinutes(12.5); _timerEnd = DateTime.UtcNow + _timerLeft; _timerRunning = true; UpdateTimerUi(); RefreshBase(); });
                    After(6500, () => IslandShot(System.IO.Path.Combine(dir, "2-timer.png")));
                    After(6700, () => { _timerRunning = false; _timerTotal = total; _timerLeft = left; UpdateTimerUi(); RefreshBase(); });
                    After(7400, () => { SetTheme(original); Services.AppLog.Write("island look done"); });
                    return;
                }
                if (open.StartsWith("system-look:"))
                {
                    // the System page after some seconds of readings, in the theme you have (DYNAMICNOTCH_OPEN=system-look:<folder>)
                    string dir = open[12..];
                    System.IO.Directory.CreateDirectory(dir);
                    Expand(byHotkey: true);
                    After(3000, () => { _expandedAt = DateTime.UtcNow.AddSeconds(60); GoToPage(1, instant: true); });
                    After(12000, () => SurveyShot(System.IO.Path.Combine(dir, "system.png")));
                    After(12250, () => SurveyShot(System.IO.Path.Combine(dir, "system-b.png"))); // a moment later: the line has moved
                    After(12500, () => SurveyShot(System.IO.Path.Combine(dir, "system-c.png")));
                    After(12800, () => { GoToPage(0, instant: true); Collapse(); Services.AppLog.Write("system look done"); });
                    return;
                }
                if (open.StartsWith("tools-look:"))
                {
                    // the Tools page in the theme you have: at rest, with the timer running a moment, and the stopwatch going with
                    // two laps (all shown quietly: no tick, no sound), then put back as it was (DYNAMICNOTCH_OPEN=tools-look:<folder>)
                    if (_timerRunning) { Services.AppLog.Write("tools look: skipped, a timer is running"); return; } // (it fakes one: never over yours)
                    string dir = open[11..];
                    System.IO.Directory.CreateDirectory(dir);
                    Expand(byHotkey: true);
                    var (total, left) = (_timerTotal, _timerLeft);
                    After(3000, () => { _expandedAt = DateTime.UtcNow.AddSeconds(30); GoToPage(2, instant: true); });
                    After(4200, () => SurveyShot(System.IO.Path.Combine(dir, "tools.png")));
                    After(4300, () => { _timerTotal = TimeSpan.FromMinutes(4); _timerLeft = TimeSpan.FromMinutes(1); _timerEnd = DateTime.UtcNow + _timerLeft; _timerRunning = true; UpdateTimerUi(); });
                    After(5400, () => SurveyShot(System.IO.Path.Combine(dir, "tools-run.png")));
                    After(5500, () =>
                    {
                        _timerRunning = false; _timerTotal = total; _timerLeft = left;
                        _stopwatch = true; _laps.Clear(); _laps.Add(TimeSpan.FromSeconds(41.3)); _laps.Add(TimeSpan.FromSeconds(38.9));
                        _swStart = DateTime.UtcNow - TimeSpan.FromSeconds(83.4); _timerRunning = true; UpdateTimerUi();
                    });
                    After(6600, () => SurveyShot(System.IO.Path.Combine(dir, "tools-stopwatch.png")));
                    After(6700, () => { _timerRunning = false; _stopwatch = false; _laps.Clear(); _swElapsed = TimeSpan.Zero; UpdateTimerUi(); GoToPage(0, instant: true); Collapse(); Services.AppLog.Write("tools look done"); });
                    return;
                }
                if (open.StartsWith("osd-look:"))
                {
                    // the pop-ups, compact and told together, a picture of each (DYNAMICNOTCH_OPEN=osd-look:<folder>)
                    string dir = open[9..];
                    System.IO.Directory.CreateDirectory(dir);
                    var ic = Environment.ProcessPath is { } exe ? Services.IconHelper.ForFile(exe) : null;
                    void Shot(int at, string name) => After(at, () => SurveyShot(System.IO.Path.Combine(dir, name + ".png")));
                    After(500, () => ShowOsd("screenshot", null, null, "Screenshot", "Saved and copied", null, art: ic, seconds: 6));
                    Shot(1300, "1-one-line");
                    After(2200, () => ShowToast(new Services.ToastInfo("WhatsApp", null, "Sara Benali", "Are we still on for tonight? I can bring the cake", ic)));
                    Shot(3000, "2-person");
                    After(3900, () => ShowOsd("app-alert:Discord", "", null, "Discord", "Something went wrong with the voice connection", null, art: ic, glyphBrush: Red, seconds: 6, prio: Prio.High));
                    After(4100, () => ShowToast(new Services.ToastInfo("Discord", null, "Yanis", "you there? calling you", ic)));
                    After(4250, () => ShowToast(new Services.ToastInfo("Discord", null, "Yanis", "pick up", ic)));
                    Shot(5000, "3-same-app");
                    After(5900, () => ShowToast(new Services.ToastInfo("WhatsApp", null, "Sara Benali", "ok see you", ic)));
                    After(5950, () => ShowToast(new Services.ToastInfo("Telegram", null, "Omar", "sent you the file", ic)));
                    After(6000, () => ShowToast(new Services.ToastInfo("Microsoft Teams", null, "Lina", "meeting moved to 3", ic)));
                    Shot(6800, "4-three-apps");
                    After(7400, NextOsd);
                    Shot(8100, "5-bundle");
                    After(8800, () => { NextOsd(); Services.AppLog.Write("osd look done"); });
                    return;
                }
                if (open.StartsWith("notifs-look:"))
                {
                    // Notifs & alerts, each view, a picture of each (DYNAMICNOTCH_OPEN=notifs-look:<folder>; never takes the keyboard)
                    string dir = open[12..];
                    System.IO.Directory.CreateDirectory(dir);
                    Expand(byHotkey: true);
                    _workTab = 2;
                    GoToPage(WorkPage, instant: true);
                    int t = 900;
                    foreach (var view in new[] { "all", "you", "alerts", "manage" })
                    {
                        string v = view;
                        After(t, () => { _inboxView = v; _expandedAt = DateTime.UtcNow.AddSeconds(30); try { RenderWorkTab(animate: false); } catch (Exception ex) { Services.AppLog.Write("notifs look: " + ex); } });
                        After(t + 700, () => SurveyShot(System.IO.Path.Combine(dir, $"notifs-{v}.png")));
                        t += 1100;
                    }
                    After(t + 300, () => { Collapse(); Services.AppLog.Write("notifs look done"); });
                    return;
                }
                if (open == "notes-look")
                {
                    // the Search page on its Notes side, for a look (never takes the keyboard)
                    Expand(byHotkey: true);
                    _expandedAt = DateTime.UtcNow.AddSeconds(25);
                    GoToPage(SearchPage, instant: true);
                    SearchTab(2, animate: false);
                    return;
                }
                if (open == "opentour")
                {
                    // for a look at opening and closing (with DYNAMICNOTCH_FRAMELOG=1): open, close, five times; never takes the keyboard
                    for (int k = 0; k < 5; k++) { After(2000 + k * 2400, () => Expand(byHotkey: false)); After(3200 + k * 2400, Collapse); }
                    return;
                }
                if (open == "pagetour")
                {
                    // for a look at page moves (with DYNAMICNOTCH_FRAMELOG=1): one by one, far jumps, then a fast scrub
                    Expand(byHotkey: true);
                    _expandedAt = DateTime.UtcNow.AddSeconds(40);
                    GoToPage(0, instant: true);
                    int[] tour = { 7, 8, 6, 1, 0, 5, 0, 4, 7, 2 };
                    for (int k = 0; k < tour.Length; k++) { int tp = tour[k]; After(1500 + k * 1100, () => GoToPage(tp)); }
                    int at = 1500 + tour.Length * 1100 + 600;
                    for (int k = 0; k < PageOrder.Length; k++) { int sp = PageOrder[k]; After(at + k * 90, () => GoToPage(sp)); }
                    return;
                }
                if (open == "ring") { var ic = Environment.ProcessPath is { } exe ? Services.IconHelper.ForFile(exe) : null; StartRinging(null, "Discord", "Alex Martin", ic, () => { }); After(9000, StopRinging); return; } // a made-up incoming call (NotchWindow.Ringing)
                if (open.StartsWith("search-look")) // the Search page's search (or, with :ai, its chat) opening, then gone; the keyboard is handed straight back
                {
                    bool ai = open.EndsWith(":ai");
                    var prev = Interop.Native.GetForegroundWindow();
                    Expand(byHotkey: true);
                    GoToPage(SearchPage, instant: true);
                    SearchSide(ai, animate: false);
                    _devLook = true;
                    After(900, () =>
                    {
                        SearchGo(null);
                        if (prev != IntPtr.Zero && prev != _hwnd) { SetActivatable(false); SetForegroundWindow(prev); }
                    });
                    After(5200, () => { _devLook = false; if (_launcherOpen) CloseLauncher(false); After(400, Collapse); });
                    return;
                }
                if (open == "search-flip") { Expand(byHotkey: true); GoToPage(SearchPage, instant: true); SearchSide(false, animate: false); After(2200, () => SearchSide(true, animate: true)); return; } // the Search page, then its AI side (NotchWindow.SearchPage)
                if (open.StartsWith("toasts")) { DevToasts(open.Length > 7 && int.TryParse(open[7..], out int sk) ? sk : 0); return; } // sample notifications (NotchWindow.AlertCard)
                if (open == "welcome")
                {
                    After(3000, () => DevWelcome());
                    return;
                }
                if (open == "welcome-now")
                {
                    _welcomeDev = true;
                    SetMode(Mode.Idle);
                    FaceGet();
                    if (!_faceShown) { _faceGreetedStart = true; FaceShow(true); }
                    bool ok = StartWelcome();
                    Services.AppLog.Write($"welcome dev: started={ok} mode={_mode} base={BaseMode} hidden={_hidden} faceShown={_faceShown} offScreen={_offScreen} dodged={_dodged} w={_sw.Target:0}x{_sh.Target:0}");
                    After(1200, () => Services.AppLog.Write($"welcome dev +1.2s: phase={_welcomePhase} mode={_mode} shape={_sw.Value:0}x{_sh.Value:0} hidden={_hidden} opacity={Root.Opacity:0.00}/{Shell.Opacity:0.00}"));
                    return;
                }
                if (open.StartsWith("work:"))
                {
                    // =work:1 (a tab), =work:1:open (its first project open) — for a look
                    Expand(byHotkey: true);
                    _expandedAt = DateTime.UtcNow.AddSeconds(25);
                    GoToPage(WorkPage, instant: true);
                    ShowWorkTab(int.Parse(open.Split(':')[1]));
                    if (open.EndsWith(":open") && Services.WorkLog.Projects().FirstOrDefault(x => x.Stage < 2) is { } first) { _openProject = first.Kept?.Id ?? first.Id; RenderWorkTab(animate: false); }
                    return;
                }
                if (open.StartsWith("sheet:"))
                {
                    // =sheet:wifi, =sheet:wifi-pw (a password line open, never focused), =sheet:bt — for a look, no keys taken
                    Expand(byHotkey: true);
                    _expandedAt = DateTime.UtcNow.AddSeconds(25); // stays open for the look
                    GoToPage(0, instant: true);
                    After(400, () =>
                    {
                        OpenRadioSheet(open.Contains("bt") ? SheetKind.Bt : SheetKind.Wifi, open.Contains("bt") ? BtTile : WifiTile);
                        if (open.EndsWith("-pw")) After(3500, () => { if (_nets.FirstOrDefault(n => !n.Connected) is { } n) { _netOpen = n.Ssid; _nets = _nets.Select(x => x.Ssid == n.Ssid ? x with { Saved = false, Signal = 70 } : x).ToList(); RenderSheet(); } });
                    });
                    return;
                }
                if (open.StartsWith("typing:"))
                {
                    // =typing:spotify — the box fills one letter every 90 ms (set in code, no keys pressed), each search timed in the log
                    string word = open[7..];
                    OpenLauncher(null);
                    _measureSearch = true;
                    for (int i = 1; i <= word.Length; i++)
                    {
                        string part = word[..i];
                        After(600 + 90 * i, () => { if (_launchBox != null) { _launchBox.Text = part; _launchBox.CaretIndex = part.Length; } });
                    }
                    After(600 + 90 * word.Length + 1500, () => _measureSearch = false);
                    return;
                }
                if (open.StartsWith("search"))
                {
                    // =search, =search:5 km in miles, =search:notch>>2 (and the second level of line 2)
                    string q = open.Length > 7 ? open[7..] : "";
                    int more = q.IndexOf(">>", StringComparison.Ordinal);
                    int line = more >= 0 && int.TryParse(q[(more + 2)..], out int n) ? n : -1;
                    if (more >= 0) q = q[..more];
                    OpenLauncher(q.Length > 0 ? q : null);
                    if (line >= 0) After(2500, () => { if (line < _hits.Count) { _launchSel = line; EnterActions(); } });
                    return;
                }
                if (open == "osd")
                {
                    ShowOsd("message", "", null, "New message", "Discord", null, glyphBrush: Green, seconds: 6, force: true);
                    return;
                }
                Expand(byHotkey: true);
                if (open == "flip") After(1500, () => SetCyber(!_settings.Neon)); // the switch, live
                if (open == "close") After(2000, Collapse); // the closing animation, live
                if (open == "strainlog")
                    After(500, async () =>
                    {
                        // a real reading logged as if it had crossed its line: the whole log, end to end
                        _strain ??= new Services.StrainService();
                        var svc = _strain;
                        await Task.Run(svc.Sample);
                        await Task.Delay(1200);
                        var r = await Task.Run(svc.Sample);
                        RememberStrain(r);
                        await LogStrainAsync(StrainTargets[0], r.Cpu, 20);
                        GoToPage(2, instant: true);
                        OpenStrainLog();
                        if (Services.StrainLog.Events.FirstOrDefault() is { } latest) After(1500, () => RenderLogDetail(latest));
                    });
                if (open == "devices") After(800, () => ShowDevices(true)); // the sound-device list over the cards
                if (open.StartsWith("music")) After(800, () => FlipCard(true, pin: true)); // the music card, even during a call
                if (open == "music-close") After(2500, Collapse); // then the closed notch, the music in front
                if (open == "cycle") for (int i = 1; i <= 10; i++) After(1400 * i, NextTheme); // every theme, twice round
                if (open.StartsWith('p') && int.TryParse(open[1..].TrimEnd('h'), out int pg))
                {
                    GoToPage(pg, instant: true);
                    // =p8h: the Search page with its fourth usual app as if the pointer rested on it (for a look)
                    if (open.EndsWith('h') && pg == SearchPage) After(3500, () =>
                    {
                        if (_spBody?.Children.OfType<StackPanel>().FirstOrDefault()?.Children.OfType<System.Windows.Controls.Primitives.UniformGrid>().FirstOrDefault() is { Children.Count: > 3 } row)
                            row.Children[3].RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0) { RoutedEvent = System.Windows.Input.Mouse.MouseEnterEvent });
                    });
                }
            });
    }

    /// <summary>
    /// The Neon frame's soft glow, when open or alerting (never in Eco). Not while the notch springs open or shut: the
    /// frame's shape changes every frame then, and the blur (and its cached picture) would be made again each time. It
    /// comes on when the spring settles. The glow is kept as a picture while it stays put: the rain moving under it
    /// doesn't make it redraw every frame.
    /// </summary>
    void CyberFrameGlow()
    {
        if (!_cyber || _cyFrame == null) return;
        var edge = CustomEdge ?? CyYellow;
        bool want = _mode is Mode.Expanded or Mode.Osd && !Eco && !_animating;
        if (!want) { if (_cyFrame.Effect != null) { _cyFrame.Effect = null; _cyFrame.CacheMode = null; } return; }
        if (_cyFrame.Effect is System.Windows.Media.Effects.DropShadowEffect d && d.Color == edge && _cyFrame.CacheMode != null) return; // as it is
        _cyFrame.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = edge, BlurRadius = 8, ShadowDepth = 0, Opacity = 0.55 };
        _cyFrame.CacheMode = new BitmapCache(Math.Max(1, _scale)) { SnapsToDevicePixels = true };
    }

    /// <summary>A picture of the notch as it is on screen (for the survey).</summary>
    void SurveyShot(string file)
    {
        try
        {
            var tl = Pill.PointToScreen(new Point(-12, 0));
            var br = Pill.PointToScreen(new Point(Pill.ActualWidth + 12, Pill.ActualHeight + 14));
            int w = (int)(br.X - tl.X), h = (int)(br.Y - tl.Y);
            if (w < 10 || h < 10) return;
            using var bmp = new System.Drawing.Bitmap(w, h);
            using (var g = System.Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen((int)tl.X, (int)tl.Y, 0, 0, bmp.Size);
            bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
        }
        catch (Exception ex) { Services.AppLog.Write("survey: " + ex.Message); }
    }

    /// <summary>For a look at the resting island: the top of the screen around it (it floats below the edge).</summary>
    void IslandShot(string file)
    {
        try
        {
            var mid = Shell.PointToScreen(new Point(Shell.ActualWidth / 2, 0));
            int w = 520, h = 64, x = (int)mid.X - w / 2, y = (int)mid.Y - 4; // (the screen's top, wherever it is among several)
            using var bmp = new System.Drawing.Bitmap(w, h);
            using (var g = System.Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen(x, y, 0, 0, bmp.Size);
            bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
        }
        catch (Exception ex) { Services.AppLog.Write("survey: " + ex.Message); }
    }

    /// <summary>The theme was switched (the tray icon follows it).</summary>
    public event Action<bool>? CyberChanged;

    void ApplyCyber(bool on, bool animate)
    {
        if (animate) After(120, () => FeelIt(Feel.Spin)); // a new look: the face spins into it
        // switching back: one last burst in the old skin, then it's gone
        if (animate && !on) CyberGlitch(strong: true);
        _cyber = on;
        if (!on) ForgetThemeFonts();
        _themeFont = on ? CyFont : null;
        _themeStretch = on ? FontStretches.SemiCondensed : FontStretches.Normal;

        var body = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) };
        body.GradientStops.Add(new GradientStop(Colors.Black, 0));
        body.GradientStops.Add(new GradientStop(CyNight, 0.55));
        body.GradientStops.Add(new GradientStop(Color.FromRgb(0x04, 0x13, 0x1D), 1));
        body.Freeze();
        _cyberBody = body;
        Pill.Background = on ? body : _pillBgBefore;
        // angular ears: a 45° flare into the screen's edge
        EarL.Data = on ? Geometry.Parse("M0,0 L10,0 L10,10 Z") : _earLBefore;
        EarR.Data = on ? Geometry.Parse("M10,0 L0,0 L0,10 Z") : _earRBefore;
        Rim.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        _hud.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on)
        {
            Pill.Clip = null;
            Stage.Clip = _clip;
            // nothing of the skin keeps ticking once it's off (a forever animation would keep the renderer awake)
            _cyTrace.BeginAnimation(OpacityProperty, null);
            _cyFrame.Effect = null;
            foreach (var t in _decoding.Values) t.Stop();
            _decoding.Clear();
        }

        FontFamily = on ? CyFont : _windowFontBefore;
        FontStretch = on ? FontStretches.SemiCondensed : FontStretches.Normal;
        if (on) { _skinMap = CyMap; _skinPrefix = "Cy"; _skinCuts = true; }
        SkinAll(on); // the content: controls, cards, colours, gauges (NotchWindow.CyberSkin)
        CyberPills(on); // the side pills' plates, the data blip (NotchWindow.CyberPills)
        CyberArt(on); // your faded pictures behind the pages (NotchWindow.CyberArt)
        Recolor("Dim", ref _dimBefore, on ? Color.FromArgb(0xB4, CyCyan.R, CyCyan.G, CyCyan.B) : null);
        Recolor("Tile", ref _tileBefore, on ? Color.FromRgb(0x0A, 0x1A, 0x24) : null);
        ApplyClockColor();
        ApplyPanelColor(); // your panel colour wins over the theme's body
        if (_devicesOpen) StyleDevicesBack(); // the device list open right now follows the switch
        StyleArtDisc(); // the cover: a spinning record in this theme, the square one in the others
        RefreshDesktopBtn(); // round, or cut like its neighbours
        UpdateRain(); // the rain comes and goes with the theme
        if (!on) Untarget();
        Controls.BatteryIcon.Health = on; // the battery as a segmented health bar
        Battery.Restyle();
        OsdBattery.Restyle();
        CyberRamCells(0);
        CyberProgress(null, default); // the activity pill's bar comes back with its next update
        GameFps.Foreground = on ? Frozen(Color.FromArgb(0xE6, CyYellow.R, CyYellow.G, CyYellow.B)) : _gameFpsBefore;
        GameFpsUnit.Foreground = on ? Frozen(Color.FromArgb(0x8C, CyCyan.R, CyCyan.G, CyCyan.B)) : Frozen(Color.FromArgb(0x73, 0xFF, 0xFF, 0xFF));

        ApplyRadius();
        ApplyAccent();
        _panelTint = null;
        UpdatePanelTint();
        UpdateRim();
        StyleCyberBtn();
        CyberHudForMode();
        if (animate && on)
        {
            // boot: a hard burst, the scan line, the header decoding itself
            CyberGlitch(strong: true);
            if (_mode == Mode.Expanded) CyberScan();
            CyberDecode(ClockText);
            CyberDecode(PageTitle);
        }
    }

    /// <summary>Changes a shared brush's colour in place (every text using it follows), remembering the original.</summary>
    static void Recolor(string key, ref Color? before, Color? to)
    {
        if (Application.Current.TryFindResource(key) is not SolidColorBrush b || b.IsFrozen) return;
        if (to is { } c) { before ??= b.Color; b.Color = c; }
        else if (before is { } old) { b.Color = old; before = null; }
    }

    // ───────────── fonts ─────────────

    static bool IsIconFont(FontFamily f) =>
        f.Source.Contains("Icons", StringComparison.OrdinalIgnoreCase) || f.Source.Contains("MDL2", StringComparison.OrdinalIgnoreCase)
        || f.Source.Contains("Emoji", StringComparison.OrdinalIgnoreCase) || f.Source.Contains("Symbol", StringComparison.OrdinalIgnoreCase);

    static bool IsDisplayFont(FontFamily f) => f is not null && (ReferenceEquals(f, _coffeeDisplay) || ReferenceEquals(f, _coffeeHand));

    /// <summary>Code and numbers set in a monospace font keep it (their columns line up).</summary>
    static bool IsMonoFont(FontFamily f) =>
        f.Source.Contains("Consolas", StringComparison.OrdinalIgnoreCase) || f.Source.Contains("Mono", StringComparison.OrdinalIgnoreCase)
        || f.Source.Contains("Cascadia", StringComparison.OrdinalIgnoreCase) || f.Source.Contains("Courier", StringComparison.OrdinalIgnoreCase);

    /// <summary>The font a text made now should wear: the theme's (Neon's, Coffee's), else the notch's own.</summary>
    static FontFamily ThemeText => _themeFont ?? UiText;

    /// <summary>
    /// A theme's font is leaving: every text it dressed gets its own back, the ones on screen and the ones that aren't
    /// (a closed menu, a page not built into the tree right now), so none keeps the old theme's type into the next.
    /// </summary>
    static void ForgetThemeFonts()
    {
        foreach (var (tb, f) in _fontBefore.ToList())
        {
            if (ReferenceEquals(tb.FontFamily, _themeFont)) { tb.FontFamily = f.Family; tb.FontStretch = f.Stretch; } // (one its own code changed since keeps that)
        }
        _fontBefore.Clear();
    }

    static void Retype(TextBlock tb, bool on)
    {
        if (on)
        {
            if (_themeFont == null) return;
            bool keep = IsIconFont(tb.FontFamily) || IsDisplayFont(tb.FontFamily) || IsMonoFont(tb.FontFamily);
            if (_fontBefore.TryGetValue(tb, out var known))
            {
                // dressed before, then given a font again by its own code: the theme's again, that one remembered
                if (!keep && !ReferenceEquals(tb.FontFamily, _themeFont)) { known.Family = tb.FontFamily; known.Stretch = tb.FontStretch; tb.FontFamily = _themeFont; tb.FontStretch = _themeStretch; }
                return;
            }
            // texts that just inherit the window's font follow it; only an explicit font is swapped (icons never, nor
            // the theme's own display faces: Coffee's serif clock, its handwritten titles; nor monospace)
            if (tb.ReadLocalValue(TextBlock.FontFamilyProperty) == DependencyProperty.UnsetValue || keep) return;
            _fontBefore.Add(tb, new FontBefore { Family = tb.FontFamily, Stretch = tb.FontStretch });
            tb.FontFamily = _themeFont;
            tb.FontStretch = _themeStretch;
        }
        else if (_fontBefore.TryGetValue(tb, out var f))
        {
            tb.FontFamily = f.Family;
            tb.FontStretch = f.Stretch;
            _fontBefore.Remove(tb);
        }
    }

    static void RetypeAll(DependencyObject node)
    {
        if (node is TextBlock tb) Retype(tb, _cyberFonts);
        int n = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(node) : 0;
        for (int i = 0; i < n; i++) RetypeAll(VisualTreeHelper.GetChild(node, i));
    }

    // ───────────── the HUD layer ─────────────

    void BuildHud()
    {
        _hud = new Canvas { IsHitTestVisible = false, Visibility = Visibility.Collapsed, ClipToBounds = false };

        // scanlines: one faint cyan line every 3 px (open panel only)
        var lines = new DrawingGroup();
        lines.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 1, 3))));
        lines.Children.Add(new GeometryDrawing(Frozen(Color.FromArgb(0x10, CyCyan.R, CyCyan.G, CyCyan.B)), null, new RectangleGeometry(new Rect(0, 0, 1, 1))));
        var lineBrush = new DrawingBrush(lines) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 1, 3), ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.None };
        lineBrush.Freeze();
        _cyLines = new Rectangle { Fill = lineBrush };

        // hazard stripes
        var stripes = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(6, 6), MappingMode = BrushMappingMode.Absolute, SpreadMethod = GradientSpreadMethod.Repeat };
        stripes.GradientStops.Add(new GradientStop(CyYellow, 0));
        stripes.GradientStops.Add(new GradientStop(CyYellow, 0.5));
        stripes.GradientStops.Add(new GradientStop(Colors.Transparent, 0.5));
        stripes.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        stripes.Freeze();
        _cyStripes = new Rectangle { Width = 34, Height = 4, Fill = stripes, Opacity = 0.85 };

        _cyTrace = new Path { Stroke = CyCyanBrush, StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Miter };
        _cyGhost = new Path { Stroke = CyCyanBrush, StrokeThickness = 2, Opacity = 0, RenderTransform = _cyGhostShift };
        _cyFrame = new Path { Stroke = Frozen(Color.FromArgb(0xF0, CyYellow.R, CyYellow.G, CyYellow.B)), StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Miter };
        _cyTicks = new Path { Stroke = Frozen(Color.FromArgb(0x90, CyYellow.R, CyYellow.G, CyYellow.B)), StrokeThickness = 1 };
        // the dress: bright brackets on the cut corners; on the island also a tight glow round the frame (all still:
        // nothing here ticks)
        _cyCorners = new Path { Stroke = CyYellowBrush, StrokeThickness = 2.4, StrokeLineJoin = PenLineJoin.Miter };
        // the neon glow: the frame's line, blurred once and kept as a picture (redrawn only when the shape changes)
        _cyHalo = new Path
        {
            StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Miter, Visibility = Visibility.Collapsed,
            Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 4, KernelType = System.Windows.Media.Effects.KernelType.Gaussian },
            CacheMode = new BitmapCache { SnapsToDevicePixels = true },
        };
        // a pulse of light that runs once round the frame now and then (NotchWindow.CyberPills: CyberBlip)
        _cyRun = new Path { StrokeThickness = 2.2, StrokeLineJoin = PenLineJoin.Miter, Opacity = 0, StrokeDashCap = PenLineCap.Flat };
        var ticks = new StreamGeometry();
        using (var ctx = ticks.Open())
            for (int i = 0; i < 12; i++)
            {
                double y = i * 5 + 0.5;
                ctx.BeginFigure(new Point(0, y), false, false);
                ctx.LineTo(new Point(i % 4 == 0 ? 6 : 3, y), true, false);
            }
        ticks.Freeze();
        _cyTicks.Data = ticks;
        _cyTag = new TextBlock
        {
            Text = "DN-99 // NOTCH.OS", FontFamily = CyFont, FontStretch = FontStretches.Condensed, FontSize = 7.5,
            Foreground = Frozen(Color.FromArgb(0xB0, CyYellow.R, CyYellow.G, CyYellow.B)),
        };

        // the scan line: a bright cyan edge with a fading trail above it
        var scan = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) };
        scan.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
        scan.GradientStops.Add(new GradientStop(Color.FromArgb(0x26, CyCyan.R, CyCyan.G, CyCyan.B), 0.8));
        scan.GradientStops.Add(new GradientStop(Color.FromArgb(0xE0, CyCyan.R, CyCyan.G, CyCyan.B), 0.95));
        scan.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        scan.Freeze();
        _cyScan = new Rectangle { Height = 24, Fill = scan, Opacity = 0, RenderTransform = _cyScanShift };

        // signal slices for the glitch
        _cyBars = Enumerable.Range(0, 5).Select(i => new Rectangle { Opacity = 0, Fill = i % 2 == 0 ? CyYellowBrush : CyCyanBrush }).ToArray();

        foreach (UIElement e in new UIElement[] { _cyLines, _cyScan, _cyTrace, _cyTicks, _cyStripes, _cyTag, _cyHalo, _cyGhost, _cyFrame, _cyCorners, _cyRun })
            _hud.Children.Add(e);
        foreach (var b in _cyBars) _hud.Children.Add(b);
        Stage.Children.Add(_hud);
    }

    /// <summary>The cut shape, its frame and its traces for the notch's current size (every frame while it moves).</summary>
    void CyberShape(double w, double h, double r)
    {
        bool tag = _mode == Mode.Game;                    // the FPS tag: small even cuts, nothing more
        double c1 = tag ? 3.5 : Math.Clamp(r * 0.8, 1.5, h * 0.45);   // bottom-left cut
        double c2 = tag ? 3.5 : Math.Clamp(r * 1.35, 2, h * 0.6);     // bottom-right cut, deeper: asymmetric like a HUD panel
        double s = h > 26 ? 3.5 : 0;                      // a step in the bottom edge
        double xs = Math.Round(w * 0.62);

        // floating (the island, NotchWindow.Island): the top corners cut too, mirrored (deeper on the left), and the
        // frame closes along the top; attached, the top is the screen's edge. One spring between the two.
        double a = tag ? 0 : Isle, ty = -(1 - a);
        double t1 = a * Math.Clamp(r * 1.1, 2, h * 0.4), t2 = a * Math.Clamp(r * 0.7, 1.5, h * 0.32);
        // the island's visor slot: a small notch cut into the middle of its top edge (it deepens as the
        // island lets go of the edge, so attaching smooths it away)
        bool compact = !tag && h <= 40 && _mode != Mode.Expanded;
        // (only with no face: the face's head would sit right under it), in the middle of what's between the slots
        double nd = compact && w > 80 && !_faceShown ? 2.5 * a : 0, nx = (SlotRoomL + w - SlotRoomR) / 2;
        Pill.Clip = nd > 0.05
            ? Poly(true, new(t1, ty), new(nx - 11, ty), new(nx - 7, ty + nd), new(nx + 7, ty + nd), new(nx + 11, ty), new(w - t2, ty), new(w, ty + t2), new(w, h - s - c2), new(w - c2, h - s), new(xs + s, h - s), new(xs, h), new(c1, h), new(0, h - c1), new(0, ty + t1))
            : Poly(true, new(t1, ty), new(w - t2, ty), new(w, ty + t2), new(w, h - s - c2), new(w - c2, h - s), new(xs + s, h - s), new(xs, h), new(c1, h), new(0, h - c1), new(0, ty + t1));
        if (Stage.Clip != null) Stage.Clip = null;
        var frame = a > 0.02
            ? nd > 0.05
                ? Poly(true, new(t1, 0), new(0, t1), new(0, h - c1), new(c1, h), new(xs, h), new(xs + s, h - s), new(w - c2, h - s), new(w, h - s - c2), new(w, t2), new(w - t2, 0), new(nx + 11, 0), new(nx + 7, nd), new(nx - 7, nd), new(nx - 11, 0))
                : Poly(true, new(t1, 0), new(0, t1), new(0, h - c1), new(c1, h), new(xs, h), new(xs + s, h - s), new(w - c2, h - s), new(w, h - s - c2), new(w, t2), new(w - t2, 0))
            : Poly(false, new(0, 0), new(0, h - c1), new(c1, h), new(xs, h), new(xs + s, h - s), new(w - c2, h - s), new(w, h - s - c2), new(w, 0));
        _cyFrame.Data = frame;
        _cyGhost.Data = frame;

        // brackets on the cut corners (the top ones only on the island)
        double arm = compact ? 6 : 11;
        var cg = new StreamGeometry();
        using (var ctx = cg.Open())
        {
            void Bracket(Point p1, Point p2, Point p3, Point p4) { ctx.BeginFigure(p1, false, false); ctx.PolyLineTo(new[] { p2, p3, p4 }, true, false); }
            if (a > 0.02)
            {
                Bracket(new(t1 + arm, 0), new(t1, 0), new(0, t1), new(0, t1 + arm));
                Bracket(new(w - t2 - arm, 0), new(w - t2, 0), new(w, t2), new(w, t2 + arm));
            }
            Bracket(new(w, h - s - c2 - arm), new(w, h - s - c2), new(w - c2, h - s), new(w - c2 - arm, h - s));
            Bracket(new(c1 + arm, h), new(c1, h), new(0, h - c1), new(0, h - c1 - arm));
        }
        cg.Freeze();
        _cyCorners.Data = cg;
        // the island's dress: a tight glow round the frame (nothing drawn through what it shows)
        bool dress = compact && a > 0.5 && w > 60;
        if (dress)
        {
            _cyHalo.Data = frame;
            _cyRun.Data = frame;
            _cyPerim = 2 * (w + h);
        }
        var dv = dress ? Visibility.Visible : Visibility.Collapsed;
        if (_cyRun.Visibility != dv) _cyRun.Visibility = dv;
        // the halo is a blur, redrawn with every frame the shape moves: never in power-saving mode (like the frame's glow)
        var hv = dress && !Eco ? Visibility.Visible : Visibility.Collapsed;
        if (_cyHalo.Visibility != hv) _cyHalo.Visibility = hv;

        if (h > 20)
        {
            var g = new StreamGeometry();
            bool any = true;
            using (var ctx = g.Open())
            if (compact) any = false; // the island: its corner brackets are the dress, no lines through what it shows
            else
            {
                if (_mode == Mode.Expanded)
                {
                    // open: the part-number tag sits bottom left; a fine trace leads from it towards the page bar
                    var (barL, _) = BarSpan(w);
                    double y = h - 9, x0 = c1 + 8 + (_cyTag.ActualWidth > 0 ? _cyTag.ActualWidth : 64) + 6, end = barL - 10;
                    if (end > x0 + 8)
                    {
                        ctx.BeginFigure(new Point(x0, y), false, false);
                        ctx.LineTo(new Point(end, y), true, false);
                        ctx.BeginFigure(new Point(end, y - 2.5), false, false);
                        ctx.LineTo(new Point(end, y + 2.5), true, false);
                    }
                }
                else
                {
                    // a pop-up: one along the bottom left that climbs a step
                    double y = h - 4.5, len = Math.Min(70, w * 0.22), x0 = c1 + 6;
                    ctx.BeginFigure(new Point(x0, y), false, false);
                    ctx.LineTo(new Point(x0 + len, y), true, false);
                    ctx.LineTo(new Point(x0 + len + 5, y - 5), true, false);
                    ctx.LineTo(new Point(x0 + len + 5 + len * 0.5, y - 5), true, false);
                }
                double rx = w - 4.5, ry = h - s - c2 - 5;
                ctx.BeginFigure(new Point(rx, ry), false, false);
                ctx.LineTo(new Point(rx, Math.Max(4, ry - Math.Min(40, h * 0.35))), true, false);
            }
            _cyTrace.StrokeThickness = _mode == Mode.Expanded ? 1.2 : 1.6;
            g.Freeze();
            _cyTrace.Data = g;
            _cyTrace.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        }
        else _cyTrace.Visibility = Visibility.Collapsed;

        _cyLines.Width = w;
        _cyLines.Height = h;
        _cyLines.Clip = Pill.Clip; // the scanlines stay inside the cut shape
        CrtPlace(w, h); // a closing CRT's line and dot ride the shrinking shape's centre
        _cyScan.Width = w;
        Canvas.SetLeft(_cyTicks, 5);
        Canvas.SetTop(_cyTicks, Math.Max(6, h * 0.22));
        Canvas.SetLeft(_cyStripes, w - c2 - 44);
        Canvas.SetTop(_cyStripes, h - s - 8);
        Canvas.SetLeft(_cyTag, c1 + 8); // bottom left: the page bar has the middle
        Canvas.SetTop(_cyTag, h - 14);
        foreach (var b in _cyBars) b.MaxWidth = w;
    }

    /// <summary>Where the page bar sits across the open panel (in the HUD's space); a sensible guess before it's laid out.</summary>
    (double L, double R) BarSpan(double w)
    {
        if (Dots.IsVisible && Dots.ActualWidth > 0 && _hud.IsVisible)
            try { var p = Dots.TranslatePoint(new Point(0, 0), _hud); return (p.X, p.X + Dots.ActualWidth); } catch (InvalidOperationException) { }
        return (w / 2 - 96, w / 2 + 96);
    }

    static Geometry Poly(bool closed, params Point[] pts)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(pts[0], closed, closed);
            for (int i = 1; i < pts.Length; i++) ctx.LineTo(pts[i], true, false);
        }
        g.Freeze();
        return g;
    }

    /// <summary>What the HUD shows in each mode: the full dress open, a lean frame otherwise, nothing moving in a game.</summary>
    void CyberHudForMode()
    {
        if (!_cyber) return;
        bool open = _mode == Mode.Expanded;
        var extras = open ? Visibility.Visible : Visibility.Collapsed;
        if (open) CyberTag();
        _cyTicks.Visibility = _cyStripes.Visibility = _cyTag.Visibility = extras;
        // the island wears the scanlines too, fainter
        bool isleMode = _mode is Mode.Idle or Mode.Media or Mode.Activity or Mode.Slim;
        _cyLines.Visibility = open || isleMode && !Eco ? Visibility.Visible : Visibility.Collapsed;
        _cyLines.Opacity = open ? 1 : 0.45;
        // the frame in your edge colour (yellow by default); a soft glow of it when open or alerting, never in a game
        var edge = CustomEdge ?? CyYellow;
        if (_cyFrame.Stroke is not SolidColorBrush fs || fs.Color.R != edge.R || fs.Color.G != edge.G || fs.Color.B != edge.B)
            _cyFrame.Stroke = Frozen(Color.FromArgb(0xF0, edge.R, edge.G, edge.B));
        CyberFrameGlow();
        _cyTrace.Opacity = _mode == Mode.Game ? 0 : 1;
        // in a game the pill is just the number: no frame line around it, nothing bright at the edge of your view
        // (on the island the frame steps back a little so the bright corner brackets carry it)
        _cyFrame.Opacity = _mode == Mode.Game ? 0 : isleMode ? 0.9 : 1; // the island: one crisp line, its glow kept tight
        if (_cyRun.Stroke == null || _cyCorners.Stroke is not SolidColorBrush cs || cs.Color != edge)
        {
            _cyCorners.Stroke = Frozen(edge);
            _cyHalo.Stroke = Frozen(Color.FromArgb(0x80, edge.R, edge.G, edge.B));
            // the pulse: the edge colour lit towards white, its head bright and its tail fading
            var hot = Color.FromRgb((byte)((edge.R + 255 * 2) / 3), (byte)((edge.G + 255 * 2) / 3), (byte)((edge.B + 255 * 2) / 3));
            _cyRun.Stroke = Frozen(hot);
        }
        _cyCorners.StrokeThickness = isleMode ? 2 : 3;
        _cyCorners.Opacity = _mode == Mode.Game ? 0 : 1;
        if (_mode == Mode.Game) { Fade(EarL, 0, 120); Fade(EarR, 0, 120); } // no flares beside the tag
        if (open && !Eco)
        {
            // a slow breath: 20 frames a second look the same and cost a fraction of the screen's full refresh rate
            var breath = new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(1400))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
            Timeline.SetDesiredFrameRate(breath, 20);
            _cyTrace.BeginAnimation(OpacityProperty, breath);
        }
        else _cyTrace.BeginAnimation(OpacityProperty, null);
        CyberTint();
    }

    /// <summary>The traces wear the pop-up's colour while it's up, cyan otherwise; the frame stays yellow.</summary>
    void CyberTint()
    {
        if (!_cyber) return;
        var c = _mode == Mode.Osd && _osdAccent is { } a ? CyMap(a) : _mode == Mode.Expanded && CurrentActivity == LiveActivity.Call ? ((SolidColorBrush)CallBrush).Color : CyCyan;
        if (_cyTrace.Stroke is SolidColorBrush b && b.Color == c) return;
        _cyTrace.Stroke = Frozen(c);
    }

    /// <summary>A mode change in the cyber skin: a glitch, plus the scan line when it opens. Never in a game.</summary>
    void CyberMoment(Mode from, Mode to)
    {
        if (!_cyber) return;
        CyberHudForMode();
        CyberSkinSoon();
        if (to == Mode.Game || from == Mode.Game || to == Mode.Slim && from == Mode.Idle) return;
        if (to == Mode.Expanded) { CyberArtShow(); CyberGlitch(strong: false); CyberScan(); CyberBoot(); CyberDecode(PageTitle); }
        else if (to == Mode.Osd) CyberGlitch(strong: false, light: true); // a flicker, not a jolt: pop-ups come often
        else if (from == Mode.Expanded && CyberCrtOff()) { } // closing: the CRT switches off (NotchWindow.CyberFx)
        else if (from == Mode.Expanded || from == Mode.Osd) CyberGlitch(strong: false, light: true);
    }

    // ───────────── motion ─────────────

    /// <summary>
    /// Signal glitch: slices of yellow and cyan flash across it, a cyan ghost of the frame splits off and snaps back,
    /// the whole shape jolts sideways in hard steps and the content flickers. ~200 ms.
    /// </summary>
    void CyberGlitch(bool strong, bool light = false, Color? tint = null)
    {
        if (Eco && !strong) return;
        _cyGhost.Stroke = tint is { } gt ? Frozen(gt) : CyCyanBrush;
        double w = Math.Max(1, Pill.ActualWidth), h = Math.Max(1, Pill.ActualHeight);
        int bars = light ? 2 : strong ? _cyBars.Length : 3;
        for (int i = 0; i < _cyBars.Length; i++)
        {
            var b = _cyBars[i];
            if (i >= bars) { b.BeginAnimation(OpacityProperty, null); b.Opacity = 0; continue; }
            b.Fill = tint is { } bt ? Frozen(bt) : i % 2 == 0 ? CyYellowBrush : CyCyanBrush;
            b.Width = w * (0.12 + _cyRand.NextDouble() * (strong ? 0.5 : 0.3));
            b.Height = 1 + _cyRand.Next(strong ? 4 : 2);
            Canvas.SetLeft(b, _cyRand.NextDouble() * (w - b.Width));
            Canvas.SetTop(b, _cyRand.NextDouble() * Math.Max(1, h - b.Height));
            int t0 = _cyRand.Next(0, strong ? 140 : 80), on = 35 + _cyRand.Next(50);
            var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(t0 + on + 90) };
            a.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            a.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.9, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(t0))));
            a.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(t0 + on))));
            a.KeyFrames.Add(new DiscreteDoubleKeyFrame(strong ? 0.6 : 0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(t0 + on + 40))));
            a.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(t0 + on + 80))));
            b.BeginAnimation(OpacityProperty, a);
        }
        // the content tears sideways for a few frames, like a bad signal (a pixel or two: felt more than seen)
        _stageTear ??= new TranslateTransform();
        if (Stage.RenderTransform != _stageTear) Stage.RenderTransform = _stageTear;
        if (strong) Steps(_stageTear, TranslateTransform.XProperty, 28, 0, -3, 2.5, -1, 0);
        else if (light) Steps(_stageTear, TranslateTransform.XProperty, 26, 0, 1.2, 0);
        else Steps(_stageTear, TranslateTransform.XProperty, 28, 0, -1, 0.6, 0);
        if (light) return;

        // the cyan ghost: chromatic split of the frame
        Steps(_cyGhost, OpacityProperty, 32, 0, 0.9, 0.5, 0.9, 0.3, 0);
        if (strong) Steps(_cyGhostShift, TranslateTransform.XProperty, 32, 0, 3, -2.5, 2, -1, 0);
        else Steps(_cyGhostShift, TranslateTransform.XProperty, 32, 0, 2, -1.5, 1, 0); // everyday: a finer split
        Steps(_cyGhostShift, TranslateTransform.YProperty, 32, 0, 1, 0, -1, 0, 0);
        // the jolt and the flicker
        _shellShake ??= new TranslateTransform();
        if (Shell.RenderTransform != _shellShake) Shell.RenderTransform = _shellShake;
        // only a strong glitch jolts the whole notch; everyday ones stay inside it (a notch that jumps feels cheap)
        if (strong) Steps(_shellShake, TranslateTransform.XProperty, 30, -3, 2.5, -1.5, 1, 0);
        if (strong) Steps(Stage, OpacityProperty, 30, 1, 0.55, 1, 0.8, 1);
    }

    /// <summary>Hard steps (no easing), the way a bad signal moves.</summary>
    static void Steps(IAnimatable target, DependencyProperty p, int stepMs, params double[] values)
    {
        var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(stepMs * values.Length) };
        for (int i = 0; i < values.Length; i++) a.KeyFrames.Add(new DiscreteDoubleKeyFrame(values[i], KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(stepMs * i))));
        a.FillBehavior = FillBehavior.Stop;
        target.BeginAnimation(p, a);
    }

    /// <summary>A scan line sweeps down the opening panel.</summary>
    void CyberScan()
    {
        if (Eco) return;
        _cyScan.Opacity = 1;
        var move = new DoubleAnimation(-24, ExpandedSize.Height + 4, TimeSpan.FromMilliseconds(620))
            { BeginTime = TimeSpan.FromMilliseconds(90), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };
        move.Completed += (_, _) => _cyScan.Opacity = 0;
        _cyScanShift.BeginAnimation(TranslateTransform.YProperty, move);
    }

    /// <summary>A text decodes itself: scrambled symbols resolve into the real letters, left to right, in ~270 ms.</summary>
    /// <param name="cursor">a block cursor rides the edge of the decoded text while it resolves (pop-ups)</param>
    /// <param name="holdMs">fully scrambled for a moment before it starts resolving (the line under a title)</param>
    void CyberDecode(TextBlock tb, bool cursor = false, int holdMs = 0)
    {
        if (!_cyber || Eco) return;
        string final = tb.Text;
        if (string.IsNullOrWhiteSpace(final) || final.Length > 48) return;
        if (_decoding.Remove(tb, out var old)) old.Stop();
        const int steps = 9;
        int step = -(holdMs / 30); // negative: still holding
        string written = "";
        var t = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(30) };
        t.Tick += (_, _) =>
        {
            // something else changed the text meanwhile (the clock ticked, a new title): it wins
            if (written.Length > 0 && tb.Text != written) { t.Stop(); _decoding.Remove(tb); return; }
            step++;
            if (step >= steps) { tb.Text = final; t.Stop(); _decoding.Remove(tb); return; }
            int shown = final.Length * Math.Max(0, step) / steps;
            var chars = final.ToCharArray();
            for (int i = shown; i < chars.Length; i++)
                if (!char.IsWhiteSpace(chars[i])) chars[i] = Scramble[_cyRand.Next(Scramble.Length)];
            written = new string(chars);
            if (cursor) written = written[..shown] + "\u2588" + written[Math.Min(written.Length, shown + 1)..];
            tb.Text = written;
        };
        _decoding[tb] = t;
        t.Start();
    }

    /// <summary>The button: solid yellow to switch it on; dark with a yellow edge while it's on.</summary>
    void StyleCyberBtn()
    {
        // the theme button wears the theme it's on; a click moves to the next one
        string key = ThemeKey;
        var sig = key switch { "neon" => CyYellow, "original" => Colors.White, _ => LookFor(key)?.Signature ?? Colors.White };
        bool dark = key == "neon";
        CyberBtn.Background = dark ? Frozen(CyNight) : Frozen(sig);
        CyberBtn.BorderBrush = dark ? CyYellowBrush : Frozen(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
        CyberBtn.Foreground = dark ? CyYellowBrush : Frozen(CyNight);
        CyberBtn.ToolTip = string.Format(Services.Loc.T("Theme: {0} · click for the next"), Services.Loc.T(ThemeNames[key]));
    }

    /// <summary>Slider fills in the cyber skin: olive into yellow.</summary>
    Brush CyberFill()
    {
        var g = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        g.GradientStops.Add(new GradientStop(CyOlive, 0));
        g.GradientStops.Add(new GradientStop(CyYellow, 1));
        g.Freeze();
        return g;
    }
}
