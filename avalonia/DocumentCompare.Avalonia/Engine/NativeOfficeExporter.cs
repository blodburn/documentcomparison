using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DocumentCompare.Avalonia.Models;

namespace DocumentCompare.Avalonia.Engine;

internal static class NativeOfficeExporter
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public static Task WriteXlsxAsync(ComparisonResultVm result, string outputPath, CancellationToken cancellationToken) =>
        Task.Run(() => WriteXlsx(result, outputPath, cancellationToken), cancellationToken);

    public static async Task WriteTrackedDocxAsync(
        string originalPath, string revisedPath, string outputPath, string author, bool includePunctuation,
        CancellationToken cancellationToken)
    {
        var oldText = await NativeDocumentReader.ReadAsync(originalPath, cancellationToken);
        var newText = await NativeDocumentReader.ReadAsync(revisedPath, cancellationToken);
        await Task.Run(() => WriteTrackedDocx(oldText, newText, revisedPath, outputPath, author, includePunctuation, cancellationToken), cancellationToken);
    }

    private static void WriteXlsx(ComparisonResultVm result, string outputPath, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        Put(zip, "[Content_Types].xml", """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
  <Default Extension="xml" ContentType="application/xml"/>
  <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
  <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
  <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
</Types>
""");
        Put(zip, "_rels/.rels", """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
</Relationships>
""");
        Put(zip, "xl/workbook.xml", """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
  <sheets><sheet name="Comparison" sheetId="1" r:id="rId1"/></sheets>
</workbook>
""");
        Put(zip, "xl/_rels/workbook.xml.rels", """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
  <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
</Relationships>
""");
        Put(zip, "xl/styles.xml", """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
  <fonts count="2"><font><sz val="10"/><name val="Calibri"/></font><font><b/><color rgb="FFFFFFFF"/><sz val="10"/><name val="Calibri"/></font></fonts>
  <fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FF1F4E78"/><bgColor indexed="64"/></patternFill></fill></fills>
  <borders count="1"><border/></borders><cellStyleXfs count="1"><xf/></cellStyleXfs>
  <cellXfs count="2"><xf fontId="0" fillId="0" borderId="0" xfId="0" applyAlignment="1"><alignment vertical="top" wrapText="1"/></xf><xf fontId="1" fillId="2" borderId="0" xfId="0" applyAlignment="1"><alignment horizontal="center" vertical="center" wrapText="1"/></xf></cellXfs>
</styleSheet>
""");

        var xml = new StringBuilder(1024 * 32);
        xml.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><cols>");
        for (var c = 0; c < result.Names.Count; c++) xml.Append($"<col min=\"{c + 1}\" max=\"{c + 1}\" width=\"38\" customWidth=\"1\"/>");
        xml.Append($"<col min=\"{result.Names.Count + 1}\" max=\"{result.Names.Count + 1}\" width=\"58\" customWidth=\"1\"/></cols><sheetData>");
        var rowNumber = 1;
        xml.Append("<row r=\"1\">");
        for (var c = 0; c < result.Names.Count; c++)
            Cell(xml, rowNumber, c, $"{(char)('A' + c)} · {result.Names[c]}{(c == result.BaseIndex ? " · Base" : "")}", 1);
        Cell(xml, rowNumber, result.Names.Count, "Changes", 1);
        xml.Append("</row>");

        foreach (var row in result.Rows)
        {
            token.ThrowIfCancellationRequested(); rowNumber++;
            xml.Append($"<row r=\"{rowNumber}\">");
            for (var c = 0; c < result.Names.Count; c++)
            {
                if (row.Members.Count <= c || row.Members[c] is null)
                {
                    Cell(xml, rowNumber, c, "[No corresponding block]", 0);
                    continue;
                }
                var segments = new List<SegmentVm>();
                if (row.HeaderSegments.Count > c) segments.AddRange(row.HeaderSegments[c]);
                if (row.HeaderSegments.Count > c && row.HeaderSegments[c].Count > 0 &&
                    row.BodySegments.Count > c && row.BodySegments[c].Count > 0)
                    segments.Add(new SegmentVm { Text = "\n", Style = "normal" });
                if (row.BodySegments.Count > c) segments.AddRange(row.BodySegments[c]);
                RichCell(xml, rowNumber, c, segments, 0);
            }
            Cell(xml, rowNumber, result.Names.Count, string.Join("\n", row.DisplayMessages), 0);
            xml.Append("</row>");
        }
        xml.Append($"</sheetData><autoFilter ref=\"A1:{ColumnName(result.Names.Count + 1)}{Math.Max(1, rowNumber)}\"/><sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews></worksheet>");
        Put(zip, "xl/worksheets/sheet1.xml", xml.ToString());
    }

    private static void Cell(StringBuilder xml, int row, int column, string value, int style)
    {
        var cell = ColumnName(column + 1) + row;
        xml.Append($"<c r=\"{cell}\" t=\"inlineStr\" s=\"{style}\"><is><t xml:space=\"preserve\">{Esc(value)}</t></is></c>");
    }

    private static void RichCell(StringBuilder xml, int row, int column, IReadOnlyList<SegmentVm> segments, int style)
    {
        var cell = ColumnName(column + 1) + row;
        xml.Append($"<c r=\"{cell}\" t=\"inlineStr\" s=\"{style}\"><is>");
        if (segments.Count == 0)
        {
            xml.Append("<t></t>");
        }
        else
        {
            foreach (var seg in segments.Where(x => !string.IsNullOrEmpty(x.Text)))
            {
                xml.Append("<r>");
                switch (seg.Style)
                {
                    case "delete":
                        xml.Append("<rPr><color rgb=\"FFC00000\"/><strike/></rPr>");
                        break;
                    case "insert":
                        xml.Append("<rPr><color rgb=\"FF1565C0\"/><u val=\"single\"/></rPr>");
                        break;
                    case "both":
                        xml.Append("<rPr><color rgb=\"FF7A3E9D\"/><strike/><u val=\"single\"/></rPr>");
                        break;
                }
                xml.Append($"<t xml:space=\"preserve\">{Esc(seg.Text)}</t></r>");
            }
        }
        xml.Append("</is></c>");
    }

    private static string ColumnName(int column)
    {
        var s = "";
        while (column > 0) { column--; s = (char)('A' + column % 26) + s; column /= 26; }
        return s;
    }

    private static void WriteTrackedDocx(string oldText, string newText, string revisedPath, string outputPath, string author, bool includePunctuation, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        if (Path.GetExtension(revisedPath).Equals(".docx", StringComparison.OrdinalIgnoreCase))
            File.Copy(revisedPath, outputPath, overwrite: true);
        else
            CreateMinimalDocx(outputPath);

        using var fs = new FileStream(outputPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Update);
        var documentEntry = zip.GetEntry("word/document.xml") ?? throw new InvalidDataException("Word document.xml not found.");
        XDocument document;
        using (var input = documentEntry.Open()) document = XDocument.Load(input, LoadOptions.PreserveWhitespace);
        XNamespace w = W;
        var body = document.Root?.Element(w + "body") ?? throw new InvalidDataException("Word body not found.");
        var section = body.Elements(w + "sectPr").LastOrDefault();
        var revisedParagraphs = body.Elements(w + "p")
            .Select(p => new RevisedParagraph(ParagraphText(p, w), p.Element(w + "pPr") is { } pPr ? new XElement(pPr) : null))
            .Where(x => !string.IsNullOrWhiteSpace(x.Text))
            .ToList();
        body.RemoveNodes();

        var oldLines = oldText.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        var newLines = newText.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        var lineMatches = Lcs(oldLines.Select(NativeComparisonEngine.Normalize).ToArray(), newLines.Select(NativeComparisonEngine.Normalize).ToArray());
        var anchors = new List<(int A, int B)> { (-1, -1) }; anchors.AddRange(lineMatches); anchors.Add((oldLines.Count, newLines.Count));
        var revisionId = 1;
        for (var k = 0; k < anchors.Count - 1; k++)
        {
            token.ThrowIfCancellationRequested();
            var left = anchors[k]; var right = anchors[k + 1];
            var ai = left.A + 1; var aj = right.A; var bi = left.B + 1; var bj = right.B;
            var paired = Math.Min(aj - ai, bj - bi);
            for (var p = 0; p < paired; p++) body.Add(TrackedParagraph(oldLines[ai + p], newLines[bi + p], author, ref revisionId, includePunctuation, PickParagraphProperties(newLines[bi + p], revisedParagraphs)));
            for (var p = ai + paired; p < aj; p++) body.Add(DeletedParagraph(oldLines[p], author, ref revisionId, null));
            for (var p = bi + paired; p < bj; p++) body.Add(InsertedParagraph(newLines[p], author, ref revisionId, PickParagraphProperties(newLines[p], revisedParagraphs)));
            if (right.A < oldLines.Count && right.B < newLines.Count) body.Add(PlainParagraph(newLines[right.B], PickParagraphProperties(newLines[right.B], revisedParagraphs)));
        }
        if (section is not null) body.Add(section);
        ReplaceEntry(zip, "word/document.xml", document.ToString(SaveOptions.DisableFormatting));
        EnsureTrackRevisions(zip);
    }

    private sealed record RevisedParagraph(string Text, XElement? Properties);

    private static XElement? PickParagraphProperties(string text, IReadOnlyList<RevisedParagraph> revised)
    {
        var key = NativeComparisonEngine.Normalize(text);
        var exact = revised.FirstOrDefault(x => NativeComparisonEngine.Normalize(x.Text) == key);
        if (exact?.Properties is not null) return new XElement(exact.Properties);
        if (key.Length >= 16)
        {
            var prefix = key[..Math.Min(24, key.Length)];
            var near = revised.FirstOrDefault(x => NativeComparisonEngine.Normalize(x.Text).Contains(prefix, StringComparison.Ordinal));
            if (near?.Properties is not null) return new XElement(near.Properties);
        }
        return null;
    }

    private static string ParagraphText(XElement paragraph, XNamespace w) =>
        string.Concat(paragraph.Descendants().Where(x => x.Name == w + "t" || x.Name == w + "delText").Select(x => x.Value)).Trim();

    private static XElement PlainParagraph(string text, XElement? properties)
    {
        XNamespace w = W;
        return new XElement(w + "p", properties is null ? null : new XElement(properties), new XElement(w + "r", TextNode(w + "t", text)));
    }

    private static XElement InsertedParagraph(string text, string author, ref int id, XElement? properties)
    {
        XNamespace w = W;
        return new XElement(w + "p", properties is null ? null : new XElement(properties), new XElement(w + "ins", RevisionAttrs(w, id++, author), new XElement(w + "r", TextNode(w + "t", text))));
    }

    private static XElement DeletedParagraph(string text, string author, ref int id, XElement? properties)
    {
        XNamespace w = W;
        return new XElement(w + "p", properties is null ? null : new XElement(properties), new XElement(w + "del", RevisionAttrs(w, id++, author), new XElement(w + "r", TextNode(w + "delText", text))));
    }

    private static XElement TrackedParagraph(string oldText, string newText, string author, ref int id, bool includePunctuation, XElement? properties)
    {
        XNamespace w = W;
        if (NativeComparisonEngine.SemanticEqual(oldText, newText)) return PlainParagraph(newText, properties);
        var p = new XElement(w + "p");
        if (properties is not null) p.Add(new XElement(properties));
        var a = Tokens(oldText); var b = Tokens(newText);
        var matches = Lcs(a.Select(x => Key(x.Text)).ToArray(), b.Select(x => Key(x.Text)).ToArray());
        var anchors = new List<(int A, int B)> { (-1, -1) }; anchors.AddRange(matches); anchors.Add((a.Count, b.Count));
        for (var k = 0; k < anchors.Count - 1; k++)
        {
            var left = anchors[k]; var right = anchors[k + 1];
            var ai = left.A + 1; var aj = right.A; var bi = left.B + 1; var bj = right.B;
            var oldChunk = JoinTokenRange(oldText, a, ai, aj); var newChunk = JoinTokenRange(newText, b, bi, bj);
            var punctuationOnly = (oldChunk.Length == 0 || PunctuationOnly(oldChunk)) &&
                                  (newChunk.Length == 0 || PunctuationOnly(newChunk));
            if (includePunctuation || !punctuationOnly)
            {
                if (oldChunk.Length > 0) p.Add(new XElement(w + "del", RevisionAttrs(w, id++, author), new XElement(w + "r", TextNode(w + "delText", oldChunk))));
                if (newChunk.Length > 0) p.Add(new XElement(w + "ins", RevisionAttrs(w, id++, author), new XElement(w + "r", TextNode(w + "t", newChunk))));
            }
            if (right.A < a.Count && right.B < b.Count) p.Add(new XElement(w + "r", TextNode(w + "t", b[right.B].Text)));
        }
        return p;
    }

    private static XAttribute[] RevisionAttrs(XNamespace w, int id, string author) => new[]
    {
        new XAttribute(w + "id", id), new XAttribute(w + "author", string.IsNullOrWhiteSpace(author) ? "Revised" : author),
        new XAttribute(w + "date", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"))
    };

    private static XElement TextNode(XName name, string text) => new(name, new XAttribute(XNamespace.Xml + "space", "preserve"), text);

    private static void EnsureTrackRevisions(ZipArchive zip)
    {
        XNamespace w = W;
        var entry = zip.GetEntry("word/settings.xml");
        XDocument settings;
        if (entry is null) settings = new XDocument(new XElement(w + "settings"));
        else { using var s = entry.Open(); settings = XDocument.Load(s, LoadOptions.PreserveWhitespace); }
        var root = settings.Root ?? new XElement(w + "settings");
        if (settings.Root is null) settings.Add(root);
        if (root.Element(w + "trackRevisions") is null) root.AddFirst(new XElement(w + "trackRevisions"));
        ReplaceEntry(zip, "word/settings.xml", settings.ToString(SaveOptions.DisableFormatting));
    }

    private static void CreateMinimalDocx(string outputPath)
    {
        using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        Put(zip, "[Content_Types].xml", """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
 <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
 <Default Extension="xml" ContentType="application/xml"/>
 <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
 <Override PartName="/word/settings.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.settings+xml"/>
</Types>
""");
        Put(zip, "_rels/.rels", """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>
""");
        Put(zip, "word/_rels/document.xml.rels", """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/settings" Target="settings.xml"/></Relationships>
""");
        Put(zip, "word/document.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document xmlns:w=\"{W}\" xmlns:r=\"{R}\"><w:body><w:sectPr/></w:body></w:document>");
        Put(zip, "word/settings.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:settings xmlns:w=\"{W}\"><w:trackRevisions/></w:settings>");
    }

    private sealed record SpanToken(int Start, int End, string Text);
    private static List<SpanToken> Tokens(string text) => Regex.Matches(text ?? "", @"[가-힣A-Za-z0-9_]+|\s+|[^가-힣A-Za-z0-9_\s]")
        .Select(m => new SpanToken(m.Index, m.Index + m.Length, m.Value)).ToList();
    private static string Key(string text) => NativeComparisonEngine.Normalize(text);
    private static string JoinTokenRange(string source, IReadOnlyList<SpanToken> tokens, int start, int end)
    {
        if (start >= end) return ""; return source[tokens[start].Start..tokens[end - 1].End];
    }
    private static bool PunctuationOnly(string value) => !string.IsNullOrWhiteSpace(value) && value.Where(x => !char.IsWhiteSpace(x)).All(x => !char.IsLetterOrDigit(x));

    private static List<(int A, int B)> Lcs(string[] a, string[] b)
    {
        var dp = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--) for (var j = b.Length - 1; j >= 0; j--)
            dp[i, j] = a[i] == b[j] ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);
        var r = new List<(int, int)>(); var x = 0; var y = 0;
        while (x < a.Length && y < b.Length) { if (a[x] == b[y]) { r.Add((x++, y++)); } else if (dp[x + 1, y] >= dp[x, y + 1]) x++; else y++; }
        return r;
    }

    private static string Esc(string value) => SecurityElement.Escape(value ?? "") ?? "";
    private static void Put(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)); writer.Write(content);
    }
    private static void ReplaceEntry(ZipArchive zip, string path, string content)
    {
        zip.GetEntry(path)?.Delete(); Put(zip, path, content);
    }
}
