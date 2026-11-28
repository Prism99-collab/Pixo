using System.IO;
using System.Windows;
using DynamicNotch.Services;
using System.Windows.Threading;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace DynamicNotch;

public partial class App : Application
{
    Mutex? _mutex;
    Forms.NotifyIcon? _tray;
    NotchWindow? _window;
    Settings _settings = new();
    readonly List<(Forms.ToolStripMenuItem, Func<bool>)> _checks = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        // for measuring how it feels on a PC with weak graphics: everything drawn by the processor (DYNAMICNOTCH_SOFTWARE=1)
        if (Environment.GetEnvironmentVariable("DYNAMICNOTCH_SOFTWARE") == "1") System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        // the elevated frame reader for "FPS in games" (Services/FpsBridge): no window, no tray, just the count
        if (e.Args.Contains("--fps-helper"))
        {
            int code = FpsHelper.Run(e.Args);
            Shutdown(code);
            return;
        }
        // a developer look at the notch face's moods and feelings, drawn into one picture
        if (Environment.GetEnvironmentVariable("DYNAMICNOTCH_WAVESHEET") is { Length: > 0 } waves)
        {
            try { NotchWindow.RenderWaveSheet(waves); } catch (Exception ex) { System.IO.File.WriteAllText(waves + ".txt", ex.ToString()); }
            Shutdown();
            return;
        }
        if (Environment.GetEnvironmentVariable("DYNAMICNOTCH_FACESHEET") is { Length: > 0 } sheet)
        {
            try { Controls.NotchFace.RenderSheet(sheet); } catch (Exception ex) { System.IO.File.WriteAllText(sheet + ".txt", ex.ToString()); }
            Shutdown();
            return;
        }
        // a notice from a script (Services/ScriptNotices): handed to the running notch, nothing else starts
        if (e.Args.Contains("--notify", StringComparer.OrdinalIgnoreCase))
        {
            Shutdown(ScriptNotices.Send(e.Args));
            return;
        }
        bool auto = e.Args.Contains(KeepAlive.AutoArg, StringComparer.OrdinalIgnoreCase);
        bool restarted = e.Args.Contains(KeepAlive.RestartArg, StringComparer.OrdinalIgnoreCase);

        if (e.Args.Contains("--quit", StringComparer.OrdinalIgnoreCase))
        {
            AskRunningCopyToQuit();
            Shutdown();
            return;
        }
        if (e.Args.Contains(KeepAlive.UninstallArg, StringComparer.OrdinalIgnoreCase))
        {
            // called by the uninstaller: close the running notch and undo everything it set up in Windows
            AskRunningCopyToQuit(); // it saves everything and closes; only one that doesn't is stopped
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("Pixo").Concat(System.Diagnostics.Process.GetProcessesByName("DynamicNotch")))
                using (p) { if (p.Id != Environment.ProcessId) try { p.Kill(); p.WaitForExit(3000); } catch { } }
            KeepAlive.RemoveAll();
            Shutdown();
            return;
        }

        _mutex = new Mutex(true, "DynamicNotch.SingleInstance", out bool first);
        if (!first)
        {
            // a restart after a crash: give the dying copy a moment to let go, otherwise just leave
            bool got = false;
            if (restarted) try { got = _mutex.WaitOne(TimeSpan.FromSeconds(10)); } catch (AbandonedMutexException) { got = true; }
            if (!got) { Shutdown(); return; }
        }
        // automatic starts are skipped when the user deliberately chose Exit
        if (auto && KeepAlive.UserTurnedOff) { Shutdown(); return; }
        ListenForQuit();
        if (!auto) KeepAlive.ClearUserExit(); // opened by hand (icon, Start menu, login): back on

        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, a) =>
        {
            AppLog.Write($"FATAL (background thread): {a.ExceptionObject}");
            AppLog.Flush(); // (written now: the process is about to end)
            if (KeepAlive.CrashedTooOften()) AppLog.Write("crashed 3 times in 5 minutes: not restarting itself (opened again by hand or at the next login)");
            else KeepAlive.RestartNow(_settings.KeepRunning); // come straight back
            AppLog.Flush();
        };
        TaskScheduler.UnobservedTaskException += (_, a) => { AppLog.Write($"background task error: {a.Exception.GetBaseException()}"); a.SetObserved(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { AppLog.Write($"process exit (code {Environment.ExitCode})"); AppLog.Flush(); };
        SessionEnding += (_, a) => { AppLog.Write($"Windows session ending ({a.ReasonSessionEnding})"); AppLog.Flush(); };
        base.OnStartup(e);
        // the first minute (the PC just started, everything is loading at once): Pixo steps aside for the rest of
        // Windows, then takes its usual step above normal so its animations stay smooth (NotchWindow.RestPriority)
        try
        {
            var me = System.Diagnostics.Process.GetCurrentProcess();
            me.PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal;
            _ = System.Threading.Tasks.Task.Delay(45_000).ContinueWith(_ => { try { if (me.PriorityClass == System.Diagnostics.ProcessPriorityClass.BelowNormal && !NotchWindow.InGameQuiet) { me.PriorityClass = NotchWindow.RestPriority; NotchWindow.GpuPriority(me.Handle, above: true); } } catch { } });
        }
        catch { }

        try { Directory.CreateDirectory(Settings.Folder); } catch { }
        _settings = Settings.Load();
        DynamicNotch.Services.ChatStore.KeepDays = _settings.AiKeepDays;
        _ = System.Threading.Tasks.Task.Run(DynamicNotch.Services.ChatStore.Trim); // old chats go, off the start-up path
        Loc.Set(_settings.Language);
        // WPF caps every animation at 60 fps unless told otherwise: let them run at the screen's real refresh
        // rate (144 / 180 / 240 Hz...). Animations that pick their own rate (visualizer, glow) keep theirs.
        // A PC without graphics hardware (or one starting in battery saver) keeps them at 60: each frame of a
        // see-through window is copied by the CPU there, and 144 of them a second is what made it lag.
        int hz = Interop.Native.RefreshRate();
        bool saver = Interop.Native.GetSystemPowerStatus(out var ps) && ps.SystemStatusFlag == 1;
        if (NotchWindow.LowGraphics || saver) hz = Math.Min(hz, 60);
        System.Windows.Media.Animation.Timeline.DesiredFrameRateProperty.OverrideMetadata(
            typeof(System.Windows.Media.Animation.Timeline), new FrameworkPropertyMetadata { DefaultValue = (int?)hz });
        AppLog.Write($"started ({(restarted ? "after a crash" : auto ? "automatically" : "by user/login")}), pid {Environment.ProcessId}");
        try
        {
            _window = new NotchWindow(_settings);
            _window.Show();
        }
        catch (Exception ex)
        {
            // never run invisibly: tell the user what happened and where the details are
            AppLog.Write($"FATAL (startup): {ex}");
            AppLog.Flush();
            MessageBox.Show($"Pixo couldn't start on this PC.\n\n{ex.GetBaseException().Message}\n\nDetails are in {Path.Combine(Settings.Folder, "app.log")}",
                "Pixo", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        try { BuildTray(); } catch (Exception ex) { AppLog.Write("tray icon skipped: " + ex.Message); }
        KeepAlive.Sync(_settings); // start-at-sign-in and keep-alive, repaired on every launch
    }

    void BuildTray()
    {
        Forms.Application.EnableVisualStyles();
        // the tray menu stays short: open, Preferences (everything else lives there), lock, exit
        var menu = new Forms.ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true };
        menu.Items.Add(Controls.DarkMenu.Header("Pixo"));
        var open = menu.Items.Add("", null, (_, _) => { if (_window?.IsLocked == true) _window.SetLocked(false); _window?.Expand(byHotkey: true); });
        var prefs = (Forms.ToolStripMenuItem)menu.Items.Add("", null, (_, _) => { if (_window != null) PreferencesWindow.ShowFor(_window); });
        prefs.Font = new Drawing.Font("Segoe UI Variable Text", 9.5f, Drawing.FontStyle.Bold);
        var lockItem = new Forms.ToolStripMenuItem("") { CheckOnClick = true };
        lockItem.Click += (_, _) => _window?.SetLocked(lockItem.Checked);
        _checks.Add((lockItem, () => _window?.IsLocked == true));
        menu.Items.Add(lockItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        var exit = menu.Items.Add("", null, (_, _) => ExitApp());
        exit.Tag = "danger";
        void Texts()
        {
            open.Text = Loc.T("Open control center") + "    Win+Alt+N";
            prefs.Text = Loc.T("Preferences…");
            lockItem.Text = Loc.T("Lock the notch (shows things, won't react)");
            exit.Text = Loc.T("Exit");
        }
        Texts();
        menu.Opening += (_, _) => { Texts(); foreach (var (item, get) in _checks) item.Checked = get(); };
        Controls.DarkMenu.Apply(menu);

        _tray = new Forms.NotifyIcon
        {
            Icon = CreateTrayIcon(),
            Text = "Pixo",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button != Forms.MouseButtons.Left || _window == null) return;
            if (_window.IsLocked) _window.SetLocked(false); // a click on the tray icon is how a locked notch comes back
            else _window.Expand(byHotkey: true);
        };
        if (_window != null)
        {
            void ShowLock() { if (_tray != null) _tray.Text = _window.IsLocked ? "Pixo (locked, click to unlock)" : "Pixo"; }
            _window.LockChanged += ShowLock;
            ShowLock();
            // the tray icon (and the windows' icon) in the theme
            _window.CyberChanged += cyber => { if (_tray != null) _tray.Icon = CreateTrayIcon(cyber); };
            if (_settings.Neon) _tray.Icon = CreateTrayIcon(true);
        }
    }

    /// <summary>The notch's own icon, for its windows (Preferences).</summary>
    public static System.Windows.Media.ImageSource? WindowIcon { get; private set; }

    /// <summary>Starts a fresh copy and closes this one (after a reset of the preferences).</summary>
    public void Restart()
    {
        try
        {
            if (Environment.ProcessPath is { } exe)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, KeepAlive.RestartArg) { UseShellExecute = false });
        }
        catch { }
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        _window?.Shutdown();
        Shutdown();
    }

    static Drawing.Icon? _trayIcon;

    /// <summary>
    /// The tray icon: Pixo's own logo (the app's icon file, at the size Windows uses for the tray on this screen).
    /// Made once and reused: drawing a new one for every theme change left Windows icon handles behind.
    /// </summary>
    static Drawing.Icon CreateTrayIcon(bool neon = false)
    {
        if (_trayIcon != null) return _trayIcon;
        try
        {
            var info = GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            using var stream = info.Stream;
            _trayIcon = new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
        }
        catch { _trayIcon = Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? "") ?? Drawing.SystemIcons.Application; }
        try { WindowIcon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/app.ico")); } catch { }
        return _trayIcon;
    }

    const string QuitEventName = "Pixo.Quit";

    /// <summary>Tells the running Pixo to save everything and close, and waits for it (up to 5 s).</summary>
    static void AskRunningCopyToQuit()
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(QuitEventName, out var ev)) return;
            using (ev) ev.Set();
            var others = System.Diagnostics.Process.GetProcessesByName("Pixo").Where(p => p.Id != Environment.ProcessId).ToList();
            foreach (var p in others) using (p) try { p.WaitForExit(5000); } catch { }
        }
        catch { }
    }

    EventWaitHandle? _quitEvent;

    /// <summary>The running copy listens for that request (from an update or the uninstaller).</summary>
    void ListenForQuit()
    {
        try
        {
            _quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
            var t = new Thread(() =>
            {
                _quitEvent.WaitOne();
                Dispatcher.BeginInvoke(() =>
                {
                    AppLog.Write("closing on request (an update or the uninstaller): everything saved first");
                    _window?.SaveEverythingNow();
                    if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
                    _window?.Shutdown();
                    Shutdown();
                });
            }) { IsBackground = true, Name = "Pixo quit listener" };
            t.Start();
        }
        catch (Exception ex) { AppLog.Write("quit listener: " + ex.Message); }
    }

    void ExitApp()
    {
        KeepAlive.MarkUserExit(); // automatic starts respect this until the notch is opened again
        AppLog.Write("exit chosen by user");
        AppLog.Flush();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        _window?.Shutdown();
        Shutdown();
    }

    static readonly Dictionary<string, DateTime> _errorSeen = new();

    static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true; // a notch should never crash the desktop experience
        // the same error again and again (in a timer, in drawing): written once a minute, never filling the disk
        string sig = e.Exception.GetType().FullName + "|" + (e.Exception.StackTrace ?? "").Split('\n').FirstOrDefault();
        var now = DateTime.UtcNow;
        if (_errorSeen.TryGetValue(sig, out var last) && (now - last).TotalSeconds < 60) return;
        _errorSeen[sig] = now;
        if (_errorSeen.Count > 200) _errorSeen.Clear();
        string full = $"[{DateTime.Now:u}] {e.Exception}\n\n";
        Task.Run(() =>
        {
            try
            {
                string path = Path.Combine(Settings.Folder, "error.log");
                Directory.CreateDirectory(Settings.Folder);
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > 512 * 1024) File.Move(path, path + ".old", true); // (the one before kept, then started fresh)
                File.AppendAllText(path, full);
            }
            catch { }
        });
        AppLog.Write("UI error (handled): " + e.Exception.GetBaseException().Message);
    }
}
