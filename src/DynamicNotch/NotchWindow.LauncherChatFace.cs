using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using DynamicNotch.Controls;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// The AI's face on its welcome page, the same little face as in the notch, big and alive, in its core:
///  • a ring of fine ticks round it with a comet orbiting slowly; while you type the comet speeds up, the ticks light up
///    as far as it has read, and both take the colour of what it understood (NotchWindow.LauncherChatForesee): green for
///    a command it will do at once, the tool's colour for the AI;
///  • it pops in and waves hello (with its hello sound);
///  • its eyes follow the pointer, and while you type it looks at what you're writing; it nods when it gets a command;
///  • under the pointer it smiles; click it and it squashes and frowns, three quick clicks and it's dizzy;
///  • it blinks, glances around, and wears its hat.
/// Everything stops when the page goes. Eco mode keeps the ring still.
/// </summary>
public partial class NotchWindow
{
    /// <summary>The welcome's greeting tells the face what it understood (null: nothing typed).</summary>
    Action<(string Glyph, Color Tone, string Words, bool Instant)?>? _welcomeHeard;

    FrameworkElement WelcomeFace(Color acc)
    {
        const double ringSize = 124, orbitSize = 134;
        var stage = new Grid { Width = 200, Height = 146, HorizontalAlignment = HorizontalAlignment.Center };
        var rest = _cyber ? CyCyan : Lighten(acc, 0.25);

        // a soft light behind it
        stage.Children.Add(new Ellipse
        {
            Width = 112, Height = 112, IsHitTestVisible = false,
            Fill = Frozen(new RadialGradientBrush(A(acc, _cyber ? (byte)0x2A : (byte)0x22), A(acc, 0))),
        });

        // the core: its ring of ticks, and the comet that goes round it
        var ring = new CoreRing { Width = ringSize, Height = ringSize, Tone = rest };
        stage.Children.Add(ring);
        var orbit = new Canvas { Width = orbitSize, Height = orbitSize, IsHitTestVisible = false, RenderTransformOrigin = new Point(0.5, 0.5) };
        var spin = new RotateTransform(-35);
        orbit.RenderTransform = spin;
        var tail = new List<Path>();
        double rad = orbitSize / 2 - 2;
        const int pieces = 8;
        const double span = 64; // degrees, head at the top
        for (int i = 0; i < pieces; i++)
        {
            double a0 = -span + span * i / pieces, a1 = -span + span * (i + 1) / pieces;
            var fig = new PathFigure { StartPoint = OnCircle(a0) };
            fig.Segments.Add(new ArcSegment(OnCircle(a1 + 0.6), new Size(rad, rad), 0, false, SweepDirection.Clockwise, true));
            var p = new Path { Data = new PathGeometry(new[] { fig }), StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            tail.Add(p);
            orbit.Children.Add(p);
        }
        var head = new Ellipse { Width = 5, Height = 5 };
        Canvas.SetLeft(head, orbitSize / 2 - 2.5);
        Canvas.SetTop(head, 2 - 2.5);
        orbit.Children.Add(head);
        stage.Children.Add(orbit);
        Point OnCircle(double deg) { double a = deg * Math.PI / 180; return new Point(orbitSize / 2 + Math.Sin(a) * rad, orbitSize / 2 - Math.Cos(a) * rad); }
        void PaintComet(Color c)
        {
            for (int i = 0; i < pieces; i++) tail[i].Stroke = Frozen(A(c, (byte)(0x10 + 0xD0 * Math.Pow((i + 1) / (double)pieces, 1.6))));
            head.Fill = Frozen(_cyber ? CyYellow : Lighten(c, 0.5));
        }
        PaintComet(rest);

        // its shadow, under the face
        var shadow = new Ellipse
        {
            Width = 50, Height = 6, Margin = new Thickness(0, 74, 0, 0), IsHitTestVisible = false,
            Fill = Frozen(new RadialGradientBrush(Color.FromArgb(0x80, 0, 0, 0), Color.FromArgb(0, 0, 0, 0))),
        };
        stage.Children.Add(shadow);

        var face = new NotchFace(54) { Neon = _cyber, Accent = acc, Eco = Eco, Hud = false, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        face.SetOutfit(FaceOutfitNow(), animate: false);
        var pop = new ScaleTransform(1, 1);
        var hit = new Border
        {
            Background = Brushes.Transparent, Child = face, Cursor = Cursors.Hand, Width = 94, Height = 76,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 6),
            RenderTransformOrigin = new Point(0.5, 0.85), RenderTransform = pop,
        };
        stage.Children.Add(hit);

        // its voice follows what it feels (Services/FaceSound)
        face.Voice += what => FaceSound.Say(what, _cyber);
        DateTime smiledAt = default;
        hit.MouseEnter += (_, _) =>
        {
            if ((DateTime.UtcNow - smiledAt).TotalSeconds < 5) return; // a smile, not one every time the pointer passes
            smiledAt = DateTime.UtcNow;
            face.Play(NotchFace.Emote.Happy, 1.2);
        };
        hit.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            // a squish under the finger, then the poke (three quick ones and it's dizzy)
            if (!Eco)
            {
                var squish = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
                squish.KeyFrames.Add(new EasingDoubleKeyFrame(0.86, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(70)), new CubicEase { EasingMode = EasingMode.EaseOut }));
                squish.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(520)), new SpringEase { Damping = 0.45 }));
                var stretch = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
                stretch.KeyFrames.Add(new EasingDoubleKeyFrame(1.08, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(70)), new CubicEase { EasingMode = EasingMode.EaseOut }));
                stretch.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(520)), new SpringEase { Damping = 0.45 }));
                pop.BeginAnimation(ScaleTransform.ScaleYProperty, squish);
                pop.BeginAnimation(ScaleTransform.ScaleXProperty, stretch);
            }
            face.Poke();
        };

        // what it understood: the ring and the comet take its colour; a command it can do at once gets a nod
        double readTo = 0;
        bool wasInstant = false;
        Color tone = rest;
        Action<(string Glyph, Color Tone, string Words, bool Instant)?> heardIt = heard =>
        {
            var c = heard?.Tone ?? rest;
            if (c != tone) { tone = c; ring.Tone = c; PaintComet(c); }
            bool instant = heard?.Instant == true;
            if (instant && !wasInstant && face.IsVisible) face.Play(NotchFace.Emote.Nod, 0.6);
            wasInstant = instant;
            readTo = heard == null ? 0 : Math.Min(1, (_launchBox?.Text.Trim().Length ?? 0) / 48.0);
            if (Eco) ring.Read = readTo;
        };

        // its eyes follow the pointer; typing, it looks up at the box and follows the caret
        DateTime typedAt = default;
        string lastText = _launchBox?.Text ?? "";
        var look = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(Eco ? 200 : 60) };
        look.Tick += (_, _) =>
        {
            if (!face.IsVisible) return;
            bool typing = (DateTime.UtcNow - typedAt).TotalSeconds < 1.3;
            face.SetMood(typing ? NotchFace.Mood.Focus : NotchFace.Mood.Idle);
            if (typing && _launchBox != null)
            {
                try
                {
                    var r = _launchBox.GetRectFromCharacterIndex(Math.Min(_launchBox.CaretIndex, _launchBox.Text.Length));
                    var at = _launchBox.TranslatePoint(new Point(r.Left, r.Top + r.Height / 2), face);
                    face.LookAt(Math.Clamp((at.X - face.ActualWidth / 2) / 140, -1, 1), Math.Clamp(-(at.Y - face.ActualHeight / 2) / 90, -1, 1));
                }
                catch { }
                return;
            }
            var p = Mouse.GetPosition(face);
            face.LookAt((p.X - face.Width / 2) / 150, -(p.Y - face.Height / 2) / 90);
        };

        // the comet: a slow orbit at rest, quicker while you type; the ticks fill smoothly as far as it has read
        var orbitClock = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(33) };
        double speed = 30; // degrees a second
        var lastTick = DateTime.UtcNow;
        orbitClock.Tick += (_, _) =>
        {
            if (!stage.IsVisible) return;
            var now = DateTime.UtcNow;
            double dt = Math.Min(0.1, (now - lastTick).TotalSeconds);
            lastTick = now;
            bool typing = (now - typedAt).TotalSeconds < 1.3;
            speed += ((typing ? 160 : 30) - speed) * Math.Min(1, dt * 3);
            spin.Angle = (spin.Angle + speed * dt) % 360;
            double gap = readTo - ring.Read;
            if (gap != 0) ring.Read = Math.Abs(gap) < 0.004 ? readTo : ring.Read + gap * Math.Min(1, dt * 8); // (it lands exactly: no tick left lit)
        };

        TextChangedEventHandler typed = (_, _) =>
        {
            string now = _launchBox?.Text ?? "";
            bool idle = (DateTime.UtcNow - typedAt).TotalSeconds > 2.5;
            typedAt = DateTime.UtcNow;
            // it listens: a nod at your first letter, a tilt at a question, a wink when you clear it
            if (now.Length > lastText.Length && now.EndsWith('?')) face.Play(NotchFace.Emote.Confused, 0.8);
            else if (now.Length == 0 && lastText.Length > 2) face.Play(NotchFace.Emote.Wink, 0.8);
            else if (lastText.Length == 0 && now.Length > 0 && idle) face.Play(NotchFace.Emote.Nod, 0.6);
            lastText = now;
        };

        stage.Loaded += (_, _) =>
        {
            if (_launchBox != null) _launchBox.TextChanged += typed;
            _welcomeHeard = heardIt;
            look.Start();
            if (Eco) return;
            lastTick = DateTime.UtcNow;
            orbitClock.Start();
            // it pops in, the ring opens round it, then it waves
            var grow = new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(520)) { EasingFunction = new BackEase { Amplitude = 0.28, EasingMode = EasingMode.EaseOut } };
            pop.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            pop.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            shadow.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(400)));
            var open = new ScaleTransform(0.82, 0.82);
            ring.RenderTransformOrigin = new Point(0.5, 0.5);
            ring.RenderTransform = open;
            var ringIn = new DoubleAnimation(0.82, 1, TimeSpan.FromMilliseconds(640)) { BeginTime = TimeSpan.FromMilliseconds(80), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            open.BeginAnimation(ScaleTransform.ScaleXProperty, ringIn);
            open.BeginAnimation(ScaleTransform.ScaleYProperty, ringIn);
            ring.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(420)) { BeginTime = TimeSpan.FromMilliseconds(80) });
            orbit.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(500)) { BeginTime = TimeSpan.FromMilliseconds(300) });
            After(260, () =>
            {
                if (!face.IsVisible) return;
                face.Play(NotchFace.Emote.Wave, 2.2);
            });
        };
        // hidden (the chat put away, another view): the clocks rest
        stage.IsVisibleChanged += (_, _) =>
        {
            if (!stage.IsLoaded) return;
            if (stage.IsVisible) { look.Start(); if (!Eco) { lastTick = DateTime.UtcNow; orbitClock.Start(); } }
            else { look.Stop(); orbitClock.Stop(); }
        };
        stage.Unloaded += (_, _) =>
        {
            if (_launchBox != null) _launchBox.TextChanged -= typed;
            look.Stop();
            orbitClock.Stop();
            if (_welcomeHeard == heardIt) _welcomeHeard = null;
        };
        return stage;
    }
}
