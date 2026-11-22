using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// The Search page: the notch's search and its AI chat, each one click away (the same as Alt+Space, now with a home of
/// its own, third in the row, before Calls). A switch on top picks one:
///  • Search: a big bar ("apps, files, settings, the web") and your usual apps under it, one click to open;
///  • Ask AI: a bar to ask anything, the chats you had lately (one click carries on) and ideas that fit right now.
/// Clicking the bar (or just typing while the pointer is over the notch) opens the same search or chat as always, in
/// place, so nothing about how they work changes. The switch slides, the content under it slides with it, and each
/// move has its soft sound.
/// </summary>
public partial class NotchWindow
{
    const int SearchPage = 8;

    bool _spAi;
    int _spTab; // 0 Search, 1 Ask AI, 2 Notes
    const double SegW = 312;
    Grid? _spRoot;
    Border? _spLit, _spBar;
    TextBlock? _spBarGlyph, _spBarHint, _spSegSearch, _spSegAi, _spSegNotes;
    Grid? _spBody;
    readonly TranslateTransform _spLitShift = new(), _spBodyShift = new();

    UIElement BuildSearchPage()
    {
        _spRoot = new Grid { Width = 440, Height = 216, Background = Brushes.Transparent };
        var col = new Grid { Margin = new Thickness(26, 4, 26, 6) };
        col.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        col.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        col.RowDefinitions.Add(new RowDefinition());
        _spRoot.Children.Add(col);

        // ── the switch: Search | Ask AI, a lit capsule sliding under the one picked
        var seg = new Grid { Width = SegW, Height = 28, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 8) };
        seg.ColumnDefinitions.Add(new ColumnDefinition());
        seg.ColumnDefinitions.Add(new ColumnDefinition());
        seg.ColumnDefinitions.Add(new ColumnDefinition());
        var track = new Border { CornerRadius = new CornerRadius(15), Tag = "track" };
        Grid.SetColumnSpan(track, 3);
        seg.Children.Add(track);
        _spLit = new Border { CornerRadius = new CornerRadius(13), Margin = new Thickness(2), RenderTransform = _spLitShift };
        seg.Children.Add(_spLit);
        (UIElement half, TextBlock label) Half(string glyph, string text, int tab)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(Glyph(glyph, 11, Brushes.White).Also(g => g.Margin = new Thickness(0, 0, 6, 0)));
            var t = new TextBlock { Text = Loc.T(text), FontSize = 11.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(t);
            var hit = new Border { Background = Brushes.Transparent, Child = row, Cursor = Cursors.Hand };
            Grid.SetColumn(hit, tab);
            hit.PreviewMouseLeftButtonDown += (_, _) => _maybeSwipe = false;
            // the side already picked: nothing to change (it would only fade the same things in again)
            hit.MouseLeftButtonUp += (_, e) => { e.Handled = true; if (tab != _spTab) SearchTab(tab, animate: true); };
            return (hit, t);
        }
        var (hs, ls) = Half("", "Search", 0);
        var (ha, la) = Half("", "Ask AI", 1);
        var (hn, ln) = Half("\uE70B", "Notes", 2);
        _spSegSearch = ls; _spSegAi = la; _spSegNotes = ln;
        seg.Children.Add(hs);
        seg.Children.Add(ha);
        seg.Children.Add(hn);
        col.Children.Add(seg);

        // ── the bar: it looks like a search field; a click opens the real one (search or chat) right here
        var barRow = new Grid();
        barRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        barRow.ColumnDefinitions.Add(new ColumnDefinition());
        barRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _spBarGlyph = Glyph("", 13, Brushes.White).Also(g => { g.Margin = new Thickness(14, 0, 10, 0); g.Opacity = 0.85; });
        barRow.Children.Add(_spBarGlyph);
        _spBarHint = new TextBlock { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Foreground = Frozen(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)), TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(_spBarHint, 1);
        barRow.Children.Add(_spBarHint);
        var keys = new Border
        {
            CornerRadius = new CornerRadius(7), Padding = new Thickness(7, 2, 7, 3), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
            Background = Frozen(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF)), BorderBrush = Frozen(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
            Child = new TextBlock { Text = "Alt + Space", FontSize = 9.5, Foreground = Frozen(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)) },
        };
        Grid.SetColumn(keys, 2);
        barRow.Children.Add(keys);
        _spBar = new Border
        {
            Height = 36, CornerRadius = new CornerRadius(18), BorderThickness = new Thickness(1), Child = barRow, Cursor = Cursors.IBeam,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(), Margin = new Thickness(0, 0, 0, 9),
        };
        _spBar.MouseEnter += (_, _) => SearchBarHover(true);
        _spBar.MouseLeave += (_, _) => SearchBarHover(false);
        _spBar.PreviewMouseLeftButtonDown += (_, _) => _maybeSwipe = false;
        _spBar.MouseLeftButtonUp += (_, e) => { e.Handled = true; SearchGo(null); };
        Grid.SetRow(_spBar, 1);
        col.Children.Add(_spBar);

        // ── what's under it: your usual apps, or your chats and ideas
        _spBody = new Grid { RenderTransform = _spBodyShift, ClipToBounds = false };
        Grid.SetRow(_spBody, 2);
        col.Children.Add(_spBody);

        SearchPaint();
        _spTab = -1;
        SearchTab(_settings.SearchPageNotes ? 2 : _settings.SearchPageAi ? 1 : 0, animate: false);
        return _spRoot;
    }

    /// <summary>The page came into view: what's under the bar is read fresh (your usual apps, your latest chats).</summary>
    bool _spIndexWired;

    void OnSearchPageShown()
    {
        if (_spRoot == null) return;
        // the list of apps is read in the background the first time: the usual ones show up as soon as it's ready
        // (only when they changed: the same apps drawn again would blink under the pointer a moment after the page showed)
        if (!_spIndexWired) { _spIndexWired = true; _index.Changed += () => Dispatcher.BeginInvoke(() => { if (_page == SearchPage && !_spAi && !_launcherOpen && UsualSig() != _spUsualSig) SearchFill(); }); }
        _index.Refresh();
        SearchPaint();
        SearchFill();
    }

    /// <summary>Search or Ask AI: the capsule slides over, the bar and what's under it change with it.</summary>
    void SearchSide(bool ai, bool animate) => SearchTab(ai ? 1 : 0, animate);

    /// <summary>Search, Ask AI or Notes: the capsule slides over, the bar and what's under it change with it.</summary>
    void SearchTab(int tab, bool animate)
    {
        bool changed = tab != _spTab;
        if (changed && _spTab == 2) { FlushNote(); EndWorkTyping(giveBack: true); } // leaving the notes: what was typed is kept
        _spTab = tab;
        bool ai = tab == 1;
        _spAi = ai;
        if (animate && changed) FeelSound(Sfx.Cue.Pick); // the sound first, with the click (nothing below may hold it back)
        // remembered a moment later: writing the settings file isn't for the instant of the click
        if (changed && (_settings.SearchPageAi != ai || _settings.SearchPageNotes != (tab == 2))) { _settings.SearchPageAi = ai; _settings.SearchPageNotes = tab == 2; After(400, _settings.Save); }
        if (_spLit == null || _spBarHint == null || _spBarGlyph == null || _spBar == null) return;
        double third = SegW / 3.0;
        var ease = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut };
        if (animate && !Eco) _spLitShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(tab * third, TimeSpan.FromMilliseconds(380)) { EasingFunction = ease });
        else { _spLitShift.BeginAnimation(TranslateTransform.XProperty, null); _spLitShift.X = tab * third; }
        _spLit.Width = third - 4;
        _spLit.HorizontalAlignment = HorizontalAlignment.Left;
        if (_spSegSearch != null) _spSegSearch.Opacity = tab == 0 ? 1 : 0.6;
        if (_spSegAi != null) _spSegAi.Opacity = tab == 1 ? 1 : 0.6;
        if (_spSegNotes != null) _spSegNotes.Opacity = tab == 2 ? 1 : 0.6;
        _spBar.Visibility = tab == 2 ? Visibility.Collapsed : Visibility.Visible; // the notes have the room to themselves
        _spBarGlyph.Text = ai ? "" : "";
        _spBarHint.Text = Loc.T(ai ? "Ask anything…" : "Apps, files, settings, the web…");
        SearchPaint();
        SearchFill(slideFrom: animate && changed && !Eco ? (tab > _lastSpTab ? 1 : -1) : 0);
        _lastSpTab = tab;
    }

    int _lastSpTab;

    /// <summary>Colours for the theme: the accent glows in the capsule and the bar's edge; Neon goes square and cyan.</summary>
    void SearchPaint()
    {
        if (_spLit == null || _spBar == null) return;
        var acc = _cyber ? CyCyan : _accent == Colors.White ? Color.FromRgb(0x8E, 0x8C, 0xFF) : _accent;
        _spLit.Background = Frozen(Color.FromArgb(_cyber ? (byte)0x30 : (byte)0x3A, acc.R, acc.G, acc.B));
        _spLit.BorderBrush = Frozen(Color.FromArgb(0x70, acc.R, acc.G, acc.B));
        _spLit.BorderThickness = new Thickness(1);
        _spLit.CornerRadius = new CornerRadius(_cyber ? 0 : 13);
        if (_spLit.Parent is Grid seg && seg.Children.OfType<Border>().FirstOrDefault(b => Equals(b.Tag, "track")) is { } track)
        {
            track.Background = Frozen(Color.FromArgb(0x24, 0x00, 0x00, 0x00));
            track.BorderBrush = Frozen(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
            track.BorderThickness = new Thickness(1);
            track.CornerRadius = new CornerRadius(_cyber ? 0 : 15);
        }
        _spBar.Background = Frozen(_cyber ? Color.FromArgb(0xC0, 0x02, 0x06, 0x0C) : Color.FromArgb(0x38, 0x00, 0x00, 0x00));
        _spBar.BorderBrush = Frozen(Color.FromArgb(_spBar.IsMouseOver ? (byte)0x90 : (byte)0x48, acc.R, acc.G, acc.B));
        _spBar.CornerRadius = new CornerRadius(_cyber ? 0 : 18);
        if (_spBarGlyph != null) _spBarGlyph.Foreground = Frozen(Lighten(acc, 0.35));
    }

    void SearchBarHover(bool on)
    {
        SearchPaint();
        if (Eco || _spBar?.RenderTransform is not ScaleTransform st) return;
        var a = new DoubleAnimation(on ? 1.015 : 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    /// <summary>
    /// One of your usual apps (the Search page and the launcher): its icon and name (cut with "…" when long, in full in
    /// the tip). Resting on it, no box: the icon lifts a touch and grows, a soft glow in the theme's colour rises under
    /// it, the name brightens, and a small × (take it out of your usual) shows on the icon's corner. Click opens it;
    /// right-click takes it out too.
    /// </summary>
    FrameworkElement UsualTile(LauncherIndex.Entry e, Action<LauncherIndex.Entry, FrameworkElement>? hide = null)
    {
        hide ??= HideUsual;
        var acc = BarAccent();
        var host = new Grid { Background = Brushes.Transparent, Cursor = Cursors.Hand, Margin = new Thickness(1, 0, 1, 0), Height = 60, ToolTip = e.Name };
        var icon = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(_cyber ? 0 : 8), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 0, 0), RenderTransformOrigin = new Point(0.5, 0.5) };
        IconInto(icon, e.Target);
        var glow = new System.Windows.Media.Effects.DropShadowEffect { Color = acc, BlurRadius = 16, ShadowDepth = 0, Opacity = 0 };
        var name = new TextBlock
        {
            Text = e.Name, FontSize = 9.5, TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(2, 42, 2, 0), Foreground = DimBrush,
        };
        var x = new Border
        {
            Width = 13, Height = 13, CornerRadius = new CornerRadius(_cyber ? 0 : 6.5), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(30, 1, 0, 0), Opacity = 0, IsHitTestVisible = false, ToolTip = Loc.T("Take it out of your usual"),
            Background = Frozen(_cyber ? Color.FromRgb(0x2A, 0x08, 0x10) : Color.FromArgb(0xE6, 0x1C, 0x1C, 0x22)),
            BorderBrush = Frozen(_cyber ? CyRed : Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
            Child = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 6.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Frozen(_cyber ? CyRed : Colors.White) },
        };
        host.Children.Add(icon);
        host.Children.Add(name);
        host.Children.Add(x);
        var lift = new TranslateTransform();
        var grow = new ScaleTransform(1, 1);
        icon.RenderTransform = new TransformGroup { Children = { grow, lift } };
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        void To(double up, double size, double shine, double badge, int ms)
        {
            var d = TimeSpan.FromMilliseconds(Eco ? 1 : ms);
            lift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(up, d) { EasingFunction = ease });
            grow.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(size, d) { EasingFunction = ease });
            grow.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(size, d) { EasingFunction = ease });
            x.BeginAnimation(OpacityProperty, new DoubleAnimation(badge, d));
            if (!Eco) glow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, new DoubleAnimation(shine, d));
        }
        host.MouseEnter += (_, _) =>
        {
            if (!Eco) icon.Effect = glow;
            x.IsHitTestVisible = true;
            To(-2, 1.08, 0.75, 1, 170);
            name.Foreground = Brushes.White;
        };
        host.MouseLeave += (_, _) =>
        {
            x.IsHitTestVisible = false;
            To(0, 1, 0, 0, 220);
            name.Foreground = DimBrush;
        };
        host.MouseLeftButtonDown += (_, _) => { if (!Eco) { grow.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94, TimeSpan.FromMilliseconds(90))); grow.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.94, TimeSpan.FromMilliseconds(90))); } };
        host.MouseLeftButtonUp += (_, ev) =>
        {
            ev.Handled = true;
            if (x.IsMouseOver) { hide(e, host); return; }
            To(-2, 1.08, 0.75, 1, 120);
            _index.Used(e);
            OpenTarget(e.Target);
        };
        host.MouseRightButtonUp += (_, ev) => { ev.Handled = true; hide(e, host); };
        return host;
    }

    /// <summary>Out of your usual: the tile folds away and the row closes up (the launcher's Ctrl+Z still brings it back).</summary>
    void HideUsual(LauncherIndex.Entry e, FrameworkElement tile)
    {
        if (!_settings.SearchHiddenApps.Contains(e.Target)) _settings.SearchHiddenApps.Add(e.Target);
        _settings.Save();
        _lastHidden = e;
        FeelSound(Services.Sfx.Cue.Clear);
        tile.IsHitTestVisible = false;
        var scale = new ScaleTransform(1, 1, tile.ActualWidth / 2, 20);
        tile.RenderTransform = scale;
        var shrink = new DoubleAnimation(1, 0.6, TimeSpan.FromMilliseconds(Eco ? 1 : 170)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(Eco ? 1 : 170));
        fade.Completed += (_, _) => SearchFill();
        tile.BeginAnimation(OpacityProperty, fade);
    }

    string _spUsualSig = "";

    /// <summary>Which usual apps the page would show now, in order (to know whether drawing them again changes anything).</summary>
    string UsualSig() => _settings.SearchFrequent ? string.Join("|", _index.Favourites(6, _settings.SearchHiddenApps).Take(6).Select(e => e.Target)) : "";

    /// <summary>Under the bar: your usual apps (Search), or your latest chats and ideas for right now (Ask AI).</summary>
    void SearchFill(int slideFrom = 0)
    {
        if (_spBody == null) return;
        _spBody.Children.Clear();
        var stack = new StackPanel();
        _spBody.Children.Add(stack);
        TextBlock Label(string t) => new() { Text = _cyber ? t.ToUpperInvariant() : t, FontSize = 10, Foreground = DimBrush, Margin = new Thickness(4, 0, 0, 4) };

        if (_spTab == 2) stack.Children.Add(NotesView()); // (NotchWindow.Notes)
        else if (!_spAi)
        {
            var usual = _settings.SearchFrequent ? _index.Favourites(6, _settings.SearchHiddenApps).ToList() : new();
            _spUsualSig = string.Join("|", usual.Take(6).Select(e => e.Target));
            if (usual.Count > 0)
            {
                stack.Children.Add(Label(Loc.T("Your usual")));
                var row = new UniformGrid { Columns = 6, Rows = 1 };
                foreach (var e in usual.Take(6)) row.Children.Add(UsualTile(e));
                stack.Children.Add(row);
            }
            else stack.Children.Add(Hint(Loc.T("Type to find apps, files, settings and answers from the web. The apps you open most will wait here.")));
        }
        else if (!_settings.AiChat) stack.Children.Add(Hint(Loc.T("The AI chat is off: turn it on in Preferences → AI chat.")));
        else
        {
            var chats = _settings.AiHistory ? ChatStore.Recent(2) : new();
            if (chats.Count > 0)
            {
                stack.Children.Add(Label(Loc.T("Pick up where you left off")));
                foreach (var c in chats) stack.Children.Add(ChatRowFor(c));
            }
            var ideas = WelcomeIdeas().Take(3).ToList();
            if (ideas.Count > 0)
            {
                stack.Children.Add(Label(Loc.T("Try")).Also(l => l.Margin = new Thickness(4, chats.Count > 0 ? 3 : 0, 0, 4)));
                var wrap = new StackPanel { Orientation = Orientation.Horizontal, ClipToBounds = true }; // one row: what doesn't fit stays out
                foreach (var idea in ideas) wrap.Children.Add(IdeaChip(idea));
                stack.Children.Add(wrap);
            }
        }

        // in: slides from the side it came from, each part a beat after the one above
        if (Eco) return;
        int i = 0;
        foreach (UIElement el in stack.Children)
        {
            el.Opacity = 0;
            var begin = TimeSpan.FromMilliseconds(40 * i++);
            el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)) { BeginTime = begin });
        }
        if (slideFrom != 0)
            _spBodyShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(slideFrom * 24, 0, TimeSpan.FromMilliseconds(360)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    /// <summary>While the search or the chat is open, the page under it steps back (and comes back after).</summary>
    void LaunchVeil(bool on)
    {
        var t = TimeSpan.FromMilliseconds(Eco ? 1 : on ? 160 : 280);
        var parts = new List<UIElement> { Pager, Dots };
        if (PageTitle.Parent is UIElement header) parts.Add(header);
        if (_barLit != null) parts.Add(_barLit);
        foreach (var el in parts)
            el.BeginAnimation(OpacityProperty, new DoubleAnimation(on ? 0 : 1, t) { BeginTime = on ? TimeSpan.Zero : TimeSpan.FromMilliseconds(60) });
    }

    /// <summary>From the Search page: the search's box starts where the page's bar was and settles in its place.</summary>
    void LaunchRise()
    {
        double from = _launchRiseFrom;
        _launchRiseFrom = 0;
        if (from < 4 || Eco) { _launchShift.BeginAnimation(TranslateTransform.YProperty, null); _launchShift.Y = 0; return; }
        _launchShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(460)) { EasingFunction = new Controls.SpringEase { Damping = 0.82 } });
    }

    TextBlock Hint(string text) => new() { Text = text, FontSize = 11, Foreground = DimBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 4, 4, 0), LineHeight = 16 };

    /// <summary>One of your chats: its title and when; a click opens it to carry on.</summary>
    FrameworkElement ChatRowFor(ChatStore.Info c)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(Glyph("", 11, Brushes.White).Also(g => { g.Opacity = 0.7; g.Margin = new Thickness(10, 0, 9, 0); }));
        var title = new TextBlock { Text = c.Title.Length > 0 ? c.Title : Loc.T("A chat"), FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(title, 1);
        row.Children.Add(title);
        var when = new TextBlock { Text = Ago(c.Updated), FontSize = 10, Foreground = DimBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 10, 0) };
        Grid.SetColumn(when, 2);
        row.Children.Add(when);
        var card = new Border
        {
            Child = row, Height = 26, Margin = new Thickness(0, 0, 0, 3), CornerRadius = new CornerRadius(_cyber ? 0 : 9), Cursor = Cursors.Hand,
            Background = Frozen(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF)), BorderBrush = Frozen(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
        };
        card.MouseEnter += (_, _) => card.Background = Frozen(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
        card.MouseLeave += (_, _) => card.Background = Frozen(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF));
        string id = c.Id;
        card.PreviewMouseLeftButtonDown += (_, _) => _maybeSwipe = false;
        card.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            FeelSound(Sfx.Cue.Open);
            _launchFromPage = true; // opened from the page, like its bar: Esc comes back here
            OpenLauncher(null);
            if (ChatStore.Load(id) is { } s) StartSession(s);
            EnterChat(null, resume: true);
        };
        return card;
    }

    FrameworkElement IdeaChip(string idea)
    {
        var acc = _cyber ? CyCyan : _accent == Colors.White ? Color.FromRgb(0x8E, 0x8C, 0xFF) : _accent;
        var chip = new Border
        {
            CornerRadius = new CornerRadius(_cyber ? 0 : 11), Padding = new Thickness(9, 3, 9, 4), Margin = new Thickness(0, 0, 6, 0), Cursor = Cursors.Hand,
            Background = Frozen(Color.FromArgb(0x22, acc.R, acc.G, acc.B)), BorderBrush = Frozen(Color.FromArgb(0x48, acc.R, acc.G, acc.B)), BorderThickness = new Thickness(1),
            Child = new TextBlock { Text = Loc.T(idea), FontSize = 10.5 },
        };
        chip.MouseEnter += (_, _) => chip.Background = Frozen(Color.FromArgb(0x3A, acc.R, acc.G, acc.B));
        chip.MouseLeave += (_, _) => chip.Background = Frozen(Color.FromArgb(0x22, acc.R, acc.G, acc.B));
        chip.PreviewMouseLeftButtonDown += (_, _) => _maybeSwipe = false;
        chip.MouseLeftButtonUp += (_, e) => { e.Handled = true; SearchGo(Loc.T(idea), ask: true); };
        return chip;
    }

    bool _launchFromPage;
    double _launchRiseFrom;

    /// <summary>The bar was clicked (or an idea): the real search or chat opens here, in the side that's picked.</summary>
    void SearchGo(string? text, bool ask = false)
    {
        FeelSound(Sfx.Cue.Open);
        _launchFromPage = true;
        // where the page's bar is: the search's own box rises from there into its place
        _launchRiseFrom = _spBar != null && _spBar.IsVisible ? Math.Max(0, _spBar.TranslatePoint(new Point(0, 0), Expanded).Y - 8) : 0;
        if (_spAi || ask)
        {
            OpenLauncher(null);
            if (!_settings.AiChat) return;
            EnterChat(text); // an idea is asked right away; the bar opens an empty chat ready to type
        }
        else OpenLauncher(text);
    }
}
