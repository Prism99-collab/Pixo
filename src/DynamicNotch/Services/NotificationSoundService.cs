using System.Reflection;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DynamicNotch.Services;

/// <summary>
/// Plays the short sound bundled with incoming notch notifications. Opening an audio device takes
/// 20–100 ms (more on Bluetooth / wireless headsets), so all of it happens on a background thread: the
/// UI thread only asks, and the pop-up animation never waits for the speakers.
/// </summary>
public sealed class NotificationSoundService : IDisposable
{
    const string Prefix = "DynamicNotch.Alerts.";

    /// <summary>The sounds to pick from (key, name), in the order Preferences shows them.</summary>
    public static readonly (string Key, string Name)[] Choices =
        { ("soft", "Soft"), ("bubble", "Bubble"), ("glass", "Glass"), ("wood", "Wood"), ("pluck", "Pluck"), ("ding", "Ding"), ("rise", "Rise"), ("none", "None") };

    readonly Dictionary<string, byte[]> _sounds = new();
    /// <summary>The one alerts play (Settings.AlertSound).</summary>
    public string Choice { get; set; } = "soft";
    readonly object _gate = new();
    WasapiOut? _output;
    WaveFileReader? _reader;
    int _generation;

    public NotificationSoundService()
    {
        var asm = Assembly.GetExecutingAssembly();
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith(Prefix) && n.EndsWith(".wav")))
            try
            {
                using Stream? resource = asm.GetManifestResourceStream(name);
                if (resource == null) continue;
                using var copy = new MemoryStream();
                resource.CopyTo(copy);
                var bytes = copy.ToArray();
                using var check = new WaveFileReader(new MemoryStream(bytes, writable: false));
                _ = check.TotalTime; // a broken file fails here, at startup, not at the first notification
                _sounds[name[Prefix.Length..^4]] = bytes;
            }
            catch (Exception ex) { AppLog.Write($"alert sound {name} failed to load: " + ex.Message); }
        if (_sounds.Count == 0) AppLog.Write("no alert sounds found");
    }

    /// <summary>The chosen alert sound.</summary>
    public void Play() => Play(Choice, alert: true);

    /// <summary>One sound by its key (Preferences plays it as you pick it); "none" stays quiet.</summary>
    public void Play(string key, bool alert = false)
    {
        if (key == "none") return;
        if (!_sounds.TryGetValue(key, out var sound) && !_sounds.TryGetValue("soft", out sound)) return;
        int generation = Interlocked.Increment(ref _generation);
        Task.Run(() =>
        {
            lock (_gate)
            {
                if (generation != _generation) return; // a newer notification already took over
                // one still sounding: let it finish (cutting it off mid-wave is a click, and two in a row say nothing more)
                if (alert && _output?.PlaybackState == PlaybackState.Playing) return; // (picking one in Preferences plays it at once)
                try
                {
                    StopLocked();
                    _reader = new WaveFileReader(new MemoryStream(sound, writable: false));
                    // full-scale stream: Windows plays it at exactly the laptop's volume (and not at all when muted)
                    _output = new WasapiOut(AudioClientShareMode.Shared, useEventSync: false, latency: 80);
                    _output.Init(_reader);
                    _output.Play();
                }
                catch (Exception ex)
                {
                    AppLog.Write("notification sound failed to play: " + ex.Message);
                    StopLocked();
                    return;
                }
            }
            // release the device once the clip is over (it is ~3 s long)
            Task.Delay(_reader?.TotalTime + TimeSpan.FromMilliseconds(300) ?? TimeSpan.FromSeconds(3.3)).ContinueWith(_ =>
            {
                lock (_gate) if (generation == _generation) StopLocked();
            });
        });
    }

    void StopLocked()
    {
        try { _output?.Stop(); } catch { }
        try { _output?.Dispose(); } catch { }
        try { _reader?.Dispose(); } catch { }
        _output = null;
        _reader = null;
    }

    public void Dispose()
    {
        Interlocked.Increment(ref _generation);
        lock (_gate) StopLocked();
    }
}
