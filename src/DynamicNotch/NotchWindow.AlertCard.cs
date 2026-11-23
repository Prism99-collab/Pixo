using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using DynamicNotch.Controls;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// Notifications, read the smart way and shown as a small living card.
///
/// Reading (<see cref="Sense"/>): who wrote it and where (a person, or someone in a group or a channel), whether it's
/// for you (your name, "mentioned you", "replied to you"), urgent ("asap", "call me", "!!!"), an offer or a newsletter,
/// a meeting you can join, or a link. An app that floods (four or more in 90 s) goes quiet: one sound, then the card
/// just counts. An app you mute stays quiet for an hour (its messages are still kept for "what did I miss?").
///
/// The card: a message from a person shows their initials in a colour of their own, with the app's icon as a little
/// badge; a run of messages pushes the last one down like a stack and counts up on the badge; a tag says when it's
/// for you, urgent or just an offer. Resting the pointer on it opens it up: the whole message (three lines) and the
/// actions that fit it (copy the code, join the meeting, open the link, open the app, mute the app for an hour).
/// </summary>
public partial class NotchWindow
{
    /// <summary>What the card shows besides the plain words (null for pop-ups that aren't notifications).</summary>
    sealed record ToastLook(
        string? Initials, Color? AvatarColor, ImageSource? Badge, int Count, string? Tag, Color? TagColor,
        string? FullText, IReadOnlyList<CardAction> Actions, NotchFace.Emote? Feel);

    sealed record CardAction(string Glyph, string Label, Action Run, bool Primary = false, bool KeepOpen = false, Color? Tone = null);

    enum SenseKind { App, Person, Group, Promo }

    sealed record SenseResult(SenseKind Kind, string Sender, string? Group, string Text, bool Mention, bool Urgent, string? JoinUrl, string? Link);

    static readonly string[] Messengers = { "whatsapp", "telegram", "discord", "teams", "slack", "messenger", "signal", "instagram", "skype", "phone link", "your phone", "messages", "line", "viber", "wechat", "snapchat" };

    static readonly Regex TitleGroup = new(@"^(?<who>[^()\[\]]{1,40}?)\s*[\(\[](?<where>[^)\]]{1,48})[\)\]]$", RegexOptions.Compiled);
    static readonly Regex TitleIn = new(@"^(?<who>.{1,40}?)\s+(?:in|@|on|dans|en)\s+(?<where>#?.{1,40})$", RegexOptions.Compiled);
    static readonly Regex BodySender = new(@"^~?\s*(?<who>[^:\d][^:]{0,30}):\s+(?<text>.+)$", RegexOptions.Compiled);
    static readonly Regex UrgentWords = new(@"\b(urgent|asap|emergency|important|call me|right now|immediately|urgente|dringend|imm[ée]diatement|tout de suite|ahora mismo)\b|!!!", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex MentionWords = new(@"\b(mentioned you|replied to you|tagged you|vous a mentionn[ée]|t.a mentionn[ée]|te mencion[óo]|hat dich erw[äa]hnt)\b|(?<![\w@])@(here|everyone|channel)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex PromoWords = new(@"(\d{1,2}\s?%\s?(off|de r[ée]duction|de descuento|rabatt)|\b(sale|deal of|special offer|limited time|discount|coupon|promo(code)?|newsletter|unsubscribe|free shipping|black friday|cyber monday|soldes|r[ée]duction|offre sp[ée]ciale|oferta|descuento|angebot)\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex JoinLink = new(@"https?://(?:[\w-]+\.)*(?:teams\.microsoft\.com/l/meetup|teams\.live\.com/meet|zoom\.us/(?:j|my|w)/|meet\.google\.com/|[\w-]+\.webex\.com/)\S*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex AnyLink = new(@"https?://\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static string? _myName;
    readonly Dictionary<string, DateTime> _mutedApps = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Queue<DateTime>> _appRush = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Your first name, from Windows (for "it's for you"); asked once, quietly.</summary>
    static async void LearnMyName()
    {
        try
        {
            var users = await Windows.System.User.FindAllAsync();
            if (users.Count == 0) return;
            if (await users[0].GetPropertyAsync(Windows.System.KnownUserProperties.FirstName) is string first && first.Trim().Length >= 2)
                _myName = first.Trim();
        }
        catch { }
    }

    static bool IsMessenger(string app) => Messengers.Any(m => app.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>Who, where, and what kind of message this is (see the class summary).</summary>
    static SenseResult Sense(ToastInfo t, string title, string body)
    {
        string rawAll = (t.Title ?? "") + " " + (t.Body ?? "");
        string? join = JoinLink.Match(rawAll) is { Success: true } j ? j.Value.TrimEnd('.', ',', ')') : null;
        string? link = join == null && AnyLink.Match(rawAll) is { Success: true } l ? l.Value.TrimEnd('.', ',', ')') : null;
        string all = title + " " + body;
        bool urgent = UrgentWords.IsMatch(all);
        bool mention = MentionWords.IsMatch(all) || _myName is { } me && Regex.IsMatch(body, @"(^|[\s@,])" + Regex.Escape(me) + @"\b", RegexOptions.IgnoreCase);

        if (!IsMessenger(t.App))
            return new SenseResult(PromoWords.IsMatch(all) && !urgent ? SenseKind.Promo : SenseKind.App, title, null, body, mention, urgent, join, link);

        // a messaging app: a person, or someone in a group / channel
        string sender = title, text = body;
        string? group = null;
        if (TitleGroup.Match(title) is { Success: true } tg) { sender = tg.Groups["who"].Value.Trim(); group = tg.Groups["where"].Value.Trim(); }
        else if (TitleIn.Match(title) is { Success: true } ti && ti.Groups["where"].Value.Length > 1) { sender = ti.Groups["who"].Value.Trim(); group = ti.Groups["where"].Value.Trim(); }
        else if (BodySender.Match(body) is { Success: true } bs && bs.Groups["who"].Value.Split(' ').Length <= 4)
        {
            group = title; sender = bs.Groups["who"].Value.Trim(); text = bs.Groups["text"].Value.Trim();
        }
        if (group != null && group.Contains(',')) group = group.Split(',')[0].Trim(); // "#general, Server" → "#general"
        bool promo = PromoWords.IsMatch(all) && !urgent && group == null && !mention;
        return new SenseResult(promo ? SenseKind.Promo : group != null ? SenseKind.Group : SenseKind.Person, sender, group, text, mention, urgent, join, link);
    }

    static string Initials(string name)
    {
        var parts = Regex.Replace(name, @"[^\p{L}\p{N}\s]", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        string a = parts[0][..1], b = parts.Length > 1 ? parts[^1][..1] : parts[0].Length > 1 ? parts[0][1..2] : "";
        return (a + b).ToUpperInvariant();
    }

    /// <summary>A person's own colour: picked from their name, the same every time, always bright enough on black.</summary>
    static Color PersonColor(string name)
    {
        uint h = 2166136261;
        foreach (char c in name.ToLowerInvariant()) h = (h ^ c) * 16777619;
        double hue = h % 360 / 360.0, s = 0.62, v = 0.86;
        double i = Math.Floor(hue * 6), f = hue * 6 - i, p = v * (1 - s), q = v * (1 - f * s), tt = v * (1 - (1 - f) * s);
        var (r, g, b) = ((int)i % 6) switch { 0 => (v, tt, p), 1 => (q, v, p), 2 => (p, v, tt), 3 => (p, q, v), 4 => (tt, p, v), _ => (v, p, q) };
        return Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }

    // ───────────── quiet rules ─────────────

    bool AppMuted(string app)
    {
        if (!_mutedApps.TryGetValue(app, out var until)) return false;
        if (DateTime.UtcNow < until) return true;
        _mutedApps.Remove(app);
        return false;
    }

    /// <summary>Counts this app's notifications: true when it's flooding (4+ in 90 s).</summary>
    bool AppRushing(string app)
    {
        var now = DateTime.UtcNow;
        if (!_appRush.TryGetValue(app, out var q)) _appRush[app] = q = new Queue<DateTime>();
        q.Enqueue(now);
        while (q.Count > 0 && (now - q.Peek()).TotalSeconds > 90) q.Dequeue();
        return q.Count >= 4;
    }

    /// <summary>Should this notification make a sound? (Not an offer, not a flood; a mention or urgent one always.)</summary>
    bool ToastSounds(ToastInfo t, bool rushing)
    {
        var s = Sense(t, Clean(t.Title), Clean(t.Body));
        if (s.Urgent || s.Mention) return true;
        if (s.Kind == SenseKind.Promo) return false;
        return !rushing;
    }

    void MuteApp(string app, string shown)
    {
        _mutedApps[app] = DateTime.UtcNow.AddHours(1);
        FeelSound(Services.Sfx.Cue.Hush);
        ShowOsd("muted", "", null, string.Format(Loc.T("{0} is quiet for an hour"), shown), Loc.T("Its messages are still kept"), null,
            glyphBrush: Frozen(Color.FromRgb(0x8E, 0x8C, 0xFF)), seconds: 2.4, prio: Prio.High, motion: Motion.None);
    }

    // ───────────── the card's parts ─────────────

    Grid? _cardAvatar;
    Border? _cardAvatarFill, _cardBadge, _cardCount, _cardTag;
    TextBlock? _cardInitials, _cardCountText;
    WrapPanel? _cardChips;
    bool _cardOpen;
    string? _cardInitialsBefore;

    void WireAlertCard()
    {
        LearnMyName();
        // the avatar: initials in the person's colour, the app's icon as a badge on its corner, the count above
        _cardAvatarFill = new Border { CornerRadius = new CornerRadius(15) };
        _cardInitials = new TextBlock { FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _cardAvatarFill.Child = _cardInitials;
        _cardBadge = new Border
        {
            Width = 14, Height = 14, CornerRadius = new CornerRadius(4.5), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, -3, -3), BorderThickness = new Thickness(1.5), BorderBrush = Frozen(Color.FromRgb(0x0E, 0x0E, 0x10)),
        };
        _cardAvatar = new Grid { Width = 30, Height = 30, Visibility = Visibility.Collapsed, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform() };
        _cardAvatar.Children.Add(_cardAvatarFill);
        _cardAvatar.Children.Add(_cardBadge);
        OsdIconHost.Children.Add(_cardAvatar);

        _cardCountText = new TextBlock { FontSize = 9, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _cardCount = new Border
        {
            MinWidth = 15, Height = 15, CornerRadius = new CornerRadius(7.5), Padding = new Thickness(4, 0, 4, 0), Child = _cardCountText,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -6, -8, 0),
            BorderThickness = new Thickness(1.5), BorderBrush = Frozen(Color.FromRgb(0x0E, 0x0E, 0x10)), Visibility = Visibility.Collapsed,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(), IsHitTestVisible = false,
        };
        Panel.SetZIndex(_cardCount, 5);
        OsdIconHost.Children.Add(_cardCount);
        OsdIconHost.ClipToBounds = false;

        // the tag on the right ("For you", "Urgent", "Offer"): the side text, in a little capsule
        if (OsdRight.Parent is Grid inner)
        {
            int col = Grid.GetColumn(OsdRight);
            inner.Children.Remove(OsdRight);
            _cardTag = new Border { CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Center, Child = OsdRight };
            Grid.SetColumn(_cardTag, col);
            inner.Children.Add(_cardTag);
        }

        // the actions, shown when it opens up under your pointer
        _cardChips = new WrapPanel { Margin = new Thickness(0, 6, 0, 1), Visibility = Visibility.Collapsed };
        OsdTextPanel.Children.Add(_cardChips);
    }

    /// <summary>The card's dress for this pop-up (avatar, badge, count, tag); plain pop-ups get the plain look back.</summary>
    void CardDress(OsdRequest r, bool sameKind, int countBefore)
    {
        if (_cardAvatar == null || _cardTag == null || _cardCount == null) return;
        var look = r.Look;
        CardClose(apply: false);

        // the avatar replaces the app icon for a person or a group
        bool avatar = look?.Initials != null;
        _cardAvatar.Visibility = avatar ? Visibility.Visible : Visibility.Collapsed;
        if (avatar)
        {
            var c = look!.AvatarColor ?? Color.FromRgb(0x5E, 0x5C, 0xE6);
            var fill = new LinearGradientBrush(Lighten(c, 0.18), Darken(c, 0.28), 60);
            fill.Freeze();
            _cardAvatarFill!.Background = fill;
            _cardInitialsBefore = _cardInitials!.Text;
            _cardInitials.Text = look.Initials;
            _cardBadge!.Background = look.Badge != null ? IconBrush(look.Badge) : null;
            _cardBadge.Visibility = look.Badge != null ? Visibility.Visible : Visibility.Collapsed;
            OsdArt.Visibility = Visibility.Collapsed;
            OsdGlyph.Visibility = Visibility.Collapsed;
            OsdIconHost.Width = OsdIconHost.Height = 30;
            bool someoneNew = _cardInitialsBefore != look.Initials || !sameKind;
            if (someoneNew && !Eco) Pop(_cardAvatar, 0.6, 1.12);
        }

        // the count of a run of messages, on the corner
        int count = look?.Count ?? 0;
        _cardCount.Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (count > 1)
        {
            _cardCountText!.Text = count > 99 ? "99+" : count.ToString();
            _cardCount.Background = Frozen(_osdAccent is { } ac ? Darken(ac, 0.05) : Color.FromRgb(0xFF, 0x45, 0x3A));
            if (count != countBefore && !Eco) Pop(_cardCount, 0.4, 1.3);
        }

        // the tag
        if (look?.Tag is { } tag)
        {
            var tc = look.TagColor ?? Colors.White;
            OsdRight.Text = tag;
            OsdRight.FontSize = 10;
            OsdRight.Foreground = Frozen(Lighten(tc, 0.3));
            _cardTag.Background = Frozen(Color.FromArgb(0x2E, tc.R, tc.G, tc.B));
            _cardTag.BorderBrush = Frozen(Color.FromArgb(0x50, tc.R, tc.G, tc.B));
            _cardTag.BorderThickness = new Thickness(1);
            _cardTag.Padding = new Thickness(7, 1, 7, 2);
        }
        else
        {
            OsdRight.FontSize = 11.5;
            _cardTag.Background = null;
            _cardTag.BorderBrush = null;
            _cardTag.BorderThickness = new Thickness(0);
            _cardTag.Padding = new Thickness(0);
        }
    }

    static void Pop(FrameworkElement el, double from, double over)
    {
        if (el.RenderTransform is not ScaleTransform st || st.IsFrozen) el.RenderTransform = st = new ScaleTransform();
        var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(420) };
        a.KeyFrames.Add(new LinearDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(over, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(170)), new CubicEase { EasingMode = EasingMode.EaseOut }));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(420)), new SineEase { EasingMode = EasingMode.EaseInOut }));
        st.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    // ───────────── a run of messages: the last one is pushed down like a card in a stack ─────────────

    Image? _cardGhost;
    Point _cardGhostAt;
    FrameworkElement _cardPushed = null!; // the words that move: the message line alone when the sender stays the same

    /// <summary>Before the words change: a picture of the words as they are (null when there's nothing to push).</summary>
    ImageSource? CardSnapshot(OsdRequest? previous, OsdRequest next, bool sameKind)
    {
        if (Eco || !sameKind || previous?.Look == null || next.Look == null || OsdTextPanel.ActualWidth < 20) return null;
        if (previous.Title == next.Title && previous.Subtitle == next.Subtitle) return null;
        FrameworkElement part = previous.Title == next.Title && OsdSub.IsVisible ? OsdSub : OsdTextPanel;
        try
        {
            double w = part.ActualWidth, h = part.ActualHeight;
            var dpi = VisualTreeHelper.GetDpi(part);
            // drawn through a brush: the picture is the words alone, wherever they sit in the card
            var dv = new DrawingVisual();
            using (var ctx = dv.RenderOpen()) ctx.DrawRectangle(new VisualBrush(part) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, w, h));
            var bmp = new RenderTargetBitmap((int)Math.Ceiling(w * dpi.DpiScaleX), (int)Math.Ceiling(h * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            bmp.Render(dv);
            bmp.Freeze();
            _cardGhostAt = part.TranslatePoint(new Point(0, 0), Osd);
            _cardPushed = part;
            return bmp;
        }
        catch { return null; }
    }

    /// <summary>After the words changed: the old message sinks and fades, the new one drops in from above (the avatar stays).</summary>
    void CardPush(ImageSource? shot)
    {
        if (shot == null) return;
        if (_cardGhost == null)
        {
            _cardGhost = new Image { IsHitTestVisible = false, Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                RenderTransformOrigin = new Point(0, 0), RenderTransform = new TransformGroup { Children = { new ScaleTransform(), new TranslateTransform() } } };
            Osd.Children.Add(_cardGhost);
        }
        var g = _cardGhost;
        g.Source = shot;
        g.Width = shot.Width;
        g.Height = shot.Height;
        g.Margin = new Thickness(_cardGhostAt.X, _cardGhostAt.Y, 0, 0);
        var tg = (TransformGroup)g.RenderTransform;
        var sink = TimeSpan.FromMilliseconds(200);
        var easeIn = new CubicEase { EasingMode = EasingMode.EaseIn };
        tg.Children[0].BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0.96, sink) { EasingFunction = easeIn });
        tg.Children[0].BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 0.96, sink) { EasingFunction = easeIn });
        tg.Children[1].BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, 11, sink) { EasingFunction = easeIn });
        var fade = new DoubleAnimation(1, 0, sink) { EasingFunction = easeIn };
        fade.Completed += (_, _) => { if (ReferenceEquals(g.Source, shot)) g.Source = null; };
        g.BeginAnimation(OpacityProperty, fade);

        // the new words: hidden for a beat while the old ones leave, then they drop in and settle
        var part = _cardPushed;
        if (part.RenderTransform is not TranslateTransform tt || tt.IsFrozen) part.RenderTransform = tt = new TranslateTransform();
        var show = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(380) };
        show.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        show.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(130))));
        show.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(380)), new CubicEase { EasingMode = EasingMode.EaseOut }));
        part.BeginAnimation(OpacityProperty, show);
        var drop = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(500) };
        drop.KeyFrames.Add(new DiscreteDoubleKeyFrame(-10, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        drop.KeyFrames.Add(new LinearDoubleKeyFrame(-10, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(130))));
        drop.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(500)), new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut }));
        tt.BeginAnimation(TranslateTransform.YProperty, drop);
    }

    // ───────────── opening up under the pointer ─────────────

    /// <summary>The pointer rests on a notification: the whole message and its actions show, the card grows to fit.</summary>
    void CardOpen()
    {
        if (_mode != Mode.Osd || _osd?.Look is not { } look || _cardChips == null || _cardOpen) return;
        if (look.FullText == null && look.Actions.Count == 0) return;
        _cardOpen = true;
        bool unfolded = OsdUnfold(); // from one line back to two (NotchWindow.OsdCompact)
        if (look.FullText is { } full) OsdSub.Text = full;
        OsdSub.TextWrapping = TextWrapping.Wrap;
        OsdSub.MaxHeight = 3 * 14.6;
        OsdSub.Visibility = Visibility.Visible;
        _cardChips.Children.Clear();
        int i = 0;
        foreach (var a in look.Actions) _cardChips.Children.Add(Chip(a, i++));
        _cardChips.Visibility = look.Actions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        OsdIconHost.VerticalAlignment = VerticalAlignment.Top;
        OsdIconHost.Margin = new Thickness(0, 10, 0, 0);
        OsdTextPanel.VerticalAlignment = VerticalAlignment.Top;
        OsdTextPanel.Margin = new Thickness(OsdTextPanel.Margin.Left, 9, OsdTextPanel.Margin.Right, 0);
        _cardTag!.VerticalAlignment = VerticalAlignment.Top;
        _cardTag.Margin = new Thickness(0, 11, 0, 0);
        // (from one line: the words' new room, not the narrow one laid out a moment ago)
        double width = unfolded ? OsdWordsRoom() : Math.Max(120, OsdTextPanel.ActualWidth);
        OsdTextPanel.Measure(new Size(width, double.PositiveInfinity));
        double h = Math.Max(_osdSize.Height, Math.Ceiling(OsdTextPanel.DesiredSize.Height + 22));
        Osd.Height = h;
        _osdSize = new Size(_osdSize.Width, h);
        CyberSkinSoon(); // the chips in the theme's dress (Neon: cut corners)
        if (!Eco)
            for (int k = 0; k < _cardChips.Children.Count; k++)
            {
                var chip = (FrameworkElement)_cardChips.Children[k];
                var tt = new TranslateTransform(0, 4);
                chip.RenderTransform = tt;
                var begin = TimeSpan.FromMilliseconds(90 + k * 45);
                chip.Opacity = 0; // hidden while it waits its turn (else it shows in full, blinks out, then fades in)
                chip.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { BeginTime = begin });
                tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(4, 0, TimeSpan.FromMilliseconds(260)) { BeginTime = begin, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            }
    }

    /// <summary>Back to the one-line card (when the pointer leaves, or a new pop-up comes).</summary>
    void CardClose(bool apply = true)
    {
        if (!_cardOpen || _cardChips == null) return;
        _cardOpen = false;
        OsdSub.TextWrapping = TextWrapping.NoWrap;
        OsdSub.MaxHeight = double.PositiveInfinity;
        _cardChips.Visibility = Visibility.Collapsed;
        OsdIconHost.VerticalAlignment = VerticalAlignment.Center;
        OsdIconHost.Margin = new Thickness(0);
        OsdTextPanel.VerticalAlignment = VerticalAlignment.Center;
        OsdTextPanel.Margin = new Thickness(OsdTextPanel.Margin.Left, 0, OsdTextPanel.Margin.Right, 0);
        if (_cardTag != null) { _cardTag.VerticalAlignment = VerticalAlignment.Center; _cardTag.Margin = new Thickness(0); }
        if (!apply || _osd == null) return;
        OsdSub.Text = _osd.Subtitle ?? "";
        OsdCompactFit(_osd); // its size again (NotchWindow.OsdCompact)
        if (_osdMerged > 0) ShowMerged();
    }

    FrameworkElement Chip(CardAction a, int index)
    {
        var accent = a.Tone ?? _osdAccent ?? Color.FromRgb(0x0A, 0x84, 0xFF);
        Brush rest = a.Primary ? Frozen(Color.FromArgb(0x38, accent.R, accent.G, accent.B)) : Frozen(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
        Brush hot = a.Primary ? Frozen(Color.FromArgb(0x5C, accent.R, accent.G, accent.B)) : Frozen(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = a.Glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 9.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0),
            Foreground = a.Primary ? Frozen(Lighten(accent, 0.35)) : Frozen(Color.FromArgb(0xD8, 0xFF, 0xFF, 0xFF)) });
        row.Children.Add(new TextBlock { Text = a.Label, FontSize = 10.5, FontWeight = a.Primary ? FontWeights.SemiBold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center });
        var chip = new Border
        {
            Child = row, CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 3, 9, 3), Margin = new Thickness(0, 0, 6, 0), Background = rest,
            BorderThickness = new Thickness(1), BorderBrush = a.Primary ? Frozen(Color.FromArgb(0x50, accent.R, accent.G, accent.B)) : Frozen(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            Cursor = Cursors.Hand,
        };
        chip.MouseEnter += (_, _) => chip.Background = hot;
        chip.MouseLeave += (_, _) => chip.Background = rest;
        // the chip takes the press itself: a press on it never counts as a click on the whole card
        chip.MouseLeftButtonDown += (_, e) => e.Handled = true;
        chip.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            FeelSound(Services.Sfx.Cue.Tap);
            if (!a.KeepOpen) { _osdHover = false; NextOsd(); }
            a.Run();
        };
        return chip;
    }

    // ───────────── putting a notification together ─────────────

    /// <summary>The card for a notification: who, the tag, the count, and the actions that fit it.</summary>
    ToastLook LookFor(ToastInfo t, SenseResult s, string app, int count, string? code)
    {
        bool person = s.Kind is SenseKind.Person or SenseKind.Group;
        string? tag = s.Urgent ? Loc.T("Urgent") : s.Mention ? Loc.T("For you") : s.Kind == SenseKind.Promo ? Loc.T("Offer") : null;
        Color? tagColor = s.Urgent ? Color.FromRgb(0xFF, 0x5A, 0x4E) : s.Mention ? Color.FromRgb(0xFF, 0xC4, 0x3D) : s.Kind == SenseKind.Promo ? Color.FromRgb(0xA0, 0xA0, 0xA8) : null;

        var actions = new List<CardAction>();
        if (person) { string place = s.Group ?? s.Sender; actions.Add(new CardAction("\uE8BD", Loc.T("Reply"), () => ReplyInPixo(t.App, place), Primary: true)); }
        if (code != null) actions.Add(new CardAction("", Loc.T("Copy code"), () => CopyQuiet(code), Primary: true));
        if (s.JoinUrl is { } join) actions.Add(new CardAction("", Loc.T("Join"), () => Launch(join), Primary: true));
        else if (s.Link is { } link) actions.Add(new CardAction("", Links.Match(link) is { Success: true } lm ? lm.Groups[1].Value : Loc.T("Open link"), () => Launch(link), Primary: actions.Count == 0));
        if (t.Aumid != null) actions.Add(new CardAction("", string.Format(Loc.T("Open {0}"), Short(app, 16)), () => OpenApp(t.Aumid), Primary: actions.Count == 0));
        actions.Add(new CardAction("", Loc.T("Mute 1 h"), () => MuteApp(t.App, app)));

        NotchFace.Emote? feel = s.Urgent ? NotchFace.Emote.Alert : s.Mention ? NotchFace.Emote.Excited : s.Kind == SenseKind.Promo ? NotchFace.Emote.Yawn : code != null ? NotchFace.Emote.Wink : null;
        string full = Clean(s.Text);
        return new ToastLook(person ? Initials(s.Sender) : null, person ? PersonColor(s.Sender) : null, person ? t.Icon : null,
            count, tag, tagColor, full.Length > 0 ? Short(full, 260) : null, actions, feel);
    }

    /// <summary>Developer look (DYNAMICNOTCH_OPEN=toasts): a few sample notifications in a row, the last one opened up.</summary>
    void DevToasts(int skip = 0)
    {
        var ic = Environment.ProcessPath is { } exe ? IconHelper.ForFile(exe) : null;
        var samples = new (int At, ToastInfo T)[]
        {
            (0, new ToastInfo("WhatsApp", null, "Alex Martin", "Are we still on for tonight? 🙂", ic)),
            (2200, new ToastInfo("WhatsApp", null, "Alex Martin", "I can bring the cake", ic)),
            (4200, new ToastInfo("WhatsApp", null, "Alex Martin", "Call me when you're free, it's urgent!", ic)),
            (8200, new ToastInfo("Discord", null, "Sam (#general, Design Club)", "@everyone the notes are up, check them before Monday", ic)),
            (12500, new ToastInfo("Google", null, "Google", "G-482913 is your Google verification code.", ic)),
            (17000, new ToastInfo("Microsoft Store", null, "Weekend deals", "Up to 50% off on games and apps this weekend only", ic)),
            (21000, new ToastInfo("Outlook", null, "Team sync in 5 minutes", "Join: https://teams.microsoft.com/l/meetup-join/abc", ic)),
        };
        int from = samples[Math.Clamp(skip, 0, Math.Max(0, samples.Length - 1))].At;
        foreach (var (at, t) in samples.Skip(skip)) After(14000 + at - from, () => ShowToast(t));
        After(14000 + 21000 - from + 900, () => { if (_mode != Mode.Osd) return; _osdHover = true; _osdTimer.Stop(); OsdCountdownHold(true); CardOpen(); ApplyShape(); });
        After(14000 + 21000 - from + 5200, () => { _osdHover = false; CardClose(); NextOsd(); });
    }

    /// <summary>You went to the app a notification is from: its pop-up has done its job and leaves now.</summary>
    void ToastSeenInApp(IntPtr hwnd)
    {
        if (_mode != Mode.Osd || _osd is not { } r || !r.Kind.StartsWith("toast:") || _osdHover || hwnd == IntPtr.Zero || hwnd == _hwnd) return;
        string app = ChatAppKey(r.Kind[6..]);
        string title = Interop.Native.WindowTitle(hwnd).ToLowerInvariant();
        string exe = "";
        try { exe = System.IO.Path.GetFileNameWithoutExtension(Interop.Native.ProcessPath(hwnd) ?? "").ToLowerInvariant(); } catch { }
        // the app's own words ("Google Chrome" -> chrome, "Microsoft Outlook" -> outlook), the maker's name aside
        var words = app.Split(' ', '.', '-').Where(w => w.Length >= 3 && w is not ("microsoft" or "google" or "app" or "desktop")).ToList();
        if (words.Count > 0 && words.Any(w => title.Contains(w) || exe.Contains(w))) NextOsd();
    }

    void CopyQuiet(string text)
    {
        _ignoreClipboardUntil = DateTime.UtcNow.AddSeconds(1.5); // the notch's own copy: not an alert, not in the history
        try { Clipboard.SetText(text); } catch { return; }
        ShowOsd("copied-code", "", null, Loc.T("Copied"), text, null, glyphBrush: Frozen(Color.FromRgb(0x30, 0xD1, 0x58)), seconds: 1.4, prio: Prio.High, motion: Motion.None);
    }
}
