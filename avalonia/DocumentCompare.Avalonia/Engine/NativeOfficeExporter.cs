using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DocumentCompare.Avalonia.Models;

namespace DocumentCompare.Avalonia.Engine;

internal static class NativeOfficeExporter
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly Regex ExportTokenRegex = new(
        @"[가-힣A-Za-z0-9_]+|\s+|[^가-힣A-Za-z0-9_\s]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static Task WriteXlsxAsync(ComparisonResultVm result, string outputPath, CancellationToken cancellationToken) =>
        Task.Run(() => WriteXlsx(result, outputPath, cancellationToken), cancellationToken);

    public static async Task WriteTrackedDocxAsync(
        string originalPath, string revisedPath, string outputPath, string author, bool includePunctuation,
        CancellationToken cancellationToken)
    {
        var oldText = await NativeDocumentReader.ReadAsync(originalPath, cancellationToken);
        var newText = await NativeDocumentReader.ReadAsync(revisedPath, cancellationToken);
        await Task.Run(() => WriteTrackedDocx(oldText, newText, originalPath, revisedPath, outputPath, author, includePunctuation, cancellationToken), cancellationToken);
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
                        xml.Append("<rPr><strike/><color rgb=\"FFC00000\"/></rPr>");
                        break;
                    case "insert":
                        xml.Append("<rPr><u val=\"single\"/><color rgb=\"FF1565C0\"/></rPr>");
                        break;
                    case "both":
                        xml.Append("<rPr><strike/><u val=\"single\"/><color rgb=\"FF7A3E9D\"/></rPr>");
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

    private static void WriteTrackedDocx(string oldText, string newText, string originalPath, string revisedPath, string outputPath, string author, bool includePunctuation, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        var revisedIsDocx = Path.GetExtension(revisedPath).Equals(".docx", StringComparison.OrdinalIgnoreCase);
        if (revisedIsDocx)
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

        if (!revisedIsDocx)
        {
            WriteFlatTrackedBody(body, oldText, newText, author, includePunctuation, token);
            ReplaceEntry(zip, "word/document.xml", document.ToString(SaveOptions.DisableFormatting));
            EnsureTrackRevisions(zip);
            return;
        }

        // Empty Word paragraphs are formatting/layout, not comparison anchors.  Keep them in B's
        // XML untouched, but exclude them from paragraph LCS so repeated "" keys cannot pull
        // unrelated legal clauses together.
        var revisedParagraphs = BodyParagraphEntries(body, w)
            .Where(x => !string.IsNullOrWhiteSpace(x.Text))
            .ToList();
        List<ParagraphSource> originalParagraphs;
        if (Path.GetExtension(originalPath).Equals(".docx", StringComparison.OrdinalIgnoreCase))
            originalParagraphs = ReadDocxParagraphSourcesFromPath(originalPath, w) ??
                                 oldText.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0)
                                     .Select(x => new ParagraphSource(x, "body")).ToList();
        else
            originalParagraphs = oldText.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0)
                .Select(x => new ParagraphSource(x, "body")).ToList();

        var oldKeys = originalParagraphs.Select(x => NativeComparisonEngine.Normalize(x.Text)).ToArray();
        var newKeys = revisedParagraphs.Select(x => NativeComparisonEngine.Normalize(x.Text)).ToArray();
        var paragraphMatches = Lcs(oldKeys, newKeys);
        var anchors = new List<(int A, int B)> { (-1, -1) };
        anchors.AddRange(paragraphMatches);
        anchors.Add((originalParagraphs.Count, revisedParagraphs.Count));

        var revisionId = NextRevisionId(document, w);
        for (var k = 0; k < anchors.Count - 1; k++)
        {
            token.ThrowIfCancellationRequested();
            var left = anchors[k]; var right = anchors[k + 1];
            var ai = left.A + 1; var aj = right.A; var bi = left.B + 1; var bj = right.B;
            var paired = Math.Min(aj - ai, bj - bi);

            for (var p = 0; p < paired; p++)
            {
                var oldIndex = ai + p; var newIndex = bi + p;
                ApplyTrackedTextDiff(revisedParagraphs[newIndex].Paragraph, originalParagraphs[oldIndex].Text, revisedParagraphs[newIndex].Text,
                    author, ref revisionId, includePunctuation, w);
            }

            // A-only paragraphs: A contributes text+logical position only. Insert the whole range
            // in source order. At end-of-document a moving cursor prevents AddAfterSelf() from
            // reversing multiple deleted paragraphs.
            if (ai + paired < aj)
            {
                var deletedRange = originalParagraphs.GetRange(ai + paired, aj - (ai + paired));
                InsertDeletedParagraphRange(body, revisedParagraphs, bi + paired, deletedRange, author, ref revisionId, w);
            }

            // B-only paragraphs already exist with their complete B formatting.
            for (var p = bi + paired; p < bj; p++)
                MarkWholeParagraphInserted(revisedParagraphs[p].Paragraph, author, ref revisionId, w);

            if (right.A < originalParagraphs.Count && right.B < revisedParagraphs.Count &&
                !NativeComparisonEngine.SemanticEqual(originalParagraphs[right.A].Text, revisedParagraphs[right.B].Text))
            {
                ApplyTrackedTextDiff(revisedParagraphs[right.B].Paragraph, originalParagraphs[right.A].Text, revisedParagraphs[right.B].Text,
                    author, ref revisionId, includePunctuation, w);
            }
        }

        ReplaceEntry(zip, "word/document.xml", document.ToString(SaveOptions.DisableFormatting));
        EnsureTrackRevisions(zip);
    }

    private static void WriteFlatTrackedBody(XElement body, string oldText, string newText, string author,
        bool includePunctuation, CancellationToken token)
    {
        XNamespace w = W;
        var section = body.Elements(w + "sectPr").LastOrDefault();
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
            for (var p = 0; p < paired; p++) body.Add(TrackedParagraph(oldLines[ai + p], newLines[bi + p], author, ref revisionId, includePunctuation, null));
            for (var p = ai + paired; p < aj; p++) body.Add(DeletedParagraph(oldLines[p], author, ref revisionId, null));
            for (var p = bi + paired; p < bj; p++) body.Add(InsertedParagraph(newLines[p], author, ref revisionId, null));
            if (right.A < oldLines.Count && right.B < newLines.Count) body.Add(PlainParagraph(newLines[right.B], null));
        }
        if (section is not null) body.Add(section);
    }

    private sealed record RunMap(XElement Run, int Start, int End, string Text, bool Simple);
    private sealed record ParagraphSource(string Text, string ContainerKind);
    private sealed record ParagraphEntry(XElement Paragraph, string Text, string ContainerKind, XElement TopLevelBlock);

    private static List<XElement> BodyParagraphs(XElement body, XNamespace w) =>
        body.Descendants(w + "p")
            .Where(p => !p.Ancestors(w + "del").Any() && !p.Ancestors(w + "moveFrom").Any())
            .ToList();

    private static string ParagraphContainerKind(XElement paragraph, XElement body, XNamespace w)
    {
        if (paragraph.Ancestors(w + "tc").Any()) return "table";
        if (paragraph.Ancestors(w + "sdt").Any()) return "sdt";
        if (paragraph.Parent == body) return "body";
        return "other";
    }

    private static XElement TopLevelBodyBlock(XElement paragraph, XElement body)
    {
        var current = paragraph;
        while (current.Parent is XElement parent && parent != body) current = parent;
        return current.Parent == body ? current : paragraph;
    }

    private static List<ParagraphEntry> BodyParagraphEntries(XElement body, XNamespace w) =>
        BodyParagraphs(body, w)
            .Select(p => new ParagraphEntry(p, ParagraphVisibleText(p, w), ParagraphContainerKind(p, body, w), TopLevelBodyBlock(p, body)))
            .ToList();

    private static List<ParagraphSource>? ReadDocxParagraphSourcesFromPath(string path, XNamespace w)
    {
        if (!Path.GetExtension(path).Equals(".docx", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return null;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        var entry = zip.GetEntry("word/document.xml"); if (entry is null) return null;
        using var input = entry.Open(); var doc = XDocument.Load(input, LoadOptions.PreserveWhitespace);
        var body = doc.Root?.Element(w + "body"); if (body is null) return null;
        return BodyParagraphs(body, w)
            .Select(p => new ParagraphSource(ParagraphVisibleText(p, w), ParagraphContainerKind(p, body, w)))
            .Where(x => !string.IsNullOrWhiteSpace(x.Text))
            .ToList();
    }

    private static string ParagraphVisibleText(XElement paragraph, XNamespace w)
    {
        var sb = new StringBuilder();
        foreach (var run in paragraph.Descendants(w + "r"))
        {
            if (run.Ancestors().Any(a => a.Name == w + "del" || a.Name == w + "moveFrom")) continue;
            foreach (var node in run.Descendants())
            {
                if (node.Name == w + "t") sb.Append(node.Value);
                else if (node.Name == w + "tab") sb.Append('\t');
                else if (node.Name == w + "br" || node.Name == w + "cr") sb.Append('\n');
            }
        }
        return sb.ToString();
    }

    private static List<RunMap> BuildRunMap(XElement paragraph, XNamespace w)
    {
        var result = new List<RunMap>(); var pos = 0;
        foreach (var run in paragraph.Descendants(w + "r").ToList())
        {
            if (run.Ancestors().Any(a => a.Name == w + "del" || a.Name == w + "moveFrom")) continue;
            var text = RunVisibleText(run, w);
            if (text.Length == 0) continue;
            var simple = run.Elements().All(x => x.Name == w + "rPr" || x.Name == w + "t");
            result.Add(new RunMap(run, pos, pos + text.Length, text, simple));
            pos += text.Length;
        }
        return result;
    }

    private static string RunVisibleText(XElement run, XNamespace w)
    {
        var sb = new StringBuilder();
        foreach (var node in run.Descendants())
        {
            if (node.Name == w + "t") sb.Append(node.Value);
            else if (node.Name == w + "tab") sb.Append('\t');
            else if (node.Name == w + "br" || node.Name == w + "cr") sb.Append('\n');
        }
        return sb.ToString();
    }

    private static XElement RunFragment(XElement sourceRun, string text, XNamespace w, bool deleted = false)
    {
        var run = new XElement(w + "r");
        var rPr = sourceRun.Element(w + "rPr"); if (rPr is not null) run.Add(new XElement(rPr));
        run.Add(TextNode(deleted ? w + "delText" : w + "t", text));
        return run;
    }

    private static List<(int Start, int End)> MergeRanges(IEnumerable<(int Start, int End)> ranges)
    {
        var sorted = ranges.Where(x => x.End > x.Start).OrderBy(x => x.Start).ThenBy(x => x.End).ToList();
        var result = new List<(int, int)>();
        foreach (var r in sorted)
        {
            if (result.Count == 0 || r.Start > result[^1].Item2) result.Add(r);
            else result[^1] = (result[^1].Item1, Math.Max(result[^1].Item2, r.End));
        }
        return result;
    }

    private static void MarkInsertionRanges(XElement paragraph, IEnumerable<(int Start, int End)> ranges,
        string author, ref int id, XNamespace w)
    {
        var merged = MergeRanges(ranges);
        if (merged.Count == 0) return;
        foreach (var map in BuildRunMap(paragraph, w))
        {
            if (map.Run.Ancestors(w + "ins").Any()) continue;
            var local = merged.Select(r => (Start: Math.Max(r.Start, map.Start) - map.Start, End: Math.Min(r.End, map.End) - map.Start))
                .Where(r => r.End > r.Start).ToList();
            if (local.Count == 0) continue;

            if (!map.Simple)
            {
                // Complex runs (drawings/fields/tabs) stay byte-for-byte intact; if their visible
                // text changed, mark the whole run rather than reconstructing and losing content.
                var parent = map.Run.Parent; if (parent is null) continue;
                map.Run.ReplaceWith(new XElement(w + "ins", RevisionAttrs(w, id++, author), map.Run));
                continue;
            }

            var pieces = new List<object>(); var cursor = 0;
            foreach (var r in local)
            {
                if (cursor < r.Start) pieces.Add(RunFragment(map.Run, map.Text[cursor..r.Start], w));
                pieces.Add(new XElement(w + "ins", RevisionAttrs(w, id++, author), RunFragment(map.Run, map.Text[r.Start..r.End], w)));
                cursor = r.End;
            }
            if (cursor < map.Text.Length) pieces.Add(RunFragment(map.Run, map.Text[cursor..], w));
            map.Run.ReplaceWith(pieces);
        }
    }

    private static void InsertDeletionAt(XElement paragraph, int offset, string deletedText, string author,
        ref int id, XNamespace w)
    {
        if (string.IsNullOrEmpty(deletedText)) return;
        var maps = BuildRunMap(paragraph, w);
        if (maps.Count == 0)
        {
            var pPr = paragraph.Element(w + "pPr");
            var del = new XElement(w + "del", RevisionAttrs(w, id++, author), new XElement(w + "r", TextNode(w + "delText", deletedText)));
            if (pPr is null) paragraph.AddFirst(del); else pPr.AddAfterSelf(del);
            return;
        }

        offset = Math.Clamp(offset, 0, maps[^1].End);
        var target = maps.FirstOrDefault(x => offset <= x.End) ?? maps[^1];
        var local = Math.Clamp(offset - target.Start, 0, target.Text.Length);
        var delNode = new XElement(w + "del", RevisionAttrs(w, id++, author), RunFragment(target.Run, deletedText, w, deleted: true));
        if (target.Simple && local > 0 && local < target.Text.Length)
        {
            var parts = new List<object>
            {
                RunFragment(target.Run, target.Text[..local], w),
                delNode,
                RunFragment(target.Run, target.Text[local..], w)
            };
            target.Run.ReplaceWith(parts);
        }
        else if (local <= 0) target.Run.AddBeforeSelf(delNode);
        else target.Run.AddAfterSelf(delNode);
    }

    private static void ApplyTrackedTextDiff(XElement paragraph, string oldText, string newText, string author,
        ref int id, bool includePunctuation, XNamespace w)
    {
        if (NativeComparisonEngine.SemanticEqual(oldText, newText)) return;
        var a = Tokens(oldText); var b = Tokens(newText);
        var matches = Lcs(a.Select(x => Key(x.Text)).ToArray(), b.Select(x => Key(x.Text)).ToArray());
        var anchors = new List<(int A, int B)> { (-1, -1) }; anchors.AddRange(matches); anchors.Add((a.Count, b.Count));
        var inserts = new List<(int Start, int End)>();
        var deletes = new List<(int Anchor, string Text)>();
        for (var k = 0; k < anchors.Count - 1; k++)
        {
            var left = anchors[k]; var right = anchors[k + 1];
            var ai = left.A + 1; var aj = right.A; var bi = left.B + 1; var bj = right.B;
            var oldChunk = JoinTokenRange(oldText, a, ai, aj); var newChunk = JoinTokenRange(newText, b, bi, bj);
            var punctuationOnly = (oldChunk.Length == 0 || PunctuationOnly(oldChunk)) &&
                                  (newChunk.Length == 0 || PunctuationOnly(newChunk));
            if (!includePunctuation && punctuationOnly) continue;
            if (newChunk.Length > 0)
            {
                var ns = b[bi].Start; var ne = b[bj - 1].End;
                inserts.Add((ns, ne));
            }
            if (oldChunk.Length > 0)
            {
                var anchor = bi < b.Count ? b[bi].Start : newText.Length;
                deletes.Add((anchor, oldChunk));
            }
        }

        // Deletions first: inserting w:del does not change the visible B text offsets.
        foreach (var d in deletes.OrderByDescending(x => x.Anchor))
            InsertDeletionAt(paragraph, d.Anchor, d.Text, author, ref id, w);
        MarkInsertionRanges(paragraph, inserts, author, ref id, w);
    }

    private static void MarkWholeParagraphInserted(XElement paragraph, string author, ref int id, XNamespace w)
    {
        var text = ParagraphVisibleText(paragraph, w);
        if (text.Length > 0) MarkInsertionRanges(paragraph, new[] { (0, text.Length) }, author, ref id, w);
    }

    private static void InsertDeletedParagraphRange(XElement body, IReadOnlyList<ParagraphEntry> revisedParagraphs,
        int nextBIndex, IReadOnlyList<ParagraphSource> deletedSources, string author, ref int id, XNamespace w)
    {
        if (deletedSources.Count == 0) return;

        // No visible B paragraph: retain B's blank/layout paragraphs and place tracked deletions at
        // the end of body, immediately before sectPr.
        if (revisedParagraphs.Count == 0)
        {
            var sectPr = body.Elements(w + "sectPr").LastOrDefault();
            XElement? cursor = null;
            foreach (var source in deletedSources)
            {
                var deleted = DeletedParagraph(source.Text, author, ref id, null);
                if (cursor is not null) { cursor.AddAfterSelf(deleted); cursor = deleted; }
                else if (sectPr is not null) { sectPr.AddBeforeSelf(deleted); cursor = deleted; }
                else { body.Add(deleted); cursor = deleted; }
            }
            return;
        }

        var pastEnd = nextBIndex >= revisedParagraphs.Count;
        var anchor = revisedParagraphs[pastEnd ? revisedParagraphs.Count - 1 : nextBIndex];
        XElement? afterCursor = null;
        XElement? afterBoundary = null;

        foreach (var source in deletedSources)
        {
            var styleSource = anchor.Paragraph.Element(w + "pPr");
            var deleted = DeletedParagraph(source.Text, author, ref id, styleSource is null ? null : new XElement(styleSource));

            // Only insert inside table/SDT when both source and B anchor are the same structural
            // family. Otherwise use the top-level B block boundary so a deleted body paragraph can
            // never be accidentally injected into a table cell or content control.
            var sameNestedFamily = source.ContainerKind == anchor.ContainerKind &&
                                   source.ContainerKind is "table" or "sdt" &&
                                   anchor.Paragraph.Parent is not null;
            var boundary = sameNestedFamily ? anchor.Paragraph : anchor.TopLevelBlock;

            if (!pastEnd)
            {
                boundary.AddBeforeSelf(deleted); // repeated AddBeforeSelf preserves forward order
            }
            else if (afterCursor is not null && ReferenceEquals(afterBoundary, boundary))
            {
                afterCursor.AddAfterSelf(deleted);
                afterCursor = deleted;
            }
            else
            {
                // Structural boundary changed (for example table -> body). Start a new cursor at
                // that boundary rather than carrying an in-table cursor into the body.
                boundary.AddAfterSelf(deleted);
                afterBoundary = boundary;
                afterCursor = deleted;
            }
        }
    }

    private static int NextRevisionId(XDocument document, XNamespace w)
    {
        var max = 0;
        foreach (var e in document.Descendants().Where(x => x.Name == w + "ins" || x.Name == w + "del" || x.Name == w + "moveFrom" || x.Name == w + "moveTo"))
            if (int.TryParse(e.Attribute(w + "id")?.Value, out var id)) max = Math.Max(max, id);
        return max + 1;
    }

    private static string ParagraphText(XElement paragraph, XNamespace w) => ParagraphVisibleText(paragraph, w).Trim();

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
        new XAttribute(w + "id", id), new XAttribute(w + "author", SanitizeXmlText(string.IsNullOrWhiteSpace(author) ? "Revised" : author)),
        new XAttribute(w + "date", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"))
    };

    private static XElement TextNode(XName name, string text) =>
        new(name, new XAttribute(XNamespace.Xml + "space", "preserve"), SanitizeXmlText(text));

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

        XNamespace rel = "http://schemas.openxmlformats.org/package/2006/relationships";
        var relPath = "word/_rels/document.xml.rels";
        var relEntry = zip.GetEntry(relPath);
        XDocument rels;
        if (relEntry is null) rels = new XDocument(new XElement(rel + "Relationships"));
        else { using var rs = relEntry.Open(); rels = XDocument.Load(rs, LoadOptions.PreserveWhitespace); }
        var relRoot = rels.Root ?? new XElement(rel + "Relationships");
        if (rels.Root is null) rels.Add(relRoot);
        const string settingsType = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/settings";
        if (!relRoot.Elements(rel + "Relationship").Any(x => (string?)x.Attribute("Type") == settingsType))
        {
            var used = relRoot.Elements(rel + "Relationship").Select(x => (string?)x.Attribute("Id")).Where(x => x is not null).ToHashSet(StringComparer.Ordinal);
            var n = 1; while (used.Contains("rIdSettings" + n)) n++;
            relRoot.Add(new XElement(rel + "Relationship", new XAttribute("Id", "rIdSettings" + n), new XAttribute("Type", settingsType), new XAttribute("Target", "settings.xml")));
        }
        ReplaceEntry(zip, relPath, rels.ToString(SaveOptions.DisableFormatting));

        XNamespace ct = "http://schemas.openxmlformats.org/package/2006/content-types";
        var ctEntry = zip.GetEntry("[Content_Types].xml");
        if (ctEntry is not null)
        {
            XDocument types; using (var cs = ctEntry.Open()) types = XDocument.Load(cs, LoadOptions.PreserveWhitespace);
            var typesRoot = types.Root;
            if (typesRoot is not null && !typesRoot.Elements(ct + "Override").Any(x => (string?)x.Attribute("PartName") == "/word/settings.xml"))
                typesRoot.Add(new XElement(ct + "Override", new XAttribute("PartName", "/word/settings.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.wordprocessingml.settings+xml")));
            ReplaceEntry(zip, "[Content_Types].xml", types.ToString(SaveOptions.DisableFormatting));
        }
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
    private static List<SpanToken> Tokens(string text) => ExportTokenRegex.Matches(text ?? "")
        .Select(m => new SpanToken(m.Index, m.Index + m.Length, m.Value)).ToList();
    private static string Key(string text) => NativeComparisonEngine.Normalize(text);
    private static string JoinTokenRange(string source, IReadOnlyList<SpanToken> tokens, int start, int end)
    {
        if (start >= end) return ""; return source[tokens[start].Start..tokens[end - 1].End];
    }
    private static bool PunctuationOnly(string value) => !string.IsNullOrWhiteSpace(value) && value.Where(x => !char.IsWhiteSpace(x)).All(x => !char.IsLetterOrDigit(x));

    private static List<(int A, int B)> Lcs(string[] a, string[] b)
    {
        if ((long)(a.Length + 1) * (b.Length + 1) > 4_000_000L)
            return SequencePairs(a, b);

        var dp = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--) for (var j = b.Length - 1; j >= 0; j--)
            dp[i, j] = a[i] == b[j] ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);
        var r = new List<(int, int)>(); var x = 0; var y = 0;
        while (x < a.Length && y < b.Length) { if (a[x] == b[y]) { r.Add((x++, y++)); } else if (dp[x + 1, y] >= dp[x, y + 1]) x++; else y++; }
        return r;
    }

    private static List<(int A, int B)> SequencePairs(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var b2j = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var j = 0; j < b.Count; j++)
        {
            if (!b2j.TryGetValue(b[j], out var list)) b2j[b[j]] = list = new List<int>();
            list.Add(j);
        }

        var queue = new Stack<(int Alo, int Ahi, int Blo, int Bhi)>();
        queue.Push((0, a.Count, 0, b.Count));
        var blocks = new List<(int A, int B, int Size)>();
        while (queue.Count > 0)
        {
            var (alo, ahi, blo, bhi) = queue.Pop();
            var bestI = alo; var bestJ = blo; var bestSize = 0;
            var previous = new Dictionary<int, int>();
            var current = new Dictionary<int, int>();
            for (var i = alo; i < ahi; i++)
            {
                current.Clear();
                if (b2j.TryGetValue(a[i], out var js))
                {
                    foreach (var j in js)
                    {
                        if (j < blo) continue;
                        if (j >= bhi) break;
                        var k = (previous.TryGetValue(j - 1, out var p) ? p : 0) + 1;
                        current[j] = k;
                        if (k > bestSize) { bestI = i - k + 1; bestJ = j - k + 1; bestSize = k; }
                    }
                }
                (previous, current) = (current, previous);
            }
            if (bestSize == 0) continue;
            blocks.Add((bestI, bestJ, bestSize));
            if (alo < bestI && blo < bestJ) queue.Push((alo, bestI, blo, bestJ));
            if (bestI + bestSize < ahi && bestJ + bestSize < bhi)
                queue.Push((bestI + bestSize, ahi, bestJ + bestSize, bhi));
        }
        blocks.Sort((x, y) => x.A != y.A ? x.A.CompareTo(y.A) : x.B.CompareTo(y.B));
        var result = new List<(int A, int B)>();
        foreach (var block in blocks)
            for (var k = 0; k < block.Size; k++) result.Add((block.A + k, block.B + k));
        return result;
    }

    private static string SanitizeXmlText(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (char.IsHighSurrogate(ch))
            {
                if (i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                    sb.Append(ch).Append(value[++i]);
                continue;
            }
            if (char.IsLowSurrogate(ch) || ch == '\uFFFE' || ch == '\uFFFF') continue;
            if (ch < 0x20 && ch != '\t' && ch != '\n' && ch != '\r') continue;
            sb.Append(ch);
        }
        return sb.ToString();
    }

    private static string Esc(string value)
    {
        value = SanitizeXmlText(value);
        if (value.Length == 0) return string.Empty;
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '&': sb.Append("&amp;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&apos;"); break;
                default: sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }
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
