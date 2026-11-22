using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// When a command could be done in clearly different ways (which person or app gets a message, delete or move, this
/// file or that one), the AI doesn't guess: it asks, with the few ways Pixo could do it as buttons (its best guess
/// first). One click, a number key (1–4), Enter for the first, or simply typing your own answer; Esc skips. The answer
/// goes straight back to the AI, which carries on in the same turn.
/// </summary>
public partial class NotchWindow
{
    TaskCompletionSource<string>? _choice; // a question with options, waiting for you
    List<string> _choiceOptions = new();
    Border? _choiceCard;

    /// <summary>The question as a card; the option chosen, what you typed instead, or that you skipped it.</summary>
    async Task<string> AskChoicesInChat(string question, List<string> options, CancellationToken cancel)
    {
        if (_chatStack == null || options.Count == 0) return "(the question couldn't be shown: go with the likeliest option and say which)";
        var tone = _cyber ? CyCyan : Color.FromRgb(0x8E, 0x8C, 0xFF);
        var icons = (FontFamily)FindResource("IconFont");
        var card = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new TextBlock { Text = "", FontFamily = icons, FontSize = 10, Foreground = Frozen(tone), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 6, 0) });
        head.Children.Add(new TextBlock { Text = _cyber ? "YOUR CALL" : "Your call", FontSize = 9.5, FontWeight = FontWeights.SemiBold, Foreground = Frozen(tone), FontFamily = _cyber ? CyFont : UiText, VerticalAlignment = VerticalAlignment.Center });
        card.Children.Add(head);
        card.Children.Add(new TextBlock { Text = question, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 8), FontFamily = _cyber ? CyFont : UiText });

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var list = new StackPanel();
        for (int i = 0; i < options.Count; i++)
        {
            string label = options[i];
            bool first = i == 0;
            var line = new Grid();
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition());
            line.Children.Add(new TextBlock { Text = (i + 1).ToString(), FontSize = 10, FontWeight = FontWeights.SemiBold, Width = 16, Foreground = Frozen(first ? tone : Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF)), VerticalAlignment = VerticalAlignment.Center, FontFamily = _cyber ? CyFont : UiText });
            var text = new TextBlock { Text = label, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Foreground = first ? Brushes.White : Frozen(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF)), FontWeight = first ? FontWeights.SemiBold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center, FontFamily = _cyber ? CyFont : UiText };
            Grid.SetColumn(text, 1);
            line.Children.Add(text);
            Brush rest = first ? Frozen(A(tone, 0x2A)) : Frozen(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
            Brush hover = first ? Frozen(A(tone, 0x46)) : Frozen(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
            var b = new Border
            {
                Child = line, Padding = new Thickness(9, 5, 10, 6), Margin = new Thickness(0, 0, 0, 5), Cursor = Cursors.Hand, CornerRadius = new CornerRadius(_cyber ? 0 : 8),
                Background = rest, BorderBrush = Frozen(first ? A(tone, 0xA0) : Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
            };
            b.MouseEnter += (_, _) => b.Background = hover;
            b.MouseLeave += (_, _) => b.Background = rest;
            b.MouseLeftButtonDown += (_, e) => e.Handled = true;
            b.MouseLeftButtonUp += (_, e) => { e.Handled = true; tcs.TrySetResult(label); };
            list.Children.Add(b);
        }
        card.Children.Add(list);
        card.Children.Add(new TextBlock { Text = Loc.T("1–4 or click · or type your own answer · Esc skips"), FontSize = 9, Foreground = Frozen(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)), Margin = new Thickness(1, 2, 0, 0), FontFamily = _cyber ? CyFont : UiText });

        var border = new Border
        {
            Tag = "step", Child = card, Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(12, 8, 18, 4), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(_cyber ? 0 : 12),
            Background = Frozen(new LinearGradientBrush(A(tone, 0x1A), A(tone, 0x06), 90)), BorderBrush = Frozen(A(tone, 0x80)),
        };
        _chatStack.Children.Add(border);
        _choiceCard = border;
        Arrive(border);
        FollowChat(force: true);
        if (!ChatOnScreen)
            ShowOsd("ai", "", null, Loc.T("The AI has a question"), question, null, glyphBrush: Frozen(tone), seconds: 6, force: true, prio: Prio.Normal, onClick: ReopenChat);

        _choice = tcs;
        _choiceOptions = options;
        using var reg = cancel.Register(() => tcs.TrySetResult("\u0000skip"));
        ChatKeys();
        var waited = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromMinutes(5)));
        string answer = waited == tcs.Task ? tcs.Task.Result : "\u0000timeout";
        _choice = null;
        _choiceOptions = new();
        _choiceCard = null;

        // answered: one quiet line instead of the card
        bool skipped = answer.StartsWith('\u0000');
        bool typed = !skipped && !options.Contains(answer);
        var summary = new StackPanel { Orientation = Orientation.Horizontal };
        summary.Children.Add(new TextBlock { Text = skipped ? "" : "", FontFamily = icons, FontSize = 9.5, Foreground = Frozen(skipped ? Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF) : Ok), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 7, 0) });
        summary.Children.Add(new TextBlock
        {
            Text = question + "  ·  " + (skipped ? Loc.T("skipped") : answer), FontSize = 11, Foreground = Frozen(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF)),
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, FontFamily = _cyber ? CyFont : UiText,
        });
        border.Child = summary;
        border.Padding = new Thickness(10, 4, 10, 5);
        border.Background = Frozen(A(skipped ? tone : Ok, 0x0C));
        border.BorderBrush = Frozen(A(skipped ? tone : Ok, 0x40));
        ChatKeys();
        _launchBox?.Focus();

        if (answer == "\u0000timeout") return "The user didn't answer within 5 minutes: don't act; say in one line what you'd need to know.";
        if (skipped) return "The user skipped the question: don't do any of the options; ask nothing more and stop here.";
        return typed ? "The user answered in their own words: " + answer : "The user chose: " + answer;
    }

    /// <summary>
    /// A key while a question waits (nothing typed yet): Enter picks the first, Esc skips. The numbers come as typed
    /// characters (<see cref="ChoiceDigit"/>): on a French keyboard the "2" key types "é", which must start your words.
    /// </summary>
    bool ChoiceKey(Key key)
    {
        if (_choice == null || _launchBox == null || _launchBox.Text.Length > 0 || Keyboard.Modifiers != ModifierKeys.None) return false;
        if (key == Key.Escape) { _choice.TrySetResult("\u0000skip"); return true; }
        if (key != Key.Enter || _choiceOptions.Count == 0) return false;
        _choice.TrySetResult(_choiceOptions[0]);
        return true;
    }

    /// <summary>A "1"–"4" typed as the first character while a question waits: that option. True: taken.</summary>
    bool ChoiceDigit(string typed)
    {
        if (_choice == null || _launchBox == null || _launchBox.Text.Length > 0 || typed.Length != 1 || typed[0] is < '1' or > '4') return false;
        int pick = typed[0] - '1';
        if (pick >= _choiceOptions.Count) return false;
        _choice.TrySetResult(_choiceOptions[pick]);
        return true;
    }

    /// <summary>A message typed while a question waits is its answer (not a new question queued behind it).</summary>
    bool AnswerChoice(string text)
    {
        if (_choice == null || text.Trim().Length == 0) return false;
        // only typed into the chat while the card is on screen: a question asked from the search later, or an old
        // button, is a new message, not the answer (the waiting question is skipped first)
        if (!ChatOnScreen || _choiceCard?.Parent != _chatStack) { _choice.TrySetResult("\u0000skip"); return false; }
        _choice.TrySetResult(text.Trim());
        if (_launchBox != null) _launchBox.Text = "";
        return true;
    }

    /// <summary>The ask_user tool's arguments: the question, and 2–4 short options (more are cut, empty ones dropped).</summary>
    static (string Question, List<string> Options) ChoiceArgs(JsonNode? args)
    {
        string q = args?["question"]?.ToString().Trim() ?? "";
        var opts = new List<string>();
        if (args?["options"] is JsonArray arr)
            foreach (var o in arr)
                if (o?.ToString().Trim() is { Length: > 0 } s && !opts.Contains(s, StringComparer.OrdinalIgnoreCase)) opts.Add(s.Length > 90 ? s[..90] : s);
        return (q.Length > 0 ? q : "Which one?", opts.Take(4).ToList());
    }
}
