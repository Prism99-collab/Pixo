using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace DynamicNotch.Services;

/// <summary>
/// PDF documents, A4: text measured with the real letter widths (Arial's, which Helvetica shares), so lines wrap
/// exactly and right-aligned numbers line up; a title with a rule under it; headings that never sit alone at the foot
/// of a page; bullets and numbers with hanging indents; quotes with a bar; code in a grey box; real tables (column widths
/// from their content, wrapped cells, a navy header repeated on each new page, light bands, a bold total row); clickable
/// links; the title small at the top of later pages; "n / N" at the foot of each. Latin languages (the PDF's built-in
/// fonts); other letters show as "?", so those documents are better as .docx.
/// </summary>
public static partial class DocWriter
{
    // ───────────── letters and their widths ─────────────

    static readonly Dictionary<char, byte> WinAnsiHigh = new()
    {
        ['€'] = 0x80, ['‚'] = 0x82, ['ƒ'] = 0x83, ['„'] = 0x84, ['…'] = 0x85, ['†'] = 0x86, ['‡'] = 0x87, ['ˆ'] = 0x88, ['‰'] = 0x89,
        ['Š'] = 0x8A, ['‹'] = 0x8B, ['Œ'] = 0x8C, ['Ž'] = 0x8E, ['‘'] = 0x91, ['’'] = 0x92, ['“'] = 0x93, ['”'] = 0x94, ['•'] = 0x95,
        ['–'] = 0x96, ['—'] = 0x97, ['˜'] = 0x98, ['™'] = 0x99, ['š'] = 0x9A, ['›'] = 0x9B, ['œ'] = 0x9C, ['ž'] = 0x9E, ['Ÿ'] = 0x9F,
    };

    static readonly Dictionary<char, string> Spelled = new()
    {
        ['☐'] = "[ ]", ['☑'] = "[x]", ['✓'] = "v", ['✔'] = "v", ['✗'] = "x", ['✘'] = "x", ['→'] = "->", ['←'] = "<-", ['⇒'] = "=>",
        ['≥'] = ">=", ['≤'] = "<=", ['≠'] = "!=", ['≈'] = "~", ['−'] = "-", [' '] = " ", [' '] = " ", [' '] = " ", ['\t'] = "    ",
        ['★'] = "*", ['☆'] = "*", ['◦'] = "-", ['▪'] = "-", ['●'] = "•",
    };

    /// <summary>Text as the PDF fonts' letters (WinAnsi): accents kept, symbols spelled out, others as '?'.</summary>
    static byte[] AnsiBytes(string s)
    {
        var bytes = new List<byte>(s.Length);
        foreach (char c in s)
        {
            if (c >= 0x20 && c < 0x7F) bytes.Add((byte)c);
            else if (c >= 0xA0 && c <= 0xFF) bytes.Add((byte)c);
            else if (WinAnsiHigh.TryGetValue(c, out byte b)) bytes.Add(b);
            else if (Spelled.TryGetValue(c, out var sp)) foreach (char x in sp) bytes.Add(x == '•' ? (byte)0x95 : (byte)x);
            else if (char.IsLowSurrogate(c)) continue; // the second half of an emoji: its first half already became '?'
            else
            {
                string plain = new string(c.ToString().Normalize(NormalizationForm.FormD).Where(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark).ToArray());
                if (plain.Length == 1 && plain[0] < 0x7F && plain[0] >= 0x20) bytes.Add((byte)plain[0]);
                else bytes.Add((byte)'?');
            }
        }
        return bytes.ToArray();
    }

    static double[][]? _widths; // per font (regular, bold, italic, bold italic): each WinAnsi byte's width in em

    static double[][] Widths()
    {
        if (_widths != null) return _widths;
        var files = new[] { "arial.ttf", "arialbd.ttf", "ariali.ttf", "arialbi.ttf" };
        var all = new double[4][];
        string fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        for (int f = 0; f < 4; f++)
        {
            var w = Enumerable.Repeat(0.556, 256).ToArray();
            try
            {
                var gt = new System.Windows.Media.GlyphTypeface(new Uri(Path.Combine(fonts, files[f])));
                for (int b = 32; b < 256; b++)
                {
                    char c = b >= 0x80 && b < 0xA0 ? WinAnsiHigh.FirstOrDefault(p => p.Value == b).Key : (char)b;
                    if (c != '\0' && gt.CharacterToGlyphMap.TryGetValue(c, out ushort g)) w[b] = gt.AdvanceWidths[g];
                }
            }
            catch { } // no Arial (very rare): an average width keeps it readable
            all[f] = w;
        }
        return _widths = all;
    }

    // ───────────── the page writer ─────────────

    /// <summary>Fonts: 1 Helvetica, 2 bold, 3 italic, 4 bold italic, 5 Courier, 6 Courier bold.</summary>
    sealed class PdfPages
    {
        public const double W = 595.28, H = 841.89, ML = 62, MR = 62, MT = 68, MB = 72;
        public const double Wd = W - ML - MR;
        public readonly List<StringBuilder> Pages = new();
        public readonly List<List<(double X, double Y, double W, double H, string Url)>> Links = new();
        StringBuilder _p = null!;
        public double Y;

        public PdfPages() => NewPage();
        public void NewPage() { _p = new StringBuilder(); Pages.Add(_p); Links.Add(new()); Y = H - MT; }
        public bool Need(double h) { if (Y - h >= MB || Y >= H - MT - 0.5) return false; NewPage(); return true; }

        static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        static string Rgb(string hex) => string.Join(" ", Enumerable.Range(0, 3).Select(i => F(Convert.ToInt32(hex.Substring(i * 2, 2), 16) / 255.0)));

        public void Box(double x, double y, double w, double h, string fill) => _p.Append($"{Rgb(fill)} rg {F(x)} {F(y)} {F(w)} {F(h)} re f\n");
        public void LineAt(double x1, double y1, double x2, double y2, string color, double width) => _p.Append($"{Rgb(color)} RG {F(width)} w {F(x1)} {F(y1)} m {F(x2)} {F(y2)} l S\n");
        public void Text(double x, double y, int font, double size, string color, string s)
        {
            if (s.Length == 0) return;
            _p.Append($"BT /F{font} {F(size)} Tf {Rgb(color)} rg {F(x)} {F(y)} Td (");
            foreach (byte b in AnsiBytes(s))
            {
                if (b is (byte)'(' or (byte)')' or (byte)'\\') _p.Append('\\').Append((char)b);
                else if (b < 0x80) _p.Append((char)b);
                else _p.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0'));
            }
            _p.Append(") Tj ET\n");
        }
        public void Link(double x, double y, double w, double h, string url) => Links[^1].Add((x, y, w, h, url));

        public static double Measure(string s, int font, double size)
        {
            if (font >= 5) return AnsiBytes(s).Length * 0.6 * size;
            var w = Widths()[font - 1];
            double sum = 0;
            foreach (byte b in AnsiBytes(s)) sum += w[b];
            return sum * size;
        }
    }

    sealed record Tok(string Text, int Font, string Color, string? Link, bool Space, double Width);
    sealed record PLine(List<Tok> Toks, double Width);

    /// <summary>Lays text out in lines of at most <paramref name="width"/> points (long words break where they must).</summary>
    static List<PLine> LayoutRuns(IEnumerable<Run> runs, double width, double size, bool bold = false, bool italic = false, string color = Ink)
    {
        var toks = new List<Tok>();
        foreach (var r in runs)
        {
            int font = r.Code ? (r.Bold || bold ? 6 : 5) : (r.Bold || bold ? 2 : 1) + (r.Italic || italic ? 2 : 0);
            string col = r.Link != null ? Accent2 : r.Code ? "24292F" : color;
            foreach (var piece in Regex.Split(r.Text.Replace("\n", " "), "( +)"))
            {
                if (piece.Length == 0) continue;
                bool space = piece[0] == ' ';
                toks.Add(new Tok(space ? " " : piece, font, col, r.Link, space, PdfPages.Measure(space ? " " : piece, font, size)));
            }
        }
        var lines = new List<PLine>();
        var cur = new List<Tok>();
        double w = 0;
        void Push() { while (cur.Count > 0 && cur[^1].Space) { w -= cur[^1].Width; cur.RemoveAt(cur.Count - 1); } lines.Add(new PLine(cur, Math.Max(0, w))); cur = new(); w = 0; }
        foreach (var t in toks)
        {
            if (t.Space && cur.Count == 0) continue;
            if (!t.Space && w + t.Width > width && cur.Count > 0) Push();
            if (!t.Space && t.Width > width)
            {
                // a word longer than the line: cut it into pieces that fit
                string rest = t.Text;
                while (rest.Length > 0)
                {
                    int n = rest.Length;
                    while (n > 1 && PdfPages.Measure(rest[..n], t.Font, size) > width - w) n--;
                    var part = rest[..n];
                    var pt = t with { Text = part, Width = PdfPages.Measure(part, t.Font, size) };
                    cur.Add(pt); w += pt.Width;
                    rest = rest[n..];
                    if (rest.Length > 0) Push();
                }
                continue;
            }
            cur.Add(t); w += t.Width;
        }
        if (cur.Count > 0 || lines.Count == 0) Push();
        return lines;
    }

    static void DrawLine(PdfPages pg, PLine line, double x, double baseline, double size, char align = 'l', double width = 0)
    {
        double at = align == 'r' ? x + width - line.Width : align == 'c' ? x + (width - line.Width) / 2 : x;
        // runs of the same look go out as one piece of text
        int i = 0;
        while (i < line.Toks.Count)
        {
            var t = line.Toks[i];
            var sb = new StringBuilder(t.Text);
            double w = t.Width;
            int j = i + 1;
            while (j < line.Toks.Count && line.Toks[j].Font == t.Font && line.Toks[j].Color == t.Color && line.Toks[j].Link == t.Link) { sb.Append(line.Toks[j].Text); w += line.Toks[j].Width; j++; }
            pg.Text(at, baseline, t.Font, size, t.Color, sb.ToString());
            if (t.Link != null) { pg.Link(at, baseline - size * 0.25, w, size * 1.1, t.Link); pg.LineAt(at, baseline - 1.4, at + w, baseline - 1.4, Accent2, 0.5); }
            at += w;
            i = j;
        }
    }

    /// <summary>Lines down the page, onto new pages as needed. <paramref name="decorate"/> draws beside each line (a quote's bar, a code box).</summary>
    static void Flow(PdfPages pg, List<PLine> lines, double x, double width, double size, double leading, char align = 'l', Action<double, double>? decorate = null, Action<int, double>? first = null)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            pg.Need(leading);
            double top = pg.Y;
            double baseline = top - leading + (leading - size) / 2 + size * 0.22;
            decorate?.Invoke(top, leading);
            if (i == 0) first?.Invoke(i, baseline);
            DrawLine(pg, lines[i], x, baseline, size, align, width);
            pg.Y -= leading;
        }
    }

    static byte[] Pdf(string md, string fallbackTitle)
    {
        var blocks = Parse(md);
        string docTitle = TitleOf(blocks, fallbackTitle);
        var pg = new PdfPages();
        const double X0 = PdfPages.ML, Wd = PdfPages.Wd, Body = 10.5, Lead = 15.6;
        bool titleDone = false, afterTitle = false;
        Kind? prev = null;
        int[] counters = new int[4];

        foreach (var b in blocks)
        {
            if (b.Kind != Kind.Number) Array.Clear(counters);
            switch (b.Kind)
            {
                case Kind.Heading when b.Level == 1 && !titleDone && prev == null:
                {
                    var lines = LayoutRuns(Runs(b.Text), Wd, 24, bold: true, color: Accent);
                    Flow(pg, lines, X0, Wd, 24, 30);
                    pg.Y -= 6;
                    pg.LineAt(X0, pg.Y, X0 + Wd, pg.Y, Accent2, 1.4);
                    pg.Y -= 18;
                    titleDone = true;
                    afterTitle = true;
                    prev = b.Kind;
                    continue;
                }
                case Kind.Heading:
                {
                    int lvl = Math.Clamp(b.Level - (titleDone ? 1 : 0), 1, 3);
                    (double size, string color, double before, double after) = lvl switch { 1 => (17.0, Accent, 16.0, 6.0), 2 => (13.5, Accent2, 12.0, 4.0), _ => (11.5, Ink, 9.0, 3.0) };
                    var lines = LayoutRuns(Runs(b.Text), Wd, size, bold: true, color: color);
                    if (pg.Y < PdfPages.H - PdfPages.MT - 1) pg.Y -= before;
                    pg.Need(lines.Count * size * 1.3 + after + Lead * 2); // never alone at the foot of a page
                    Flow(pg, lines, X0, Wd, size, size * 1.3);
                    pg.Y -= after;
                    break;
                }
                case Kind.Paragraph:
                {
                    bool subtitle = afterTitle && b.Text.Length < 140 && !b.Text.Contains('\n'); // only the line right under the title
                    double size = subtitle ? 12.5 : Body;
                    foreach (var part in b.Text.Split('\n'))
                        Flow(pg, LayoutRuns(Runs(part), Wd, size, color: subtitle ? Muted : Ink), X0, Wd, size, subtitle ? 17 : Lead);
                    pg.Y -= subtitle ? 12 : 7;
                    break;
                }
                case Kind.Bullet:
                case Kind.Number:
                {
                    double indent = 4 + b.Level * 18, gutter = 16;
                    string mark;
                    if (b.Kind == Kind.Bullet) mark = b.Level == 0 ? "•" : "-";
                    else
                    {
                        int l = Math.Min(3, b.Level);
                        counters[l] = counters[l] == 0 ? Math.Max(1, b.Num) : counters[l] + 1;
                        for (int k = l + 1; k < 4; k++) counters[k] = 0;
                        mark = l switch { 0 => counters[l] + ".", 1 => (char)('a' + (counters[l] - 1) % 26) + ".", _ => counters[l] + "." };
                        gutter = 20;
                    }
                    var lines = LayoutRuns(Runs(b.Text), Wd - indent - gutter, Body);
                    Flow(pg, lines, X0 + indent + gutter, Wd - indent - gutter, Body, Lead - 1, first: (_, baseline) =>
                    {
                        if (b.Kind == Kind.Bullet) pg.Text(X0 + indent + 2, baseline, 2, Body, Accent2, mark);
                        else pg.Text(X0 + indent + gutter - 5 - PdfPages.Measure(mark, 1, Body), baseline, 1, Body, Accent2, mark);
                    });
                    pg.Y -= 3;
                    break;
                }
                case Kind.Quote:
                {
                    var lines = LayoutRuns(Runs(b.Text), Wd - 22, Body, italic: true, color: "4B5563");
                    pg.Y -= 7;
                    Flow(pg, lines, X0 + 16, Wd - 22, Body, Lead, decorate: (top, h) => pg.Box(X0 + 2, top - h, 2.6, h, Accent3));
                    pg.Y -= 9;
                    break;
                }
                case Kind.Code:
                {
                    const double size = 8.8, lead = 12;
                    int perLine = Math.Max(20, (int)((Wd - 20) / (0.6 * size)));
                    var all = new List<PLine>();
                    foreach (var raw in b.Text.Split('\n'))
                    {
                        string line = raw.Replace("\t", "    ");
                        do
                        {
                            string part = line.Length > perLine ? line[..perLine] : line;
                            line = line.Length > perLine ? line[perLine..] : "";
                            all.Add(new PLine(new() { new Tok(part, 5, "24292F", null, false, part.Length * 0.6 * size) }, part.Length * 0.6 * size));
                        } while (line.Length > 0);
                    }
                    pg.Need(all.Count <= 18 ? all.Count * lead + 12 : lead + 8); // a short block stays in one piece
                    pg.Box(X0, pg.Y - 5, Wd, 5, "F4F5F7");
                    pg.Y -= 5;
                    Flow(pg, all, X0 + 10, Wd - 20, size, lead, decorate: (top, h) => pg.Box(X0, top - h, Wd, h + 0.4, "F4F5F7"));
                    pg.Box(X0, pg.Y - 5, Wd, 5, "F4F5F7");
                    pg.Y -= 15;
                    break;
                }
                case Kind.Rule:
                    pg.Need(16);
                    pg.Y -= 6;
                    pg.LineAt(X0, pg.Y, X0 + Wd, pg.Y, Rule, 0.8);
                    pg.Y -= 12;
                    break;
                case Kind.PageBreak:
                    pg.NewPage();
                    break;
                case Kind.Table when b.Rows is { Count: > 0 } rows:
                    PdfTable(pg, rows, b.Align);
                    pg.Y -= 12;
                    break;
            }
            prev = b.Kind;
            afterTitle = false;
        }
        return PdfFile(pg, docTitle);
    }

    static void PdfTable(PdfPages pg, List<string[]> rows, char[]? align)
    {
        const double size = 9.4, lead = 12.6, padX = 6, padY = 5;
        double X0 = PdfPages.ML, Wd = PdfPages.Wd;
        int cols = rows.Max(r => r.Length);
        string Cell(int r, int c) => c < rows[r].Length ? rows[r][c] : "";
        // widths: as wide as the content asks, at least the longest word, together the page's width
        var natural = new double[cols];
        var min = new double[cols];
        for (int c = 0; c < cols; c++)
            for (int r = 0; r < rows.Count; r++)
            {
                string t = Plain(Cell(r, c));
                int font = r == 0 ? 2 : 1;
                natural[c] = Math.Max(natural[c], PdfPages.Measure(t, font, size) + padX * 2 + 1);
                foreach (var word in t.Split(' ')) min[c] = Math.Max(min[c], Math.Min(Wd / cols * 1.6, PdfPages.Measure(word, font, size) + padX * 2 + 1));
            }
        double sumN = natural.Sum(), sumM = min.Sum();
        double[] widths;
        if (sumN <= Wd) widths = natural.Select(n => n * Wd / sumN).ToArray();
        else if (sumM <= Wd) widths = Enumerable.Range(0, cols).Select(c => min[c] + (natural[c] - min[c]) * (Wd - sumM) / Math.Max(1, sumN - sumM)).ToArray();
        else widths = min.Select(m => m * Wd / sumM).ToArray();

        var numeric = Enumerable.Range(0, cols).Select(c =>
        {
            var vals = rows.Skip(1).Select(r => c < r.Length ? Plain(r[c]).Trim() : "").Where(v => v.Length > 0).ToList();
            return vals.Count > 0 && vals.All(v => LooksNumeric(v) || v is "-" or "—");
        }).ToArray();
        char Align(int c) => align != null && c < align.Length && align[c] != 'l' ? align[c] : numeric[c] ? 'r' : 'l';

        List<PLine>[] LayoutRow(int r) => Enumerable.Range(0, cols).Select(c =>
            LayoutRuns(Runs(Cell(r, c)), widths[c] - padX * 2, size, bold: r == 0 || IsTotalRow(rows[r]), color: r == 0 ? "FFFFFF" : Ink)).ToArray();

        var header = LayoutRow(0);
        double headerH = header.Max(l => l.Count) * lead + padY * 2;

        void DrawRow(List<PLine>[] cells, double h, string? fill, bool total)
        {
            double top = pg.Y;
            if (fill != null) pg.Box(X0, top - h, Wd, h, fill);
            if (total) pg.LineAt(X0, top, X0 + Wd, top, Accent2, 1.1);
            double x = X0;
            for (int c = 0; c < cols; c++)
            {
                double y = top - padY;
                foreach (var line in cells[c])
                {
                    double baseline = y - lead + (lead - size) / 2 + size * 0.22;
                    DrawLine(pg, line, x + padX, baseline, size, Align(c), widths[c] - padX * 2);
                    y -= lead;
                }
                x += widths[c];
            }
            pg.Y -= h;
            pg.LineAt(X0, pg.Y, X0 + Wd, pg.Y, Rule, 0.6);
        }

        pg.Need(headerH + lead * 2 + padY * 2);
        DrawRow(header, headerH, Accent, false);
        for (int r = 1; r < rows.Count; r++)
        {
            var cells = LayoutRow(r);
            double h = cells.Max(l => l.Count) * lead + padY * 2;
            if (pg.Need(h)) DrawRow(header, headerH, Accent, false); // a new page: the header again
            bool total = IsTotalRow(rows[r]);
            DrawRow(cells, h, total ? "E3E9F3" : r % 2 == 0 ? Band : null, total);
        }
    }

    /// <summary>The file: catalog, pages (each with its text, links, the title above and the page number below), fonts.</summary>
    static byte[] PdfFile(PdfPages pg, string title)
    {
        int count = pg.Pages.Count;
        for (int i = 0; i < count; i++)
        {
            var p = pg.Pages[i];
            string num = $"{i + 1} / {count}";
            double w = PdfPages.Measure(num, 1, 8.5);
            // drawn on a scratch page with the same writer, then added to the page's own drawing
            var tmp = new PdfPages();
            tmp.Text((PdfPages.W - w) / 2, 38, 1, 8.5, "8A8F98", num);
            if (i > 0)
            {
                string t = title.Length > 80 ? title[..80] + "…" : title;
                tmp.Text(PdfPages.ML, PdfPages.H - 40, 1, 8.5, "8A8F98", t);
                tmp.LineAt(PdfPages.ML, PdfPages.H - 46, PdfPages.W - PdfPages.MR, PdfPages.H - 46, "E5E8EE", 0.6);
            }
            p.Append(tmp.Pages[0]);
        }

        var objects = new List<byte[]>();
        int Add(byte[] o) { objects.Add(o); return objects.Count; }
        int Reserve() { objects.Add(Array.Empty<byte>()); return objects.Count; }
        byte[] B(string s) => Encoding.Latin1.GetBytes(s);

        int catalog = Reserve(), pagesId = Reserve(), info = Reserve();
        string[] baseFonts = { "Helvetica", "Helvetica-Bold", "Helvetica-Oblique", "Helvetica-BoldOblique", "Courier", "Courier-Bold" };
        var fontIds = baseFonts.Select(f => Add(B($"<< /Type /Font /Subtype /Type1 /BaseFont /{f} /Encoding /WinAnsiEncoding >>"))).ToArray();
        string fontRes = string.Join(" ", fontIds.Select((id, i) => $"/F{i + 1} {id} 0 R"));
        var pageIds = new List<int>();
        for (int i = 0; i < count; i++)
        {
            byte[] raw = B(pg.Pages[i].ToString());
            using var ms = new MemoryStream();
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, true)) z.Write(raw, 0, raw.Length);
            byte[] packed = ms.ToArray();
            var content = new List<byte>(B($"<< /Length {packed.Length} /Filter /FlateDecode >>\nstream\n"));
            content.AddRange(packed);
            content.AddRange(B("\nendstream"));
            int contentId = Add(content.ToArray());
            var annots = pg.Links[i].Select(l => Add(B($"<< /Type /Annot /Subtype /Link /Rect [{N(l.X)} {N(l.Y)} {N(l.X + l.W)} {N(l.Y + l.H)}] /Border [0 0 0] /A << /S /URI /URI {PdfString(l.Url, ascii: true)} >> >>"))).ToList();
            pageIds.Add(Add(B($"<< /Type /Page /Parent {pagesId} 0 R /MediaBox [0 0 {N(PdfPages.W)} {N(PdfPages.H)}] /Resources << /Font << {fontRes} >> >> /Contents {contentId} 0 R"
                + (annots.Count > 0 ? $" /Annots [{string.Join(" ", annots.Select(a => a + " 0 R"))}]" : "") + " >>")));
        }
        objects[catalog - 1] = B($"<< /Type /Catalog /Pages {pagesId} 0 R /ViewerPreferences << /DisplayDocTitle true >> >>");
        objects[pagesId - 1] = B($"<< /Type /Pages /Kids [{string.Join(" ", pageIds.Select(p => p + " 0 R"))}] /Count {count} >>");
        objects[info - 1] = B($"<< /Title {PdfString(title)} /Producer (Pixo) /CreationDate (D:{DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}) >>"); // (Gregorian whatever the PC's calendar)

        using var file = new MemoryStream();
        void Put(byte[] b) => file.Write(b, 0, b.Length);
        Put(B("%PDF-1.4\n%âãÏÓ\n"));
        var offsets = new List<long>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(file.Position);
            Put(B($"{i + 1} 0 obj\n"));
            Put(objects[i]);
            Put(B("\nendobj\n"));
        }
        long xref = file.Position;
        var x = new StringBuilder($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) x.Append($"{o:0000000000} 00000 n \n");
        x.Append($"trailer\n<< /Size {objects.Count + 1} /Root {catalog} 0 R /Info {info} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        Put(B(x.ToString()));
        return file.ToArray();
    }

    /// <summary>A PDF text string: plain when it's simple ASCII, else UTF-16 (any language, for the title).</summary>
    static string PdfString(string s, bool ascii = false)
    {
        if (ascii || s.All(c => c >= 0x20 && c < 0x7F))
            return "(" + string.Concat(s.Where(c => c >= 0x20 && c < 0x7F).Select(c => c is '(' or ')' or '\\' ? "\\" + c : c.ToString())) + ")";
        return "<FEFF" + string.Concat(Encoding.BigEndianUnicode.GetBytes(s).Select(b => b.ToString("X2"))) + ">";
    }
}
