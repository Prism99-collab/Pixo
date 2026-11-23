using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// Notifs &amp; alerts, the Flow page's last tab: everything the notch caught from the PC (every app's notifications) and
/// its own alerts worth looking back on (a download, a reminder, low battery…), kept two weeks on this PC only.
///  • All: by day, a sender's run as one line (×N), searchable; open one to read it all, reply, copy, quiet its app;
///  • For you: people waiting on an answer (a reply ready to draft, done, later), and "Catch me up";
///  • People, Apps, Alerts: just those;
///  • Manage: calm alerts on or off, and how each app comes in: Pop up, Quiet (kept here, Pixo only glances), or Off.
/// </summary>
public partial class NotchWindow
{
    (string Md, DateTime At, int Count)? _catchUp;

    /// <summary>A person's (or an app's) notifications in a row, shown as one line with how many.</summary>
    sealed record NotifGroup(string App, string Title, List<NotifHistory.Entry> Items)
    {
        public NotifHistory.Entry Latest => Items[0];
        public string Key => App + "|" + Title + "|" + Items[^1].At.Ticks;
    }

    static List<NotifGroup> Grouped(IEnumerable<NotifHistory.Entry> newestFirst)
    {
        var groups = new List<NotifGroup>();
        foreach (var e in newestFirst)
        {
            var last = groups.LastOrDefault();
            // the same sender, the same day, within 2 hours of the one after it: one group
            if (last != null && last.App == e.App && last.Title == e.Title && last.Items[^1].At.Date == e.At.Date && last.Items[^1].At - e.At < TimeSpan.FromHours(2)) last.Items.Add(e);
            else groups.Add(new NotifGroup(e.App, e.Title, new List<NotifHistory.Entry> { e }));
        }
        return groups;
    }

    UIElement BuildNotifsTab()
    {
        var stack = new StackPanel();
        var all = NotifHistory.Recent();
        var waiting = NotifHistory.Waiting();
        int people = all.Count(NotifHistory.FromPerson), alerts = all.Count(NotifHistory.IsAlert), apps = all.Count - people - alerts;
        int today = all.Count(n => n.At.Date == DateTime.Today);
        FrameworkElement? clearAll = null;
        if (all.Count > 0 && _inboxView != "manage")
        {
            bool sure = _confirmDelete == "history";
            clearAll = sure
                ? WorkChip("Clear all?", () =>
                  {
                      if (ConfirmTooSoon) return;
                      _confirmDelete = null;
                      NotifHistory.Clear();
                      _catchUp = null;
                      FeelIt(Feel.EatAll);
                      WorkSay("History cleared");
                      RenderWorkTab(animate: false);
                  }, "Delete every kept notification and alert (they're only on this PC)", strong: true).Also(c => { c.Margin = new Thickness(0); c.Background = Frozen(Color.FromRgb(0xE5, 0x48, 0x4D)); })
                : IconButton("", "Clear all (asks once)", () => AskToConfirm("history"), 11);
        }
        // on the right: Manage (a gear: calm alerts, each app's way in), and clearing everything
        bool managing = _inboxView == "manage";
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(IconButton("", managing ? "Back to the list" : "Manage: calm alerts, and how each app comes in", () =>
        {
            _inboxView = managing ? "all" : "manage"; _inboxApp = ""; _inboxFind = "";
            RenderWorkTab(animate: false);
        }, 11, managing ? WorkAcc : null).Also(b => b.Margin = new Thickness(0, 0, clearAll != null ? 10 : 0, 0)));
        if (clearAll != null) right.Children.Add(clearAll);
        WorkHead(today > 0 ? $"{today} today" : all.Count > 0 ? $"{all.Count} kept" : "", right);

        // the views (an app's own, when one is picked), and how they come in
        var filters = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        void View(string id, string label, string tip) => filters.Children.Add(FilterChip(label, _inboxView == id && _inboxApp.Length == 0, () => { _inboxView = id; _inboxApp = ""; _inboxFind = ""; RenderWorkTab(animate: false); }).Also(c => c.ToolTip = tip));
        View("all", "All", "Everything of the last two weeks, by day");
        View("you", waiting.Count > 0 ? $"For you · {waiting.Count}" : "For you", "People who asked you something and haven't had an answer, and a catch-up");
        View("people", $"People · {people}", "Messages and mail");
        View("apps", $"Apps · {apps}", "What apps and Windows told you");
        if (alerts > 0 || _inboxView == "alerts") View("alerts", $"Alerts · {alerts}", "What Pixo told you: downloads, reminders, battery…");
        if (_inboxApp.Length > 0) filters.Children.Add(FilterChip(_inboxApp + " ×", true, () => { _inboxApp = ""; RenderWorkTab(animate: false); }));
        stack.Children.Add(filters);

        if (_inboxView == "manage") { stack.Children.Add(ManageNotifs()); return stack; }
        if (_inboxView == "you" && _inboxApp.Length == 0)
        {
            if (waiting.Count == 0)
                stack.Children.Add(WorkEmpty(Controls.NotchFace.Emote.Love, "All caught up",
                    "Nobody's waiting on you. When someone asks you something in a chat or a mail, it lands here with a reply ready to draft."));
            int i = 0;
            foreach (var n in waiting) stack.Children.Add(Arriving(WaitingCard(n), i++));
            // the catch-up: what happened, in a few lines (written once, kept until new ones come)
            if (all.Count > 0) stack.Children.Add(CatchUpCard(all));
            return stack;
        }

        // what came in quietly, and who's waiting: one line, above the list
        if (_inboxView == "all" && _inboxApp.Length == 0 && _inboxFind.Length == 0 && NotifsLine(waiting.Count) is { } said) stack.Children.Add(said);

        var find = WorkBox(_cyber ? "SEARCH NOTIFS…" : "Search the last two weeks…", q => { _inboxFind = q; RenderWorkTab(animate: false); }, _inboxFind);
        find.TextChanged += (_, _) => { if (find.Text.Length == 0 && _inboxFind.Length > 0) { _inboxFind = ""; RenderWorkTab(animate: false); } };
        stack.Children.Add(BoxHost(find).Also(g => g.Margin = new Thickness(0, 0, 0, 4)));

        var list = all.Where(n => _inboxApp.Length > 0 ? n.App == _inboxApp : _inboxView switch
            {
                "people" => NotifHistory.FromPerson(n),
                "apps" => !NotifHistory.FromPerson(n) && !NotifHistory.IsAlert(n),
                "alerts" => NotifHistory.IsAlert(n),
                _ => true,
            })
            .Where(n => _inboxFind.Length == 0 || (n.App + " " + n.Title + " " + n.Body).Contains(_inboxFind, StringComparison.CurrentCultureIgnoreCase)).Take(200).ToList();
        if (list.Count == 0)
        {
            stack.Children.Add(_inboxFind.Length > 0
                ? WorkEmpty(Controls.NotchFace.Emote.Confused, "Nothing matches", $"Nothing with “{_inboxFind}” in the last two weeks.")
                : WorkEmpty(null, "Nothing here yet", "Notifications and alerts are kept for two weeks, on this PC only."));
        }
        else
            foreach (var day in Grouped(list).GroupBy(g => g.Latest.At.Date))
            {
                string label = day.Key == DateTime.Today ? "Today" : day.Key == DateTime.Today.AddDays(-1) ? "Yesterday" : day.Key.ToString("dddd d MMMM");
                var dayHead = new Grid { Margin = new Thickness(2, 4, 0, 4) };
                dayHead.Children.Add(WorkText(Cap(label) + $"  ·  {day.Sum(g => g.Items.Count)}", 10, _cyber ? CyYellow : A(TextTone, 0x90), bold: true, wrap: false));
                var dayList = day.SelectMany(g => g.Items).ToList();
                string dayId = "day:" + day.Key.ToString("yyyyMMdd");
                dayHead.Children.Add(SmallButtons((_confirmDelete == dayId ? "Sure? Clear " + label.ToLowerInvariant() : "Clear day", () =>
                {
                    if (_confirmDelete != dayId) { AskToConfirm(dayId); return; }
                    if (ConfirmTooSoon) return;
                    _confirmDelete = null;
                    var ids = dayList.Select(e => e.Id).ToHashSet();
                    NotifHistory.RemoveWhere(e => ids.Contains(e.Id));
                    WorkSay($"Cleared {label.ToLowerInvariant()}");
                    RenderWorkTab(animate: false);
                })).Also(b => b.Margin = new Thickness(0)));
                stack.Children.Add(dayHead);
                foreach (var g in day) stack.Children.Add(NotifRow(g));
            }
        return stack;
    }

    /// <summary>
    /// One quiet line by Pixo above the list: how many came in quietly today (no pop-up, it only glanced) and who's waiting
    /// on you (a click shows them). Nothing to say: no line.
    /// </summary>
    FrameworkElement? NotifsLine(int waiting)
    {
        int quiet = QuietToday;
        if (quiet == 0 && waiting == 0) return null;
        var parts = new List<string>();
        if (quiet > 0) parts.Add(quiet == 1 ? "1 came in quietly today" : $"{quiet} came in quietly today");
        if (waiting > 0) parts.Add(waiting == 1 ? "1 person waiting on you ›" : $"{waiting} people waiting on you ›");
        var line = WorkText(string.Join("  ·  ", parts), 11, _cyber ? CyCyan : Lighten(WorkAcc, 0.35), wrap: false).Also(t =>
        {
            t.Margin = new Thickness(2, 0, 0, 4);
            t.TextTrimming = TextTrimming.CharacterEllipsis;
        });
        if (waiting > 0)
        {
            line.Cursor = Cursors.Hand;
            line.ToolTip = "Show who's waiting";
            line.MouseLeftButtonUp += (_, e) => { e.Handled = true; _inboxView = "you"; RenderWorkTab(animate: false); };
        }
        return line;
    }

    /// <summary>An app's way in, set from the page: said, felt, and the page drawn again.</summary>
    void SetAppMode(string app, string mode)
    {
        if (AppAlertMode(app) == mode) return;
        SetAppAlertMode(app, mode);
        WorkSay(mode switch { "quiet" => $"{app}: quiet", "off" => $"{app}: off", _ => $"{app}: pops up" });
        FeelIt(mode == "show" ? Feel.On : Feel.Hush);
        if (mode == "off" && _inboxApp == app) _inboxApp = "";
        RenderWorkTab(animate: false);
    }

    /// <summary>Manage: calm alerts on or off, and each app's way in (Pop up, Quiet, Off), the busiest first.</summary>
    UIElement ManageNotifs()
    {
        var box = new StackPanel();
        // calm alerts
        var calm = new Grid();
        calm.ColumnDefinitions.Add(new ColumnDefinition());
        calm.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        words.Children.Add(WorkText(Cap("Calm alerts"), 11.5, _cyber ? CyYellow : Colors.White, bold: true, wrap: false));
        words.Children.Add(WorkText("Shorter pop-ups. Offers and apps flooding you don't pop up: Pixo only glances, and they wait here.", 11, A(TextTone, 0xA8)).Also(t => t.Margin = new Thickness(0, 2, 0, 0)));
        calm.Children.Add(words);
        var flip = FilterChip(_settings.CalmAlerts ? "On" : "Off", _settings.CalmAlerts, () =>
        {
            _settings.CalmAlerts = !_settings.CalmAlerts;
            _settings.Save();
            WorkSay(_settings.CalmAlerts ? "Calm alerts on" : "Calm alerts off");
            FeelIt(_settings.CalmAlerts ? Feel.On : Feel.Off);
            RenderWorkTab(animate: false);
        }).Also(c => { c.VerticalAlignment = VerticalAlignment.Center; c.MinWidth = 44; });
        Grid.SetColumn(flip, 1);
        calm.Children.Add(flip);
        box.Children.Add(WorkCard(calm));

        // each app
        // the ones set to Off or Quiet first (always listed, so they can be set back), then the busiest
        var apps = NotifHistory.Apps();
        foreach (var m in NotifHistory.Muted().Concat(_settings.QuietApps))
            if (!apps.Any(a => a.App.Equals(m, StringComparison.OrdinalIgnoreCase))) apps.Add((m, 0, DateTime.MinValue));
        apps = apps.OrderBy(a => AppAlertMode(a.App) == "show" ? 1 : 0).ThenByDescending(a => a.Count).ToList();
        if (apps.Count == 0)
        {
            box.Children.Add(WorkEmpty(null, "No apps yet", "An app shows up here once it has sent a notification."));
            return box;
        }
        box.Children.Add(WorkText(Cap("Each app"), 10, _cyber ? CyYellow : A(TextTone, 0x90), bold: true, wrap: false).Also(t => t.Margin = new Thickness(2, 6, 0, 1)));
        box.Children.Add(WorkText("Pop up: in the notch.  Quiet: kept here, Pixo only glances.  Off: not shown, not kept.", 10, A(TextTone, 0x80)).Also(t => t.Margin = new Thickness(2, 0, 0, 4)));
        foreach (var (app, count, last) in apps.Where((a, i) => i < 40 || AppAlertMode(a.App) != "show"))
        {
            string mode = AppAlertMode(app);
            var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(AppTile(app));
            var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Opacity = mode == "off" ? 0.55 : 1 };
            name.Children.Add(WorkText(app, 11, Colors.White, bold: true, wrap: false).Also(t => t.TextTrimming = TextTrimming.CharacterEllipsis));
            name.Children.Add(WorkText(count > 0 ? $"{count} kept · last {WorkAgo(last)}" : mode == "off" ? "Off" : "Nothing kept", 10, A(TextTone, 0x80), wrap: false));
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            var modes = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            foreach (var (id, label) in new[] { ("show", "Pop up"), ("quiet", "Quiet"), ("off", "Off") })
            {
                string to = id;
                modes.Children.Add(FilterChip(label, mode == id, () => SetAppMode(app, to)).Also(c => c.Margin = new Thickness(4, 0, 0, 0)));
            }
            Grid.SetColumn(modes, 2);
            row.Children.Add(modes);
            box.Children.Add(row);
        }
        return box;
    }

    /// <summary>
    /// "Catch me up": the AI reads today's notifications and says what matters (who needs an answer, what happened, what
    /// can wait), in a few lines. Kept until new notifications come in.
    /// </summary>
    Border CatchUpCard(List<NotifHistory.Entry> all)
    {
        var today = all.Where(n => n.At > DateTime.Now.AddHours(-18)).ToList();
        var body = new StackPanel();
        var head = new Grid();
        head.Children.Add(WorkText(Cap("Catch me up"), 11.5, _cyber ? CyYellow : Colors.White, bold: true, wrap: false));
        head.Children.Add(WorkText(today.Count == 0 ? "nothing new" : $"{today.Count} since this morning", 10, A(TextTone, 0x80), wrap: false).Also(t => t.HorizontalAlignment = HorizontalAlignment.Right));
        body.Children.Add(head);
        bool fresh = _catchUp is { } c && c.Count == today.Count && DateTime.Now - c.At < TimeSpan.FromHours(6);
        if (!fresh)
            body.Children.Add(WorkText(today.Count == 0 ? "Nothing to catch up on." : "What matters in your notifications, in a few lines: who's waiting on you, what happened, what can wait.", 11, A(TextTone, 0xA8)).Also(t => t.Margin = new Thickness(0, 2, 0, 4)));
        if (today.Count > 0)
        {
            var acts = new WrapPanel();
            acts.Children.Add(WorkChip(fresh ? "Again" : "Catch me up", () =>
            {
                string list = string.Join("\n", today.Take(80).Select(n => $"{n.At:HH:mm} · {n.App} · {n.Title}: {(n.Body.Length > 160 ? n.Body[..160] + "…" : n.Body)}"));
                AskOnCard(body, "Catch me up", "Catch me up on these notifications: first who's waiting on an answer from me (name, what they want), then what happened that I should know, then one line on what can be ignored. Short bullets, no filler.",
                    _ => Task.FromResult("THE USER'S NOTIFICATIONS SINCE THIS MORNING (newest first):\n" + list), a => _catchUp = (a, DateTime.Now, today.Count), () => _catchUp = null);
            }, "A short summary of what you missed", strong: !fresh));
            body.Children.Add(acts);
        }
        if (fresh && _catchUp is { } kept) body.Children.Add(AnswerBlock("Catch me up", kept.Md, () => _catchUp = null));
        return WorkCard(body, hot: !fresh && today.Count > 0);
    }

    Border FilterChip(string label, bool on, Action act)
    {
        var c = WorkChip(label, act, strong: on);
        c.Padding = new Thickness(8, 2, 8, 3);
        c.MinHeight = 22;
        return c;
    }

    /// <summary>A letter tile for an app, in its brand colour (else one of its own); the notch's own alerts: a bell.</summary>
    FrameworkElement AppTile(string app)
    {
        bool alert = app == NotifHistory.PixoApp;
        int h = 0;
        foreach (char ch in app.ToLowerInvariant()) h = h * 31 + ch; // (the same colour every run)
        h = Math.Abs(h % 1000);
        var palette = new[] { Color.FromRgb(0x58, 0x65, 0xF2), Color.FromRgb(0x25, 0xD3, 0x66), Color.FromRgb(0xFF, 0x8A, 0x4C), Color.FromRgb(0xE5, 0x4B, 0x8F), Color.FromRgb(0x2E, 0xA8, 0xFF), Color.FromRgb(0xF2, 0xC1, 0x4E) };
        var c = _cyber ? (alert || h % 2 == 1 ? CyYellow : CyCyan) : alert ? Color.FromRgb(0xFF, 0xB0, 0x20) : BrandColor(app) ?? palette[h % palette.Length];
        return new Border
        {
            Width = 18, Height = 18, CornerRadius = new CornerRadius(_cyber ? 0 : 5), Background = Frozen(A(c, _cyber ? (byte)0x22 : (byte)0xD0)), BorderBrush = Frozen(_cyber ? A(c, 0xB0) : Colors.Transparent), BorderThickness = new Thickness(_cyber ? 1 : 0),
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 8, 0),
            Child = alert
                ? new TextBlock { Text = G.Bell, FontSize = 10, Foreground = Frozen(_cyber ? c : Colors.White), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontFamily = (FontFamily)FindResource("IconFont") }
                : new TextBlock { Text = app.Length > 0 ? app[..1].ToUpperInvariant() : "?", FontSize = 10, FontWeight = _cyber ? FontWeights.Bold : FontWeights.SemiBold, Foreground = Frozen(_cyber ? c : Colors.White), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontFamily = WorkFont },
        };
    }

    Border WaitingCard(NotifHistory.Entry n)
    {
        var body = new StackPanel();
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(AppTile(n.App));
        var words = new StackPanel();
        var top = new Grid();
        top.Children.Add(WorkText(n.Title, 11.5, _cyber ? CyYellow : Colors.White, bold: true, wrap: false).Also(t => t.Margin = new Thickness(0, 0, 110, 0)));
        top.Children.Add(WorkText($"{n.App} · {WorkAgo(n.At)}", 10, A(TextTone, 0x80), wrap: false).Also(t => t.HorizontalAlignment = HorizontalAlignment.Right));
        words.Children.Add(top);
        if (n.Body.Length > 0) words.Children.Add(WorkText(n.Body.Length > 220 ? n.Body[..220] + "…" : n.Body, 11, A(TextTone, 0xD0)).Also(t => t.MaxHeight = 46));
        Grid.SetColumn(words, 1);
        row.Children.Add(words);
        body.Children.Add(row);
        body.Children.Add(ReplyArea(n, "wait|" + n.Id, n.Body).Also(r => r.Margin = new Thickness(26, 6, 0, 0)));
        var acts = new WrapPanel { Margin = new Thickness(26, 4, 0, 0) };
        acts.Children.Add(WorkChip("Open " + n.App, async () => await OpenNotifApp(n.App), "Go to the conversation's app"));
        acts.Children.Add(WorkChip("✓ Done", () => { NotifHistory.Done(n); WorkSay("Done"); RenderWorkTab(animate: false); }, "Answered: take it off the list"));
        acts.Children.Add(WorkChip("Later", () => { NotifHistory.Snooze(n, TimeSpan.FromHours(3)); WorkSay("Back in 3 hours"); RenderWorkTab(animate: false); }, "Hide it for 3 hours"));
        body.Children.Add(acts);
        return WorkCard(body, hot: DateTime.Now - n.At < TimeSpan.FromHours(1));
    }

    /// <summary>The app a notification came from, in front (open already: its window; else started).</summary>
    async Task OpenNotifApp(string app)
    {
        var hwnd = FindUiWindow(app.ToLowerInvariant());
        if (hwnd != IntPtr.Zero && await Front(hwnd)) { WorkSay(app); return; }
        try { WorkSay((await ToolDo("open " + app)).StartsWith("Error") ? "Couldn't open " + app : app); } catch { WorkSay("Couldn't open " + app); }
    }

    /// <summary>
    /// Reply without leaving the notch: three replies that fit (from the AI, one click puts one in the bar to send or
    /// change), and a bar to write your own; Enter sends it in the app it came from (NotchWindow.ChatSend).
    /// </summary>
    FrameworkElement ReplyArea(NotifHistory.Entry n, string key, string messages)
    {
        var sense = Sense(new ToastInfo(n.App, null, n.Title, n.Body, null), n.Title, n.Body);
        string who = sense.Group ?? sense.Sender;
        var area = new StackPanel { Margin = new Thickness(0, 4, 0, 2) };
        var ideas = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4), ClipToBounds = true }; // one row: what doesn't fit stays out
        area.Children.Add(ideas);
        TextBox? box = null;
        box = WorkBox(string.Format("Reply to {0} on {1}…", who, n.App), text => _ = SendReplyAsync(n.App, who, text));
        area.Children.Add(BoxHost(box));
        // the ideas: a soft "thinking" chip, then the three replies sliding in
        var wait = WorkText("Ideas…", 10, A(TextTone, 0x70), wrap: false).Also(t => t.Margin = new Thickness(2, 2, 0, 2));
        ideas.Children.Add(wait);
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _ = Fill();
        async Task Fill()
        {
            var list = await ReplyIdeasAsync(key, n.App, who, messages, cts.Token);
            ideas.Children.Clear();
            int i = 0;
            foreach (var idea in list)
            {
                string t = idea;
                var chip = WorkChip(t, () => { box!.Text = t; BeginWorkTyping(box); box.CaretIndex = box.Text.Length; }, "Put it in the bar (Enter sends it)");
                ideas.Children.Add(chip);
                if (!Eco)
                {
                    chip.Opacity = 0;
                    chip.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { BeginTime = TimeSpan.FromMilliseconds(60 * i++) });
                }
            }
            if (list.Count == 0) ideas.Visibility = Visibility.Collapsed;
        }
        return area;
    }

    async Task SendReplyAsync(string app, string who, string text)
    {
        Collapse(); // the app comes up where the notch was
        await Task.Delay(220);
        var r = await SendToChatAsync(app, who, text);
        SaySent(r, app, who);
    }

    void DraftReply(StackPanel host, NotifHistory.Entry n)
    {
        string question = "Write a short, natural reply to this message, in its language and tone. Only the reply text, nothing around it.";
        AskOnCard(host, "Reply to " + n.Title, question, _ => Task.FromResult($"A message on {n.App} from {n.Title}, at {n.At:HH:mm}:\n{n.Body}"));
    }

    /// <summary>One sender's notifications in a row (×N): click to open them, × to remove; ⋯ to mute or clear the app.</summary>
    Border NotifRow(NotifGroup g)
    {
        var n = g.Latest;
        bool open = _inboxOpen.Contains(g.Key);
        var row = new Grid { Background = Brushes.Transparent, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(AppTile(n.App));
        var words = new StackPanel();
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleLine = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 8, 0) };
        if (g.Items.Count > 1)
        {
            var count = new Border
            {
                Child = WorkText("×" + g.Items.Count, 10, _cyber ? Colors.Black : Colors.White, bold: true, wrap: false), Background = Frozen(_cyber ? CyCyan : A(WorkAcc, 0xB0)),
                CornerRadius = new CornerRadius(_cyber ? 0 : 6), Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(4, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            DockPanel.SetDock(count, Dock.Right);
            titleLine.Children.Add(count);
        }
        titleLine.Children.Add(WorkText(n.Title.Length > 0 ? n.Title : n.App, 11, Colors.White, bold: true, wrap: false).Also(t => { t.TextTrimming = TextTrimming.CharacterEllipsis; t.ToolTip = n.Title; }));
        top.Children.Add(titleLine);
        var side = new StackPanel { Orientation = Orientation.Horizontal };
        Grid.SetColumn(side, 1);
        side.Children.Add(WorkText($"{n.App} · {n.At:HH:mm}", 10, A(TextTone, 0x80), wrap: false).Also(t => { t.MaxWidth = 92; t.TextTrimming = TextTrimming.CharacterEllipsis; t.VerticalAlignment = VerticalAlignment.Center; }));
        side.Children.Add(IconButton("", "Remove " + (g.Items.Count > 1 ? $"these {g.Items.Count}" : "it"), () => { var ids = g.Items.Select(e => e.Id).ToHashSet(); NotifHistory.RemoveWhere(e => ids.Contains(e.Id)); WorkSay("Removed"); RenderWorkTab(animate: false); }, 8));
        top.Children.Add(side);
        words.Children.Add(top);
        if (!open)
        {
            if (n.Body.Length > 0) words.Children.Add(WorkText(n.Body, 11, A(TextTone, 0xC0), wrap: false).Also(t => t.TextTrimming = TextTrimming.CharacterEllipsis));
        }
        else
        {
            foreach (var e in g.Items.Take(12))
                words.Children.Add(WorkText((g.Items.Count > 1 ? $"{e.At:HH:mm}  " : "") + (e.Body.Length > 0 ? e.Body : "(no text)"), 11, A(TextTone, 0xD0)).Also(t => t.Margin = new Thickness(0, 1, 0, 1)));
            if (NotifHistory.FromPerson(n)) words.Children.Add(ReplyArea(n, g.Key, string.Join("\n", g.Items.Take(6).Reverse().Select(e => e.Body))));
            var acts = new WrapPanel { Margin = new Thickness(0, 4, 0, 2) };
            acts.Children.Add(WorkChip("Copy", () => { CopyText(string.Join("\n", g.Items.Select(e => $"{e.Title}: {e.Body}")), "Copied"); WorkSay("Copied"); }));
            if (!NotifHistory.IsAlert(n))
            {
                acts.Children.Add(WorkChip("Open " + n.App, async () => await OpenNotifApp(n.App)));
                acts.Children.Add(WorkChip("Only " + n.App, () => { _inboxApp = n.App; RenderWorkTab(animate: false); }, "Show only this app's notifications"));
                bool quiet = AppQuiet(n.App);
                acts.Children.Add(WorkChip(quiet ? "Let it pop up" : "Quiet " + n.App, () => SetAppMode(n.App, quiet ? "show" : "quiet"),
                    quiet ? "Its notifications pop up in the notch again" : "No pop-ups from it: Pixo only glances, and it's kept here"));
                acts.Children.Add(WorkChip("Turn off", () => SetAppMode(n.App, "off"), "Neither shown nor kept (the ones kept go too)"));
            }
            words.Children.Add(acts);
        }
        Grid.SetColumn(words, 1);
        row.Children.Add(words);
        var card = new Border
        {
            Child = row, Padding = new Thickness(6, 4, 6, 5), Margin = new Thickness(0, 0, 0, 4), CornerRadius = new CornerRadius(_cyber ? 0 : 8),
            Background = Frozen(open ? (_cyber ? Color.FromArgb(0x70, 0x00, 0x00, 0x00) : Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)) : Colors.Transparent),
            BorderBrush = Frozen(_cyber ? A(NotifHistory.FromPerson(n) ? CyYellow : CyCyan, open ? (byte)0x70 : (byte)0x28) : Color.FromArgb(open ? (byte)0x20 : (byte)0x00, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(_cyber ? 2 : 1, 0, 0, _cyber ? 0 : 1),
        };
        card.MouseEnter += (_, _) => { if (!open) card.Background = Frozen(_cyber ? A(CyCyan, 0x10) : Color.FromArgb(0x0E, 0xFF, 0xFF, 0xFF)); };
        card.MouseLeave += (_, _) => { if (!open) card.Background = Brushes.Transparent; };
        card.PreviewMouseLeftButtonDown += (_, _) => _maybeSwipe = false;
        card.MouseLeftButtonUp += (_, e) => { if (e.Handled) return; e.Handled = true; if (!_inboxOpen.Remove(g.Key)) _inboxOpen.Add(g.Key); RenderWorkTab(animate: false); };
        return card;
    }
}
