using System.IO.Compression;
using System.Security.Cryptography;
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
    private static readonly HashSet<string> TrackedRevisionNames = new(StringComparer.Ordinal)
    {
        "ins", "del", "moveFrom", "moveTo", "moveFromRangeStart", "moveFromRangeEnd",
        "moveToRangeStart", "moveToRangeEnd", "cellIns", "cellDel", "numberingChange",
        "customXmlInsRangeStart", "customXmlInsRangeEnd", "customXmlDelRangeStart", "customXmlDelRangeEnd",
        "customXmlMoveFromRangeStart", "customXmlMoveFromRangeEnd", "customXmlMoveToRangeStart", "customXmlMoveToRangeEnd",
        "conflictIns", "conflictDel", "customXmlConflictInsRangeStart", "customXmlConflictInsRangeEnd",
        "customXmlConflictDelRangeStart", "customXmlConflictDelRangeEnd"
    };

    private static string TemporaryOutputPath(string outputPath)
    {
        var full = Path.GetFullPath(outputPath);
        var dir = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
    }

    private static void CommitTemporaryOutput(string temporaryPath, string outputPath)
    {
        File.Move(temporaryPath, Path.GetFullPath(outputPath), overwrite: true);
    }

    public static async Task WriteXlsxAsync(ComparisonResultVm result, string outputPath, CancellationToken cancellationToken)
    {
        var temporaryPath = TemporaryOutputPath(outputPath);
        try
        {
            await Task.Run(() => WriteXlsx(result, temporaryPath, cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            CommitTemporaryOutput(temporaryPath, outputPath);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
        }
    }

    public static async Task WriteTrackedDocxAsync(
        string originalPath, string revisedPath, string outputPath, string author, bool includePunctuation,
        CancellationToken cancellationToken, ComparisonResultVm? comparisonResult = null,
        int originalDocumentIndex = -1, int revisedDocumentIndex = -1)
    {
        var outputFullPath = Path.GetFullPath(outputPath);
        if (string.Equals(Path.GetFullPath(originalPath), outputFullPath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFullPath(revisedPath), outputFullPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("입력 원본 문서(A/B) 자체를 변경추적 출력으로 덮어쓸 수 없습니다. 다른 파일명으로 저장하세요.");

        var oldText = await NativeDocumentReader.ReadAsync(originalPath, cancellationToken);
        var newText = await NativeDocumentReader.ReadAsync(revisedPath, cancellationToken);
        var temporaryPath = TemporaryOutputPath(outputPath);
        try
        {
            await Task.Run(() => WriteTrackedDocx(oldText, newText, originalPath, revisedPath, temporaryPath, author,
                includePunctuation, cancellationToken, comparisonResult, originalDocumentIndex, revisedDocumentIndex), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            CommitTemporaryOutput(temporaryPath, outputPath);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
        }
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
  <fonts count="2"><font><sz val="10"/><name val="Calibri"/></font><font><b/><sz val="10"/><color rgb="FFFFFFFF"/><name val="Calibri"/></font></fonts>
  <fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FF1F4E78"/><bgColor indexed="64"/></patternFill></fill></fills>
  <borders count="1"><border/></borders><cellStyleXfs count="1"><xf/></cellStyleXfs>
  <cellXfs count="2"><xf fontId="0" fillId="0" borderId="0" xfId="0" applyAlignment="1"><alignment vertical="top" wrapText="1"/></xf><xf fontId="1" fillId="2" borderId="0" xfId="0" applyAlignment="1"><alignment horizontal="center" vertical="center" wrapText="1"/></xf></cellXfs>
</styleSheet>
""");

        var xml = new StringBuilder(1024 * 32);
        xml.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews><cols>");
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
        xml.Append($"</sheetData><autoFilter ref=\"A1:{ColumnName(result.Names.Count + 1)}{Math.Max(1, rowNumber)}\"/></worksheet>");
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
                        xml.Append("<rPr><color rgb=\"FF1565C0\"/><u val=\"single\"/></rPr>");
                        break;
                    case "both":
                        xml.Append("<rPr><strike/><color rgb=\"FF7A3E9D\"/><u val=\"single\"/></rPr>");
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

    private static void WriteTrackedDocx(string oldText, string newText, string originalPath, string revisedPath, string outputPath,
        string author, bool includePunctuation, CancellationToken token, ComparisonResultVm? comparisonResult,
        int originalDocumentIndex, int revisedDocumentIndex)
    {
        var outputFullPath = Path.GetFullPath(outputPath);
        var originalFullPath = Path.GetFullPath(originalPath);
        var revisedFullPath = Path.GetFullPath(revisedPath);
        if (string.Equals(originalFullPath, outputFullPath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(revisedFullPath, outputFullPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("입력 원본 문서(A/B) 자체를 변경추적 출력으로 덮어쓸 수 없습니다. 다른 파일명으로 저장하세요.");
        Directory.CreateDirectory(Path.GetDirectoryName(outputFullPath)!);
        var originalIsDocx = Path.GetExtension(originalPath).Equals(".docx", StringComparison.OrdinalIgnoreCase);
        var revisedIsDocx = Path.GetExtension(revisedPath).Equals(".docx", StringComparison.OrdinalIgnoreCase);
        if (revisedIsDocx)
            EnsureNoExistingTrackedRevisions(revisedPath);
        if (originalIsDocx && revisedIsDocx)
        {
            EnsureAncillaryWordPartsEquivalent(originalPath, revisedPath);
            EnsureMainDocumentUnsupportedContentEquivalent(originalPath, revisedPath);
        }
        else if (originalIsDocx || revisedIsDocx)
        {
            var docxPath = originalIsDocx ? originalPath : revisedPath;
            if (ReadMainDocumentAncillaryReferenceSignature(docxPath).Length > 0)
                throw new InvalidOperationException("DOCX와 TXT 간 Word 변경추적 내보내기에서 header/footer/footnote/endnote 부속 내용을 안전하게 추적할 수 없습니다. 부속 참조가 있는 DOCX는 동일 형식의 DOCX와 비교하세요.");
            if (ReadMainDocumentUnsupportedContentSignature(docxPath).Length > 0)
                throw new InvalidOperationException("DOCX와 TXT 간 Word 변경추적 내보내기에서 본문 관계/필드/기호 같은 비텍스트 내용을 안전하게 추적할 수 없습니다. 비텍스트 참조가 있는 DOCX는 동일 형식의 DOCX와 비교하세요.");
        }

        if (revisedIsDocx)
        {
            File.Copy(revisedPath, outputPath, overwrite: true);
        }
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
        var revisedNumbering = NativeDocumentReader.BuildNumberingContext(zip, w);
        var revisedParagraphs = BodyParagraphEntries(body, w, revisedNumbering)
            .Where(x => !string.IsNullOrWhiteSpace(x.Text))
            .ToList();
        List<ParagraphSource> originalParagraphs;
        if (Path.GetExtension(originalPath).Equals(".docx", StringComparison.OrdinalIgnoreCase))
            originalParagraphs = ReadDocxParagraphSourcesFromPath(originalPath, w) ??
                                 oldText.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0)
                                     .Select(x => new ParagraphSource(x, "body", new ParagraphAddress(-1, -1, -1, -1, -1, -1, -1, -1, -1), new NativeDocumentReader.ParagraphNumberInfo(string.Empty, null, 0))).ToList();
        else
            originalParagraphs = oldText.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0)
                .Select(x => new ParagraphSource(x, "body", new ParagraphAddress(-1, -1, -1, -1, -1, -1, -1, -1, -1), new NativeDocumentReader.ParagraphNumberInfo(string.Empty, null, 0))).ToList();

        // Reuse the comparison result as high-confidence paragraph anchors.  This keeps Word
        // export on the same article/general-unit lineage as the on-screen comparison.  Exact
        // paragraph anchors fill the remaining gaps; only the residual gaps use local similarity.
        var preferred = BuildPreferredParagraphPairs(originalParagraphs, revisedParagraphs, comparisonResult,
            originalDocumentIndex, revisedDocumentIndex);
        var ops = AlignParagraphs(originalParagraphs, revisedParagraphs, preferred, token);

        var matchedOld = ops.Where(x => x.Old >= 0 && x.New >= 0).Select(x => x.Old).ToHashSet();
        var matchedNew = ops.Where(x => x.Old >= 0 && x.New >= 0).Select(x => x.New).ToHashSet();
        var fullyInsertedRows = revisedParagraphs.Select((entry, index) => (entry, index))
            .Where(x => x.entry.Row is not null)
            .GroupBy(x => x.entry.Row!)
            .Where(g => g.All(x => !matchedNew.Contains(x.index)))
            .Select(g => g.Key).ToHashSet();
        var fullyDeletedRows = originalParagraphs.Select((source, index) => (source, index))
            .Where(x => x.source.Address.Table >= 0 && x.source.Address.Row >= 0)
            .GroupBy(x => (x.source.Address.Table, x.source.Address.Row))
            .Where(g => g.All(x => !matchedOld.Contains(x.index)))
            .Select(g => g.Key).ToHashSet();
        // Never trust raw table/row ordinals across A and B: deleting an earlier table shifts every
        // later table index.  Base structural maps on aligned paragraphs; the nested-table fallback
        // below is permitted only through an already matched containing cell.
        var rowMap = BuildMatchedRowMap(originalParagraphs, revisedParagraphs, ops);
        var cellMap = BuildMatchedCellMap(originalParagraphs, revisedParagraphs, ops);
        var paragraphMap = BuildMatchedParagraphMap(originalParagraphs, revisedParagraphs, ops);
        var tableMap = BuildMatchedTableMap(originalParagraphs, revisedParagraphs, ops, cellMap);
        var fullyInsertedCells = revisedParagraphs.Select((entry, index) => (entry, index))
            .Where(x => x.entry.Cell is not null && x.entry.Row is not null && !fullyInsertedRows.Contains(x.entry.Row))
            .GroupBy(x => x.entry.Cell!)
            .Where(g => g.All(x => !matchedNew.Contains(x.index)))
            .Select(g => g.Key).ToHashSet();
        var fullyDeletedCells = originalParagraphs.Select((source, index) => (source, index))
            .Where(x => x.source.Address.Table >= 0 && x.source.Address.Row >= 0 && x.source.Address.Cell >= 0 &&
                        !fullyDeletedRows.Contains((x.source.Address.Table, x.source.Address.Row)))
            .GroupBy(x => (x.source.Address.Table, x.source.Address.Row, x.source.Address.Cell))
            .Where(g => g.All(x => !matchedOld.Contains(x.index)))
            .Select(g => g.Key).ToHashSet();

        var revisionId = NextRevisionId(document, w);
        foreach (var row in fullyInsertedRows)
            MarkTableRowRevision(row, inserted: true, author, ref revisionId, w);
        foreach (var cell in fullyInsertedCells)
            MarkTableCellRevision(cell, inserted: true, author, ref revisionId, w);

        var bCursor = 0;
        for (var k = 0; k < ops.Count;)
        {
            token.ThrowIfCancellationRequested();
            var op = ops[k];
            if (op.Old >= 0 && op.New >= 0)
            {
                ApplyTrackedNumberingDiff(revisedParagraphs[op.New].Paragraph,
                    originalParagraphs[op.Old].Numbering, revisedParagraphs[op.New].Numbering,
                    author, ref revisionId, w);
                ApplyTrackedTextDiff(revisedParagraphs[op.New].Paragraph, originalParagraphs[op.Old].Text,
                    revisedParagraphs[op.New].Text, author, ref revisionId, includePunctuation, w);
                bCursor = Math.Max(bCursor, op.New + 1);
                k++;
                continue;
            }
            if (op.New >= 0)
            {
                var entry = revisedParagraphs[op.New];
                if ((entry.Row is null || !fullyInsertedRows.Contains(entry.Row)) &&
                    (entry.Cell is null || !fullyInsertedCells.Contains(entry.Cell)))
                    MarkWholeParagraphInserted(entry.Paragraph, author, ref revisionId, w);
                bCursor = Math.Max(bCursor, op.New + 1);
                k++;
                continue;
            }

            var deleted = new List<ParagraphSource>();
            while (k < ops.Count && ops[k].Old >= 0 && ops[k].New < 0)
            {
                deleted.Add(originalParagraphs[ops[k].Old]);
                k++;
            }
            InsertDeletedParagraphRange(body, revisedParagraphs, bCursor, deleted, fullyDeletedRows, fullyDeletedCells,
                tableMap, rowMap, cellMap, paragraphMap, author, ref revisionId, w);
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

    private sealed record RunMap(XElement Run, int Start, int End, string Text);
    private sealed record ParagraphAddress(int TopBlock, int Table, int Row, int Cell, int Paragraph,
        int ParentTable, int ParentRow, int ParentCell, int NestedTableOrdinal);
    private sealed record ParagraphSource(string Text, string ContainerKind, ParagraphAddress Address,
        NativeDocumentReader.ParagraphNumberInfo Numbering);
    private sealed record ParagraphEntry(XElement Paragraph, string Text, string ContainerKind, XElement TopLevelBlock,
        ParagraphAddress Address, XElement? Table, XElement? Row, XElement? Cell,
        NativeDocumentReader.ParagraphNumberInfo Numbering);
    private sealed record ParagraphOp(int Old, int New);

    private static string MemberAnchorText(MemberVm member)
    {
        if (!string.IsNullOrWhiteSpace(member.Header)) return member.Header;
        if (!string.IsNullOrWhiteSpace(member.Body) && !member.Body.Contains('\n')) return member.Body;
        if (!string.IsNullOrWhiteSpace(member.Text) && !member.Text.Contains('\n')) return member.Text;
        return string.Empty;
    }

    private static string LogicalParagraphText(ParagraphSource source) =>
        string.IsNullOrWhiteSpace(source.Numbering.Label) ? source.Text : (source.Numbering.Label + " " + source.Text).Trim();

    private static string LogicalParagraphText(ParagraphEntry entry) =>
        string.IsNullOrWhiteSpace(entry.Numbering.Label) ? entry.Text : (entry.Numbering.Label + " " + entry.Text).Trim();

    private static int FindParagraphByText<T>(IReadOnlyList<T> items, Func<T, string> textSelector, string text,
        HashSet<int> used, int preferredAfter)
    {
        var key = NativeComparisonEngine.Normalize(text);
        if (key.Length == 0) return -1;
        var candidates = Enumerable.Range(0, items.Count)
            .Where(i => !used.Contains(i) && NativeComparisonEngine.Normalize(textSelector(items[i])) == key)
            .ToList();
        if (candidates.Count == 0) return -1;
        var at = candidates.OrderBy(i => i < preferredAfter ? 1 : 0).ThenBy(i => Math.Abs(i - preferredAfter)).First();
        used.Add(at);
        return at;
    }

    private static List<(int Old, int New)> LongestMonotonicPairs(List<(int Old, int New)> raw)
    {
        raw = raw.Distinct().OrderBy(x => x.Old).ThenBy(x => x.New).ToList();
        if (raw.Count <= 1) return raw;
        var len = Enumerable.Repeat(1, raw.Count).ToArray();
        var prev = Enumerable.Repeat(-1, raw.Count).ToArray();
        var best = 0;
        for (var i = 0; i < raw.Count; i++)
        {
            for (var j = 0; j < i; j++)
            {
                if (raw[j].Old < raw[i].Old && raw[j].New < raw[i].New && len[j] + 1 > len[i])
                { len[i] = len[j] + 1; prev[i] = j; }
            }
            if (len[i] > len[best]) best = i;
        }
        var result = new List<(int Old, int New)>();
        for (var i = best; i >= 0; i = prev[i])
        {
            result.Add(raw[i]);
            if (prev[i] < 0) break;
        }
        result.Reverse();
        return result;
    }

    private static List<(int Old, int New)> BuildPreferredParagraphPairs(
        IReadOnlyList<ParagraphSource> oldParagraphs, IReadOnlyList<ParagraphEntry> newParagraphs,
        ComparisonResultVm? result, int oldDoc, int newDoc)
    {
        if (result is null || oldDoc < 0 || newDoc < 0 || oldDoc == newDoc) return new();
        if (result.Rows.Count == 0) return new();
        var usedOld = new HashSet<int>(); var usedNew = new HashSet<int>();
        var raw = new List<(int Old, int New)>();
        var oldHint = 0; var newHint = 0;
        foreach (var row in result.Rows)
        {
            if (row.Members.Count <= Math.Max(oldDoc, newDoc)) continue;
            var om = row.Members[oldDoc]; var nm = row.Members[newDoc];
            if (om is null || nm is null) continue;
            var ot = MemberAnchorText(om); var nt = MemberAnchorText(nm);
            if (ot.Length == 0 || nt.Length == 0) continue;
            var oi = FindParagraphByText(oldParagraphs, LogicalParagraphText, ot, usedOld, oldHint);
            var ni = FindParagraphByText(newParagraphs, LogicalParagraphText, nt, usedNew, newHint);
            if (oi < 0 || ni < 0) continue;
            raw.Add((oi, ni));
            oldHint = oi + 1; newHint = ni + 1;
        }
        // Moved units can cross in document order.  A single linear paragraph edit stream cannot
        // use crossing anchors, so retain the largest monotonic subset and let residual gaps be
        // handled as delete/insert around B's final order.
        return LongestMonotonicPairs(raw);
    }

    private static double ParagraphMatchCost(ParagraphSource oa, ParagraphEntry nb)
    {
        var sim = NativeComparisonEngine.SemanticEqual(oa.Text, nb.Text)
            ? 1.0 : NativeComparisonEngine.ExportSimilarity(oa.Text, nb.Text);
        var containerPenalty = oa.ContainerKind == nb.ContainerKind ? 0.0 : .10;
        return sim >= .38 ? .92 * (1.0 - sim) + containerPenalty : 1.04 + containerPenalty;
    }

    private static List<ParagraphOp> AlignParagraphGapExact(IReadOnlyList<ParagraphSource> a, IReadOnlyList<ParagraphEntry> b,
        int a0, int a1, int b0, int b1, CancellationToken token)
    {
        var n = a1 - a0; var m = b1 - b0;
        var result = new List<ParagraphOp>();
        if (n <= 0) { for (var j = b0; j < b1; j++) result.Add(new ParagraphOp(-1, j)); return result; }
        if (m <= 0) { for (var i = a0; i < a1; i++) result.Add(new ParagraphOp(i, -1)); return result; }
        const double gap = .48;
        var dp = new double[n + 1, m + 1];
        var prev = new byte[n + 1, m + 1];
        const byte M = 1, D = 2, I = 3;
        for (var i = 1; i <= n; i++) { dp[i, 0] = dp[i - 1, 0] + gap; prev[i, 0] = D; }
        for (var j = 1; j <= m; j++) { dp[0, j] = dp[0, j - 1] + gap; prev[0, j] = I; }
        for (var i = 1; i <= n; i++)
        {
            if ((i & 31) == 0) token.ThrowIfCancellationRequested();
            for (var j = 1; j <= m; j++)
            {
                var mc = dp[i - 1, j - 1] + ParagraphMatchCost(a[a0 + i - 1], b[b0 + j - 1]);
                var dc = dp[i - 1, j] + gap;
                var ic = dp[i, j - 1] + gap;
                if (mc <= dc && mc <= ic) { dp[i, j] = mc; prev[i, j] = M; }
                else if (dc <= ic) { dp[i, j] = dc; prev[i, j] = D; }
                else { dp[i, j] = ic; prev[i, j] = I; }
            }
        }
        var rev = new List<ParagraphOp>(); var x = n; var y = m;
        while (x > 0 || y > 0)
        {
            var op = prev[x, y];
            if (op == M) { rev.Add(new ParagraphOp(a0 + x - 1, b0 + y - 1)); x--; y--; }
            else if (op == D) { rev.Add(new ParagraphOp(a0 + x - 1, -1)); x--; }
            else { rev.Add(new ParagraphOp(-1, b0 + y - 1)); y--; }
        }
        rev.Reverse(); return rev;
    }

    private static double[] AlignmentLastRow(IReadOnlyList<ParagraphSource> a, IReadOnlyList<ParagraphEntry> b,
        int a0, int a1, int b0, int b1, bool reverse, CancellationToken token)
    {
        const double gap = .48;
        var n = a1 - a0; var m = b1 - b0;
        var prior = new double[m + 1]; var current = new double[m + 1];
        for (var j = 1; j <= m; j++) prior[j] = prior[j - 1] + gap;
        for (var i = 1; i <= n; i++)
        {
            if ((i & 15) == 0) token.ThrowIfCancellationRequested();
            current[0] = prior[0] + gap;
            var ai = reverse ? a1 - i : a0 + i - 1;
            for (var j = 1; j <= m; j++)
            {
                var bj = reverse ? b1 - j : b0 + j - 1;
                var mc = prior[j - 1] + ParagraphMatchCost(a[ai], b[bj]);
                var dc = prior[j] + gap;
                var ic = current[j - 1] + gap;
                current[j] = Math.Min(mc, Math.Min(dc, ic));
            }
            (prior, current) = (current, prior);
        }
        return prior;
    }

    private static List<ParagraphOp> AlignParagraphGap(IReadOnlyList<ParagraphSource> a, IReadOnlyList<ParagraphEntry> b,
        int a0, int a1, int b0, int b1, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var n = a1 - a0; var m = b1 - b0;
        if (n <= 0 || m <= 0 || (long)(n + 1) * (m + 1) <= 250_000L)
            return AlignParagraphGapExact(a, b, a0, a1, b0, b1, token);

        // Hirschberg alignment preserves the same match/gap cost model with O(m) working memory.
        // Unlike the old large-gap fallback it does not blindly pair paragraph i with paragraph i.
        var amid = a0 + n / 2;
        var forward = AlignmentLastRow(a, b, a0, amid, b0, b1, false, token);
        var backward = AlignmentLastRow(a, b, amid, a1, b0, b1, true, token);
        var bestJ = 0; var bestCost = double.PositiveInfinity;
        for (var j = 0; j <= m; j++)
        {
            var cost = forward[j] + backward[m - j];
            if (cost < bestCost - 1e-12) { bestCost = cost; bestJ = j; }
        }
        var splitB = b0 + bestJ;
        var left = AlignParagraphGap(a, b, a0, amid, b0, splitB, token);
        var right = AlignParagraphGap(a, b, amid, a1, splitB, b1, token);
        left.AddRange(right);
        return left;
    }

    private static List<ParagraphOp> AlignParagraphs(IReadOnlyList<ParagraphSource> a, IReadOnlyList<ParagraphEntry> b,
        IReadOnlyList<(int Old, int New)> preferred, CancellationToken token)
    {
        var oldKeys = a.Select(x => NativeComparisonEngine.Normalize(LogicalParagraphText(x))).ToArray();
        var newKeys = b.Select(x => NativeComparisonEngine.Normalize(LogicalParagraphText(x))).ToArray();
        var exact = Lcs(oldKeys, newKeys);
        var forced = preferred.OrderBy(x => x.Old).ThenBy(x => x.New).ToList();
        var anchors = new List<(int Old, int New)> { (-1, -1) };
        var po = -1; var pn = -1;
        foreach (var f in forced)
        {
            foreach (var e in exact.Where(e => e.A > po && e.A < f.Old && e.B > pn && e.B < f.New))
                anchors.Add((e.A, e.B));
            if (f.Old > anchors[^1].Old && f.New > anchors[^1].New) anchors.Add(f);
            po = f.Old; pn = f.New;
        }
        foreach (var e in exact.Where(e => e.A > po && e.B > pn))
            if (e.A > anchors[^1].Old && e.B > anchors[^1].New) anchors.Add((e.A, e.B));
        anchors.Add((a.Count, b.Count));

        var result = new List<ParagraphOp>();
        for (var k = 0; k < anchors.Count - 1; k++)
        {
            token.ThrowIfCancellationRequested();
            var left = anchors[k]; var right = anchors[k + 1];
            result.AddRange(AlignParagraphGap(a, b, left.Old + 1, right.Old, left.New + 1, right.New, token));
            if (right.Old < a.Count && right.New < b.Count)
                result.Add(new ParagraphOp(right.Old, right.New));
        }
        return result;
    }

    private static Dictionary<int, int> BuildMatchedTableMap(IReadOnlyList<ParagraphSource> oldParagraphs,
        IReadOnlyList<ParagraphEntry> newParagraphs, IReadOnlyList<ParagraphOp> ops,
        IReadOnlyDictionary<(int Table, int Row, int Cell), (int Table, int Row, int Cell)> cellMap)
    {
        var votes = new Dictionary<(int OldTable, int NewTable), int>();
        foreach (var op in ops.Where(x => x.Old >= 0 && x.New >= 0))
        {
            var oldTable = oldParagraphs[op.Old].Address.Table;
            var newTable = newParagraphs[op.New].Address.Table;
            if (oldTable < 0 || newTable < 0) continue;
            var key = (oldTable, newTable);
            votes[key] = votes.TryGetValue(key, out var count) ? count + 1 : 1;
        }
        var result = new Dictionary<int, int>();
        var usedNew = new HashSet<int>();
        foreach (var candidate in votes.OrderByDescending(x => x.Value)
                     .ThenBy(x => x.Key.OldTable).ThenBy(x => x.Key.NewTable))
        {
            if (result.ContainsKey(candidate.Key.OldTable) || usedNew.Contains(candidate.Key.NewTable)) continue;
            result[candidate.Key.OldTable] = candidate.Key.NewTable;
            usedNew.Add(candidate.Key.NewTable);
        }

        // A nested table whose only row/cell changed completely may have no matched paragraph of
        // its own, so the vote-only map above has no evidence for it.  In that case, inherit
        // identity from a confidently matched containing cell, but only when the number of direct
        // nested tables in that cell is unchanged.  This avoids reintroducing the old raw-index
        // shift bug when a sibling table was actually inserted or deleted.
        var oldTables = oldParagraphs.Where(x => x.Address.Table >= 0)
            .GroupBy(x => x.Address.Table).ToDictionary(g => g.Key, g => g.First().Address);
        var newTables = newParagraphs.Where(x => x.Address.Table >= 0)
            .GroupBy(x => x.Address.Table).ToDictionary(g => g.Key, g => g.First().Address);
        foreach (var (oldTable, oldAddress) in oldTables.OrderBy(x => x.Key))
        {
            if (result.ContainsKey(oldTable) || oldAddress.ParentTable < 0 ||
                oldAddress.ParentRow < 0 || oldAddress.ParentCell < 0 || oldAddress.NestedTableOrdinal < 0)
                continue;
            if (!cellMap.TryGetValue((oldAddress.ParentTable, oldAddress.ParentRow, oldAddress.ParentCell),
                    out var newParentCell))
                continue;

            var oldSiblings = oldTables.Values
                .Where(a => a.ParentTable == oldAddress.ParentTable && a.ParentRow == oldAddress.ParentRow &&
                            a.ParentCell == oldAddress.ParentCell)
                .Select(a => a.Table).Distinct().Count();
            var newSiblingAddresses = newTables.Values
                .Where(a => a.ParentTable == newParentCell.Table && a.ParentRow == newParentCell.Row &&
                            a.ParentCell == newParentCell.Cell)
                .GroupBy(a => a.Table).Select(g => g.First()).ToList();
            if (oldSiblings != newSiblingAddresses.Count || oldSiblings != 1) continue;

            var candidate = newSiblingAddresses[0];
            if (usedNew.Contains(candidate.Table)) continue;
            result[oldTable] = candidate.Table;
            usedNew.Add(candidate.Table);
        }
        return result;
    }

    private static Dictionary<(int Table, int Row), (int Table, int Row)> BuildMatchedRowMap(
        IReadOnlyList<ParagraphSource> oldParagraphs, IReadOnlyList<ParagraphEntry> newParagraphs,
        IReadOnlyList<ParagraphOp> ops)
    {
        var votes = new Dictionary<((int Table, int Row) Old, (int Table, int Row) New), int>();
        foreach (var op in ops.Where(x => x.Old >= 0 && x.New >= 0))
        {
            var oa = oldParagraphs[op.Old].Address;
            var nb = newParagraphs[op.New].Address;
            if (oa.Table < 0 || oa.Row < 0 || nb.Table < 0 || nb.Row < 0) continue;
            var key = ((oa.Table, oa.Row), (nb.Table, nb.Row));
            votes[key] = votes.TryGetValue(key, out var count) ? count + 1 : 1;
        }
        var result = new Dictionary<(int Table, int Row), (int Table, int Row)>();
        var usedNew = new HashSet<(int Table, int Row)>();
        foreach (var candidate in votes.OrderByDescending(x => x.Value)
                     .ThenBy(x => x.Key.Old.Table).ThenBy(x => x.Key.Old.Row))
        {
            if (result.ContainsKey(candidate.Key.Old) || usedNew.Contains(candidate.Key.New)) continue;
            result[candidate.Key.Old] = candidate.Key.New;
            usedNew.Add(candidate.Key.New);
        }
        return result;
    }

    private static Dictionary<(int Table, int Row, int Cell), (int Table, int Row, int Cell)> BuildMatchedCellMap(
        IReadOnlyList<ParagraphSource> oldParagraphs, IReadOnlyList<ParagraphEntry> newParagraphs,
        IReadOnlyList<ParagraphOp> ops)
    {
        var votes = new Dictionary<((int Table, int Row, int Cell) Old, (int Table, int Row, int Cell) New), int>();
        foreach (var op in ops.Where(x => x.Old >= 0 && x.New >= 0))
        {
            var oa = oldParagraphs[op.Old].Address;
            var nb = newParagraphs[op.New].Address;
            if (oa.Table < 0 || oa.Row < 0 || oa.Cell < 0 || nb.Table < 0 || nb.Row < 0 || nb.Cell < 0) continue;
            var key = ((oa.Table, oa.Row, oa.Cell), (nb.Table, nb.Row, nb.Cell));
            votes[key] = votes.TryGetValue(key, out var count) ? count + 1 : 1;
        }
        var result = new Dictionary<(int Table, int Row, int Cell), (int Table, int Row, int Cell)>();
        var usedNew = new HashSet<(int Table, int Row, int Cell)>();
        foreach (var candidate in votes.OrderByDescending(x => x.Value)
                     .ThenBy(x => x.Key.Old.Table).ThenBy(x => x.Key.Old.Row).ThenBy(x => x.Key.Old.Cell))
        {
            if (result.ContainsKey(candidate.Key.Old) || usedNew.Contains(candidate.Key.New)) continue;
            result[candidate.Key.Old] = candidate.Key.New;
            usedNew.Add(candidate.Key.New);
        }
        return result;
    }

    private static Dictionary<ParagraphAddress, ParagraphAddress> BuildMatchedParagraphMap(
        IReadOnlyList<ParagraphSource> oldParagraphs, IReadOnlyList<ParagraphEntry> newParagraphs,
        IReadOnlyList<ParagraphOp> ops)
    {
        var result = new Dictionary<ParagraphAddress, ParagraphAddress>();
        foreach (var op in ops.Where(x => x.Old >= 0 && x.New >= 0))
        {
            var oldAddress = oldParagraphs[op.Old].Address;
            var newAddress = newParagraphs[op.New].Address;
            if (oldAddress.Table < 0 || oldAddress.Row < 0 || oldAddress.Cell < 0 ||
                newAddress.Table < 0 || newAddress.Row < 0 || newAddress.Cell < 0)
                continue;
            result[oldAddress] = newAddress;
        }
        return result;
    }

    private static List<XElement> BodyParagraphs(XElement body, XNamespace w) =>
        body.Descendants(w + "p")
            .Where(p => !p.Ancestors(w + "del").Any() && !p.Ancestors(w + "moveFrom").Any())
            .ToList();

    private static string ParagraphContainerKind(XElement paragraph, XElement body, XNamespace w)
    {
        if (paragraph.Ancestors(w + "tc").Any()) return "table";
        if (paragraph.Ancestors(w + "txbxContent").Any()) return "textbox";
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

    private static int RefIndex(IEnumerable<XElement> items, XElement target)
    {
        var i = 0;
        foreach (var item in items) { if (ReferenceEquals(item, target)) return i; i++; }
        return -1;
    }

    private static List<XElement> TableRows(XElement table, XNamespace w) =>
        NativeDocumentReader.EnumerateTransparentChildren(table, w + "tr", w).ToList();

    private static List<XElement> RowCells(XElement row, XNamespace w) =>
        NativeDocumentReader.EnumerateTransparentChildren(row, w + "tc", w).ToList();

    private static XElement StructuralChildBoundary(XElement element, XElement container)
    {
        var current = element;
        while (current.Parent is XElement parent && parent != container) current = parent;
        return current.Parent == container ? current : element;
    }

    private static ParagraphAddress AddressOf(XElement paragraph, XElement body, XNamespace w)
    {
        var top = TopLevelBodyBlock(paragraph, body);
        var topIndex = RefIndex(body.Elements(), top);
        var table = paragraph.Ancestors(w + "tbl").FirstOrDefault();
        if (table is null) return new ParagraphAddress(topIndex, -1, -1, -1, -1, -1, -1, -1, -1);
        var tables = body.Descendants(w + "tbl").ToList();
        var tableIndex = RefIndex(tables, table);
        var row = paragraph.Ancestors(w + "tr").FirstOrDefault();
        var cell = paragraph.Ancestors(w + "tc").FirstOrDefault();
        var rowIndex = row is null ? -1 : RefIndex(TableRows(table, w), row);
        var cellIndex = row is null || cell is null ? -1 : RefIndex(RowCells(row, w), cell);
        var paragraphIndex = cell is null ? -1 : RefIndex(
            cell.Descendants(w + "p").Where(p => ReferenceEquals(p.Ancestors(w + "tc").FirstOrDefault(), cell)), paragraph);

        var parentTable = table.Ancestors(w + "tbl").FirstOrDefault();
        var parentCell = table.Ancestors(w + "tc").FirstOrDefault();
        var parentRow = parentCell?.Ancestors(w + "tr").FirstOrDefault();
        var parentTableIndex = parentTable is null ? -1 : RefIndex(tables, parentTable);
        var parentRowIndex = parentTable is null || parentRow is null ? -1 : RefIndex(TableRows(parentTable, w), parentRow);
        var parentCellIndex = parentRow is null || parentCell is null ? -1 : RefIndex(RowCells(parentRow, w), parentCell);
        var nestedTableOrdinal = parentCell is null ? -1 : RefIndex(
            parentCell.Descendants(w + "tbl")
                .Where(t => ReferenceEquals(t.Ancestors(w + "tc").FirstOrDefault(), parentCell)), table);
        return new ParagraphAddress(topIndex, tableIndex, rowIndex, cellIndex, paragraphIndex,
            parentTableIndex, parentRowIndex, parentCellIndex, nestedTableOrdinal);
    }

    private static List<ParagraphEntry> BodyParagraphEntries(XElement body, XNamespace w,
        NativeDocumentReader.NumberingContext numbering) =>
        BodyParagraphs(body, w)
            .Select(p =>
            {
                var table = p.Ancestors(w + "tbl").FirstOrDefault();
                var row = p.Ancestors(w + "tr").FirstOrDefault();
                var cell = p.Ancestors(w + "tc").FirstOrDefault();
                var numberInfo = NativeDocumentReader.NumberInfo(p, numbering, w);
                return new ParagraphEntry(p, ParagraphVisibleText(p, w), ParagraphContainerKind(p, body, w),
                    TopLevelBodyBlock(p, body), AddressOf(p, body, w), table, row, cell, numberInfo);
            }).ToList();

    private static List<ParagraphSource>? ReadDocxParagraphSourcesFromPath(string path, XNamespace w)
    {
        if (!Path.GetExtension(path).Equals(".docx", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return null;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        var entry = zip.GetEntry("word/document.xml"); if (entry is null) return null;
        using var input = entry.Open(); var doc = XDocument.Load(input, LoadOptions.PreserveWhitespace);
        var body = doc.Root?.Element(w + "body"); if (body is null) return null;
        var numbering = NativeDocumentReader.BuildNumberingContext(zip, w);
        return BodyParagraphs(body, w)
            .Select(p => new ParagraphSource(ParagraphVisibleText(p, w), ParagraphContainerKind(p, body, w),
                AddressOf(p, body, w), NativeDocumentReader.NumberInfo(p, numbering, w)))
            .Where(x => !string.IsNullOrWhiteSpace(x.Text))
            .ToList();
    }

    private static string ParagraphVisibleText(XElement paragraph, XNamespace w) =>
        NativeDocumentReader.ParagraphVisibleText(paragraph, w);

    private static List<RunMap> BuildRunMap(XElement paragraph, XNamespace w)
    {
        var result = new List<RunMap>(); var pos = 0;
        foreach (var run in NativeDocumentReader.VisibleRunsInParagraph(paragraph, w).ToList())
        {
            var text = NativeDocumentReader.RunVisibleText(run, w);
            if (text.Length == 0) continue;
            result.Add(new RunMap(run, pos, pos + text.Length, text));
            pos += text.Length;
        }
        return result;
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

    private static XElement RunWithChild(XElement sourceRun, XElement child, XNamespace w)
    {
        var run = new XElement(w + "r");
        var rPr = sourceRun.Element(w + "rPr");
        if (rPr is not null) run.Add(new XElement(rPr));
        run.Add(child);
        return run;
    }

    private static bool Covered(int start, int end, IReadOnlyList<(int Start, int End)> ranges) =>
        ranges.Any(r => r.Start < end && r.End > start);

    private static IEnumerable<object> SplitRunForInsertions(RunMap map, IReadOnlyList<(int Start, int End)> local,
        string author, ref int id, XNamespace w)
    {
        var pieces = new List<object>(); var cursor = 0;
        foreach (var child in map.Run.Elements().Where(x => x.Name != w + "rPr"))
        {
            if (child.Name == w + "t")
            {
                var value = child.Value; var childStart = cursor; var childEnd = cursor + value.Length;
                var cuts = local.SelectMany(r => new[] { Math.Max(childStart, r.Start), Math.Min(childEnd, r.End) })
                    .Where(x => x > childStart && x < childEnd).Append(childStart).Append(childEnd).Distinct().OrderBy(x => x).ToList();
                for (var k = 0; k + 1 < cuts.Count; k++)
                {
                    var a = cuts[k]; var b = cuts[k + 1]; if (b <= a) continue;
                    var run = RunFragment(map.Run, value[(a - childStart)..(b - childStart)], w);
                    if (Covered(a, b, local)) pieces.Add(new XElement(w + "ins", RevisionAttrs(w, id++, author), run));
                    else pieces.Add(run);
                }
                cursor = childEnd;
                continue;
            }
            if (child.Name == w + "tab" || child.Name == w + "br" || child.Name == w + "cr")
            {
                var run = RunWithChild(map.Run, new XElement(child), w);
                if (Covered(cursor, cursor + 1, local)) pieces.Add(new XElement(w + "ins", RevisionAttrs(w, id++, author), run));
                else pieces.Add(run);
                cursor++;
                continue;
            }

            // Fields, drawings and other non-text run children are copied untouched and never
            // turned into insertions merely because adjacent text changed.
            pieces.Add(RunWithChild(map.Run, new XElement(child), w));
        }
        return pieces;
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
            var pieces = SplitRunForInsertions(map, local, author, ref id, w).ToList();
            if (pieces.Count > 0) map.Run.ReplaceWith(pieces);
        }
    }

    private static XElement DirectParagraphChild(XElement run, XElement paragraph)
    {
        var current = run;
        while (current.Parent is XElement parent && parent != paragraph) current = parent;
        return current.Parent == paragraph ? current : run;
    }

    private static List<object> SplitRunAtDeletion(RunMap map, int local, XElement delNode, XNamespace w)
    {
        var pieces = new List<object>(); var cursor = 0; var inserted = false;
        foreach (var child in map.Run.Elements().Where(x => x.Name != w + "rPr"))
        {
            if (child.Name == w + "t")
            {
                var value = child.Value; var end = cursor + value.Length;
                if (!inserted && local >= cursor && local <= end)
                {
                    var cut = Math.Clamp(local - cursor, 0, value.Length);
                    if (cut > 0) pieces.Add(RunFragment(map.Run, value[..cut], w));
                    pieces.Add(delNode); inserted = true;
                    if (cut < value.Length) pieces.Add(RunFragment(map.Run, value[cut..], w));
                }
                else pieces.Add(RunFragment(map.Run, value, w));
                cursor = end; continue;
            }
            if (child.Name == w + "tab" || child.Name == w + "br" || child.Name == w + "cr")
            {
                if (!inserted && local == cursor) { pieces.Add(delNode); inserted = true; }
                pieces.Add(RunWithChild(map.Run, new XElement(child), w)); cursor++; continue;
            }
            pieces.Add(RunWithChild(map.Run, new XElement(child), w));
        }
        if (!inserted) pieces.Add(delNode);
        return pieces;
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
        var targetIndex = maps.FindIndex(x => offset < x.End);
        if (targetIndex < 0) targetIndex = maps.Count - 1;
        var target = maps[targetIndex];
        var local = Math.Clamp(offset - target.Start, 0, target.Text.Length);
        var delNode = new XElement(w + "del", RevisionAttrs(w, id++, author), RunFragment(target.Run, deletedText, w, deleted: true));
        if (local > 0 && local < target.Text.Length)
        {
            target.Run.ReplaceWith(SplitRunAtDeletion(target, local, delNode, w));
        }
        else if (local <= 0)
        {
            var boundary = DirectParagraphChild(target.Run, paragraph);
            var previous = targetIndex > 0 && maps[targetIndex - 1].End == offset ? maps[targetIndex - 1] : null;
            if (previous is not null && ReferenceEquals(DirectParagraphChild(previous.Run, paragraph), boundary))
                target.Run.AddBeforeSelf(delNode);
            else
                boundary.AddBeforeSelf(delNode);
        }
        else
        {
            DirectParagraphChild(target.Run, paragraph).AddAfterSelf(delNode);
        }
    }

    private static XElement EnsureParagraphProperties(XElement paragraph, XNamespace w)
    {
        var pPr = paragraph.Element(w + "pPr");
        if (pPr is not null) return pPr;
        pPr = new XElement(w + "pPr");
        paragraph.AddFirst(pPr);
        return pPr;
    }

    private static XElement EnsureNumberingProperties(XElement paragraph,
        NativeDocumentReader.ParagraphNumberInfo current, XNamespace w)
    {
        var pPr = EnsureParagraphProperties(paragraph, w);
        var numPr = pPr.Element(w + "numPr");
        if (numPr is not null) return numPr;
        numPr = new XElement(w + "numPr");
        if (current.NumId is int numId && numId != 0)
        {
            numPr.Add(new XElement(w + "ilvl", new XAttribute(w + "val", current.Level)));
            numPr.Add(new XElement(w + "numId", new XAttribute(w + "val", numId)));
        }
        var afterNumPr = new HashSet<string>(StringComparer.Ordinal)
        {
            "suppressLineNumbers","pBdr","shd","tabs","suppressAutoHyphens","kinsoku","wordWrap",
            "overflowPunct","topLinePunct","autoSpaceDE","autoSpaceDN","bidi","adjustRightInd","snapToGrid",
            "spacing","ind","contextualSpacing","mirrorIndents","suppressOverlap","jc","textDirection",
            "textAlignment","textboxTightWrap","outlineLvl","divId","cnfStyle","rPr","sectPr","pPrChange"
        };
        var before = pPr.Elements().FirstOrDefault(x => afterNumPr.Contains(x.Name.LocalName));
        if (before is null) pPr.Add(numPr); else before.AddBeforeSelf(numPr);
        return numPr;
    }

    private static void ApplyTrackedNumberingDiff(XElement paragraph,
        NativeDocumentReader.ParagraphNumberInfo oldInfo, NativeDocumentReader.ParagraphNumberInfo newInfo,
        string author, ref int id, XNamespace w)
    {
        var oldLabel = (oldInfo.Label ?? string.Empty).Trim();
        var newLabel = (newInfo.Label ?? string.Empty).Trim();
        if (string.Equals(oldLabel, newLabel, StringComparison.Ordinal)) return;
        if (oldLabel.Length == 0 && newLabel.Length == 0) return;

        var numPr = EnsureNumberingProperties(paragraph, newInfo, w);
        if (oldLabel.Length == 0)
        {
            if (numPr.Element(w + "ins") is null)
                numPr.Add(new XElement(w + "ins", RevisionAttrs(w, id++, author)));
            return;
        }

        if (numPr.Element(w + "numberingChange") is null)
            numPr.Add(new XElement(w + "numberingChange", RevisionAttrs(w, id++, author),
                new XAttribute(w + "original", SanitizeXmlText(oldLabel))));
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
        MarkParagraphMarkRevision(paragraph, inserted: true, author, ref id, w);
    }

    private static void MarkTableRowRevision(XElement row, bool inserted, string author, ref int id, XNamespace w)
    {
        var trPr = row.Element(w + "trPr");
        if (trPr is null) { trPr = new XElement(w + "trPr"); row.AddFirst(trPr); }
        var kind = inserted ? w + "ins" : w + "del";
        if (trPr.Element(kind) is not null) return;
        var revision = new XElement(kind, RevisionAttrs(w, id++, author));
        var before = trPr.Element(w + "trPrChange");
        if (before is null) trPr.Add(revision); else before.AddBeforeSelf(revision);
    }

    private static void MarkTableCellRevision(XElement cell, bool inserted, string author, ref int id, XNamespace w)
    {
        var tcPr = cell.Element(w + "tcPr");
        if (tcPr is null) { tcPr = new XElement(w + "tcPr"); cell.AddFirst(tcPr); }
        var kind = inserted ? w + "cellIns" : w + "cellDel";
        if (tcPr.Element(kind) is not null) return;
        var revision = new XElement(kind, RevisionAttrs(w, id++, author));
        // cellIns/cellDel are late tcPr children; keep tcPrChange last when present.
        var before = tcPr.Element(w + "tcPrChange");
        if (before is null) tcPr.Add(revision); else before.AddBeforeSelf(revision);
    }

    private static void InsertDeletedTableCell(IReadOnlyList<ParagraphEntry> revisedParagraphs,
        IReadOnlyList<ParagraphSource> sources,
        IReadOnlyDictionary<(int Table, int Row), (int Table, int Row)> rowMap,
        IReadOnlyDictionary<(int Table, int Row, int Cell), (int Table, int Row, int Cell)> cellMap,
        string author, ref int id, XNamespace w)
    {
        if (sources.Count == 0) return;
        var key = sources[0].Address;
        if (!rowMap.TryGetValue((key.Table, key.Row), out var revisedRow)) return;
        var row = revisedParagraphs.FirstOrDefault(x => x.Address.Table == revisedRow.Table &&
            x.Address.Row == revisedRow.Row && x.Row is not null)?.Row;
        if (row is null) return;
        var cells = RowCells(row, w);

        XElement? before = null; XElement? after = null;
        var mappedAfter = cellMap.Where(x => x.Key.Table == key.Table && x.Key.Row == key.Row && x.Key.Cell > key.Cell &&
                                           x.Value.Table == revisedRow.Table && x.Value.Row == revisedRow.Row)
            .OrderBy(x => x.Key.Cell).Select(x => ((int Table, int Row, int Cell)?)x.Value).FirstOrDefault();
        if (mappedAfter is { } afterAddress)
            before = revisedParagraphs.FirstOrDefault(x => x.Address.Table == afterAddress.Table && x.Address.Row == afterAddress.Row &&
                x.Address.Cell == afterAddress.Cell && x.Cell is not null)?.Cell;
        var mappedBefore = cellMap.Where(x => x.Key.Table == key.Table && x.Key.Row == key.Row && x.Key.Cell < key.Cell &&
                                            x.Value.Table == revisedRow.Table && x.Value.Row == revisedRow.Row)
            .OrderByDescending(x => x.Key.Cell).Select(x => ((int Table, int Row, int Cell)?)x.Value).FirstOrDefault();
        if (mappedBefore is { } beforeAddress)
            after = revisedParagraphs.FirstOrDefault(x => x.Address.Table == beforeAddress.Table && x.Address.Row == beforeAddress.Row &&
                x.Address.Cell == beforeAddress.Cell && x.Cell is not null)?.Cell;

        var template = before ?? after ?? (cells.Count == 0 ? null : cells[Math.Clamp(key.Cell, 0, cells.Count - 1)]);
        var cell = new XElement(w + "tc");
        var tcPr = template?.Element(w + "tcPr"); if (tcPr is not null) cell.Add(new XElement(tcPr));
        MarkTableCellRevision(cell, inserted: false, author, ref id, w);
        var pPr = template?.Descendants(w + "p").FirstOrDefault()?.Element(w + "pPr");
        foreach (var source in sources.OrderBy(x => x.Address.Paragraph))
            cell.Add(PlainParagraph(source.Text, pPr is null ? null : new XElement(pPr)));
        if (!cell.Elements(w + "p").Any()) cell.Add(new XElement(w + "p"));

        if (before is not null) StructuralChildBoundary(before, row).AddBeforeSelf(cell);
        else if (after is not null) StructuralChildBoundary(after, row).AddAfterSelf(cell);
        else if (cells.Count == 0) row.Add(cell);
        else if (key.Cell >= 0 && key.Cell < cells.Count) StructuralChildBoundary(cells[key.Cell], row).AddBeforeSelf(cell);
        else StructuralChildBoundary(cells[^1], row).AddAfterSelf(cell);
    }

    private static void InsertDeletedTableRow(IReadOnlyList<ParagraphEntry> revisedParagraphs,
        IReadOnlyList<ParagraphSource> sources, IReadOnlyDictionary<int, int> tableMap,
        IReadOnlyDictionary<(int Table, int Row), (int Table, int Row)> rowMap,
        string author, ref int id, XNamespace w)
    {
        if (sources.Count == 0) return;
        var key = sources[0].Address;
        if (!tableMap.TryGetValue(key.Table, out var revisedTableIndex)) return;
        var table = revisedParagraphs.FirstOrDefault(x => x.Address.Table == revisedTableIndex && x.Table is not null)?.Table;
        if (table is null) return;

        XElement? before = null; XElement? after = null;
        var mappedAfter = rowMap.Where(x => x.Key.Table == key.Table && x.Key.Row > key.Row &&
                                          x.Value.Table == revisedTableIndex)
            .OrderBy(x => x.Key.Row).Select(x => ((int Table, int Row)?)x.Value).FirstOrDefault();
        if (mappedAfter is { } afterAddress)
            before = revisedParagraphs.FirstOrDefault(x => x.Address.Table == afterAddress.Table &&
                x.Address.Row == afterAddress.Row && x.Row is not null)?.Row;
        var mappedBefore = rowMap.Where(x => x.Key.Table == key.Table && x.Key.Row < key.Row &&
                                           x.Value.Table == revisedTableIndex)
            .OrderByDescending(x => x.Key.Row).Select(x => ((int Table, int Row)?)x.Value).FirstOrDefault();
        if (mappedBefore is { } beforeAddress)
            after = revisedParagraphs.FirstOrDefault(x => x.Address.Table == beforeAddress.Table &&
                x.Address.Row == beforeAddress.Row && x.Row is not null)?.Row;

        var existingRows = TableRows(table, w);
        XElement? templateRow = before ?? after ??
            (existingRows.Count == 0 ? null : existingRows[Math.Clamp(key.Row, 0, existingRows.Count - 1)]);
        var templateCells = templateRow is null ? new List<XElement>() : RowCells(templateRow, w);
        var maxCell = Math.Max(0, sources.Max(x => x.Address.Cell));
        var row = new XElement(w + "tr");
        MarkTableRowRevision(row, inserted: false, author, ref id, w);
        for (var c = 0; c <= maxCell; c++)
        {
            var tc = new XElement(w + "tc");
            var templateCell = templateCells.Count == 0 ? null : templateCells[Math.Min(c, templateCells.Count - 1)];
            var tcPr = templateCell?.Element(w + "tcPr"); if (tcPr is not null) tc.Add(new XElement(tcPr));
            var cellSources = sources.Where(x => x.Address.Cell == c).OrderBy(x => x.Address.Paragraph).ToList();
            if (cellSources.Count == 0) tc.Add(new XElement(w + "p"));
            else
            {
                var pPr = templateCell?.Descendants(w + "p").FirstOrDefault()?.Element(w + "pPr");
                foreach (var source in cellSources)
                    tc.Add(PlainParagraph(source.Text, pPr is null ? null : new XElement(pPr)));
            }
            row.Add(tc);
        }

        existingRows = TableRows(table, w);
        if (before is not null) StructuralChildBoundary(before, table).AddBeforeSelf(row);
        else if (after is not null) StructuralChildBoundary(after, table).AddAfterSelf(row);
        else if (existingRows.Count == 0) table.Add(row);
        else if (key.Row >= 0 && key.Row < existingRows.Count) StructuralChildBoundary(existingRows[key.Row], table).AddBeforeSelf(row);
        else StructuralChildBoundary(existingRows[^1], table).AddAfterSelf(row);
    }

    private static bool TryInsertDeletedTableParagraph(IReadOnlyList<ParagraphEntry> revisedParagraphs,
        ParagraphSource source,
        IReadOnlyDictionary<(int Table, int Row, int Cell), (int Table, int Row, int Cell)> cellMap,
        IReadOnlyDictionary<ParagraphAddress, ParagraphAddress> paragraphMap,
        string author, ref int id, XNamespace w)
    {
        if (source.Address.Table < 0 || source.Address.Row < 0 || source.Address.Cell < 0) return false;
        if (!cellMap.TryGetValue((source.Address.Table, source.Address.Row, source.Address.Cell), out var revisedCell)) return false;
        var sameCell = revisedParagraphs
            .Where(x => x.Address.Table == revisedCell.Table && x.Address.Row == revisedCell.Row &&
                        x.Address.Cell == revisedCell.Cell && x.Cell is not null)
            .OrderBy(x => x.Address.Paragraph).ToList();
        if (sameCell.Count == 0) return false;

        ParagraphEntry? before = null; ParagraphEntry? after = null;
        var mappedAfter = paragraphMap
            .Where(x => x.Key.Table == source.Address.Table && x.Key.Row == source.Address.Row &&
                        x.Key.Cell == source.Address.Cell && x.Key.Paragraph > source.Address.Paragraph)
            .OrderBy(x => x.Key.Paragraph).Select(x => (ParagraphAddress?)x.Value).FirstOrDefault();
        if (mappedAfter is not null)
            before = sameCell.FirstOrDefault(x => x.Address == mappedAfter);
        var mappedBefore = paragraphMap
            .Where(x => x.Key.Table == source.Address.Table && x.Key.Row == source.Address.Row &&
                        x.Key.Cell == source.Address.Cell && x.Key.Paragraph < source.Address.Paragraph)
            .OrderByDescending(x => x.Key.Paragraph).Select(x => (ParagraphAddress?)x.Value).FirstOrDefault();
        if (mappedBefore is not null)
            after = sameCell.FirstOrDefault(x => x.Address == mappedBefore);

        var target = before ?? after ??
            sameCell.FirstOrDefault(x => x.Address.Paragraph >= source.Address.Paragraph) ?? sameCell[^1];
        var pPr = target.Paragraph.Element(w + "pPr");
        var deleted = DeletedParagraph(source.Text, author, ref id, pPr is null ? null : new XElement(pPr));
        if (before is not null) before.Paragraph.AddBeforeSelf(deleted);
        else if (after is not null) after.Paragraph.AddAfterSelf(deleted);
        else if (target.Address.Paragraph >= source.Address.Paragraph) target.Paragraph.AddBeforeSelf(deleted);
        else target.Paragraph.AddAfterSelf(deleted);
        return true;
    }

    private static void InsertDeletedParagraphRange(XElement body, IReadOnlyList<ParagraphEntry> revisedParagraphs,
        int nextBIndex, IReadOnlyList<ParagraphSource> deletedSources, IReadOnlySet<(int Table, int Row)> fullyDeletedRows,
        IReadOnlySet<(int Table, int Row, int Cell)> fullyDeletedCells,
        IReadOnlyDictionary<int, int> tableMap,
        IReadOnlyDictionary<(int Table, int Row), (int Table, int Row)> rowMap,
        IReadOnlyDictionary<(int Table, int Row, int Cell), (int Table, int Row, int Cell)> cellMap,
        IReadOnlyDictionary<ParagraphAddress, ParagraphAddress> paragraphMap,
        string author, ref int id, XNamespace w)
    {
        if (deletedSources.Count == 0) return;

        var i = 0;
        XElement? fallbackAfterCursor = null;
        XElement? fallbackAfterBoundary = null;
        while (i < deletedSources.Count)
        {
            var source = deletedSources[i];
            var key = (source.Address.Table, source.Address.Row);
            if (source.ContainerKind == "table" && fullyDeletedRows.Contains(key))
            {
                var group = new List<ParagraphSource>();
                while (i < deletedSources.Count && deletedSources[i].Address.Table == key.Table && deletedSources[i].Address.Row == key.Row)
                    group.Add(deletedSources[i++]);
                var before = id;
                InsertDeletedTableRow(revisedParagraphs, group, tableMap, rowMap, author, ref id, w);
                if (id != before) continue;
                // If the corresponding B table no longer exists, fall through to safe top-level
                // deleted paragraphs rather than injecting content into an unrelated surviving cell.
                foreach (var item in group)
                    InsertDeletedBodyFallback(body, revisedParagraphs, nextBIndex, item, author, ref id, w,
                        ref fallbackAfterCursor, ref fallbackAfterBoundary);
                continue;
            }
            var cellKey = (source.Address.Table, source.Address.Row, source.Address.Cell);
            if (source.ContainerKind == "table" && fullyDeletedCells.Contains(cellKey))
            {
                var group = new List<ParagraphSource>();
                while (i < deletedSources.Count && deletedSources[i].Address.Table == cellKey.Table &&
                       deletedSources[i].Address.Row == cellKey.Row && deletedSources[i].Address.Cell == cellKey.Cell)
                    group.Add(deletedSources[i++]);
                var before = id;
                InsertDeletedTableCell(revisedParagraphs, group, rowMap, cellMap, author, ref id, w);
                if (id != before) continue;
                foreach (var item in group)
                    InsertDeletedBodyFallback(body, revisedParagraphs, nextBIndex, item, author, ref id, w,
                        ref fallbackAfterCursor, ref fallbackAfterBoundary);
                continue;
            }
            if (source.ContainerKind == "table" && TryInsertDeletedTableParagraph(revisedParagraphs, source, cellMap, paragraphMap, author, ref id, w))
            { i++; continue; }
            InsertDeletedBodyFallback(body, revisedParagraphs, nextBIndex, source, author, ref id, w,
                ref fallbackAfterCursor, ref fallbackAfterBoundary);
            i++;
        }
    }

    private static void InsertDeletedBodyFallback(XElement body, IReadOnlyList<ParagraphEntry> revisedParagraphs,
        int nextBIndex, ParagraphSource source, string author, ref int id, XNamespace w,
        ref XElement? afterCursor, ref XElement? afterBoundary)
    {
        if (revisedParagraphs.Count == 0)
        {
            var deleted = DeletedParagraph(source.Text, author, ref id, null);
            var sectPr = body.Elements(w + "sectPr").LastOrDefault();
            if (afterCursor is not null) afterCursor.AddAfterSelf(deleted);
            else if (sectPr is null) body.Add(deleted); else sectPr.AddBeforeSelf(deleted);
            afterCursor = deleted; afterBoundary = body;
            return;
        }
        var pastEnd = nextBIndex >= revisedParagraphs.Count;
        var anchor = revisedParagraphs[pastEnd ? revisedParagraphs.Count - 1 : nextBIndex];
        var styleSource = anchor.Paragraph.Element(w + "pPr");
        var deletedP = DeletedParagraph(source.Text, author, ref id, styleSource is null ? null : new XElement(styleSource));
        // Never inject a fallback deletion into a table/textbox/SDT merely because it is the
        // nearest visible paragraph; use the top-level B block as the safe structural boundary.
        var boundary = anchor.TopLevelBlock;
        if (!pastEnd)
        {
            boundary.AddBeforeSelf(deletedP); // repeated AddBeforeSelf preserves source order
            return;
        }
        if (afterCursor is not null && ReferenceEquals(afterBoundary, boundary))
            afterCursor.AddAfterSelf(deletedP);
        else
            boundary.AddAfterSelf(deletedP);
        afterCursor = deletedP;
        afterBoundary = boundary;
    }

    private static bool IsTrackedRevisionElement(XElement element)
    {
        var local = element.Name.LocalName;
        return TrackedRevisionNames.Contains(local) || local.EndsWith("Change", StringComparison.Ordinal);
    }

    private static bool HasTrackedRevision(XDocument doc, XNamespace w) =>
        doc.Descendants().Any(IsTrackedRevisionElement);

    private static void EnsureNoExistingTrackedRevisions(string revisedPath)
    {
        using var fs = new FileStream(revisedPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        XNamespace w = W;
        foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith("word/", StringComparison.OrdinalIgnoreCase) &&
                                                     e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
        {
            if (entry.Length > 64L * 1024 * 1024)
                throw new InvalidDataException($"DOCX 내부 XML이 너무 큽니다: {entry.FullName} ({entry.Length:N0} bytes)");
            XDocument doc;
            using (var input = entry.Open()) doc = XDocument.Load(input, LoadOptions.PreserveWhitespace);
            if (HasTrackedRevision(doc, w))
                throw new InvalidOperationException($"최종 문서(B)의 {entry.FullName}에 기존 Word 변경추적이 남아 있습니다. 기존 변경사항을 모두 수락/거부한 사본을 B로 사용한 뒤 다시 내보내세요.");
        }
    }

    private static string AncillaryPartKind(string name)
    {
        var file = Path.GetFileName(name).ToLowerInvariant();
        if (file.StartsWith("header", StringComparison.Ordinal) && file.EndsWith(".xml", StringComparison.Ordinal)) return "header";
        if (file.StartsWith("footer", StringComparison.Ordinal) && file.EndsWith(".xml", StringComparison.Ordinal)) return "footer";
        if (file == "footnotes.xml") return "footnotes";
        if (file == "endnotes.xml") return "endnotes";
        return string.Empty;
    }

    private static string RelationshipPartName(string partName)
    {
        var slash = partName.LastIndexOf('/');
        var dir = slash >= 0 ? partName[..(slash + 1)] : string.Empty;
        var file = slash >= 0 ? partName[(slash + 1)..] : partName;
        return dir + "_rels/" + file + ".rels";
    }

    private static string ResolvePackageTarget(string sourcePartName, string target)
    {
        target = Uri.UnescapeDataString((target ?? string.Empty).Replace('\\', '/'));
        var pieces = new List<string>();
        if (!target.StartsWith('/'))
        {
            var slash = sourcePartName.LastIndexOf('/');
            if (slash >= 0)
                pieces.AddRange(sourcePartName[..slash].Split('/', StringSplitOptions.RemoveEmptyEntries));
        }
        foreach (var piece in target.TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (piece == ".") continue;
            if (piece == "..")
            {
                if (pieces.Count > 0) pieces.RemoveAt(pieces.Count - 1);
                continue;
            }
            pieces.Add(piece);
        }
        return string.Join("/", pieces);
    }

    private static string EntrySha256(ZipArchiveEntry entry)
    {
        if (entry.Length > 128L * 1024 * 1024)
            throw new InvalidDataException($"DOCX 관계 대상이 너무 큽니다: {entry.FullName} ({entry.Length:N0} bytes)");
        using var stream = entry.Open();
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static Dictionary<string, string> ReadPartRelationshipSignatures(
        ZipArchive zip, string sourcePartName)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var relEntry = zip.GetEntry(RelationshipPartName(sourcePartName));
        if (relEntry is null) return result;
        if (relEntry.Length > 8L * 1024 * 1024)
            throw new InvalidDataException($"DOCX 관계 XML이 너무 큽니다: {relEntry.FullName} ({relEntry.Length:N0} bytes)");
        XNamespace pr = "http://schemas.openxmlformats.org/package/2006/relationships";
        XDocument rels; using (var input = relEntry.Open()) rels = XDocument.Load(input, LoadOptions.PreserveWhitespace);
        foreach (var rel in rels.Root?.Elements(pr + "Relationship") ?? Enumerable.Empty<XElement>())
        {
            var id = rel.Attribute("Id")?.Value;
            if (string.IsNullOrEmpty(id)) continue;
            var type = rel.Attribute("Type")?.Value ?? string.Empty;
            var target = rel.Attribute("Target")?.Value ?? string.Empty;
            var mode = rel.Attribute("TargetMode")?.Value ?? string.Empty;
            if (mode.Equals("External", StringComparison.OrdinalIgnoreCase))
            {
                result[id] = $"external|{type}|{target}";
                continue;
            }
            var resolved = ResolvePackageTarget(sourcePartName, target);
            var targetEntry = zip.GetEntry(resolved);
            result[id] = targetEntry is null
                ? $"missing|{type}|{resolved}"
                : $"internal|{type}|{EntrySha256(targetEntry)}";
        }
        return result;
    }

    private static string AncillaryPartSemanticSignature(
        ZipArchive zip, ZipArchiveEntry entry, XDocument doc, XNamespace w)
    {
        var relationships = ReadPartRelationshipSignatures(zip, entry.FullName);
        var sb = new StringBuilder();

        static bool IgnorableIdentityAttribute(XAttribute attribute)
        {
            if (attribute.Name.NamespaceName == W && attribute.Name.LocalName.StartsWith("rsid", StringComparison.Ordinal))
                return true;
            if (attribute.Name.NamespaceName == "http://schemas.microsoft.com/office/word/2010/wordml" &&
                attribute.Name.LocalName is "paraId" or "textId")
                return true;
            return false;
        }

        void AppendElement(XElement element)
        {
            sb.Append("E{").Append(element.Name.NamespaceName).Append('}').Append(element.Name.LocalName).Append('[');
            foreach (var attribute in element.Attributes()
                         .Where(a => !a.IsNamespaceDeclaration && !IgnorableIdentityAttribute(a))
                         .OrderBy(a => a.Name.NamespaceName, StringComparer.Ordinal)
                         .ThenBy(a => a.Name.LocalName, StringComparer.Ordinal))
            {
                var value = attribute.Value;
                if (attribute.Name.NamespaceName == R)
                    value = relationships.TryGetValue(value, out var resolved) ? resolved : "missing-rel|" + value;
                sb.Append('{').Append(attribute.Name.NamespaceName).Append('}').Append(attribute.Name.LocalName)
                    .Append('=').Append(value.Length).Append(':').Append(value).Append(';');
            }
            sb.Append(']');
            foreach (var node in element.Nodes())
            {
                if (node is XElement child) AppendElement(child);
                else if (node is XText text &&
                         (!string.IsNullOrWhiteSpace(text.Value) ||
                          element.Name == w + "t" || element.Name == w + "delText" || element.Name == w + "instrText"))
                    sb.Append("T").Append(text.Value.Length).Append(':').Append(text.Value).Append(';');
            }
            sb.Append("/E;");
        }

        if (doc.Root is not null) AppendElement(doc.Root);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static Dictionary<string, string> ReadAncillarySemanticSignatures(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        XNamespace w = W;
        foreach (var entry in zip.Entries)
        {
            var kind = AncillaryPartKind(entry.FullName); if (kind.Length == 0) continue;
            if (entry.Length > 32L * 1024 * 1024)
                throw new InvalidDataException($"DOCX 부속 XML이 너무 큽니다: {entry.FullName} ({entry.Length:N0} bytes)");
            XDocument doc; using (var input = entry.Open()) doc = XDocument.Load(input, LoadOptions.PreserveWhitespace);
            result[entry.FullName] = AncillaryPartSemanticSignature(zip, entry, doc, w);
        }
        return result;
    }

    private static string ReadMainDocumentUnsupportedContentSignature(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        var documentEntry = zip.GetEntry("word/document.xml");
        if (documentEntry is null) return string.Empty;
        XNamespace w = W;
        XDocument doc; using (var input = documentEntry.Open()) doc = XDocument.Load(input, LoadOptions.PreserveWhitespace);
        var relationships = ReadPartRelationshipSignatures(zip, "word/document.xml");
        var tokens = new List<string>();

        foreach (var element in doc.Descendants())
        {
            if (element.Name == w + "headerReference" || element.Name == w + "footerReference")
                continue;

            foreach (var attribute in element.Attributes().Where(a => a.Name.NamespaceName == R))
            {
                var resolved = relationships.TryGetValue(attribute.Value, out var signature)
                    ? signature
                    : "missing-rel|" + attribute.Value;
                tokens.Add($"RELREF|{element.Name.LocalName}|{attribute.Name.LocalName}|{resolved}");
            }

            if (element.Name == w + "instrText")
            {
                var value = NativeComparisonEngine.Normalize(element.Value);
                if (value.Length > 0) tokens.Add("FIELD|" + value);
            }
            else if (element.Name == w + "fldSimple")
            {
                var value = NativeComparisonEngine.Normalize(element.Attribute(w + "instr")?.Value ?? string.Empty);
                if (value.Length > 0) tokens.Add("FIELD|" + value);
            }
            else if (element.Name == w + "sym")
            {
                var attrs = string.Join(";", element.Attributes()
                    .Where(a => !a.IsNamespaceDeclaration)
                    .OrderBy(a => a.Name.NamespaceName, StringComparer.Ordinal)
                    .ThenBy(a => a.Name.LocalName, StringComparer.Ordinal)
                    .Select(a => $"{{{a.Name.NamespaceName}}}{a.Name.LocalName}={a.Value}"));
                tokens.Add("SYM|" + attrs);
            }
        }
        return string.Join("\n", tokens);
    }

    private static void EnsureMainDocumentUnsupportedContentEquivalent(string originalPath, string revisedPath)
    {
        var a = ReadMainDocumentUnsupportedContentSignature(originalPath);
        var b = ReadMainDocumentUnsupportedContentSignature(revisedPath);
        if (!string.Equals(a, b, StringComparison.Ordinal))
            throw new InvalidOperationException("A와 B의 Word 본문 관계/필드/기호 같은 비텍스트 내용이 서로 다릅니다. 현재 변경추적 엔진이 이 변경을 안전하게 표현하지 못하므로 저장을 중단했습니다.");
    }

    private static string ReadMainDocumentAncillaryReferenceSignature(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        var documentEntry = zip.GetEntry("word/document.xml");
        if (documentEntry is null) return string.Empty;
        XNamespace w = W;
        XDocument doc; using (var input = documentEntry.Open()) doc = XDocument.Load(input, LoadOptions.PreserveWhitespace);

        var relationshipTargets = new Dictionary<string, string>(StringComparer.Ordinal);
        var relEntry = zip.GetEntry("word/_rels/document.xml.rels");
        if (relEntry is not null)
        {
            XNamespace pr = "http://schemas.openxmlformats.org/package/2006/relationships";
            XDocument rels; using (var input = relEntry.Open()) rels = XDocument.Load(input, LoadOptions.PreserveWhitespace);
            foreach (var rel in rels.Root?.Elements(pr + "Relationship") ?? Enumerable.Empty<XElement>())
            {
                var id = rel.Attribute("Id")?.Value;
                if (string.IsNullOrEmpty(id)) continue;
                var target = rel.Attribute("Target")?.Value ?? string.Empty;
                var mode = rel.Attribute("TargetMode")?.Value ?? string.Empty;
                relationshipTargets[id] = mode.Equals("External", StringComparison.OrdinalIgnoreCase)
                    ? "external:" + target
                    : ResolvePackageTarget("word/document.xml", target);
            }
        }

        var tokens = new List<string>();
        var body = doc.Root?.Element(w + "body");
        var bodyParagraphs = body is null ? new List<XElement>() : BodyParagraphs(body, w).ToList();
        var sectionProperties = doc.Descendants(w + "sectPr").ToList();
        foreach (var element in doc.Descendants())
        {
            if (element.Name == w + "headerReference" || element.Name == w + "footerReference")
            {
                var id = element.Attribute(XName.Get("id", R))?.Value ?? string.Empty;
                var target = relationshipTargets.TryGetValue(id, out var resolved) ? resolved : "missing:" + id;
                var sectPr = element.Ancestors(w + "sectPr").FirstOrDefault();
                var sectionIndex = sectPr is null ? -1 : RefIndex(sectionProperties, sectPr);
                var ownerParagraph = sectPr?.Ancestors(w + "p").FirstOrDefault();
                var paragraphIndex = ownerParagraph is null ? -1 : RefIndex(bodyParagraphs, ownerParagraph);
                tokens.Add($"{element.Name.LocalName}|section={sectionIndex}|paragraph={paragraphIndex}|{element.Attribute(w + "type")?.Value ?? string.Empty}|{target}");
            }
            else if (element.Name == w + "footnoteReference" || element.Name == w + "endnoteReference")
            {
                var ownerParagraph = element.Ancestors(w + "p").FirstOrDefault();
                var paragraphIndex = ownerParagraph is null ? -1 : RefIndex(bodyParagraphs, ownerParagraph);
                var localIndex = ownerParagraph is null ? -1 : RefIndex(
                    ownerParagraph.Descendants(element.Name)
                        .Where(x => ReferenceEquals(x.Ancestors(w + "p").FirstOrDefault(), ownerParagraph)), element);
                tokens.Add($"{element.Name.LocalName}|paragraph={paragraphIndex}|local={localIndex}|{element.Attribute(w + "id")?.Value ?? string.Empty}");
            }
        }
        return string.Join("\n", tokens);
    }

    private static void EnsureAncillaryWordPartsEquivalent(string originalPath, string revisedPath)
    {
        var aRefs = ReadMainDocumentAncillaryReferenceSignature(originalPath);
        var bRefs = ReadMainDocumentAncillaryReferenceSignature(revisedPath);
        if (!string.Equals(aRefs, bRefs, StringComparison.Ordinal))
            throw new InvalidOperationException("A와 B의 Word header/footer/footnote/endnote 참조 위치가 서로 다릅니다. 현재 변경추적 내보내기는 본문 XML을 기준으로 하므로, 부속 파트 변경을 누락한 불완전한 파일 생성을 막기 위해 저장을 중단했습니다.");

        var a = ReadAncillarySemanticSignatures(originalPath);
        var b = ReadAncillarySemanticSignatures(revisedPath);
        foreach (var part in a.Keys.Union(b.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var av = a.TryGetValue(part, out var at) ? at : string.Empty;
            var bv = b.TryGetValue(part, out var bt) ? bt : string.Empty;
            if (!string.Equals(av, bv, StringComparison.Ordinal))
            {
                var kind = AncillaryPartKind(part);
                throw new InvalidOperationException($"A와 B의 Word {kind} 내용/위치가 서로 다릅니다 ({part}). 현재 변경추적 내보내기는 본문 XML을 기준으로 하므로, {kind} 변경을 누락한 불완전한 파일 생성을 막기 위해 저장을 중단했습니다.");
            }
        }
    }

    private static int NextRevisionId(XDocument document, XNamespace w)
    {
        var max = 0;
        foreach (var e in document.Descendants().Where(IsTrackedRevisionElement))
        {
            var idText = e.Attributes().FirstOrDefault(a => a.Name.LocalName == "id")?.Value;
            if (int.TryParse(idText, out var id)) max = Math.Max(max, id);
        }
        return max + 1;
    }

    private static XElement PlainParagraph(string text, XElement? properties)
    {
        XNamespace w = W;
        return new XElement(w + "p", properties is null ? null : new XElement(properties), new XElement(w + "r", TextNode(w + "t", text)));
    }

    private static void MarkParagraphMarkRevision(XElement paragraph, bool inserted, string author, ref int id, XNamespace w)
    {
        var pPr = paragraph.Element(w + "pPr");
        if (pPr is null)
        {
            pPr = new XElement(w + "pPr");
            paragraph.AddFirst(pPr);
        }
        var rPr = pPr.Element(w + "rPr");
        if (rPr is null)
        {
            rPr = new XElement(w + "rPr");
            var before = pPr.Elements().FirstOrDefault(x => x.Name == w + "sectPr" || x.Name == w + "pPrChange");
            if (before is null) pPr.Add(rPr); else before.AddBeforeSelf(rPr);
        }
        var kind = inserted ? w + "ins" : w + "del";
        if (rPr.Element(kind) is null)
        {
            var revision = new XElement(kind, RevisionAttrs(w, id++, author));
            var before = rPr.Element(w + "rPrChange");
            if (before is null) rPr.Add(revision); else before.AddBeforeSelf(revision);
        }
    }

    private static XElement InsertedParagraph(string text, string author, ref int id, XElement? properties)
    {
        XNamespace w = W;
        var p = new XElement(w + "p", properties is null ? null : new XElement(properties),
            new XElement(w + "ins", RevisionAttrs(w, id++, author), new XElement(w + "r", TextNode(w + "t", text))));
        MarkParagraphMarkRevision(p, inserted: true, author, ref id, w);
        return p;
    }

    private static XElement DeletedParagraph(string text, string author, ref int id, XElement? properties)
    {
        XNamespace w = W;
        var p = new XElement(w + "p", properties is null ? null : new XElement(properties),
            new XElement(w + "del", RevisionAttrs(w, id++, author), new XElement(w + "r", TextNode(w + "delText", text))));
        MarkParagraphMarkRevision(p, inserted: false, author, ref id, w);
        return p;
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
