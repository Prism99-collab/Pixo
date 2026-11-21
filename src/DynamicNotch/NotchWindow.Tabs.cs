using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DynamicNotch.Controls;

namespace DynamicNotch;

/// <summary>
/// Every group of tabs (Home's Output / Input / Apps, the Shelf / Recent of Clipboard &amp; Files, the Settings page's
/// Display / Behaviour / Smart) is one quiet track with a single highlight in Pixo's colour that glides to the tab you
/// pick, the way the timer's Focus / Break does: no more separate pills each lighting up on its own. The track is drawn
/// behind only the tabs (a gear button beside them stays outside it), once their row is on screen, so pages that
/// arrange the row at start-up (the Settings page's compact head) find it where they left it.
/// </summary>
public partial class NotchWindow
{
    sealed class TabTrack
    {
        public Panel Row = null!;
        public Grid Host = null!;
        public Border Track = null!, Thumb = null!;
        public readonly List<Button> Tabs = new();
        public Button? On;
        public bool Placed;
    }

    readonly ConditionalWeakTable<Panel, TabTrack> _tracks = new();
    readonly ConditionalWeakTable<Button, StrongBox<bool>> _tabWant = new();
    readonly HashSet<Panel> _tabRowsWaiting = new();

    /// <summary>A tab, picked or not. The first time its row is on screen, the row gets its track.</summary>
    void StyleTab(Button tab, bool on)
    {
        _tabWant.AddOrUpdate(tab, new StrongBox<bool>(on));
        if (tab.Parent is Panel row && row.IsLoaded && TrackFor(row) is { } t)
        {
            if (!t.Tabs.Contains(tab))
            {
                t.Tabs.Add(tab);
                tab.Style = (Style)FindResource("Segment");
                tab.ClearValue(BackgroundProperty);
            }
            tab.Foreground = Brushes.White;
            tab.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            if (on && t.On != tab) { bool moved = t.On != null; t.On = tab; PlaceTrack(t, animate: moved && t.Placed); }
            else if (!on && t.On == tab) { t.On = null; PlaceTrack(t, animate: true); }
            else if (!t.Placed) PlaceTrack(t, animate: false);
            return;
        }
        // not on screen yet: the old look for now, and the track as soon as the row shows
        if (on) tab.SetResourceReference(BackgroundProperty, "AccentFill"); // Pixo's colour, and it follows it
        else tab.Background = (Brush)FindResource("Tile");
        tab.Foreground = Brushes.White;
        tab.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        if (tab.Parent is Panel waiting && !waiting.IsLoaded && _tabRowsWaiting.Add(waiting)) waiting.Loaded += OnTabRowLoaded;
    }

    void OnTabRowLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Panel row) return;
        row.Loaded -= OnTabRowLoaded;
        _tabRowsWaiting.Remove(row);
        Dispatcher.BeginInvoke(() =>
        {
            foreach (var b in row.Children.OfType<Button>())
                if (_tabWant.TryGetValue(b, out var want)) StyleTab(b, want.Value);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>The row, moved into a host that draws the track and the highlight behind it (once per row).</summary>
    TabTrack? TrackFor(Panel row)
    {
        if (_tracks.TryGetValue(row, out var have)) return have;
        if (row.Parent is not Panel parent) return null;
        int at = parent.Children.IndexOf(row);
        var host = new Grid
        {
            Margin = row.Margin, HorizontalAlignment = row.HorizontalAlignment, VerticalAlignment = row.VerticalAlignment,
            Visibility = row.Visibility,
        };
        foreach (var dp in new[] { Grid.RowProperty, Grid.ColumnProperty, Grid.RowSpanProperty, Grid.ColumnSpanProperty, DockPanel.DockProperty })
        {
            host.SetValue(dp, row.GetValue(dp));
            row.ClearValue(dp);
        }
        parent.Children.RemoveAt(at);
        row.Margin = new Thickness(0);
        row.HorizontalAlignment = HorizontalAlignment.Left;
        row.VerticalAlignment = VerticalAlignment.Center;
        var layer = new Canvas { IsHitTestVisible = false };
        var track = new Border { BorderThickness = new Thickness(1), Background = Frozen(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)), BorderBrush = Frozen(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)), Opacity = 0 };
        var thumb = new Border { Opacity = 0 };
        thumb.SetResourceReference(Border.BackgroundProperty, "AccentFill");
        layer.Children.Add(track);
        layer.Children.Add(thumb);
        host.Children.Add(layer);
        host.Children.Add(row);
        parent.Children.Insert(at, host);
        var t = new TabTrack { Row = row, Host = host, Track = track, Thumb = thumb };
        _tracks.Add(row, t);
        row.SizeChanged += (_, _) => PlaceTrack(t, animate: false);
        row.IsVisibleChanged += (_, _) => { if (row.IsVisible) PlaceTrack(t, animate: false); };
        return t;
    }

    /// <summary>The track around the tabs; the highlight glides to the picked one (a calm spring), or fades if none is.</summary>
    void PlaceTrack(TabTrack t, bool animate)
    {
        // not laid out (a hidden page, a collapsed row): nothing now; its size or its showing calls back (never a retry loop)
        if (t.Row.ActualWidth <= 0 || !t.Row.IsVisible) return;
        var shown = t.Tabs.Where(b => b.IsVisible && b.ActualWidth > 0).ToList();
        if (shown.Count == 0) return;
        Rect R(Button b) => new(b.TranslatePoint(new Point(0, 0), t.Row), new Size(b.ActualWidth, b.ActualHeight));
        var all = shown.Select(R).Aggregate(Rect.Union);
        const double pad = 2;
        double th = all.Height + pad * 2;
        Canvas.SetLeft(t.Track, all.Left - pad);
        Canvas.SetTop(t.Track, all.Top - pad);
        t.Track.Width = all.Width + pad * 2;
        t.Track.Height = th;
        t.Track.CornerRadius = new CornerRadius(_cyber ? 0 : th / 2);
        t.Track.Opacity = 1;
        t.Placed = true;

        if (t.On == null || !t.On.IsVisible)
        {
            t.Thumb.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(animate && !Eco ? 180 : 1)));
            return;
        }
        var r = R(t.On);
        t.Thumb.Height = r.Height;
        Canvas.SetTop(t.Thumb, r.Top);
        t.Thumb.CornerRadius = new CornerRadius(_cyber ? 0 : r.Height / 2);
        bool show = t.Thumb.Opacity < 0.5;
        if (!animate || Eco || show)
        {
            t.Thumb.BeginAnimation(Canvas.LeftProperty, null);
            t.Thumb.BeginAnimation(WidthProperty, null);
            Canvas.SetLeft(t.Thumb, r.Left);
            t.Thumb.Width = r.Width;
            t.Thumb.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(animate && !Eco ? 160 : 1)));
            return;
        }
        var spring = new SpringEase { Damping = 0.82 };
        var span = TimeSpan.FromMilliseconds(360);
        t.Thumb.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(r.Left, span) { EasingFunction = spring });
        t.Thumb.BeginAnimation(WidthProperty, new DoubleAnimation(r.Width, span) { EasingFunction = spring });
    }
}
