using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;

namespace DynamicNotch.Services;

/// <summary>
/// Real, well-made documents from what the AI writes (markdown): Word (.docx), Excel (.xlsx), PDF and PowerPoint
/// (.pptx), each in one calm house style (navy and blue accents, clear type, generous spacing) so a file from Pixo
/// looks finished, not generated. Built by hand (the formats are zipped XML / PDF objects): no Office needed, no extra
/// libraries. Anything else is written as text (UTF-8).
///  • Word: a title, real heading styles (the navigation pane works), real bullet and numbered lists, styled tables
///    with a repeating header row, clickable links, quotes, code, page breaks, page numbers.
///  • Excel: one sheet per "## " section, a title row, a styled header with filters and frozen panes, numbers, money,
///    percents and dates as real values with proper formats, working formulas, a bold total row, fitted columns,
///    optional chart ("chart: column|bar|line|pie|area").
///  • PDF: A4, text measured exactly (no ragged guesses), real tables with a header that repeats on new pages,
///    bullets, quotes, code boxes, clickable links, page numbers.
///  • PowerPoint: 16:9 slides: a title slide, section slides, bullet slides (two columns when long), table slides,
///    slide numbers.
/// (DocXlsx.cs, DocDocx.cs, DocPdf.cs, DocPptx.cs)
/// </summary>
public static partial class DocWriter
{
    public static void Write(string path, string content)
    {
        content ??= "";
        string title = Path.GetFileNameWithoutExtension(path);
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".docx": File.WriteAllBytes(path, Docx(content, title)); break;
            case ".xlsx": File.WriteAllBytes(path, Xlsx(content, title)); break;
            case ".pdf": File.WriteAllBytes(path, Pdf(content, title)); break;
            case ".pptx": File.WriteAllBytes(path, Pptx(content, title)); break;
            default: File.WriteAllText(path, content, new UTF8Encoding(false)); break;
        }
    }

    public static bool IsDocument(string path) => Path.GetExtension(path).ToLowerInvariant() is ".docx" or ".xlsx" or ".pdf" or ".pptx";

    // ───────────────────────────── the house style ─────────────────────────────

    const string Ink = "1F2328", Muted = "6B7280", Accent = "1F3864", Accent2 = "2F5597", Accent3 = "5B9BD5", Band = "F2F5FA", Rule = "D6DCE5";
    static readonly string[] SeriesColors = { "2F5597", "ED7D31", "70AD47", "FFC000", "5B9BD5", "A5A5A5", "7030A0", "C00000" };

    // ───────────────────────────── markdown, read once ─────────────────────────────

    enum Kind { Heading, Paragraph, Bullet, Number, Code, Table, Rule, Quote, PageBreak }
    sealed record Block(Kind Kind, string Text, int Level = 0, List<string[]>? Rows = null, char[]? Align = null, int Num = 0);

    static readonly Regex PageBreakLine = new(@"^\s*(\\pagebreak|\\newpage|\[\s*page\s*break\s*\]|<\s*pagebreak\s*/?\s*>|<!--\s*page\s*break\s*-->|---\s*page\s*break\s*---)\s*$", RegexOptions.IgnoreCase);

    static List<Block> Parse(string md)
    {
        var blocks = new List<Block>();
        var lines = md.Replace("\r", "").Split('\n');
        var para = new StringBuilder();
        void Flush() { if (para.Length > 0) { blocks.Add(new Block(Kind.Paragraph, para.ToString().Trim())); para.Clear(); } }
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd();
            if (line.TrimStart().StartsWith("```"))
            {
                Flush();
                var code = new StringBuilder();
                for (i++; i < lines.Length && !lines[i].TrimStart().StartsWith("```"); i++) code.Append(lines[i].TrimEnd()).Append('\n');
                blocks.Add(new Block(Kind.Code, code.ToString().TrimEnd('\n')));
                continue;
            }
            if (line.Trim().Length == 0) { Flush(); continue; }
            if (PageBreakLine.IsMatch(line)) { Flush(); blocks.Add(new Block(Kind.PageBreak, "")); continue; }
            if (IsTableRow(line))
            {
                Flush();
                var rows = new List<string[]>();
                char[]? align = null;
                for (; i < lines.Length && IsTableRow(lines[i]); i++)
                {
                    if (IsSeparator(lines[i])) { align ??= Cells(lines[i]).Select(c => c.StartsWith(':') && c.EndsWith(':') ? 'c' : c.EndsWith(':') ? 'r' : 'l').ToArray(); continue; }
                    rows.Add(Cells(lines[i]));
                }
                i--;
                if (rows.Count > 0) blocks.Add(new Block(Kind.Table, "", Rows: rows, Align: align));
                continue;
            }
            var h = Regex.Match(line, @"^\s*(#{1,6})\s+(.*?)\s*#*\s*$");
            if (h.Success) { Flush(); blocks.Add(new Block(Kind.Heading, h.Groups[2].Value.Trim(), h.Groups[1].Value.Length)); continue; }
            if (Regex.IsMatch(line, @"^\s*(?:---+|\*\*\*+|___+)\s*$")) { Flush(); blocks.Add(new Block(Kind.Rule, "")); continue; }
            var q = Regex.Match(line, @"^\s*>\s?(.*)$");
            if (q.Success)
            {
                Flush();
                var quote = new StringBuilder(q.Groups[1].Value.Trim());
                while (i + 1 < lines.Length && Regex.Match(lines[i + 1], @"^\s*>\s?(.*)$") is { Success: true } more) { quote.Append(' ').Append(more.Groups[1].Value.Trim()); i++; }
                blocks.Add(new Block(Kind.Quote, quote.ToString().Trim()));
                continue;
            }
            var li = Regex.Match(line, @"^(\s*)([-*•+])\s+(.*)$");
            if (li.Success)
            {
                Flush();
                string text = li.Groups[3].Value;
                text = Regex.Replace(text, @"^\[\s\]\s+", "☐ ");
                text = Regex.Replace(text, @"^\[[xX]\]\s+", "☑ ");
                blocks.Add(new Block(Kind.Bullet, text, Math.Min(3, Indent(li.Groups[1].Value))));
                continue;
            }
            var ni = Regex.Match(line, @"^(\s*)(\d+)[.)]\s+(.*)$");
            if (ni.Success) { Flush(); blocks.Add(new Block(Kind.Number, ni.Groups[3].Value, Math.Min(3, Indent(ni.Groups[1].Value)), Num: int.TryParse(ni.Groups[2].Value, out int n) ? n : 1)); continue; }
            // a line ending in two spaces or a backslash keeps its line break
            if (para.Length > 0) para.Append(lines[i - 1].EndsWith("  ") || lines[i - 1].EndsWith("\\") ? "\n" : " ");
            para.Append(line.Trim().TrimEnd('\\'));
        }
        Flush();
        return blocks;
    }

    static int Indent(string ws) => ws.Replace("\t", "    ").Length / 2;
    static bool IsTableRow(string line) => Regex.IsMatch(line, @"^\s*\|.*\|\s*$");
    static bool IsSeparator(string line) => Regex.IsMatch(line, @"^\s*\|?(\s*:?-{2,}:?\s*\|)+\s*:?-{0,}:?\s*\|?\s*$");
    static string[] Cells(string line)
    {
        // split on | but not on \| (an escaped bar inside a cell)
        var parts = Regex.Split(line.Trim().Trim('|'), @"(?<!\\)\|");
        return parts.Select(c => c.Replace("\\|", "|").Trim()).ToArray();
    }

    /// <summary>A piece of a line with one look.</summary>
    sealed record Run(string Text, bool Bold = false, bool Italic = false, bool Code = false, string? Link = null);

    static readonly Regex Inline = new(
        @"\*\*\*(.+?)\*\*\*|\*\*(.+?)\*\*|__(.+?)__|`([^`]+)`|(?<![\w*])\*([^*\s][^*]*?)\*(?!\w)|(?<![\w_])_([^_\s][^_]*?)_(?![\w_])|\[([^\]]+)\]\(([^)\s]+)\)|(https?://[^\s<>\]\)]+[^\s<>\]\).,;:!?'""])",
        RegexOptions.Compiled);

    /// <summary>A line of text as runs: bold, italic, code, links (markdown links and bare web addresses).</summary>
    static List<Run> Runs(string s)
    {
        var list = new List<Run>();
        int at = 0;
        foreach (Match m in Inline.Matches(s))
        {
            if (m.Index > at) list.Add(new Run(s[at..m.Index]));
            if (m.Groups[1].Success) list.Add(new Run(m.Groups[1].Value, Bold: true, Italic: true));
            else if (m.Groups[2].Success || m.Groups[3].Success) list.Add(new Run(m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value, Bold: true));
            else if (m.Groups[4].Success) list.Add(new Run(m.Groups[4].Value, Code: true));
            else if (m.Groups[5].Success || m.Groups[6].Success) list.Add(new Run(m.Groups[5].Success ? m.Groups[5].Value : m.Groups[6].Value, Italic: true));
            else if (m.Groups[7].Success) list.Add(new Run(m.Groups[7].Value, Link: SafeLink(m.Groups[8].Value)));
            else list.Add(new Run(m.Groups[9].Value, Link: SafeLink(m.Groups[9].Value)));
            at = m.Index + m.Length;
        }
        if (at < s.Length) list.Add(new Run(s[at..]));
        return list;
    }

    /// <summary>Only web and mail links become clickable (never a file or a script).</summary>
    static string? SafeLink(string url) => Regex.IsMatch(url, @"^(https?://|mailto:)", RegexOptions.IgnoreCase) ? url : null;

    static string Plain(string s) => string.Concat(Runs(s).Select(r => r.Text));

    static string X(string s) => SecurityElement.Escape(Regex.Replace(s ?? "", @"[\x00-\x08\x0B\x0C\x0E-\x1F￾￿]", "")) ?? "";

    static bool IsTotalRow(string[] row) => row.Length > 0 && Regex.IsMatch(Plain(row[0]).Trim(), @"^(grand\s+)?totals?:?$|^sum$|^somme$|^total\s+g[ée]n[ée]ral$|^subtotal$|^sous-total$|^average$|^moyenne$", RegexOptions.IgnoreCase);

    static bool LooksNumeric(string cell) => Regex.IsMatch(Plain(cell).Trim(), @"^[-−+(]?\s*[$€£¥₹]?\s*\d[\d\s,.']*\s*(%|[$€£¥₹]|k|m|bn|mad|dh|usd|eur)?\)?$", RegexOptions.IgnoreCase);

    static string Col(int c) { string s = ""; c++; while (c > 0) { int m = (c - 1) % 26; s = (char)('A' + m) + s; c = (c - 1) / 26; } return s; }

    static string[] SplitCsv(string line, char sep)
    {
        var cells = new List<string>();
        var cur = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (ch == '"') { if (quoted && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; } else quoted = !quoted; }
            else if (ch == sep && !quoted) { cells.Add(cur.ToString()); cur.Clear(); }
            else cur.Append(ch);
        }
        cells.Add(cur.ToString());
        return cells.ToArray();
    }

    static byte[] Zip(Dictionary<string, string> parts)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            // [Content_Types].xml first: some readers expect it there
            foreach (var (name, xml) in parts.OrderBy(p => p.Key == "[Content_Types].xml" ? 0 : 1))
            {
                var e = zip.CreateEntry(name, CompressionLevel.Optimal);
                using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
                w.Write(xml);
            }
        }
        return ms.ToArray();
    }

    const string XmlHead = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>";

    static string CoreProps(string title) => XmlHead
        + "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:dcterms=\"http://purl.org/dc/terms/\" xmlns:dcmitype=\"http://purl.org/dc/dcmitype/\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">"
        + $"<dc:title>{X(title)}</dc:title>"
        + $"<dcterms:created xsi:type=\"dcterms:W3CDTF\">{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}</dcterms:created><dcterms:modified xsi:type=\"dcterms:W3CDTF\">{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}</dcterms:modified>"
        + "</cp:coreProperties>";

    const string CoreType = "<Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/>";
    const string CoreRel = "<Relationship Id=\"rIdCore\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/>";

    /// <summary>The document's title: its first heading, else the file's name made readable.</summary>
    static string TitleOf(List<Block> blocks, string fallback)
    {
        var h = blocks.FirstOrDefault(b => b.Kind == Kind.Heading);
        if (h != null) return Plain(h.Text);
        return Regex.Replace(fallback, @"[_\-]+", " ").Trim();
    }

    static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
