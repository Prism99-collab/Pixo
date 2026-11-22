namespace DynamicNotch;

// Developer only (DYNAMICNOTCH_IDLELOG=1): every 5 s, writes to app.log what may keep Pixo drawing while it sits
// still (the springs, the music meter), with the CPU it used meanwhile.
public partial class NotchWindow
{
    static readonly Dictionary<Type, List<System.Windows.DependencyProperty>> PropsOf = new();

    /// <summary>The properties of this object an animation is still running on (not ones that finished and hold their end).</summary>
    static IEnumerable<string> Running(System.Windows.DependencyObject d)
    {
        var storage = typeof(System.Windows.Media.Animation.AnimationTimeline).Assembly.GetType("System.Windows.Media.Animation.AnimationStorage");
        var get = storage?.GetMethod("GetStorage", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var clocksField = storage?.GetField("_animationClocks", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (get == null || clocksField == null) yield break;
        if (!PropsOf.TryGetValue(d.GetType(), out var props))
        {
            props = new List<System.Windows.DependencyProperty>();
            foreach (var f in d.GetType().GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.FlattenHierarchy))
                if (f.FieldType == typeof(System.Windows.DependencyProperty) && f.GetValue(null) is System.Windows.DependencyProperty dp) props.Add(dp);
            PropsOf[d.GetType()] = props;
        }
        foreach (var dp in props)
        {
            object? st;
            try { st = get.Invoke(null, new object[] { d, dp }); } catch { continue; }
            if (st == null || clocksField.GetValue(st) is not System.Collections.IEnumerable clocks) continue;
            foreach (System.Windows.Media.Animation.AnimationClock c in clocks)
                if (c.CurrentState == System.Windows.Media.Animation.ClockState.Active)
                    yield return dp.Name + (c.Timeline.RepeatBehavior == System.Windows.Media.Animation.RepeatBehavior.Forever ? "(forever)" : "");
        }
    }

    void WireIdleProbe()
    {
        if (Environment.GetEnvironmentVariable("DYNAMICNOTCH_IDLELOG") != "1") return;
        var me = System.Diagnostics.Process.GetCurrentProcess();
        double last = me.TotalProcessorTime.TotalSeconds;
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        t.Tick += (_, _) =>
        {
            // what's animating right now (each of these keeps the frames coming)
            var moving = new List<string>();
            void Walk(System.Windows.DependencyObject d, int depth)
            {
                if (moving.Count > 20 || depth > 60) return;
                if (d is System.Windows.UIElement ue)
                {
                    if (ue.HasAnimatedProperties) foreach (var name in Running(ue)) moving.Add(Describe(ue) + "." + name);
                    if (ue.RenderTransform is System.Windows.Media.Animation.Animatable rt && rt.HasAnimatedProperties) foreach (var name in Running(rt)) moving.Add(Describe(ue) + ".transform." + name);
                    if (ue.RenderTransform is System.Windows.Media.TransformGroup tg) foreach (var ch in tg.Children) if (ch.HasAnimatedProperties) foreach (var name in Running(ch)) moving.Add(Describe(ue) + ".transform." + name);
                    if (ue is System.Windows.Shapes.Shape sh2 && sh2.Fill is System.Windows.Media.Animation.Animatable fo && fo.HasAnimatedProperties) foreach (var name in Running(fo)) moving.Add(Describe(ue) + ".fill." + name);
                    if (ue.Effect is System.Windows.Media.Animation.Animatable ef && ef.HasAnimatedProperties) foreach (var name in Running(ef)) moving.Add(Describe(ue) + ".effect." + name);
                }
                int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(d);
                for (int i = 0; i < n; i++) Walk(System.Windows.Media.VisualTreeHelper.GetChild(d, i), depth + 1);
            }
            static string Describe(System.Windows.UIElement u)
            {
                if (u is not System.Windows.FrameworkElement f) return u.GetType().Name;
                string me = f.Name.Length > 0 ? f.Name : f.GetType().Name;
                // the nearest named parent says where it is
                System.Windows.DependencyObject? up = System.Windows.Media.VisualTreeHelper.GetParent(f);
                for (int k = 0; k < 8 && up != null; k++, up = System.Windows.Media.VisualTreeHelper.GetParent(up))
                    if (up is System.Windows.FrameworkElement pf && pf.Name.Length > 0) return me + "<" + pf.Name;
                return me;
            }
            try { foreach (System.Windows.Window w in System.Windows.Application.Current.Windows) if (w.IsVisible) { moving.Add("[" + w.GetType().Name + "]"); Walk(w, 0); } } catch { }
            // (no frame counter here: hooking every frame to count them would itself keep the frames coming)
            Services.AppLog.Write($"idle probe: animations running: {(moving.Count == 0 ? "none" : string.Join(", ", moving.Distinct().Take(20)))}");
            me.Refresh();
            double now = me.TotalProcessorTime.TotalSeconds, used = (now - last) / 5 * 100;
            last = now;
            Services.AppLog.Write($"idle probe: {used:0.0}% of a core | mode {_mode}, springs {(_animating ? "moving" : "still")}, " +
                $"meter {(_meter.IsEnabled ? $"on every {_meter.Interval.TotalMilliseconds:0} ms" : "off")} (bars {_eqRunning}, slim glow {_slimBeat}, call {_callBarsOn}), music {_media.IsPlaying}");
        };
        t.Start();
    }
}
