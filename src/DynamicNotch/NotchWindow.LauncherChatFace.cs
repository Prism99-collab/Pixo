using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DynamicNotch.Controls;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// The AI's face on its welcome page, the same little face as in the notch, big and alive:
///  • it pops in and waves hello (with its hello sound);
///  • its eyes follow the pointer, and while you type it looks down at what you're writing;
///  • under the pointer it smiles; click it and it squashes and frowns, three quick clicks and it's dizzy;
///  • it blinks, glances around, and wears its hat; a soft light glows behind it and its shadow sits under it.
/// Everything stops when the page goes.
/// </summary>
public partial class NotchWindow
{
    FrameworkElement WelcomeFace(Color acc)
    {
        var stage = new Grid { Width = 140, Height = 108, HorizontalAlignment = HorizontalAlignment.Center };
        // a soft light behind it, and its shadow on the ground
        stage.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 132, Height = 100, IsHitTestVisible = false,
            Fill = Frozen(new RadialGradientBrush(A(acc, _cyber ? (byte)0x40 : (byte)0x30), A(acc, 0))),
        });
        var shadow = new System.Windows.Shapes.Ellipse
        {
            Width = 58, Height = 8, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 10), IsHitTestVisible = false,
            Fill = Frozen(new RadialGradientBrush(Color.FromArgb(0x90, 0, 0, 0), Color.FromArgb(0, 0, 0, 0))),
        };
        stage.Children.Add(shadow);

        var face = new NotchFace(54) { Neon = _cyber, Accent = acc, Eco = Eco, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
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
            _launchBox.TextChanged += typed;
            look.Start();
            if (Eco) return;
            // it pops in, then waves
            var grow = new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(520)) { EasingFunction = new BackEase { Amplitude = 0.55, EasingMode = EasingMode.EaseOut } };
            pop.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            pop.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            shadow.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(400)));
            After(260, () =>
            {
                if (!face.IsVisible) return;
                face.Play(NotchFace.Emote.Wave, 2.2);
            });
        };
        stage.Unloaded += (_, _) =>
        {
            _launchBox.TextChanged -= typed;
            look.Stop();
        };
        return stage;
    }
}
