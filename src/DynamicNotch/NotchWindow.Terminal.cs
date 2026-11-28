using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// The Terminal page, after Tools: a PowerShell a hover away (Services/ShellSession). Type a command, Enter runs it;
/// each run is kept as a block: the command, what it wrote (errors in red), and on its right a dot and the time it
/// took (green done, red failed, with the exit code), like a run log. ↑ and ↓ bring back earlier commands, Ctrl+L
/// clears, Ctrl+C (nothing selected) stops what's running, Esc gives the keyboard back. The folder and the last
/// commands are remembered; programs that wait for keys go to Windows Terminal (the button opens it right here).
/// Output arrives in batches (every 60 ms while something runs) and each block keeps its last 1,500 lines, so a
/// command that writes a lot never holds up the notch.
/// </summary>
public partial class NotchWindow
{
    const int TerminalPage = 9;
    const int TermKeepLines = 1500, TermKeepBlocks = 40;

    sealed class TermBlock
    {
        public string Cmd = "";
        public readonly List<(string Text, bool Err)> Lines = new();
        public int Dropped;
        public bool Running = true, Failed;
        public int Code;
        public DateTime Started = DateTime.UtcNow;
        public double Secs;
        public StackPanel? Out;
        public TextBlock? Status;
        public Ellipse? Dot;
    }

    ShellSession? _shell;
    Grid? _termPage;
    StackPanel? _termLog;
    ScrollViewer? _termScroll;
    TextBox? _termBox;
    TextBlock? _termDir, _termShell;
    Border? _termStopBtn;
    string? _termBuiltFor;
    readonly List<TermBlock> _termBlocks = new();
    readonly ConcurrentQueue<(string Text, bool Err)> _termIncoming = new();
    int _termQueued, _termDroppedIn; // (lines waiting; lines let go because a command wrote faster than anyone can read)
    const int TermQueueMax = 20_000;
    DispatcherTimer? _termFlush;
    int _termHistAt = -1;
    string _termDraft = "";
    static readonly FontFamily TermMono = new("Cascadia Mono, Cascadia Code, Consolas");
    static readonly Regex TermSecret = new(@"(?i)(password|passwd|token|secret|api[_-]?key|bearer)", RegexOptions.Compiled);

    Color TermInk => Skin.Lit;
    Color TermSoft => Skin.Label;
    Color TermLine => Skin.Line;
    Color TermAcc => _cyber ? CyYellow : Skin.Accent;
    Color TermOk => MinimalMapIf(Color.FromRgb(0x30, 0xD1, 0x58));
    Color TermBad => MinimalMapIf(_cyber ? CyRed : Color.FromRgb(0xFF, 0x6B, 0x60));

    Grid BuildTerminalPage()
    {
        _termPage = new Grid { Width = PageWidth, Height = 216 };
        if (Environment.GetEnvironmentVariable("DYNAMICNOTCH_TERMDEMO") == "1") TermDemo(); // (made-up runs, for a picture of the page)
        RebuildTerminalPage();
        return _termPage;
    }

    void TermDemo()
    {
        var t = DateTime.UtcNow;
        _termBlocks.Add(new TermBlock { Cmd = "git status", Running = false, Secs = 0.21, Started = t, Lines = { ("On branch main", false), ("Your branch is up to date with 'origin/main'.", false), ("nothing to commit, working tree clean", false) } });
        _termBlocks.Add(new TermBlock { Cmd = "npm run build", Running = false, Failed = true, Code = 1, Secs = 4.8, Started = t, Lines = { ("> site@2.3.0 build", false), ("> vite build", false), ("error TS2304: Cannot find name 'Pager'.", true), ("src/app.ts:42:7", true) } });
        _termBlocks.Add(new TermBlock { Cmd = "Get-Date", Running = false, Secs = 0.03, Started = t, Lines = { ("Friday, 27 November 2026 23:58:12", false) } });
    }

    void OnTerminalShown()
    {
        if (_termBuiltFor != Skin.Key + "|" + _accent) RebuildTerminalPage();
        TermScrollEnd();
    }

    void RebuildTerminalPage()
    {
        if (_termPage == null) return;
        _termBuiltFor = Skin.Key + "|" + _accent;
        string keep = _termBox?.Text ?? "";
        _termPage.Children.Clear();
        var root = new Grid { Margin = new Thickness(14, 2, 14, 8) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(22) });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });

        // the head: the shell and the folder on the left, Stop · Clear · Windows Terminal on the right
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _termShell = new TextBlock { Text = Cap(_shell?.ShellName ?? "PowerShell"), FontFamily = ThemeText, FontSize = _cyber ? 9 : 11, FontWeight = FontWeights.Medium, Foreground = Frozen(TermInk), VerticalAlignment = VerticalAlignment.Center };
        head.Children.Add(_termShell);
        _termDir = new TextBlock
        {
            FontFamily = TermMono, FontSize = 10.5, Foreground = Frozen(TermSoft), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0),
            TextTrimming = TextTrimming.CharacterEllipsis, Cursor = Cursors.Hand, ToolTip = Loc.T("Open this folder"), Background = Brushes.Transparent,
        };
        _termDir.MouseLeftButtonUp += (_, e) => { e.Handled = true; Launch(TermFolder); };
        Grid.SetColumn(_termDir, 1);
        head.Children.Add(_termDir);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _termStopBtn = TermIcon("", Loc.T("Stop (Ctrl+C)"), TermStop);
        _termStopBtn.Visibility = _shell?.Busy == true ? Visibility.Visible : Visibility.Collapsed;
        tools.Children.Add(_termStopBtn);
        tools.Children.Add(TermIcon("", Loc.T("Clear (Ctrl+L)"), TermClear));
        tools.Children.Add(TermIcon("", Loc.T("Open in Windows Terminal"), TermOpenOutside));
        Grid.SetColumn(tools, 2);
        head.Children.Add(tools);
        root.Children.Add(head);

        // the log
        _termLog = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        _termScroll = new ScrollViewer
        {
            Content = _termLog, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false, PanningMode = PanningMode.VerticalOnly,
        };
        Grid.SetRow(_termScroll, 1);
        root.Children.Add(_termScroll);

        // the line you type on
        var line = new Grid();
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition());
        line.Children.Add(new TextBlock { Text = _cyber ? ">" : "❯", FontFamily = TermMono, FontSize = 12, Foreground = Frozen(TermAcc), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 8, 1) });
        _termBox = new TextBox
        {
            Text = keep, FontFamily = TermMono, FontSize = 12, Foreground = Frozen(TermInk), CaretBrush = Frozen(TermAcc), SelectionBrush = Frozen(TermAcc),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0, 0, 8, 0), VerticalContentAlignment = VerticalAlignment.Center,
            FocusVisualStyle = null, AcceptsReturn = false,
        };
        var ghost = new TextBlock { Text = Loc.T("Type a command"), FontFamily = TermMono, FontSize = 12, Foreground = Frozen(A(TermSoft, 0x70)), IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 1) };
        ghost.Visibility = keep.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        _termBox.TextChanged += (_, _) => ghost.Visibility = _termBox.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        _termBox.PreviewMouseLeftButtonDown += (_, _) => { _maybeSwipe = false; BeginWorkTyping(_termBox); };
        _termBox.PreviewKeyDown += TermKey;
        var boxCell = new Grid { Children = { _termBox, ghost } };
        Grid.SetColumn(boxCell, 1);
        line.Children.Add(boxCell);
        var lineFrame = new Border
        {
            Child = line, CornerRadius = new CornerRadius(_cyber ? 0 : 9), BorderThickness = new Thickness(1),
            Background = Frozen(_cyber ? Color.FromArgb(0x90, 0x00, 0x00, 0x00) : A(TermInk, 0x0C)), BorderBrush = Frozen(_cyber ? A(CyCyan, 0x60) : TermLine),
            Cursor = Cursors.IBeam,
        };
        lineFrame.MouseLeftButtonDown += (_, e) => { _maybeSwipe = false; BeginWorkTyping(_termBox); e.Handled = true; };
        if (_cyber) NeonCorner(lineFrame, 6, 0);
        Grid.SetRow(lineFrame, 2);
        root.Children.Add(lineFrame);
        _termPage.Children.Add(root);

        TermShowDir();
        TermRenderAll();
    }

    Border TermIcon(string glyph, string tip, Action click)
    {
        var g = new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 12, Foreground = Frozen(TermSoft), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var b = new Border { Width = 26, Height = 22, CornerRadius = new CornerRadius(_cyber ? 0 : 6), Background = Brushes.Transparent, Child = g, Cursor = Cursors.Hand, ToolTip = tip };
        b.MouseEnter += (_, _) => { b.Background = Frozen(A(TermInk, 0x12)); g.Foreground = Frozen(TermInk); };
        b.MouseLeave += (_, _) => { b.Background = Brushes.Transparent; g.Foreground = Frozen(TermSoft); };
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; FeelSound(Services.Sfx.Cue.Tap); click(); };
        return b;
    }

    string TermFolder => _shell?.Folder is { Length: > 0 } f ? f : Directory.Exists(_settings.TerminalFolder) ? _settings.TerminalFolder : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    void TermShowDir()
    {
        if (_termDir == null) return;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var f = TermFolder;
        _termDir.Text = f.StartsWith(home, StringComparison.OrdinalIgnoreCase) ? "~" + f[home.Length..] : f;
        if (_termShell != null) _termShell.Text = Cap(_shell?.ShellName ?? "PowerShell");
        if (_termStopBtn != null) _termStopBtn.Visibility = _shell?.Busy == true ? Visibility.Visible : Visibility.Collapsed;
    }

    // ───────────── the log ─────────────

    void TermRenderAll()
    {
        if (_termLog == null) return;
        _termLog.Children.Clear();
        if (_termBlocks.Count == 0) { _termLog.Children.Add(TermEmpty()); return; }
        foreach (var b in _termBlocks) _termLog.Children.Add(TermBlockView(b));
    }

    /// <summary>Nothing run yet: what this is, in one line, and a few commands to start with (yours first).</summary>
    FrameworkElement TermEmpty()
    {
        var st = new StackPanel { Margin = new Thickness(2, 18, 2, 0) };
        st.Children.Add(new TextBlock { Text = Loc.T("Run a command"), FontFamily = ThemeText, FontSize = 13, FontWeight = FontWeights.Medium, Foreground = Frozen(TermInk) });
        st.Children.Add(new TextBlock { Text = Loc.T("Your folder and variables carry over from one command to the next"), FontFamily = ThemeText, FontSize = 11, Foreground = Frozen(TermSoft), Margin = new Thickness(0, 3, 0, 12), TextWrapping = TextWrapping.Wrap });
        var chips = new WrapPanel();
        var picks = _settings.TerminalHistory.AsEnumerable().Reverse().Distinct().Take(4).ToList();
        if (picks.Count == 0) picks = new() { "git status", "Get-ChildItem", "ipconfig", "Get-Date" };
        foreach (var cmd in picks)
        {
            var t = new TextBlock { Text = cmd.Length > 28 ? cmd[..27] + "…" : cmd, FontFamily = TermMono, FontSize = 11, Foreground = Frozen(TermInk) };
            var chip = new Border
            {
                Child = t, Padding = new Thickness(9, 4, 9, 5), Margin = new Thickness(0, 0, 6, 6), CornerRadius = new CornerRadius(_cyber ? 0 : 7),
                Background = Frozen(A(TermInk, 0x0C)), BorderBrush = Frozen(TermLine), BorderThickness = new Thickness(1), Cursor = Cursors.Hand, ToolTip = cmd,
            };
            chip.MouseEnter += (_, _) => chip.Background = Frozen(A(TermInk, 0x18));
            chip.MouseLeave += (_, _) => chip.Background = Frozen(A(TermInk, 0x0C));
            chip.MouseLeftButtonUp += (_, e) => { e.Handled = true; TermRun(cmd); };
            chips.Children.Add(chip);
        }
        st.Children.Add(chips);
        return st;
    }

    FrameworkElement TermBlockView(TermBlock b)
    {
        var st = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var cmd = new TextBlock { FontFamily = TermMono, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = b.Cmd };
        cmd.Inlines.Add(new System.Windows.Documents.Run((_cyber ? "> " : "❯ ")) { Foreground = Frozen(TermAcc) });
        cmd.Inlines.Add(new System.Windows.Documents.Run(b.Cmd) { Foreground = Frozen(TermInk) });
        top.Children.Add(cmd);
        // the signature: a dot and the time it took
        b.Dot = new Ellipse { Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 6, 0) };
        b.Status = new TextBlock { FontFamily = TermMono, FontSize = 10.5, Foreground = Frozen(TermSoft), VerticalAlignment = VerticalAlignment.Center };
        var right = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 0), Children = { b.Dot, b.Status } };
        Grid.SetColumn(right, 1);
        top.Children.Add(right);
        st.Children.Add(top);
        b.Out = new StackPanel { Margin = new Thickness(14, 3, 0, 0) };
        st.Children.Add(b.Out);
        if (b.Dropped > 0) b.Out.Children.Add(TermSegment(string.Format(Loc.T("… {0} earlier lines"), b.Dropped), false, muted: true));
        TermAppend(b, b.Lines);
        TermStatus(b);
        return st;
    }

    TextBox TermSegment(string text, bool err, bool muted = false) => new()
    {
        Text = text, IsReadOnly = true, IsReadOnlyCaretVisible = false, IsTabStop = false, FocusVisualStyle = null, Cursor = Cursors.IBeam, IsUndoEnabled = false,
        FontFamily = TermMono, FontSize = 11, TextWrapping = TextWrapping.Wrap, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
        Foreground = Frozen(err ? TermBad : muted ? A(TermSoft, 0x90) : A(TermInk, 0xD8)), SelectionBrush = Frozen(TermAcc),
    };

    /// <summary>New lines into a block: next to the last ones from the same stream, or a new piece when it switches (one append per run of lines).</summary>
    void TermAppend(TermBlock b, IEnumerable<(string Text, bool Err)> lines)
    {
        if (b.Out == null) return;
        var run = new System.Text.StringBuilder();
        bool? runErr = null;
        void Flush()
        {
            if (runErr is not { } err || run.Length == 0) return;
            if (b.Out.Children.Count > 0 && b.Out.Children[^1] is TextBox last && (string)(last.Tag ?? "") == (err ? "e" : "o")) last.AppendText("\n" + run);
            else { var seg = TermSegment(run.ToString(), err); seg.Tag = err ? "e" : "o"; b.Out.Children.Add(seg); }
            run.Clear();
        }
        foreach (var (text, err) in lines)
        {
            if (runErr != err) { Flush(); runErr = err; }
            else run.Append('\n');
            run.Append(text);
        }
        Flush();
    }

    void TermStatus(TermBlock b)
    {
        if (b.Dot == null || b.Status == null) return;
        if (b.Running)
        {
            b.Dot.Fill = Frozen(A(TermInk, 0x50));
            double s = (DateTime.UtcNow - b.Started).TotalSeconds;
            b.Status.Text = s < 1 ? Loc.T("running") : TermTime(s);
            return;
        }
        b.Dot.Fill = Frozen(b.Failed ? TermBad : TermOk);
        b.Status.Text = (b.Code is not (0 or -1) ? $"exit {b.Code} · " : "") + TermTime(b.Secs);
    }

    static string TermTime(double s) => s < 1 ? $"{s * 1000:0} ms" : s < 60 ? $"{s:0.0} s" : $"{(int)s / 60}:{(int)s % 60:00}";

    void TermScrollEnd(bool onlyIfNear = false)
    {
        if (_termScroll == null) return;
        if (onlyIfNear && _termScroll.ScrollableHeight - _termScroll.VerticalOffset > 40) return; // (you scrolled up to read: it stays there)
        _termScroll.ScrollToEnd(); // (applied at the next layout: no forced layout per batch)
    }

    // ───────────── running ─────────────

    void TermRun(string command)
    {
        command = command.Trim();
        if (command.Length == 0) return;
        if (command is "clear" or "cls") { TermClear(); return; }
        if (_shell?.Busy == true) return;
        if (_shell == null)
        {
            _shell = new ShellSession(TermFolder);
            _shell.Line += (t, err) =>
            {
                if (Interlocked.Increment(ref _termQueued) > TermQueueMax) { Interlocked.Decrement(ref _termQueued); Interlocked.Increment(ref _termDroppedIn); return; }
                _termIncoming.Enqueue((t, err));
            };
            _shell.Done += (code, failed, folder) => Dispatcher.BeginInvoke(() => TermDone(code, failed, folder));
        }
        // the history: the last 50 commands, never one that looks like it holds a password or a key
        _termHistAt = -1;
        if (!TermSecret.IsMatch(command))
        {
            _settings.TerminalHistory.Remove(command);
            _settings.TerminalHistory.Add(command);
            if (_settings.TerminalHistory.Count > 50) _settings.TerminalHistory.RemoveAt(0);
        }
        if (_termBlocks.Count == 0) _termLog?.Children.Clear();
        var b = new TermBlock { Cmd = command };
        _termBlocks.Add(b);
        while (_termBlocks.Count > TermKeepBlocks) { _termBlocks.RemoveAt(0); _termLog?.Children.RemoveAt(0); }
        _termLog?.Children.Add(TermBlockView(b));
        if (!_shell.Run(command)) { b.Running = false; b.Failed = true; TermStatus(b); return; }
        _termFlush ??= new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(60) };
        _termFlush.Tick -= TermFlushTick;
        _termFlush.Tick += TermFlushTick;
        _termFlush.Start();
        TermShowDir();
        TermScrollEnd();
    }

    void TermFlushTick(object? sender, EventArgs e)
    {
        TermFlushNow();
        if (_shell?.Busy != true && _termIncoming.IsEmpty) _termFlush?.Stop();
    }

    void TermFlushNow()
    {
        if (_termBlocks.Count == 0) { while (_termIncoming.TryDequeue(out _)) { } return; }
        var b = _termBlocks[^1];
        var batch = new List<(string, bool)>();
        while (batch.Count < 4000 && _termIncoming.TryDequeue(out var l)) { batch.Add(l); Interlocked.Decrement(ref _termQueued); }
        if (Interlocked.Exchange(ref _termDroppedIn, 0) is > 0 and var lost) b.Dropped += lost;
        // PowerShell pads its tables with empty lines: none at the start of a block
        if (b.Lines.Count == 0) batch = batch.SkipWhile(l => l.Item1.Length == 0).ToList();
        if (batch.Count > 0)
        {
            b.Lines.AddRange(batch);
            if (b.Lines.Count > TermKeepLines)
            {
                int cut = b.Lines.Count - TermKeepLines * 2 / 3; // (a big cut, so the block isn't rebuilt on every batch)
                b.Lines.RemoveRange(0, cut);
                b.Dropped += cut;
                if (b.Out != null) { b.Out.Children.Clear(); b.Out.Children.Add(TermSegment(string.Format(Loc.T("… {0} earlier lines"), b.Dropped), false, muted: true)); TermAppend(b, b.Lines); }
            }
            else TermAppend(b, batch);
            TermScrollEnd(onlyIfNear: true);
        }
        if (b.Running) TermStatus(b);
    }

    void TermDone(int code, bool failed, string folder)
    {
        TermFlushNow();
        if (_termBlocks.Count > 0 && _termBlocks[^1] is { Running: true } b)
        {
            // and no empty lines at the end of it either
            while (b.Lines.Count > 0 && b.Lines[^1].Text.Length == 0) b.Lines.RemoveAt(b.Lines.Count - 1);
            if (b.Out != null && b.Out.Children.Count > 0 && b.Out.Children[^1] is TextBox last) last.Text = last.Text.TrimEnd('\n', ' ');
            b.Running = false;
            b.Failed = failed;
            b.Code = code;
            b.Secs = (DateTime.UtcNow - b.Started).TotalSeconds;
            TermStatus(b);
        }
        _settings.TerminalFolder = folder;
        _settings.Save();
        TermShowDir();
        TermScrollEnd(onlyIfNear: true);
    }

    void TermStop()
    {
        if (_shell?.Busy != true) return;
        _shell.Stop();
        if (_termBlocks.Count > 0 && _termBlocks[^1] is { } b) b.Lines.Add((Loc.T("Stopped"), true));
        TermRenderAll();
        TermShowDir();
    }

    void TermClear()
    {
        _termBlocks.Clear();
        while (_termIncoming.TryDequeue(out _)) { }
        TermRenderAll();
    }

    void TermOpenOutside()
    {
        var dir = TermFolder;
        try { Process.Start(new ProcessStartInfo("wt.exe", $"-d \"{dir.TrimEnd('\\')}\\.\"") { UseShellExecute = true }); Collapse(); } // ("C:\\." : a root's trailing \\ would escape the quote)
        catch
        {
            try { Process.Start(new ProcessStartInfo("powershell.exe") { UseShellExecute = true, WorkingDirectory = dir }); Collapse(); }
            catch (Exception ex) { AppLog.Write("terminal: no outside terminal: " + ex.Message); }
        }
    }

    void TermKey(object sender, KeyEventArgs e)
    {
        if (_termBox == null) return;
        var hist = _settings.TerminalHistory;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        switch (e.Key)
        {
            case Key.Enter:
                var cmd = _termBox.Text;
                _termBox.Text = "";
                TermRun(cmd);
                break;
            case Key.Escape:
                EndWorkTyping(giveBack: true);
                break;
            case Key.Up when hist.Count > 0:
                if (_termHistAt < 0) { _termDraft = _termBox.Text; _termHistAt = hist.Count; }
                _termHistAt = Math.Max(0, _termHistAt - 1);
                _termBox.Text = hist[_termHistAt];
                _termBox.CaretIndex = _termBox.Text.Length;
                break;
            case Key.Down when _termHistAt >= 0:
                _termHistAt++;
                if (_termHistAt >= hist.Count) { _termHistAt = -1; _termBox.Text = _termDraft; }
                else _termBox.Text = hist[_termHistAt];
                _termBox.CaretIndex = _termBox.Text.Length;
                break;
            case Key.L when ctrl:
                TermClear();
                break;
            case Key.C when ctrl && _termBox.SelectionLength == 0 && _shell?.Busy == true:
                TermStop();
                break;
            default:
                return;
        }
        e.Handled = true;
    }
}
