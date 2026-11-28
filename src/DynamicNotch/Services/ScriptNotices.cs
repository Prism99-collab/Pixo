using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace DynamicNotch.Services;

/// <summary>
/// Notices from your own scripts and builds: <c>Pixo.exe --notify "Build done" [--title CI] [--icon ok|fail|warn|info] [--seconds 4]</c>
/// hands one line to the running notch and exits (0 shown, 1 Pixo isn't running or has them off, 2 nothing to say).
/// The two copies talk over a named pipe only this Windows user can open; the running one reads at most 4 KB per
/// notice, waits 2 s at most for it, and shows plain text only (nothing in a notice is ever run or opened).
/// </summary>
public static class ScriptNotices
{
    public sealed record Notice(string Text, string? Title, string Icon, double Seconds);

    static string PipeName => "Pixo.Notify." + (WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName);
    static readonly string[] Icons = { "info", "ok", "fail", "warn" };

    /// <summary>The second copy: sends what the command line says and returns the exit code.</summary>
    public static int Send(string[] args)
    {
        string? Arg(string name)
        {
            int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        var text = Arg("--notify")?.Trim();
        if (string.IsNullOrEmpty(text)) return 2;
        double.TryParse(Arg("--seconds"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double sec);
        var n = new Notice(text, Arg("--title")?.Trim(), (Arg("--icon") ?? "info").ToLowerInvariant(), sec);
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(1500);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(n);
            if (bytes.Length > 4096) return 2;
            pipe.Write(bytes);
            pipe.Flush();
            return 0;
        }
        catch { return 1; }
    }

    static CancellationTokenSource? _listen;

    /// <summary>The running copy: hands each notice that arrives to <paramref name="show"/> (on a background thread).</summary>
    public static void Listen(bool on, Action<Notice> show)
    {
        _listen?.Cancel();
        _listen = null;
        if (!on) return;
        var cts = _listen = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            int fails = 0;
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(cts.Token);
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                    wait.CancelAfter(2000);
                    var buf = new byte[4097];
                    int got = 0, r;
                    while (got < buf.Length && (r = await pipe.ReadAsync(buf.AsMemory(got), wait.Token)) > 0) got += r;
                    if (got is 0 or > 4096) continue;
                    fails = 0;
                    if (Clean(JsonSerializer.Deserialize<Notice>(Encoding.UTF8.GetString(buf, 0, got))) is { } n) show(n);
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    if (fails++ == 0) AppLog.Write("script notices: " + ex.Message);
                    try { await Task.Delay(Math.Min(60_000, 1000 << Math.Min(fails, 6)), cts.Token); } catch { return; } // (a pipe that won't open: never a busy loop, never a log line a second)
                }
            }
        });
    }

    /// <summary>What a notice may hold: one short line of text, a short title, a known icon, 1.5 to 12 seconds.</summary>
    public static Notice? Clean(Notice? n)
    {
        if (n == null || string.IsNullOrWhiteSpace(n.Text)) return null;
        static string Line(string s, int max)
        {
            var one = string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)); // (new lines, tabs: one space)
            var sb = new StringBuilder(one.Length);
            foreach (var ch in one) if (!char.IsControl(ch)) sb.Append(ch);
            var t = sb.ToString();
            return t.Length <= max ? t : t[..(max - 1)] + "…";
        }
        string? title = string.IsNullOrWhiteSpace(n.Title) ? null : Line(n.Title, 40);
        return new Notice(Line(n.Text, 120), title, Icons.Contains(n.Icon) ? n.Icon : "info", n.Seconds <= 0 ? 4 : Math.Clamp(n.Seconds, 1.5, 12));
    }
}
