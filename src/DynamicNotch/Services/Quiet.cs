namespace DynamicNotch.Services;

/// <summary>
/// Work that can wait: run one after another on a single low-priority thread, a while after Pixo starts,
/// so it never competes with the PC (or Pixo's own animations) while everything is starting.
/// </summary>
static class Quiet
{
    static readonly System.Collections.Concurrent.BlockingCollection<(long at, Action work)> Queue = new();
    static readonly object Gate = new();
    static Thread? _thread;

    /// <summary>Runs <paramref name="work"/> no sooner than <paramref name="afterMs"/> from now, after what's queued before it.</summary>
    public static void Later(int afterMs, Action work)
    {
        lock (Gate)
            if (_thread == null)
            {
                _thread = new Thread(Run) { IsBackground = true, Name = "Pixo quiet work", Priority = ThreadPriority.Lowest };
                _thread.Start();
            }
        Queue.Add((Environment.TickCount64 + afterMs, work));
    }

    static void Run()
    {
        foreach (var (at, work) in Queue.GetConsumingEnumerable())
        {
            long wait = at - Environment.TickCount64;
            if (wait > 0) Thread.Sleep((int)Math.Min(wait, int.MaxValue));
            try { work(); } catch { }
            Thread.Sleep(50); // a breath between jobs
        }
    }
}
