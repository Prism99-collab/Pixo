using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// The AI understanding better and answering better, all decided here on the PC (no request spent on it):
///  • a short follow-up ("and in France?", "what about the pro?", "et pour demain ?") is read with the question before
///    it: same kind of question, and the search is for the whole thing, not the three words;
///  • an answer that asks you something back gets its choices as buttons, however short it is;
///  • before an answer is final it's checked once for the misses that matter: no answer at all, a question about a
///    number or a date answered without one, "I can't look that up" with fresh research in hand, placeholders left in
///    something it built. Such an answer goes back to the AI once, with what's missing;
///  • answers you marked bad lately are shown to the AI, so it doesn't answer like that again.
/// </summary>
public partial class NotchWindow
{
    // ───────────── follow-ups ─────────────

    static readonly Regex FollowUpStart = new(
        @"^\s*(?:and|&|also|plus|then|or|what about|how about|and what about|same (?:for|in|with|but)|but (?:in|for|with)|" +
        @"et|et (?:en|pour|le|la|les|si)|pareil (?:pour|en|avec)|mais (?:en|pour)|y|y (?:en|para)|w|o)\b" +
        @"|^\s*(?:in|for|with|without|at|on|en|pour|avec|sans|à|a|para|con)\s+[^\s?]+(?:\s+[^\s?]+){0,3}\s*\??\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly Regex LeadWords = new(@"^\s*(?:and what about|what about|how about|and|&|also|plus|then|or|et|y|w|o)\b[\s,]*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex OwnQuestion = new(@"^\s*(?:what|what'?s|who|who'?s|when|where|which|why|how|is|are|does|do|did|can|could|should|will|quel(?:le)?s?|qui|quand|o[uù]|pourquoi|comment|combien|est-ce|qu'est|c'est quoi|qu[eé]|qui[eé]n|cu[aá]ndo|d[oó]nde|por qu[eé]|chno|chkoun|fin|wach)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The message continues the last question ("and in France?", "what about the pro?"), as opposed to a command that
    /// starts with "and" ("and open spotify") or a whole new question ("and what's the population of Japan?").
    /// </summary>
    static bool ContinuesQuestion(string text)
    {
        if (!IsFollowUp(text)) return false;
        string core = LeadWords.Replace(text.Trim(), "").Trim();
        if (core.Length == 0 || DoWords.IsMatch(core) || OwnQuestion.IsMatch(core)) return false;
        return AskKind(core) is Ask.Chat or Ask.Fact;
    }

    /// <summary>A short message that only makes sense with the question before it.</summary>
    static bool IsFollowUp(string text)
    {
        string t = text.Trim();
        if (t.Length == 0 || t.Length > 60 || IsSmallTalk(t)) return false;
        return t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 7 && FollowUpStart.IsMatch(t);
    }

    static readonly Regex Preposition = new(@"^(?:in|for|at|on|with|en|pour|à|a|avec|para|con)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// What a follow-up asks, whole: the question before it, changed by its words. "price of the iphone 16 in Morocco" +
    /// "and in France?" → "price of the iphone 16 in France" (the place it replaces goes).
    /// </summary>
    static string FollowUpQuery(string previous, string text)
    {
        string t = Regex.Replace(text.Trim(), @"^\s*(?:and what about|what about|how about|and|&|also|plus|then|et|y|w|o)\b[\s,]*", "", RegexOptions.IgnoreCase).Trim(' ', '?', '.', '!', ',');
        string p = previous.Trim().TrimEnd('?', '.', '!', ' ');
        if (t.Length == 0) return p;
        // the new place, time or thing replaces the old one said the same way ("in Morocco" → "in France")
        if (Preposition.Match(t) is { Success: true } prep)
        {
            var old = Regex.Match(p, @"\s" + Regex.Escape(prep.Value) + @"\s+[^,;]{1,40}$", RegexOptions.IgnoreCase);
            if (old.Success) p = p[..old.Index];
        }
        return (p + " " + t).Trim();
    }

    /// <summary>The user's question before this one in the chat (not the automatic checks), or null.</summary>
    static string? PreviousQuestion(ChatSession session)
    {
        bool skippedCurrent = false;
        for (int i = session.Messages.Count - 1; i >= 0; i--)
        {
            if (session.Messages[i] is not JsonObject m || m["role"]?.ToString() != "user" || m["auto"] != null) continue;
            if (!skippedCurrent) { skippedCurrent = true; continue; } // the message being answered now
            string q = (m["shown"] ?? m["content"])?.ToString() ?? "";
            return q.Trim().Length > 0 && q.Length < 600 ? q.Trim() : null;
        }
        return null;
    }

    // ───────────── a question back ─────────────

    /// <summary>The answer asks the user something (its last line is a question): its options are shown as buttons.</summary>
    static bool AsksBack(string answer)
    {
        var lines = WithoutFollowUps(answer, streaming: false).TrimEnd().Split('\n');
        string last = lines.Length > 0 ? lines[^1].Trim() : "";
        return last.EndsWith('?') || last.EndsWith('？');
    }

    // ───────────── the answer, checked before it's final ─────────────

    static readonly Regex WantsNumber = new(@"\b(how (much|many|old|long|far|big|tall|fast|heavy)|price|prices|cost|costs|when (is|was|will|did|does)|what year|what time|which year|population|salary|rate|score|percentage|combien|prix|quand|quel(le)? ann[ée]e|chhal|bch?hal|cu[aá]nto|precio)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // "I can't browse", "as an AI", "my knowledge cutoff" (not an honest "I can't find an official price")
    static readonly Regex CantLookUp = new(@"\b(i (?:can'?t|cannot|am unable to|'m unable to) (?:browse|access the (?:internet|web)|search the (?:internet|web)|look (?:things|it|that) up)|i don'?t have (?:the ability to browse|access to (?:the )?(?:internet|web|real[- ]time)|real[- ]time)|i do not have (?:access to (?:the )?(?:internet|web)|real[- ]time)|as an ai(?: language model)?|my (?:knowledge|training) (?:cut-?off|data)|je n'ai pas acc[èe]s (?:à|a) (?:internet|internet en temps r[ée]el)|je ne peux pas (?:naviguer|acc[ée]der (?:à|a) internet))\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex NumberWords = new(@"\b(one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|twenty|thirty|forty|fifty|hundred|thousand|million|billion|dozen|un|une|deux|trois|quatre|cinq|six|sept|huit|neuf|dix|cent|mille|milliards?|millions?|uno|dos|tres|cuatro|cinco|diez|mil)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // "[Name]", "[Hiring Manager Name]", "[insert date]" (a markdown link "[text](url)" isn't one)
    static readonly Regex Placeholder = new(@"\[[^\]\n'""]{0,40}\b(?:insert|your|name|date|company|address|phone|email|amount|position|title|city|number)\b[^\]\n'""]{0,40}\](?!\()|lorem ipsum|<placeholder>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex PlaceholderMark = new(@"\bTODO\b|\bXXX\b|\bTBD\b", RegexOptions.Compiled); // (capitals only: "my todo list" is fine)
    static readonly Regex CodeParts = new(@"```[\s\S]*?(```|$)|`[^`\n]*`", RegexOptions.Compiled);

    /// <summary>
    /// What's missing from an answer that's about to be final, said to the AI so it fixes it in one more go; null when
    /// it's fine. Only the misses that are sure (never style): an empty answer, a number asked for and none given, "I
    /// can't search" with research in hand, placeholders in something built.
    /// </summary>
    static string? AnswerGap(Ask kind, string question, string answer, bool hadResearch)
    {
        string a = WithoutFollowUps(answer, streaming: false).Trim();
        if (kind is Ask.Talk or Ask.Action or Ask.Correction) return null; // a chat line, or done with tools: checked elsewhere
        // (a short answer is often the right one: "**144**", "Gracias", "Paris.": only nothing at all is a miss)
        if (a.Length == 0)
            return "Your answer is empty. Give the actual answer now.";
        if (kind is Ask.Fact or Ask.News && hadResearch && CantLookUp.IsMatch(a))
            return "You already have fresh research for this question above: answer from it. Don't say you can't search or look things up.";
        if (kind == Ask.Fact && WantsNumber.IsMatch(question) && !Regex.IsMatch(a, @"\d") && !NumberWords.IsMatch(a) && !AsksBack(a)
            && !Regex.IsMatch(a, @"\b(no (official|public|reliable)|not (announced|released|known|public|found)|unknown|isn'?t (known|public|announced)|(can'?t|cannot|couldn'?t|could not) find|n'est pas (connu|annonc)|pas encore|introuvable|je ne trouve pas)\b", RegexOptions.IgnoreCase))
            return "The question asks for a number or a date, and your answer gives none. Give it from the research (with its source and as of when), or say plainly that no source gives it.";
        string prose = CodeParts.Replace(a, " "); // (code isn't checked: row["name"] in a script is no placeholder)
        if (kind == Ask.Build && (Placeholder.IsMatch(prose) || PlaceholderMark.IsMatch(prose)))
            return "Your answer still has placeholders (like [Name] or 'lorem ipsum'). Fill them with real content; for a detail only the user knows, write a sensible example and say in one line what to change.";
        return null;
    }

    // ───────────── answers you marked bad ─────────────

    static (DateTime Stamp, string Text) _badCache;
    // (used only when "the AI remembers" is on in Preferences: then what you marked bad is part of what it keeps)

    /// <summary>The last few answers marked bad (👎) in the past month, for the AI not to answer like that again.</summary>
    static string RecentBadAnswers()
    {
        try
        {
            string path = Path.Combine(Settings.Folder, "ai-feedback.jsonl");
            if (!File.Exists(path)) return "";
            var stamp = File.GetLastWriteTimeUtc(path);
            if (_badCache.Stamp == stamp) return _badCache.Text;
            var cut = DateTime.Now.AddDays(-30);
            var lines = new List<string>();
            foreach (var line in File.ReadLines(path).Reverse().Take(40))
            {
                try
                {
                    var o = JsonNode.Parse(line);
                    if (o?["at"]?.ToString() is { } at && DateTime.TryParse(at, out var when) && when < cut) continue;
                    string q = o?["question"]?.ToString() ?? "", a = o?["answer"]?.ToString() ?? "";
                    if (q.Length == 0) continue;
                    lines.Add($"- Q: {Clip(q, 100)} → answer marked bad: {Clip(a.Replace('\n', ' '), 120)}");
                    if (lines.Count == 3) break;
                }
                catch { }
            }
            string text = lines.Count == 0 ? "" : "ANSWERS THE USER MARKED BAD lately (work out what was wrong: off-topic, wrong, too long, didn't do it; never answer like that again):\n" + string.Join("\n", lines);
            _badCache = (stamp, text);
            return text;
        }
        catch { return ""; }
        static string Clip(string s, int n) => s.Length > n ? s[..n] + "…" : s;
    }
}
