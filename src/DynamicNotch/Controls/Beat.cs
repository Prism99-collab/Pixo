using System.Windows.Threading;

namespace DynamicNotch.Controls;

/// <summary>
/// One 30-a-second beat shared by what moves at rest at that pace: the face and the music's bars. On timers of their
/// own they ticked out of step, and each tick redrew the whole see-through window, so two 30-frame motions cost 60
/// redraws a second; on one beat their changes land in the same frame: one redraw.
/// </summary>
public static class Beat
{
    static DispatcherTimer? _timer;
    static readonly List<Action> Subs = new();

    public static void Join(Action tick)
    {
        if (Subs.Contains(tick)) return;
        Subs.Add(tick);
        if (_timer == null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(1000.0 / 30) };
            _timer.Tick += (_, _) => { foreach (var s in Subs.ToArray()) s(); };
        }
        if (!_timer.IsEnabled) _timer.Start();
    }

    public static void Leave(Action tick)
    {
        Subs.Remove(tick);
        if (Subs.Count == 0) _timer?.Stop();
    }
}
