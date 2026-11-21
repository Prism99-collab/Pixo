using System.Text;
using System.Text.RegularExpressions;

namespace DynamicNotch.Services;

/// <summary>
/// PowerPoint decks, 16:9, in the house style. From markdown: the first "# " heading (and the line under it) is the
/// title slide (navy, white title); each "## " heading starts a slide; a later "# " heading is a section slide; "---"
/// also starts a new slide. A slide shows its bullets (nested, bold words kept; two columns when there are many), its
/// text, and a table if it has one (navy header, light bands). Lines starting "Notes:" are left out of the slide (they
/// are for the speaker). Text sizes follow how much there is, so nothing runs off the slide. Every slide but the
/// first carries its number.
/// </summary>
public static partial class DocWriter
{
    sealed class SlideSpec
    {
        public string Title = "";
        public string? Subtitle;
        public bool Cover, Section;
        public readonly List<(string Text, int Level, bool Numbered, int Num)> Items = new();
        public readonly List<string> Text = new();
        public List<string[]>? Table;
        public char[]? Align;
        public bool HasBody => Items.Count > 0 || Text.Count > 0 || Table != null;
    }

    const long SlideW = 12192000, SlideH = 6858000, Emu = 12700; // 13.33 × 7.5 in; points to EMU

    static List<SlideSpec> SlidesFrom(string md, string fallbackTitle)
    {
        var slides = new List<SlideSpec>();
        SlideSpec? cur = null;
        SlideSpec New(string title = "") { cur = new SlideSpec { Title = title }; slides.Add(cur); return cur; }
        foreach (var b in Parse(md))
        {
            switch (b.Kind)
            {
                case Kind.Heading when b.Level == 1 && slides.Count == 0:
                    New(Plain(b.Text)).Cover = true;
                    break;
                case Kind.Heading when b.Level == 1:
                    New(Plain(b.Text)).Section = true;
                    break;
                case Kind.Heading when b.Level == 2:
                    New(Plain(b.Text));
                    break;
                case Kind.Heading:
                    (cur ?? New()).Items.Add(("**" + Plain(b.Text) + "**", 0, false, 0));
                    break;
                case Kind.Rule or Kind.PageBreak:
                    if (cur is { } c && (c.HasBody || c.Cover || c.Section)) cur = null;
                    break;
                case Kind.Paragraph when Regex.IsMatch(b.Text, @"^\s*(speaker\s+)?notes?\s*:", RegexOptions.IgnoreCase):
                    break; // the speaker's notes: not on the slide
                case Kind.Paragraph:
                    if (cur is { Cover: true } cover && cover.Subtitle == null && !cover.HasBody) cover.Subtitle = Plain(b.Text);
                    else if (cur is { Section: true } sec && sec.Subtitle == null && !sec.HasBody) sec.Subtitle = Plain(b.Text);
                    else (cur is { Cover: false } ? cur : New()).Text.Add(b.Text);
                    break;
                case Kind.Quote:
                    (cur is { Cover: false } ? cur : New()).Text.Add("“" + b.Text + "”");
                    break;
                case Kind.Bullet:
                case Kind.Number:
                    (cur is { Cover: false } ? cur : New()).Items.Add((b.Text, Math.Min(2, b.Level), b.Kind == Kind.Number, b.Num));
                    break;
                case Kind.Code:
                    var host = cur is { Cover: false } ? cur : New();
                    foreach (var line in b.Text.Split('\n')) host.Text.Add("`" + line + "`");
                    break;
                case Kind.Table:
                    var t = cur is { Cover: false } ? cur : New();
                    if (t.Table != null) t = New(t.Title);
                    t.Table = b.Rows;
                    t.Align = b.Align;
                    break;
            }
        }
        // a section heading with content under it is a normal slide
        foreach (var s in slides) if (s.Section && s.HasBody) s.Section = false;
        // "Thank you", "Questions?", "Contact": a closing slide, dark like the cover, its line under the title
        foreach (var s in slides)
            if (!s.Cover && s.Table == null && s.Items.Count == 0 && s.Text.Count <= 2 && Regex.IsMatch(s.Title, @"^\s*(thanks?|thank you|merci|questions?|q\s*&\s*a|contact|get in touch|shukran|شكرا)(?!\w)", RegexOptions.IgnoreCase))
            {
                s.Section = true;
                if (s.Text.Count > 0) s.Subtitle = string.Join("   ", s.Text.Select(Plain));
                s.Text.Clear();
            }
        if (slides.Count == 0) slides.Add(new SlideSpec { Title = Regex.Replace(fallbackTitle, @"[_\-]+", " "), Cover = true });
        return slides;
    }

    static byte[] Pptx(string md, string fallbackTitle)
    {
        var slides = SlidesFrom(md, fallbackTitle);
        var parts = new Dictionary<string, string>();
        var ids = new StringBuilder();
        var rels = new StringBuilder(
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideMaster\" Target=\"slideMasters/slideMaster1.xml\"/>"
            + "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/theme\" Target=\"theme/theme1.xml\"/>"
            + "<Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/presProps\" Target=\"presProps.xml\"/>"
            + "<Relationship Id=\"rId4\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/viewProps\" Target=\"viewProps.xml\"/>"
            + "<Relationship Id=\"rId5\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/tableStyles\" Target=\"tableStyles.xml\"/>");
        var types = new StringBuilder();
        for (int i = 0; i < slides.Count; i++)
        {
            parts[$"ppt/slides/slide{i + 1}.xml"] = SlideXml(slides[i], i + 1);
            parts[$"ppt/slides/_rels/slide{i + 1}.xml.rels"] = XmlHead + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout\" Target=\"../slideLayouts/slideLayout1.xml\"/></Relationships>";
            ids.Append($"<p:sldId id=\"{256 + i}\" r:id=\"rId{10 + i}\"/>");
            rels.Append($"<Relationship Id=\"rId{10 + i}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide\" Target=\"slides/slide{i + 1}.xml\"/>");
            types.Append($"<Override PartName=\"/ppt/slides/slide{i + 1}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slide+xml\"/>");
        }
        const string ns = "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"";
        parts["[Content_Types].xml"] = XmlHead + "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>"
            + "<Override PartName=\"/ppt/presentation.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml\"/>"
            + "<Override PartName=\"/ppt/slideMasters/slideMaster1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slideMaster+xml\"/>"
            + "<Override PartName=\"/ppt/slideLayouts/slideLayout1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml\"/>"
            + "<Override PartName=\"/ppt/theme/theme1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.theme+xml\"/>"
            + "<Override PartName=\"/ppt/presProps.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.presProps+xml\"/>"
            + "<Override PartName=\"/ppt/viewProps.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.viewProps+xml\"/>"
            + "<Override PartName=\"/ppt/tableStyles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.tableStyles+xml\"/>"
            + types + CoreType + "</Types>";
        parts["_rels/.rels"] = XmlHead + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"ppt/presentation.xml\"/>" + CoreRel + "</Relationships>";
        parts["docProps/core.xml"] = CoreProps(slides[0].Title.Length > 0 ? slides[0].Title : fallbackTitle);
        parts["ppt/presentation.xml"] = XmlHead + $"<p:presentation {ns} saveSubsetFonts=\"1\"><p:sldMasterIdLst><p:sldMasterId id=\"2147483648\" r:id=\"rId1\"/></p:sldMasterIdLst><p:sldIdLst>{ids}</p:sldIdLst><p:sldSz cx=\"{SlideW}\" cy=\"{SlideH}\"/><p:notesSz cx=\"6858000\" cy=\"9144000\"/><p:defaultTextStyle><a:lvl1pPr><a:defRPr lang=\"en-US\"/></a:lvl1pPr></p:defaultTextStyle></p:presentation>";
        parts["ppt/_rels/presentation.xml.rels"] = XmlHead + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" + rels + "</Relationships>";
        parts["ppt/presProps.xml"] = XmlHead + $"<p:presentationPr {ns}/>";
        parts["ppt/viewProps.xml"] = XmlHead + $"<p:viewPr {ns}><p:normalViewPr><p:restoredLeft sz=\"15620\"/><p:restoredTop sz=\"94660\"/></p:normalViewPr><p:gridSpacing cx=\"76200\" cy=\"76200\"/></p:viewPr>";
        parts["ppt/tableStyles.xml"] = XmlHead + "<a:tblStyleLst xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" def=\"{5C22544A-7EE6-4342-B048-85BDC9FD1C3A}\"/>";
        parts["ppt/slideMasters/slideMaster1.xml"] = XmlHead + $"<p:sldMaster {ns}><p:cSld><p:bg><p:bgRef idx=\"1001\"><a:schemeClr val=\"bg1\"/></p:bgRef></p:bg><p:spTree>{GroupProps}</p:spTree></p:cSld>"
            + "<p:clrMap bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/>"
            + "<p:sldLayoutIdLst><p:sldLayoutId id=\"2147483649\" r:id=\"rId1\"/></p:sldLayoutIdLst>"
            + "<p:txStyles><p:titleStyle><a:lvl1pPr><a:defRPr sz=\"3200\"><a:latin typeface=\"+mj-lt\"/></a:defRPr></a:lvl1pPr></p:titleStyle><p:bodyStyle><a:lvl1pPr><a:defRPr sz=\"2000\"><a:latin typeface=\"+mn-lt\"/></a:defRPr></a:lvl1pPr></p:bodyStyle><p:otherStyle><a:lvl1pPr><a:defRPr sz=\"1800\"><a:latin typeface=\"+mn-lt\"/></a:defRPr></a:lvl1pPr></p:otherStyle></p:txStyles></p:sldMaster>";
        parts["ppt/slideMasters/_rels/slideMaster1.xml.rels"] = XmlHead + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout\" Target=\"../slideLayouts/slideLayout1.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/theme\" Target=\"../theme/theme1.xml\"/></Relationships>";
        parts["ppt/slideLayouts/slideLayout1.xml"] = XmlHead + $"<p:sldLayout {ns} type=\"blank\" preserve=\"1\"><p:cSld name=\"Blank\"><p:spTree>{GroupProps}</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>";
        parts["ppt/slideLayouts/_rels/slideLayout1.xml.rels"] = XmlHead + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideMaster\" Target=\"../slideMasters/slideMaster1.xml\"/></Relationships>";
        parts["ppt/theme/theme1.xml"] = PptTheme;
        return Zip(parts);
    }

    const string GroupProps = "<p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr>";

    // ───────────── one slide ─────────────

    static string SlideXml(SlideSpec s, int number)
    {
        var sp = new StringBuilder();
        int id = 2;
        long In(double inches) => (long)(inches * 914400);
        bool dark = s.Cover || s.Section;

        void Rect(long x, long y, long w, long h, string color)
            => sp.Append($"<p:sp><p:nvSpPr><p:cNvPr id=\"{id++}\" name=\"Shape {id}\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"{w}\" cy=\"{h}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom><a:solidFill><a:srgbClr val=\"{color}\"/></a:solidFill><a:ln><a:noFill/></a:ln></p:spPr></p:sp>");
        void Box(long x, long y, long w, long h, string paras, string anchor = "t", string name = "Text")
            => sp.Append($"<p:sp><p:nvSpPr><p:cNvPr id=\"{id++}\" name=\"{name} {id}\"/><p:cNvSpPr txBox=\"1\"/><p:nvPr/></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"{w}\" cy=\"{h}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom><a:noFill/></p:spPr>"
                + $"<p:txBody><a:bodyPr wrap=\"square\" lIns=\"0\" tIns=\"0\" rIns=\"0\" bIns=\"0\" anchor=\"{anchor}\"><a:normAutofit/></a:bodyPr><a:lstStyle/>{paras}</p:txBody></p:sp>");

        if (s.Cover)
        {
            Rect(In(0.9), In(2.55), In(1.1), In(0.07), Accent3);
            Box(In(0.9), In(2.8), In(11.4), In(1.9), Para(s.Title, s.Title.Length > 60 ? 3600 : 4400, "FFFFFF", bold: true, font: "+mj-lt", lineSpacing: 95), "b", "Title");
            if (s.Subtitle != null) Box(In(0.9), In(4.85), In(11.0), In(1.2), Para(s.Subtitle, 2000, "C9D3E6"), "t", "Subtitle");
        }
        else if (s.Section)
        {
            Box(In(0.9), In(2.6), In(11.4), In(1.6), Para(s.Title, 4000, "FFFFFF", bold: true, font: "+mj-lt"), "b", "Title");
            Rect(In(0.9), In(4.4), In(0.9), In(0.06), Accent3);
            if (s.Subtitle != null) Box(In(0.9), In(4.65), In(11.0), In(1.0), Para(s.Subtitle, 1800, "C9D3E6"), "t", "Subtitle");
        }
        else
        {
            // the title, with a short accent bar beside it
            double titleTop = 0.55;
            if (s.Title.Length > 0)
            {
                Rect(In(0.62), In(titleTop + 0.08), In(0.07), In(0.5), Accent2);
                Box(In(0.9), In(titleTop), In(11.6), In(0.72), Para(s.Title, s.Title.Length > 55 ? 2400 : 2800, Ink, bold: true, font: "+mj-lt"), "ctr", "Title");
            }
            double top = s.Title.Length > 0 ? 1.62 : 0.7, bottom = 6.75, left = 0.9, width = 11.6;
            double textH = s.Table != null ? (s.Items.Count + s.Text.Count > 0 ? Math.Min(1.9, 0.45 * (s.Items.Count + s.Text.Count) + 0.2) : 0) : bottom - top;
            if (s.Items.Count + s.Text.Count > 0)
            {
                int count = s.Items.Count + s.Text.Count;
                var texts = s.Items.Select(i => (Plain(i.Text), i.Level)).Concat(s.Text.Select(t => (Plain(t), 0))).ToList();
                // how tall the text would be at a size, in one column of a width (inches): wrapped lines plus the gaps
                double Tall(double widthIn, int sz, IEnumerable<(string Text, int Level)> list)
                {
                    double pt = sz / 100.0, h = 0;
                    foreach (var (t, lvl) in list)
                    {
                        double perLine = Math.Max(8, (widthIn - 0.4 - lvl * 0.44) * 72 / (pt * 0.52));
                        h += Math.Ceiling(Math.Max(1, t.Length) / perLine) * pt * 1.25 + pt * 0.42;
                    }
                    return h / 72;
                }
                // big and calm when there's little to say: the largest size that fits (fewer points may go larger)
                int[] sizes = count <= 3 ? new[] { 2800, 2400, 2000, 1800, 1600 } : count <= 5 ? new[] { 2400, 2000, 1800, 1600 } : new[] { 2000, 1800, 1600 };
                if (s.Table != null) sizes = new[] { 1800, 1600, 1400 };
                int size = sizes.FirstOrDefault(z => Tall(width, z, texts) <= textH, 0);
                // nothing fits in one column: two columns (points only), at the largest size that fits half
                bool twoCols = size == 0 && s.Table == null && s.Text.Count == 0 && s.Items.Count >= 4;
                if (twoCols) size = new[] { 1800, 1600, 1400 }.FirstOrDefault(z => Tall(width / 2 - 0.25, z, texts) / 2 <= textH, 1400);
                else if (size == 0) size = sizes[^1];
                if (twoCols)
                {
                    int half = (s.Items.Count + 1) / 2;
                    // keep a sub-point with its parent
                    while (half < s.Items.Count && s.Items[half].Level > 0) half++;
                    Box(In(left), In(top), In(width / 2 - 0.25), In(textH), BodyParas(s.Items.Take(half), Enumerable.Empty<string>(), size), "t", "Content");
                    Box(In(left + width / 2 + 0.25), In(top), In(width / 2 - 0.25), In(textH), BodyParas(s.Items.Skip(half), Enumerable.Empty<string>(), size), "t", "Content");
                }
                else Box(In(left), In(top), In(width), In(textH), BodyParas(s.Items, s.Text, size), "t", "Content");
            }
            if (s.Table != null) sp.Append(TableFrame(s.Table, s.Align, In(left), In(top + textH + (textH > 0 ? 0.25 : 0)), In(width), In(bottom - top - textH - 0.3), id++));
            // the slide's number
            Box(In(12.0), In(7.0), In(0.8), In(0.3), $"<a:p><a:pPr algn=\"r\"/><a:fld id=\"{{B6F15528-21DE-4FAA-801E-634DDDAF4B2B}}\" type=\"slidenum\"><a:rPr lang=\"en-US\" sz=\"1000\"><a:solidFill><a:srgbClr val=\"9AA1AC\"/></a:solidFill></a:rPr><a:t>{number}</a:t></a:fld></a:p>", "t", "Slide Number");
        }

        string bg = dark ? $"<p:bg><p:bgPr><a:gradFill rotWithShape=\"1\"><a:gsLst><a:gs pos=\"0\"><a:srgbClr val=\"{Accent}\"/></a:gs><a:gs pos=\"100000\"><a:srgbClr val=\"14213D\"/></a:gs></a:gsLst><a:lin ang=\"2700000\" scaled=\"0\"/></a:gradFill><a:effectLst/></p:bgPr></p:bg>"
            : "<p:bg><p:bgPr><a:solidFill><a:srgbClr val=\"FFFFFF\"/></a:solidFill><a:effectLst/></p:bgPr></p:bg>";
        return XmlHead + "<p:sld xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\">"
            + $"<p:cSld>{bg}<p:spTree>{GroupProps}{sp}</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>";
    }

    /// <summary>Runs of text as DrawingML: bold, italic and code kept, links clickable-looking (blue).</summary>
    static string RunsDml(string text, int size, string color, bool bold = false, string? font = null)
    {
        var sb = new StringBuilder();
        foreach (var r in Runs(text))
        {
            string c = r.Link != null ? Accent2 : color;
            string f = r.Code ? "<a:latin typeface=\"Consolas\"/>" : font != null ? $"<a:latin typeface=\"{font}\"/>" : "";
            sb.Append($"<a:r><a:rPr lang=\"en-US\" sz=\"{size}\"{(r.Bold || bold ? " b=\"1\"" : "")}{(r.Italic ? " i=\"1\"" : "")}{(r.Link != null ? " u=\"sng\"" : "")} dirty=\"0\"><a:solidFill><a:srgbClr val=\"{c}\"/></a:solidFill>{f}</a:rPr><a:t>{X(r.Text)}</a:t></a:r>");
        }
        return sb.ToString();
    }

    static string Para(string text, int size, string color, bool bold = false, string? font = null, int lineSpacing = 100)
        => $"<a:p><a:pPr><a:lnSpc><a:spcPct val=\"{lineSpacing * 1000}\"/></a:lnSpc></a:pPr>{RunsDml(text, size, color, bold, font)}</a:p>";

    static string BodyParas(IEnumerable<(string Text, int Level, bool Numbered, int Num)> items, IEnumerable<string> texts, int size)
    {
        var sb = new StringBuilder();
        foreach (var t in texts)
            sb.Append($"<a:p><a:pPr><a:lnSpc><a:spcPct val=\"112000\"/></a:lnSpc><a:spcAft><a:spcPts val=\"{size / 2}\"/></a:spcAft><a:buNone/></a:pPr>{RunsDml(t, size, "374151")}</a:p>");
        foreach (var (text, level, numbered, num) in items)
        {
            int sz = Math.Max(1200, size - level * 200);
            long marL = 342900 + level * 400000;
            string bullet = numbered && level == 0
                ? $"<a:buClr><a:srgbClr val=\"{Accent2}\"/></a:buClr><a:buFont typeface=\"+mj-lt\"/><a:buAutoNum type=\"arabicPeriod\"{(num > 1 ? $" startAt=\"{num}\"" : "")}/>"
                : $"<a:buClr><a:srgbClr val=\"{(level == 0 ? Accent2 : Accent3)}\"/></a:buClr><a:buFont typeface=\"Arial\"/><a:buChar char=\"{(level == 0 ? "•" : "–")}\"/>";
            bool isHeader = Regex.IsMatch(text, @"^\*\*[^*]+\*\*$") && level == 0 && !numbered;
            if (isHeader)
                sb.Append($"<a:p><a:pPr><a:spcBef><a:spcPts val=\"{size / 2}\"/></a:spcBef><a:spcAft><a:spcPts val=\"200\"/></a:spcAft><a:buNone/></a:pPr>{RunsDml(text, size, Accent)}</a:p>");
            else
                sb.Append($"<a:p><a:pPr marL=\"{marL}\" lvl=\"{level}\" indent=\"-285750\"><a:lnSpc><a:spcPct val=\"108000\"/></a:lnSpc><a:spcBef><a:spcPts val=\"{(level == 0 ? size * 2 / 5 : 300)}\"/></a:spcBef>{bullet}</a:pPr>{RunsDml(text, sz, level == 0 ? Ink : "4B5563")}</a:p>");
        }
        if (sb.Length == 0) sb.Append("<a:p><a:endParaRPr lang=\"en-US\"/></a:p>");
        return sb.ToString();
    }

    static string TableFrame(List<string[]> rows, char[]? align, long x, long y, long w, long maxH, int id)
    {
        int cols = rows.Max(r => r.Length);
        int size = rows.Count > 10 || cols > 6 ? 1200 : rows.Count > 7 || cols > 4 ? 1400 : 1600;
        var want = Enumerable.Range(0, cols).Select(c => Math.Clamp(rows.Max(r => c < r.Length ? Plain(r[c]).Length : 0), 4, 40) + 3.0).ToArray();
        double sum = want.Sum();
        // as wide as the content needs (at least 60% of the slide's width, at most all of it): no lonely gaps
        double natural = want.Sum(v => v * size / 100.0 * 0.56 * Emu + 0.32 * 914400);
        long tw = (long)Math.Clamp(natural, w * 0.6, w);
        var grid = want.Select(v => (long)(tw * v / sum)).ToArray();
        long rowH = Math.Min(maxH / Math.Max(1, rows.Count), (long)(size / 100.0 * Emu * 2.5));
        var numeric = Enumerable.Range(0, cols).Select(c =>
        {
            var vals = rows.Skip(1).Select(r => c < r.Length ? Plain(r[c]).Trim() : "").Where(v => v.Length > 0).ToList();
            return vals.Count > 0 && vals.All(LooksNumeric);
        }).ToArray();
        string Ln(string side, string color) => $"<a:{side} w=\"6350\"><a:solidFill><a:srgbClr val=\"{color}\"/></a:solidFill></a:{side}>";
        var sb = new StringBuilder();
        sb.Append($"<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"{id}\" name=\"Table {id}\"/><p:cNvGraphicFramePr><a:graphicFrameLocks noGrp=\"1\"/></p:cNvGraphicFramePr><p:nvPr/></p:nvGraphicFramePr>");
        sb.Append($"<p:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"{grid.Sum()}\" cy=\"{rowH * rows.Count}\"/></p:xfrm>");
        sb.Append("<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/table\"><a:tbl><a:tblPr firstRow=\"1\" bandRow=\"1\"/><a:tblGrid>");
        foreach (var g in grid) sb.Append($"<a:gridCol w=\"{g}\"/>");
        sb.Append("</a:tblGrid>");
        for (int r = 0; r < rows.Count; r++)
        {
            bool head = r == 0, total = r > 0 && IsTotalRow(rows[r]);
            string fill = head ? Accent : total ? "E3E9F3" : r % 2 == 0 ? Band : "FFFFFF";
            sb.Append($"<a:tr h=\"{rowH}\">");
            for (int c = 0; c < cols; c++)
            {
                string cell = c < rows[r].Length ? rows[r][c] : "";
                char a = align != null && c < align.Length && align[c] != 'l' ? align[c] : numeric[c] && !head ? 'r' : numeric[c] ? 'r' : 'l';
                string algn = a == 'r' ? " algn=\"r\"" : a == 'c' ? " algn=\"ctr\"" : "";
                string runs = Plain(cell).Length == 0 ? $"<a:endParaRPr lang=\"en-US\" sz=\"{size}\"/>" : RunsDml(cell, size, head ? "FFFFFF" : Ink, bold: head || total);
                sb.Append($"<a:tc><a:txBody><a:bodyPr/><a:lstStyle/><a:p><a:pPr{algn}/>{runs}</a:p></a:txBody>"
                    + $"<a:tcPr marL=\"91440\" marR=\"91440\" marT=\"45720\" marB=\"45720\" anchor=\"ctr\">{Ln("lnL", head ? Accent : "FFFFFF")}{Ln("lnR", head ? Accent : "FFFFFF")}{Ln("lnT", total ? Accent2 : Rule)}{Ln("lnB", Rule)}<a:solidFill><a:srgbClr val=\"{fill}\"/></a:solidFill></a:tcPr></a:tc>");
            }
            sb.Append("</a:tr>");
        }
        sb.Append("</a:tbl></a:graphicData></a:graphic></p:graphicFrame>");
        return sb.ToString();
    }

    static readonly string PptTheme = XmlHead
        + "<a:theme xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" name=\"Pixo\"><a:themeElements>"
        + $"<a:clrScheme name=\"Pixo\"><a:dk1><a:srgbClr val=\"{Ink}\"/></a:dk1><a:lt1><a:srgbClr val=\"FFFFFF\"/></a:lt1><a:dk2><a:srgbClr val=\"{Accent}\"/></a:dk2><a:lt2><a:srgbClr val=\"EEF2F8\"/></a:lt2>"
        + $"<a:accent1><a:srgbClr val=\"{Accent2}\"/></a:accent1><a:accent2><a:srgbClr val=\"ED7D31\"/></a:accent2><a:accent3><a:srgbClr val=\"70AD47\"/></a:accent3><a:accent4><a:srgbClr val=\"FFC000\"/></a:accent4><a:accent5><a:srgbClr val=\"{Accent3}\"/></a:accent5><a:accent6><a:srgbClr val=\"7030A0\"/></a:accent6>"
        + $"<a:hlink><a:srgbClr val=\"{Accent2}\"/></a:hlink><a:folHlink><a:srgbClr val=\"7030A0\"/></a:folHlink></a:clrScheme>"
        + "<a:fontScheme name=\"Pixo\"><a:majorFont><a:latin typeface=\"Segoe UI Semibold\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:majorFont><a:minorFont><a:latin typeface=\"Segoe UI\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:minorFont></a:fontScheme>"
        + "<a:fmtScheme name=\"Pixo\"><a:fillStyleLst><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:fillStyleLst>"
        + "<a:lnStyleLst><a:ln w=\"6350\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln><a:ln w=\"12700\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln><a:ln w=\"19050\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln></a:lnStyleLst>"
        + "<a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst>"
        + "<a:bgFillStyleLst><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:bgFillStyleLst></a:fmtScheme>"
        + "</a:themeElements><a:objectDefaults/><a:extraClrSchemeLst/></a:theme>";
}
