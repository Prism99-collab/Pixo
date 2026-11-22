using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace DynamicNotch.Services;

public static class DeviceKinds
{
    /// <summary>The icon for a kind of device (Segoe Fluent Icons).</summary>
    public static string Glyph(this DeviceKind k) => k switch
    {
        DeviceKind.Mouse => "", DeviceKind.Headset or DeviceKind.Earbuds => "", DeviceKind.Keyboard => "",
        DeviceKind.Controller => "", DeviceKind.Speaker => "", _ => "",
    };

    public static string Label(this DeviceKind k) => k switch
    {
        DeviceKind.Mouse => "Mouse", DeviceKind.Headset => "Headset", DeviceKind.Earbuds => "Earbuds", DeviceKind.Keyboard => "Keyboard",
        DeviceKind.Controller => "Controller", DeviceKind.Speaker => "Speaker", _ => "Device",
    };
}

/// <summary>
/// Batteries of wireless mice, keyboards, headsets and controllers of many brands (Logitech, Razer, SteelSeries, Corsair,
/// HyperX, Glorious, Keychron, PlayStation, Xbox and many more), read by BatteryHub (GPL-3.0, by krisgfx9:
/// github.com/krisgfx9/Wireless-Device-Battery-Monitor). It ships unmodified beside Pixo in Tools\BatteryHub and runs
/// as its own program: Pixo starts "BatteryHub.exe --json" now and then, at low priority and with no window, and
/// reads the list it prints. Nothing of it is built into Pixo. Bluetooth devices are left out (Pixo reads those itself).
/// </summary>
public sealed class BatteryHubReader
{
    public static string ToolPath => Path.Combine(AppContext.BaseDirectory, "Tools", "BatteryHub", "BatteryHub.exe");
    public static bool Available => File.Exists(ToolPath);
    static Process? _running;

    /// <summary>Pixo is closing (or updating): a run still going is ended, so its files are never held.</summary>
    public static void StopRunning()
    {
        try { if (_running is { HasExited: false } p) p.Kill(entireProcessTree: true); } catch { }
    }

    /// <summary>One run of the tool (about 5 s): the devices it found, or null when it couldn't run.</summary>
    public static async Task<List<(string Key, DeviceBattery Battery)>?> ReadAsync(CancellationToken stop)
    {
        if (!Available) return null;
        try
        {
            var psi = new ProcessStartInfo(ToolPath, "--json")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(ToolPath)!,
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            _running = p;
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            var read = p.StandardOutput.ReadToEndAsync(stop);
            _ = p.StandardError.ReadToEndAsync(stop);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            try { await p.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { try { p.Kill(entireProcessTree: true); } catch { } return null; }
            return Parse(await read);
        }
        catch (Exception ex) { AppLog.Write("batteryhub: " + ex.Message); return null; }
    }

    static List<(string, DeviceBattery)> Parse(string json)
    {
        var list = new List<(string, DeviceBattery)>();
        int start = json.IndexOf('[');
        if (start < 0) return list;
        using var doc = JsonDocument.Parse(json[start..]);
        foreach (var d in doc.RootElement.EnumerateArray())
        {
            string S(string n) => d.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            bool B(string n) => d.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;
            string via = S("via");
            if (via.Contains("bluetooth", StringComparison.OrdinalIgnoreCase)) continue; // Bluetooth: Pixo's own list has it
            int? pct = d.TryGetProperty("percent", out var pv) && pv.ValueKind == JsonValueKind.Number && pv.TryGetInt32(out int pi) && pi is >= 0 and <= 100 ? pi : null;
            bool connected = !d.TryGetProperty("connected", out var cv) || cv.ValueKind != JsonValueKind.False;
            var state = !connected ? BatteryState.Off : B("asleep") ? BatteryState.Stale : BatteryState.Live;
            var kind = S("kind").ToLowerInvariant() switch
            {
                "mouse" => DeviceKind.Mouse, "headset" or "headphones" => DeviceKind.Headset, "keyboard" => DeviceKind.Keyboard,
                "controller" or "gamepad" => DeviceKind.Controller, "earbuds" => DeviceKind.Earbuds, "speaker" => DeviceKind.Speaker, _ => DeviceKind.Other,
            };
            string name = S("name");
            if (name.Length == 0) continue;
            list.Add((S("key"), new DeviceBattery(kind, name, pct, B("charging"), state, DateTime.UtcNow)));
        }
        return list;
    }
}
