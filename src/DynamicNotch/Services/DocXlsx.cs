using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DynamicNotch.Services;

/// <summary>
/// Excel workbooks. Each "## " heading starts a sheet (named after it); the first "# " heading is the workbook's
/// title. Every sheet: its title in row 1, the table's header in row 3 (styled, filters on, frozen below it), the data
/// from row 4. Formulas are written by the AI as if the header were row 1 (the way anyone writes them), and moved down
/// to where the table really is. Numbers, money, percents and ISO dates become real values with formats; a "Total" row
/// is bold with a line above it; columns fit their content; long text wraps. A line "chart: column|bar|line|pie|area
/// [title]" under a table adds a chart of it beside the table. Text under a table becomes notes below it.
/// </summary>
public static partial class DocWriter
{
    const int TitleRows = 2; // the title row and a breath under it: the table's header is row 3 on every sheet

    sealed class SheetSpec
    {
        public string Name = "Sheet1";
        public string? Title;
        public readonly List<(string? Caption, List<string[]> Rows)> Tables = new();
        public readonly List<string> Notes = new();
        public string? Chart, ChartTitle;
    }

    static readonly Regex ChartLine = new(@"^\s*chart\s*:\s*(column|columns|bar|bars|line|lines|pie|area|donut|doughnut)\b\s*[-:–]?\s*(.*)$", RegexOptions.IgnoreCase);

    static List<SheetSpec> SheetsFrom(string content, string fallbackTitle)
    {
        var blocks = Parse(content);
        var sheets = new List<SheetSpec>();
        string? bookTitle = null;
        SheetSpec? cur = null;
        string? caption = null;
        SheetSpec Cur() { if (cur == null) { cur = new SheetSpec(); sheets.Add(cur); } return cur; }

        if (!blocks.Any(b => b.Kind == Kind.Table))
        {
            // no markdown table: CSV-like rows (comma, semicolon or tab)
            var lines = content.Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith("```") && !l.TrimStart().StartsWith("#")).ToList();
            char sep = lines.Count == 0 ? ',' : new[] { '\t', ';', ',' }.OrderByDescending(c => lines.Take(5).Sum(l => l.Count(ch => ch == c))).First();
            var s = new SheetSpec { Title = blocks.FirstOrDefault(b => b.Kind == Kind.Heading)?.Text };
            s.Tables.Add((null, lines.Select(l => SplitCsv(l, sep)).ToList()));
            sheets.Add(s);
        }
        else
            foreach (var b in blocks)
            {
                switch (b.Kind)
                {
                    case Kind.Heading when b.Level == 1 && bookTitle == null && sheets.Count == 0:
                        bookTitle = Plain(b.Text);
                        break;
                    case Kind.Heading when b.Level <= 2:
                        cur = new SheetSpec { Title = Plain(b.Text) };
                        sheets.Add(cur);
                        caption = null;
                        break;
                    case Kind.Heading:
                        caption = Plain(b.Text);
                        break;
                    case Kind.Table:
                        Cur().Tables.Add((caption, b.Rows!));
                        caption = null;
                        break;
                    case Kind.Paragraph when ChartLine.Match(Plain(b.Text)) is { Success: true } m:
                        Cur().Chart = m.Groups[1].Value.ToLowerInvariant();
                        Cur().ChartTitle = m.Groups[2].Value.Trim().Trim('"');
                        break;
                    case Kind.Paragraph or Kind.Bullet or Kind.Number or Kind.Quote:
                        Cur().Notes.Add((b.Kind == Kind.Bullet ? "• " : b.Kind == Kind.Number ? b.Num + ". " : "") + Plain(b.Text));
                        break;
                }
            }
        sheets.RemoveAll(s => s.Tables.Count == 0 && s.Notes.Count == 0);
        if (sheets.Count == 0) sheets.Add(new SheetSpec());
        // names: from the headings, Excel's rules (31 letters, no []:*?/\), each different
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < sheets.Count; i++)
        {
            var s = sheets[i];
            if (s.Title == null && i == 0) s.Title = bookTitle;
            string name = Regex.Replace(Regex.Replace(s.Title ?? "", @"^(sheet|feuille|onglet)\s*\d*\s*[:\-–]\s*", "", RegexOptions.IgnoreCase), @"[\[\]:*?/\\']", " ").Trim();
            if (name.Length == 0) name = sheets.Count == 1 ? "Sheet1" : "Sheet" + (i + 1);
            if (name.Length > 31) name = name[..31].Trim();
            string baseName = name;
            for (int k = 2; !used.Add(name); k++) name = (baseName.Length > 27 ? baseName[..27] : baseName) + " " + k;
            s.Name = name;
            s.Title ??= sheets.Count == 1 ? bookTitle ?? Regex.Replace(fallbackTitle, @"[_\-]+", " ") : name;
            if (i == 0 && bookTitle != null) s.Title = bookTitle; // the first sheet carries the workbook's title (its tab keeps its own name)
        }
        return sheets;
    }

    // ───────────── cell values ─────────────

    enum CellKind { Empty, Text, Number, Percent, Money, Date, Formula }
    record struct Cell(CellKind Kind, string Text, double Value = 0, int Decimals = 0, string Symbol = "", bool SymbolAfter = false);

    static readonly Regex MoneySym = new(@"^(?<pre>[$€£¥₹]|USD|EUR|GBP|MAD|DH|DHS)?\s*(?<num>[-−+(]?\s*\d[\d\s  ,.']*\)?)\s*(?<post>[$€£¥₹]|USD|EUR|GBP|MAD|DH|DHS|dirhams?)?$", RegexOptions.IgnoreCase);

    static Cell ParseCell(string raw)
    {
        string s = Plain(raw).Trim();
        if (s.Length == 0 || s is "-" or "—" or "–") return new Cell(s.Length == 0 ? CellKind.Empty : CellKind.Text, s);
        if (s.StartsWith('=') && s.Length > 1) return new Cell(CellKind.Formula, s[1..]);
        // ISO dates (and date-times): real dates
        if (Regex.IsMatch(s, @"^\d{4}-\d{2}-\d{2}( \d{2}:\d{2})?$") && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return new Cell(CellKind.Date, s, dt.ToOADate(), s.Length > 10 ? 1 : 0);
        // phone numbers, codes with leading zeros, long ids: text
        if (s.StartsWith('+') || Regex.IsMatch(s, @"^0\d") || Regex.Replace(s, @"\D", "").Length > 15) return new Cell(CellKind.Text, s);
        bool pct = s.EndsWith('%');
        string body = pct ? s[..^1].Trim() : s;
        var m = MoneySym.Match(body);
        if (!m.Success) return new Cell(CellKind.Text, s);
        string num = m.Groups["num"].Value.Replace(" ", "").Replace(" ", "").Replace(" ", "").Replace("'", "").Replace('−', '-');
        bool neg = num.StartsWith('(') && num.EndsWith(')');
        num = num.Trim('(', ')', '+');
        if (num.StartsWith('-')) { neg = !neg; num = num[1..]; }
        if (!TryNumber(num, out double v, out int dec)) return new Cell(CellKind.Text, s);
        if (neg) v = -v;
        string sym = m.Groups["pre"].Success && m.Groups["pre"].Length > 0 ? m.Groups["pre"].Value : m.Groups["post"].Success ? m.Groups["post"].Value : "";
        if (pct) return new Cell(CellKind.Percent, s, v / 100, dec);
        if (sym.Length > 0) return new Cell(CellKind.Money, s, v, dec, SymbolOf(sym), !(m.Groups["pre"].Length > 0));
        return new Cell(CellKind.Number, s, v, dec);
    }

    static string SymbolOf(string s) => s.ToUpperInvariant() switch { "USD" => "$", "EUR" => "€", "GBP" => "£", "DHS" or "DIRHAM" or "DIRHAMS" => "DH", _ => s.Length <= 3 ? s.ToUpperInvariant() == "DH" ? "DH" : s : s };

    /// <summary>"1,234.5", "1.234,5", "1234,5", "12,000": the last separator with 1-2 digits after it (or any '.') is the decimal point.</summary>
    static bool TryNumber(string s, out double v, out int decimals)
    {
        v = 0; decimals = 0;
        if (!Regex.IsMatch(s, @"^\d[\d,.]*$")) return false;
        int lastDot = s.LastIndexOf('.'), lastComma = s.LastIndexOf(',');
        string norm;
        if (lastDot >= 0 && lastComma >= 0)
            norm = lastDot > lastComma ? s.Replace(",", "") : s.Replace(".", "").Replace(',', '.');
        else if (lastComma >= 0)
            norm = Regex.IsMatch(s, @"^\d{1,3}(,\d{3})+$") ? s.Replace(",", "") : s.Count(c => c == ',') == 1 ? s.Replace(',', '.') : s.Replace(",", "");
        else if (lastDot >= 0 && Regex.IsMatch(s, @"^\d{1,3}(\.\d{3}){2,}$"))
            norm = s.Replace(".", ""); // 1.234.567
        else norm = s;
        if (!double.TryParse(norm, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return false;
        int dot = norm.IndexOf('.');
        decimals = dot < 0 ? 0 : norm.Length - dot - 1;
        return true;
    }

    /// <summary>The format a column's numbers share: money with its symbol, percents, decimals, plain counts, years/ids.</summary>
    static string ColumnFormat(string header, List<Cell> cells)
    {
        var nums = cells.Where(c => c.Kind is CellKind.Number or CellKind.Money or CellKind.Percent or CellKind.Date).ToList();
        // a column of formulas only: a percent when its header says so (progress, share, rate...)
        if (nums.Count == 0)
            return cells.Any(c => c.Kind == CellKind.Formula) && Regex.IsMatch(header, @"%|percent|pourcent|progress|progr[eè]s|rate|taux|share|ratio|margin|marge|completion|growth|croissance|avancement", RegexOptions.IgnoreCase) ? "0%" : "";
        if (nums.Count(c => c.Kind == CellKind.Date) * 2 > nums.Count) return nums.Any(c => c.Decimals == 1) ? "yyyy-mm-dd hh:mm" : "yyyy-mm-dd";
        int dec = Math.Min(2, nums.Max(c => c.Decimals));
        string d = dec > 0 ? "." + new string('0', dec) : "";
        var money = nums.Where(c => c.Kind == CellKind.Money).ToList();
        if (money.Count > 0)
        {
            var c0 = money.GroupBy(c => (c.Symbol, c.SymbolAfter)).OrderByDescending(g => g.Count()).First().Key;
            string q = "\"" + c0.Symbol.Replace("\"", "") + "\"";
            string fmt = c0.SymbolAfter ? $"#,##0{d}\\ {q}" : c0.Symbol.Length > 1 ? $"{q}\\ #,##0{d}" : $"{q}#,##0{d}";
            return fmt + ";-" + fmt;
        }
        if (nums.Count(c => c.Kind == CellKind.Percent) * 2 > nums.Count) return "0" + (nums.Max(c => c.Decimals) > 0 ? ".0" : "") + "%";
        bool yearish = Regex.IsMatch(header, @"\b(year|ann[ée]e|id|code|zip|postal|no\.?|n°|#|number|num[ée]ro|ref)\b", RegexOptions.IgnoreCase)
            || nums.All(c => c.Kind == CellKind.Number && c.Decimals == 0 && c.Value >= 1900 && c.Value <= 2100);
        if (yearish) return "0";
        return "#,##0" + d;
    }

    /// <summary>A formula written as if the header were row 1: its row numbers moved down to the table's real place.</summary>
    static string ShiftFormula(string f, int by)
    {
        if (by == 0) return f;
        var sb = new StringBuilder();
        // leave text in quotes alone
        foreach (var (part, i) in Regex.Split(f, "(\"[^\"]*\")").Select((p, i) => (p, i)))
        {
            if (part.StartsWith('"')) { sb.Append(part); continue; }
            sb.Append(Regex.Replace(part, @"(?<![A-Za-z_\d.$])(\$?)([A-Za-z]{1,3})(\$?)(\d+)(?![\d(A-Za-z_])", m =>
            {
                // locked rows ($14) too: they were written in the same "header in row 1" picture
                if (!int.TryParse(m.Groups[4].Value, out int row) || row < 1 || row > 1048000) return m.Value;
                return m.Groups[1].Value + m.Groups[2].Value.ToUpperInvariant() + m.Groups[3].Value + (row + by).ToString(CultureInfo.InvariantCulture);
            }));
        }
        return sb.ToString();
    }

    // ───────────── styles ─────────────

    sealed class XlStyles
    {
        readonly Dictionary<string, int> _fmts = new();
        readonly List<string> _xfs = new() { "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" };
        readonly Dictionary<string, int> _xfIndex = new();

        public const int FBody = 0, FHeader = 1, FBold = 2, FTitle = 3, FNote = 4, FCaption = 5;
        public const int LNone = 0, LBand = 3, LTotal = 4, LHeader = 2;
        public const int BNone = 0, BThin = 1, BTotal = 2;

        int Fmt(string code)
        {
            if (code.Length == 0) return 0;
            if (!_fmts.TryGetValue(code, out int id)) _fmts[code] = id = 164 + _fmts.Count;
            return id;
        }

        public int Xf(int font, int fill, int border, string fmt = "", string h = "", bool wrap = false, string v = "center")
        {
            int f = Fmt(fmt);
            string key = $"{font}|{fill}|{border}|{f}|{h}|{wrap}|{v}";
            if (_xfIndex.TryGetValue(key, out int i)) return i;
            string align = $"<alignment vertical=\"{v}\"{(h.Length > 0 ? $" horizontal=\"{h}\"" : "")}{(wrap ? " wrapText=\"1\"" : "")}/>";
            _xfs.Add($"<xf numFmtId=\"{f}\" fontId=\"{font}\" fillId=\"{fill}\" borderId=\"{border}\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\">{align}</xf>");
            _xfIndex[key] = _xfs.Count - 1;
            return _xfs.Count - 1;
        }

        public string Xml()
        {
            string Font(string inner) => $"<font>{inner}<name val=\"Calibri\"/><family val=\"2\"/><scheme val=\"minor\"/></font>";
            string Fill(string rgb) => $"<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF{rgb}\"/><bgColor indexed=\"64\"/></patternFill></fill>";
            string Side(string name, string style, string rgb) => style.Length == 0 ? $"<{name}/>" : $"<{name} style=\"{style}\"><color rgb=\"FF{rgb}\"/></{name}>";
            var sb = new StringBuilder(XmlHead + "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
            if (_fmts.Count > 0)
            {
                sb.Append($"<numFmts count=\"{_fmts.Count}\">");
                foreach (var (code, id) in _fmts) sb.Append($"<numFmt numFmtId=\"{id}\" formatCode=\"{X(code)}\"/>");
                sb.Append("</numFmts>");
            }
            sb.Append("<fonts count=\"6\">")
              .Append(Font($"<sz val=\"11\"/><color rgb=\"FF{Ink}\"/>"))
              .Append(Font("<b/><sz val=\"11\"/><color rgb=\"FFFFFFFF\"/>"))
              .Append(Font($"<b/><sz val=\"11\"/><color rgb=\"FF{Ink}\"/>"))
              .Append(Font($"<b/><sz val=\"16\"/><color rgb=\"FF{Accent}\"/>"))
              .Append(Font($"<i/><sz val=\"10\"/><color rgb=\"FF{Muted}\"/>"))
              .Append(Font($"<b/><sz val=\"12\"/><color rgb=\"FF{Accent2}\"/>"))
              .Append("</fonts>");
            sb.Append("<fills count=\"5\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill>")
              .Append(Fill(Accent)).Append(Fill(Band)).Append(Fill("E3E9F3")).Append("</fills>");
            sb.Append("<borders count=\"3\"><border><left/><right/><top/><bottom/><diagonal/></border>")
              .Append($"<border>{Side("left", "thin", Rule)}{Side("right", "thin", Rule)}{Side("top", "thin", Rule)}{Side("bottom", "thin", Rule)}<diagonal/></border>")
              .Append($"<border>{Side("left", "thin", Rule)}{Side("right", "thin", Rule)}{Side("top", "medium", Accent2)}{Side("bottom", "thin", Rule)}<diagonal/></border>")
              .Append("</borders>");
            sb.Append("<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>");
            sb.Append($"<cellXfs count=\"{_xfs.Count}\">").Append(string.Concat(_xfs)).Append("</cellXfs>");
            sb.Append("<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>");
            sb.Append("</styleSheet>");
            return sb.ToString();
        }
    }

    // ───────────── the workbook ─────────────

    static byte[] Xlsx(string content, string fallbackTitle)
    {
        var sheets = SheetsFrom(content, fallbackTitle);
        var st = new XlStyles();
        var parts = new Dictionary<string, string>();
        var types = new StringBuilder();
        var bookSheets = new StringBuilder();
        var bookRels = new StringBuilder();
        var defined = new StringBuilder();
        int charts = 0;

        for (int si = 0; si < sheets.Count; si++)
        {
            var s = sheets[si];
            var rowsXml = new StringBuilder();
            var widths = new Dictionary<int, double>();
            var wrapCols = new HashSet<int>();
            int maxCol = 1;
            int r = 0; // the last row written (1-based)
            string? filter = null;
            (int HeaderRow, int FirstData, int LastData, int Cols, List<int> NumCols)? chartTable = null;

            void Width(int c, double w) { widths[c] = Math.Max(widths.TryGetValue(c, out var x) ? x : 0, w); }
            void Row(int row, string cells, double? height = null)
                => rowsXml.Append($"<row r=\"{row}\"{(height is { } h ? $" ht=\"{N(h)}\" customHeight=\"1\"" : "")}>{cells}</row>");
            string Str(int c, int row, string text, int xf) => $"<c r=\"{Col(c)}{row}\" s=\"{xf}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{X(text)}</t></is></c>";

            // the title
            r = 1;
            Row(r, Str(0, r, s.Title ?? s.Name, st.Xf(XlStyles.FTitle, XlStyles.LNone, XlStyles.BNone, v: "center")), 26);
            r = TitleRows;

            foreach (var (caption, table) in s.Tables)
            {
                if (table.Count == 0) continue;
                int cols = table.Max(t => t.Length);
                maxCol = Math.Max(maxCol, cols);
                if (s.Tables[0].Rows != table)
                {
                    r++; // a blank row between two tables
                    if (caption != null) { r++; Row(r, Str(0, r, caption, st.Xf(XlStyles.FCaption, XlStyles.LNone, XlStyles.BNone)), 20); }
                }
                var header = table[0];
                var data = table.Skip(1).ToList();
                var parsed = data.Select(row => Enumerable.Range(0, cols).Select(c => ParseCell(c < row.Length ? row[c] : "")).ToArray()).ToList();
                var formats = Enumerable.Range(0, cols).Select(c => ColumnFormat(c < header.Length ? Plain(header[c]) : "", parsed.Where((_, i) => !IsTotalRow(data[i])).Select(p => p[c]).ToList())).ToArray();
                bool[] numeric = formats.Select(f => f.Length > 0).ToArray();

                // the header row
                int headerRow = ++r;
                if (s.Tables[0].Rows == table) r = headerRow = TitleRows + 1;
                var hx = new StringBuilder();
                for (int c = 0; c < cols; c++)
                {
                    string text = c < header.Length ? Plain(header[c]) : "";
                    hx.Append(Str(c, headerRow, text, st.Xf(XlStyles.FHeader, XlStyles.LHeader, XlStyles.BThin, h: numeric[c] ? "right" : "left", wrap: true)));
                    Width(c, Math.Min(30, text.Length * 1.1 + 4));
                }
                Row(headerRow, hx.ToString(), 24);

                // the data
                int shift = headerRow - 1; // formulas were written with the header in row 1
                for (int i = 0; i < data.Count; i++)
                {
                    int row = ++r;
                    bool total = IsTotalRow(data[i]);
                    bool band = !total && i % 2 == 1;
                    int fill = total ? XlStyles.LTotal : band ? XlStyles.LBand : XlStyles.LNone;
                    int border = total ? XlStyles.BTotal : XlStyles.BThin;
                    var cx = new StringBuilder();
                    for (int c = 0; c < cols; c++)
                    {
                        var cell = parsed[i][c];
                        bool boldCell = total || (c < data[i].Length && Regex.IsMatch(data[i][c].Trim(), @"^\*\*.+\*\*$"));
                        int font = boldCell ? XlStyles.FBold : XlStyles.FBody;
                        string refName = Col(c) + row;
                        switch (cell.Kind)
                        {
                            case CellKind.Formula:
                                cx.Append($"<c r=\"{refName}\" s=\"{st.Xf(font, fill, border, formats[c], h: numeric[c] ? "right" : "")}\"><f>{X(ShiftFormula(cell.Text, shift))}</f></c>");
                                Width(c, 12);
                                break;
                            case CellKind.Number or CellKind.Money or CellKind.Percent or CellKind.Date:
                                string fmt = formats[c].Length > 0 ? formats[c] : cell.Kind == CellKind.Percent ? "0%" : "General";
                                cx.Append($"<c r=\"{refName}\" s=\"{st.Xf(font, fill, border, fmt == "General" ? "" : fmt, h: "right")}\"><v>{cell.Value.ToString("R", CultureInfo.InvariantCulture)}</v></c>");
                                Width(c, cell.Text.Length + 4);
                                break;
                            default:
                                bool longText = cell.Text.Length > 48;
                                if (longText) wrapCols.Add(c);
                                cx.Append(Str(c, row, cell.Text, st.Xf(font, fill, border, h: "", wrap: longText, v: longText ? "top" : "center")));
                                Width(c, Math.Min(50, cell.Text.Length * 1.05 + 3));
                                break;
                        }
                    }
                    Row(row, cx.ToString());
                }
                if (filter == null && data.Count > 0)
                {
                    filter = $"{Col(0)}{headerRow}:{Col(cols - 1)}{r}";
                    var numCols = Enumerable.Range(1, cols - 1).Where(c => numeric[c] && !formats[c].StartsWith("yyyy") && formats[c] != "0").ToList();
                    // percents beside amounts would flatten the chart: amounts only, unless it's all percents
                    if (numCols.Any(c => !formats[c].Contains('%'))) numCols.RemoveAll(c => formats[c].Contains('%'));
                    int lastData = r - (data.Count > 0 && IsTotalRow(data[^1]) ? 1 : 0);
                    if (numCols.Count > 0 && lastData > headerRow) chartTable = (headerRow, headerRow + 1, lastData, cols, numCols);
                }
            }

            // notes under the tables
            if (s.Notes.Count > 0)
            {
                r++;
                foreach (var note in s.Notes) { r++; Row(r, Str(0, r, note, st.Xf(XlStyles.FNote, XlStyles.LNone, XlStyles.BNone, v: "top"))); }
            }

            // the sheet
            var sx = new StringBuilder(XmlHead + "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">");
            sx.Append("<sheetPr><pageSetUpPr fitToPage=\"1\"/></sheetPr>");
            sx.Append($"<dimension ref=\"A1:{Col(maxCol - 1)}{Math.Max(1, r)}\"/>");
            sx.Append($"<sheetViews><sheetView {(si == 0 ? "tabSelected=\"1\" " : "")}showGridLines=\"0\" workbookViewId=\"0\">");
            if (filter != null) sx.Append($"<pane ySplit=\"{TitleRows + 1}\" topLeftCell=\"A{TitleRows + 2}\" activePane=\"bottomLeft\" state=\"frozen\"/><selection pane=\"bottomLeft\" activeCell=\"A{TitleRows + 2}\" sqref=\"A{TitleRows + 2}\"/>");
            sx.Append("</sheetView></sheetViews>");
            sx.Append("<sheetFormatPr defaultRowHeight=\"18\" customHeight=\"1\"/>");
            sx.Append("<cols>");
            for (int c = 0; c < maxCol; c++)
            {
                double w = Math.Clamp(widths.TryGetValue(c, out var x) ? x : 10, 9, wrapCols.Contains(c) ? 52 : 46);
                sx.Append($"<col min=\"{c + 1}\" max=\"{c + 1}\" width=\"{N(w)}\" customWidth=\"1\"/>");
            }
            sx.Append("</cols>");
            sx.Append("<sheetData>").Append(rowsXml).Append("</sheetData>");
            if (filter != null)
            {
                sx.Append($"<autoFilter ref=\"{filter}\"/>");
                defined.Append($"<definedName name=\"_xlnm._FilterDatabase\" localSheetId=\"{si}\" hidden=\"1\">{X("'" + s.Name.Replace("'", "''") + "'")}!{Regex.Replace(filter, @"([A-Z]+)(\d+)", "$$$1$$$2")}</definedName>");
            }
            sx.Append("<pageMargins left=\"0.5\" right=\"0.5\" top=\"0.6\" bottom=\"0.6\" header=\"0.3\" footer=\"0.3\"/>");
            sx.Append($"<pageSetup orientation=\"{(maxCol > 6 ? "landscape" : "portrait")}\" fitToWidth=\"1\" fitToHeight=\"0\"/>");

            // a chart beside the table
            string? sheetRels = null;
            if (s.Chart != null && chartTable is { } ct)
            {
                charts++;
                sx.Append("<drawing r:id=\"rId1\"/>");
                sheetRels = XmlHead + $"<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/drawing\" Target=\"../drawings/drawing{charts}.xml\"/></Relationships>";
                int fromCol = maxCol + 1;
                parts[$"xl/drawings/drawing{charts}.xml"] = XmlHead
                    + "<xdr:wsDr xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\">"
                    + $"<xdr:twoCellAnchor editAs=\"oneCell\"><xdr:from><xdr:col>{fromCol}</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>{TitleRows}</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:from><xdr:to><xdr:col>{fromCol + 8}</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>{TitleRows + 18}</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:to>"
                    + $"<xdr:graphicFrame macro=\"\"><xdr:nvGraphicFramePr><xdr:cNvPr id=\"2\" name=\"Chart {charts}\"/><xdr:cNvGraphicFramePr/></xdr:nvGraphicFramePr><xdr:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/></xdr:xfrm>"
                    + "<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/chart\"><c:chart r:id=\"rId1\"/></a:graphicData></a:graphic></xdr:graphicFrame><xdr:clientData/></xdr:twoCellAnchor></xdr:wsDr>";
                parts[$"xl/drawings/_rels/drawing{charts}.xml.rels"] = XmlHead
                    + $"<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/chart\" Target=\"../charts/chart{charts}.xml\"/></Relationships>";
                parts[$"xl/charts/chart{charts}.xml"] = ChartXml(s, ct.HeaderRow, ct.FirstData, ct.LastData, ct.NumCols);
                types.Append($"<Override PartName=\"/xl/drawings/drawing{charts}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.drawing+xml\"/>");
                types.Append($"<Override PartName=\"/xl/charts/chart{charts}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.drawingml.chart+xml\"/>");
            }
            sx.Append("</worksheet>");
            parts[$"xl/worksheets/sheet{si + 1}.xml"] = sx.ToString();
            if (sheetRels != null) parts[$"xl/worksheets/_rels/sheet{si + 1}.xml.rels"] = sheetRels;
            types.Append($"<Override PartName=\"/xl/worksheets/sheet{si + 1}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
            bookSheets.Append($"<sheet name=\"{X(s.Name)}\" sheetId=\"{si + 1}\" r:id=\"rId{si + 1}\"/>");
            bookRels.Append($"<Relationship Id=\"rId{si + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{si + 1}.xml\"/>");
        }

        int stylesId = sheets.Count + 1;
        parts["[Content_Types].xml"] = XmlHead + "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>"
            + "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>"
            + "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>"
            + types + CoreType + "</Types>";
        parts["_rels/.rels"] = XmlHead + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" + CoreRel + "</Relationships>";
        parts["docProps/core.xml"] = CoreProps(sheets[0].Title ?? fallbackTitle);
        parts["xl/workbook.xml"] = XmlHead + "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">"
            + "<bookViews><workbookView activeTab=\"0\"/></bookViews>"
            + "<sheets>" + bookSheets + "</sheets>"
            + (defined.Length > 0 ? "<definedNames>" + defined + "</definedNames>" : "")
            + "<calcPr calcId=\"191029\" fullCalcOnLoad=\"1\"/></workbook>";
        parts["xl/_rels/workbook.xml.rels"] = XmlHead + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" + bookRels
            + $"<Relationship Id=\"rId{stylesId}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>";
        parts["xl/styles.xml"] = st.Xml();
        return Zip(parts);
    }

    /// <summary>A chart of a table: the first column as categories, the number columns as series (pie: the first one).</summary>
    static string ChartXml(SheetSpec s, int headerRow, int first, int last, List<int> numCols)
    {
        string sheet = "'" + s.Name.Replace("'", "''") + "'";
        string kind = s.Chart switch { "bar" or "bars" => "bar", "line" or "lines" => "line", "pie" => "pie", "donut" or "doughnut" => "doughnut", "area" => "area", _ => "col" };
        bool round = kind is "pie" or "doughnut";
        var series = round ? numCols.Take(1).ToList() : numCols.Take(6).ToList();
        var sb = new StringBuilder();
        for (int i = 0; i < series.Count; i++)
        {
            int c = series[i];
            string color = SeriesColors[i % SeriesColors.Length];
            sb.Append($"<c:ser><c:idx val=\"{i}\"/><c:order val=\"{i}\"/><c:tx><c:strRef><c:f>{X(sheet)}!${Col(c)}${headerRow}</c:f></c:strRef></c:tx>");
            if (round)
            {
                sb.Append("<c:spPr><a:ln w=\"19050\"><a:solidFill><a:srgbClr val=\"FFFFFF\"/></a:solidFill></a:ln></c:spPr>");
                for (int p = 0; p <= last - first; p++)
                    sb.Append($"<c:dPt><c:idx val=\"{p}\"/><c:bubble3D val=\"0\"/><c:spPr><a:solidFill><a:srgbClr val=\"{SeriesColors[p % SeriesColors.Length]}\"/></a:solidFill><a:ln w=\"19050\"><a:solidFill><a:srgbClr val=\"FFFFFF\"/></a:solidFill></a:ln></c:spPr></c:dPt>");
                sb.Append("<c:dLbls><c:numFmt formatCode=\"0%\" sourceLinked=\"0\"/><c:spPr><a:noFill/><a:ln><a:noFill/></a:ln></c:spPr><c:txPr><a:bodyPr/><a:lstStyle/><a:p><a:pPr><a:defRPr sz=\"1000\" b=\"1\"><a:solidFill><a:srgbClr val=\"FFFFFF\"/></a:solidFill></a:defRPr></a:pPr><a:endParaRPr lang=\"en-US\"/></a:p></c:txPr><c:showLegendKey val=\"0\"/><c:showVal val=\"0\"/><c:showCatName val=\"0\"/><c:showSerName val=\"0\"/><c:showPercent val=\"1\"/><c:showBubbleSize val=\"0\"/><c:showLeaderLines val=\"0\"/></c:dLbls>");
            }
            else if (kind == "line")
                sb.Append($"<c:spPr><a:ln w=\"28575\" cap=\"rnd\"><a:solidFill><a:srgbClr val=\"{color}\"/></a:solidFill><a:round/></a:ln></c:spPr><c:marker><c:symbol val=\"circle\"/><c:size val=\"5\"/><c:spPr><a:solidFill><a:srgbClr val=\"{color}\"/></a:solidFill><a:ln><a:noFill/></a:ln></c:spPr></c:marker>");
            else
                sb.Append($"<c:spPr><a:solidFill><a:srgbClr val=\"{color}\"/></a:solidFill><a:ln><a:noFill/></a:ln></c:spPr>{(kind is "col" or "bar" ? "<c:invertIfNegative val=\"0\"/>" : "")}");
            sb.Append($"<c:cat><c:strRef><c:f>{X(sheet)}!$A${first}:$A${last}</c:f></c:strRef></c:cat>");
            sb.Append($"<c:val><c:numRef><c:f>{X(sheet)}!${Col(c)}${first}:${Col(c)}${last}</c:f></c:numRef></c:val>");
            if (kind == "line") sb.Append("<c:smooth val=\"0\"/>");
            sb.Append("</c:ser>");
        }
        string axisText = $"<c:txPr><a:bodyPr/><a:lstStyle/><a:p><a:pPr><a:defRPr sz=\"900\"><a:solidFill><a:srgbClr val=\"{Muted}\"/></a:solidFill></a:defRPr></a:pPr><a:endParaRPr lang=\"en-US\"/></a:p></c:txPr>";
        string axes = round ? "" :
            $"<c:catAx><c:axId val=\"500000001\"/><c:scaling><c:orientation val=\"{(kind == "bar" ? "maxMin" : "minMax")}\"/></c:scaling><c:delete val=\"0\"/><c:axPos val=\"{(kind == "bar" ? "l" : "b")}\"/><c:numFmt formatCode=\"General\" sourceLinked=\"1\"/><c:majorTickMark val=\"none\"/><c:minorTickMark val=\"none\"/><c:tickLblPos val=\"nextTo\"/><c:spPr><a:ln w=\"9525\"><a:solidFill><a:srgbClr val=\"{Rule}\"/></a:solidFill></a:ln></c:spPr>{axisText}<c:crossAx val=\"500000002\"/><c:crosses val=\"autoZero\"/><c:auto val=\"1\"/><c:lblAlgn val=\"ctr\"/><c:lblOffset val=\"100\"/><c:noMultiLvlLbl val=\"0\"/></c:catAx>"
            + $"<c:valAx><c:axId val=\"500000002\"/><c:scaling><c:orientation val=\"minMax\"/></c:scaling><c:delete val=\"0\"/><c:axPos val=\"{(kind == "bar" ? "t" : "l")}\"/><c:majorGridlines><c:spPr><a:ln w=\"9525\"><a:solidFill><a:srgbClr val=\"EEF1F5\"/></a:solidFill></a:ln></c:spPr></c:majorGridlines><c:numFmt formatCode=\"General\" sourceLinked=\"1\"/><c:majorTickMark val=\"none\"/><c:minorTickMark val=\"none\"/><c:tickLblPos val=\"nextTo\"/><c:spPr><a:ln><a:noFill/></a:ln></c:spPr>{axisText}<c:crossAx val=\"500000001\"/><c:crosses val=\"{(kind == "bar" ? "max" : "autoZero")}\"/><c:crossBetween val=\"between\"/></c:valAx>";
        string group = kind switch
        {
            "col" or "bar" => $"<c:barChart><c:barDir val=\"{(kind == "bar" ? "bar" : "col")}\"/><c:grouping val=\"clustered\"/><c:varyColors val=\"0\"/>{sb}<c:gapWidth val=\"70\"/><c:overlap val=\"-10\"/><c:axId val=\"500000001\"/><c:axId val=\"500000002\"/></c:barChart>",
            "line" => $"<c:lineChart><c:grouping val=\"standard\"/><c:varyColors val=\"0\"/>{sb}<c:marker val=\"1\"/><c:axId val=\"500000001\"/><c:axId val=\"500000002\"/></c:lineChart>",
            "area" => $"<c:areaChart><c:grouping val=\"standard\"/><c:varyColors val=\"0\"/>{sb}<c:axId val=\"500000001\"/><c:axId val=\"500000002\"/></c:areaChart>",
            "doughnut" => $"<c:doughnutChart><c:varyColors val=\"1\"/>{sb}<c:firstSliceAng val=\"0\"/><c:holeSize val=\"55\"/></c:doughnutChart>",
            _ => $"<c:pieChart><c:varyColors val=\"1\"/>{sb}<c:firstSliceAng val=\"0\"/></c:pieChart>",
        };
        string title = string.IsNullOrWhiteSpace(s.ChartTitle) ? (series.Count == 1 ? "" : s.Title ?? "") : s.ChartTitle!;
        string titleXml = title.Length == 0 ? "<c:autoTitleDeleted val=\"1\"/>" :
            $"<c:title><c:tx><c:rich><a:bodyPr/><a:lstStyle/><a:p><a:pPr><a:defRPr sz=\"1300\" b=\"1\"><a:solidFill><a:srgbClr val=\"{Ink}\"/></a:solidFill></a:defRPr></a:pPr><a:r><a:rPr lang=\"en-US\" sz=\"1300\" b=\"1\"><a:solidFill><a:srgbClr val=\"{Ink}\"/></a:solidFill></a:rPr><a:t>{X(title)}</a:t></a:r></a:p></c:rich></c:tx><c:overlay val=\"0\"/></c:title><c:autoTitleDeleted val=\"0\"/>";
        bool legend = round || series.Count > 1;
        return XmlHead
            + "<c:chartSpace xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">"
            + "<c:roundedCorners val=\"0\"/><c:chart>" + titleXml
            + "<c:plotArea><c:layout/>" + group + axes + "<c:spPr><a:noFill/><a:ln><a:noFill/></a:ln></c:spPr></c:plotArea>"
            + (legend ? $"<c:legend><c:legendPos val=\"b\"/><c:overlay val=\"0\"/>{axisText}</c:legend>" : "")
            + "<c:plotVisOnly val=\"1\"/><c:dispBlanksAs val=\"gap\"/></c:chart>"
            + $"<c:spPr><a:solidFill><a:srgbClr val=\"FFFFFF\"/></a:solidFill><a:ln w=\"9525\"><a:solidFill><a:srgbClr val=\"{Rule}\"/></a:solidFill></a:ln></c:spPr>"
            + "</c:chartSpace>";
    }
}
