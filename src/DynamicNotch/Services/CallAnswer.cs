using System.Windows.Automation;

namespace DynamicNotch.Services;

/// <summary>
/// Answers or declines a ringing call by pressing the app's own button (Accept, Answer, Join call, Decline…), found
/// through Windows' accessibility tree, the way <see cref="CallLeave"/> leaves one. Works without bringing the window
/// forward in most apps (Teams, WhatsApp, Zoom, Skype); Chromium-based ones build their tree on the first look, so it
/// looks a few times. Only buttons whose name is one of these words are pressed, and only in the ringing app's windows.
/// </summary>
public static class CallAnswer
{
    // a name that is exactly one of these (after "(Ctrl+…)" and trailing dots are cut)
    static readonly HashSet<string> AnswerExact = new(StringComparer.OrdinalIgnoreCase)
    {
        "Accept", "Answer", "Join call", "Join Call", "Join voice", "Join Voice", "Accept call", "Answer call", "Pick up",
        "Accept with audio", "Accept call with audio", "Accept voice call", "Join with audio",
        "Accepter", "Répondre", "Décrocher", "Rejoindre", "Rejoindre l'appel", "Rejoindre l’appel", "Accepter l'appel", "Accepter l’appel", "Accepter avec l'audio", "Accepter avec l’audio",
        "Aceptar", "Contestar", "Responder", "Atender", "Aceitar",
        "Accetta", "Rispondi", "Annehmen", "Anruf annehmen",
        "قبول", "رد", "الرد",
    };

    // video answers: only when no audio-only one is there
    static readonly HashSet<string> AnswerVideo = new(StringComparer.OrdinalIgnoreCase)
    {
        "Accept with video", "Accept call with video", "Join with video", "Join Video", "Accepter avec la vidéo", "Aceptar con video", "Mit Video annehmen",
    };

    static readonly HashSet<string> DeclineExact = new(StringComparer.OrdinalIgnoreCase)
    {
        "Decline", "Decline call", "Reject", "Reject call", "Ignore call", "Dismiss call",
        "Refuser", "Refuser l'appel", "Refuser l’appel", "Rejeter",
        "Rechazar", "Recusar", "Rejeitar", "Rifiuta", "Ablehnen", "Anruf ablehnen",
        "رفض",
    };

    /// <summary>Presses the Answer button in one of the windows; its name, or null when none was found.</summary>
    public static string? Answer(IReadOnlyList<IntPtr> windows, int looks = 3, bool strict = false) =>
        Press(windows, strict ? Strict : AnswerExact, AnswerVideo, looks);

    // where a plain "Accept" can mean something else (a friend request, an invite): only names that say "call"
    static readonly HashSet<string> Strict = new(AnswerExact.Where(n => n.Contains("call", StringComparison.OrdinalIgnoreCase) || n.Contains("appel", StringComparison.OrdinalIgnoreCase)
        || n.Contains("voice", StringComparison.OrdinalIgnoreCase) || n.Contains("audio", StringComparison.OrdinalIgnoreCase) || n.Contains("Anruf", StringComparison.OrdinalIgnoreCase) || n == "Answer"), StringComparer.OrdinalIgnoreCase);

    /// <summary>Presses the Decline button in one of the windows; its name, or null when none was found.</summary>
    public static string? Decline(IReadOnlyList<IntPtr> windows, int looks = 3) => Press(windows, DeclineExact, null, looks);

    static string? Press(IReadOnlyList<IntPtr> windows, HashSet<string> names, HashSet<string>? fallback, int looks)
    {
        for (int attempt = 0; attempt < looks; attempt++)
        {
            foreach (var w in windows)
                if (Find(w, names) is { } b && Invoke(b) is { } name) return name;
            if (fallback != null)
                foreach (var w in windows)
                    if (Find(w, fallback) is { } b && Invoke(b) is { } name) return name;
            if (attempt < looks - 1) Thread.Sleep(350);
        }
        return null;
    }

    static AutomationElement? Find(IntPtr window, HashSet<string> names)
    {
        try
        {
            var root = AutomationElement.FromHandle(window);
            var buttons = root.FindAll(TreeScope.Descendants, new AndCondition(
                new OrCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.SplitButton)),
                new PropertyCondition(AutomationElement.IsEnabledProperty, true)));
            AutomationElement? hidden = null;
            foreach (AutomationElement b in buttons)
            {
                if (!names.Contains(Clean(b.Current.Name))) continue;
                if (!b.Current.IsOffscreen) return b;
                hidden ??= b;
            }
            return hidden;
        }
        catch { return null; }
    }

    static string? Invoke(AutomationElement b)
    {
        try
        {
            string name = b.Current.Name;
            if (b.TryGetCurrentPattern(InvokePattern.Pattern, out var p)) { ((InvokePattern)p).Invoke(); return name; }
            if (b.TryGetCurrentPattern(TogglePattern.Pattern, out var t)) { ((TogglePattern)t).Toggle(); return name; }
        }
        catch { }
        return null;
    }

    /// <summary>"Accept (Ctrl+Shift+S)" → "Accept"; stray spaces and dots trimmed.</summary>
    static string Clean(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        int cut = s.IndexOf(" (", StringComparison.Ordinal);
        if (cut > 0) s = s[..cut];
        return s.Replace(' ', ' ').Trim().TrimEnd('.', '…').Trim();
    }
}
