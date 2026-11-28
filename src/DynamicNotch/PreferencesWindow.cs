using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using DynamicNotch.Controls;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// Preferences (right-click the tray icon): every setting of the notch in one calm window.
///  • a sidebar with the sections and a search box that finds any switch, choice or group by name or description,
///    whatever section it lives in (a group found takes you to it);
///  • drawn with one kit (PreferencesWindow.Kit): a graphite ramp, the theme's accent, and controls that all work
///    with the keyboard;
///  • Appearance shows both themes as live miniature previews, and a colour mixer (wheel, brightness, the four
///    things to paint, presets, shuffle) that paints the real notch while you drag;
///  • Pages lets you hide the notch pages you don't use; Language switches the notch and this window between
///    English, French and Arabic (right to left);
///  • every change applies and saves at once ("Saved" flashes), the notch's own switches follow.
/// Built only when opened; on Windows 11 it sits on Mica, the system's own translucent material. Nothing runs
/// while it's closed.
/// </summary>
public sealed partial class PreferencesWindow : Window
{
    static PreferencesWindow? _open;

    public static void ShowFor(NotchWindow notch, int section = -1)
    {
        if (_open != null)
        {
            if (_open.WindowState == WindowState.Minimized) _open.WindowState = WindowState.Normal;
            if (section >= 0 && (_open._sec != (Sec)section || _open._query.Length > 0))
            {
                _open._sec = (Sec)section;
                if (_open._query.Length > 0) _open._search.Text = ""; // (that shows the section)
                else { _open.RenderNav(); _open.RenderContent(); }
            }
            _open.Activate();
            return;
        }
        _open = new PreferencesWindow(notch);
        if (section >= 0) _open._sec = (Sec)section;
        _open.Closed += (_, _) => _open = null;
        _open.Show();
        _open.Activate();
    }

    enum Sec { General, Appearance, Behaviour, Alerts, Pages, Shortcuts, About, Search, Calls, Ai }

    /// <summary>The AI chat's section (for the chat's header to open it directly).</summary>
    public const int AiSection = (int)Sec.Ai;
    static readonly (Sec Sec, string Glyph, string Name, string Sub)[] Sections =
    {
        (Sec.General, "", "General", "Language, start-up and power"),
        (Sec.Appearance, "", "Appearance", "Theme, colours and how the notch looks"),
        (Sec.Behaviour, "", "Behaviour", "How the notch reacts to you"),
        (Sec.Search, "\uE721", "Search", "What it finds, how it opens, what it remembers"),
        (Sec.Ai, "\uE8BD", "AI chat", "The assistant in the search: key, model, how it answers, what it may do"),
        (Sec.Calls, "\uE717", "Calls", "The Calls page, and what happens when a call starts"),
        (Sec.Alerts, "", "Alerts", "What the notch tells you, and the smart helpers"),
        (Sec.Pages, "", "Pages", "Choose the pages of the control center"),
        (Sec.Shortcuts, "", "Shortcuts", "Keys and gestures"),
        (Sec.About, "", "About", "Version, files and reset"),
    };

    sealed record Row(Sec Sec, string Glyph, string Title, string Desc, Func<bool> Get, Action<bool> Set, string? Group = null, int Page = -1);

    readonly NotchWindow _notch;
    readonly Settings _s;
    Sec _sec = Sec.General;
    string _query = "";
    bool _mica;
    readonly StackPanel _nav = new();
    readonly StackPanel _content = new() { Margin = new Thickness(40, 32, 40, 72), MaxWidth = 820 };
    readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, PanningMode = PanningMode.VerticalOnly };
    TextBox _search = null!;
    int _paint;
    double _h, _sat, _v = 1;

    Color AccentColor => Hex(_s.ColorAccent) ?? _s.Theme switch
    {
        "neon" => Color.FromRgb(0xFE, 0xE8, 0x01), "liquid" => Color.FromRgb(0x7F, 0xE7, 0xD9),
        "crystal" => Color.FromRgb(0xB9, 0x8C, 0xFF), "abyss" => Color.FromRgb(0x2E, 0xE6, 0xD6), "g6" => Color.FromRgb(0xFF, 0x4F, 0x9A), "coffee" => Color.FromRgb(0xDD, 0xA5, 0x6B),
        _ => Color.FromRgb(0x0A, 0x84, 0xFF),
    };
    Brush Accent => Frozen(AccentColor);

    PreferencesWindow(NotchWindow notch)
    {
        _notch = notch;
        _s = notch.PrefSettings;
        Title = "Pixo · " + Loc.T("Preferences");
        Width = 940;
        Height = 660;
        MinWidth = 760;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = WindowBrush;
        Foreground = Text;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 13;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        SourceInitialized += (_, _) => { Chrome(); Build(); }; // (built once, on its material)
        _scroll.PreviewMouseWheel += SmoothWheel;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                // a menu open over the window closes itself; typing in a box: Esc leaves the box (what you typed is saved)
                if (Keyboard.FocusedElement is DependencyObject f && PresentationSource.FromDependencyObject(f) is { } src && src != PresentationSource.FromVisual(this)) return;
                e.Handled = true;
                if (ReferenceEquals(Keyboard.FocusedElement, _search) && _query.Length == 0) { Keyboard.ClearFocus(); return; }
                if (Keyboard.FocusedElement is TextBox or PasswordBox && !ReferenceEquals(Keyboard.FocusedElement, _search)) { Keyboard.ClearFocus(); return; }
                if (_query.Length > 0) _search.Text = ""; else Close();
            }
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { _search.Focus(); _search.SelectAll(); e.Handled = true; }
        };
        // a box still being typed in saves as it loses the keyboard: closing takes the keyboard first
        Closing += (_, _) => Keyboard.ClearFocus();
    }

    // ───────────── window chrome: dark title bar, rounded corners, Mica on Windows 11 ─────────────

    void Chrome()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int on = 1, round = 2;
        DwmSetWindowAttribute(hwnd, 20, ref on, 4);      // dark title bar
        DwmSetWindowAttribute(hwnd, 33, ref round, 4);   // rounded corners
        if (Environment.OSVersion.Version.Build < 22621) return;
        int mica = 2;
        if (DwmSetWindowAttribute(hwnd, 38, ref mica, 4) != 0) return;
        var m = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref m);
        if (HwndSource.FromHwnd(hwnd) is { } src) src.CompositionTarget.BackgroundColor = Colors.Transparent;
        Background = Brushes.Transparent;
        _mica = true;
    }

    [StructLayout(LayoutKind.Sequential)] struct MARGINS { public int Left, Right, Top, Bottom; }
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS m);

    // ───────────── layout ─────────────

    void Build()
    {
        // rebuilt on a language / theme change: the long-lived parts leave their old places first
        if (_scroll.Parent is Panel oldRight) oldRight.Children.Remove(_scroll);
        if (_nav.Parent is ScrollViewer oldNav) oldNav.Content = null;
        double keep = _scroll.VerticalOffset;
        Kit();
        FlowDirection = Loc.Rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        Title = "Pixo · " + Loc.T("Preferences");
        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(252) });
        root.ColumnDefinitions.Add(new ColumnDefinition());

        // sidebar: the name, the search, the sections, and the way to the notch itself
        var side = new Grid { Background = SideBrush };
        side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        side.RowDefinitions.Add(new RowDefinition());
        side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        side.Children.Add(Brand());
        _search = new TextBox
        {
            Margin = new Thickness(12, 2, 12, 14), Height = 34, Padding = new Thickness(0), Background = Frozen(Color.FromArgb(0x59, 0x00, 0x00, 0x00)),
            BorderBrush = Edge, BorderThickness = new Thickness(1), FontSize = 13, Text = _query, VerticalContentAlignment = VerticalAlignment.Center,
        };
        _search.Template = SearchTemplate();
        AutomationName(_search, Loc.T("Search settings"));
        _search.TextChanged += (_, _) => { _query = _search.Text.Trim(); RenderContent(); RenderNav(); };
        Grid.SetRow(_search, 1);
        side.Children.Add(_search);
        var navScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _nav, Focusable = false };
        Grid.SetRow(navScroll, 2);
        side.Children.Add(navScroll);
        var foot = new StackPanel { Margin = new Thickness(12, 10, 12, 16) };
        var open = PillButton("\uE8A7", Loc.T("Open the control center"), () => { if (_notch.IsLocked) _notch.SetLocked(false); _notch.Expand(byHotkey: true); }, primary: false);
        open.HorizontalAlignment = HorizontalAlignment.Stretch;
        foot.Children.Add(open);
        foot.Children.Add(new TextBlock { Text = Loc.T("Version") + " " + Version, FontSize = 11.5, Foreground = Muted, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) });
        Grid.SetRow(foot, 3);
        side.Children.Add(foot);
        var seam = new Border { Width = 1, Background = Edge, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetRowSpan(seam, 4);
        side.Children.Add(seam);
        root.Children.Add(side);

        // content, and "Saved" rising at its foot
        _scroll.Content = _content;
        var right = new Grid();
        right.Children.Add(_scroll);
        right.Children.Add(Toast());
        Grid.SetColumn(right, 1);
        root.Children.Add(right);

        Content = root;
        RenderNav();
        RenderContent(keepAt: keep);
    }

    /// <summary>Pixo's logo, at a size.</summary>
    static FrameworkElement PixoLogo(double size)
    {
        var img = new System.Windows.Controls.Image { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        try { img.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/logo.png")); } catch { }
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        return img;
    }

    FrameworkElement Brand()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 22, 16, 18) };
        panel.Children.Add(PixoLogo(30));
        var names = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(new TextBlock { Text = "Pixo", FontSize = 15, FontWeight = FontWeights.SemiBold, FontFamily = DisplayFont });
        names.Children.Add(new TextBlock { Text = Loc.T("Preferences"), FontSize = 12, Foreground = Muted });
        panel.Children.Add(names);
        return panel;
    }

    static string Version => typeof(App).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "";

    /// <summary>The notch in miniature: its shape in the current theme, a live bar and its camera dot, the rim breathing in the accent colour.</summary>
    FrameworkElement MiniNotch(double w, double h, bool cyber)
    {
        double r = h / 2, pad = 10;
        var host = new Canvas { Width = w + pad * 2, Height = h + pad * 2, VerticalAlignment = VerticalAlignment.Center, FlowDirection = FlowDirection.LeftToRight };
        var shape = new Path
        {
            Data = Geometry.Parse(cyber
                ? FormattableString.Invariant($"M0,0 L{w},0 L{w},{h - r * 0.9} L{w - r * 0.9},{h} L{w * 0.62 + 2},{h} L{w * 0.62},{h + 2} L{r * 0.5},{h + 2} L0,{h - r * 0.5 + 2} Z")
                : FormattableString.Invariant($"M0,0 L{w},0 L{w},{h - r} A{r},{r} 0 0 1 {w - r},{h} L{r},{h} A{r},{r} 0 0 1 0,{h - r} Z")), // (always a dot for decimals: a comma-decimal PC broke it)
            Fill = Brushes.Black, Stroke = Accent, StrokeThickness = 1.4, StrokeLineJoin = PenLineJoin.Round,
            Effect = new DropShadowEffect { Color = AccentColor, BlurRadius = 14, ShadowDepth = 0, Opacity = 0.7 },
        };
        Canvas.SetLeft(shape, pad);
        Canvas.SetTop(shape, 2);
        host.Children.Add(shape);
        // a live activity's bar on the left, the camera dot on the right
        var bar = new Rectangle { Width = w * 0.3, Height = Math.Max(1.5, h * 0.12), RadiusX = cyber ? 0 : 1, RadiusY = cyber ? 0 : 1, Fill = Accent };
        Canvas.SetLeft(bar, pad + w * 0.16);
        Canvas.SetTop(bar, 2 + h * 0.46);
        host.Children.Add(bar);
        var dot = new Ellipse { Width = h * 0.34, Height = h * 0.34, Fill = Frozen(Color.FromRgb(0x22, 0x22, 0x2A)), Stroke = Frozen(Color.FromRgb(0x3A, 0x3A, 0x48)), StrokeThickness = 0.8 };
        Canvas.SetLeft(dot, pad + w * 0.74);
        Canvas.SetTop(dot, 2 + h * 0.3);
        host.Children.Add(dot);
        // a still glow: animating an effect would redraw the whole (translucent) window every frame
        return host;
    }

    void RenderNav()
    {
        _nav.Children.Clear();
        foreach (var (sec, glyph, name, _) in Sections)
        {
            // three groups: the notch itself, what it does for you, and the reference
            if (sec is Sec.Search or Sec.Shortcuts) _nav.Children.Add(new Border { Height = 1, Background = Edge, Margin = new Thickness(20, 8, 20, 8) });
            bool on = _query.Length == 0 && sec == _sec;
            var inner = new Grid { Height = 36 };
            inner.Children.Add(new Border { CornerRadius = new CornerRadius(8), Background = on ? Frozen(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF)) : Brushes.Transparent });
            inner.Children.Add(new Border { Width = 3, Height = 16, CornerRadius = new CornerRadius(1.5), Background = Accent, HorizontalAlignment = HorizontalAlignment.Left, Opacity = on ? 1 : 0 });
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 10, 0) };
            var icon = Glyph(glyph, 15, on ? Text : Dim);
            icon.Width = 20;
            row.Children.Add(icon);
            row.Children.Add(new TextBlock { Text = Loc.T(name), FontSize = 13.5, Margin = new Thickness(12, 0, 0, 0), Foreground = on ? Text : Frozen(Color.FromRgb(0xD4, 0xD4, 0xD9)), VerticalAlignment = VerticalAlignment.Center });
            inner.Children.Add(row);
            var b = new Button { Content = inner, Style = _ghost, Height = 36, Margin = new Thickness(8, 1, 8, 1), HorizontalContentAlignment = HorizontalAlignment.Stretch, FocusVisualStyle = Ring(8) };
            AutomationName(b, Loc.T(name));
            var s = sec;
            b.Click += (_, _) =>
            {
                _sec = s;
                if (_query.Length > 0) _search.Text = ""; else { RenderNav(); RenderContent(); }
            };
            _nav.Children.Add(b);
        }
    }

    void RenderContent(double keepAt = -1)
    {
        _content.Children.Clear();
        BeginAnimation(SmoothOffsetProperty, null);
        _scrollAnimating = false;
        if (_query.Length > 0) { RenderSearch(); _scroll.ScrollToTop(); Enter(); return; }
        var (_, _, name, sub) = Sections.First(x => x.Sec == _sec);
        Header(Loc.T(name), Loc.T(sub));
        switch (_sec)
        {
            case Sec.General: General(); break;
            case Sec.Appearance: Appearance(); break;
            case Sec.Behaviour: Behaviour(); break;
            case Sec.Alerts: Alerts(); break;
            case Sec.Pages: Pages(); break;
            case Sec.Shortcuts: Shortcuts(); break;
            case Sec.Search: SearchSection(); break;
            case Sec.Ai: AiSectionView(); break;
            case Sec.Calls: CallsSection(); break;
            case Sec.About: About(); break;
        }
        if (keepAt >= 0)
        {
            // the same section drawn again (a theme, a size, an order changed): it stays where you were, no entrance
            _content.UpdateLayout();
            _scroll.ScrollToVerticalOffset(keepAt);
            return;
        }
        _scroll.ScrollToTop();
        Enter();
        if (_jump is { } to)
        {
            _jump = null;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => JumpTo(to));
        }
    }

    /// <summary>Draws this section again where it is (after a change that alters what it shows).</summary>
    void Redraw() => RenderContent(keepAt: _scroll.VerticalOffset);

    string? _jump;

    /// <summary>A group found by the search: the page glides to it and its title glows once in the accent.</summary>
    void JumpTo(string title)
    {
        var t = _content.Children.OfType<FrameworkElement>().FirstOrDefault(e => e.Tag as string == title);
        if (t == null) return;
        double y = t.TranslatePoint(new Point(0, 0), _content).Y + _content.Margin.Top - 16;
        _scrollTarget = Math.Clamp(y, 0, _scroll.ScrollableHeight);
        var glide = new DoubleAnimation(_scroll.VerticalOffset, _scrollTarget, TimeSpan.FromMilliseconds(420)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };
        glide.Completed += (_, _) => _scrollAnimating = false;
        _scrollAnimating = true;
        BeginAnimation(SmoothOffsetProperty, glide);
        var label = t as TextBlock ?? (t as Panel)?.Children.OfType<TextBlock>().FirstOrDefault();
        if (label == null) return;
        var ink = new SolidColorBrush(AccentColor);
        label.Foreground = ink;
        ink.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(((SolidColorBrush)Text).Color, TimeSpan.FromMilliseconds(1400)) { BeginTime = TimeSpan.FromMilliseconds(500) });
    }

    /// <summary>A section slides in.</summary>
    void Enter()
    {
        var shift = new TranslateTransform();
        _content.RenderTransform = shift;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        _content.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
    }


    // ───────────── the switches ─────────────

    List<Row> Rows() => new()
    {
        new(Sec.General, "", "Start with Windows", "Opens by itself when you sign in", () => _s.StartWithWindows, v => { _s.StartWithWindows = v; KeepAlive.Sync(_s); }),
        new(Sec.General, "", "Always keep running", "Comes back after a crash, a sleep or a lock", () => _s.KeepRunning, v => { _s.KeepRunning = v; KeepAlive.Sync(_s); }),
        new(Sec.General, "\uE7B3", "Hide from screen sharing", "Pixo stays on your screen but out of calls, recordings and screenshots (Teams, Zoom, Discord, OBS)", () => _s.HideFromCapture, v => { _s.HideFromCapture = v; CaptureGuard.Set(v); }),
        new(Sec.General, "\uE756", "Notices from scripts", "Your scripts and builds can show a notice: Pixo.exe --notify \"Build done\" --icon ok (or fail, warn, info)", () => _s.ScriptNotices, v => _notch.SetScriptNotices(v)),
        new(Sec.General, "", "Battery mode", "Lighter animations while Windows battery saver is on, fewer checks when unplugged", () => _s.EcoOnBattery, v => _s.EcoOnBattery = v),

        new(Sec.Appearance, "", "Companion", "Now and then, Pixo's face comes down from the notch for a little moment. It sleeps there when you're away, and you can poke it", () => _s.FaceCompanion, v => _s.FaceCompanion = v),
        new(Sec.Appearance, "\uE76E", "Face in the notch", "A little face lives in the idle notch: it blinks, looks at the pointer and reacts to what happens. Click it to poke it, right-click for another hat", () => _s.NotchFace, v => _s.NotchFace = v),
        new(Sec.Appearance, "", "Typing sounds", "A soft click for each key you type in Pixo, like a phone's keyboard, and a tiny tick for Caps Lock", () => _s.TypingSounds, v => _s.TypingSounds = v),
        new(Sec.Appearance, "\uE8AB", "Slide between pages", "A click on another page slides the strip to it. Off, the page fades in where you look: quicker and lighter", () => _s.PageSlide, v => _s.PageSlide = v),
        new(Sec.Appearance, "\uE73F", "Compact when resting", "Unused for 5 seconds, the notch tucks in small; it grows back when the pointer comes near", () => _s.CompactWhenResting, v => _s.CompactWhenResting = v),
        new(Sec.Appearance, "\uE767", "Face sounds", "Tiny sounds of its own: a hello, a boing when you poke it, a chime when an answer is done", () => _s.FaceSounds, v => _s.FaceSounds = v),
        new(Sec.Appearance, "\uE8BD", "Face hints", "Back on Home after a while, one short line beside the face: new messages, someone waiting, a reminder soon", () => _s.FaceHints, v => _s.FaceHints = v),
        new(Sec.Appearance, "", "Interface sounds", "Soft little sounds for what you do: a pluck when you pick, a tick when you copy, a sparkle when you pin, a nom when you delete", () => _s.UiSounds, v => _s.UiSounds = v),
        new(Sec.Appearance, "", "Tips from Pixo", "Now and then a one-line tip in a bubble under the face, each said only once", () => _s.FaceTips, v => _s.FaceTips = v),
        new(Sec.Appearance, "\uE8A0", "Tuck pills into the notch", "The side pills show as a small light in the notch, and slide out when they start or when the pointer comes near", () => _s.TuckPills, v => _s.TuckPills = v),
        new(Sec.Appearance, "", "Minimal when idle", "A thin bar when nothing is happening", () => _s.MinimalWhenIdle, v => _s.MinimalWhenIdle = v),
        new(Sec.Appearance, "", "Slim over windows", "Stays slim over browser tabs and windows", () => _s.SlimOverWindows, v => _s.SlimOverWindows = v),
        new(Sec.Appearance, "", "Music activity", "Artwork and a visualizer while music plays", () => _s.ShowMediaActivity, v => _s.ShowMediaActivity = v),
        new(Sec.Appearance, "", "Music visualizer", "Bars that move with the music", () => _s.AnimatedVisualizer, v => _s.AnimatedVisualizer = v),

        new(Sec.Behaviour, "", "Open at top edge", "Push the pointer to the top of the screen to open it", () => _s.HoverToOpen, v => _s.HoverToOpen = v),
        new(Sec.Behaviour, "", "Drop files on the notch", "Keep files on the shelf, drag them out anywhere", () => _s.ShelfDrop, v => _s.ShelfDrop = v),
        new(Sec.Behaviour, "", "Hide in full screen", "Out of the way in videos and full-screen apps", () => _s.HideInFullscreen, v => _s.HideInFullscreen = v),
        new(Sec.Behaviour, "", "FPS in games", "A tiny frame-rate pill while you play", () => _s.GameStats, v => _notch.PrefSetFps(v)),
        new(Sec.Behaviour, "", "Lock the notch", "It shows things but reacts to nothing", () => _s.Locked, v => _notch.SetLocked(v)),

        new(Sec.Alerts, "\uE772", "Pinitor", "The battery of your wireless mice, keyboards, headsets and controllers, found on its own (checked every 2 minutes)", () => _s.DeviceHub, v => _s.DeviceHub = v),
        new(Sec.Alerts, "", "Notification alerts", "Messages and app notifications", () => _s.NotificationAlerts, v => _s.NotificationAlerts = v),
        new(Sec.Alerts, "\uE708", "Calm alerts", "Shorter pop-ups; offers and floods only make the face glance (all kept in Notifs & alerts)", () => _s.CalmAlerts, v => _s.CalmAlerts = v),
        new(Sec.Alerts, "", "System alerts", "Volume, brightness, battery, devices", () => _s.SystemAlerts, v => _s.SystemAlerts = v),
        new(Sec.Alerts, "", "App launch alerts", "\"Opening Spotify\" when an app starts", () => _s.AppLaunchAlerts, v => _s.AppLaunchAlerts = v),
        new(Sec.Alerts, "", "“Copied” alerts", "A nod when you copy something", () => _s.ClipboardAlerts, v => _s.ClipboardAlerts = v),
        new(Sec.Alerts, "", "Battery drain", "Spots apps eating power", () => _s.SmartBatteryDrain, v => _s.SmartBatteryDrain = v, "Smart"),
        new(Sec.Alerts, "", "Bad connection", "Shaky Wi-Fi before your calls", () => _s.SmartConnection, v => _s.SmartConnection = v, "Smart"),
        new(Sec.Alerts, "", "Charge reminder", "When ~45 min are left", () => _s.SmartChargeReminder, v => _s.SmartChargeReminder = v, "Smart"),
        new(Sec.Alerts, "", "Downloads tidy-up", "Weekly, if it's over 5 GB", () => _s.SmartDownloads, v => _s.SmartDownloads = v, "Smart"),
        new(Sec.Alerts, "", "Break reminder", "After long stretches of work", () => _s.SmartBreaks, v => _notch.PrefSetBreaks(v), "Smart"),
        new(Sec.Alerts, "", "Meeting mode", "Camera on: pause music, quiet", () => _s.MeetingMode, v => _notch.PrefSetMeeting(v), "Smart"),
        new(Sec.Alerts, "\uE7BA", "Overload warnings", "CPU, GPU, memory and heat watched; warns when one stays high", () => _notch.PrefStrainOn("all"), v => _notch.PrefSetStrain("all", v), "Strain"),

        new(Sec.Ai, "\uE8BD", "AI chat in the search", "“Ask AI” in the results, Shift+Enter on what you typed, Tab to go back to a chat", () => _s.AiChat, v => _s.AiChat = v, "ai"),
        new(Sec.Ai, "\uE945", "Let it act on your PC", "The notch's commands, files, the clipboard, opening apps and pages", () => _s.AiTools, v => _s.AiTools = v, "ai"),
        new(Sec.Ai, "\uE774", "Let it use the web", "Searches and reads pages for current answers", () => _s.AiWeb, v => _s.AiWeb = v, "ai"),
        new(Sec.Ai, "\uE81C", "Keep your chats", "Saved on this PC so you can go back to them (Ctrl+H in the chat)", () => _s.AiHistory, v => _s.AiHistory = v, "ai"),
        new(Sec.Ai, "\uE8EE", "Go on with the last chat", "Asking from the search continues a chat used in the last 30 minutes", () => _s.AiContinue, v => _s.AiContinue = v, "ai"),
        new(Sec.Ai, "\uE8F1", "Let it remember things about you", "Short facts you tell it (your work, your preferences), kept on this PC and used in every chat", () => _s.AiMemory, v => _s.AiMemory = v, "ai"),
        new(Sec.Ai, "\uE7E7", "Shorten long alerts", "Wordy alerts from apps are put in a few words (messages from people are never sent)", () => _s.AiAlerts, v => _s.AiAlerts = v, "ai"),
        new(Sec.Search, "\uE765", "Type to search", "With the notch open and the pointer on it, just start typing", () => _s.SearchTypeToOpen, v => { _s.SearchTypeToOpen = v; _notch.PrefSearchChanged(); }, "open"),
        new(Sec.Search, "\uE721", "Search button", "The magnifier in the control center's header", () => _s.SearchButton, v => { _s.SearchButton = v; _notch.PrefSearchChanged(); }, "open"),
        new(Sec.Search, "\uE8EF", "Commands and answers", "“volume 30”, “remind me…”, sums, time zones and dates", () => _s.SearchCommands, v => _s.SearchCommands = v, "find"),
        new(Sec.Search, "\uE8B7", "Files across the PC", "From the index Windows keeps for File Explorer's search", () => _s.SearchWholePc, v => _s.SearchWholePc = v, "find"),
        new(Sec.Search, "\uE8A5", "Words inside documents", "Finds a PDF or a Word file by what's written in it", () => _s.SearchInsideFiles, v => _s.SearchInsideFiles = v, "find"),
        new(Sec.Search, "\uE7C4", "Open windows", "Goes to an app or a window that's already open", () => _s.SearchWindows, v => _s.SearchWindows = v, "find"),
        new(Sec.Search, "\uE77F", "Clipboard and shelf", "Finds what you copied or kept on the shelf", () => _s.SearchClipboard, v => _s.SearchClipboard = v, "find"),
        new(Sec.Search, "\uE945", "Learn from my picks", "What you choose for what you type moves to the top", () => _s.SearchLearn, v => _s.SearchLearn = v, "find"),
        new(Sec.Search, "\uE734", "Frequent apps", "Your most-used apps when the search is empty", () => _s.SearchFrequent, v => _s.SearchFrequent = v, "empty"),
        new(Sec.Search, "\uE81C", "Recent searches", "Shown when the search is empty; ↑ brings them back", () => _s.SearchRecent, v => _s.SearchRecent = v, "empty"),
        new(Sec.Search, "\uE82F", "Suggestions", "A few things to try", () => _s.SearchTry, v => _s.SearchTry = v, "empty"),

        new(Sec.Calls, "\uE717", "Calls page", "Second page of the control center: your call, from the notch", () => !_s.HiddenPages.Contains(6), v => _notch.PrefSetPage(6, v), "page"),
        new(Sec.Calls, "\uE8A7", "Open on the Calls page", "During a call, the notch opens straight on it", () => _s.CallsOpenOnCall, v => _s.CallsOpenOnCall = v, "page"),
        new(Sec.Calls, "\uE717", "Answer and end calls from the notch", "Answer and Decline when a call rings, End call when it starts, pressing the app's own buttons", () => _s.CallButtons, v => _s.CallButtons = v, "page"),
        new(Sec.Calls, "\uE765", "Call app shortcuts", "Camera, share, raise hand and end, through Teams, Zoom and Meet's own shortcuts", () => _s.CallShortcuts, v => { _s.CallShortcuts = v; _notch.PrefCallsChanged(); }, "page"),
        new(Sec.Calls, "\uE768", "Pause music when a call starts", "And play it again when the call ends", () => _s.CallPauseMusic, v => _s.CallPauseMusic = v, "start"),
        new(Sec.Calls, "\uE74F", "Quiet other apps during calls", "Everything else down to a whisper, then back", () => _s.CallQuietOthers, v => _s.CallQuietOthers = v, "start"),
        new(Sec.Calls, "\uE708", "Focus during calls", "Notifications wait until the call ends", () => _s.CallFocus, v => _s.CallFocus = v, "start"),
        new(Sec.Calls, "\uE7F4", "Presenter mode hides desktop icons", "A clean desktop while you share your screen", () => _s.PresenterHidesIcons, v => _s.PresenterHidesIcons = v, "start"),
        new(Sec.Calls, "\uE81C", "Call history", "Your last calls on the Calls page (only on this PC)", () => _s.CallHistory, v => _s.CallHistory = v, "start"),
        new(Sec.Pages, "\uE821", "Flow", "What you\'re working on, your chats and projects, and two weeks of notifications", () => !_s.HiddenPages.Contains(7), v => _notch.PrefSetPage(7, v), Page: 7),
        new(Sec.Pages, "\uE721", "Search", "The search and the AI chat, side by side (the same as Alt+Space)", () => !_s.HiddenPages.Contains(8), v => _notch.PrefSetPage(8, v), Page: 8),
        new(Sec.Pages, "\uE717", "Calls", "Your call, from the notch", () => !_s.HiddenPages.Contains(6), v => _notch.PrefSetPage(6, v), Page: 6),
        new(Sec.Pages, "", "Hardware", "CPU and every core, memory, graphics, network, battery, ping, the busiest apps and the drives", () => !_s.HiddenPages.Contains(1), v => _notch.PrefSetPage(1, v), Page: 1),
        new(Sec.Pages, "", "Tools", "Timer, snip, colour picker, calculator and more", () => !_s.HiddenPages.Contains(2), v => _notch.PrefSetPage(2, v), Page: 2),
        new(Sec.Pages, "\uE756", "Terminal", "A PowerShell in the notch: your folder and last commands are kept, each run shows its time and whether it worked", () => !_s.HiddenPages.Contains(9), v => _notch.PrefSetPage(9, v), Page: 9),
        new(Sec.Pages, "\uE8AB", "Converter", "Units and currencies, both ways at once, the rates kept for offline", () => !_s.HiddenPages.Contains(10), v => _notch.PrefSetPage(10, v), Page: 10),
        new(Sec.Pages, "\uE706", "Weather", "Now, the next hours and the week for a place you choose, asked only while the page is open", () => !_s.HiddenPages.Contains(11), v => _notch.PrefSetPage(11, v), Page: 11),
        new(Sec.Pages, "\uE9D9", "AI usage", "Your Claude Code and ChatGPT limits, how fast you use them, and what Claude Code cost today and this week, read on this PC", () => !_s.HiddenPages.Contains(12), v => _notch.PrefSetPage(12, v), Page: 12),
        new(Sec.Pages, "", "Clipboard & Files", "Copy history, the shelf and recent files", () => !_s.HiddenPages.Contains(3), v => _notch.PrefSetPage(3, v), Page: 3),
        new(Sec.Pages, "", "Colors", "The colour studio of the notch", () => !_s.HiddenPages.Contains(4), v => _notch.PrefSetPage(4, v), Page: 4),
    };

    void RenderSearch()
    {
        Header(Loc.T("Search"), string.Format(Loc.T("Settings matching “{0}”"), _query));
        var hits = Rows().Where(r => Matches(r.Title) || Matches(r.Desc) || Matches(Sections.First(s => s.Sec == r.Sec).Name)).ToList();
        var places = Places.Where(pl => Matches(pl.Title) || pl.Words.Contains(_query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hits.Count == 0 && places.Count == 0)
        {
            var none = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 48, 0, 0) };
            var g = Glyph("\uE721", 28, Muted);
            none.Children.Add(g);
            none.Children.Add(new TextBlock { Text = Loc.T("Nothing found. Try another word."), Foreground = Dim, Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Center });
            _content.Children.Add(none);
            return;
        }
        if (places.Count > 0)
        {
            Label(Loc.T("Go to"));
            var list = new StackPanel();
            for (int i = 0; i < places.Count; i++)
            {
                var (sec, title, _) = places[i];
                if (i > 0) list.Children.Add(Divider(56));
                var (_, glyph, secName, _) = Sections.First(x => x.Sec == sec);
                var row = new Grid { MinHeight = 52 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(8), Background = Faint, Child = Glyph(glyph, 14, Dim), HorizontalAlignment = HorizontalAlignment.Center });
                var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 9, 12, 9) };
                words.Children.Add(new TextBlock { Text = Loc.T(title), FontSize = 13.5 });
                words.Children.Add(new TextBlock { Text = Loc.T(secName), FontSize = 12, Foreground = Dim, Margin = new Thickness(0, 1, 0, 0) });
                Grid.SetColumn(words, 1);
                row.Children.Add(words);
                var go = Glyph(Loc.Rtl ? "\uE76B" : "\uE76C", 11, Muted);
                go.Margin = new Thickness(0, 0, 18, 0);
                Grid.SetColumn(go, 2);
                row.Children.Add(go);
                var b = new Button { Content = row, Style = _ghost, HorizontalContentAlignment = HorizontalAlignment.Stretch, FocusVisualStyle = Ring(8) };
                AutomationName(b, Loc.T(title));
                string to = Loc.T(title);
                var target = sec;
                b.Click += (_, _) => { _sec = target; _jump = to; _search.Text = ""; };
                list.Children.Add(b);
            }
            _content.Children.Add(CardOf(list, new Thickness(0, 2, 0, 2)));
        }
        foreach (var group in hits.GroupBy(r => r.Sec))
        {
            Label(Loc.T(Sections.First(s => s.Sec == group.Key).Name));
            Card(group);
        }
    }

    /// <summary>The groups that aren't one switch (what the search can take you to), with the words people may look for them by.</summary>
    static readonly (Sec Sec, string Title, string Words)[] Places =
    {
        (Sec.General, "Language", "english french arabic français العربية langue"),
        (Sec.Appearance, "Theme", "themes look mono minimal original neon liquid glass crystal abyss dusk coffee"),
        (Sec.Appearance, "Colours", "color colour accent wheel paint palette preset shuffle"),
        (Sec.Appearance, "Closed", "closed notch capsule line size width height position distance edge resize"),
        (Sec.Appearance, "Beside the face", "clock date battery weather cpu claude parts"),
        (Sec.Appearance, "Open panel", "panel size scale compact large bigger smaller zoom"),
        (Sec.Appearance, "Background", "opacity transparent solid"),
        (Sec.Appearance, "Island style", "island bubbles"),
        (Sec.Appearance, "Music style", "music wave visualizer bars"),
        (Sec.Appearance, "Face's hat", "hat outfit beanie cap crown santa witch party"),
        (Sec.Behaviour, "Beside the FPS in games", "fps frame rate games cpu gpu temperature"),
        (Sec.Behaviour, "Size in games", "fps pill size games"),
        (Sec.Behaviour, "Every browser tab that plays", "browser extension tabs chrome edge brave opera firefox video"),
        (Sec.Alerts, "Alert sound", "sound beep chime ding"),
        (Sec.Alerts, "Warning lines", "overload cpu gpu memory heat temperature threshold"),
        (Sec.Search, "Shortcut", "hotkey key alt space ctrl"),
        (Sec.Search, "Web search", "google bing duckduckgo brave engine"),
        (Sec.Search, "Memory", "forget recent learned history"),
        (Sec.Ai, "Connection", "key api gemini openrouter groq connect"),
        (Sec.Ai, "Model", "model gemma gemini claude deepseek"),
        (Sec.Ai, "Voice", "voice mic microphone speech talk dictation read aloud"),
        (Sec.Ai, "Your instructions", "instructions prompt system always"),
        (Sec.Ai, "What it remembers about you", "memory facts remember"),
        (Sec.Ai, "Your chats", "chats history delete folder"),
        (Sec.Calls, "Quick replies", "replies messages call chat"),
        (Sec.Pages, "Shown in the control center, in this order", "pages order hide show"),
    };

    bool Matches(string en) =>
        en.Contains(_query, StringComparison.OrdinalIgnoreCase) || Loc.T(en).Contains(_query, StringComparison.OrdinalIgnoreCase);

    // ───────────── sections ─────────────

    void General()
    {
        Label(Loc.T("Language"));
        var langs = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, -8, 0) };
        foreach (var (code, name, sample) in new[] { ("en", "English", "Hello"), ("fr", "Français", "Bonjour"), ("ar", "العربية", "مرحبا") })
        {
            bool on = Loc.Lang == code;
            var stage = new Grid { Height = 64, Background = Frozen(Color.FromRgb(0x1F, 0x1F, 0x23)) };
            stage.Children.Add(new TextBlock { Text = sample, FontSize = 20, FontFamily = DisplayFont, Foreground = on ? Text : Dim, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            string c = code;
            langs.Children.Add(PickCard(Stage(stage), name, on, () =>
            {
                _s.Language = c;
                _s.Save();
                Loc.Set(c);
                _notch.PrefRelabel();
                Build();
                Saved();
            }));
        }
        _content.Children.Add(langs);
        Note(Loc.T("The notch's pages and this window follow the language; a few alerts stay in English."), top: 2);

        Label(Loc.T("Start-up and power"));
        Card(Rows().Where(r => r.Sec == Sec.General));
    }

    void Appearance()
    {
        Label(Loc.T("Theme"));
        var themes = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, -8, 0) };
        foreach (var key in ThemeKeys) themes.Children.Add(ThemeCard(key));
        _content.Children.Add(themes);

        Label(Loc.T("Colours"));
        _content.Children.Add(Mixer());

        ClosedSection();
        PanelSizeSection();
        Choice(Loc.T("Background"), NotchWindow.BodyOpacities.Select(n => (n.Key, Loc.T(n.Name))).ToArray(), () => _s.BodyOpacity.ToString(), v => _notch.SetBodyOpacity(v));
        Choice(Loc.T("Island style"), NotchWindow.IslandStyles.Select(w => (w.Key, Loc.T(w.Name))).ToArray(), () => _s.IslandStyle, v => _notch.SetIslandStyle(v));
        Choice(Loc.T("Music style"), NotchWindow.WaveStyles.Select(w => (w.Key, Loc.T(w.Name))).ToArray(), () => _s.MusicWave, v => _notch.SetWaveStyle(v));

        Label(Loc.T("Look"));
        Card(Rows().Where(r => r.Sec == Sec.Appearance));

        Label(Loc.T("Face's hat"));
        var hats = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        foreach (var (name, key) in new[] { ("Auto (season)", "auto"), ("None", "none"), ("Beanie", "beanie"), ("Cap", "cap"), ("Party", "party"), ("Crown", "crown"), ("Santa", "santa"), ("Witch", "witch") })
        {
            var b = PillButton(null, Loc.T(name), () => { }, primary: false);
            b.Margin = new Thickness(0, 0, 8, 8);
            SetPill(b, (_s.FaceOutfit ?? "auto") == key);
            b.Click += (_, _) =>
            {
                _s.FaceOutfit = key; // it puts it on at its next look (a tenth of a second)
                _s.Save();
                foreach (var other in hats.Children.OfType<Button>()) SetPill(other, ReferenceEquals(other, b));
                Saved();
            };
            hats.Children.Add(b);
        }
        _content.Children.Add(hats);
    }

    static readonly string[] ThemeKeys = { "mono", "original", "minimal", "neon", "liquid", "crystal", "abyss", "g6", "coffee" };
    static readonly Dictionary<string, string> ThemeTitles = new()
    {
        ["mono"] = "Mono", ["original"] = "Original", ["minimal"] = "Minimal", ["neon"] = "Neon", ["liquid"] = "Prism", ["crystal"] = "Crystal", ["abyss"] = "Abyss", ["g6"] = "Dusk", ["coffee"] = "Coffee",
    };

    /// <summary>A theme, as a little picture of the notch in it.</summary>
    FrameworkElement ThemeCard(string key)
    {
        bool on = _s.Theme == key;
        var stage = new Grid { Height = 92, ClipToBounds = true };
        const double w = 124, h = 32;
        TextBlock Clock() => new() { Text = "12:07", FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Border Glass(Brush body, Brush rim, params Brush[] lights)
        {
            var g = new Grid();
            var corner = new CornerRadius(0, 0, h / 2, h / 2);
            g.Children.Add(new Border { CornerRadius = corner, Background = body });
            foreach (var l in lights) g.Children.Add(new Border { CornerRadius = corner, Background = l });
            g.Children.Add(new Border { CornerRadius = corner, BorderBrush = rim, BorderThickness = new Thickness(1.2, 0, 1.2, 1.2) });
            g.Children.Add(Clock());
            return new Border { Width = w, Height = h, VerticalAlignment = VerticalAlignment.Top, Child = g };
        }
        RadialGradientBrush Light(Color c, double x, double y) => new()
        {
            Center = new Point(x, y), GradientOrigin = new Point(x, y), RadiusX = 0.6, RadiusY = 1.1,
            GradientStops = { new GradientStop(c, 0), new GradientStop(Colors.Transparent, 1) },
        };
        LinearGradientBrush V(params (string C, double At)[] stops)
        {
            var g = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) };
            foreach (var (c, at) in stops) g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(c), at));
            return g;
        }
        switch (key)
        {
            case "neon":
                stage.Background = new LinearGradientBrush(Color.FromRgb(0x00, 0x06, 0x0E), Color.FromRgb(0x06, 0x1E, 0x2A), 90);
                stage.Children.Add(new Path
                {
                    Data = Geometry.Parse(FormattableString.Invariant($"M0,0 L{w},0 L{w},{h - 10} L{w - 10},{h} L{w * 0.62 + 4},{h} L{w * 0.62},{h + 4} L6,{h + 4} L0,{h - 2} Z")),
                    Fill = Frozen(Color.FromRgb(0x00, 0x06, 0x0E)), Stroke = Frozen(Color.FromRgb(0xFE, 0xE8, 0x01)), StrokeThickness = 1.5,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
                });
                stage.Children.Add(new TextBlock { Text = "12:07", FontFamily = new FontFamily("Bahnschrift"), FontStretch = FontStretches.SemiCondensed, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Frozen(Color.FromRgb(0xFE, 0xE8, 0x01)), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 9, 0, 0) });
                break;
            case "liquid":
                stage.Background = new LinearGradientBrush(Color.FromRgb(0x1C, 0x4A, 0x6E), Color.FromRgb(0x4A, 0x2A, 0x6A), 35);
                stage.Children.Add(Glass(V(("#F2080B13", 0), ("#DC101725", 0.5), ("#C41A253A", 1)), V(("#00FFFFFF", 0), ("#90FFFFFF", 1)),
                    Light(Color.FromArgb(0x60, 0x3F, 0xE0, 0xC5), 0.15, 1), Light(Color.FromArgb(0x55, 0x9B, 0x7B, 0xFF), 0.88, 0.95)));
                break;
            case "crystal":
            {
                // inside a geode: violet night, amethyst points growing from the corners, the notch cut like a gem
                stage.Background = V(("#0B0614", 0), ("#24123D", 0.6), ("#4A2A7A", 1));
                var gem = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
                foreach (var (c, at) in new[] { ("#E0FF9CD8", 0.0), ("#E0B98CFF", 0.3), ("#E08FA8FF", 0.62), ("#E0C8F0FF", 0.85), ("#E0F1EAFF", 1.0) })
                    gem.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(c), at));
                foreach (var (x, len, tilt) in new[] { (8.0, 40.0, 14.0), (18.0, 28.0, 30.0), (150.0, 44.0, -16.0), (138.0, 30.0, -32.0), (160.0, 26.0, -6.0) })
                {
                    var shard = new Path
                    {
                        Data = Geometry.Parse(FormattableString.Invariant($"M-5,0 L-5,-{len - 8} L0,-{len} L5,-{len - 8} L5,0 Z")),
                        Fill = new LinearGradientBrush(Color.FromArgb(0xC0, 0xD9, 0xC4, 0xFF), Color.FromArgb(0x70, 0x8A, 0x5C, 0xF0), 0),
                        Stroke = Frozen(Color.FromArgb(0x90, 0xF1, 0xEA, 0xFF)), StrokeThickness = 0.7,
                        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(x, 96, 0, 0),
                        RenderTransform = new RotateTransform(tilt),
                    };
                    stage.Children.Add(shard);
                }
                stage.Children.Add(Glass(V(("#FC0A0612", 0), ("#F8160C27", 0.5), ("#F4261342", 1)), gem,
                    Light(Color.FromArgb(0x60, 0xB9, 0x8C, 0xFF), 0.5, 1.15), V(("#30F1EAFF", 0), ("#00F1EAFF", 0.5))));
                break;
            }
            case "abyss":
                stage.Background = new LinearGradientBrush(Color.FromRgb(0x01, 0x06, 0x10), Color.FromRgb(0x03, 0x16, 0x2E), 90);
                stage.Children.Add(Glass(V(("#000000", 0), ("#020B1A", 0.5), ("#041E38", 1)), V(("#002EE6D6", 0), ("#702EE6D6", 1)),
                    Light(Color.FromArgb(0x60, 0x2E, 0xE6, 0xD6), 0.5, 1.1)));
                var rnd = new Random(3);
                for (int i = 0; i < 9; i++)
                    stage.Children.Add(new Ellipse
                    {
                        Width = 3, Height = 3, Fill = Frozen(i % 3 == 0 ? Color.FromRgb(0x7C, 0xF5, 0xFF) : Color.FromRgb(0x2E, 0xE6, 0xD6)), Opacity = 0.4 + rnd.NextDouble() * 0.5,
                        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(10 + rnd.NextDouble() * 150, 42 + rnd.NextDouble() * 44, 0, 0),
                    });
                break;
            case "g6":
            {
                // a coastal sunset behind it: violet night above, flamingo pink, gold low
                stage.Background = V(("#2A1240", 0), ("#7A1E5C", 0.5), ("#FF6B5A", 0.86), ("#FFC857", 1));
                var line = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
                foreach (var (c, at) in new[] { ("#00FFC857", 0.0), ("#D0FFC857", 0.22), ("#E0FF4F9A", 0.5), ("#C022E1D1", 0.8), ("#0022E1D1", 1.0) })
                    line.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(c), at));
                var duskCard = Glass(V(("#FC0C0613", 0), ("#F81C0A26", 0.5), ("#F4340C30", 1)), V(("#00FF4F9A", 0), ("#80FF9A3C", 1)),
                    Light(Color.FromArgb(0x60, 0xFF, 0xC8, 0x57), 0.5, 1.15), Light(Color.FromArgb(0x40, 0xFF, 0x4F, 0x9A), 0.88, 0.1));
                if (duskCard.Child is Grid cg)
                {
                    cg.Children.Add(new Border { Height = 1.2, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(14, 0, 14, 1.5), Background = line });
                    if (cg.Children.OfType<TextBlock>().LastOrDefault() is TextBlock clock) { clock.FontFamily = new FontFamily("Bahnschrift"); clock.FontStretch = FontStretches.SemiCondensed; clock.Foreground = Frozen(Color.FromRgb(0xFF, 0xE3, 0xCF)); }
                }
                stage.Children.Add(duskCard);
                break;
            }
            case "coffee":
            {
                // a café counter: warm wood and lamplight, the notch a cup of dark roast with its crema, steam rising
                stage.Background = V(("#2A1A10", 0), ("#5A3620", 0.62), ("#A66A3C", 1));
                var crema = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
                foreach (var (c, at) in new[] { ("#00D89E62", 0.0), ("#C0D89E62", 0.2), ("#E0F3DFC3", 0.5), ("#C0D89E62", 0.8), ("#00D89E62", 1.0) })
                    crema.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(c), at));
                var cup = Glass(V(("#FC130B07", 0), ("#FA23150D", 0.5), ("#F83A2314", 1)), V(("#00D89E62", 0), ("#90D89E62", 1)),
                    Light(Color.FromArgb(0x58, 0xD8, 0x9E, 0x62), 0.5, 1.15));
                if (cup.Child is Grid cg)
                {
                    cg.Children.Add(new Border { Height = 1.4, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(12, 0, 12, 1.5), Background = crema });
                    if (cg.Children.OfType<TextBlock>().LastOrDefault() is TextBlock clock)
                    {
                        clock.FontFamily = NotchWindow.CoffeeDisplay; // (the same one: the theme's font swap knows to leave it)
                        clock.FontWeight = FontWeights.Normal;
                        clock.Foreground = Frozen(Color.FromRgb(0xF3, 0xDF, 0xC3));
                    }
                }
                stage.Children.Add(cup);
                // steam curling up from the counter, and a little rosetta of latte art in the foam of a second cup
                foreach (var (x, bend) in new[] { (70.0, 6.0), (84.0, -5.0) })
                    stage.Children.Add(new Path
                    {
                        Data = Geometry.Parse(FormattableString.Invariant($"M{x},90 C{x - bend},80 {x + bend * 1.6},70 {x},60 S{x - bend},48 {x + bend * 0.6},40")),
                        Stroke = new LinearGradientBrush(Color.FromArgb(0x00, 0xF3, 0xDF, 0xC3), Color.FromArgb(0x90, 0xF3, 0xDF, 0xC3), 90) { MappingMode = BrushMappingMode.RelativeToBoundingBox },
                        StrokeThickness = 2.4, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                    });
                break;
            }
            case "mono":
            {
                // white on black, nothing else
                stage.Background = Frozen(Color.FromRgb(0x1C, 0x1C, 0x1C));
                var mono = Glass(Brushes.Black, Frozen(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)));
                if (mono.Child is Grid mg && mg.Children.OfType<TextBlock>().LastOrDefault() is TextBlock mclock) mclock.FontWeight = FontWeights.Medium;
                stage.Children.Add(mono);
                break;
            }
            case "minimal":
            {
                // a plain desk: one graphite body, a hairline, the clock in off-white; nothing else
                stage.Background = Frozen(Color.FromRgb(0x1A, 0x1A, 0x1B));
                var flat = Glass(Frozen(Color.FromRgb(0x0E, 0x0E, 0x0F)), Frozen(Color.FromArgb(0x26, 0xEC, 0xEB, 0xE8)));
                if (flat.Child is Grid fg && fg.Children.OfType<TextBlock>().LastOrDefault() is TextBlock clock)
                {
                    clock.FontWeight = FontWeights.Medium;
                    clock.Foreground = Frozen(Color.FromRgb(0xEC, 0xEB, 0xE8));
                }
                stage.Children.Add(flat);
                break;
            }
            default:
                stage.Background = new LinearGradientBrush(Color.FromRgb(0x1E, 0x26, 0x3E), Color.FromRgb(0x12, 0x17, 0x2A), 30);
                stage.Children.Add(new Border
                {
                    Width = w, Height = h, CornerRadius = new CornerRadius(0, 0, h / 2, h / 2), Background = Brushes.Black, VerticalAlignment = VerticalAlignment.Top,
                    BorderBrush = Frozen(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1, 0, 1, 1), Child = Clock(),
                });
                break;
        }
        return PickCard(Stage(stage), Loc.T(ThemeTitles[key]), on, () =>
        {
            _notch.SetTheme(key);
            Build(); // (the accent follows the theme)
            Saved();
        });
    }

    /// <summary>The colour mixer: the same one as the notch's Colors page, bigger, painting the notch live.</summary>
    FrameworkElement Mixer()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        var left = new StackPanel { Margin = new Thickness(18, 18, 0, 18) };
        var wheel = new ColorWheel { Width = 150, Height = 150, FlowDirection = FlowDirection.LeftToRight };
        var bright = new PillSlider { Height = 20, Margin = new Thickness(4, 12, 4, 0), TrackHeight = 7, FlowDirection = FlowDirection.LeftToRight };
        left.Children.Add(wheel);
        left.Children.Add(bright);
        grid.Children.Add(left);

        var right = new StackPanel { Margin = new Thickness(22, 18, 18, 18) };
        Grid.SetColumn(right, 1);
        int count = NotchWindow.PrefPaintCount;
        var targets = new UniformGrid { Columns = count };
        var swatches = new Border[count];
        TextBlock hex = null!, hint = null!;
        Button auto = null!;
        var labels = new TextBlock[count];
        var names = Enumerable.Range(0, count).Select(NotchWindow.PrefPaintName).ToArray();
        var hints = Enumerable.Range(0, count).Select(NotchWindow.PrefPaintHint).ToArray();
        for (int i = 0; i < count; i++)
        {
            int idx = i;
            var cell = new StackPanel { Cursor = Cursors.Hand, Background = Brushes.Transparent };
            swatches[i] = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), BorderThickness = new Thickness(2.5) };
            labels[i] = new TextBlock { Text = Loc.T(names[i]), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
            cell.Children.Add(swatches[i]);
            cell.Children.Add(labels[i]);
            cell.MouseLeftButtonUp += (_, _) => { _paint = idx; Load(); };
            targets.Children.Add(cell);
        }
        right.Children.Add(targets);

        var readout = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        readout.ColumnDefinitions.Add(new ColumnDefinition());
        readout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var info = new StackPanel();
        hex = new TextBlock { FontSize = 18, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"), FlowDirection = FlowDirection.LeftToRight, HorizontalAlignment = Loc.Rtl ? HorizontalAlignment.Right : HorizontalAlignment.Left };
        hint = new TextBlock { FontSize = 12, Foreground = Dim };
        info.Children.Add(hex);
        info.Children.Add(hint);
        readout.Children.Add(info);
        auto = PillButton(null, Loc.T("Auto"), () => { }, primary: false);
        Grid.SetColumn(auto, 1);
        readout.Children.Add(auto);
        right.Children.Add(readout);

        var presets = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
        for (int i = 0; i < NotchWindow.PresetCount; i++)
        {
            int idx = i;
            var cols = NotchWindow.PresetColors(i);
            Brush fill;
            if (cols == null) fill = new LinearGradientBrush(Colors.White, Color.FromRgb(0x3A, 0x3A, 0x3E), 45);
            else
            {
                var g = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
                for (int k = 0; k < 3; k++) { g.GradientStops.Add(new GradientStop(cols[k], k / 3.0)); g.GradientStops.Add(new GradientStop(cols[k], (k + 1) / 3.0)); }
                fill = g;
            }
            var tile = new Border
            {
                Width = 54, Height = 26, CornerRadius = new CornerRadius(8), Background = fill, Margin = new Thickness(0, 0, 8, 8), Cursor = Cursors.Hand,
                BorderBrush = Edge, BorderThickness = new Thickness(1), ToolTip = Loc.T(NotchWindow.PresetName(i).Split(" · ")[0]),
            };
            if (cols == null) tile.Child = new TextBlock { Text = "Auto", FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = Brushes.Black, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            tile.MouseLeftButtonUp += (_, _) => { _notch.PrefPreset(idx); Load(); Saved(); };
            presets.Children.Add(tile);
        }
        presets.Children.Add(PillButton("", Loc.T("Shuffle"), () => { _notch.PrefShuffle(); Load(); Saved(); }, primary: false));
        right.Children.Add(presets);
        grid.Children.Add(right);

        void Load()
        {
            var c = _notch.PrefShownColor(_paint);
            var (h, s, v) = ColorWheel.ToHsv(c);
            if (s > 0.01) _h = h;
            _sat = s;
            _v = v;
            bright.Value = v;
            Show();
        }

        void Show()
        {
            var c = _notch.PrefShownColor(_paint);
            bool isAuto = _notch.PrefColor(_paint) == null;
            wheel.SetMarker(_h, _sat, c);
            var mix = new LinearGradientBrush(Colors.Black, ColorWheel.Hsv(_h, _sat, 1), 0);
            mix.Freeze();
            bright.FillBrush = mix;
            hex.Text = (isAuto ? Loc.T("Auto") + " · " : "") + $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            hint.Text = Loc.T(hints[_paint]);
            for (int i = 0; i < count; i++)
            {
                var k = _notch.PrefColor(i) == null ? (Brush?)null : Frozen(_notch.PrefShownColor(i));
                swatches[i].Background = k ?? new LinearGradientBrush(Color.FromRgb(0xFF, 0x5E, 0x5E), Color.FromRgb(0x0A, 0x84, 0xFF), 45);
                swatches[i].BorderBrush = i == _paint ? Text : Edge;
                labels[i].Foreground = i == _paint ? Text : Dim;
                labels[i].FontWeight = i == _paint ? FontWeights.SemiBold : FontWeights.Normal;
            }
            SetPill(auto, isAuto);
        }

        void Paint(bool save)
        {
            var c = ColorWheel.Hsv(_h, _sat, _v);
            _notch.PrefSetColor(_paint, $"#{c.R:X2}{c.G:X2}{c.B:X2}", save);
            Show();
        }

        wheel.Picked += (h, s) => { _h = h; _sat = s; if (_v < 0.2) { _v = 1; bright.Value = 1; } Paint(false); };
        wheel.Committed += () => { Paint(true); Saved(); };
        bright.ValueChanged += v => { _v = Math.Max(0.04, v); Paint(false); };
        bright.ValueCommitted += _ => { Paint(true); Saved(); };
        auto.Click += (_, _) =>
        {
            if (_notch.PrefColor(_paint) == null) Paint(true);
            else _notch.PrefSetColor(_paint, null, true);
            Load();
            Saved();
        };
        grid.Loaded += (_, _) => Load();
        return CardOf(grid);
    }

    /// <summary>Behaviour, and the FPS pill's extras: what shows beside the frame rate in a game, and in which colour.</summary>
    void Behaviour()
    {
        Card(Rows().Where(r => r.Sec == Sec.Behaviour));

        Label(Loc.T("Beside the FPS in games"));
        var stats = new List<Row>
        {
            new(Sec.Behaviour, "", "CPU usage", "The processor's load, in %", () => _notch.PrefFpsStat("cpu"), v => _notch.PrefSetFpsStat("cpu", v)),
            new(Sec.Behaviour, "", "GPU usage", "The graphics card's load, in %", () => _notch.PrefFpsStat("gpu"), v => _notch.PrefSetFpsStat("gpu", v)),
            new(Sec.Behaviour, "", "CPU temperature", StrainService.CpuTempAvailable ? "In °C" : Loc.T("This PC doesn't report it"),
                () => _notch.PrefFpsStat("cputemp") && StrainService.CpuTempAvailable, v => _notch.PrefSetFpsStat("cputemp", v)),
            new(Sec.Behaviour, "", "GPU temperature", "In °C (when the graphics driver reports it)", () => _notch.PrefFpsStat("gputemp"), v => _notch.PrefSetFpsStat("gputemp", v)),
        };
        Card(stats);
        Note(Loc.T("Small and quiet: they change once a second, in fixed widths, so nothing moves in the corner of your eye."));

        Label(Loc.T("Size in games"));
        var sizes = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        foreach (var (name, k) in new[] { ("Small", 0.85), ("Normal", 1.0), ("Large", 1.2), ("Extra large", 1.4) })
        {
            var b = PillButton(null, Loc.T(name), () => { }, primary: false);
            b.Margin = new Thickness(0, 0, 8, 8);
            b.Tag = k;
            SetPill(b, Math.Abs(_notch.PrefFpsScale - k) < 0.01);
            double size = k;
            b.Click += (_, _) =>
            {
                _notch.PrefSetFpsScale(size);
                foreach (var other in sizes.Children.OfType<Button>()) SetPill(other, ReferenceEquals(other, b));
                Saved();
            };
            sizes.Children.Add(b);
        }
        _content.Children.Add(sizes);

        Label(Loc.T("Their colours"));
        var colours = new StackPanel { Margin = new Thickness(16, 10, 16, 10) };
        foreach (bool cpu in new[] { true, false })
        {
            var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            g.Children.Add(new TextBlock { Text = cpu ? "CPU" : "GPU", FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center });
            var swatches = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, FlowDirection = FlowDirection.LeftToRight };
            bool isCpu = cpu;
            void Paint()
            {
                string current = _notch.PrefFpsColor(isCpu) ?? NotchWindow.StatColors[0].Hex;
                foreach (Border b in swatches.Children) b.BorderBrush = string.Equals((string)b.Tag, current, StringComparison.OrdinalIgnoreCase) ? Text : Brushes.Transparent;
            }
            foreach (var (name, hex) in NotchWindow.StatColors)
            {
                var sw = new Border
                {
                    Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Margin = new Thickness(4, 0, 0, 0), Cursor = Cursors.Hand, Tag = hex,
                    Background = Frozen((Color)ColorConverter.ConvertFromString(hex)), BorderThickness = new Thickness(2), ToolTip = Loc.T(name),
                };
                string chosen = hex;
                sw.MouseLeftButtonUp += (_, _) => { _notch.PrefSetFpsColor(isCpu, chosen == NotchWindow.StatColors[0].Hex ? null : chosen); Paint(); Saved(); };
                swatches.Children.Add(sw);
            }
            Paint();
            g.Children.Add(swatches);
            colours.Children.Add(g);
        }
        _content.Children.Add(CardOf(colours));

        BrowserTabs();
    }

    /// <summary>
    /// The optional browser extension: a browser tells Windows about one playing tab only; with the extension every
    /// tab that plays shows in the notch, with its own buttons. Its state, the folder it's in, and how to add it.
    /// </summary>
    void BrowserTabs()
    {
        Label(Loc.T("Every browser tab that plays"));
        var box = new StackPanel { Margin = new Thickness(16, 14, 16, 16) };
        var connected = _notch.PrefBrowsersConnected;
        string Name(string b) => b switch { "MSEdge" => "Edge", _ => b };
        box.Children.Add(new TextBlock
        {
            Text = connected.Count > 0
                ? string.Format(Loc.T("Connected: {0}"), string.Join(", ", connected.Select(Name)))
                : Loc.T("Optional: without it, the notch shows the one tab your browser tells Windows about"),
            FontSize = 13.5, FontWeight = connected.Count > 0 ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap,
            Foreground = connected.Count > 0 ? Good : Text,
        });
        box.Children.Add(new TextBlock
        {
            Text = Loc.T("The \"Pixo Media\" extension shows every tab playing a video, music or a stream, with play, pause, next and previous. It only talks to the notch on this PC."),
            FontSize = 12, Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 10),
        });
        string[] steps =
        {
            Loc.T("1. Open your browser's extensions page: chrome://extensions, edge://extensions, brave://extensions or opera://extensions"),
            Loc.T("2. Turn on \"Developer mode\""),
            Loc.T("3. Click \"Load unpacked\" and choose the \"chromium\" folder"),
            Loc.T("Firefox: about:debugging → This Firefox → Load Temporary Add-on → firefox\\manifest.json"),
        };
        foreach (var s in steps)
            box.Children.Add(new TextBlock { Text = s, FontSize = 12.5, Foreground = Text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6), LineHeight = 18 });
        string folder = System.IO.Path.Combine(AppContext.BaseDirectory, "extension");
        var open = PillButton("", Loc.T("Open the extension folder"), () =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true }); }
            catch (Exception ex) { AppLog.Write("extension folder: " + ex.Message); }
        }, primary: true);
        open.HorizontalAlignment = HorizontalAlignment.Left;
        open.Margin = new Thickness(0, 10, 0, 0);
        box.Children.Add(open);
        _content.Children.Add(CardOf(box));
    }

    void Alerts()
    {
        Label(Loc.T("Alerts"));
        Card(Rows().Where(r => r.Sec == Sec.Alerts && r.Group == null));
        // the sound for messages, reminders and finished answers: each one plays as you pick it
        Choice(Loc.T("Alert sound"), Services.NotificationSoundService.Choices.Select(c => (c.Key, Loc.T(c.Name))).ToArray(),
            () => _s.AlertSound, v => { _s.AlertSound = v; _notch.PrefPlayAlert(v); });
        Label(Loc.T("Smart helpers"));
        Card(Rows().Where(r => r.Sec == Sec.Alerts && r.Group == "Smart"));

        // strain warnings ("Overload risk" in Neon): the master switch and what it watches
        Label(Loc.T("Overload warnings"));
        var strain = new List<Row>
        {
            new(Sec.Alerts, "\uE7BA", "Overload warnings", "A warning when something stays over its line for 20 s (never in games)",
                () => _notch.PrefStrainOn("all"), v => _notch.PrefSetStrain("all", v)),
        };
        foreach (var (key, name, line) in _notch.PrefStrainTargets)
        {
            bool available = key != "cputemp" || StrainService.CpuTempAvailable;
            string glyph = key switch { "cpu" => "\uE950", "gpu" => "\uE7F4", "ram" => "\uE964", _ => "\uE9CA" };
            strain.Add(new(Sec.Alerts, glyph, name,
                available ? string.Format(Loc.T("Warn at {0} and over"), line) : Loc.T("This PC doesn't report it"),
                () => _notch.PrefStrainOn(key), v => { if (available) _notch.PrefSetStrain(key, v); }));
        }
        Card(strain);

        // where each line is: a stepper per target (5 % / 5 °C a step)
        Label(Loc.T("Warning lines"));
        var lines = new StackPanel();
        bool first = true;
        foreach (var (key, name, _) in _notch.PrefStrainTargets)
        {
            if (key == "cputemp" && !StrainService.CpuTempAvailable) continue;
            if (!first) lines.Children.Add(Divider(16));
            first = false;
            var g = new Grid { Margin = new Thickness(16, 10, 12, 10) };
            g.Children.Add(new TextBlock { Text = Loc.T(name), FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center });
            var stepper = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, FlowDirection = FlowDirection.LeftToRight };
            var value = new TextBlock { MinWidth = 54, TextAlignment = TextAlignment.Center, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            System.Windows.Documents.Typography.SetNumeralAlignment(value, FontNumeralAlignment.Tabular);
            string k = key;
            void ShowValue() { var (v, unit, _, _) = _notch.PrefStrainLine(k); value.Text = $"{v:0}{unit}"; }
            void Step(int dir) { var (v, _, _, _) = _notch.PrefStrainLine(k); _notch.PrefSetStrainLine(k, v + dir * 5); ShowValue(); Saved(); }
            var minus = PillButton("", "", () => Step(-1), primary: false);
            var plus = PillButton("", "", () => Step(1), primary: false);
            minus.Width = plus.Width = 34;
            minus.Padding = plus.Padding = new Thickness(0);
            stepper.Children.Add(minus);
            stepper.Children.Add(value);
            stepper.Children.Add(plus);
            ShowValue();
            g.Children.Add(stepper);
            lines.Children.Add(g);
        }
        _content.Children.Add(CardOf(lines));

        Label(Loc.T("Break reminder"));
        var row = new Grid { Margin = new Thickness(16, 12, 12, 12) };
        row.Children.Add(new TextBlock { Text = Loc.T("Break reminder every"), FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center });
        var chips = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (int m in new[] { 45, 60, 90, 120 })
        {
            var b = PillButton(null, $"{m} min", () => { }, primary: false);
            b.Margin = new Thickness(6, 0, 0, 0);
            SetPill(b, _s.BreakMinutes == m);
            b.Click += (_, _) =>
            {
                _s.BreakMinutes = m;
                _notch.PrefChanged();
                foreach (var other in chips.Children.OfType<Button>()) SetPill(other, ReferenceEquals(other, b));
                Saved();
            };
            chips.Children.Add(b);
        }
        row.Children.Add(chips);
        _content.Children.Add(CardOf(row));
    }

    void Pages()
    {
        Label(Loc.T("Always there"));
        var fixedRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(13, 13, 16, 13) };
        fixedRow.Children.Add(new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(8), Background = Faint, Child = Glyph("\uE80F", 14, Dim) });
        fixedRow.Children.Add(new TextBlock { Text = Loc.T("Home and Settings stay, so the notch is always usable"), Margin = new Thickness(13, 0, 0, 0), Foreground = Dim, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        _content.Children.Add(CardOf(fixedRow));
        Label(Loc.T("Shown in the control center, in this order"));
        // each page with its switch and two arrows: up moves it closer to Home, down further along the bar
        var rows = Rows().Where(r => r.Sec == Sec.Pages && r.Page >= 0).ToDictionary(r => r.Page);
        var list = new StackPanel();
        var order = _notch.PrefPageOrder;
        for (int k = 0; k < order.Count; k++)
        {
            int page = order[k];
            var line = new Grid();
            line.ColumnDefinitions.Add(new ColumnDefinition());
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (rows.TryGetValue(page, out var r)) line.Children.Add(RowView(r));
            else line.Children.Add(new TextBlock { Text = Loc.T(NotchWindow.PageName(page)), Margin = new Thickness(56, 18, 16, 18), FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center });
            line.Children.Add(OrderArrows(k, order.Count, d => { _notch.PrefMovePage(page, d); Redraw(); Saved(); }));
            if (k > 0) list.Children.Add(Divider(56));
            list.Children.Add(line);
        }
        _content.Children.Add(CardOf(list));
    }

    /// <summary>The search: its key, how else it opens, what it looks through, what it shows when empty, the web, memory.</summary>
    void SearchSection()
    {
        Label(Loc.T("Shortcut"));
        var keys = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        var taken = new TextBlock { Foreground = Warn, FontSize = 12, Margin = new Thickness(4, 0, 0, 10), TextWrapping = TextWrapping.Wrap };
        void ShowTaken()
        {
            string name = NotchWindow.LauncherKeyOf(_s.SearchHotkey).Name;
            taken.Text = _notch.PrefSearchKeyTaken ? string.Format(Loc.T("{0} is taken by another app: pick another one"), name) : "";
            taken.Visibility = taken.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        foreach (var (key, name) in new[] { ("alt-space", "Alt+Space"), ("ctrl-space", "Ctrl+Space"), ("ctrl-alt-space", "Ctrl+Alt+Space"), ("win-alt-space", "Win+Alt+Space"), ("off", "Off") })
        {
            var b = PillButton(null, key == "off" ? Loc.T(name) : name, () => { }, primary: false);
            b.Margin = new Thickness(0, 0, 8, 8);
            SetPill(b, _s.SearchHotkey == key || key == "alt-space" && NotchWindow.LauncherKeyOf(_s.SearchHotkey).Name == "Alt+Space");
            string k = key;
            b.Click += (_, _) =>
            {
                _s.SearchHotkey = k;
                _notch.PrefSearchChanged();
                foreach (var other in keys.Children.OfType<Button>()) SetPill(other, ReferenceEquals(other, b));
                ShowTaken();
                Saved();
            };
            keys.Children.Add(b);
        }
        _content.Children.Add(keys);
        ShowTaken();
        _content.Children.Add(taken);
        Card(Rows().Where(r => r.Sec == Sec.Search && r.Group == "open"));

        Label(Loc.T("What it finds"));
        Card(Rows().Where(r => r.Sec == Sec.Search && r.Group == "find"));

        Label(Loc.T("When it's empty"));
        Card(Rows().Where(r => r.Sec == Sec.Search && r.Group == "empty"));
        if (_notch.PrefHiddenApps > 0)
        {
            Button? back = null;
            back = PillButton("\uE7B3", string.Format(Loc.T("Show removed apps again ({0})"), _notch.PrefHiddenApps), () =>
            {
                _notch.PrefShowHiddenApps();
                if (back != null) back.Visibility = Visibility.Collapsed;
                Saved();
            }, primary: false);
            back.HorizontalAlignment = HorizontalAlignment.Left;
            back.Margin = new Thickness(0, 0, 0, 6);
            _content.Children.Add(back);
        }

        Choice(Loc.T("Web search"), new[] { ("google", "Google"), ("bing", "Bing"), ("duckduckgo", "DuckDuckGo"), ("brave", "Brave Search") },
            () => _s.SearchEngine, v => _s.SearchEngine = v);
        Choice(Loc.T("AI assistant for questions"), new[] { ("chatgpt", "ChatGPT"), ("claude", "Claude"), ("perplexity", "Perplexity"), ("copilot", "Copilot"), ("off", Loc.T("Off")) },
            () => _s.SearchAssistant, v => _s.SearchAssistant = v);
        Label(Loc.T("Memory"));
        var memory = new WrapPanel();
        Button? recent = null;
        recent = PillButton("\uE81C", Loc.T("Forget recent searches"), () =>
        {
            _notch.PrefForgetSearches();
            if (recent != null) recent.IsEnabled = false;
            Saved();
        }, primary: false);
        recent.Margin = new Thickness(0, 0, 8, 8);
        recent.IsEnabled = _notch.PrefRecentSearches > 0;
        memory.Children.Add(recent);
        var learned = PillButton("\uE945", Loc.T("Forget what it learned"), () =>
        {
            if (MessageBox.Show(this, Loc.T("Frequent apps and the order of results start over."), Loc.T("Forget what it learned"),
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            _notch.PrefForgetLearned();
            Saved();
        }, primary: false);
        learned.Margin = new Thickness(0, 0, 8, 8);
        memory.Children.Add(learned);
        _content.Children.Add(memory);
    }

    /// <summary>The Calls page: the page, its buttons, what happens when a call starts, your quick replies.</summary>
    void CallsSection()
    {
        Label(Loc.T("The page"));
        Card(Rows().Where(r => r.Sec == Sec.Calls && r.Group == "page"));
        Label(Loc.T("When a call starts"));
        Card(Rows().Where(r => r.Sec == Sec.Calls && r.Group == "start"));

        Label(Loc.T("Quick replies"));
        Note(Loc.T("One per line. A click copies it and opens the call's chat."), top: -4);
        var box = new TextBox
        {
            Text = string.Join(Environment.NewLine, _s.CallReplies), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 96, MaxHeight = 180,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(10, 8, 10, 8),
        };
        box.LostKeyboardFocus += (_, _) =>
        {
            var list = box.Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(12).ToList();
            if (list.SequenceEqual(_s.CallReplies)) return;
            _s.CallReplies = list;
            _s.Save();
            Saved();
        };
        box.Margin = new Thickness(0, 0, 0, 8);
        _content.Children.Add(box);
        var clear = PillButton("\uE74D", Loc.T("Clear call history"), () => { _notch.PrefClearCallHistory(); Saved(); }, primary: false);
        clear.HorizontalAlignment = HorizontalAlignment.Left;
        _content.Children.Add(clear);
    }

    /// <summary>
    /// The AI chat: on or off and what it may do, the key (pasted here or in the chat, checked with OpenRouter),
    /// the model, how it answers, the user's own instructions, and the kept chats.
    /// </summary>
    void AiSectionView()
    {
        Card(Rows().Where(r => r.Sec == Sec.Ai && r.Group == "ai"));

        // ── who answers
        Services.OpenRouter.Choose(_s.AiProvider);
        Choice(Loc.T("The AI runs on"), new[] { ("gemini", Loc.T("Google Gemini + Groq (free, thousands of requests a day)")), ("openrouter", Loc.T("OpenRouter (many models; 50 free a day)")) },
            () => Services.OpenRouter.IsGemini ? "gemini" : "openrouter", v => { _s.AiProvider = v; Services.OpenRouter.Choose(v); RenderContent(); });

        // ── the key
        Label(Loc.T("Connection"));
        var box = new StackPanel();
        var state = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 2) };
        var help = new TextBlock { FontSize = 12, Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        void ShowState()
        {
            bool has = Services.OpenRouter.HasKey;
            state.Text = has ? "✓  " + Loc.T("Connected to") + " " + Services.OpenRouter.ProviderName : Loc.T("Not connected yet");
            state.Foreground = has ? Good : Text;
            help.Text = has ? Loc.T("Your key is saved on this PC, encrypted for your Windows account. Paste another one below to replace it.")
                : Services.OpenRouter.IsGemini
                    ? Loc.T("1. Open Google AI Studio (Get a key) and sign in with Google.  2. Click “Create API key” and copy it.  3. Paste it below and press Check & save.")
                    : Loc.T("1. Sign in at openrouter.ai/keys (free).  2. Create a key and copy it right away (it's shown once).  3. Paste it below and press Check & save.");
        }
        ShowState();
        box.Children.Add(state);
        box.Children.Add(help);
        var field = new Grid();
        field.ColumnDefinitions.Add(new ColumnDefinition());
        field.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var pw = new PasswordBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0), VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(10, 7, 10, 7) };
        var pwHint = new TextBlock { Text = Services.OpenRouter.IsGemini ? "AQ.… or AIza…" : "sk-or-v1-…", Foreground = Muted, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        pw.PasswordChanged += (_, _) => pwHint.Visibility = pw.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var pwHost = new Grid();
        pwHost.Children.Add(pw);
        pwHost.Children.Add(pwHint);
        field.Children.Add(FieldHost(pwHost, new Thickness(0, 0, 8, 0)));
        var result = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 8, 0, 0), Visibility = Visibility.Collapsed };
        Button check = null!;
        async void Check()
        {
            string raw = pw.Password.Trim();
            var m = System.Text.RegularExpressions.Regex.Match(raw, @"AIza[0-9A-Za-z_\-]{30,}|AQ\.[0-9A-Za-z_\-]{30,}|gsk_[A-Za-z0-9]{20,}|sk-or-[A-Za-z0-9_\-]{20,}");
            string key = m.Success ? m.Value : raw;
            result.Visibility = Visibility.Visible;
            if (key.Length < 32 || key.Contains(' '))
            {
                result.Foreground = Warn;
                result.Text = raw.Contains("...") || raw.Contains('…') ? Loc.T("That's a shortened key from a list: copy the whole key.") : Loc.T("That isn't a key: a Google key starts with AQ. or AIza, an OpenRouter key with sk-or-v1-.");
                return;
            }
            check.IsEnabled = false;
            result.Foreground = Dim;
            result.Text = Loc.T("Checking the key…");
            bool? ok = await Services.OpenRouter.CheckKey(key);
            check.IsEnabled = true;
            if (ok == false) { result.Foreground = Warn; result.Text = Loc.T("The provider says this key isn't valid. Create a new one and paste it here."); return; }
            if (!Services.OpenRouter.SaveKey(key)) { result.Foreground = Warn; result.Text = Loc.T("Couldn't save the key on this PC."); return; }
            pw.Password = "";
            result.Foreground = Good;
            result.Text = ok == true ? Loc.T("Key checked and saved.") : Loc.T("Saved. It couldn't be checked right now: it's checked on your first message.");
            if (Services.OpenRouter.KindOf(key) is { } kind) { _s.AiProvider = kind; Services.OpenRouter.Choose(_s.AiProvider); }
            ShowState();
            Saved();
        }
        check = PillButton("\uE73E", Loc.T("Check & save"), Check, primary: true);
        pw.KeyDown += (_, e) => { if (e.Key == Key.Enter) Check(); };
        Grid.SetColumn(check, 1);
        field.Children.Add(check);
        box.Children.Add(field);
        box.Children.Add(result);
        var links = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        var get = PillButton("\uE8A7", Loc.T("Get a key"), () => Open(Services.OpenRouter.IsGemini ? "https://aistudio.google.com/apikey" : "https://openrouter.ai/keys"), primary: false);
        get.Margin = new Thickness(0, 0, 8, 0);
        links.Children.Add(get);
        var credits = PillButton("\uE8C7", Services.OpenRouter.IsGemini ? Loc.T("Your limits") : Loc.T("Credits"), () => Open(Services.OpenRouter.IsGemini ? "https://aistudio.google.com/usage" : "https://openrouter.ai/settings/credits"), primary: false);
        credits.Margin = new Thickness(0, 0, 8, 0);
        links.Children.Add(credits);
        Button forget = null!;
        forget = PillButton("\uE74D", Loc.T("Forget the key"), () => { Services.OpenRouter.ForgetKey(); ShowState(); forget.IsEnabled = false; Saved(); }, primary: false);
        forget.IsEnabled = Services.OpenRouter.HasKey;
        links.Children.Add(forget);
        box.Children.Add(links);
        _content.Children.Add(CardOf(box, new Thickness(16)));

        // ── Groq: the second engine (free, very fast); it shares the work with Google and covers for it
        if (Services.OpenRouter.IsGemini)
        {
            Label(Loc.T("Second engine: Groq (optional, free)"));
            var gbox = new StackPanel();
            var gstate = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 2) };
            var ghelp = new TextBlock { FontSize = 12, Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
            void GroqState()
            {
                bool has = Services.OpenRouter.HasGroqKey, refused = Services.OpenRouter.GroqRefused;
                gstate.Text = refused ? Loc.T("Groq didn't accept its key") : has ? "✓  " + Loc.T("Groq connected: it shares the work with Google") : Loc.T("Not added");
                gstate.Foreground = refused ? Warn : has ? Good : Text;
                ghelp.Text = has && !refused
                    ? Loc.T("Quick questions and commands go to Groq (it answers in under a second), big or visual ones to Google, and each takes over when the other is busy, used up or down.")
                    : Loc.T("1. Open console.groq.com/keys and sign in (free).  2. Click “Create API Key” and copy it.  3. Paste it below and press Check & save. It makes the AI faster and gives it a second daily allowance.");
            }
            GroqState();
            gbox.Children.Add(gstate);
            gbox.Children.Add(ghelp);
            var gfield = new Grid();
            gfield.ColumnDefinitions.Add(new ColumnDefinition());
            gfield.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var gpw = new PasswordBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0), VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(10, 7, 10, 7) };
            var gHint = new TextBlock { Text = "gsk_…", Foreground = Muted, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            gpw.PasswordChanged += (_, _) => gHint.Visibility = gpw.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            var gHost = new Grid();
            gHost.Children.Add(gpw);
            gHost.Children.Add(gHint);
            gfield.Children.Add(FieldHost(gHost, new Thickness(0, 0, 8, 0)));
            var gresult = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 8, 0, 0), Visibility = Visibility.Collapsed };
            Button gcheck = null!;
            async void GroqCheck()
            {
                var gm = System.Text.RegularExpressions.Regex.Match(gpw.Password.Trim(), @"gsk_[A-Za-z0-9]{20,}");
                gresult.Visibility = Visibility.Visible;
                if (!gm.Success) { gresult.Foreground = Warn; gresult.Text = Loc.T("That isn't a Groq key: it starts with gsk_."); return; }
                gcheck.IsEnabled = false;
                gresult.Foreground = Dim;
                gresult.Text = Loc.T("Checking with Groq…");
                bool? ok = await Services.OpenRouter.CheckKey(gm.Value);
                gcheck.IsEnabled = true;
                if (ok == false) { gresult.Foreground = Warn; gresult.Text = Loc.T("Groq says this key isn't valid. Create a new one and paste it here."); return; }
                if (!Services.OpenRouter.SaveKey(gm.Value)) { gresult.Foreground = Warn; gresult.Text = Loc.T("Couldn't save the key on this PC."); return; }
                gpw.Password = "";
                _s.AiProvider = "gemini";
                _s.AiGroqAsked = true;
                Services.OpenRouter.Choose(_s.AiProvider);
                gresult.Foreground = Good;
                gresult.Text = ok == true ? Loc.T("Groq added: the two now share the work.") : Loc.T("Saved. It couldn't be checked right now: it's checked on first use.");
                GroqState();
                Saved();
            }
            gcheck = PillButton("\uE73E", Loc.T("Check & save"), GroqCheck, primary: true);
            gpw.KeyDown += (_, e) => { if (e.Key == Key.Enter) GroqCheck(); };
            Grid.SetColumn(gcheck, 1);
            gfield.Children.Add(gcheck);
            gbox.Children.Add(gfield);
            gbox.Children.Add(gresult);
            var glinks = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            var gget = PillButton("\uE8A7", Loc.T("Get a Groq key"), () => Open("https://console.groq.com/keys"), primary: false);
            gget.Margin = new Thickness(0, 0, 8, 0);
            glinks.Children.Add(gget);
            Button gforget = null!;
            gforget = PillButton("\uE74D", Loc.T("Remove Groq"), () => { try { System.IO.File.Delete(System.IO.Path.Combine(Settings.Folder, "groq.key")); } catch { } GroqState(); gforget.IsEnabled = false; Saved(); }, primary: false);
            gforget.IsEnabled = Services.OpenRouter.HasGroqKey;
            glinks.Children.Add(gforget);
            gbox.Children.Add(glinks);
            _content.Children.Add(CardOf(gbox, new Thickness(16)));
        }

        // ── the model
        if (Services.OpenRouter.IsGemini)
        {
            Choice(Loc.T("Model"), new[] { ("openrouter/free", Loc.T("Auto (best with free requests left)")), ("gemini-flash-lite-latest", "Gemini Flash-Lite"), ("gemma-4-31b-it", "Gemma 4 31B"), ("gemma-4-26b-a4b-it", "Gemma 4 26B"), ("gemini-flash-latest", "Gemini Flash") },
                () => _s.AiGeminiModel, v => _s.AiGeminiModel = v);
            _content.Children.Add(new TextBlock { Text = Loc.T("Free with Google, per day: Gemma 4 about 1,500 each, Flash-Lite about 500, Flash about 20. Auto moves to the next model when one runs out; they all come back at midnight, California time."), Foreground = Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 4, 0, 6) });
        }
        else
        Choice(Loc.T("Model"), new[]
        {
            ("openrouter/free", Loc.T("Free (auto)")), ("deepseek/deepseek-v4-flash", "DeepSeek Flash"), ("google/gemini-3-flash-preview", "Gemini Flash"),
            ("anthropic/claude-haiku-5.5", "Claude Haiku"), ("anthropic/claude-sonnet-5.5", "Claude Sonnet"),
        }, () => _s.AiModel, v => _s.AiModel = v);
        var custom = new Grid { Margin = new Thickness(0, 0, 0, 4), Visibility = Services.OpenRouter.IsGemini ? Visibility.Collapsed : Visibility.Visible };
        custom.ColumnDefinitions.Add(new ColumnDefinition());
        custom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        custom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var idBox = new TextBox { Text = _s.AiModel, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(10, 7, 10, 7), ToolTip = Loc.T("Any model id from openrouter.ai/models") };
        custom.Children.Add(FieldHost(idBox, new Thickness(0, 0, 8, 0)));
        var use = PillButton(null, Loc.T("Use this model"), () =>
        {
            string id = idBox.Text.Trim();
            if (id.Length == 0) return;
            _s.AiModel = id;
            _notch.PrefChanged();
            RenderContent();
            Saved();
        }, primary: false);
        Grid.SetColumn(use, 1);
        custom.Children.Add(use);
        var browse = PillButton("\uE8A7", Loc.T("Browse"), () => Open("https://openrouter.ai/models"), primary: false);
        browse.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(browse, 2);
        custom.Children.Add(browse);
        _content.Children.Add(custom);
        if (!Services.OpenRouter.IsGemini)
            _content.Children.Add(new TextBlock { Text = Loc.T("Free models cost nothing but can be slow or busy. Paid ones need credits on OpenRouter, and are better at doing things on the PC."), Foreground = Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 4, 0, 6) });

        // ── where it searches
        Choice(Loc.T("It searches the web with"), new[] { ("google", "Google"), ("brave", "Brave Search"), ("duckduckgo", "DuckDuckGo") },
            () => _s.AiSearchEngine, v => { _s.AiSearchEngine = v; Services.WebTools.Engine = v; });
        _content.Children.Add(new TextBlock { Text = Loc.T("If it doesn't answer, the others are tried, then the news and Wikipedia. Google runs in Windows' own Edge engine, out of sight."), Foreground = Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 0, 0, 8) });

        // ── what waits for your OK
        Choice(Loc.T("Ask before running commands"), new[] { ("risky", Loc.T("Only risky ones")), ("changes", Loc.T("Anything that changes something")), ("never", Loc.T("Never ask")) },
            () => _s.AiConfirm ? _s.AiAsk : "never", v => { _s.AiAsk = v; _s.AiConfirm = true; });
        _content.Children.Add(new TextBlock { Text = Loc.T("Risky: deleting, installing, closing programs, downloads, the registry, security, disks and power. Looking things up never asks."), Foreground = Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 0, 0, 8) });

        // ── how it answers
        Choice(Loc.T("Answers"), new[] { ("brief", Loc.T("Brief")), ("balanced", Loc.T("Balanced")), ("detailed", Loc.T("Detailed")) }, () => _s.AiStyle, v => _s.AiStyle = v);
        Choice(Loc.T("Creativity"), new[] { ("precise", Loc.T("Precise")), ("balanced", Loc.T("Balanced")), ("creative", Loc.T("Creative")) }, () => _s.AiCreativity, v => _s.AiCreativity = v);

        Label(Loc.T("Your instructions"));
        var ins = new TextBox
        {
            Text = _s.AiInstructions, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 74, MaxHeight = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(10, 8, 10, 8),
        };
        var insHint = new TextBlock { Text = Loc.T("What it should always know or do. E.g. “I'm a student in Casablanca. Answer in French. Keep code in C#.”"), Foreground = Muted, TextWrapping = TextWrapping.Wrap, IsHitTestVisible = false, Margin = new Thickness(12, 9, 12, 0) };
        insHint.Visibility = ins.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ins.TextChanged += (_, _) => insHint.Visibility = ins.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ins.LostKeyboardFocus += (_, _) =>
        {
            if (ins.Text == _s.AiInstructions) return;
            _s.AiInstructions = ins.Text.Length > 1500 ? ins.Text[..1500] : ins.Text;
            _notch.PrefChanged();
            Saved();
        };
        var insHost = new Grid();
        insHost.Children.Add(ins);
        insHost.Children.Add(insHint);
        _content.Children.Add(FieldHost(insHost, new Thickness(0, 0, 0, 8)));

        // ── voice
        Label(Loc.T("Voice"));
        Card(new[]
        {
            new Row(Sec.Ai, "\uE720", "Talk to it", "The mic beside the message box, or Ctrl+M: your words appear as you speak", () => _s.AiVoice, v => _s.AiVoice = v, "voice"),
            new Row(Sec.Ai, "\uE724", "Send what I say right away", "Off: what you said waits in the box so you can check it", () => _s.AiVoiceSend, v => _s.AiVoiceSend = v, "voice"),
            new Row(Sec.Ai, "\uE9CE", "Double-check unclear speech with Gemini", "When the offline voice isn't sure (Darija, mixed languages), that recording is checked again by Google's Gemini (with its key)", () => _s.AiVoiceDoubleCheck, v => _s.AiVoiceDoubleCheck = v, "voice"),
        });
        Choice(Loc.T("Listening with"), new[]
            {
                ("auto", Loc.T("Smart: on this PC when ready, else Gemini or Windows")),
                ("local", Loc.T("On this PC (offline, private, about 100 languages)")),
                ("windows", Loc.T("Windows (words appear as you speak; needs its online speech setting)")),
                ("ai", Services.OpenRouter.IsGemini ? Loc.T("Gemini (free with your key, any language)") : Loc.T("OpenRouter (needs $0.50 credit, any language)")),
            },
            () => _s.AiVoiceEngine == "openrouter" ? "ai" : _s.AiVoiceEngine, v => _s.AiVoiceEngine = v);
        Choice(Loc.T("Offline voice"), new[] { ("fast", Loc.T("Fast · 56 MB, about a second")), ("accurate", Loc.T("More accurate · 181 MB, slower")) },
            () => _s.AiVoiceModel, v => _s.AiVoiceModel = v);
        // the offline voice knows about 100 languages; Windows' dictation only a few: both kinds are listed
        var langs = new List<(string, string)> { ("auto", Loc.T("Detect it")) };
        try { foreach (var l in Windows.Media.SpeechRecognition.SpeechRecognizer.SupportedTopicLanguages) langs.Add((l.LanguageTag, l.DisplayName)); } catch { }
        foreach (var (tag, name) in new[] { ("ar", "العربية / Darija"), ("fr", "Français"), ("en", "English"), ("es", "Español"), ("de", "Deutsch"), ("it", "Italiano"), ("pt", "Português"), ("nl", "Nederlands"), ("tr", "Türkçe"), ("ru", "Русский"), ("hi", "हिन्दी"), ("zh", "中文"), ("ja", "日本語"), ("ko", "한국어") })
            if (!langs.Any(l => l.Item1.StartsWith(tag, StringComparison.OrdinalIgnoreCase))) langs.Add((tag, name));
        Choice(Loc.T("The language you speak"), langs.ToArray(), () => _s.AiVoiceLanguage, v => _s.AiVoiceLanguage = v);
        Choice(Loc.T("Read answers aloud"), new[] { ("voice", Loc.T("When I talk")), ("always", Loc.T("Always")), ("never", Loc.T("Never")) }, () => _s.AiSpeak, v => _s.AiSpeak = v);
        var voices = Services.VoiceOut.Voices();
        if (voices.Count > 0)
            Choice(Loc.T("Its voice"), new[] { ("", Loc.T("Default")) }.Concat(voices.Select(v => (v.Id, $"{v.Name.Replace("Microsoft ", "")} · {v.Language}"))).ToArray(), () => _s.AiVoiceId, v => _s.AiVoiceId = v);
        Choice(Loc.T("Speed"), new[] { ("0.85", Loc.T("Slower")), ("1", Loc.T("Normal")), ("1.25", Loc.T("Faster")), ("1.5", Loc.T("Fast")) },
            () => _s.AiVoiceRate.ToString(System.Globalization.CultureInfo.InvariantCulture), v => _s.AiVoiceRate = double.Parse(v, System.Globalization.CultureInfo.InvariantCulture));
        _content.Children.Add(new TextBlock { Text = Loc.T("More voices (Arabic, French...): Windows Settings → Time & language → Speech → Add voices."), Foreground = Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 0, 0, 8) });

        // ── pictures
        Choice(Loc.T("Making pictures"), new[] { ("", Loc.T("Automatic (free if any, else cheapest)")), ("google/gemini-3.1-flash-lite-image", "Gemini Flash Lite Image"), ("google/gemini-2.5-flash-image", "Gemini 2.5 Flash Image"), ("openai/gpt-5-image-mini", "GPT Image mini") },
            () => _s.AiImageModel, v => _s.AiImageModel = v);
        _content.Children.Add(new TextBlock { Text = Loc.T("Pictures are made through OpenRouter (Google's free tier makes none, and OpenRouter has no free picture model now): add an OpenRouter key with a little credit, about a cent a picture."), Foreground = Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 0, 0, 8) });

        // ── what it remembers
        Label(Loc.T("What it remembers about you"));
        var facts = Services.AiMemory.All();
        var memo = new StackPanel();
        void FillMemory()
        {
            memo.Children.Clear();
            var now = Services.AiMemory.All();
            if (now.Count == 0)
            {
                memo.Children.Add(new TextBlock { Text = Loc.T("Nothing yet. Tell it in the chat, e.g. “remember that I work night shifts”."), Foreground = Dim, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 14, 16, 14) });
                return;
            }
            for (int i = 0; i < now.Count; i++)
            {
                string fact = now[i];
                if (i > 0) memo.Children.Add(Divider(16));
                var row = new Grid { MinHeight = 38 };
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(new TextBlock { Text = fact, FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 10, 10, 10) });
                var del = IconButton("\uE74D", "Forget this", () => { Services.AiMemory.Remove(fact); FillMemory(); Saved(); });
                del.Margin = new Thickness(0, 0, 10, 0);
                Grid.SetColumn(del, 1);
                row.Children.Add(del);
                memo.Children.Add(row);
            }
        }
        FillMemory();
        _content.Children.Add(CardOf(memo));
        if (facts.Count > 0)
        {
            Button clearMemo = null!;
            clearMemo = PillButton("\uE74D", Loc.T("Forget everything"), () => { Services.AiMemory.Clear(); FillMemory(); clearMemo.IsEnabled = false; Saved(); }, primary: false);
            clearMemo.HorizontalAlignment = HorizontalAlignment.Left;
            clearMemo.Margin = new Thickness(0, 0, 0, 6);
            _content.Children.Add(clearMemo);
        }

        // ── what it learned while working (how things get done here, this person's habits)
        int learned = Services.AiLearn.Count;
        Label(Loc.T("What it learned on this PC"));
        _content.Children.Add(new TextBlock
        {
            Text = learned == 0 ? Loc.T("Nothing yet: it learns how things get done here as it works (which steps worked, your corrections, your habits).")
                : string.Format(Loc.T("{0} ways of doing things and lessons, used to get it right faster next time. Kept on this PC."), learned),
            FontSize = 11.5, Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 0, 6),
        });
        if (learned > 0)
        {
            Button forgetLearned = null!;
            forgetLearned = PillButton("\uE74D", Loc.T("Forget what it learned"), () => { Services.AiLearn.Clear(); forgetLearned.IsEnabled = false; Saved(); }, primary: false);
            forgetLearned.HorizontalAlignment = HorizontalAlignment.Left;
            forgetLearned.Margin = new Thickness(0, 0, 0, 6);
            _content.Children.Add(forgetLearned);
        }

        // ── kept chats
        Choice(Loc.T("Keep chats for"), new[] { ("0", Loc.T("Newest 60")), ("90", Loc.T("3 months")), ("30", Loc.T("1 month")), ("7", Loc.T("1 week")) },
            () => _s.AiKeepDays.ToString(), v => { _s.AiKeepDays = int.Parse(v); Services.ChatStore.KeepDays = _s.AiKeepDays; Services.ChatStore.Trim(); Saved(); });
        Label(Loc.T("Your chats"));
        var (n, bytes) = Services.ChatStore.Usage();
        var where = new TextBlock
        {
            Text = string.Format(Loc.T("{0} chats · {1} · pictures are never saved"), n, Services.SystemMonitor.Bytes(bytes)) + "\n" + Services.ChatStore.Folder,
            FontSize = 11.5, Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 0, 6),
        };
        _content.Children.Add(where);
        var openFolder = PillButton("\uE838", Loc.T("Open the folder"), () =>
        {
            System.IO.Directory.CreateDirectory(Services.ChatStore.Folder);
            Open(Services.ChatStore.Folder);
        }, primary: false);
        openFolder.HorizontalAlignment = HorizontalAlignment.Left;
        openFolder.Margin = new Thickness(0, 0, 0, 6);
        _content.Children.Add(openFolder);
        Button wipe = null!;
        bool armed = false;
        wipe = PillButton("\uE74D", n == 0 ? Loc.T("No chats kept") : string.Format(Loc.T("Delete all chats ({0})"), n), () =>
        {
            if (!armed) { armed = true; ((TextBlock)((StackPanel)wipe.Content).Children[^1]).Text = Loc.T("Click again to delete them all"); return; }
            Services.ChatStore.DeleteAll();
            ((TextBlock)((StackPanel)wipe.Content).Children[^1]).Text = Loc.T("Deleted");
            wipe.IsEnabled = false;
            Saved();
        }, primary: false);
        wipe.IsEnabled = n > 0;
        wipe.HorizontalAlignment = HorizontalAlignment.Left;
        _content.Children.Add(wipe);
    }

    static void Open(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    /// <summary>The open panel's size: four cards, each with the panel drawn at its size under the screen's edge.</summary>
    void PanelSizeSection()
    {
        Label(Loc.T("Open panel"));
        var cards = new UniformGrid { Columns = 4, Margin = new Thickness(0, 0, -8, 0) };
        string now = NotchWindow.PanelSizes.Any(p => p.Key == _s.PanelSize) ? _s.PanelSize : "standard";
        foreach (var (key, name, k) in NotchWindow.PanelSizes)
        {
            bool on = key == now;
            var stage = new Grid { Height = 64, Background = Frozen(Color.FromRgb(0x1F, 0x1F, 0x23)), ClipToBounds = true };
            // the panel at this size (as on a screen: wider is bigger), with a few rows inside it
            double w = Math.Round(66 * k), h = Math.Round(41 * k);
            var rows = new StackPanel { Margin = new Thickness(Math.Round(7 * k), Math.Round(9 * k), Math.Round(7 * k), 0) };
            foreach (double f in new[] { 0.62, 0.9, 0.48 })
                rows.Children.Add(new Border { Width = (w - 14 * k) * f, Height = Math.Max(2, Math.Round(3 * k)), CornerRadius = new CornerRadius(1.5), Margin = new Thickness(0, 0, 0, Math.Round(4 * k)), HorizontalAlignment = HorizontalAlignment.Left, Background = Frozen(Color.FromArgb(on ? (byte)0x8C : (byte)0x55, 0xFF, 0xFF, 0xFF)) });
            stage.Children.Add(new Border { Width = w, Height = h, CornerRadius = new CornerRadius(0, 0, 9 * k, 9 * k), Background = Brushes.Black, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center, Child = rows });
            string kk = key;
            cards.Children.Add(PickCard(Stage(stage), Loc.T(name), on, () => { _notch.PrefSetPanelSize(kk); Redraw(); Saved(); }));
        }
        _content.Children.Add(cards);
        Note(Loc.T("Everything in the open notch grows or shrinks together. The closed notch keeps its own size"), top: 2);
    }

    /// <summary>
    /// Closed: the style as three little pictures (Notch, Capsule, Line), then its size as sliders that move the real
    /// notch while you drag (it glides there on its own springs), and Reset.
    /// </summary>
    void ClosedSection()
    {
        var head = new Grid { Margin = new Thickness(2, 24, 0, 10), Tag = Loc.T("Closed") };
        head.Children.Add(new TextBlock { Text = Loc.T("Closed"), FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Text, VerticalAlignment = VerticalAlignment.Center });
        var reset = PillButton("\uE777", Loc.T("Reset"), () => { _notch.ResetShape(); Redraw(); Saved(); }, primary: false);
        reset.Height = 28;
        reset.Padding = new Thickness(10, 0, 10, 0);
        reset.HorizontalAlignment = HorizontalAlignment.Right;
        head.Children.Add(reset);
        _content.Children.Add(head);

        string style = _notch.ClosedStyleNow;
        var cards = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, -8, 0) };
        foreach (var (key, name) in new[] { ("notch", "Notch"), ("capsule", "Capsule"), ("line", "Line") })
        {
            bool on = style == key;
            var stage = new Grid { Height = 56, Background = Frozen(Color.FromRgb(0x1F, 0x1F, 0x23)), ClipToBounds = true };
            FrameworkElement shape = key switch
            {
                "notch" => new Border { Width = 70, Height = 18, CornerRadius = new CornerRadius(0, 0, 9, 9), Background = Brushes.Black, VerticalAlignment = VerticalAlignment.Top },
                "capsule" => new Border { Width = 70, Height = 16, CornerRadius = new CornerRadius(8), Background = Brushes.Black, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 8, 0, 0) },
                _ => new Border { Width = 70, Height = 3, CornerRadius = new CornerRadius(1.5), Background = Frozen(Color.FromRgb(0xE8, 0xE8, 0xEA)), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 0, 0) },
            };
            shape.HorizontalAlignment = HorizontalAlignment.Center;
            stage.Children.Add(shape);
            string k = key;
            cards.Children.Add(PickCard(Stage(stage), Loc.T(name), on, () => { _notch.SetClosedStyle(k); Redraw(); Saved(); }));
        }
        _content.Children.Add(cards);

        // the sizes of the style in use, each a slider that moves the notch live
        var rows = new StackPanel();
        void Slider(string label, string key, (double Min, double Max) range, double value, string unit, double step)
        {
            var g = new Grid { Height = 48, Margin = new Thickness(16, 0, 16, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(176) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
            g.Children.Add(new TextBlock { Text = Loc.T(label), FontSize = 13.5, Foreground = Text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            var shown = new TextBlock { FontSize = 12.5, Foreground = Dim, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            System.Windows.Documents.Typography.SetNumeralAlignment(shown, FontNumeralAlignment.Tabular);
            string Fmt(double v) => key == "x"
                ? (Math.Abs(v) < 0.005 ? Loc.T("Centre") : string.Format(Loc.T(v < 0 ? "{0:0}% left" : "{0:0}% right"), Math.Abs(v) * 100))
                : (step < 1 ? v.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture) : v.ToString("0", System.Globalization.CultureInfo.CurrentCulture)) + " " + unit;
            shown.Text = Fmt(value);
            var slider = new PillSlider { Height = 20, TrackHeight = 5, VerticalAlignment = VerticalAlignment.Center, FlowDirection = FlowDirection.LeftToRight, Margin = new Thickness(0, 0, 12, 0), FillBrush = Accent };
            slider.Value = (value - range.Min) / (range.Max - range.Min);
            double At(double f) { double v = range.Min + Math.Clamp(f, 0, 1) * (range.Max - range.Min); return Math.Round(v / step) * step; }
            slider.ValueChanged += f => { double v = At(f); shown.Text = Fmt(v); _notch.SetShapeValue(key, v, save: false); };
            slider.ValueCommitted += f => { _notch.SetShapeValue(key, At(f), save: true); Saved(); };
            Grid.SetColumn(slider, 1);
            g.Children.Add(slider);
            Grid.SetColumn(shown, 2);
            g.Children.Add(shown);
            if (rows.Children.Count > 0) rows.Children.Add(Divider(16));
            rows.Children.Add(g);
        }
        var preset = NotchWindow.NotchSizes.FirstOrDefault(n => n.Key == _s.NotchSize);
        if (preset.Key == null) preset = NotchWindow.NotchSizes[1];
        if (style == "line")
        {
            Slider("Length", "length", NotchWindow.LineLengthRange, _s.LineLength, "px", 1);
            Slider("Thickness", "thickness", NotchWindow.LineThickRange, _s.LineThickness, "px", 0.5);
            Slider("Distance from the edge", "gap", NotchWindow.GapRange, _s.LineGap, "px", 1);
        }
        else
        {
            Slider("Width", "width", NotchWindow.WidthRange, _s.NotchWidth > 0 ? _s.NotchWidth : preset.W, "px", 1);
            Slider("Height", "height", NotchWindow.HeightRange, _s.NotchHeight > 0 ? _s.NotchHeight : preset.H, "px", 1);
            if (style == "capsule") Slider("Distance from the edge", "gap", NotchWindow.GapRange, _s.EdgeGap, "px", 1);
        }
        Slider("Position", "x", (-1, 1), _s.PosX, "", 0.01);
        _content.Children.Add(CardOf(rows));

        if (style == "line")
        {
            // the line with music: it fills as the song plays
            Card(new[] { new Row(Sec.Appearance, "\uE8D6", "Song on the line", "With music playing, the line fills from the left as the song goes, in its colour", () => _s.LineProgress, v => _notch.PrefSetLineProgress(v)) });
            return;
        }
        // beside the face: your choice of parts, in your order (left, right, left…; more than two take turns)
        Label(Loc.T("Beside the face"));
        var list = new StackPanel();
        var parts = _notch.PrefClosedParts;
        for (int k = 0; k < parts.Count; k++)
        {
            var (key, name, _) = parts[k];
            var (glyph, desc) = key switch
            {
                "clock" => ("\uE823", "The time"),
                "date" => ("\uE787", "The day and the date"),
                "battery" => ("\uE83F", "How much is left (only on a laptop)"),
                "weather" => ("\uE706", "The temperature where you set it on the Weather page"),
                "cpu" => ("\uE950", "How busy the processor is"),
                _ => ("\uE9D9", "How much of Claude's 5-hour session you've used"),
            };
            string kk = key;
            var line = new Grid();
            line.ColumnDefinitions.Add(new ColumnDefinition());
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.Children.Add(RowView(new Row(Sec.Appearance, glyph, name, desc, () => _s.ClosedParts.Contains(kk), v => _notch.PrefSetPart(kk, v))));
            line.Children.Add(OrderArrows(k, parts.Count, d => { _notch.PrefMovePart(kk, d); Redraw(); Saved(); }));
            if (k > 0) list.Children.Add(Divider(56));
            list.Children.Add(line);
        }
        _content.Children.Add(CardOf(list));
    }

    void Shortcuts()
    {
        var list = new StackPanel();
        var items = new (string[] Keys, string What)[]
        {
            (new[] { "Win", "Alt", "N" }, "Open or close the control center"),
            (new[] { "Shift", "Scroll" }, "Change page"),
            (new[] { "Two-finger swipe" }, "Change page on a touchpad"),
            (new[] { "Esc" }, "Close the control center"),
            (new[] { "Drag a file", "Notch" }, "Keep it on the shelf"),
            (new[] { "Swipe down" }, "Open it with a finger (touch screens)"),
            (new[] { "Click", "Tray icon" }, "Unlock a locked notch"),
            (new[] { "Right-click", "Tray icon" }, "This window"),
        };
        for (int i = 0; i < items.Length; i++)
        {
            if (i > 0) list.Children.Add(Divider(16));
            var g = new Grid { Margin = new Thickness(16, 12, 16, 12) };
            g.Children.Add(new TextBlock { Text = Loc.T(items[i].What), FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 160, 0), TextWrapping = TextWrapping.Wrap });
            var keys = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, FlowDirection = FlowDirection.LeftToRight };
            for (int k = 0; k < items[i].Keys.Length; k++)
            {
                if (k > 0) keys.Children.Add(new TextBlock { Text = "+", Foreground = Muted, Margin = new Thickness(5, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
                keys.Children.Add(Keycap(Loc.T(items[i].Keys[k])));
            }
            g.Children.Add(keys);
            list.Children.Add(g);
        }
        _content.Children.Add(CardOf(list));
    }

    /// <summary>Up and down for a row in an ordered list (the first can't go up, the last can't go down).</summary>
    FrameworkElement OrderArrows(int index, int count, Action<int> move)
    {
        var arrows = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        arrows.Children.Add(IconButton("\uE70E", "Earlier", () => move(-1), index > 0));
        arrows.Children.Add(IconButton("\uE70D", "Later", () => move(1), index < count - 1));
        Grid.SetColumn(arrows, 1);
        return arrows;
    }

    static FrameworkElement Keycap(string text) => new Border
    {
        MinWidth = 26, Height = 26, Padding = new Thickness(8, 0, 8, 1), CornerRadius = new CornerRadius(6),
        Background = Frozen(Color.FromRgb(0x2A, 0x2A, 0x2F)), BorderBrush = Frozen(Color.FromRgb(0x0C, 0x0C, 0x0E)), BorderThickness = new Thickness(1, 1, 1, 2),
        Child = new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center },
    };

    void About()
    {
        var hero = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 28, 0, 28) };
        var mark = PixoLogo(76);
        mark.HorizontalAlignment = HorizontalAlignment.Center;
        hero.Children.Add(mark);
        hero.Children.Add(new TextBlock { Text = "Pixo", FontSize = 24, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0), FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI") });
        hero.Children.Add(new TextBlock { Text = Loc.T("Version") + " " + Version, Foreground = Dim, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) });
        hero.Children.Add(new TextBlock { Text = Loc.T("A living island at the top of your screen."), Foreground = Dim, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) });
        _content.Children.Add(CardOf(hero));

        var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        actions.Children.Add(PillButton("", Loc.T("Open the settings folder"), () => Launch(Settings.Folder), primary: false));
        actions.Children.Add(PillButton("", Loc.T("Releases on GitHub"), () => Launch("https://github.com/Prism99-collab/Pixo/releases"), primary: false));
        var reset = PillButton("", Loc.T("Reset all preferences"), () =>
        {
            if (MessageBox.Show(this, Loc.T("Every preference goes back to how it was at install, and the notch restarts."), Loc.T("Reset all preferences"),
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            var fresh = new Settings { Welcomed = true };
            fresh.SaveAndSeal(); // (written now, and nothing from this session lands over it as the notch restarts)
            AppLog.Write("preferences reset by user");
            ((App)Application.Current).Restart();
        }, primary: false);
        reset.Foreground = Danger;
        actions.Children.Add(reset);
        foreach (FrameworkElement a in actions.Children) a.Margin = new Thickness(0, 0, 8, 8);
        _content.Children.Add(actions);
    }

    static void Launch(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }

    // ───────────── scrolling: a slim pill of a scrollbar, and the wheel glides ─────────────

    /// <summary>
    /// A thin rounded thumb floating on nothing: barely there at rest, it widens and brightens under the pointer and
    /// takes the accent colour while you drag it.
    /// </summary>
    Style ScrollStyle()
    {
        var a = AccentColor;
        string accent = $"#{a.R:X2}{a.G:X2}{a.B:X2}";
        string xaml = $@"
<Style TargetType='ScrollBar' xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Setter Property='Width' Value='12' />
  <Setter Property='MinWidth' Value='12' />
  <Setter Property='Template'>
    <Setter.Value>
      <ControlTemplate TargetType='ScrollBar'>
        <Grid Background='Transparent'>
          <Track x:Name='PART_Track' IsDirectionReversed='True'>
            <Track.Thumb>
              <Thumb>
                <Thumb.Template>
                  <ControlTemplate TargetType='Thumb'>
                    <Border x:Name='T' Width='4' CornerRadius='2' Margin='0,6,3,6' HorizontalAlignment='Right' Background='#38FFFFFF' />
                    <ControlTemplate.Triggers>
                      <Trigger Property='IsMouseOver' Value='True'>
                        <Setter TargetName='T' Property='Width' Value='7' />
                        <Setter TargetName='T' Property='CornerRadius' Value='3.5' />
                        <Setter TargetName='T' Property='Background' Value='#80FFFFFF' />
                      </Trigger>
                      <Trigger Property='IsDragging' Value='True'>
                        <Setter TargetName='T' Property='Width' Value='7' />
                        <Setter TargetName='T' Property='CornerRadius' Value='3.5' />
                        <Setter TargetName='T' Property='Background' Value='{accent}' />
                      </Trigger>
                    </ControlTemplate.Triggers>
                  </ControlTemplate>
                </Thumb.Template>
              </Thumb>
            </Track.Thumb>
          </Track>
        </Grid>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";
        return (Style)System.Windows.Markup.XamlReader.Parse(xaml);
    }

    static readonly DependencyProperty SmoothOffsetProperty = DependencyProperty.Register("SmoothOffset", typeof(double), typeof(PreferencesWindow),
        new PropertyMetadata(0.0, (d, e) => ((PreferencesWindow)d)._scroll.ScrollToVerticalOffset((double)e.NewValue)));
    double _scrollTarget;
    bool _scrollAnimating;

    /// <summary>The wheel glides to where it's going (sliders and the colour wheel keep their own wheel).</summary>
    void SmoothWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.OriginalSource is DependencyObject src && (FindUp<PillSlider>(src) || FindUp<ColorWheel>(src))) return;
        if (e.OriginalSource is Visual v && PresentationSource.FromVisual(v) != PresentationSource.FromVisual(this)) return; // (a menu's own list)
        e.Handled = true;
        if (!_scrollAnimating) _scrollTarget = _scroll.VerticalOffset;
        _scrollTarget = Math.Clamp(_scrollTarget - e.Delta * 0.9, 0, _scroll.ScrollableHeight);
        var glide = new DoubleAnimation(_scroll.VerticalOffset, _scrollTarget, TimeSpan.FromMilliseconds(280)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        glide.Completed += (_, _) => _scrollAnimating = false;
        _scrollAnimating = true;
        BeginAnimation(SmoothOffsetProperty, glide);
    }

    static bool FindUp<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null) { if (d is T) return true; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d); }
        return false;
    }

    // ───────────── small pieces ─────────────

    ControlTemplate SearchTemplate()
    {
        var t = new ControlTemplate(typeof(TextBox));
        var bd = new FrameworkElementFactory(typeof(Border), "Bd");
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        bd.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(BorderBrushProperty));
        bd.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(BorderThicknessProperty));
        var grid = new FrameworkElementFactory(typeof(Grid));
        var icon = new FrameworkElementFactory(typeof(TextBlock));
        icon.SetValue(TextBlock.TextProperty, "\uE721");
        icon.SetValue(TextBlock.FontFamilyProperty, IconFont);
        icon.SetValue(TextBlock.FontSizeProperty, 12.0);
        icon.SetValue(TextBlock.ForegroundProperty, Muted);
        icon.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        icon.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Left);
        icon.SetValue(MarginProperty, new Thickness(11, 1, 0, 0));
        icon.SetValue(IsHitTestVisibleProperty, false);
        icon.SetValue(FlowDirectionProperty, FlowDirection.LeftToRight);
        grid.AppendChild(icon);
        var hint = new FrameworkElementFactory(typeof(TextBlock), "Hint");
        hint.SetValue(TextBlock.TextProperty, Loc.T("Search settings"));
        hint.SetValue(TextBlock.ForegroundProperty, Muted);
        hint.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        hint.SetValue(MarginProperty, new Thickness(32, 0, 0, 0));
        hint.SetValue(IsHitTestVisibleProperty, false);
        hint.SetValue(VisibilityProperty, Visibility.Collapsed);
        grid.AppendChild(hint);
        var key = new FrameworkElementFactory(typeof(TextBlock), "Key");
        key.SetValue(TextBlock.TextProperty, "Ctrl+F");
        key.SetValue(TextBlock.FontSizeProperty, 11.0);
        key.SetValue(TextBlock.ForegroundProperty, Muted);
        key.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        key.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Right);
        key.SetValue(MarginProperty, new Thickness(0, 0, 10, 0));
        key.SetValue(IsHitTestVisibleProperty, false);
        key.SetValue(VisibilityProperty, Visibility.Collapsed);
        key.SetValue(FlowDirectionProperty, FlowDirection.LeftToRight);
        grid.AppendChild(key);
        var host = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        host.SetValue(MarginProperty, new Thickness(29, 0, 8, 0));
        host.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        host.SetValue(FocusableProperty, false);
        grid.AppendChild(host);
        bd.AppendChild(grid);
        t.VisualTree = bd;
        // the hint and the key only while it's empty
        var empty = new Trigger { Property = TextBox.TextProperty, Value = "" };
        empty.Setters.Add(new Setter(VisibilityProperty, Visibility.Visible, "Hint"));
        empty.Setters.Add(new Setter(VisibilityProperty, Visibility.Visible, "Key"));
        t.Triggers.Add(empty);
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BorderBrushProperty, EdgeHover, "Bd"));
        t.Triggers.Add(hover);
        var focused = new Trigger { Property = TextBox.IsKeyboardFocusedProperty, Value = true };
        focused.Setters.Add(new Setter(Border.BorderBrushProperty, Accent, "Bd"));
        focused.Setters.Add(new Setter(VisibilityProperty, Visibility.Collapsed, "Key"));
        t.Triggers.Add(focused);
        return t;
    }

    static Color? Hex(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        try { return (Color)ColorConverter.ConvertFromString(s); } catch { return null; }
    }

}
