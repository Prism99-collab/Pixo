using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// Notes, the Search page's third side: a little notes app in the notch. Your notes on the left (pinned ones first,
/// then the newest), the open one on the right: its title and its text, written straight in, saved as you type (half a
/// second after you stop). Pin, copy or delete it from its foot; a delete asks once ("Sure?"). A new note comes in at
/// the top of the list and its title takes the keyboard. Everything stays on this PC (notes.json).
/// The keyboard is only taken when you click into a note, and given back when you leave the page or the notch closes.
/// </summary>
public partial class NotchWindow
{
    List<Note>? _notes;
    string? _noteOpen;
    StackPanel? _noteList;
    Grid? _noteEditor;
    TextBox? _noteTitle, _noteBody;
    TextBlock? _noteWhen, _noteCount;
    readonly Dictionary<string, (TextBlock Title, TextBlock Snippet, Border Card)> _noteRows = new();
    System.Windows.Threading.DispatcherTimer? _noteSave;
    bool _noteFilling;
    DateTime _noteAskAt;
    bool _noteAsking;

    List<Note> Notes => _notes ??= NotesDemo ? DemoNotes() : NotesStore.Load();

    // for a look (DYNAMICNOTCH_NOTESDEMO=1): a few made-up notes, kept in memory only, never saved
    static readonly bool NotesDemo = Environment.GetEnvironmentVariable("DYNAMICNOTCH_NOTESDEMO") == "1";
    static List<Note> DemoNotes() => new()
    {
        new() { Title = "Trip to Lisbon", Tag = "personal", Body = "Book the flight before Friday\n- charger, adapter, sunglasses\n- hotel: www.example.com/lisbon\nAsk sam@example.com about it", Pinned = true, Updated = DateTime.Now.AddHours(-2) },
        new() { Title = "Groceries", Tag = "personal", Body = "Milk, eggs, bread\nCoffee beans\nSomething for dinner", Updated = DateTime.Now.AddMinutes(-25) },
        new() { Title = "", Tag = "ideas", Body = "Idea: a little widget that shows the weather in the notch when it rains", Updated = DateTime.Now.AddDays(-1) },
        new() { Title = "Meeting notes", Tag = "work", Body = "Ship the new build on Monday\nCheck the icons at 150%", Updated = DateTime.Now.AddDays(-3) },
    };

    Color NoteAcc => _cyber ? CyCyan : _accent == Colors.White ? Color.FromRgb(0x8E, 0x8C, 0xFF) : _accent;
    FontFamily NoteFont => _cyber ? CyFont : UiText;

    static IEnumerable<Note> Ordered(IEnumerable<Note> notes) => notes.OrderByDescending(n => n.Pinned).ThenByDescending(n => n.Updated);

    /// <summary>The whole Notes side: the list and the open note (or a quiet welcome when there are none).</summary>
    FrameworkElement NotesView()
    {
        _noteRows.Clear();
        _noteDots.Clear();
        var root = new Grid { Height = 166 };
        if (Notes.Count == 0)
        {
            root.Children.Add(NotesEmpty());
            return root;
        }
        if (_noteOpen == null || Notes.All(n => n.Id != _noteOpen)) _noteOpen = Ordered(Notes).First().Id;

        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(134) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        root.ColumnDefinitions.Add(new ColumnDefinition());

        // ── the list: a small head (how many, +), then the notes
        var left = new Grid();
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition());
        var head = new Grid { Margin = new Thickness(4, 0, 0, 4) };
        _noteCount = new TextBlock { FontSize = 10, FontFamily = NoteFont, Foreground = Frozen(A(TextTone, 0x90)), VerticalAlignment = VerticalAlignment.Center };
        head.Children.Add(_noteCount);
        var plus = IconButton("", Loc.T("New note"), NewNote, 10, NoteAcc);
        plus.HorizontalAlignment = HorizontalAlignment.Right;
        head.Children.Add(plus);
        left.Children.Add(head);
        _noteList = new StackPanel();
        var scroll = new ScrollViewer { Content = _noteList, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, PanningMode = PanningMode.VerticalOnly };
        Grid.SetRow(scroll, 1);
        left.Children.Add(scroll);
        root.Children.Add(left);
        foreach (var n in Ordered(Notes)) _noteList.Children.Add(NoteRow(n));
        UpdateNoteCount();

        // ── the open note
        _noteEditor = new Grid();
        Grid.SetColumn(_noteEditor, 2);
        root.Children.Add(_noteEditor);
        FillEditor(animate: false);
        return root;
    }

    FrameworkElement NotesEmpty()
    {
        var acc = NoteAcc;
        var box = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        box.Children.Add(Glyph("", 22, Frozen(A(acc, 0xC0))).Also(g => { g.HorizontalAlignment = HorizontalAlignment.Center; g.Margin = new Thickness(0, 0, 0, 8); }));
        box.Children.Add(new TextBlock { Text = Loc.T("Your notes live here"), FontSize = 12.5, FontWeight = FontWeights.SemiBold, FontFamily = NoteFont, Foreground = Frozen(TextTone), HorizontalAlignment = HorizontalAlignment.Center });
        box.Children.Add(new TextBlock { Text = Loc.T("Ideas, lists, things to remember. Kept on this PC."), FontSize = 10.5, FontFamily = NoteFont, Foreground = Frozen(A(TextTone, 0x90)), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 12) });
        var btn = new Border
        {
            CornerRadius = new CornerRadius(_cyber ? 0 : 13), Padding = new Thickness(14, 5, 14, 6), HorizontalAlignment = HorizontalAlignment.Center, Cursor = Cursors.Hand,
            Background = Frozen(A(acc, 0x38)), BorderBrush = Frozen(A(acc, 0x80)), BorderThickness = new Thickness(1),
            Child = new TextBlock { Text = "+  " + Loc.T("New note"), FontSize = 11.5, FontWeight = FontWeights.SemiBold, FontFamily = NoteFont, Foreground = Brushes.White },
        };
        btn.MouseEnter += (_, _) => btn.Background = Frozen(A(acc, 0x55));
        btn.MouseLeave += (_, _) => btn.Background = Frozen(A(acc, 0x38));
        btn.PreviewMouseLeftButtonDown += (_, _) => _maybeSwipe = false;
        btn.MouseLeftButtonUp += (_, e) => { e.Handled = true; NewNote(); };
        box.Children.Add(btn);
        return box;
    }

    /// <summary>A note in the list: its title (or its first words), a line of its text, when.</summary>
    FrameworkElement NoteRow(Note n)
    {
        var acc = NoteAcc;
        bool open = n.Id == _noteOpen;
        var stack = new StackPanel();
        var top = new StackPanel { Orientation = Orientation.Horizontal };
        var dot = NoteDot(n, 6);
        top.Children.Add(dot);
        _noteDots[n.Id] = dot;
        if (n.Pinned) top.Children.Add(Glyph("", 8, Frozen(Lighten(acc, 0.3))).Also(g => { g.Margin = new Thickness(0, 0, 4, 0); g.VerticalAlignment = VerticalAlignment.Center; }));
        var title = new TextBlock { Text = TitleOf(n), FontSize = 11, FontWeight = FontWeights.SemiBold, FontFamily = NoteFont, Foreground = Frozen(TextTone), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 104 };
        top.Children.Add(title);
        stack.Children.Add(top);
        var snippet = new TextBlock { Text = SnippetOf(n), FontSize = 9.5, FontFamily = NoteFont, Foreground = Frozen(A(TextTone, 0x88)), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) };
        stack.Children.Add(snippet);
        var card = new Border
        {
            CornerRadius = new CornerRadius(_cyber ? 0 : 9), Padding = new Thickness(8, 5, 8, 6), Margin = new Thickness(0, 0, 0, 3), Cursor = Cursors.Hand,
            BorderThickness = new Thickness(1), Child = stack, RenderTransform = new TranslateTransform(),
        };
        void Paint(bool hover)
        {
            bool isOpen = n.Id == _noteOpen;
            card.Background = Frozen(isOpen ? A(acc, 0x30) : hover ? Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x26, 0x00, 0x00, 0x00));
            card.BorderBrush = Frozen(isOpen ? A(acc, 0x70) : Color.FromArgb(hover ? (byte)0x1C : (byte)0x0C, 0xFF, 0xFF, 0xFF));
        }
        Paint(false);
        card.Tag = (Action<bool>)Paint;
        card.MouseEnter += (_, _) => Paint(true);
        card.MouseLeave += (_, _) => Paint(false);
        card.PreviewMouseLeftButtonDown += (_, _) => _maybeSwipe = false;
        card.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (n.Id == _noteOpen) return;
            FlushNote();
            _noteOpen = n.Id;
            FeelSound(Sfx.Cue.Tap);
            foreach (var row in _noteRows.Values) (row.Card.Tag as Action<bool>)?.Invoke(row.Card.IsMouseOver);
            FillEditor(animate: true);
        };
        _noteRows[n.Id] = (title, snippet, card);
        return card;
    }

    static string TitleOf(Note n)
    {
        if (n.Title.Trim().Length > 0) return n.Title.Trim();
        string first = n.Body.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        return first.Length > 0 ? first : Loc.T("New note");
    }

    static string SnippetOf(Note n)
    {
        var lines = n.Body.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (n.Title.Trim().Length == 0 && lines.Count > 0) lines.RemoveAt(0); // (its first line is already the title)
        return lines.Count > 0 ? lines[0] : Ago(n.Updated);
    }

    /// <summary>The open note: its title, a line of light, its text; at its foot, when it changed, pin, copy, delete.</summary>
    void FillEditor(bool animate)
    {
        if (_noteEditor == null) return;
        var n = Notes.FirstOrDefault(x => x.Id == _noteOpen);
        _noteEditor.Children.Clear();
        _noteAsking = false;
        if (n == null) return;
        var acc = NoteAcc;
        _noteFilling = true;

        var card = new Border
        {
            CornerRadius = new CornerRadius(_cyber ? 0 : 12), BorderThickness = new Thickness(1), Padding = new Thickness(11, 7, 8, 5),
            Background = Frozen(_cyber ? Color.FromArgb(0xC8, 0x02, 0x06, 0x0C) : Color.FromArgb(0x6A, 0x00, 0x00, 0x00)), // solid enough that what is behind a see-through theme never shows through the words
            BorderBrush = Frozen(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF)),
        };
        var g = new Grid();
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition());
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        card.Child = g;

        _noteTitle = NoteBox(n.Title, Loc.T("Title"), 13, FontWeights.SemiBold, multi: false);
        g.Children.Add(Ghosted(_noteTitle));
        var line = new Border { Height = 1, Margin = new Thickness(0, 3, 30, 5), HorizontalAlignment = HorizontalAlignment.Stretch };
        var lg = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        lg.GradientStops.Add(new GradientStop(A(acc, 0xA0), 0));
        lg.GradientStops.Add(new GradientStop(A(acc, 0x00), 1));
        lg.Freeze();
        line.Background = lg;
        Grid.SetRow(line, 1);
        g.Children.Add(line);
        _noteBody = NoteBox(n.Body, Loc.T("Start writing…"), 11.5, FontWeights.Normal, multi: true);
        var body = SmartBody(_noteBody); // its links, emails and numbers can be clicked while you're not typing (NotchWindow.NotesSmart)
        Grid.SetRow(body, 2);
        g.Children.Add(body);

        // the foot: when it changed, then pin, copy, delete
        var foot = new Grid { Margin = new Thickness(0, 3, 0, 0) };
        _noteWhen = new TextBlock { FontSize = 9.5, FontFamily = NoteFont, Foreground = Frozen(A(TextTone, 0x80)), VerticalAlignment = VerticalAlignment.Center };
        var footLeft = new StackPanel { Orientation = Orientation.Horizontal };
        footLeft.Children.Add(TagButton(n));
        footLeft.Children.Add(_noteWhen);
        foot.Children.Add(footLeft);
        var chips = AiChips(n, () => null);
        Grid.SetRow(chips, 3);
        g.Children.Add(chips);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        // the AI helpers: the chips slide in above the foot (only when the AI is set up)
        if (NoteAiReady)
            tools.Children.Add(IconButton("\uE945", Loc.T("Tidy up, summarize, make a list"), () =>
            {
                bool show = chips.Visibility != Visibility.Visible;
                FeelSound(Sfx.Cue.Tap);
                chips.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                if (show && !Eco && chips.RenderTransform is TranslateTransform ct)
                {
                    chips.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
                    ct.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                }
            }, 9.5, Lighten(NoteAcc, 0.2)));
        tools.Children.Add(IconButton(n.Pinned ? "" : "", Loc.T(n.Pinned ? "Unpin" : "Pin to the top"), () => PinNote(n), 9.5));
        tools.Children.Add(IconButton("", Loc.T("Copy the note"), () =>
        {
            string text = (n.Title.Trim().Length > 0 ? n.Title.Trim() + "\n\n" : "") + n.Body;
            CopyQuiet(text);
            FeelSound(Sfx.Cue.Copy);
            if (_noteWhen != null) { _noteWhen.Text = Loc.T("Copied"); After(1400, UpdateNoteWhen); }
        }, 9.5));
        TextBlock? del = null;
        del = IconButton("", Loc.T("Delete"), () => AskDelete(n, del!), 9.5); // the first click asks, the second deletes
        tools.Children.Add(del);
        foot.Children.Add(tools);
        Grid.SetRow(foot, 4);
        g.Children.Add(foot);
        _noteEditor.Children.Add(card);
        _noteFilling = false;
        UpdateNoteWhen();

        if (animate && !Eco)
        {
            var shift = new TranslateTransform();
            card.RenderTransform = shift;
            card.BeginAnimation(OpacityProperty, new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(280)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
    }

    /// <summary>A borderless box to write in, that takes the keyboard on a click (and gives it back when you leave).</summary>
    TextBox NoteBox(string text, string hint, double size, FontWeight weight, bool multi)
    {
        var box = new TextBox
        {
            Text = text, FontSize = size, FontWeight = weight, FontFamily = NoteFont, Foreground = Frozen(TextTone), Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), Padding = new Thickness(0), FocusVisualStyle = null, Tag = hint,
            CaretBrush = Frozen(_cyber ? CyYellow : Colors.White), SelectionBrush = Frozen(NoteAcc),
            AcceptsReturn = multi, AcceptsTab = false, TextWrapping = multi ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalScrollBarVisibility = multi ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Disabled, VerticalContentAlignment = VerticalAlignment.Top,
        };
        if (!multi) box.MaxLength = 120;
        box.PreviewMouseLeftButtonDown += (_, _) => { _maybeSwipe = false; BeginWorkTyping(box); };
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; FlushNote(); EndWorkTyping(giveBack: true); }
            else if (multi && e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && ContinueList(box)) e.Handled = true;
            else if (!multi && e.Key == Key.Enter) { e.Handled = true; if (_noteBody != null) { _noteBody.Focus(); _noteBody.CaretIndex = _noteBody.Text.Length; } }
        };
        box.TextChanged += (_, _) => { if (!_noteFilling) NoteEdited(); };
        return box;
    }

    /// <summary>The box with its grey hint while empty.</summary>
    Grid Ghosted(TextBox box)
    {
        var host = new Grid();
        var ghost = new TextBlock { Text = (string)box.Tag, FontSize = box.FontSize, FontWeight = box.FontWeight, FontFamily = box.FontFamily, Foreground = Frozen(A(TextTone, 0x55)), IsHitTestVisible = false };
        void Show() => ghost.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        box.TextChanged += (_, _) => Show();
        Show();
        host.Children.Add(box);
        host.Children.Add(ghost);
        return host;
    }

    /// <summary>Typed into: the note and its row follow at once; it's saved half a second after you stop.</summary>
    void NoteEdited()
    {
        var n = Notes.FirstOrDefault(x => x.Id == _noteOpen);
        if (n == null || _noteTitle == null || _noteBody == null) return;
        n.Title = _noteTitle.Text;
        n.Body = _noteBody.Text;
        n.Updated = DateTime.Now;
        if (_noteRows.TryGetValue(n.Id, out var row)) { row.Title.Text = TitleOf(n); row.Snippet.Text = SnippetOf(n); }
        UpdateNoteWhen();
        MaybeAutoTitle(n);
        _noteSave ??= NewNoteSave();
        _noteSave.Stop();
        _noteSave.Start();
    }

    System.Windows.Threading.DispatcherTimer NewNoteSave()
    {
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        t.Tick += (_, _) => { t.Stop(); SaveNotes(); };
        return t;
    }

    void SaveNotes()
    {
        // a note left completely empty isn't kept
        _notes?.RemoveAll(x => x.Id != _noteOpen && x.Title.Trim().Length == 0 && x.Body.Trim().Length == 0);
        if (_notes != null && !NotesDemo) NotesStore.Save(_notes);
    }

    /// <summary>Anything typed and not yet saved is saved now (leaving the note, the page, or the notch).</summary>
    void FlushNote()
    {
        if (_noteSave?.IsEnabled == true) { _noteSave.Stop(); SaveNotes(); }
    }

    void UpdateNoteWhen()
    {
        if (_noteWhen == null) return;
        var n = Notes.FirstOrDefault(x => x.Id == _noteOpen);
        if (n == null) return;
        int words = n.Body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        _noteWhen.Text = (n.Updated > DateTime.Now.AddSeconds(-20) ? Loc.T("Saved") : Loc.T("Edited") + " " + Ago(n.Updated)) + (words > 0 ? $"  ·  {words} {Loc.T(words == 1 ? "word" : "words")}" : "");
    }

    void UpdateNoteCount()
    {
        if (_noteCount != null) _noteCount.Text = Notes.Count == 1 ? Loc.T("1 note") : string.Format(Loc.T("{0} notes"), Notes.Count);
    }

    /// <summary>A new, empty note at the top of the list, open, its title ready to type.</summary>
    void NewNote()
    {
        FlushNote();
        var n = new Note();
        Notes.Add(n);
        _noteOpen = n.Id;
        FeelSound(Sfx.Cue.Pin);
        SearchFill(); // the list draws again with it on top (pinned ones stay above)
        if (_noteRows.TryGetValue(n.Id, out var row) && !Eco && row.Card.RenderTransform is TranslateTransform tt)
        {
            row.Card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)));
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-10, 0, TimeSpan.FromMilliseconds(380)) { EasingFunction = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut } });
        }
        if (_noteTitle is { } box) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => BeginWorkTyping(box));
    }

    void PinNote(Note n)
    {
        FlushNote();
        n.Pinned = !n.Pinned;
        FeelSound(n.Pinned ? Sfx.Cue.Pin : Sfx.Cue.Unpin);
        SaveNotes();
        SearchFill();
    }

    /// <summary>Delete: the first click asks ("Sure?", red), the second (not a double-click) deletes; it goes after 3 s.</summary>
    void AskDelete(Note n, TextBlock btn)
    {
        var now = DateTime.UtcNow;
        if (!_noteAsking)
        {
            _noteAsking = true;
            _noteAskAt = now;
            btn.Text = Loc.T("Sure?");
            btn.FontFamily = NoteFont;
            btn.FontWeight = FontWeights.SemiBold;
            btn.Foreground = Frozen(Color.FromRgb(0xFF, 0x6B, 0x6B));
            FeelSound(Sfx.Cue.Tap);
            After(3000, () => { if (_noteAsking && _noteAskAt == now && _noteOpen == n.Id) FillEditor(animate: false); });
            return;
        }
        if ((now - _noteAskAt).TotalMilliseconds < System.Windows.Forms.SystemInformation.DoubleClickTime) return; // a double-click isn't a yes
        _noteAsking = false;
        DeleteNote(n);
    }

    void DeleteNote(Note n)
    {
        if (_noteSave?.IsEnabled == true) _noteSave.Stop();
        FeelSound(Sfx.Cue.Clear);
        void Gone()
        {
            Notes.Remove(n);
            _noteOpen = null;
            if (!NotesDemo) NotesStore.Save(Notes);
            SearchFill();
        }
        if (Eco || !_noteRows.TryGetValue(n.Id, out var row)) { Gone(); return; }
        // the row folds away first, then the list closes up
        row.Card.IsHitTestVisible = false;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180));
        fade.Completed += (_, _) => Gone();
        row.Card.BeginAnimation(OpacityProperty, fade);
        if (row.Card.RenderTransform is TranslateTransform tt) tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-14, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
        _noteEditor?.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, TimeSpan.FromMilliseconds(160)) { FillBehavior = FillBehavior.Stop });
    }
}
