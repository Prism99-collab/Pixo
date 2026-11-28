using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace DynamicNotch.Services;

/// <summary>
/// The Terminal page's shell: one PowerShell kept open in the background (PowerShell 7 when it's installed, else the
/// Windows PowerShell every PC has), fed one command at a time. The folder and your variables stay from one command to
/// the next. After each command a marker line comes back with its exit code and the folder it ended in; anything
/// written to the error stream, or a non-zero exit code, marks the command as failed.
/// It is not a console: programs that wait for keys (an editor, a REPL, a password prompt) belong in Windows Terminal,
/// and Stop ends the shell and starts a fresh one in the same folder. Colour codes are removed from the text.
/// </summary>
public sealed class ShellSession : IDisposable
{
    const char Mark = '\u0001';
    static readonly Regex Ansi = new(@"\x1B(\[[0-9;?]*[ -/]*[@-~]|\][^\x07\x1B]*(\x07|\x1B\\)|[@-Z\\-_])", RegexOptions.Compiled);

    Process? _p;
    int _gen; // (each shell started: lines of an ended one are ignored)
    public string Folder { get; private set; }
    public bool Busy { get; private set; }
    public string ShellName { get; private set; } = "PowerShell";

    /// <summary>A line of output (true: from the error stream), on a background thread.</summary>
    public event Action<string, bool>? Line;
    /// <summary>The command ended: its exit code, whether it failed, the folder it left you in (background thread).</summary>
    public event Action<int, bool, string>? Done;

    bool _errSeen, _outEnded, _errEnded;
    int _endCode;
    readonly object _gate = new();
    const string ErrMark = "\u0001PIXOERR";

    public ShellSession(string folder) => Folder = Directory.Exists(folder) ? folder : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    static string? Pwsh7()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            try { var f = Path.Combine(dir.Trim(), "pwsh.exe"); if (File.Exists(f)) return f; } catch { }
        var pf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
        return File.Exists(pf) ? pf : null;
    }

    void Start()
    {
        var exe = Pwsh7();
        ShellName = exe != null ? "PowerShell 7" : "Windows PowerShell";
        var psi = new ProcessStartInfo(exe ?? "powershell.exe", "-NoLogo -NoProfile -Command -")
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Folder,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, StandardInputEncoding = new UTF8Encoding(false),
        };
        psi.Environment["NO_COLOR"] = "1";
        psi.Environment["TERM"] = "dumb";
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        int gen = ++_gen;
        p.OutputDataReceived += (_, e) => { if (e.Data != null && gen == _gen) OnOut(e.Data); };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null || gen != _gen) return;
            int at = e.Data.IndexOf(ErrMark, StringComparison.Ordinal);
            if (at < 0) { lock (_gate) _errSeen = true; Line?.Invoke(Clean(e.Data), true); return; }
            if (at > 0) { lock (_gate) _errSeen = true; Line?.Invoke(Clean(e.Data[..at]), true); }
            lock (_gate) _errEnded = true;
            TryEnd();
        };
        p.Exited += (_, _) =>
        {
            if (gen != _gen) return;
            _p = null;
            p.Dispose();
            if (Busy) { Busy = false; Done?.Invoke(-1, true, Folder); } // (the shell itself ended: "exit", or it was closed)
        };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        _p = p;
        // plain text, UTF-8 both ways, no progress bars, tables only as wide as they need
        Write("[Console]::InputEncoding = [Text.Encoding]::UTF8; [Console]::OutputEncoding = [Text.Encoding]::UTF8; $OutputEncoding = [Text.Encoding]::UTF8; $ProgressPreference = 'SilentlyContinue'; " +
              "$PSDefaultParameterValues['Format-Table:AutoSize'] = $true; if ($PSStyle) { $PSStyle.OutputRendering = 'PlainText' }");
    }

    void Write(string line)
    {
        try { _p?.StandardInput.WriteLine(line); _p?.StandardInput.Flush(); } catch (Exception ex) { AppLog.Write("terminal: " + ex.Message); }
    }

    void OnOut(string data)
    {
        // the marker: "PIXO <exit code> <folder>", at the start of its line or after output that had no line end
        int at = data.IndexOf(Mark + "PIXO ", StringComparison.Ordinal);
        if (at < 0) { Line?.Invoke(Clean(data), false); return; }
        if (at > 0) Line?.Invoke(Clean(data[..at]), false);
        var rest = data[(at + 6)..];
        int sp = rest.IndexOf(' ');
        int code = int.TryParse(sp > 0 ? rest[..sp] : rest, out var c) ? c : 0;
        if (sp > 0 && rest[(sp + 1)..] is { Length: > 0 } dir) Folder = dir;
        lock (_gate) { _endCode = code; _outEnded = true; }
        TryEnd();
    }

    /// <summary>The command is over once both streams said so (errors can come in after the output's marker).</summary>
    void TryEnd()
    {
        int code; bool failed;
        lock (_gate)
        {
            if (!_outEnded || !_errEnded || !Busy) return;
            code = _endCode;
            failed = code != 0 || _errSeen;
            Busy = false;
        }
        Done?.Invoke(code, failed, Folder);
    }

    static string Clean(string s) => Ansi.Replace(s, "").TrimEnd();

    /// <summary>Runs one line. False while another is still running.</summary>
    public bool Run(string command)
    {
        if (Busy || string.IsNullOrWhiteSpace(command)) return false;
        if (_p == null || _p.HasExited) Start();
        Busy = true;
        lock (_gate) { _errSeen = false; _outEnded = false; _errEnded = false; }
        Write("$global:LASTEXITCODE = 0");
        Write(command.Replace('\r', ' ').Replace('\n', ' '));
        Write("\"$([char]1)PIXO $(if ($null -eq $LASTEXITCODE) { 0 } else { $LASTEXITCODE }) $((Get-Location).ProviderPath)\"");
        Write("[Console]::Error.WriteLine(\"$([char]1)PIXOERR\")");
        return true;
    }

    /// <summary>Ends what's running (the shell and everything it started), and the next command starts a fresh one here.</summary>
    public void Stop()
    {
        var p = _p;
        _p = null;
        _gen++;
        try { p?.Kill(entireProcessTree: true); } catch { }
        p?.Dispose();
        if (Busy) { Busy = false; Done?.Invoke(-1, true, Folder); }
    }

    public void Dispose() => Stop();
}
