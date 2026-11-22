using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// The notes' smart side:
///  • AI helpers (the ✦ at a note's foot): Tidy up (spelling, clearer, nothing lost), Summarize (a short line on top),
///    Make a list. Each change can be undone for a few seconds. A note left without a title gets one from what you
///    wrote, once, a moment after you stop typing (only when the AI is set up; otherwise its first line stands in).
///  • Smart typing: links, emails and phone numbers are clickable while you're not typing in the note (a click
///    anywhere else in it starts typing right there); Enter on a list line ("- ", "• ", "1.", "[ ]") starts the next
///    item, and Enter on an empty item ends the list.
///  • Colour tags: Work, Personal, Ideas — a small dot on the note in the list; click the dot at its foot to change.
/// </summary>
public partial class NotchWindow
{
    static readonly (string Key, string Name, Color Color)[] NoteTags =
    {
        ("work", "Work", Color.FromRgb(0x4F, 0x8B, 0xFF)),
        ("personal", "Personal", Color.FromRgb(0x34, 0xC7, 0x7B)),
        ("ideas", "Ideas", Color.FromRgb(0xFF, 0xB2, 0x3F)),
    };

    static Color? NoteTagColor(string tag) => NoteTags.FirstOrDefault(t => t.Key == tag) is { Key: not null } t ? t.Color : null;

    readonly Dictionary<string, Shape> _noteDots = new();
    System.Windows.Threading.DispatcherTimer? _noteTitleTimer;
    (string Id, string Before, DateTime At)? _noteUndo;

    // ───────────── colour tags ─────────────

    /// <summary>The small dot before a note's title in the list (hidden when it has no tag).</summary>
    Shape NoteDot(Note n, double size)
    {
        Shape dot = _cyber ? new Rectangle() : new Ellipse();
        dot.Width = dot.Height = size;
        dot.VerticalAlignment = VerticalAlignment.Center;
        dot.Margin = new Thickness(0, 0, 5, 0);
        PaintDot(dot, n.Tag);
        return dot;
    }

    static void PaintDot(Shape dot, string tag)
    {
        var c = NoteTagColor(tag);
        dot.Visibility = c == null ? Visibility.Collapsed : Visibility.Visible;
        if (c is { } col) dot.Fill = Frozen(col);
    }

    /// <summary>The tag button at a note's foot: its dot (an empty ring when none); a click moves to the next tag.</summary>
    FrameworkElement TagButton(Note n)
    {
        var ring = new Ellipse { Width = 9, Height = 9, StrokeThickness = 1.2 };
        void Paint()
        {
            var c = NoteTagColor(n.Tag);
            ring.Fill = c is { } col ? Frozen(col) : Brushes.Transparent;
            ring.Stroke = Frozen(c is { } col2 ? Lighten(col2, 0.3) : A(TextTone, 0x70));
        }
        Paint();
        var host = new Border
        {
            Background = Brushes.Transparent, Padding = new Thickness(2, 4, 6, 4), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Child = ring,
            ToolTip = Loc.T("Colour tag: Work, Personal, Ideas"), RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(),
        };
        host.PreviewMouseLeftButtonDown += (_, _) => _maybeSwipe = false;
        host.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            int i = Array.FindIndex(NoteTags, t => t.Key == n.Tag);
            n.Tag = i + 1 < NoteTags.Length ? NoteTags[i + 1].Key : "";
            Paint();
            if (_noteDots.TryGetValue(n.Id, out var dot)) PaintDot(dot, n.Tag);
            FeelSound(Sfx.Cue.Tap);
            if (!Eco && host.RenderTransform is ScaleTransform st)
            {
                var pop = new DoubleAnimation(1.35, 1, TimeSpan.FromMilliseconds(300)) { EasingFunction = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut } };
                st.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
                st.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
            }
            if (_noteWhen != null)
            {
                _noteWhen.Text = n.Tag.Length == 0 ? Loc.T("No tag") : Loc.T(NoteTags.First(t => t.Key == n.Tag).Name);
                After(1300, UpdateNoteWhen);
            }
            SaveNotes();
        };
        return host;
    }

    // ───────────── smart typing ─────────────

    static readonly Regex ListLine = new(@"^(\s*)([-*•]|\d+[.)]|\[[ xX]?\])(\s+)(.*)$", RegexOptions.Compiled);

    /// <summary>Enter on a list line: the next item starts (numbers count up, boxes come empty); on an empty item the list ends.</summary>
    bool ContinueList(TextBox box)
    {
        int caret = box.CaretIndex;
        if (box.SelectionLength > 0) return false;
        string text = box.Text;
        int start = caret == 0 ? 0 : text.LastIndexOf('\n', caret - 1) + 1;
        int end = text.IndexOf('\n', caret); if (end < 0) end = text.Length;
        if (caret != end) return false; // only at the end of the line
        var m = ListLine.Match(text[start..end]);
        if (!m.Success) return false;
        if (m.Groups[4].Value.Trim().Length == 0)
        {
            // an empty item: the list is done, its marker goes
            box.Text = text[..start] + text[end..];
            box.CaretIndex = start;
            return true;
        }
        string marker = m.Groups[2].Value;
        if (Regex.Match(marker, @"^(\d+)([.)])$") is { Success: true } num) marker = (int.Parse(num.Groups[1].Value) + 1) + num.Groups[2].Value;
        else if (marker.StartsWith('[')) marker = "[ ]";
        string add = "\n" + m.Groups[1].Value + marker + " ";
        box.Text = text[..caret] + add + text[caret..];
        box.CaretIndex = caret + add.Length;
        return true;
    }

    static readonly Regex LinkLike = new(
        @"(?<url>(?:https?://|www\.)[^\s<>""]+[^\s<>"".,;:!?)\]])|(?<mail>[\w.+-]+@[\w-]+(?:\.[\w-]+)+)|(?<tel>(?<![\w+])\+?\d[\d \-().]{6,}\d(?!\w))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    static bool HasLinks(string text) => LinkLike.Matches(text).Any(m => !m.Groups["tel"].Success || m.Value.Count(char.IsDigit) >= 8);

    /// <summary>
    /// The text of a note, with a reading view on top while you're not typing in it: there its links, emails and phone
    /// numbers can be clicked. A click anywhere else starts typing at that spot (the box takes over).
    /// </summary>
    Grid SmartBody(TextBox box)
    {
        var host = new Grid();
        var ghosted = Ghosted(box);
        host.Children.Add(ghosted);
        var reader = new TextBlock
        {
            FontSize = box.FontSize, FontFamily = box.FontFamily, Foreground = box.Foreground, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 2, 0),
            Background = Brushes.Transparent, Cursor = Cursors.IBeam,
        };
        var scroll = new ScrollViewer { Content = reader, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        host.Children.Add(scroll);

        void Read()
        {
            bool show = !box.IsKeyboardFocusWithin && HasLinks(box.Text);
            scroll.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            ghosted.Opacity = show ? 0 : 1;
            if (!show) return;
            reader.Inlines.Clear();
            string text = box.Text;
            int at = 0;
            var acc = Lighten(NoteAcc, 0.35);
            foreach (Match m in LinkLike.Matches(text))
            {
                if (m.Groups["tel"].Success && m.Value.Count(char.IsDigit) < 8) continue;
                if (m.Index > at) reader.Inlines.Add(new Run(text[at..m.Index]));
                string target = m.Groups["url"].Success ? (m.Value.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + m.Value : m.Value)
                    : m.Groups["mail"].Success ? "mailto:" + m.Value : "tel:" + new string(m.Value.Where(c => char.IsDigit(c) || c == '+').ToArray());
                var link = new Hyperlink(new Run(m.Value)) { Foreground = Frozen(acc), Cursor = Cursors.Hand, TextDecorations = TextDecorations.Underline, ToolTip = target };
                link.PreviewMouseLeftButtonDown += (_, e) => { e.Handled = true; _maybeSwipe = false; };
                link.PreviewMouseLeftButtonUp += (_, e) => { e.Handled = true; FeelSound(Sfx.Cue.Open); Launch(target); };
                reader.Inlines.Add(link);
                at = m.Index + m.Length;
            }
            if (at < text.Length) reader.Inlines.Add(new Run(text[at..]));
        }

        // a click on the words (not a link): typing starts right where you clicked
        reader.PreviewMouseLeftButtonDown += (_, _) => _maybeSwipe = false;
        reader.MouseLeftButtonUp += (_, e) =>
        {
            if (e.Handled) return;
            e.Handled = true;
            var p = e.GetPosition(box);
            scroll.Visibility = Visibility.Collapsed;
            ghosted.Opacity = 1;
            BeginWorkTyping(box);
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
            {
                int i = box.GetCharacterIndexFromPoint(p, snapToText: true);
                box.CaretIndex = i >= 0 ? Math.Min(box.Text.Length, i + 1) : box.Text.Length;
            });
        };
        box.LostKeyboardFocus += (_, _) => Read();
        box.GotKeyboardFocus += (_, _) => { scroll.Visibility = Visibility.Collapsed; ghosted.Opacity = 1; };
        Read();
        return host;
    }

    // ───────────── AI helpers ─────────────

    bool NoteAiReady { get { try { PrepareAi(); return _settings.AiChat && OpenRouter.HasKey; } catch { return false; } } }

    /// <summary>One answer from the AI for a note (a tidier text, a summary, a list, a title), or null.</summary>
    async Task<string?> NoteAiAsync(string kind, string title, string body, CancellationToken stop)
    {
        try
        {
            PrepareAi();
            if (!OpenRouter.HasKey || OpenRouter.LoadKey() is not { } key) return null;
            string ask = kind switch
            {
                "tidy" => "Rewrite this note so it reads clearly: fix spelling, grammar and punctuation, smooth awkward wording, and organise it a little (short paragraphs, lists kept as lists). Keep its language, its meaning and every detail (names, numbers, dates, links); don't add anything. Return only the note's text.",
                "sum" => "Summarize this note in one or two short sentences, in its language, keeping the key facts (names, numbers, dates). Return only the summary.",
                "list" => "Turn this note into a clean list: one item per line, each starting with \"- \", in its language, keeping every detail; short items, no heading, no extra words. Return only the list.",
                _ => "Give this note a short title (2 to 5 words) in its language, the way a person would name it. No quotes, no period at the end. Return only the title.",
            };
            var msgs = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = "You help with a personal note. " + ask },
                new JsonObject { ["role"] = "user", ["content"] = (title.Trim().Length > 0 ? "Title: " + title.Trim() + "\n\n" : "") + body },
            };
            OpenRouter.ChatRequest.Value = false;
            var turn = await OpenRouter.Stream(key, ChatModel, msgs, null, _ => { }, null, stop, kind == "title" ? 0.4 : 0.3, "low");
            string text = turn.Text.Trim();
            return text.Length == 0 ? null : text;
        }
        catch { return null; }
    }

    /// <summary>The ✦ chips under a note: Tidy up, Summarize, Make a list. They slide in and out.</summary>
    FrameworkElement AiChips(Note n, Func<FrameworkElement?> chipsHost)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2), Visibility = Visibility.Collapsed, RenderTransform = new TranslateTransform() };
        foreach (var (kind, label) in new[] { ("tidy", "Tidy up"), ("sum", "Summarize"), ("list", "Make a list") })
            row.Children.Add(AiChip(n, kind, Loc.T(label)));
        return row;
    }

    Border AiChip(Note n, string kind, string label)
    {
        var acc = NoteAcc;
        var text = new TextBlock { Text = label, FontSize = 10, FontFamily = NoteFont, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        var chip = new Border
        {
            CornerRadius = new CornerRadius(_cyber ? 0 : 9), Padding = new Thickness(8, 2, 8, 3), Margin = new Thickness(0, 0, 5, 0), Cursor = Cursors.Hand,
            Background = Frozen(A(acc, 0x2C)), BorderBrush = Frozen(A(acc, 0x66)), BorderThickness = new Thickness(1), Child = text,
        };
        chip.MouseEnter += (_, _) => chip.Background = Frozen(A(acc, 0x48));
        chip.MouseLeave += (_, _) => chip.Background = Frozen(A(acc, 0x2C));
        chip.PreviewMouseLeftButtonDown += (_, _) => _maybeSwipe = false;
        chip.MouseLeftButtonUp += async (_, e) =>
        {
            e.Handled = true;
            if (n.Body.Trim().Length == 0 || chip.Tag is "busy") return;
            chip.Tag = "busy";
            FeelSound(Sfx.Cue.Tap);
            text.Text = "…";
            var think = new DoubleAnimation(0.45, 1, TimeSpan.FromMilliseconds(520)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
            if (!Eco) _noteBody?.BeginAnimation(OpacityProperty, think);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            string before = n.Body;
            string? answer = await NoteAiAsync(kind, n.Title, n.Body, cts.Token);
            _noteBody?.BeginAnimation(OpacityProperty, null);
            chip.Tag = null;
            text.Text = label;
            if (answer == null)
            {
                if (_noteWhen != null) { _noteWhen.Text = Loc.T("The AI didn't answer"); After(1800, UpdateNoteWhen); }
                FeelSound(Sfx.Cue.Oops);
                return;
            }
            if (n.Body != before) return; // you kept typing meanwhile: your words win
            n.Body = kind == "sum" ? "✦ " + answer.Replace("\n", " ") + "\n\n" + before : answer;
            n.Updated = DateTime.Now;
            _noteUndo = (n.Id, before, DateTime.UtcNow);
            FeelSound(Sfx.Cue.Done);
            SaveNotes();
            if (_noteOpen == n.Id) FillEditor(animate: true);
            if (_noteRows.TryGetValue(n.Id, out var r)) { r.Title.Text = TitleOf(n); r.Snippet.Text = SnippetOf(n); }
            ShowUndo(n);
        };
        return chip;
    }

    /// <summary>For a few seconds after the AI changed a note: "Undo" in its foot puts your words back.</summary>
    void ShowUndo(Note n)
    {
        if (_noteWhen == null || _noteUndo is not { } u || u.Id != n.Id) return;
        _noteWhen.Inlines.Clear();
        _noteWhen.Inlines.Add(new Run(Loc.T("Changed by the AI") + "  ·  "));
        var link = new Hyperlink(new Run(Loc.T("Undo"))) { Foreground = Frozen(Lighten(NoteAcc, 0.35)), TextDecorations = null, Cursor = Cursors.Hand };
        link.PreviewMouseLeftButtonDown += (_, e) => { e.Handled = true; _maybeSwipe = false; };
        link.PreviewMouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (_noteUndo is not { } un || un.Id != n.Id) return;
            n.Body = un.Before;
            n.Updated = DateTime.Now;
            _noteUndo = null;
            FeelSound(Sfx.Cue.Off);
            SaveNotes();
            if (_noteOpen == n.Id) FillEditor(animate: false);
            if (_noteRows.TryGetValue(n.Id, out var r)) { r.Title.Text = TitleOf(n); r.Snippet.Text = SnippetOf(n); }
        };
        _noteWhen.Inlines.Add(link);
        var at = u.At;
        After(9000, () => { if (_noteUndo is { } still && still.At == at) { _noteUndo = null; UpdateNoteWhen(); } });
    }

    /// <summary>A note with words but no title: a moment after you stop typing, the AI names it (once).</summary>
    void MaybeAutoTitle(Note n)
    {
        if (n.Title.Trim().Length > 0 || n.AutoTitled || n.Body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length < 6 || NotesDemo) return;
        _noteTitleTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _noteTitleTimer.Stop();
        _noteTitleTimer.Tag = n;
        _noteTitleTimer.Tick -= OnAutoTitle;
        _noteTitleTimer.Tick += OnAutoTitle;
        _noteTitleTimer.Start();
    }

    async void OnAutoTitle(object? sender, EventArgs e)
    {
        _noteTitleTimer?.Stop();
        if (_noteTitleTimer?.Tag is not Note n || n.Title.Trim().Length > 0 || n.AutoTitled || !NoteAiReady) return;
        n.AutoTitled = true; // once: if you clear it, it stays as you want it
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        string? title = await NoteAiAsync("title", "", n.Body, cts.Token);
        if (title == null || n.Title.Trim().Length > 0) return;
        title = title.Split('\n')[0].Trim().Trim('"', '“', '”', '.', '#', '*').Trim();
        if (title.Length is 0 or > 60) return;
        n.Title = title;
        SaveNotes();
        if (_noteRows.TryGetValue(n.Id, out var r)) { r.Title.Text = TitleOf(n); r.Snippet.Text = SnippetOf(n); }
        // the open note's title fills in softly (never while you're typing in it)
        if (_noteOpen == n.Id && _noteTitle is { } box && !box.IsKeyboardFocusWithin)
        {
            _noteFilling = true;
            box.Text = title;
            _noteFilling = false;
            if (!Eco) box.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(420)));
        }
    }
}
