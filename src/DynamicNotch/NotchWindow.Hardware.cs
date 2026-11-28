using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// The Hardware page (the System page's place, second after Tools in the old order): your PC on one sheet, every
/// reading in a row, each second.
///  • left, one row per part: CPU (a bar for every core, the clock, the heat, the load), memory (a bar, used of total;
///    resting on it shows the cache and the commit), GPU (its name, heat, load), network (down, up, this PC's local
///    address), battery (charging, time left, health, the draw) and the connection's ping;
///  • right, the apps doing the most, by CPU or by memory (click one, then click again to end it), and the drives.
/// Readings come off the UI thread once a second (two on battery saver) and only while the page is on screen; rows are
/// made once and only their numbers and bars change, so nothing on the page is rebuilt while it runs.
/// </summary>
public partial class NotchWindow
{
    sealed class HwRow
    {
        public required Border Root;
        public required TextBlock Detail, Value;
        public required Grid Extra;       // what sits before the detail (the core bars, a level bar, a chip)
    }

    Grid? _hwPage;
    string? _hwBuiltFor;
    readonly DispatcherTimer _hwTimer = new(DispatcherPriority.Background);
    readonly HardwareInfo _hw = new();
    readonly AppLoad _appLoad = new();
    StrainService? _hwStrain;
    bool _hwBusy, _hwByMemory;
    int _hwTick;
    HwRow? _hwCpu, _hwRam, _hwGpu, _hwNet, _hwBat, _hwPing;
    Grid? _hwCores;
    Rectangle? _hwRamFill;
    StackPanel? _hwApps, _hwDrives;
    Border? _hwCpuTab, _hwMemTab;
    SystemSnapshot? _hwSnap;
    StrainService.Reading? _hwRead;
    HardwareInfo.Live? _hwLive;
    List<AppLoad.App> _hwAppList = new();
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, ImageSource?> _hwIcons = new(StringComparer.OrdinalIgnoreCase);
    BatteryDetails? _hwBattery;
    ConnectionDetails? _hwConn;
    string? _hwArmed;            // the app a first click picked: a second click ends it
    DateTime _hwArmedAt;

    Color HwInk => Skin.Lit;
    Color HwSoft => Skin.Label;
    Color HwLine => Skin.Line;
    Color HwAcc => _cyber ? CyCyan : Skin.Accent;
    Color HwWarm => MinimalMapIf(_cyber ? CyYellow : Color.FromRgb(0xFF, 0x9F, 0x0A));
    Color HwHot => MinimalMapIf(_cyber ? CyRed : Color.FromRgb(0xFF, 0x6B, 0x60));
    Color HwGood => MinimalMapIf(_cyber ? Color.FromRgb(0x00, 0xFF, 0xA3) : Color.FromRgb(0x30, 0xD1, 0x58));

    /// <summary>Neon: a frame with one corner cut (and optionally the bottom-left), following the element's size.</summary>
    static void NeonCorner(FrameworkElement el, double cut, double cutBL = 0)
    {
        el.SizeChanged += (_, e) => el.Clip = Controls.NeonFrame.Outline(e.NewSize.Width, e.NewSize.Height, 0, cut, 0, cutBL, 0);
    }

    Grid BuildHardwarePage()
    {
        _hwPage = new Grid { Width = PageWidth, Height = 216 };
        _hwTimer.Tick += async (_, _) => await SampleHardwareAsync();
        RebuildHardwarePage();
        return _hwPage;
    }

    void OnHardwareShown()
    {
        if (_hwBuiltFor != Skin.Key + "|" + _accent) RebuildHardwarePage();
        _hwTimer.Interval = TimeSpan.FromSeconds(Eco ? 2 : 1);
        _hwTimer.Start();
        _ = SampleHardwareAsync();
    }

    void HardwareHidden()
    {
        _hwTimer.Stop();
        _hwArmed = null;
        if (_hwStrain != null && !_hwBusy) { _hwStrain.Dispose(); _hwStrain = null; } // (its per-app GPU query stops with the page)
    }

    Border HwCard(UIElement child, Thickness pad) => new()
    {
        Child = child, Padding = pad, CornerRadius = new CornerRadius(_cyber ? 0 : 12), BorderThickness = new Thickness(1),
        Background = Frozen(_cyber ? Color.FromArgb(0xA6, 0x02, 0x08, 0x0C) : A(HwInk, 0x08)), BorderBrush = Frozen(_cyber ? A(CyCyan, 0x50) : HwLine),
    };

    TextBlock HwText(string text, double size, Color c, bool mono = false, FontWeight? weight = null)
    {
        var t = new TextBlock
        {
            Text = text, FontSize = size, Foreground = Frozen(c), FontFamily = ThemeText, FontWeight = weight ?? FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        if (mono) System.Windows.Documents.Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular); // (figures keep their width as they change)
        return t;
    }

    void RebuildHardwarePage()
    {
        if (_hwPage == null) return;
        _hwBuiltFor = Skin.Key + "|" + _accent;
        _hwPage.Children.Clear();
        var root = new Grid { Margin = new Thickness(12, 2, 12, 8) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(268) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        root.ColumnDefinitions.Add(new ColumnDefinition());

        // left: the parts
        var rows = new StackPanel();
        _hwCpu = HwRowMake("", "CPU");
        _hwCores = new Grid { Height = 10, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        _hwCpu.Extra.Children.Add(_hwCores);
        _hwRam = HwRowMake("", Cap("Memory"));
        _hwRamFill = new Rectangle { Fill = Frozen(HwAcc), RadiusX = 1.5, RadiusY = 1.5, HorizontalAlignment = HorizontalAlignment.Left, RenderTransform = new ScaleTransform(0, 1) };
        _hwRam.Extra.Children.Add(new Grid
        {
            Width = 28, Height = 3, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
            Children = { new Rectangle { Fill = Frozen(A(HwInk, 0x1C)), RadiusX = 1.5, RadiusY = 1.5 }, _hwRamFill },
        });
        _hwRamFill.Width = 28;
        _hwGpu = HwRowMake("", "GPU");
        _hwNet = HwRowMake("", Cap("Network"));
        _hwBat = HwRowMake("", Cap("Battery"));
        _hwPing = HwRowMake("", "Ping");
        // the network row: its tooltip says this PC's local address, a click copies it
        _hwNet.Root.Cursor = Cursors.Hand;
        _hwNet.Root.MouseLeftButtonUp += (_, e) =>
        {
            if (_hwLive?.LocalIp is not { } ip) return;
            e.Handled = true;
            PickCopy(ip);
            ShowOsd("copied-ip", "\uE8C8", null, Loc.T("Copied"), ip, null, seconds: 1.2, prio: Prio.High);
        };
        var all = new[] { _hwCpu, _hwRam, _hwGpu, _hwNet, _hwBat, _hwPing };
        foreach (var r in all) rows.Children.Add(r.Root);
        all[^1].Root.BorderThickness = new Thickness(0); // (no line under the last row)
        root.Children.Add(HwCard(rows, new Thickness(12, 2, 12, 2)));

        // right: the apps, then the drives
        var right = new Grid();
        right.RowDefinitions.Add(new RowDefinition());
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var apps = new StackPanel();
        var head = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        head.Children.Add(HwText(Cap(Loc.T("Apps")), _cyber ? 8.5 : 11, HwInk, weight: FontWeights.Medium));
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _hwCpuTab = HwTab("CPU", false);
        _hwMemTab = HwTab(Loc.T("Memory"), true);
        tabs.Children.Add(_hwCpuTab);
        tabs.Children.Add(_hwMemTab);
        head.Children.Add(tabs);
        apps.Children.Add(head);
        _hwApps = new StackPanel();
        apps.Children.Add(_hwApps);
        right.Children.Add(HwCard(apps, new Thickness(10, 8, 10, 6)));
        _hwDrives = new StackPanel();
        var drives = HwCard(_hwDrives, new Thickness(10, 6, 10, 6));
        drives.Margin = new Thickness(0, 6, 0, 0);
        Grid.SetRow(drives, 1);
        right.Children.Add(drives);
        Grid.SetColumn(right, 2);
        root.Children.Add(right);
        _hwPage.Children.Add(root);
        HwTabs();
        RenderHardware();
    }

    HwRow HwRowMake(string glyph, string label)
    {
        var g = new Grid { Height = 31 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 11, Foreground = Frozen(HwSoft), Width = 20, VerticalAlignment = VerticalAlignment.Center });
        name.Children.Add(HwText(label, _cyber ? 9 : 11.5, HwInk, weight: FontWeights.Medium));
        g.Children.Add(name);
        var extra = new Grid { VerticalAlignment = VerticalAlignment.Center };
        var detail = HwText("", 10.5, HwSoft);
        var dock = new Grid { VerticalAlignment = VerticalAlignment.Center, ClipToBounds = true };
        dock.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        dock.ColumnDefinitions.Add(new ColumnDefinition());
        dock.Children.Add(extra);
        Grid.SetColumn(detail, 1);
        dock.Children.Add(detail);
        Grid.SetColumn(dock, 1);
        g.Children.Add(dock);
        var value = HwText("–", 12, HwInk, mono: true, weight: FontWeights.Medium);
        value.Margin = new Thickness(10, 0, 0, 0);
        value.TextAlignment = TextAlignment.Right;
        value.MinWidth = 38;
        Grid.SetColumn(value, 2);
        g.Children.Add(value);
        var root = new Border { Child = g, BorderBrush = Frozen(A(HwInk, 0x0A)), BorderThickness = new Thickness(0, 0, 0, 1), Background = Brushes.Transparent };
        return new HwRow { Root = root, Detail = detail, Value = value, Extra = extra };
    }

    Border HwTab(string text, bool memory)
    {
        var t = HwText(text, 10, HwSoft, weight: FontWeights.Medium);
        var b = new Border { Child = t, Padding = new Thickness(7, 1, 7, 2), CornerRadius = new CornerRadius(_cyber ? 0 : 6), Cursor = Cursors.Hand, Background = Brushes.Transparent, Margin = new Thickness(2, 0, 0, 0) };
        b.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (_hwByMemory == memory) return;
            _hwByMemory = memory;
            _hwArmed = null;
            FeelSound(Services.Sfx.Cue.Tap);
            HwTabs();
            HwRenderApps();
        };
        return b;
    }

    void HwTabs()
    {
        foreach (var (tab, on) in new[] { (_hwCpuTab, !_hwByMemory), (_hwMemTab, _hwByMemory) })
        {
            if (tab == null) continue;
            tab.Background = on ? Frozen(A(HwInk, 0x16)) : Brushes.Transparent;
            ((TextBlock)tab.Child).Foreground = Frozen(on ? HwInk : HwSoft);
        }
    }

    // ───────────── reading ─────────────

    async Task SampleHardwareAsync()
    {
        if (_page != 1 || _mode != Mode.Expanded) { HardwareHidden(); return; }
        if (_hwBusy) return;
        _hwBusy = true;
        try
        {
            int tick = _hwTick++;
            _hwStrain ??= new StrainService();
            var strain = _hwStrain;
            bool appsTick = tick % 2 == 0;
            var readTask = Task.Run(() => { try { return (StrainService.Reading?)strain.Sample(perApp: appsTick); } catch { return null; } });
            var liveTask = Task.Run(() => { try { _ = _hw.Names; return (HardwareInfo.Live?)_hw.Sample(); } catch { return null; } });
            var snapTask = _monitor.SampleAsync();
            Task<List<AppLoad.App>>? appsTask = appsTick ? readTask.ContinueWith(_ =>
            {
                List<AppLoad.App> list;
                try { list = _appLoad.Sample(strain.GpuByProcess); } catch { return new List<AppLoad.App>(); }
                // the icons of the apps that could show, read here (a shell icon takes up to 100 ms: never on the UI thread)
                foreach (var a in list.OrderByDescending(x => x.Cpu).Take(6).Concat(list.OrderByDescending(x => x.MemoryMb).Take(6)))
                    if (a.Path != null && !_hwIcons.ContainsKey(a.Path))
                    {
                        ImageSource? ic = null;
                        try { ic = IconHelper.ForPath(a.Path); if (ic is { CanFreeze: true, IsFrozen: false }) ic.Freeze(); } catch { }
                        _hwIcons[a.Path] = ic is { IsFrozen: true } ? ic : null;
                    }
                return list;
            }, TaskScheduler.Default) : null;
            Task<BatteryDetails>? batTask = tick % 3 == 0 ? _monitor.BatteryAsync() : null;
            Task<ConnectionDetails>? connTask = tick % 5 == 0 ? _monitor.ConnectionAsync() : null;
            if (await snapTask is { } s) _hwSnap = s;
            if (await readTask is { } r) _hwRead = r;
            if (await liveTask is { } l) _hwLive = l;
            if (appsTask != null) _hwAppList = await appsTask;
            if (batTask != null) _hwBattery = await batTask;
            if (_page != 1 || _mode != Mode.Expanded) return;
            RenderHardware();
            if (appsTask != null) HwRenderApps();
            if (connTask != null) { _hwConn = await connTask; if (_page == 1) HwRenderPing(); }
        }
        catch (Exception ex) { AppLog.Write("hardware page: " + ex.Message); }
        finally { _hwBusy = false; }
    }

    // ───────────── painting (in place: only numbers and bars change) ─────────────

    readonly Dictionary<Color, Brush> _hwBrushes = new();
    Brush HwB(Color c)
    {
        if (_hwBrushes.TryGetValue(c, out var b)) return b;
        if (_hwBrushes.Count > 64) _hwBrushes.Clear(); // (a theme change: the old colours go)
        return _hwBrushes[c] = Frozen(c);
    }

    Color HwLevel(double v) => v >= 0.9 ? HwHot : v >= 0.75 ? HwWarm : HwInk;
    Color HwHeat(double? t, double warm, double hot) => t is not { } c ? HwSoft : c >= hot ? HwHot : c >= warm ? HwWarm : HwSoft;

    void RenderHardware()
    {
        if (_hwCpu == null || _hwRam == null || _hwGpu == null || _hwNet == null || _hwBat == null) return;
        var s = _hwSnap;
        var r = _hwRead;
        var l = _hwLive;
        var chip = _hw.Names;
        string ghz = l?.Ghz is { } g ? $"{g:0.0} GHz" : "";
        string temp = r?.CpuTemp is { } ct ? $"{ct:0}°" : "";
        _hwCpu.Detail.Text = temp.Length > 0 ? temp : ghz;
        _hwCpu.Detail.Foreground = HwB(HwHeat(r?.CpuTemp, 80, 92));
        _hwCpu.Root.ToolTip = $"{chip.Cpu}\n{chip.Cores} " + Loc.T("cores") + $", {chip.Threads} " + Loc.T("threads") + (ghz.Length > 0 ? " · " + ghz : "");
        if (s != null) { _hwCpu.Value.Text = $"{s.Cpu * 100:0}%"; _hwCpu.Value.Foreground = HwB(HwLevel(s.Cpu)); }
        HwCoreBars(l?.Cores);

        if (s != null)
        {
            double used = s.MemUsed / 1073741824.0, total = s.MemTotal / 1073741824.0;
            _hwRam.Detail.Text = $"{used:0.0} / {total:0} GB";
            _hwRam.Value.Text = $"{s.Memory * 100:0}%";
            _hwRam.Value.Foreground = HwB(HwLevel(s.Memory));
            HwBar(_hwRamFill, s.Memory);
            if (l != null) _hwRam.Root.ToolTip = string.Format(Loc.T("Cache {0:0.0} GB · commit {1:0.0} of {2:0.0} GB"), l.CacheGb, l.CommitGb, l.CommitLimitGb);
        }

        bool gpuKnown = r?.GpuKnown != false && _hwStrain?.GpuAvailable != false;
        _hwGpu.Root.Visibility = gpuKnown ? Visibility.Visible : Visibility.Collapsed;
        string gtemp = r?.GpuTemp is { } gt ? $" · {gt:0}°" : "";
        _hwGpu.Detail.Text = (chip.GpuShort ?? "") + gtemp;
        _hwGpu.Detail.Foreground = HwB(HwHeat(r?.GpuTemp, 78, 88));
        _hwGpu.Root.ToolTip = chip.Gpu;
        if (r != null) { _hwGpu.Value.Text = $"{r.Gpu:0}%"; _hwGpu.Value.Foreground = HwB(HwLevel(r.Gpu / 100)); }

        if (s != null)
        {
            _hwNet.Detail.Text = $"↓ {SystemMonitor.Bytes(s.DownBps)}/s   ↑ {SystemMonitor.Bytes(s.UpBps)}/s";
            _hwNet.Value.Text = SystemMonitor.Bytes(s.DownBps + s.UpBps) + "/s";
        }
        _hwNet.Root.ToolTip = l?.LocalIp is { } ip ? string.Format(Loc.T("This PC on your network: {0} · click to copy"), ip) : null;

        HwRenderBattery();
        if (_hwDrives != null && l != null) HwRenderDrives(l.Drives);
    }

    void HwBar(Rectangle? fill, double v)
    {
        if (fill?.RenderTransform is not ScaleTransform st) return;
        v = Math.Clamp(v, 0, 1);
        fill.Fill = HwB(v >= 0.9 ? HwHot : HwAcc);
        if (Eco) { st.BeginAnimation(ScaleTransform.ScaleXProperty, null); st.ScaleX = v; return; }
        st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(v, TimeSpan.FromMilliseconds(320)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    /// <summary>One thin bar per core (up to 32), its height the core's load: the CPU row's own small picture.</summary>
    void HwCoreBars(double[]? cores)
    {
        if (_hwCores == null || cores == null || cores.Length == 0) return;
        int n = Math.Min(cores.Length, 12); // (12 bars say it; more only crowd the row)
        if (_hwCores.Children.Count != n * 2)
        {
            _hwCores.Children.Clear();
            _hwCores.ColumnDefinitions.Clear();
            double w = 2;
            _hwCores.Width = n * (w + 1) - 1;
            for (int i = 0; i < n; i++)
            {
                _hwCores.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w + (i < n - 1 ? 1 : 0)) });
                var bar = new Rectangle { Width = w, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Height = 12, RenderTransformOrigin = new Point(0.5, 1), RenderTransform = new ScaleTransform(1, 0.08) };
                Grid.SetColumn(bar, i);
                _hwCores.Children.Add(new Rectangle { Width = w, HorizontalAlignment = HorizontalAlignment.Left, Fill = HwB(A(HwInk, 0x14)) }.Also(b => Grid.SetColumn(b, i)));
                _hwCores.Children.Add(bar);
            }
        }
        var ink = HwB(A(HwInk, 0xD0));
        for (int i = 0; i < n; i++)
        {
            if (_hwCores.Children[i * 2 + 1] is not Rectangle bar || bar.RenderTransform is not ScaleTransform st) continue;
            int from = i * cores.Length / n, to = Math.Max(from + 1, (i + 1) * cores.Length / n);
            double load = 0;
            for (int k = from; k < to; k++) load = Math.Max(load, cores[k]);
            double v = Math.Max(0.08, load);
            bar.Fill = load >= 0.9 ? HwB(HwHot) : ink;
            st.ScaleY = v; // (no tween: a second apart, the bars read as a live meter)
        }
    }

    void HwRenderBattery()
    {
        if (_hwBat == null) return;
        var b = _hwBattery;
        if (!_power.HasBattery || b is { Present: false })
        {
            _hwBat.Detail.Text = Loc.T("Plugged in");
            _hwBat.Value.Text = "AC";
            _hwBat.Value.Foreground = HwB(HwSoft);
            return;
        }
        var parts = new List<string>();
        if (b?.Full == true || (_power.PluggedIn && _power.Percent >= 100)) parts.Add(Loc.T("Full"));
        else if (_power.PluggedIn) parts.Add(b?.TimeToFull is { } tf ? string.Format(Loc.T("Full in {0}"), SystemMonitor.Duration(tf)) : Loc.T("Charging"));
        else if (b?.TimeLeft is { } tl) parts.Add(string.Format(Loc.T("{0} left"), SystemMonitor.Duration(tl)));
        if (b?.WattsNow is { } w && Math.Abs(w) >= 0.5 && !_power.PluggedIn) parts.Add($"{Math.Abs(w):0.0} W");
        if (b?.HealthPercent is { } h) parts.Add(string.Format(Loc.T("health {0:0}%"), h));
        _hwBat.Detail.Text = string.Join(" · ", parts);
        _hwBat.Detail.Foreground = HwB(b?.HealthPercent is < 70 ? HwWarm : HwSoft);
        _hwBat.Value.Text = $"{_power.Percent}%";
        _hwBat.Value.Foreground = HwB(_power.Percent <= 20 && !_power.PluggedIn ? HwHot : HwInk); // (colour only when it's low)
    }

    void HwRenderPing()
    {
        if (_hwPing == null) return;
        var c = _hwConn;
        if (c == null) return;
        if (!c.Online) { _hwPing.Detail.Text = Loc.T("Offline"); _hwPing.Value.Text = "–"; _hwPing.Value.Foreground = HwB(HwHot); return; }
        var parts = new List<string> { c.Name };
        if (c.JitterMs is { } j) parts.Add(string.Format(Loc.T("jitter {0} ms"), j));
        if (c.Captive) parts.Add(Loc.T("sign-in needed"));
        _hwPing.Detail.Text = string.Join(" · ", parts.Where(p => p.Length > 0));
        long? ms = c.TypicalMs ?? c.PingMs;
        _hwPing.Value.Text = ms is { } m ? $"{m} ms" : "–";
        _hwPing.Value.Foreground = HwB(ms is > 150 ? HwHot : ms is > 80 ? HwWarm : HwInk);
    }

    void HwRenderApps()
    {
        if (_hwApps == null) return;
        int rows = _hwLive?.Drives.Count > 2 ? 4 : 5;
        var list = (_hwByMemory ? _hwAppList.Where(a => a.MemoryMb > 0).OrderByDescending(a => a.MemoryMb) : _hwAppList.Where(a => a.Cpu >= 0.1).OrderByDescending(a => a.Cpu)).Take(rows).ToList();
        if (_hwArmed != null && (DateTime.UtcNow - _hwArmedAt).TotalSeconds > 4) _hwArmed = null;
        _hwApps.Children.Clear();
        foreach (var a in list)
        {
            var g = new Grid { Height = 22, Background = Brushes.Transparent, Cursor = a.CanEnd ? Cursors.Hand : null, ToolTip = a.Count > 1 ? string.Format(Loc.T("{0} · {1} processes"), a.Display, a.Count) : a.Display };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            if (a.Path != null && _hwIcons.TryGetValue(a.Path, out var icon) && icon != null) g.Children.Add(new Image { Source = icon, Width = 13, Height = 13, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center });
            bool armed = _hwArmed == a.Name;
            var name = HwText(armed ? string.Format(Loc.T("End {0}?"), a.Display) : a.Display, 11, armed ? HwHot : HwInk);
            Grid.SetColumn(name, 1);
            g.Children.Add(name);
            string val = _hwByMemory ? (a.MemoryMb >= 1024 ? $"{a.MemoryMb / 1024.0:0.0} GB" : $"{a.MemoryMb} MB") : $"{a.Cpu:0.0}%";
            var v = HwText(armed ? Loc.T("End") : val, 10.5, armed ? HwHot : HwSoft, mono: true);
            v.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(v, 2);
            g.Children.Add(v);
            if (a.CanEnd)
            {
                string key = a.Name;
                g.MouseEnter += (_, _) => g.Background = HwB(A(HwInk, 0x0C));
                g.MouseLeave += (_, _) => g.Background = Brushes.Transparent;
                g.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    if (_hwArmed == key) { _hwArmed = null; bool ok = AppLoad.End(key); AppLog.Write($"hardware page: ended an app ({(ok ? "ok" : "refused")})"); _hwAppList.RemoveAll(x => x.Name == key); }
                    else { _hwArmed = key; _hwArmedAt = DateTime.UtcNow; FeelSound(Services.Sfx.Cue.Tap); }
                    HwRenderApps();
                };
            }
            _hwApps.Children.Add(g);
        }
        if (list.Count == 0) _hwApps.Children.Add(HwText(_hwAppList.Count == 0 ? Loc.T("Reading…") : Loc.T("Nothing busy"), 10.5, HwSoft));
    }

    void HwRenderDrives(IReadOnlyList<HardwareInfo.Drive> drives)
    {
        if (_hwDrives == null) return;
        var shown = drives.Take(3).ToList();
        string key = string.Join("|", shown.Select(d => $"{d.Name}{d.UsedGb:0}"));
        if ((string?)_hwDrives.Tag == key) return;
        _hwDrives.Tag = key;
        _hwDrives.Children.Clear();
        foreach (var d in shown)
        {
            double f = d.TotalGb > 0 ? d.UsedGb / d.TotalGb : 0;
            var g = new Grid { Height = 20, ToolTip = d.Label.Length > 0 ? d.Label : null };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(HwText(d.Name, 11, HwInk, weight: FontWeights.Medium));
            var bar = new Grid { Height = 3, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 8, 0) };
            bar.Children.Add(new Rectangle { Fill = HwB(A(HwInk, 0x1C)), RadiusX = 1.5, RadiusY = 1.5 });
            bar.Children.Add(new Rectangle { Fill = HwB(f >= 0.9 ? HwHot : HwAcc), RadiusX = 1.5, RadiusY = 1.5, HorizontalAlignment = HorizontalAlignment.Stretch, RenderTransform = new ScaleTransform(f, 1) });
            Grid.SetColumn(bar, 1);
            g.Children.Add(bar);
            var t = HwText($"{d.UsedGb:0} / {d.TotalGb:0} GB", 10.5, HwSoft, mono: true);
            Grid.SetColumn(t, 2);
            g.Children.Add(t);
            _hwDrives.Children.Add(g);
        }
    }
}
