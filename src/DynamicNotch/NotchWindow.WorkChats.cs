using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// Chats, the Flow page's second tab: your messages from people (Discord, WhatsApp, Teams, Telegram…) as
/// conversations, newest first, each with its person's initials in their own colour. The newest one is open with its
/// reply bar and three replies that fit; a click opens another. New ones put a count on the tab, and when a message
/// came in since you last looked, the page opens right on Chats. A message's pop-up has a Reply button that lands
/// here, the bar ready to type.
/// </summary>
public partial class NotchWindow
{
    const int ChatsTab = 3;
    /// <summary>The rail's order, top to bottom (tab numbers): Now, Chats, Projects, Inbox.</summary>
    static readonly int[] WorkOrder = { 0, ChatsTab, 1, 2 };
    static int WorkPos(int tab) => Array.IndexOf(WorkOrder, tab) is var p and >= 0 ? p : 0;

    DateTime _chatsSeenAt = DateTime.Now.AddMinutes(-30);
    string? _chatOpenKey;
    TextBox? _chatsBox;
    bool _chatsFocusBox;
    TextBlock? _chatsBadge;

    sealed record ChatThread(string Key, string App, string Who, string? Group, List<NotifHistory.Entry> Items)
    {
        public NotifHistory.Entry Latest => Items[0];
    }

    /// <summary>Messages from people in the last three days, as conversations (one per person or group, per app), newest first.</summary>
    List<ChatThread> ChatThreads()
    {
        var cleared = _settings.ChatsCleared;
        var list = new List<ChatThread>();
        var byKey = new Dictionary<string, ChatThread>();
        var since = DateTime.Now.AddDays(-3);
        foreach (var e in NotifHistory.Recent())
        {
            if (e.At < since) break;
            if (!NotifHistory.FromPerson(e)) continue;
            var s = Sense(new ToastInfo(e.App, null, e.Title, e.Body, null), e.Title, e.Body);
            string place = s.Group ?? s.Sender;
            string key = e.App.ToLowerInvariant() + "|" + place.ToLowerInvariant();
            if (!byKey.TryGetValue(key, out var t))
            {
                t = new ChatThread(key, e.App, s.Sender, s.Group, new List<NotifHistory.Entry>());
                byKey[key] = t;
                list.Add(t);
            }
            if (t.Items.Count < 6) t.Items.Add(e);
        }
        // cleared ones stay away until someone writes again
        list.RemoveAll(t => cleared.TryGetValue(t.Key, out var upTo) && t.Latest.At <= upTo);
        return list;
    }

    int NewChats() => ChatThreads().Count(t => t.Latest.At > _chatsSeenAt);

    FrameworkElement BuildChatsTab()
    {
        _chatsSeenAt = DateTime.Now;
        UpdateChatsBadge();
        var threads = ChatThreads();
        var stack = new StackPanel();
        if (threads.Count == 0)
        {
            stack.Children.Add(WorkText("No messages from people lately. DMs from Discord, WhatsApp, Teams and the others land here, ready to answer.", 11, A(TextTone, 0xA0)).Also(t => t.Margin = new Thickness(2, 6, 8, 0)));
            return stack;
        }
        if (_chatOpenKey == null || _chatOpenKey.Length > 0 && threads.All(t => t.Key != _chatOpenKey)) _chatOpenKey = threads[0].Key;
        // the tab's own actions: fold them all, or clear the list (they come back when someone writes again)
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        tools.Children.Add(IconButton(_chatOpenKey.Length > 0 ? "\uE70E" : "\uE70D", _chatOpenKey.Length > 0 ? "Fold them all" : "Open the newest", () =>
        {
            _chatOpenKey = _chatOpenKey is { Length: > 0 } ? "" : null;
            FeelSound(Sfx.Cue.Tap);
            RenderWorkTab(animate: false);
        }, 10));
        tools.Children.Add(IconButton("\uE74D", "Clear the chats (they come back when someone writes again)", () =>
        {
            foreach (var t in threads) _settings.ChatsCleared[t.Key] = t.Latest.At;
            _settings.Save();
            WorkSay("Cleared");
            FeelSound(Sfx.Cue.Clear);
            RenderWorkTab(animate: false);
        }, 10));
        WorkHead(threads.Count == 1 ? "1 conversation" : $"{threads.Count} conversations", tools);
        _chatsBox = null;
        foreach (var t in threads.Take(12)) stack.Children.Add(ChatCard(t, t.Key == _chatOpenKey));
        // a Reply from a pop-up: the bar takes the keyboard as soon as it's on screen
        // (the bar on screen when it runs: opening the notch can draw this tab twice, and the first bar is gone by then)
        if (_chatsFocusBox && _chatsBox != null)
        {
            _chatsFocusBox = false;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
            {
                if (_chatsBox is not { } box) return;
                BeginWorkTyping(box);
                box.CaretIndex = box.Text.Length;
            });
        }
        return stack;
    }

    FrameworkElement ChatCard(ChatThread t, bool open)
    {
        var n = t.Latest;
        var card = new StackPanel();
        // the head: their initials in their colour (the app's letter in the corner), who, where, when
        var head = new Grid { Background = Brushes.Transparent, Cursor = Cursors.Hand };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var c = PersonColor(t.Who);
        var avatar = new Grid { Width = 26, Height = 26, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Top };
        var fill = new LinearGradientBrush(Lighten(c, 0.18), Darken(c, 0.28), 60);
        fill.Freeze();
        avatar.Children.Add(new Border { CornerRadius = new CornerRadius(_cyber ? 0 : 13), Background = fill,
            Child = new TextBlock { Text = Initials(t.Who), FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
        avatar.Children.Add(new Border
        {
            Width = 12, Height = 12, CornerRadius = new CornerRadius(_cyber ? 0 : 4), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -3, -3),
            Background = Frozen(Color.FromRgb(0x22, 0x22, 0x28)), BorderBrush = Frozen(Color.FromRgb(0x12, 0x12, 0x16)), BorderThickness = new Thickness(1.5),
            Child = new TextBlock { Text = t.App.Length > 0 ? t.App[..1].ToUpperInvariant() : "?", FontSize = 7, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        });
        head.Children.Add(avatar);
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(WorkText(t.Who + (t.Group != null ? "  ·  " + t.Group : ""), 11.5, _cyber ? CyYellow : Colors.White, bold: true, wrap: false).Also(x => x.TextTrimming = TextTrimming.CharacterEllipsis));
        if (!open) names.Children.Add(WorkText(n.Body, 10.5, A(TextTone, 0xB0), wrap: false).Also(x => x.TextTrimming = TextTrimming.CharacterEllipsis));
        Grid.SetColumn(names, 1);
        head.Children.Add(names);
        var side = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6, 1, 0, 0) };
        if (n.At > _chatsSeenAt.AddMinutes(-0.1) && !open || t.Items.Count > 1 && !open)
            side.Children.Add(new Border { CornerRadius = new CornerRadius(_cyber ? 0 : 6), Padding = new Thickness(5, 0, 5, 1), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
                Background = Frozen(A(WorkAcc, 0xB0)), Child = WorkText("×" + t.Items.Count, 8.5, _cyber ? Colors.Black : Colors.White, bold: true, wrap: false) });
        side.Children.Add(WorkText($"{t.App} · {WorkAgo(n.At)}", 9.5, A(TextTone, 0x80), wrap: false));
        side.Children.Add(IconButton("\uE711", "Clear this conversation", () =>
        {
            _settings.ChatsCleared[t.Key] = t.Latest.At;
            _settings.Save();
            FeelSound(Sfx.Cue.Clear);
            RenderWorkTab(animate: false);
        }, 8).Also(x => x.Margin = new Thickness(4, 0, 0, 0)));
        Grid.SetColumn(side, 2);
        head.Children.Add(side);
        head.MouseLeftButtonUp += (_, e) => { e.Handled = true; _chatOpenKey = open ? "" : t.Key; FeelSound(Sfx.Cue.Tap); RenderWorkTab(animate: false); };
        head.PreviewMouseLeftButtonDown += (_, _) => _maybeSwipe = false;
        card.Children.Add(head);

        if (open)
        {
            // the last few messages, oldest first, then the reply area (ideas + the bar)
            // the last three, two lines each at most (the card stays short: its reply bar is always in view)
            var said = new StackPanel { Margin = new Thickness(34, 3, 0, 0) };
            foreach (var e in t.Items.Take(3).Reverse())
                said.Children.Add(WorkText((t.Items.Count > 1 ? $"{e.At:HH:mm}  " : "") + (e.Body.Length > 0 ? e.Body : "(no text)"), 10.5, A(TextTone, 0xD8)).Also(x => { x.Margin = new Thickness(0, 1, 0, 1); x.MaxHeight = 30; x.TextTrimming = TextTrimming.CharacterEllipsis; }));
            card.Children.Add(said);
            var reply = ReplyArea(n, "chat|" + t.Key + "|" + n.Id, string.Join("\n", t.Items.Take(6).Reverse().Select(e => e.Body)));
            reply.Margin = new Thickness(34, 2, 0, 0);
            _chatsBox = FindBox(reply);
            card.Children.Add(reply);
            // (open the app: its letter on the avatar is a link)
            avatar.Cursor = Cursors.Hand;
            avatar.ToolTip = "Open " + t.App;
            avatar.MouseLeftButtonUp += async (_, e) => { e.Handled = true; await OpenNotifApp(t.App); };
        }
        return WorkCard(card, hot: open);
    }

    static TextBox? FindBox(DependencyObject root)
    {
        if (root is TextBox tb) return tb;
        int n = root is Visual ? VisualTreeHelper.GetChildrenCount(root) : 0;
        if (n == 0 && root is Panel p) { foreach (UIElement ch in p.Children) if (FindBox(ch) is { } f) return f; return null; }
        for (int i = 0; i < n; i++) if (FindBox(VisualTreeHelper.GetChild(root, i)) is { } f) return f;
        if (root is Panel p2) foreach (UIElement ch in p2.Children) if (FindBox(ch) is { } f) return f;
        return null;
    }

    void UpdateChatsBadge()
    {
        if (_chatsBadge?.Parent is not Border b) return;
        int fresh = _workTab == ChatsTab && _page == WorkPage && _mode == Mode.Expanded ? 0 : NewChats();
        _chatsBadge.Text = fresh.ToString();
        b.Visibility = fresh > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>From a message's pop-up: the notch opens on Flow → Chats, that conversation open, the bar ready to type.</summary>
    void ReplyInPixo(string app, string place)
    {
        _chatOpenKey = app.ToLowerInvariant() + "|" + place.ToLowerInvariant();
        _chatsFocusBox = true;
        if (_mode != Mode.Expanded) Expand(byHotkey: true);
        GoToPage(WorkPage);
        if (_workTab != ChatsTab) ShowWorkTab(ChatsTab); else RenderWorkTab(animate: false);
    }
}
