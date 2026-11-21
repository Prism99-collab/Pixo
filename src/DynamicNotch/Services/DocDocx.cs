using System.Text;
using System.Text.RegularExpressions;

namespace DynamicNotch.Services;

/// <summary>
/// Word documents: the first "# " heading is the document's title (Title style, a line under it), the others real
/// Heading 1-3 styles (the navigation pane and a table of contents work); real bullet and numbered lists (Word's own
/// numbering, nested up to three levels, each numbered list counting from its own start); tables in a clean style (a
/// navy header that repeats on every page, light bands, numbers right-aligned, a bold total row); clickable links;
/// quotes with a bar; code in a grey box; page breaks; "Page X of Y" in the footer. Calibri, A4, 2.3 cm margins.
/// </summary>
public static partial class DocWriter
{
    static byte[] Docx(string md, string fallbackTitle)
    {
        var blocks = Parse(md);
        var body = new StringBuilder();
        var links = new List<string>();
        var nums = new List<int>(); // the start of each numbered list (a w:num each)
        bool titleDone = false;
        int listNum = 0;
        Kind? prev = null;

        string RunsXml(string text, bool forceBold = false, string? size = null)
        {
            var sb = new StringBuilder();
            foreach (var run in Runs(text))
            {
                var rpr = new StringBuilder();
                if (run.Link != null) rpr.Append("<w:rStyle w:val=\"Hyperlink\"/>");
                if (run.Code) rpr.Append("<w:rFonts w:ascii=\"Consolas\" w:hAnsi=\"Consolas\" w:cs=\"Consolas\"/>");
                if (run.Bold || forceBold) rpr.Append("<w:b/><w:bCs/>");
                if (run.Italic) rpr.Append("<w:i/><w:iCs/>");
                if (run.Code) rpr.Append("<w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"F1F3F6\"/>");
                if (size != null) rpr.Append($"<w:sz w:val=\"{size}\"/><w:szCs w:val=\"{size}\"/>");
                string r = $"<w:r>{(rpr.Length > 0 ? $"<w:rPr>{rpr}</w:rPr>" : "")}{TextXml(run.Text)}</w:r>";
                if (run.Link != null)
                {
                    links.Add(run.Link);
                    sb.Append($"<w:hyperlink r:id=\"rIdL{links.Count}\" w:history=\"1\">{r}</w:hyperlink>");
                }
                else sb.Append(r);
            }
            return sb.ToString();
        }

        string P(string style, string inner, string extra = "") => $"<w:p><w:pPr><w:pStyle w:val=\"{style}\"/>{extra}</w:pPr>{inner}</w:p>";

        foreach (var b in blocks)
        {
            switch (b.Kind)
            {
                case Kind.Heading when b.Level == 1 && !titleDone && prev == null:
                    body.Append(P("Title", RunsXml(b.Text)));
                    titleDone = true;
                    break;
                case Kind.Heading:
                    int level = Math.Clamp(b.Level - (titleDone ? 1 : 0), 1, 3);
                    if (b.Level == 1 && titleDone) level = 1;
                    body.Append(P("Heading" + level, RunsXml(b.Text)));
                    break;
                case Kind.Paragraph:
                    // a short line right under the title reads as its subtitle
                    if (prev == Kind.Heading && titleDone && body.ToString().EndsWith("</w:p>") && CountParagraphs(body) == 1 && b.Text.Length < 140 && !b.Text.Contains('\n'))
                        body.Append(P("Subtitle", RunsXml(b.Text)));
                    else
                        body.Append(P("Normal", string.Join("<w:r><w:br/></w:r>", b.Text.Split('\n').Select(l => RunsXml(l)))));
                    break;
                case Kind.Bullet:
                    body.Append(P("ListParagraph", RunsXml(b.Text), $"<w:numPr><w:ilvl w:val=\"{Math.Min(2, b.Level)}\"/><w:numId w:val=\"1\"/></w:numPr>"));
                    break;
                case Kind.Number:
                    // a new list (not continuing one, nor a sub-list inside bullets) counts from its own start
                    if (prev != Kind.Number && !(prev == Kind.Bullet && b.Level > 0) || listNum == 0) { nums.Add(Math.Max(1, b.Num)); listNum = nums.Count + 1; }
                    body.Append(P("ListParagraph", RunsXml(b.Text), $"<w:numPr><w:ilvl w:val=\"{Math.Min(2, b.Level)}\"/><w:numId w:val=\"{listNum}\"/></w:numPr>"));
                    break;
                case Kind.Quote:
                    body.Append(P("Quote", RunsXml(b.Text)));
                    break;
                case Kind.Code:
                    var lines = b.Text.Split('\n');
                    for (int i = 0; i < lines.Length; i++)
                        body.Append(P("Code", $"<w:r>{TextXml(lines[i].Length == 0 ? " " : lines[i])}</w:r>", i == lines.Length - 1 ? "<w:spacing w:after=\"200\"/>" : ""));
                    break;
                case Kind.Rule:
                    body.Append("<w:p><w:pPr><w:pBdr><w:bottom w:val=\"single\" w:sz=\"6\" w:space=\"1\" w:color=\"" + Rule + "\"/></w:pBdr><w:spacing w:after=\"200\"/></w:pPr></w:p>");
                    break;
                case Kind.PageBreak:
                    body.Append("<w:p><w:r><w:br w:type=\"page\"/></w:r></w:p>");
                    break;
                case Kind.Table when b.Rows is { Count: > 0 } rows:
                    body.Append(TableXml(rows, b.Align, (t, bold) => RunsXml(t, bold)));
                    body.Append("<w:p><w:pPr><w:spacing w:after=\"120\"/></w:pPr></w:p>");
                    break;
            }
            prev = b.Kind;
        }
        if (body.Length == 0) body.Append("<w:p/>");

        string document = XmlHead
            + "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><w:body>"
            + body
            + "<w:sectPr><w:footerReference w:type=\"default\" r:id=\"rIdFooter\"/><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1304\" w:right=\"1304\" w:bottom=\"1304\" w:left=\"1304\" w:header=\"624\" w:footer=\"567\" w:gutter=\"0\"/></w:sectPr></w:body></w:document>";

        var rels = new StringBuilder(XmlHead + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
            + "<Relationship Id=\"rIdStyles\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>"
            + "<Relationship Id=\"rIdNumbering\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/numbering\" Target=\"numbering.xml\"/>"
            + "<Relationship Id=\"rIdSettings\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/settings\" Target=\"settings.xml\"/>"
            + "<Relationship Id=\"rIdFooter\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/footer\" Target=\"footer1.xml\"/>");
        for (int i = 0; i < links.Count; i++)
            rels.Append($"<Relationship Id=\"rIdL{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink\" Target=\"{X(links[i])}\" TargetMode=\"External\"/>");
        rels.Append("</Relationships>");

        return Zip(new()
        {
            ["[Content_Types].xml"] = XmlHead + "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>"
                + "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>"
                + "<Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/>"
                + "<Override PartName=\"/word/numbering.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml\"/>"
                + "<Override PartName=\"/word/settings.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.settings+xml\"/>"
                + "<Override PartName=\"/word/footer1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.footer+xml\"/>"
                + CoreType + "</Types>",
            ["_rels/.rels"] = XmlHead + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" + CoreRel + "</Relationships>",
            ["docProps/core.xml"] = CoreProps(TitleOf(blocks, fallbackTitle)),
            ["word/_rels/document.xml.rels"] = rels.ToString(),
            ["word/document.xml"] = document,
            ["word/styles.xml"] = DocxStyles,
            ["word/numbering.xml"] = DocxNumbering(nums),
            ["word/settings.xml"] = XmlHead + "<w:settings xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:defaultTabStop w:val=\"720\"/><w:characterSpacingControl w:val=\"doNotCompress\"/><w:compat><w:compatSetting w:name=\"compatibilityMode\" w:uri=\"http://schemas.microsoft.com/office/word\" w:val=\"15\"/></w:compat></w:settings>",
            ["word/footer1.xml"] = DocxFooter,
        });
    }

    static int CountParagraphs(StringBuilder sb) => Regex.Matches(sb.ToString(), "<w:p>").Count;

    /// <summary>Text with tabs as real tabs.</summary>
    static string TextXml(string t)
    {
        var parts = t.Split('\t');
        return string.Join("<w:tab/>", parts.Select(p => $"<w:t xml:space=\"preserve\">{X(p)}</w:t>"));
    }

    static string TableXml(List<string[]> rows, char[]? align, Func<string, bool, string> runs)
    {
        int cols = rows.Max(r => r.Length);
        // columns as wide as their content asks (within reason), on the page's 9298 twips
        var want = Enumerable.Range(0, cols).Select(c => Math.Clamp(rows.Max(r => c < r.Length ? Plain(r[c]).Length : 0), 4, 40) + 2.0).ToArray();
        double sum = want.Sum();
        var grid = want.Select(w => (int)(9298 * w / sum)).ToArray();
        var numeric = Enumerable.Range(0, cols).Select(c => rows.Skip(1).Where(r => c < r.Length && Plain(r[c]).Trim().Length > 0).DefaultIfEmpty().All(r => r != null && LooksNumeric(r[c]))
            && rows.Skip(1).Any(r => c < r.Length && Plain(r[c]).Trim().Length > 0)).ToArray();
        var sb = new StringBuilder("<w:tbl><w:tblPr><w:tblStyle w:val=\"PixoTable\"/><w:tblW w:w=\"5000\" w:type=\"pct\"/><w:tblLayout w:type=\"autofit\"/><w:tblLook w:val=\"04A0\" w:firstRow=\"1\" w:lastRow=\"0\" w:firstColumn=\"0\" w:lastColumn=\"0\" w:noHBand=\"0\" w:noVBand=\"1\"/></w:tblPr><w:tblGrid>");
        foreach (var g in grid) sb.Append($"<w:gridCol w:w=\"{g}\"/>");
        sb.Append("</w:tblGrid>");
        for (int r = 0; r < rows.Count; r++)
        {
            bool total = r > 0 && IsTotalRow(rows[r]);
            sb.Append("<w:tr>").Append(r == 0 ? "<w:trPr><w:tblHeader/><w:cantSplit/></w:trPr>" : "<w:trPr><w:cantSplit/></w:trPr>");
            for (int c = 0; c < cols; c++)
            {
                string cell = c < rows[r].Length ? rows[r][c] : "";
                char a = align != null && c < align.Length && align[c] != 'l' ? align[c] : numeric[c] ? 'r' : 'l';
                string jc = a == 'r' ? "<w:jc w:val=\"right\"/>" : a == 'c' ? "<w:jc w:val=\"center\"/>" : "";
                string tcPr = $"<w:tcW w:w=\"{grid[c]}\" w:type=\"dxa\"/>" + (total ? $"<w:tcBorders><w:top w:val=\"single\" w:sz=\"12\" w:space=\"0\" w:color=\"{Accent2}\"/></w:tcBorders><w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"E3E9F3\"/>" : "");
                sb.Append($"<w:tc><w:tcPr>{tcPr}<w:vAlign w:val=\"center\"/></w:tcPr><w:p><w:pPr><w:spacing w:before=\"0\" w:after=\"0\"/>{jc}</w:pPr>{runs(cell, total)}</w:p></w:tc>");
            }
            sb.Append("</w:tr>");
        }
        sb.Append("</w:tbl>");
        return sb.ToString();
    }

    static string DocxNumbering(List<int> starts)
    {
        string Lvl(int i, string fmt, string text, int left, string? font = null)
            => $"<w:lvl w:ilvl=\"{i}\"><w:start w:val=\"1\"/><w:numFmt w:val=\"{fmt}\"/><w:lvlText w:val=\"{text}\"/><w:lvlJc w:val=\"left\"/><w:pPr><w:ind w:left=\"{left}\" w:hanging=\"360\"/></w:pPr>"
               + $"<w:rPr>{(font != null ? $"<w:rFonts w:ascii=\"{font}\" w:hAnsi=\"{font}\" w:hint=\"default\"/>" : "")}<w:color w:val=\"{Accent2}\"/></w:rPr></w:lvl>";
        var sb = new StringBuilder(XmlHead + "<w:numbering xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">");
        sb.Append("<w:abstractNum w:abstractNumId=\"0\"><w:multiLevelType w:val=\"hybridMultilevel\"/>")
          .Append(Lvl(0, "bullet", "•", 720, "Calibri")).Append(Lvl(1, "bullet", "◦", 1440, "Calibri")).Append(Lvl(2, "bullet", "▪", 2160, "Calibri"))
          .Append("</w:abstractNum>");
        sb.Append("<w:abstractNum w:abstractNumId=\"1\"><w:multiLevelType w:val=\"hybridMultilevel\"/>")
          .Append(Lvl(0, "decimal", "%1.", 720)).Append(Lvl(1, "lowerLetter", "%2.", 1440)).Append(Lvl(2, "lowerRoman", "%3.", 2160))
          .Append("</w:abstractNum>");
        sb.Append("<w:num w:numId=\"1\"><w:abstractNumId w:val=\"0\"/></w:num>");
        for (int i = 0; i < starts.Count; i++)
            sb.Append($"<w:num w:numId=\"{i + 2}\"><w:abstractNumId w:val=\"1\"/><w:lvlOverride w:ilvl=\"0\"><w:startOverride w:val=\"{starts[i]}\"/></w:lvlOverride></w:num>");
        sb.Append("</w:numbering>");
        return sb.ToString();
    }

    static readonly string DocxFooter = XmlHead
        + "<w:ftr xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><w:p><w:pPr><w:spacing w:after=\"0\"/><w:jc w:val=\"center\"/></w:pPr>"
        + Field("PAGE", "1") + "<w:r><w:rPr><w:color w:val=\"8A8F98\"/><w:sz w:val=\"18\"/></w:rPr><w:t xml:space=\"preserve\"> / </w:t></w:r>" + Field("NUMPAGES", "1")
        + "</w:p></w:ftr>";

    static string Field(string code, string shown)
    {
        const string rpr = "<w:rPr><w:color w:val=\"8A8F98\"/><w:sz w:val=\"18\"/></w:rPr>";
        return $"<w:r>{rpr}<w:fldChar w:fldCharType=\"begin\"/></w:r><w:r>{rpr}<w:instrText xml:space=\"preserve\"> {code} </w:instrText></w:r><w:r>{rpr}<w:fldChar w:fldCharType=\"separate\"/></w:r><w:r>{rpr}<w:t>{shown}</w:t></w:r><w:r>{rpr}<w:fldChar w:fldCharType=\"end\"/></w:r>";
    }

    static readonly string DocxStyles = XmlHead
        + "<w:styles xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">"
        + "<w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii=\"Calibri\" w:hAnsi=\"Calibri\" w:eastAsia=\"Calibri\" w:cs=\"Calibri\"/>"
        + $"<w:color w:val=\"{Ink}\"/><w:sz w:val=\"22\"/><w:szCs w:val=\"22\"/><w:lang w:val=\"en-US\" w:eastAsia=\"en-US\" w:bidi=\"ar-SA\"/></w:rPr></w:rPrDefault>"
        + "<w:pPrDefault><w:pPr><w:spacing w:after=\"140\" w:line=\"288\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault></w:docDefaults>"
        + "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/><w:qFormat/></w:style>"
        + "<w:style w:type=\"character\" w:default=\"1\" w:styleId=\"DefaultParagraphFont\"><w:name w:val=\"Default Paragraph Font\"/><w:uiPriority w:val=\"1\"/><w:semiHidden/><w:unhideWhenUsed/></w:style>"
        + $"<w:style w:type=\"paragraph\" w:styleId=\"Title\"><w:name w:val=\"Title\"/><w:basedOn w:val=\"Normal\"/><w:next w:val=\"Normal\"/><w:qFormat/><w:pPr><w:pBdr><w:bottom w:val=\"single\" w:sz=\"12\" w:space=\"8\" w:color=\"{Accent2}\"/></w:pBdr><w:spacing w:before=\"0\" w:after=\"240\" w:line=\"240\" w:lineRule=\"auto\"/><w:contextualSpacing/></w:pPr><w:rPr><w:rFonts w:ascii=\"Calibri Light\" w:hAnsi=\"Calibri Light\" w:cs=\"Calibri Light\"/><w:color w:val=\"{Accent}\"/><w:kern w:val=\"28\"/><w:sz w:val=\"52\"/><w:szCs w:val=\"52\"/></w:rPr></w:style>"
        + $"<w:style w:type=\"paragraph\" w:styleId=\"Subtitle\"><w:name w:val=\"Subtitle\"/><w:basedOn w:val=\"Normal\"/><w:next w:val=\"Normal\"/><w:qFormat/><w:pPr><w:spacing w:before=\"0\" w:after=\"280\"/></w:pPr><w:rPr><w:color w:val=\"{Muted}\"/><w:sz w:val=\"26\"/><w:szCs w:val=\"26\"/></w:rPr></w:style>"
        + Heading(1, 32, Accent, 360, 120)
        + Heading(2, 26, Accent2, 280, 100)
        + Heading(3, 23, "404040", 220, 80)
        + "<w:style w:type=\"paragraph\" w:styleId=\"ListParagraph\"><w:name w:val=\"List Paragraph\"/><w:basedOn w:val=\"Normal\"/><w:qFormat/><w:pPr><w:spacing w:after=\"60\"/><w:ind w:left=\"720\"/><w:contextualSpacing/></w:pPr></w:style>"
        + $"<w:style w:type=\"paragraph\" w:styleId=\"Quote\"><w:name w:val=\"Quote\"/><w:basedOn w:val=\"Normal\"/><w:next w:val=\"Normal\"/><w:qFormat/><w:pPr><w:pBdr><w:left w:val=\"single\" w:sz=\"18\" w:space=\"10\" w:color=\"{Accent3}\"/></w:pBdr><w:spacing w:before=\"120\" w:after=\"200\"/><w:ind w:left=\"360\" w:right=\"360\"/></w:pPr><w:rPr><w:i/><w:iCs/><w:color w:val=\"4B5563\"/></w:rPr></w:style>"
        + "<w:style w:type=\"paragraph\" w:styleId=\"Code\"><w:name w:val=\"Code\"/><w:basedOn w:val=\"Normal\"/><w:qFormat/><w:pPr><w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"F4F5F7\"/><w:spacing w:after=\"0\" w:line=\"240\" w:lineRule=\"auto\"/><w:ind w:left=\"120\" w:right=\"120\"/></w:pPr><w:rPr><w:rFonts w:ascii=\"Consolas\" w:hAnsi=\"Consolas\" w:cs=\"Consolas\"/><w:color w:val=\"24292F\"/><w:sz w:val=\"19\"/><w:szCs w:val=\"19\"/></w:rPr></w:style>"
        + $"<w:style w:type=\"character\" w:styleId=\"Hyperlink\"><w:name w:val=\"Hyperlink\"/><w:basedOn w:val=\"DefaultParagraphFont\"/><w:uiPriority w:val=\"99\"/><w:unhideWhenUsed/><w:rPr><w:color w:val=\"{Accent2}\"/><w:u w:val=\"single\"/></w:rPr></w:style>"
        + "<w:style w:type=\"table\" w:default=\"1\" w:styleId=\"TableNormal\"><w:name w:val=\"Normal Table\"/><w:uiPriority w:val=\"99\"/><w:semiHidden/><w:unhideWhenUsed/><w:tblPr><w:tblInd w:w=\"0\" w:type=\"dxa\"/><w:tblCellMar><w:top w:w=\"0\" w:type=\"dxa\"/><w:left w:w=\"108\" w:type=\"dxa\"/><w:bottom w:w=\"0\" w:type=\"dxa\"/><w:right w:w=\"108\" w:type=\"dxa\"/></w:tblCellMar></w:tblPr></w:style>"
        + "<w:style w:type=\"table\" w:styleId=\"PixoTable\"><w:name w:val=\"Pixo Table\"/><w:basedOn w:val=\"TableNormal\"/><w:uiPriority w:val=\"59\"/>"
        + "<w:pPr><w:spacing w:after=\"0\" w:line=\"240\" w:lineRule=\"auto\"/></w:pPr><w:rPr><w:sz w:val=\"20\"/><w:szCs w:val=\"20\"/></w:rPr>"
        + $"<w:tblPr><w:tblStyleRowBandSize w:val=\"1\"/><w:tblBorders><w:top w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"{Rule}\"/><w:left w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"{Rule}\"/><w:bottom w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"{Rule}\"/><w:right w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"{Rule}\"/><w:insideH w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"{Rule}\"/><w:insideV w:val=\"nil\"/></w:tblBorders>"
        + "<w:tblCellMar><w:top w:w=\"70\" w:type=\"dxa\"/><w:left w:w=\"115\" w:type=\"dxa\"/><w:bottom w:w=\"70\" w:type=\"dxa\"/><w:right w:w=\"115\" w:type=\"dxa\"/></w:tblCellMar></w:tblPr>"
        + $"<w:tblStylePr w:type=\"firstRow\"><w:rPr><w:b/><w:bCs/><w:color w:val=\"FFFFFF\"/></w:rPr><w:tcPr><w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"{Accent}\"/></w:tcPr></w:tblStylePr>"
        + $"<w:tblStylePr w:type=\"band1Horz\"><w:tcPr><w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"{Band}\"/></w:tcPr></w:tblStylePr>"
        + "</w:style>"
        + "</w:styles>";

    static string Heading(int n, int size, string color, int before, int after) =>
        $"<w:style w:type=\"paragraph\" w:styleId=\"Heading{n}\"><w:name w:val=\"heading {n}\"/><w:basedOn w:val=\"Normal\"/><w:next w:val=\"Normal\"/><w:uiPriority w:val=\"9\"/><w:qFormat/>"
        + $"<w:pPr><w:keepNext/><w:keepLines/><w:spacing w:before=\"{before}\" w:after=\"{after}\" w:line=\"259\" w:lineRule=\"auto\"/><w:outlineLvl w:val=\"{n - 1}\"/></w:pPr>"
        + $"<w:rPr><w:b/><w:bCs/><w:color w:val=\"{color}\"/><w:sz w:val=\"{size}\"/><w:szCs w:val=\"{size}\"/></w:rPr></w:style>";
}
