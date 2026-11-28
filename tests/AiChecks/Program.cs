// The AI's checks: how messages are read, the calendar, and reading documents. Exit code 1 when something fails.
using System.IO;
using System.Reflection;
using DynamicNotch;
using System.Text.Json.Nodes;
using DynamicNotch.Services;

int failed = 0, passed = 0;
void Check(bool ok, string what) { if (ok) passed++; else { failed++; Console.WriteLine("FAIL " + what); } }

// ───────── how a message is read ─────────
var askKind = typeof(DynamicNotch.NotchWindow).GetMethod("AskKind", BindingFlags.NonPublic | BindingFlags.Static)!;
foreach (var raw in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "cases.txt")))
{
    if (raw.Trim().Length == 0 || raw.TrimStart().StartsWith('#')) continue;
    var parts = raw.Split('|', 3).Select(p => p.Trim()).ToArray();
    if (parts.Length < 3) continue;
    var (kind, play, msg) = (parts[0], parts[1], parts[2]);
    string got = askKind.Invoke(null, new object[] { msg })!.ToString()!;
    var plays = AiPlaybook.For(msg).Select(p => p.Name).ToList();
    Check(kind == "*" || got == kind, $"“{msg}” read as {got}, should be {kind}");
    if (play == "-") Check(plays.Count == 0, $"“{msg}” pulls in know-how it shouldn't: {string.Join(", ", plays)}");
    else if (play.Length > 0) Check(plays.Contains(play), $"“{msg}” misses the know-how “{play}” (got: {string.Join(", ", plays)})");
}

// ───────── the calendar ─────────
var d = DateTime.Today;
string Day(DateTime t) => t.ToString("yyyyMMdd");
string ics = $"""
BEGIN:VCALENDAR
X-WR-CALNAME:Test
BEGIN:VEVENT
UID:one
DTSTART:{Day(d)}T{DateTime.Today.AddHours(10).ToUniversalTime():HHmmss}Z
DTEND:{Day(d)}T{DateTime.Today.AddHours(11).ToUniversalTime():HHmmss}Z
SUMMARY:Standup\, team
LOCATION:Room 2
END:VEVENT
BEGIN:VEVENT
UID:weekly
DTSTART;TZID=Africa/Casablanca:{Day(d.AddDays(-21))}T140000
DTEND;TZID=Africa/Casablanca:{Day(d.AddDays(-21))}T150000
RRULE:FREQ=WEEKLY;BYDAY={string.Join(",", new[] { "SU", "MO", "TU", "WE", "TH", "FR", "SA" })}
EXDATE;TZID=Africa/Casablanca:{Day(d.AddDays(1))}T140000
SUMMARY:Every day but tomorrow
END:VEVENT
BEGIN:VEVENT
UID:daily3
DTSTART:{Day(d.AddDays(-1))}T080000
RRULE:FREQ=DAILY;COUNT=3
SUMMARY:Three mornings
END:VEVENT
BEGIN:VEVENT
UID:allday
DTSTART;VALUE=DATE:{Day(d)}
DTEND;VALUE=DATE:{Day(d.AddDays(1))}
SUMMARY:Holiday
END:VEVENT
BEGIN:VEVENT
UID:gone
DTSTART:{Day(d)}T170000
SUMMARY:Cancelled one
STATUS:CANCELLED
END:VEVENT
BEGIN:VEVENT
UID:weekly
RECURRENCE-ID;TZID=Africa/Casablanca:{Day(d.AddDays(2))}T140000
DTSTART;TZID=Africa/Casablanca:{Day(d.AddDays(2))}T160000
DTEND;TZID=Africa/Casablanca:{Day(d.AddDays(2))}T170000
SUMMARY:Moved one
END:VEVENT
END:VCALENDAR
""";
var today = CalendarFeed.Parse(ics, d, d.AddDays(1));
Check(today.Any(e => e.Title == "Standup, team" && e.Start == d.AddHours(10) && e.Location == "Room 2"), "calendar: a timed event (UTC) at its local time, with its place");
Check(today.Count(e => e.Title == "Every day but tomorrow") == 1, "calendar: a weekly repeat shows today");
Check(today.Any(e => e.Title == "Holiday" && e.AllDay), "calendar: an all-day event");
Check(!today.Any(e => e.Title == "Cancelled one"), "calendar: a cancelled event is left out");
Check(today.Count(e => e.Title == "Three mornings") == 1, "calendar: a daily repeat with a count");
var tomorrow = CalendarFeed.Parse(ics, d.AddDays(1), d.AddDays(2));
Check(!tomorrow.Any(e => e.Title == "Every day but tomorrow"), "calendar: an excluded date (EXDATE)");
Check(tomorrow.Any(e => e.Title == "Three mornings"), "calendar: the count's last day");
Check(!CalendarFeed.Parse(ics, d.AddDays(2), d.AddDays(3)).Any(e => e.Title == "Three mornings"), "calendar: nothing after the count");
var later = CalendarFeed.Parse(ics, d.AddDays(2), d.AddDays(3));
Check(later.Any(e => e.Title == "Moved one") && !later.Any(e => e.Title == "Every day but tomorrow"), "calendar: a moved occurrence replaces the usual one");
string old = $"""
BEGIN:VCALENDAR
BEGIN:VEVENT
UID:o1
DTSTART:20120105T073000
RRULE:FREQ=DAILY
SUMMARY:Since 2012
END:VEVENT
BEGIN:VEVENT
UID:o2
DTSTART:{d.AddYears(-12):yyyyMMdd}T090000
RRULE:FREQ=MONTHLY
SUMMARY:Monthly for years
END:VEVENT
BEGIN:VEVENT
UID:o3
DTSTART:{d.AddYears(-30):yyyyMMdd}
RRULE:FREQ=YEARLY
SUMMARY:Birthday
END:VEVENT
END:VCALENDAR
""";
var oldToday = CalendarFeed.Parse(old, d, d.AddDays(1));
Check(oldToday.Any(e => e.Title == "Since 2012" && e.Start == d.AddHours(7.5)), "calendar: a daily event started years ago still shows today");
Check(oldToday.Any(e => e.Title == "Monthly for years" && e.Start == d.AddHours(9)), "calendar: a monthly event started years ago shows on its day");
Check(oldToday.Any(e => e.Title == "Birthday" && e.AllDay), "calendar: a yearly all-day event (a birthday) from long ago");
var sw = System.Diagnostics.Stopwatch.StartNew();
for (int i = 0; i < 50; i++) CalendarFeed.Parse(old, d, d.AddDays(7));
Check(sw.ElapsedMilliseconds < 1500, $"calendar: reading is quick ({sw.ElapsedMilliseconds} ms for 50 reads)");
Check(CalendarFeed.FindLink("here https://calendar.google.com/calendar/ical/abc%40gmail.com/private-123/basic.ics thanks") is { } l && l.EndsWith("basic.ics"), "calendar: a pasted Google link is recognised");
Check(CalendarFeed.FindLink("open https://www.youtube.com/watch?v=abc") == null, "calendar: other links aren't taken for a calendar");

// ───────── deep search: the passages that answer the question ─────────
string page = "Welcome to our site. Subscribe to our newsletter for updates.\n\nThe company was founded in 2003 and makes cars.\n\nThe Model Y price starts at $44,990 in the US, the company said in its latest update.\n\nOur privacy policy and cookie settings. All rights reserved.";
string kept = WebTools.Passages(page, WebTools.KeyWords("what's the price of the tesla model y"), 400);
Check(kept.Contains("$44,990") && !kept.Contains("newsletter") && !kept.Contains("cookie"), "deep search: the passage with the answer is kept, the page's clutter isn't (got: " + kept + ")");
Check(string.Join(" ", WebTools.KeyWords("whast the price that tesla used for the model y")) == "whast price tesla model", "deep search: the question's key words (got: " + string.Join(" ", WebTools.KeyWords("whast the price that tesla used for the model y")) + ")");
var effortFor = typeof(DynamicNotch.NotchWindow).GetMethod("EffortFor", BindingFlags.NonPublic | BindingFlags.Static)!;
var kindOf = (string q) => askKind.Invoke(null, new object[] { q })!;
string Effort(string q) => (string)effortFor.Invoke(null, new object[] { kindOf(q), q, false })!;
Check(Effort("open spotify") == "low" && Effort("how are you") == "low" && Effort("who won the ballon d'or 2026") == "low", "thinking: little for commands, chat and plain facts");
Check(Effort("should i buy a ps5 or a gaming pc for 600 euros") == "medium" && Effort("make me an excel table of my expenses") == "medium", "thinking: more for choices and things to build");
Check(Effort("think carefully: is it worth learning rust in 2026") == "high", "thinking: the most when asked to think carefully");
Check(Effort("which is better for gaming, the rtx 5070 or the rx 9070?") == "medium", "thinking: a fact that compares is weighed, not just quoted");

// ───────── offline voice (only where its model and a test recording are: PIXO_VOICE_TEST=folder of s0..s3.wav) ─────────
if (Environment.GetEnvironmentVariable("PIXO_VOICE_TEST") is { Length: > 0 } voiceDir && LocalSpeech.Ready(LocalSpeech.Fast))
{
    float[] Wave(string f)
    {
        var b = File.ReadAllBytes(f); int at = 12;
        while (at + 8 < b.Length && System.Text.Encoding.ASCII.GetString(b, at, 4) != "data") at += 8 + BitConverter.ToInt32(b, at + 4);
        var x = new float[(b.Length - at - 8) / 2];
        for (int i = 0; i < x.Length; i++) x[i] = BitConverter.ToInt16(b, at + 8 + i * 2) / 32768f;
        return x;
    }
    var voiceClock = System.Diagnostics.Stopwatch.StartNew();
    var en = LocalSpeech.Write(LocalSpeech.Fast, Wave(Path.Combine(voiceDir, "s0.wav")), "auto", false, CancellationToken.None).GetAwaiter().GetResult();
    var fr = LocalSpeech.Write(LocalSpeech.Fast, Wave(Path.Combine(voiceDir, "s2.wav")), "auto", false, CancellationToken.None).GetAwaiter().GetResult();
    Console.WriteLine($"voice: \"{en?.Text}\" [{en?.Language} {en?.Sure:0.00}] · \"{fr?.Text}\" [{fr?.Language} {fr?.Sure:0.00}] in {voiceClock.ElapsedMilliseconds} ms");
    Check(en?.Text.Contains("Spotify", StringComparison.OrdinalIgnoreCase) == true && en?.Language == "en", "offline voice: an English command, with the app's name");
    Check(fr?.Text.Contains("Discord", StringComparison.OrdinalIgnoreCase) == true && fr?.Language == "fr", "offline voice: a French command, with the app's name");
    Check(LocalSpeech.Write(LocalSpeech.Fast, new float[16000], "auto", false, CancellationToken.None).GetAwaiter().GetResult() is null or { Text.Length: < 3 }, "offline voice: silence gives no words");
    var mine = new[] { "fr", "en", "ar", "es" };
    var frLang = LocalSpeech.Detect(LocalSpeech.Fast, Wave(Path.Combine(voiceDir, "s2.wav")), mine, CancellationToken.None).GetAwaiter().GetResult();
    var enLang = LocalSpeech.Detect(LocalSpeech.Fast, Wave(Path.Combine(voiceDir, "s0.wav")), mine, CancellationToken.None).GetAwaiter().GetResult();
    Check(frLang?.Language == "fr" && enLang?.Language == "en", $"offline voice: the language picked among yours (got {frLang?.Language}, {enLang?.Language})");
    var quick = System.Diagnostics.Stopwatch.StartNew();
    var fixedFr = LocalSpeech.Write(LocalSpeech.Fast, Wave(Path.Combine(voiceDir, "s2.wav")), "fr", true, CancellationToken.None).GetAwaiter().GetResult();
    Console.WriteLine($"voice live look (language known): {quick.ElapsedMilliseconds} ms · \"{fixedFr?.Text}\"");
    Check(fixedFr?.Text.Contains("Discord", StringComparison.OrdinalIgnoreCase) == true, "offline voice: a live look in a known language");
}

// ───────── understanding: follow-ups, choices, the answer checked ─────────
MethodInfo M(string name) => typeof(DynamicNotch.NotchWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;
bool FollowUp(string t) => (bool)M("IsFollowUp").Invoke(null, new object[] { t })!;
string Rewrite(string prev, string t) => (string)M("FollowUpQuery").Invoke(null, new object[] { prev, t })!;
Check(FollowUp("and in France?") && FollowUp("what about the pro max") && FollowUp("et pour demain ?") && FollowUp("in euros?"), "follow-ups: short messages that continue the last question are seen");
Check(!FollowUp("open spotify and play lofi") && !FollowUp("what is the capital of australia") && !FollowUp("hey how are you doing today"), "follow-ups: a full new question or a command isn't one");
Check(Rewrite("price of the iphone 16 in Morocco", "and in France?") == "price of the iphone 16 in France", "follow-ups: the new place replaces the old one (got: " + Rewrite("price of the iphone 16 in Morocco", "and in France?") + ")");
Check(Rewrite("who won the champions league 2024", "and 2023?") == "who won the champions league 2024 2023" || Rewrite("who won the champions league 2024", "and 2023?").EndsWith("2023"), "follow-ups: the new words are searched with the question (got: " + Rewrite("who won the champions league 2024", "and 2023?") + ")");
Check(Rewrite("weather in paris today", "what about tomorrow") == "weather in paris today tomorrow" || Rewrite("weather in paris today", "what about tomorrow").Contains("paris"), "follow-ups: the subject stays (got: " + Rewrite("weather in paris today", "what about tomorrow") + ")");
bool AsksBack(string a) => (bool)M("AsksBack").Invoke(null, new object[] { a })!;
Check(AsksBack("Which one did you mean?\n>> alex on Discord | the group") && AsksBack("Alex or the group?") && !AsksBack("Done: Spotify is open.\n>> Play lofi"), "choices: an answer that asks back gets its options as buttons");
var askType = typeof(DynamicNotch.NotchWindow).GetNestedType("Ask", BindingFlags.NonPublic)!;
object AskOf(string k) => Enum.Parse(askType, k);
string? Gap(string kind, string q, string a, bool research) => (string?)M("AnswerGap").Invoke(null, new object[] { AskOf(kind), q, a, research });
Check(Gap("Fact", "how much is the iphone 16 in morocco", "It's available in most stores, prices vary by shop.", true) != null, "answer check: a price asked and none given goes back once");
Check(Gap("Fact", "how much is the iphone 16 in morocco", "**About 9,990 MAD** for the 128 GB (Apple Maroc, Nov 2026).", true) == null, "answer check: a real answer with the number passes");
Check(Gap("Fact", "when is gta 6 coming out", "There's no official release date yet: Rockstar hasn't announced one.", true) == null, "answer check: 'not announced' is an honest answer, not a miss");
Check(Gap("Fact", "who is the ceo of nvidia", "I can't browse the internet, but as of my training data it was Jensen Huang.", true) != null, "answer check: 'I can't browse' with research in hand goes back");
Check(Gap("Build", "write me a cover letter for a barista job", "Dear [Hiring Manager Name],\nI am applying...", false) != null, "answer check: placeholders in something built go back");
Check(Gap("Build", "make me a reading list", "1. [Atomic Habits](https://jamesclear.com) by James Clear", false) == null, "answer check: a markdown link isn't a placeholder");
Check(Gap("Talk", "hey", "Hey!", false) == null && Gap("Action", "open spotify", "Done.", false) == null, "answer check: small talk and actions aren't checked here");
Check(Gap("Chat", "explain recursion", "", false) != null, "answer check: an empty answer goes back");
Check(Gap("Chat", "12*12", "**144**", false) == null && Gap("Chat", "translate thanks to spanish", "Gracias", false) == null && Gap("Fact", "capital of france?", "Paris.", true) == null, "answer check: a right short answer is never sent back");
Check(Gap("Fact", "how many players in a football team", "Eleven players per side.", true) == null, "answer check: a number written as a word counts");
Check(Gap("Fact", "how much is the new switch 2 in morocco", "I can't find an official Moroccan price yet: resellers list it around different prices.", true) == null, "answer check: an honest 'no official price found' isn't sent back");
Check(Gap("Fact", "who is the ceo of nvidia", "I can't find an official statement, but reports name Jensen Huang.", true) == null, "answer check: an honest 'I can't find' isn't taken for 'I can't browse'");
Check(Gap("Build", "write a python script that prints the user's name", "```python\nprint(row[\"name\"])  # TODO later\n```", false) == null, "answer check: code isn't checked for placeholders");
Check(Gap("Build", "make me a todo list for moving", "1. Book the van\n2. Pack the kitchen (my todo list)", false) == null, "answer check: a todo list is not a placeholder");
bool Continues(string t) => (bool)M("ContinuesQuestion").Invoke(null, new object[] { t })!;
Check(Continues("and in France?") && Continues("what about the pro max") && Continues("et pour demain ?"), "follow-ups: these continue the last question");
Check(!Continues("and open spotify") && !Continues("also send it to alex") && !Continues("and what's the population of japan?") && !Continues("then close discord"), "follow-ups: a command or a new question starting with 'and' is not a follow-up");
var choiceArgs = M("ChoiceArgs");
var parsed = ((string, System.Collections.Generic.List<string>))choiceArgs.Invoke(null, new object?[] { JsonNode.Parse("{\"question\":\"Which Alex?\",\"options\":[\"Message Alex on Discord\",\"\",\"Message the group\",\"message alex on discord\",\"a\",\"b\",\"c\"]}") })!;
Check(parsed.Item1 == "Which Alex?" && parsed.Item2.Count == 4 && parsed.Item2[0] == "Message Alex on Discord" && !parsed.Item2.Contains(""), "choices: at most 4 options, empty and repeated ones dropped (got: " + string.Join(" / ", parsed.Item2) + ")");
var tools = (JsonArray)M("ChatTools").Invoke(null, null)!;
Check(tools.Any(t => t?["function"]?["name"]?.ToString() == "ask_user") && tools.Any(t => t?["function"]?["name"]?.ToString() == "add_note"), "choices: the AI has its ask_user and add_note tools");

// ───────── charts: the AI's numbers are read, bad ones refused ─────────
var chartParse = typeof(DynamicNotch.NotchWindow).Assembly.GetType("DynamicNotch.ChartData")!.GetMethod("Parse")!;
object? Chart(string json) { try { return chartParse.Invoke(null, new object[] { json }); } catch { return null; } }
Check(Chart("{\"type\":\"line\",\"labels\":[\"Thu\",\"Fri\"],\"series\":[{\"name\":\"High\",\"values\":[23,21]}]}") != null, "charts: a normal line chart is read");
Check(Chart("{\"type\":\"bar\",\"series\":[{\"values\":[\"3.4M\",\"1,9\",null]}]}") != null, "charts: numbers written as text, and gaps, are read");
Check(Chart("{\"type\":\"line\",\"series\":[]}") == null && Chart("not json") == null, "charts: no numbers or broken data is refused (shown as text instead)");
var chartRule = (string)typeof(DynamicNotch.NotchWindow).GetField("ChartWords", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!.ToString()!;
Check(System.Text.RegularExpressions.Regex.IsMatch("make me a graph of these changes in function of time", chartRule, System.Text.RegularExpressions.RegexOptions.IgnoreCase), "charts: \"make me a graph\" is seen as a chart request");

var chartWords = (System.Text.RegularExpressions.Regex)typeof(DynamicNotch.NotchWindow).GetField("ChartWords", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
Check(chartWords.IsMatch("show me this as a bar chart") && chartWords.IsMatch("plot the price of gold this year") && chartWords.IsMatch("fais moi un graphique des ventes"), "charts: asking for a chart, in any wording");
Check(!chartWords.IsMatch("what's top of the billboard charts this week") && !chartWords.IsMatch("who topped the music charts in 2025"), "charts: \"the charts\" (music) isn't a chart request");
var timeMention = (System.Text.RegularExpressions.Regex)typeof(DynamicNotch.NotchWindow).GetField("TimeMention", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
Check(timeMention.IsMatch("Your meeting is tomorrow at 9") && timeMention.IsMatch("the store opens at 9:30") && timeMention.IsMatch("call him at 6pm"), "reminders: a real time in an answer is offered");
Check(!timeMention.IsMatch("temperatures peak at 23°C on Saturday") && !timeMention.IsMatch("battery at 50%") && !timeMention.IsMatch("it sells at 23 dollars"), "reminders: a number that isn't a time (23°C, 50%) isn't");

// ───────── the Work page: what a window is, projects, the inbox ─────────
string P(string exe, string title) => WorkLog.Parse(exe, title) is { } w ? $"{w.Kind}|{w.Name}" : "-";
Check(P(@"C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE", "Pricing report.docx - Word") == "doc|Pricing report.docx", "work: a Word document");
Check(P(@"C:\x\WINWORD.EXE", "Pricing report - Word") == "doc|Pricing report.docx", "work: a Word document whose extension is hidden");
Check(P(@"C:\x\EXCEL.EXE", "Budget 2026.xlsx  -  Saved - Excel") == "sheet|Budget 2026.xlsx", "work: an Excel sheet (\"Saved\" left out)");
Check(P(@"C:\x\WINWORD.EXE", "Document1 - Word") == "-", "work: a blank new document isn't work yet");
Check(P(@"C:\x\brave.exe", "Market research on pricing - Brave") == "tab|Market research on pricing", "work: a browser tab");
Check(P(@"C:\x\msedge.exe", "Q3 results.pdf and 2 more pages - Personal - Microsoft\u200B Edge") == "pdf|Q3 results.pdf", "work: a PDF open in Edge");
Check(P(@"C:\x\chrome.exe", "Project plan - Google Docs - Google Chrome") == "doc|Project plan", "work: a Google Doc");
Check(P(@"C:\x\brave.exe", "Lofi beats to study - YouTube - Brave") == "-" && P(@"C:\x\brave.exe", "New Tab - Brave") == "-", "work: videos and blank tabs aren't work");
Check(P(@"C:\x\Code.exe", "● app.ts - website - Visual Studio Code") == "code|app.ts", "work: code, with its folder");
Check(P(@"C:\x\AcroRd32.exe", "contract-final.pdf - Adobe Acrobat Reader (64-bit)") == "pdf|contract-final.pdf", "work: a PDF in Acrobat");
Check(P(@"C:\x\Discord.exe", "#general | Server - Discord") == "-", "work: chat apps aren't work");
Check(P(@"C:\x\brave.exe", "(67) WhatsApp - Brave") == "-" && P(@"C:\x\brave.exe", "Inbox (3) - me@gmail.com - Gmail - Brave") == "-", "work: chats with an unread count, and mail, aren't work");
Check(WorkLog.TabTitle(@"C:\x\brave.exe", "(4) Instagram - Brave") == "Instagram" && WorkLog.TabTitle(@"C:\x\Code.exe", "x") == null, "work: the page in front still shows on Now (without its count)");

string workDir = Path.Combine(Path.GetTempPath(), "dn-work-checks-" + Guid.NewGuid().ToString("N")[..6]);
WorkLog.UseFolder(workDir);
var t0 = DateTime.Now.AddHours(-3);
void Use(string exe, string title, int minutes, ref DateTime at) { for (int i = 0; i < minutes * 12; i++) { WorkLog.Seen(exe, "App", title, 5, at); at = at.AddSeconds(5); } }
var clock = t0;
// a morning on the pricing report: the doc, its sheet, a PDF and a page about pricing, used together, twice
for (int round = 0; round < 2; round++)
{
    Use(@"C:\x\WINWORD.EXE", "Pricing report.docx - Word", 6, ref clock);
    Use(@"C:\x\EXCEL.EXE", "Pricing data.xlsx - Excel", 4, ref clock);
    Use(@"C:\x\msedge.exe", "Competitor pricing 2026.pdf - Microsoft\u200B Edge", 3, ref clock);
    Use(@"C:\x\brave.exe", "SaaS pricing models explained - Brave", 2, ref clock);
    clock = clock.AddMinutes(25); // a break: a new stretch of work
}
// later, something else: a biology essay and its sources
clock = clock.AddHours(1);
Use(@"C:\x\WINWORD.EXE", "Biology essay.docx - Word", 8, ref clock);
Use(@"C:\x\brave.exe", "Cell biology basics - Brave", 3, ref clock);
Use(@"C:\x\brave.exe", "Mitochondria function in cells - Brave", 3, ref clock);
var projects = WorkLog.Projects();
var pricing = projects.FirstOrDefault(p => p.Items.Any(i => i.Name == "Pricing report.docx"));
var bio = projects.FirstOrDefault(p => p.Items.Any(i => i.Name == "Biology essay.docx"));
Check(pricing != null && pricing.Items.Count == 4 && pricing.Name == "Pricing", $"work: things used together become one project, named by what they share (got: {pricing?.Name} with {pricing?.Items.Count})");
Check(bio != null && bio != pricing && !bio.Items.Any(i => i.Name.Contains("ricing")), $"work: another subject becomes another project (got: {bio?.Name})");
// your edits win: rename and take a tab out
var keptMeta = WorkLog.Keep(pricing!);
keptMeta.Name = "Client X pricing";
WorkLog.Remove(pricing!.Items.First(i => i.Kind == "tab").Key, keptMeta);
var again = WorkLog.Projects().First(p => p.Id == keptMeta.Id);
Check(again.Name == "Client X pricing" && again.Items.Count == 3 && again.Kept != null, "work: a renamed project keeps its name, and a tab taken out stays out");
WorkLog.Save();
WorkLog.UseFolder(workDir);
Check(WorkLog.Projects().Any(p => p.Name == "Client X pricing" && p.Items.Count == 3), "work: projects and edits are kept after a restart");

// hiding: a found project you don't want stays gone (even after a restart), and comes back only when asked
var bioP = WorkLog.Projects().First(p => p.Items.Any(i => i.Name == "Biology essay.docx"));
WorkLog.Hide(bioP);
Check(!WorkLog.Projects().Any(p => p.Items.Any(i => i.Name == "Biology essay.docx")) && WorkLog.HiddenCount() == 1, "work: a hidden project is gone, and its things don't come back as a new project");
WorkLog.Save(); WorkLog.UseFolder(workDir);
Check(!WorkLog.Projects().Any(p => p.Items.Any(i => i.Name == "Biology essay.docx")), "work: a hidden project stays hidden after a restart");
WorkLog.UnhideAll();
Check(WorkLog.Projects().Any(p => p.Items.Any(i => i.Name == "Biology essay.docx")), "work: hidden projects come back when asked");
var forgetKey = WorkLog.Recent(20).First(i => i.Name == "Cell biology basics").Key;
WorkLog.ForgetItem(forgetKey);
Check(WorkLog.Recent(20).All(i => i.Key != forgetKey) && WorkLog.Projects().All(p => p.Items.All(i => i.Key != forgetKey)), "work: a forgotten thing leaves Recent and every project");

int projectsBefore = WorkLog.Projects().Count;
WorkLog.ClearRecent();
Check(WorkLog.Recent(20).Count == 0 && WorkLog.Projects().Count == projectsBefore, "work: clearing Recent empties it without touching projects");
WorkLog.Seen(@"C:\x\WINWORD.EXE", "Word", "Pricing report.docx - Word", 5, DateTime.Now.AddSeconds(1));
Check(WorkLog.Recent(20).Count == 1 && WorkLog.Recent(20)[0].Name == "Pricing report.docx", "work: after clearing, what you use again comes back to Recent");

NotifHistory.UseFolder(workDir);
NotifHistory.Add(DateTime.Now.AddMinutes(-31), "Snipping Tool", "Screenshot copied", "Select here to mark up and share");
NotifHistory.Mute("Snipping Tool", true);
NotifHistory.Add(DateTime.Now.AddMinutes(-30.5), "Snipping Tool", "Screenshot copied", "again");
Check(NotifHistory.Recent().All(n => n.App != "Snipping Tool") && NotifHistory.Muted().Contains("Snipping Tool"), "inbox: a muted app's notifications are removed and no longer kept");
NotifHistory.Mute("Snipping Tool", false);
NotifHistory.Add(DateTime.Now.AddMinutes(-30), "Discord", "Sam", "can you send me the file before 6?");
NotifHistory.Add(DateTime.Now.AddMinutes(-20), "Discord", "Sam", "thanks!");
NotifHistory.Add(DateTime.Now.AddMinutes(-10), "WhatsApp", "Mom", "Are you coming for dinner?");
NotifHistory.Add(DateTime.Now.AddMinutes(-5), "Gmail", "Security alert", "Your verification code is 123456. Can you confirm it was you?");
NotifHistory.Add(DateTime.Now.AddMinutes(-4), "Windows", "Update available", "Restart to finish?");
var waitingNow = NotifHistory.Waiting();
Check(waitingNow.Count == 2 && waitingNow.Any(w => w.Title == "Mom") && waitingNow.Any(w => w.Title == "Sam"), $"inbox: people asking you something are waiting; codes and system alerts aren't (got: {string.Join(", ", waitingNow.Select(w => w.Title))})");
NotifHistory.Done(waitingNow.First(w => w.Title == "Mom"));
Check(NotifHistory.Waiting().All(w => w.Title != "Mom") && NotifHistory.Recent().Count == 5, "inbox: done takes it off the list, history keeps everything");
NotifHistory.UseFolder(workDir);
Check(NotifHistory.Recent().Count == 5 && NotifHistory.Waiting().All(w => w.Title != "Mom"), "inbox: history and done are kept after a restart");
NotifHistory.Add(DateTime.Now.AddMinutes(-3), NotifHistory.PixoApp, "Download complete", "report.pdf");
var appsSeen = NotifHistory.Apps();
Check(NotifHistory.IsAlert(NotifHistory.Recent()[0]) && !NotifHistory.IsAlert(waitingNow[0]) && appsSeen.All(a => a.App != NotifHistory.PixoApp) && appsSeen.First(a => a.App == "Discord").Count == 2,
    $"notifs: the notch's own alerts are kept apart from apps; apps counted ({string.Join(", ", appsSeen.Select(a => a.App + " " + a.Count))})");
NotifHistory.Mute("windows", true);
Check(NotifHistory.IsMuted("Windows") && NotifHistory.IsMuted(" windows ") && NotifHistory.Recent().All(n => n.App != "Windows") && NotifHistory.Waiting().Any(w => w.Title == "Sam"), "notifs: an app turned off (any case) is neither kept nor shown");
NotifHistory.Mute("windows", false);
NotifHistory.Flush();
try { Directory.Delete(workDir, true); } catch { }

// ───────── reading documents ─────────
string dir = Path.Combine(Path.GetTempPath(), "dn-checks");
Directory.CreateDirectory(dir);
try
{
    string xlsx = Path.Combine(dir, "t.xlsx");
    DocWriter.Write(xlsx, "| City | Population |\n|---|---|\n| Casablanca | 3360000 |\n| Rabat | 577000 |");
    string x = DocReader.Read(xlsx) ?? "";
    Check(x.Contains("Casablanca") && x.Contains("3360000") && x.Contains("Population"), "documents: an Excel sheet is read (got: " + x[..Math.Min(80, x.Length)] + ")");

    string pdf = Path.Combine(dir, "t.pdf");
    DocWriter.Write(pdf, "# Invoice 2041\nTotal due: 1,250 MAD\nPaid by transfer.");
    string p = DocReader.Read(pdf) ?? "";
    Check(p.Contains("Invoice 2041") && p.Contains("1,250"), "documents: a PDF's text is read (got: " + p[..Math.Min(80, p.Length)] + ")");

    string png = Path.Combine(dir, "t.png");
    using (var bmp = new System.Drawing.Bitmap(900, 160))
    using (var g = System.Drawing.Graphics.FromImage(bmp))
    {
        g.Clear(System.Drawing.Color.White);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        g.DrawString("Meeting at 15:30 with Karim", new System.Drawing.Font("Segoe UI", 34), System.Drawing.Brushes.Black, 20, 40);
        bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
    }
    string o = DocReader.Read(png) ?? "";
    Check(o.Contains("Karim") && o.Contains("15:30"), "documents: the text in a picture is read (got: " + o.Replace('\n', ' ')[..Math.Min(90, o.Length)] + ")");
}
finally { try { Directory.Delete(dir, true); } catch { } }

// ───────── the files the AI makes ─────────
{
    string docDir = Path.Combine(Path.GetTempPath(), "pixo-docchecks");
    Directory.CreateDirectory(docDir);
    string md = "# Budget\n\n## Costs\n| Item | Jan | Share |\n|---|---|---|\n| Rent | $1,200.00 | 40% |\n| Food | $400 | 13% |\n| Total | =SUM(B2:B3) | |\n\nchart: column\n\n## Deck part\n- one **bold** point\n- [a link](https://example.com)\n\n> a quote\n\n```\ncode\n```\n";
    foreach (var ext in new[] { ".docx", ".xlsx", ".pptx", ".pdf" })
    {
        string f = Path.Combine(docDir, "check" + ext);
        try { File.Delete(f); DocWriter.Write(f, md); } catch (Exception ex) { Check(false, $"{ext} not written: {ex.Message}"); continue; }
        var bytes = File.ReadAllBytes(f);
        if (ext == ".pdf") { Check(System.Text.Encoding.ASCII.GetString(bytes, 0, 5) == "%PDF-" && System.Text.Encoding.ASCII.GetString(bytes).Contains("%%EOF"), "the PDF is well formed"); continue; }
        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(bytes));
        var names = zip.Entries.Select(e => e.FullName).ToHashSet();
        string main = ext switch { ".docx" => "word/document.xml", ".xlsx" => "xl/workbook.xml", _ => "ppt/presentation.xml" };
        Check(names.Contains("[Content_Types].xml") && names.Contains(main), $"{ext} has its parts");
        foreach (var e in zip.Entries.Where(e => e.FullName.EndsWith(".xml") || e.FullName.EndsWith(".rels")))
            try { using var s = e.Open(); System.Xml.Linq.XDocument.Load(s); } catch (Exception ex) { Check(false, $"{ext} {e.FullName} isn't valid XML: {ex.Message}"); }
        if (ext == ".xlsx")
        {
            using var s = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
            string sheet = new StreamReader(s).ReadToEnd();
            Check(sheet.Contains("<f>SUM(B4:B5)</f>"), "an Excel formula written for row 1 moves to the table's real rows");
            Check(names.Contains("xl/charts/chart1.xml"), "an Excel chart is added");
        }
    }
}

// ───────── 👎 kept on this PC ─────────
string feedback = Path.Combine(Settings.Folder, "ai-feedback.jsonl");
if (File.Exists(feedback))
{
    var lines = File.ReadAllLines(feedback).Where(l => l.Trim().Length > 0).ToList();
    Console.WriteLine($"\n{lines.Count} answer(s) marked bad, to turn into checks:");
    foreach (var line in lines.TakeLast(10))
        try { var n = JsonNode.Parse(line)!; Console.WriteLine($"  {n["at"]}  [{n["kind"]}]  {n["question"]}"); } catch { }
}

// ───────── notices from scripts: what a notice may hold ─────────
{
    var n = ScriptNotices.Clean(new("  Build\r\ndone\t\u0007ok  ", "  CI  ", "OK!", 99))!;
    Check(n.Text == "Build done ok" && n.Title == "CI", $"a script notice keeps one clean line (got “{n.Text}” / “{n.Title}”)");
    Check(n.Icon == "info" && n.Seconds == 12, $"a script notice falls back to a known icon and at most 12 s (got {n.Icon}, {n.Seconds})");
    Check(ScriptNotices.Clean(new(new string('x', 500), null, "fail", 0))! is { Text.Length: 120, Icon: "fail", Seconds: 4 }, "a long script notice is cut to 120 characters, 4 s by default");
    Check(ScriptNotices.Clean(new("   ", null, "ok", 3)) == null, "an empty script notice shows nothing");
}

// ───────── the Converter page's units ─────────
{
    string Cv(string cat, string a, string b, double v) => Converter.Format(Converter.Convert(cat, a, b, v));
    Check(Cv("Length", "cm", "in", 100) == "39.3701", $"100 cm is 39.3701 in (got {Cv("Length", "cm", "in", 100)})");
    Check(Cv("Temperature", "C", "F", 0) == "32" && Cv("Temperature", "F", "C", 212) == "100" && Cv("Temperature", "K", "C", 0) == "-273.15", "temperatures convert (0 °C = 32 °F, 212 °F = 100 °C, 0 K = -273.15 °C)");
    Check(Cv("Data", "GiB", "MiB", 1) == "1024" && Cv("Data", "GB", "GiB", 1) == "0.931323", $"1 GiB = 1024 MiB, 1 GB = 0.931323 GiB (got {Cv("Data", "GB", "GiB", 1)})");
    Check(Cv("Weight", "kg", "lb", 1) == "2.20462" && Cv("Time", "h", "min", 1.5) == "90", "1 kg = 2.20462 lb, 1.5 h = 90 min");
    Check(Converter.TryParse("1,5", out var p1) && p1 == 1.5 && Converter.TryParse("1,234.5", out var p2) && p2 == 1234.5 && Converter.TryParse(" 2 000 ", out var p3) && p3 == 2000, "numbers read with a comma or a dot, and with spaces between thousands");
    Check(!Converter.TryParse("abc", out _) && !Converter.TryParse("", out _), "words are not numbers");
    Check(Converter.Format(1234567.891) == "1234568" && Converter.Format(0.000123456789) == "0.000123457", $"six significant digits, no exponent in the usual range (got {Converter.Format(1234567.891)}, {Converter.Format(0.000123456789)})");
    var rates = new Fx.Rates("2026-11-27", new() { ["EUR"] = 1, ["USD"] = 1.05, ["MAD"] = 10.5 }, DateTime.UtcNow);
    Check(Fx.Convert(rates, "USD", "MAD", 1.05) is double m && Math.Abs(m - 10.5) < 1e-9 && Fx.Convert(rates, "USD", "XXX", 1) == null, "currencies convert through the euro; an unknown code gives no answer");
}

// ───────── the weather: codes, °F, Open-Meteo's answer ─────────
{
    Check(WeatherService.Describe(0) == ("Clear", WeatherService.Kind.Clear) && WeatherService.Describe(2).Kind == WeatherService.Kind.PartlyCloudy
        && WeatherService.Describe(63).Kind == WeatherService.Kind.Rain && WeatherService.Describe(75).Kind == WeatherService.Kind.Snow
        && WeatherService.Describe(96).Kind == WeatherService.Kind.Storm && WeatherService.Describe(48).Kind == WeatherService.Kind.Fog,
        "weather codes read as the right sky");
    Check(WeatherService.ToF(0) == 32 && WeatherService.ToF(100) == 212 && Math.Abs(WeatherService.ToF(-40) + 40) < 1e-9, "°C to °F");
    var hourly = string.Join(",", Enumerable.Range(0, 48).Select(i => $"\"2026-11-28T{i % 24:00}:00\"".Replace("2026-11-28", i < 24 ? "2026-11-28" : "2026-11-29")));
    var temps = string.Join(",", Enumerable.Range(0, 48).Select(i => (10 + i % 5).ToString()));
    var codes = string.Join(",", Enumerable.Range(0, 48).Select(_ => "3"));
    var wx = WeatherService.Parse($$$"""
    {"current":{"time":"2026-11-28T14:30","temperature_2m":18.4,"apparent_temperature":16.9,"relative_humidity_2m":70,"wind_speed_10m":12.2,"weather_code":2,"is_day":1},
     "hourly":{"time":[{{{hourly}}}],"temperature_2m":[{{{temps}}}],"weather_code":[{{{codes}}}],"precipitation_probability":[{{{codes}}}],"is_day":[{{{codes}}}]},
     "daily":{"time":["2026-11-28","2026-11-29"],"weather_code":[2,61],"temperature_2m_max":[21.0,17.5],"temperature_2m_min":[12.0,9.5],"precipitation_probability_max":[5,80]}}
    """);
    Check(wx.Hours.Count == 24 && wx.Hours[0].At.Hour == 14 && wx.Hours[^1].At == new DateTime(2026, 11, 29, 13, 0, 0), $"the 24 hours start at the current hour (got {wx.Hours.Count}, from {wx.Hours.FirstOrDefault()?.At:HH:mm})");
    Check(wx.Days.Count == 2 && wx.Days[1].Rain == 80 && wx.Current.Humidity == 70 && wx.Current.IsDay, "the week and now are read from the forecast");
}

// ───────── the AI usage page: limits, pace, price ─────────
{
    var read = new DateTime(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);
    // half of a 5-hour window gone, 20 % used: on pace for 40 % at reset
    var half = new AiLimits.Window("Session", 20, read.AddHours(2.5), 5);
    Check(AiLimits.Pace(half, read, read).AtReset is double p40 && Math.Abs(p40 - 40) < 0.01, "the pace: 20 % used halfway through is 40 % at reset");
    // 80 % used with half the window gone: the limit comes when 100 % is reached, at 5/8 of the window
    var fast = new AiLimits.Window("Session", 80, read.AddHours(2.5), 5);
    Check(AiLimits.Pace(fast, read, read).LimitAt == read.AddHours(-2.5).AddHours(5 * 100.0 / 80 / 2), "the pace: past 100 % it says when the limit is reached");
    Check(AiLimits.Pace(half, read, read.AddMinutes(16)) == (null, null) && AiLimits.Pace(half with { Used = 0 }, read, read) == (null, null)
        && AiLimits.Pace(new AiLimits.Window("Session", 5, read.AddHours(4.8), 5), read, read) == (null, null), "no pace for an old reading, 0 %, or under a tenth of the window");
    // one request: 1M in, 1M out, 1M cache read, 1M cache write (5 min), 1M (1 h) on a Sonnet: 3 + 15 + 0.3 + 3.75 + 6
    Check(Math.Abs(AiLimits.Usd("claude-sonnet-4-5", 1_000_000, 1_000_000, 1_000_000, 1_000_000, 1_000_000) - 28.05) < 1e-9, "Claude Code's price: input, output, cache read 0.1x, writes 1.25x and 2x");
    Check(AiLimits.Usd("claude-opus-4-1-20250805", 1_000_000, 0, 0, 0, 0) == 15 && AiLimits.Usd("claude-opus-4-6", 1_000_000, 0, 0, 0, 0) == 5 && AiLimits.Usd("gpt-5", 1_000_000, 0, 0, 0, 0) == 0,
        "the first Opus models cost 15 $, the later ones 5 $; a model not in the table counts no dollars");
    var claude = AiLimits.ParseClaude(System.Text.Json.Nodes.JsonNode.Parse("""{"five_hour":{"utilization":8.0,"resets_at":"2026-11-27T16:00:00+00:00"},"seven_day":{"utilization":31,"resets_at":"2026-12-01T09:00:00Z"},"seven_day_opus":null}"""), "max", read);
    Check(claude.Plan == "Max" && claude.Windows.Count == 2 && claude.Windows[0] is { Label: "Session", Used: 8, Hours: 5 } && claude.Windows[0].ResetsAt == new DateTime(2026, 11, 27, 16, 0, 0, DateTimeKind.Utc) && claude.Windows[1].Used == 31,
        "Claude's answer reads as Session 8 % and Week 31 %, Max");
    var codex = AiLimits.ParseCodex(System.Text.Json.Nodes.JsonNode.Parse("""{"plan_type":"free","rate_limit":{"primary_window":{"used_percent":12,"limit_window_seconds":2592000,"reset_after_seconds":3600,"reset_at":0},"secondary_window":null}}"""), read);
    Check(codex is { Plan: "Free" } && codex.Windows.Count == 1 && codex.Windows[0] is { Label: "Month", Used: 12 } && codex.Windows[0].ResetsAt == read.AddHours(1),
        "ChatGPT's answer reads as Month 12 %, Free, resetting in an hour");
    Check(AiLimits.ParseCodex(System.Text.Json.Nodes.JsonNode.Parse("""{"rate_limit":{}}"""), read) == null, "a ChatGPT answer with no limits gives nothing");
}

// ───────── Claude desktop's sign-ins: which one the usage page uses ─────────
{
    var now = new DateTime(2026, 11, 28, 0, 0, 0, DateTimeKind.Utc);
    long Ms(DateTime t) => new DateTimeOffset(t).ToUnixTimeMilliseconds();
    var cache = System.Text.Json.Nodes.JsonNode.Parse($$$"""
    {"acct:a:https://api.anthropic.com:user:inference":{"token":"no-profile","expiresAt":{{{Ms(now.AddDays(300))}}},"subscriptionType":"max"},
     "acct:a:https://api.anthropic.com:user:profile":{"token":"short","expiresAt":{{{Ms(now.AddDays(2))}}},"subscriptionType":"max"},
     "acct:a:https://api.anthropic.com:user:inference user:profile":{"token":"long","expiresAt":{{{Ms(now.AddDays(30))}}},"subscriptionType":"max"},
     "acct:a:https://api.anthropic.com:user:profile user:x":{"token":"expired","expiresAt":{{{Ms(now.AddDays(-1))}}}}}
    """);
    Check(ClaudeDesktopSignIn.Pick(cache, now) is { Token: "long", Plan: "max" }, "Claude desktop: the longest-lasting sign-in that can read the profile, never an expired one");
    Check(ClaudeDesktopSignIn.Pick(System.Text.Json.Nodes.JsonNode.Parse("{}"), now) == null, "Claude desktop: no sign-in, no token");
}

// ───────── the Converter reads numbers the way people write them ─────────
{
    double V(string s) => Converter.TryParse(s, out var v) ? v : double.NaN;
    Check(V("1.234,5") == 1234.5 && V("1.234.567") == 1234567 && V("1,234,567") == 1234567, $"European and English grouping (got {V("1.234,5")}, {V("1.234.567")}, {V("1,234,567")})");
    Check(V("1 234,5") == 1234.5 && V("1 234") == 1234, "French grouping with narrow and no-break spaces");
    Check(V("١٢٫٥") == 12.5 && V("۳") == 3, "Arabic-Indic digits and the Arabic decimal sign");
}

Console.WriteLine($"\n{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
