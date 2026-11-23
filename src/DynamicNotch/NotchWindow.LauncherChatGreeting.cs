using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DynamicNotch;

/// <summary>
/// The welcome page's greeting, alive:
///  • the greeting itself decodes in, its letters scrambling through symbols before they settle, left to right;
///    in Neon it has a cyan and magenta ghost that splits apart while it decodes and glitches once in a while after,
///    and a block cursor that blinks after it; in the other themes a light sweeps across the words once;
///  • the line under it types itself out; while you type, it gives way to what the AI understood (a command done at
///    once, or the kind of help it's about to give), with the same colour the ring round the face takes.
/// Everything stops when the page goes. Eco mode shows it all at once, still.
/// </summary>
public partial class NotchWindow
{
    FrameworkElement Greeting()
    {
        var acc = ChatAccent;
        int hour = DateTime.Now.Hour;
        string hello = hour < 5 ? "Up late?" : hour < 12 ? "Good morning" : hour < 18 ? "Good afternoon" : "Good evening";
        string target = _cyber ? hello.ToUpperInvariant().TrimEnd('?') : hello;
        string sub = _settings.AiTools ? "Ask anything, or tell me what to do on your PC." : "Ask me anything.";
        var font = ThemeText;
        double size = _cyber ? 17 : 20;
        var timers = new List<DispatcherTimer>();
        DispatcherTimer Timer(int ms, EventHandler tick) { var t = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(ms) }; t.Tick += tick; timers.Add(t); return t; }

        var root = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 14) };

        // ── status line
        var dot = new System.Windows.Shapes.Ellipse { Width = 6, Height = 6, Fill = Frozen(Ok), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        var status = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 5) };
        status.Children.Add(dot);
        status.Children.Add(new TextBlock
        {
            Text = (_cyber ? "ONLINE  //  " : "Online  ·  ") + (_cyber ? ModelName(ChatModel).ToUpperInvariant() : ModelName(ChatModel)),
            FontSize = 9.5, FontFamily = font, Foreground = Frozen(A(acc, 0xB0)), VerticalAlignment = VerticalAlignment.Center,
        });
        var statusPlate = new Border
        {
            Child = status, HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(9, 2, 10, 3), Margin = new Thickness(0, 0, 0, 7),
            CornerRadius = new CornerRadius(_cyber ? 0 : 9), Background = Frozen(A(acc, 0x14)), BorderBrush = Frozen(A(acc, 0x40)), BorderThickness = new Thickness(1),
        };
        // (no status line: the face says it all)

        // ── the greeting: the words, with two coloured ghosts behind in Neon
        TextBlock Layer(Brush brush) => new() { Text = target, FontSize = size, FontWeight = _cyber ? FontWeights.Bold : FontWeights.SemiBold, FontFamily = font, Foreground = brush, HorizontalAlignment = HorizontalAlignment.Center };
        var stage = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
        TextBlock? ghostA = null, ghostB = null;
        var ta = new TranslateTransform(); var tb = new TranslateTransform();
        if (_cyber)
        {
            ghostA = Layer(Frozen(Color.FromArgb(0xA0, CyCyan.R, CyCyan.G, CyCyan.B)));
            ghostB = Layer(Frozen(Color.FromArgb(0xA0, CyMagenta.R, CyMagenta.G, CyMagenta.B)));
            ghostA.RenderTransform = ta; ghostB.RenderTransform = tb;
            stage.Children.Add(ghostA);
            stage.Children.Add(ghostB);
        }
        // the light that sweeps across (other themes)
        var sweep = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        var white = Color.FromRgb(0xFF, 0xFF, 0xFF);
        var glow = Lighten(acc, 0.45);
        var s0 = new GradientStop(white, 0); var s1 = new GradientStop(white, -0.2); var s2 = new GradientStop(glow, -0.1); var s3 = new GradientStop(white, 0); var s4 = new GradientStop(Lighten(acc, 0.25), 1);
        sweep.GradientStops.Add(s0); sweep.GradientStops.Add(s1); sweep.GradientStops.Add(s2); sweep.GradientStops.Add(s3); sweep.GradientStops.Add(s4);
        var main = Layer(_cyber ? Frozen(CyYellow) : sweep);
        if (_cyber) main.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = CyYellow, BlurRadius = 8, ShadowDepth = 0, Opacity = 0.3 };
        stage.Children.Add(main);

        var line = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        line.Children.Add(stage);
        var cursor = new Border { Width = _cyber ? 7 : 2.5, Height = size * 0.78, Margin = new Thickness(_cyber ? 4 : 3, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center, Background = Frozen(_cyber ? CyYellow : acc) };
        line.Children.Add(cursor);
        root.Children.Add(line);

        // under it: what it can do; while you type, what it understood (it swaps in place, one line, same height)
        var under = new TextBlock { Text = sub, FontSize = 11.5, Foreground = DimBrush, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontFamily = ThemeText };
        var heardGlyph = new TextBlock { FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 6, 0) };
        var heardWords = new TextBlock { FontSize = _cyber ? 11 : 11.5, FontFamily = ThemeText, Foreground = Frozen(Color.FromArgb(0xEE, 0xFF, 0xFF, 0xFF)), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 250 };
        var heardTag = new TextBlock { FontSize = _cyber ? 10 : 10.5, FontFamily = ThemeText, Foreground = DimBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 0, 0) };
        var heard = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = 0, IsHitTestVisible = false };
        heard.Children.Add(heardGlyph);
        heard.Children.Add(heardWords);
        heard.Children.Add(heardTag);
        var second = new Grid { Height = 18, Margin = new Thickness(0, 4, 0, 0) };
        second.Children.Add(under);
        second.Children.Add(heard);
        root.Children.Add(second);

        // while you type: what it would do, read on the PC (NotchWindow.LauncherChatForesee), a moment after the last key
        (string Glyph, Color Tone, string Words, bool Instant)? shown = null;
        var think = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(140) };
        timers.Add(think);
        void Show(bool on)
        {
            if (Eco) { heard.Opacity = on ? 1 : 0; under.Opacity = on ? 0 : 1; return; }
            var t = TimeSpan.FromMilliseconds(on ? 180 : 140);
            heard.BeginAnimation(OpacityProperty, new DoubleAnimation(on ? 1 : 0, t));
            under.BeginAnimation(OpacityProperty, new DoubleAnimation(on ? 0 : 1, t));
        }
        think.Tick += (_, _) =>
        {
            think.Stop();
            string text = _launchBox?.Text ?? "";
            var now = _chatAwaitingKey || _chatView != ChatView.Chat ? null : Foresee(text);
            _welcomeHeard?.Invoke(now);
            if (now == null) { if (shown != null) Show(false); shown = null; return; }
            var (glyph, tone, words, instant) = now.Value;
            heardGlyph.Text = glyph;
            heardGlyph.Foreground = Frozen(tone);
            heardWords.Text = _cyber ? words.ToUpperInvariant() : words;
            heardTag.Text = instant ? (_cyber ? "// INSTANT" : "·  instant, on this PC") : (_cyber ? "// AI" : "·  AI");
            if (shown == null) Show(true);
            shown = now;
        };
        TextChangedEventHandler retype = (_, _) => { think.Stop(); think.Start(); };
        root.Loaded += (_, _) => { if (_launchBox != null) _launchBox.TextChanged += retype; };
        root.Unloaded += (_, _) => { if (_launchBox != null) _launchBox.TextChanged -= retype; };

        if (Eco) { cursor.Visibility = Visibility.Collapsed; return root; }

        // ── it comes alive
        var rnd = new Random();
        const string noise = "▓▒░<>#%&@$01{}[]=+*";
        void SetAll(string t) { main.Text = t; if (ghostA != null) ghostA.Text = t; if (ghostB != null) ghostB.Text = t; }
        void Ghosts(bool on) { if (ghostA != null) ghostA.Opacity = on ? 1 : 0; if (ghostB != null) ghostB.Opacity = on ? 1 : 0; }
        int frame = 0, frames = Math.Max(14, target.Length + 6);
        under.Text = "";
        // the dot breathes
        var pulse = new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(900)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        Timeline.SetDesiredFrameRate(pulse, 20);
        dot.BeginAnimation(OpacityProperty, pulse);
        // the cursor blinks
        var blink = Timer(530, (_, _) => cursor.Opacity = cursor.Opacity > 0.5 ? 0 : 1);
        // the words decode, left to right
        int typed = 0;
        DispatcherTimer typer = null!;
        typer = Timer(16, (_, _) =>
        {
            if (typed >= sub.Length) { typer.Stop(); return; }
            typed = Math.Min(sub.Length, typed + 2);
            under.Text = sub[..typed];
        });
        DispatcherTimer decode = null!;
        decode = Timer(32, (_, _) =>
        {
            frame++;
            int settled = (int)Math.Floor((double)frame / frames * target.Length * 1.15);
            var chars = target.Select((c, i) => i < settled || c == ' ' ? c : noise[rnd.Next(noise.Length)]).ToArray();
            SetAll(new string(chars));
            if (ghostA != null)
            {
                double spread = settled < target.Length ? 2.5 : 0;
                ta.X = -spread - rnd.NextDouble() * spread; ta.Y = (rnd.NextDouble() - 0.5) * spread * 0.6;
                tb.X = spread + rnd.NextDouble() * spread; tb.Y = (rnd.NextDouble() - 0.5) * spread * 0.6;
            }
            if (settled >= target.Length)
            {
                SetAll(target);
                ta.X = tb.X = ta.Y = tb.Y = 0;
                Ghosts(false); // at rest the words stand clean: the ghosts come back only with a glitch
                decode.Stop();
                typer.Start();
            }
        });
        // after that: a glitch now and then (Neon), a light sweeping across (other themes)
        var after = Timer(_cyber ? 4200 : 4800, (sender, _) =>
        {
            if (_cyber)
            {
                int n = 0;
                DispatcherTimer g = null!;
                Ghosts(true);
                g = Timer(45, (_, _) =>
                {
                    n++;
                    ta.X = -1 - rnd.Next(1, 5); tb.X = 1 + rnd.Next(1, 5); ta.Y = rnd.Next(-2, 3); tb.Y = rnd.Next(-2, 3);
                    if (n == 2) { var c = target.ToCharArray(); int at = rnd.Next(c.Length); if (c[at] != ' ') c[at] = noise[rnd.Next(noise.Length)]; SetAll(new string(c)); }
                    if (n >= 5) { SetAll(target); ta.X = tb.X = ta.Y = tb.Y = 0; Ghosts(false); g.Stop(); timers.Remove(g); }
                });
                g.Start();
            }
            else
            {
                var dur = TimeSpan.FromMilliseconds(1300);
                var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
                s1.BeginAnimation(GradientStop.OffsetProperty, new DoubleAnimation(-0.3, 1.1, dur) { EasingFunction = ease });
                s2.BeginAnimation(GradientStop.OffsetProperty, new DoubleAnimation(-0.15, 1.25, dur) { EasingFunction = ease });
                s3.BeginAnimation(GradientStop.OffsetProperty, new DoubleAnimation(0, 1.4, dur) { EasingFunction = ease });
                ((DispatcherTimer)sender!).Stop(); // once: then the page stays still
            }
        });

        root.Loaded += (_, _) =>
        {
            frame = 0;
            Ghosts(true);
            decode.Start();
            blink.Start();
            after.Start();
        };
        root.Unloaded += (_, _) =>
        {
            foreach (var t in timers) t.Stop();
            dot.BeginAnimation(OpacityProperty, null);
        };
        return root;
    }
}
