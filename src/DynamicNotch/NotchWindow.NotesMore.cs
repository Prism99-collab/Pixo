using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// More for the notes:
///  • Checklists: a line starting with "[ ]" (or "[]") is a box; in the reading view it's a real box to tick, and the
///    note's row says how far along it is ("2/5 done").
///  • Remind me: a day or a time in the note ("tomorrow 5pm", "Friday at 10", "at 17:30") puts a small chip by the
///    title; a click sets a Pixo reminder for it (a second click takes it back).
///  • Quick capture: "note: buy milk" in the search saves a note without opening anything.
///  • Notes in the search: what you type finds your notes too; Enter opens the note.
///  • Undo: a deleted note can come back for a few seconds.
/// </summary>
public partial class NotchWindow
{
    // ───────────── checklists ─────────────

    static readonly Regex CheckLine = new(@"^(\s*)\[( |x|X)?\]\s?(.*)$", RegexOptions.Compiled);

    static (int Done, int All) Checks(string body)
    {
        int done = 0, all = 0;
        foreach (var line in body.Split('\n'))
        {
            var m = CheckLine.Match(line.TrimEnd('\r'));
            if (!m.Success) continue;
            all++;
            if (m.Groups[2].Value is "x" or "X") done++;
        }
        return (done, all);
    }

    static bool HasChecks(string body) => Checks(body).All > 0;

    /// <summary>A box to tick in the reading view: a click flips "[ ]" and "[x]" on that line of the note.</summary>
    InlineUIContainer CheckBoxFor(TextBox box, int lineIndex, bool done)
    {
        var acc = NoteAcc;
        var mark = new TextBlock { Text = done ? "" : "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 8, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var b = new Border
        {
            Width = 12, Height = 12, CornerRadius = new CornerRadius(_cyber ? 0 : 3.5), BorderThickness = new Thickness(1.2), Margin = new Thickness(0, 0, 6, -2), Cursor = Cursors.Hand,
            Background = Frozen(done ? A(acc, 0xD0) : Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)), BorderBrush = Frozen(done ? A(acc, 0xFF) : A(TextTone, 0x90)), Child = mark,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(),
        };
        b.PreviewMouseLeftButtonDown += (_, e) => { e.Handled = true; _maybeSwipe = false; };
        b.PreviewMouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            var lines = box.Text.Split('\n');
            if (lineIndex >= lines.Length) return;
            var m = CheckLine.Match(lines[lineIndex].TrimEnd('\r'));
            if (!m.Success) return;
            bool now = !(m.Groups[2].Value is "x" or "X");
            lines[lineIndex] = m.Groups[1].Value + (now ? "[x] " : "[ ] ") + m.Groups[3].Value;
            FeelSound(now ? Sfx.Cue.Done : Sfx.Cue.Tap);
            box.Text = string.Join("\n", lines); // (saved like any edit; the reading view draws again)
            _noteRead?.Invoke();
        };
        return new InlineUIContainer(b) { BaselineAlignment = BaselineAlignment.Center };
    }

    Action? _noteRead;

    /// <summary>The reading view's text: boxes for checklist lines (ticked ones dimmed and struck through), links clickable.</summary>
    void FillReader(TextBlock reader, TextBox box, Action<InlineCollection, string> addLinks)
    {
        reader.Inlines.Clear();
        var lines = box.Text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r');
            var m = CheckLine.Match(line);
            if (m.Success)
            {
                bool done = m.Groups[2].Value is "x" or "X";
                reader.Inlines.Add(new Run(m.Groups[1].Value));
                reader.Inlines.Add(CheckBoxFor(box, i, done));
                var span = new Span();
                addLinks(span.Inlines, m.Groups[3].Value);
                if (done) { span.Foreground = Frozen(A(TextTone, 0x80)); span.TextDecorations = TextDecorations.Strikethrough; }
                reader.Inlines.Add(span);
            }
            else addLinks(reader.Inlines, line);
            if (i < lines.Length - 1) reader.Inlines.Add(new LineBreak());
        }
    }

    // ───────────── remind me ─────────────

    static readonly Regex WhenLike = new(
        @"\b(?:(?:today|tonight|tomorrow|(?:on |next )?(?:mon|tues|wednes|thurs|fri|satur|sun)day)(?:\s+(?:morning|afternoon|evening|night))?(?:\s+(?:at\s+)?\d{1,2}(?:[:.h]\d{2})?\s*(?:am|pm)?)?|at\s+\d{1,2}(?:[:.h]\d{2})?\s*(?:am|pm)?|\d{1,2}[:h]\d{2}\s*(?:am|pm)?|\d{1,2}\s*(?:am|pm))\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The first day or time in the note that's still ahead, or null.</summary>
    static DateTime? NoteWhen(Note n)
    {
        var now = DateTime.Now;
        foreach (Match m in WhenLike.Matches(n.Title + "\n" + n.Body))
            if (LauncherIntent.When(m.Value, now) is { } at && at > now.AddMinutes(1) && at < now.AddDays(60))
            {
                // a day alone ("tomorrow"): the morning, at 9
                if (at.TimeOfDay == TimeSpan.Zero) at = at.Date.AddHours(9);
                return at;
            }
        return null;
    }

    string ReminderText(Note n) => Loc.T("Note") + ": " + TitleOf(n);

    /// <summary>The small chip by the title: "⏰ Tue 17:00" (a click sets the reminder; set, it's lit and a click takes it back).</summary>
    Border? ReminderChip(Note n)
    {
        if (NoteWhen(n) is not { } at) return null;
        var acc = NoteAcc;
        bool IsSet() => _reminders.Any(r => r.At == at && r.Text == ReminderText(n));
        var text = new TextBlock { FontSize = 9.5, FontFamily = NoteFont, VerticalAlignment = VerticalAlignment.Center };
        var chip = new Border
        {
            CornerRadius = new CornerRadius(_cyber ? 0 : 8), Padding = new Thickness(6, 1, 7, 2), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(1), Cursor = Cursors.Hand, Child = text, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(),
        };
        string When() => at.Date == DateTime.Today ? at.ToString("t") : at.Date == DateTime.Today.AddDays(1) ? Loc.T("Tomorrow") + " " + at.ToString("t") : at.ToString("ddd t");
        void Paint()
        {
            bool on = IsSet();
            text.Text = (on ? "✓ " : "⏰ ") + When();
            text.Foreground = Frozen(on ? Colors.White : Lighten(acc, 0.35));
            chip.Background = Frozen(on ? A(acc, 0x70) : A(acc, 0x22));
            chip.BorderBrush = Frozen(A(acc, on ? (byte)0xB0 : (byte)0x60));
            chip.ToolTip = on ? Loc.T("Reminder set (click to take it back)") : Loc.T("Remind me then");
        }
        Paint();
        chip.PreviewMouseLeftButtonDown += (_, e) => { e.Handled = true; _maybeSwipe = false; };
        chip.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (IsSet())
            {
                _reminders.RemoveAll(r => r.At == at && r.Text == ReminderText(n));
                SaveReminders();
                ScheduleReminders();
                FeelSound(Sfx.Cue.Off);
            }
            else
            {
                AddReminder(ReminderText(n), at);
                FeelSound(Sfx.Cue.Pin);
            }
            Paint();
            if (!Eco && chip.RenderTransform is ScaleTransform st)
            {
                var pop = new DoubleAnimation(1.15, 1, TimeSpan.FromMilliseconds(280)) { EasingFunction = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut } };
                st.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
                st.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
            }
        };
        return chip;
    }

    // ───────────── quick capture and the search ─────────────

    static readonly Regex QuickNoteAsk = new(@"^\s*(?:note|n)\s*:\s*(.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>"note: buy milk" in the search: the first result saves it as a note.</summary>
    bool OfferQuickNote(string q, Action<Hit, double> offer)
    {
        var m = QuickNoteAsk.Match(q);
        if (!m.Success || m.Groups[1].Value.Trim().Length == 0) return false;
        string text = m.Groups[1].Value.Trim();
        offer(new Hit(Loc.T("Save as a note"), text, "", null, () => QuickNote(text), Section: Loc.T("Notes"), Key: "note-new"), 5000);
        return true;
    }

    void QuickNote(string text)
    {
        var n = new Note { Body = text };
        Notes.Add(n);
        if (!NotesDemo) NotesStore.Save(Notes);
        FeelSound(Sfx.Cue.Pin);
        CloseLauncher(giveBack: true);
        After(220, () => ShowOsd("note-saved", "", null, Loc.T("Saved to Notes"), text.Length > 48 ? text[..47] + "…" : text, null,
            glyphBrush: Frozen(NoteAcc), seconds: 2.2, prio: Prio.Low, onClick: () => OpenNote(n.Id)));
    }

    /// <summary>Your notes in the search results (every word you typed in its title or text).</summary>
    void OfferNotes(string find, Action<Hit, double> offer)
    {
        var words = find.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return;
        foreach (var n in Notes)
        {
            string low = (n.Title + "\n" + n.Body).ToLowerInvariant();
            if (!words.All(w => low.Contains(w))) continue;
            string id = n.Id;
            bool inTitle = n.Title.ToLowerInvariant().Contains(words[0]);
            offer(new Hit(TitleOf(n), Loc.T("Note") + " · " + SnippetOf(n), "", null, () => OpenNote(id), Section: Loc.T("Notes"), Highlight: find, Key: "note|" + id),
                (inTitle ? 320 : 270) + (n.Pinned ? 20 : 0));
        }
    }

    /// <summary>Opens the notch on that note (Search page → Notes).</summary>
    void OpenNote(string id)
    {
        if (_launcherOpen) CloseLauncher(giveBack: false);
        FlushNote();
        _noteOpen = id;
        if (_mode != Mode.Expanded) Expand(byHotkey: true);
        GoToPage(SearchPage);
        if (_spTab != 2) SearchTab(2, animate: false); else SearchFill();
    }

    // ───────────── undo a delete ─────────────

    (Note Note, DateTime At)? _noteDeleted;

    /// <summary>"Deleted · Undo" for six seconds where the count was (or under the welcome, if it was the last one).</summary>
    FrameworkElement? UndoDeleteLink()
    {
        if (_noteDeleted is not { } d || (DateTime.UtcNow - d.At).TotalSeconds > 6) return null;
        var t = new TextBlock { FontSize = 10, FontFamily = NoteFont, Foreground = Frozen(A(TextTone, 0x90)), VerticalAlignment = VerticalAlignment.Center };
        t.Inlines.Add(new Run(Loc.T("Deleted") + "  ·  "));
        var link = new Hyperlink(new Run(Loc.T("Undo"))) { Foreground = Frozen(Lighten(NoteAcc, 0.35)), TextDecorations = null, Cursor = Cursors.Hand };
        link.PreviewMouseLeftButtonDown += (_, e) => { e.Handled = true; _maybeSwipe = false; };
        link.PreviewMouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (_noteDeleted is not { } back) return;
            _noteDeleted = null;
            Notes.Add(back.Note);
            _noteOpen = back.Note.Id;
            if (!NotesDemo) NotesStore.Save(Notes);
            FeelSound(Sfx.Cue.On);
            SearchFill();
        };
        t.Inlines.Add(link);
        var at = d.At;
        After(6200, () => { if (_noteDeleted is { } still && still.At == at) { _noteDeleted = null; if (_page == SearchPage && _spTab == 2 && !_workTyping) UpdateNoteCountOrUndo(); } });
        return t;
    }

    void UpdateNoteCountOrUndo()
    {
        if (_noteCount?.Parent is not Grid head) { if (Notes.Count == 0) SearchFill(); return; }
        var old = head.Children.OfType<TextBlock>().FirstOrDefault(x => x.Tag is "undo");
        if (old != null) head.Children.Remove(old);
        _noteCount.Visibility = Visibility.Visible;
        UpdateNoteCount();
    }
}
