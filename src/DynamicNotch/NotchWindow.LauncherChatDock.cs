using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace DynamicNotch;

/// <summary>
/// The chat's dock: one floating glass capsule at the bottom that holds everything the chat can do. The AI's mark
/// and its model (click to switch), your chats, a new one, ideas, today's usage, reading aloud, more, keep on top and
/// pop out. Each button lifts and lights under the pointer; the one in use keeps a small light under it.
/// Ideas and Usage open as small sheets that rise out of the dock; Esc or a click elsewhere puts them away.
/// The page above it stays quiet: the face and its greeting.
/// </summary>
public partial class NotchWindow
{
    Border? _dock, _dockSheet;
    StackPanel? _dockRow;
    TextBlock? _dockHistory, _dockIdeas, _dockUsage;
    string? _dockSheetKind;
    bool _devLook; // a developer look (DYNAMICNOTCH_OPEN=chat): the page stays without the keyboard

    FrameworkElement BuildChatDock()
    {
        _dockRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        // the AI's mark and its model: a click switches the model
        var brand = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, Background = Brushes.Transparent, Margin = new Thickness(4, 0, 2, 0) };
        if (_chatOrbHost != null) { DetachFromParent(_chatOrbHost); _chatOrbHost.Margin = new Thickness(0, 0, 5, 0); brand.Children.Add(_chatOrbHost); }
        if (_chatModelChip != null)
        {
            DetachFromParent(_chatModelChip);
            _chatModelChip.Margin = new Thickness(0, 0, 4, 1);
            _chatModelChip.MaxWidth = 120;
            _chatModelChip.TextTrimming = TextTrimming.CharacterEllipsis;
            _chatModelChip.Tag = "dock";
            brand.Children.Add(_chatModelChip);
        }
        brand.MouseLeftButtonDown += (_, e) => e.Handled = true;
        brand.MouseLeftButtonUp += (_, e) => { e.Handled = true; CloseDockSheet(); ModelMenu(); };
        _dockRow.Children.Add(brand);
        _dockRow.Children.Add(DockDivider());

        _dockHistory = DockItem("", "Your chats  ·  Ctrl+H", () => { CloseDockSheet(); ShowChatView(_chatView == ChatView.History ? ChatView.Chat : ChatView.History); PaintDock(); });
        _dockRow.Children.Add(_dockHistory);
        _dockRow.Children.Add(DockItem("", "New chat  ·  Ctrl+N", () => { CloseDockSheet(); NewChat(); FeelIt(Feel.Pick); PaintDock(); }));
        _dockIdeas = DockItem("", "Ideas: what I could do for you now", () => ToggleDockSheet("ideas"));
        _dockRow.Children.Add(_dockIdeas);
        _dockUsage = DockItem("", "Today's AI usage", () => ToggleDockSheet("usage"));
        _dockRow.Children.Add(_dockUsage);
        _dockRow.Children.Add(DockDivider());
        var speak = BuildSpeakerButton();
        AsDockItem(speak);
        _dockRow.Children.Add(speak);
        if (_moreButton != null) { DetachFromParent(_moreButton); AsDockItem(_moreButton); _dockRow.Children.Add(_moreButton); }
        if (_pinButton != null) { DetachFromParent(_pinButton); AsDockItem(_pinButton); _dockRow.Children.Add(_pinButton); }
        if (_popButton != null) { DetachFromParent(_popButton); AsDockItem(_popButton); _dockRow.Children.Add(_popButton); }

        _dock = new Border
        {
            Child = _dockRow, Height = 32, Padding = new Thickness(5, 0, 5, 0), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
            BorderThickness = new Thickness(1), Margin = new Thickness(0, 4, 0, -14), RenderTransform = new TranslateTransform(), Tag = "dock",
        };
        _dock.MouseLeftButtonDown += (_, e) => DragPop(e);
        StyleDock();
        return _dock;
    }

    static void DetachFromParent(FrameworkElement el)
    {
        switch (el.Parent)
        {
            case Panel p: p.Children.Remove(el); break;
            case Border b when ReferenceEquals(b.Child, el): b.Child = null; break;
            case ContentControl c when ReferenceEquals(c.Content, el): c.Content = null; break;
        }
    }

    FrameworkElement DockDivider() => new Border { Width = 1, Height = 14, Margin = new Thickness(5, 0, 5, 0), Background = Frozen(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)), VerticalAlignment = VerticalAlignment.Center };

    TextBlock DockItem(string glyph, string tip, Action act)
    {
        var b = HeadButton(glyph, tip, act);
        AsDockItem(b);
        return b;
    }

    /// <summary>A dock button: a little bigger, a soft pill under the pointer, a lift, and menus that open upward.</summary>
    void AsDockItem(TextBlock b)
    {
        b.Tag = "dock";
        b.Width = 28;
        b.Height = 26;
        b.FontSize = 12.5;
        b.Padding = new Thickness(0, 6, 0, 0);
        b.Margin = new Thickness(1, 0, 1, 0);
        b.RenderTransformOrigin = new Point(0.5, 0.5);
        b.RenderTransform = new TranslateTransform();
        var hot = Frozen(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF));
        b.MouseEnter += (_, _) =>
        {
            if (b.Tag is not "on") b.Background = hot;
            if (!Eco) ((TranslateTransform)b.RenderTransform).BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-1.5, TimeSpan.FromMilliseconds(150)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        };
        b.MouseLeave += (_, _) =>
        {
            if (b.Tag is not "on") b.Background = Brushes.Transparent;
            ((TranslateTransform)b.RenderTransform).BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        };
        b.PreviewMouseLeftButtonDown += (_, _) => _maybeSwipe = false;
    }

    /// <summary>The dock's look for the theme in use (glass and the AI's colour, or Neon's square plate).</summary>
    void StyleDock()
    {
        if (_dock == null) return;
        var acc = ChatAccent;
        _dock.CornerRadius = new CornerRadius(_cyber ? 0 : 16);
        _dock.Background = Frozen(_cyber ? Color.FromArgb(0xE8, 0x03, 0x0A, 0x10) : Color.FromArgb(0xE6, 0x1C, 0x1C, 0x22));
        _dock.BorderBrush = Frozen(_cyber ? A(CyCyan, 0x70) : A(Lighten(acc, 0.2), 0x30));
        if (!_cyber && !Eco) _dock.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Direction = 270, Opacity = 0.4, Color = Colors.Black };
        PaintDock();
    }

    /// <summary>The button in use (your chats open, a sheet open) keeps a soft light.</summary>
    void PaintDock()
    {
        var acc = ChatAccent;
        void Lit(TextBlock? b, bool on)
        {
            if (b == null) return;
            b.Tag = on ? "on" : "dock";
            b.Background = on ? Frozen(A(acc, 0x30)) : Brushes.Transparent;
            b.Foreground = on ? Frozen(Lighten(acc, 0.35)) : DimBrush;
        }
        Lit(_dockHistory, _chatView == ChatView.History);
        Lit(_dockIdeas, _dockSheetKind == "ideas");
        Lit(_dockUsage, _dockSheetKind == "usage");
    }

    // ───────────────────────────── the sheets ─────────────────────────────

    void ToggleDockSheet(string kind)
    {
        if (_dockSheetKind == kind) { CloseDockSheet(); return; }
        if (_chatView == ChatView.History) ShowChatView(ChatView.Chat);
        OpenDockSheet(kind);
    }

    void OpenDockSheet(string kind)
    {
        if (_chatHost == null) return;
        bool swap = _dockSheet != null;
        if (_dockSheet != null) { _chatHost.Children.Remove(_dockSheet); _dockSheet = null; }
        _dockSheetKind = kind;
        var acc = ChatAccent;
        FrameworkElement content = kind == "usage" ? (QuotaCard() ?? SheetLine("Your usage shows here once the AI is set up")) : IdeasList();
        _dockSheet = new Border
        {
            Child = content, Padding = new Thickness(kind == "usage" ? 2 : 6), Margin = new Thickness(6, 0, 16, 8), VerticalAlignment = VerticalAlignment.Bottom,
            CornerRadius = new CornerRadius(_cyber ? 0 : 16), BorderThickness = new Thickness(1),
            Background = Frozen(_cyber ? Color.FromArgb(0xF4, 0x03, 0x0A, 0x10) : Color.FromArgb(0xF4, 0x18, 0x18, 0x1E)),
            BorderBrush = Frozen(_cyber ? A(CyCyan, 0x70) : A(Lighten(acc, 0.2), 0x30)),
            RenderTransformOrigin = new Point(0.5, 1), RenderTransform = new TransformGroup { Children = { new ScaleTransform(1, 1), new TranslateTransform() } },
        };
        Panel.SetZIndex(_dockSheet, 30);
        _chatHost.Children.Add(_dockSheet);
        FeelSound(Services.Sfx.Cue.Tap);
        if (!Eco)
        {
            // it rises out of the dock
            var g = (TransformGroup)_dockSheet.RenderTransform;
            var st = (ScaleTransform)g.Children[0];
            var tt = (TranslateTransform)g.Children[1];
            var t = TimeSpan.FromMilliseconds(swap ? 260 : 380);
            var spring = new Controls.SpringEase { Damping = 0.78 };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.92, 1, t) { EasingFunction = spring });
            st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.85, 1, t) { EasingFunction = spring });
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, t) { EasingFunction = spring });
            _dockSheet.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)));
        }
        PaintDock();
    }

    void CloseDockSheet()
    {
        if (_dockSheet == null || _chatHost == null) { _dockSheetKind = null; PaintDock(); return; }
        var sheet = _dockSheet;
        _dockSheet = null;
        _dockSheetKind = null;
        PaintDock();
        if (Eco) { _chatHost.Children.Remove(sheet); return; }
        var g = (TransformGroup)sheet.RenderTransform;
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var t = TimeSpan.FromMilliseconds(150);
        ((ScaleTransform)g.Children[0]).BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.9, t) { EasingFunction = ease });
        ((TranslateTransform)g.Children[1]).BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, t) { EasingFunction = ease });
        var fade = new DoubleAnimation(0, t);
        fade.Completed += (_, _) => _chatHost?.Children.Remove(sheet);
        sheet.BeginAnimation(OpacityProperty, fade);
    }

    TextBlock SheetLine(string text) => new() { Text = text, FontSize = 11, Foreground = DimBrush, Margin = new Thickness(10, 8, 10, 8), TextWrapping = TextWrapping.Wrap, FontFamily = _cyber ? CyFont : UiText };

    /// <summary>What it could do right now, as quiet rows: a click puts the words in the box.</summary>
    FrameworkElement IdeasList()
    {
        var list = new StackPanel();
        list.Children.Add(new TextBlock { Text = _cyber ? "IDEAS // NOW" : "Ideas for right now", FontSize = 9.5, FontWeight = FontWeights.SemiBold, Foreground = Frozen(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)), Margin = new Thickness(8, 2, 0, 4), FontFamily = _cyber ? CyFont : UiText });
        int k = 0;
        foreach (var idea in WelcomeIdeas())
        {
            var (glyph, tone) = IdeaLook(idea);
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 11.5, Foreground = Frozen(tone), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center });
            var words = new TextBlock { Text = idea, FontSize = 11.5, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontFamily = _cyber ? CyFont : UiText };
            Grid.SetColumn(words, 1);
            row.Children.Add(words);
            var hot = Frozen(A(tone, 0x1E));
            var item = new Border { Child = row, Height = 30, CornerRadius = new CornerRadius(_cyber ? 0 : 9), Background = Brushes.Transparent, Cursor = Cursors.Hand, Padding = new Thickness(2, 0, 8, 0) };
            item.MouseEnter += (_, _) => item.Background = hot;
            item.MouseLeave += (_, _) => item.Background = Brushes.Transparent;
            item.MouseLeftButtonDown += (_, e) => e.Handled = true;
            string text = idea;
            item.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                CloseDockSheet();
                if (_launchBox == null) return;
                _launchBox.Text = text;
                _launchBox.CaretIndex = text.Length;
                _launchBox.Focus();
            };
            if (!Eco)
            {
                item.Opacity = 0;
                item.RenderTransform = new TranslateTransform(0, 6);
                var begin = TimeSpan.FromMilliseconds(60 + 40 * k);
                item.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { BeginTime = begin });
                ((TranslateTransform)item.RenderTransform).BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(320)) { BeginTime = begin, EasingFunction = new Controls.SpringEase { Damping = 0.8 } });
            }
            list.Children.Add(item);
            k++;
        }
        return list;
    }

    // ───────────────────────────── the welcome ─────────────────────────────

    /// <summary>The first message goes: the welcome floats up and fades (the face nods first), then makes way.</summary>
    void WelcomeLeaves()
    {
        if (_chatStack == null) return;
        foreach (var w in _chatStack.Children.OfType<FrameworkElement>().Where(x => x.Tag as string == "welcome").ToList())
        {
            if (Eco) { _chatStack.Children.Remove(w); continue; }
            w.Tag = "leaving";
            w.IsHitTestVisible = false;
            // it stops taking room at once (the message comes in at the top), and leaves over it
            w.Margin = new Thickness(0, 0, 0, -Math.Max(0, w.ActualHeight));
            w.RenderTransformOrigin = new Point(0.5, 0.3);
            var st = new ScaleTransform(1, 1);
            var tt = new TranslateTransform();
            w.RenderTransform = new TransformGroup { Children = { st, tt } };
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var t = TimeSpan.FromMilliseconds(240);
            st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.9, t) { EasingFunction = ease });
            st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.9, t) { EasingFunction = ease });
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-22, t) { EasingFunction = ease });
            var fade = new DoubleAnimation(0, t);
            fade.Completed += (_, _) => { _chatStack?.Children.Remove(w); FollowChat(force: true); };
            w.BeginAnimation(OpacityProperty, fade);
        }
    }
}
