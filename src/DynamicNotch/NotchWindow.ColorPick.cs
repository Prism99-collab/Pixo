using System.Runtime.InteropServices;
using static DynamicNotch.Interop.Native;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// Pick colour (Tools): the screen holds still, a loupe follows the pointer with the pixels under it eleven times over
/// and their colour as HEX; a click copies it, the arrow keys move one pixel at a time, Esc or a right-click leaves.
/// The picked colour arrives as a pop-up with its swatch, HEX and RGB (click it to copy the RGB instead).
/// The screen is read once, as it was when the picker opened (so the loupe never sees itself); nothing is kept after.
/// </summary>
public partial class NotchWindow
{

    Window? _picker;
    bool _picking; // (set at once: a fast double-click on the tile opens one picker, never two)

    void StartColorPick()
    {
        if (_picking) return;
        _picking = true;
        Collapse();
        // after the notch has closed: it's not part of what you pick from
        After(260, () =>
        {
            int x0 = GetSystemMetrics(76), y0 = GetSystemMetrics(77), w = GetSystemMetrics(78), h = GetSystemMetrics(79);
            if (w <= 0 || h <= 0) { _picking = false; return; }
            int[] px;
            try
            {
                using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                using (var g = System.Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen(x0, y0, 0, 0, new System.Drawing.Size(w, h));
                var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                px = new int[w * h];
                Marshal.Copy(data.Scan0, px, 0, px.Length);
                bmp.UnlockBits(data);
            }
            catch (Exception ex) { AppLog.Write("pick colour: screen not readable: " + ex.Message); _picking = false; return; }
            OpenPicker(px, x0, y0, w, h);
        });
    }

    void OpenPicker(int[] px, int x0, int y0, int w, int h)
    {
        const int Cells = 11, Zoom = 11, Loupe = Cells * Zoom; // 121 px of loupe, the pixel you're on in the middle
        Color At(int sx, int sy)
        {
            int x = Math.Clamp(sx - x0, 0, w - 1), y = Math.Clamp(sy - y0, 0, h - 1);
            int v = px[y * w + x];
            return Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        }

        var zoomed = new WriteableBitmap(Cells, Cells, 96, 96, PixelFormats.Bgr32, null);
        var lens = new Image { Source = zoomed, Width = Loupe, Height = Loupe, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(lens, BitmapScalingMode.NearestNeighbor);
        var mid = (Cells / 2) * Zoom;
        var loupeArt = new Grid { Width = Loupe, Height = Loupe, ClipToBounds = true };
        loupeArt.Children.Add(lens);
        // the pixel you're on: a hairline square, dark outside and light inside, readable on any colour
        loupeArt.Children.Add(new Rectangle { Width = Zoom + 2, Height = Zoom + 2, Stroke = Frozen(new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0))), StrokeThickness = 1, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(mid - 1, mid - 1, 0, 0) });
        loupeArt.Children.Add(new Rectangle { Width = Zoom, Height = Zoom, Stroke = Brushes.White, StrokeThickness = 1, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(mid, mid, 0, 0) });

        var swatch = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(3), BorderBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF))), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center };
        var hex = new TextBlock { FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, Foreground = Frozen(new SolidColorBrush(Color.FromRgb(0xEC, 0xEB, 0xE8))), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var label = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 8, 10, 9), Children = { swatch, hex } };
        var card = new Border
        {
            CornerRadius = new CornerRadius(14), Padding = new Thickness(4, 4, 4, 0), IsHitTestVisible = false,
            Background = Frozen(new SolidColorBrush(Color.FromArgb(0xF4, 0x12, 0x12, 0x13))),
            BorderBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x1E, 0xEC, 0xEB, 0xE8))), BorderThickness = new Thickness(1),
            Child = new StackPanel { Children = { new Border { CornerRadius = new CornerRadius(10), ClipToBounds = true, Child = loupeArt }, label } },
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Direction = 270, Opacity = 0.45, Color = Colors.Black },
        };
        var hint = new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 5, 10, 6), IsHitTestVisible = false,
            Background = Frozen(new SolidColorBrush(Color.FromArgb(0xE8, 0x12, 0x12, 0x13))),
            Child = new TextBlock { Text = Loc.T("Click to copy · arrows move a pixel · Esc to leave"), FontFamily = UiText, FontSize = 11.5, Foreground = Frozen(new SolidColorBrush(Color.FromArgb(0xB0, 0xEC, 0xEB, 0xE8))) },
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 40),
        };
        var canvas = new Canvas();
        canvas.Children.Add(card);
        var root = new Grid { Background = Frozen(new SolidColorBrush(Color.FromArgb(0x01, 0, 0, 0))), Children = { canvas } };

        var win = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, Topmost = true, ShowInTaskbar = false,
            ResizeMode = ResizeMode.NoResize, Cursor = Cursors.Cross, Content = root, ShowActivated = true, Title = "Pick colour",
        };
        _picker = win;
        bool closed = false;
        win.Closed += (_, _) => { closed = true; if (_picker == win) _picker = null; _picking = false; };
        Color now = Colors.Black;
        var bgr = new int[Cells * Cells];

        void Track()
        {
            GetCursorPos(out var c);
            now = At(c.X, c.Y);
            for (int j = 0; j < Cells; j++)
                for (int i = 0; i < Cells; i++)
                {
                    var k = At(c.X + i - Cells / 2, c.Y + j - Cells / 2);
                    bgr[j * Cells + i] = k.R << 16 | k.G << 8 | k.B;
                }
            zoomed.WritePixels(new Int32Rect(0, 0, Cells, Cells), bgr, Cells * 4, 0);
            swatch.Background = new SolidColorBrush(now);
            hex.Text = $"#{now.R:X2}{now.G:X2}{now.B:X2}";
            // beside the pointer, below and to the right; it moves to the other side near an edge of the screen
            var p = win.PointFromScreen(new Point(c.X, c.Y));
            double cw = card.ActualWidth > 0 ? card.ActualWidth : Loupe + 10, ch = card.ActualHeight > 0 ? card.ActualHeight : Loupe + 40;
            double x = p.X + 20, y = p.Y + 20;
            if (x + cw > root.ActualWidth - 8) x = p.X - 20 - cw;
            if (y + ch > root.ActualHeight - 8) y = p.Y - 20 - ch;
            Canvas.SetLeft(card, Math.Round(x));
            Canvas.SetTop(card, Math.Round(y));
        }

        void Close(bool pick)
        {
            if (closed) return;
            closed = true;
            win.Close(); // (whatever else is open: this window always goes)
            if (pick) ColorPicked(now);
        }

        win.SourceInitialized += (_, _) =>
        {
            // over every screen, in real pixels (WPF's own sizes are per screen when they differ)
            var hwnd = new WindowInteropHelper(win).Handle;
            Interop.Native.SetWindowPos(hwnd, IntPtr.Zero, x0, y0, w, h, 0x0010 | 0x0004); // SWP_NOACTIVATE | SWP_NOZORDER
        };
        win.Loaded += (_, _) => { root.Children.Add(hint); win.Activate(); Keyboard.Focus(win); Track(); };
        win.MouseMove += (_, _) => Track();
        win.MouseLeftButtonUp += (_, _) => Close(pick: true);
        win.MouseRightButtonUp += (_, _) => Close(pick: false);
        win.Deactivated += (_, _) => Close(pick: false); // (another window came in front: nothing picked)
        win.PreviewKeyDown += (_, e) =>
        {
            GetCursorPos(out var c);
            int step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
            switch (e.Key)
            {
                case Key.Escape: Close(pick: false); break;
                case Key.Enter or Key.Space: Close(pick: true); break;
                case Key.Left: SetCursorPos(c.X - step, c.Y); Track(); break;
                case Key.Right: SetCursorPos(c.X + step, c.Y); Track(); break;
                case Key.Up: SetCursorPos(c.X, c.Y - step); Track(); break;
                case Key.Down: SetCursorPos(c.X, c.Y + step); Track(); break;
                default: return;
            }
            e.Handled = true;
        };
        win.Show();
    }

    /// <summary>The colour is on the clipboard as HEX; the pop-up shows it, and a click on it copies the RGB.</summary>
    void ColorPicked(Color c)
    {
        string hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}", rgb = $"rgb({c.R}, {c.G}, {c.B})";
        PickCopy(hex);
        AppLog.Write("pick colour: picked");
        ShowOsd("picked", "", null, hex + "  " + Loc.T("copied"), rgb + " · " + Loc.T("click to copy"), null,
            glyphBrush: Frozen(new SolidColorBrush(c)), seconds: 3.2, prio: Prio.High,
            onClick: () => { PickCopy(rgb); ShowOsd("picked", "", null, rgb + "  " + Loc.T("copied"), null, null, glyphBrush: Frozen(new SolidColorBrush(c)), seconds: 1.6, prio: Prio.High); });
    }

    void PickCopy(string text)
    {
        _ignoreClipboardUntil = DateTime.UtcNow.AddMilliseconds(800); // (our own copy: no "Copied" pop-up on top)
        try { Clipboard.SetText(text); } catch (Exception ex) { AppLog.Write("pick colour: clipboard busy: " + ex.Message); }
    }
}
