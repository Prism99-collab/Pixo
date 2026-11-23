using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace DynamicNotch.Services;

public sealed record ToastInfo(string App, string? Aumid, string Title, string Body, ImageSource? Icon);

/// <summary>
/// Mirrors Windows toast notifications (Teams, Outlook, WhatsApp, Discord...).
/// Uses the instant change event when Windows delivers it, otherwise a light poll; it detects
/// which one actually works on this PC and adapts.
/// </summary>
public sealed class NotificationService
{
    readonly Dispatcher _ui;
    UserNotificationListener? _listener;
    readonly HashSet<uint> _seen = new();
    // what each one said last time: apps like Discord keep ONE notification per conversation and rewrite it with every
    // new message (same id), so a new id alone would miss every message after the first
    readonly Dictionary<uint, string> _said = new();
    // what was last passed on for each one (its title and text): rewritten with the same words isn't news
    readonly Dictionary<uint, string> _told = new();
    DispatcherTimer? _poll;
    TimeSpan _interval = TimeSpan.FromSeconds(2);
    bool _primed, _eventMode, _checking, _again;
    DateTimeOffset _lastLook; // when the notifications were last listed: anything created after it is new
    DateTime _lastEvent;

    public bool Available { get; private set; }
    public string Status { get; private set; } = "not started";
    public event Action<ToastInfo>? Received;

    public NotificationService(Dispatcher ui) => _ui = ui;

    /// <summary>How often to look for new notifications when events aren't available (slower on battery).</summary>
    public void SetInterval(TimeSpan interval)
    {
        _interval = interval;
        ApplyInterval();
    }

    void ApplyInterval()
    {
        if (_poll == null) return;
        // with live events the poll is only a safety net
        var pace = _eventMode ? TimeSpan.FromSeconds(Math.Max(10, _interval.TotalSeconds * 3)) : _interval;
        // nobody has touched the keyboard or mouse for a minute: nobody is reading, so a slower look is enough (every
        // notification is still found, a few seconds later); the next look after you're back is at the brisk pace again
        if (!_eventMode && Interop.Native.IdleTime() > TimeSpan.FromSeconds(60) && pace < IdlePace) pace = IdlePace;
        if (_poll.Interval != pace) _poll.Interval = pace; // (setting it restarts the wait, so only when it changes)
    }

    static readonly TimeSpan IdlePace = TimeSpan.FromSeconds(8);
    // how long a notification counts as fresh (its words read on every look), and how often every other one is read
    static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(5), SweepEvery = TimeSpan.FromSeconds(10);
    DateTime _lastSweep;

    public async Task InitAsync()
    {
        try
        {
            _listener = UserNotificationListener.Current;
            // unpackaged apps can wait forever for this consent; don't hang on it
            var request = _listener.RequestAccessAsync().AsTask();
            if (await Task.WhenAny(request, Task.Delay(4000)) != request) { Status = "no answer from Windows"; return; }
            var access = request.Result;
            Status = access.ToString();
            if (access != UserNotificationListenerAccessStatus.Allowed) return;
            Available = true;
            await CheckAsync(fromEvent: false);
            _primed = true;

            try
            {
                _listener.NotificationChanged += (_, _) => _ui.InvokeAsync(async () =>
                {
                    _lastEvent = DateTime.UtcNow;
                    await CheckAsync(fromEvent: true);
                });
                _eventMode = true;
            }
            catch { _eventMode = false; }
            Status += _eventMode ? " · live events" : " · polling";

            // (normal priority: at "background" it waited behind a streaming AI answer or an animation, long enough for
            // a message Discord shows and clears once read to come and go unseen)
            _poll = new DispatcherTimer(DispatcherPriority.Normal);
            _poll.Tick += async (_, _) => { ApplyInterval(); await CheckAsync(fromEvent: false); };
            ApplyInterval();
            _poll.Start();
        }
        catch (Exception ex)
        {
            Status = ex.GetType().Name + ": " + ex.Message;
            Available = false;
        }
    }

    async Task CheckAsync(bool fromEvent)
    {
        if (_listener == null) return;
        if (_checking) { _again = true; return; }
        _checking = true;
        try
        {
            do
            {
                _again = false;
                // asked and read off the screen's thread: every notification waiting in the Action Center is read each
                // time (to see one being rewritten), and with dozens there that's many calls to Windows
                var looked = DateTimeOffset.Now; // (taken before asking: one that arrives while the list is read is new next time)
                if (_lastLook == default) _lastLook = looked - TimeSpan.FromSeconds(30); // (the first look after start: the last 30 s count)
                var listener = _listener;
                // reading a notification's words is several calls to Windows each, so not every one is read on every
                // look: a new one, a fresh one (the last few minutes, where a chat app's rewrites mostly land), all of
                // them when Windows announced a change, and all of them every few seconds anyway, so an old one being
                // rewritten (Discord's one-per-conversation) is still caught, only a few seconds later at worst
                bool sweep = fromEvent || !_primed || DateTime.UtcNow - _lastSweep >= SweepEvery;
                if (sweep) _lastSweep = DateTime.UtcNow;
                var known = new HashSet<uint>(_said.Keys);
                var freshSince = looked - FreshFor;
                List<(UserNotification N, string? Said, DateTimeOffset Created)> list;
                try
                {
                    list = await Task.Run(async () =>
                    {
                        var all = await listener.GetNotificationsAsync(NotificationKinds.Toast);
                        return all.Select(n =>
                        {
                            var created = n.CreationTime;
                            string? said = sweep || created >= freshSince || !known.Contains(n.Id) ? Words(n) : null; // (null: not read this time)
                            return (n, said, created);
                        }).ToList();
                    });
                }
                catch { return; }

                var current = new HashSet<uint>();
                foreach (var (n, said, created) in list)
                {
                    current.Add(n.Id);
                    bool isNew = _seen.Add(n.Id);
                    bool rewritten = !isNew && said != null && _said.TryGetValue(n.Id, out var before) && before != said && said.Length > 0;
                    if (said != null) _said[n.Id] = said;
                    if (!_primed || !isNew && !rewritten) continue;
                    // new to Pixo: it's news if it came after Pixo's last look (however late this look is), not one that was
                    // already there and only now listed (a rewritten one is new news, whatever its age)
                    if (isNew && created < _lastLook - TimeSpan.FromSeconds(10))
                    {
                        AppLog.Write($"notification: {n.AppInfo?.DisplayInfo?.DisplayName ?? "?"} (skipped: {(DateTimeOffset.Now - created).TotalSeconds:0} s old, from before the last look)");
                        continue;
                    }

                    // the safety-net poll found something the event never announced: events don't work here
                    if (!fromEvent && _eventMode && (DateTime.UtcNow - _lastEvent).TotalSeconds > 3)
                    {
                        _eventMode = false;
                        Status = Status.Replace("live events", "polling");
                        ApplyInterval();
                    }

                    var info = await Task.Run(() => ReadAsync(n)); // (its words and its picture, off the screen's thread)
                    if (info != null && _told.TryGetValue(n.Id, out var told) && told == info.Title + "\n" + info.Body) continue; // the same words again
                    if (info != null) _told[n.Id] = info.Title + "\n" + info.Body;
                    if (info != null)
                    {
                        AppLog.Write($"notification: {info.App} ({(rewritten ? "updated" : "new")})"); // (the app only: what it says stays private)
                        Received?.Invoke(info);
                    }
                }
                _seen.IntersectWith(current);
                _lastLook = looked;
                foreach (var gone in _said.Keys.Where(k => !current.Contains(k)).ToList()) { _said.Remove(gone); _told.Remove(gone); }
            } while (_again);
        }
        finally { _checking = false; }
    }

    /// <summary>A notification's words, as one string (to see when an app rewrites it).</summary>
    static string Words(UserNotification n)
    {
        try
        {
            var binding = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
            return binding == null ? "" : string.Join("\u001f", binding.GetTextElements().Select(t => t.Text));
        }
        catch { return ""; }
    }

    static async Task<ToastInfo?> ReadAsync(UserNotification n)
    {
        try
        {
            string app = n.AppInfo?.DisplayInfo?.DisplayName ?? "Notification";
            string? aumid = null;
            try { aumid = n.AppInfo?.AppUserModelId; } catch { }
            var binding = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
            var texts = binding?.GetTextElements().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList() ?? new();
            string title = texts.Count > 0 ? texts[0] : app;
            string body = string.Join(" · ", texts.Skip(1));
            ImageSource? icon = null;
            try
            {
                var logo = n.AppInfo?.DisplayInfo?.GetLogo(new Windows.Foundation.Size(256, 256));
                if (logo != null)
                {
                    using var ras = await logo.OpenReadAsync();
                    using var s = ras.AsStreamForRead();
                    var ms = new MemoryStream();
                    await s.CopyToAsync(ms);
                    ms.Position = 0;
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.DecodePixelWidth = 128; // crisp at any display scaling
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                    bmp.Freeze();
                    icon = bmp;
                }
            }
            catch { }
            return new ToastInfo(app, aumid, title, body, icon);
        }
        catch { return null; }
    }
}
