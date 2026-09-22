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

    private static void EnsureSourceStateStillMatches(SourceFileStateVm expected)
    {
        var current = NativeComparisonEngine.CaptureSourceFileState(expected.Path);
        if (!NativeComparisonEngine.SameSourceFileState(expected, current))
            throw new InvalidOperationException($"내보내기 중 입력 파일이 변경되었습니다: {Path.GetFileName(expected.Path)}. 다시 내보내세요.");
    }

    private static void EnsureComparisonSourcesStillMatch(ComparisonResultVm? result, params int[] indices)
    {
        if (result is null || result.SourceFiles.Count == 0) return;
        var check = indices.Length == 0 ? Enumerable.Range(0, result.SourceFiles.Count) : indices.Distinct();
        foreach (var index in check)
        {
            if (index < 0 || index >= result.SourceFiles.Count)
                throw new InvalidOperationException("비교 결과의 입력 파일 정보가 현재 내보내기 대상과 일치하지 않습니다.");
            var expected = result.SourceFiles[index];
            var current = NativeComparisonEngine.CaptureSourceFileState(expected.Path);
            if (!NativeComparisonEngine.SameSourceFileState(expected, current))
                throw new InvalidOperationException($"내보내기 중 입력 파일이 변경되었습니다: {Path.GetFileName(expected.Path)}. 다시 비교한 뒤 내보내세요.");
        }
    }

    public static async Task WriteXlsxAsync(ComparisonResultVm result, string outputPath, CancellationToken cancellationToken)
    {
        var temporaryPath = TemporaryOutputPath(outputPath);
        try
        {
            await Task.Run(() => WriteXlsx(result, temporaryPath, cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            EnsureComparisonSourcesStillMatch(result);
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

        // Export can also be called without a ComparisonResult (tests/API consumers). Capture
        // independent source identities so repeated path reads inside the DOCX exporter cannot
        // silently combine different file versions.
        var originalState = NativeComparisonEngine.CaptureSourceFileState(originalPath);
        var revisedState = NativeComparisonEngine.CaptureSourceFileState(revisedPath);
        var oldText = await NativeDocumentReader.ReadAsync(originalPath, cancellationToken);
        var newText = await NativeDocumentReader.ReadAsync(revisedPath, cancellationToken);
        EnsureSourceStateStillMatches(originalState);
        EnsureSourceStateStillMatches(revisedState);
        EnsureComparisonSourcesStillMatch(comparisonResult, originalDocumentIndex, revisedDocumentIndex);
        var temporaryPath = TemporaryOutputPath(outputPath);
        try
        {
            await Task.Run(() => WriteTrackedDocx(oldText, newText, originalPath, revisedPath, temporaryPath, author,
                includePunctuation, cancellationToken, comparisonResult, originalDocumentIndex, revisedDocumentIndex), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            EnsureSourceStateStillMatches(originalState);
            EnsureSourceStateStillMatches(revisedState);
            EnsureComparisonSourcesStillMatch(comparisonResult, originalDocumentIndex, revisedDocumentIndex);
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
            // DOCX -> DOCX export intentionally uses revised document B as the physical base.
            // Formatting-only differences are not comparison-content changes in this product;
            // B's styles/layout are therefore preserved and must not block tracked-text export.
            // Structural/non-text content that cannot be reproduced safely is still guarded by
            // EnsureAncillaryWordPartsEquivalent / EnsureMainDocumentUnsupportedContentEquivalent.
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

    private static XElement RunFromLogicalText(
        string text, XNamespace w, bool deleted = false, XElement? runProperties = null)
    {
        var run = new XElement(w + "r");
        if (runProperties is not null) run.Add(new XElement(runProperties));

        var buffer = new StringBuilder();
        void Flush()
        {
            if (buffer.Length == 0) return;
            run.Add(TextNode(deleted ? w + "delText" : w + "t", buffer.ToString()));
            buffer.Clear();
        }

        foreach (var ch in text ?? string.Empty)
        {
            if (ch == '\t')
            {
                Flush();
                run.Add(new XElement(w + "tab"));
            }
            else if (ch == '\n')
            {
                Flush();
                run.Add(new XElement(w + "br"));
            }
            else if (ch != '\r')
            {
                buffer.Append(ch);
            }
        }
        Flush();
        return run;
    }

    private static XElement RunFragment(XElement sourceRun, string text, XNamespace w, bool deleted = false)
    {
        return RunFromLogicalText(text, w, deleted, sourceRun.Element(w + "rPr"));
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
            if (child.Name == w + "tab" || child.Name == w + "br" || child.Name == w + "cr" ||
                child.Name == w + "noBreakHyphen")
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
            if (child.Name == w + "tab" || child.Name == w + "br" || child.Name == w + "cr" ||
                child.Name == w + "noBreakHyphen")
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
            var del = new XElement(w + "del", RevisionAttrs(w, id++, author),
                RunFromLogicalText(deletedText, w, deleted: true));
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
        if ((file.StartsWith("comments", StringComparison.Ordinal) && file.EndsWith(".xml", StringComparison.Ordinal)) ||
            file == "people.xml")
            return "comments";
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

    private sealed record UnsupportedRelationshipRef(
        string ElementName, string AttributeName, string Resolved,
        string AnchorKind, string AnchorText, int AnchorOccurrence,
        string OwnerText, int OwnerOccurrence, string OutsideText,
        string PreviousParagraph, string NextParagraph, string StructuralPath);

    private sealed record UnsupportedOtherRef(
        string Kind, string Value, string OwnerText, int OwnerOccurrence,
        string PreviousParagraph, string NextParagraph,
        string BeforeToken, string AfterToken, string StructuralPath);

    private sealed record UnsupportedMainContentSnapshot(
        List<UnsupportedRelationshipRef> Relationships, List<UnsupportedOtherRef> FieldsAndSymbols);

    private static string StructuralPathWithinParagraph(XElement element, XElement? paragraph)
    {
        if (paragraph is null) return "outside";
        var parts = new List<string>();
        var current = element;
        while (!ReferenceEquals(current, paragraph))
        {
            var parent = current.Parent as XElement;
            if (parent is null) return "outside";
            var ordinal = parent.Elements().TakeWhile(x => !ReferenceEquals(x, current)).Count();
            parts.Add(current.Name.LocalName + "#" + ordinal);
            current = parent;
        }
        parts.Reverse();
        return string.Join("/", parts);
    }

    private const string EmptyParagraphContextToken = "";

    private static string ParagraphContextWindow(IReadOnlyList<XElement> paragraphs, int index, int step, XNamespace w)
    {
        if (index < 0) return string.Empty;
        var values = new List<string>();
        for (var i = index + step; i >= 0 && i < paragraphs.Count && values.Count < 8; i += step)
        {
            var value = NativeComparisonEngine.Normalize(ParagraphVisibleText(paragraphs[i], w));
            values.Add(value.Length == 0 ? EmptyParagraphContextToken : value);
        }
        return string.Join("", values);
    }

    private static bool ContextOverlap(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return false;
        var right = b.Split('', StringSplitOptions.RemoveEmptyEntries)
            .Where(x => x != EmptyParagraphContextToken).ToHashSet(StringComparer.Ordinal);
        return a.Split('', StringSplitOptions.RemoveEmptyEntries)
            .Where(x => x != EmptyParagraphContextToken).Any(right.Contains);
    }

    private static string ImmediateContextToken(string window)
    {
        if (window.Length == 0) return string.Empty;
        return window.Split('\u001f', StringSplitOptions.None)[0];
    }

    private static bool SameImmediateParagraphContext(string xPrevious, string xNext, string yPrevious, string yNext)
    {
        return string.Equals(ImmediateContextToken(xPrevious), ImmediateContextToken(yPrevious), StringComparison.Ordinal) &&
               string.Equals(ImmediateContextToken(xNext), ImmediateContextToken(yNext), StringComparison.Ordinal);
    }

    private static int ParagraphTextOccurrence(IReadOnlyList<XElement> paragraphs, int index, string ownerText, XNamespace w)
    {
        if (index < 0 || ownerText.Length == 0) return -1;
        var occurrence = 0;
        for (var i = 0; i < index; i++)
            if (NativeComparisonEngine.Normalize(ParagraphVisibleText(paragraphs[i], w)) == ownerText)
                occurrence++;
        return occurrence;
    }

    private static int AnchorTextOccurrence(XElement anchor, XElement? ownerParagraph, string anchorText, XNamespace w)
    {
        if (ownerParagraph is null || anchorText.Length == 0) return -1;
        var prefix = new StringBuilder();
        foreach (var run in NativeDocumentReader.VisibleRunsInParagraph(ownerParagraph, w))
        {
            if (run.AncestorsAndSelf().Any(x => ReferenceEquals(x, anchor))) break;
            prefix.Append(NativeDocumentReader.RunVisibleText(run, w));
        }
        var normalizedPrefix = NativeComparisonEngine.Normalize(prefix.ToString());
        var occurrence = 0;
        var at = 0;
        while ((at = normalizedPrefix.IndexOf(anchorText, at, StringComparison.Ordinal)) >= 0)
        {
            occurrence++;
            at += Math.Max(1, anchorText.Length);
        }
        return occurrence;
    }

    private static string OutsideAnchorText(string ownerText, string anchorText)
    {
        if (anchorText.Length == 0) return ownerText;
        var at = ownerText.IndexOf(anchorText, StringComparison.Ordinal);
        return at < 0 ? ownerText : ownerText.Remove(at, anchorText.Length);
    }

    private static (string Text, int Distance) NearestDistinctContext(string window, string ownerText)
    {
        if (window.Length == 0) return (string.Empty, -1);
        var values = window.Split('', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < values.Length; i++)
        {
            var value = values[i] == EmptyParagraphContextToken ? string.Empty : values[i];
            if (value.Length > 0 && !string.Equals(value, ownerText, StringComparison.Ordinal))
                return (value, i + 1);
        }
        return (string.Empty, -1);
    }

    private static bool SameSemanticOwner(
        string xText, int xOccurrence, string xPrevious, string xNext,
        string yText, int yOccurrence, string yPrevious, string yNext)
    {
        if (!string.Equals(xText, yText, StringComparison.Ordinal)) return false;
        if (xText.Length > 0 && xOccurrence == yOccurrence) return true;
        var xp = NearestDistinctContext(xPrevious, xText);
        var yp = NearestDistinctContext(yPrevious, yText);
        if (xp.Distance > 0 && xp == yp) return true;
        var xn = NearestDistinctContext(xNext, xText);
        var yn = NearestDistinctContext(yNext, yText);
        return xn.Distance > 0 && xn == yn;
    }

    private static string RunChildVisibleText(XElement child, XNamespace w)
    {
        if (child.Name == w + "t") return child.Value;
        if (child.Name == w + "tab") return "	";
        if (child.Name == w + "br" || child.Name == w + "cr") return "\n";
        if (child.Name == w + "noBreakHyphen") return "-";
        return string.Empty;
    }

    private static string BoundaryToken(string value, bool before)
    {
        var normalized = NativeComparisonEngine.Normalize(value);
        if (normalized.Length == 0) return string.Empty;
        var matches = Regex.Matches(normalized, @"[가-힣A-Za-z0-9_]+|[^\s]");
        if (matches.Count == 0) return string.Empty;
        return before ? matches[^1].Value : matches[0].Value;
    }

    private static (string Before, string After) VisibleBoundaryTokens(
        XElement element, XElement? paragraph, XNamespace w)
    {
        if (paragraph is null) return (string.Empty, string.Empty);
        var runs = NativeDocumentReader.VisibleRunsInParagraph(paragraph, w).ToList();
        var before = new StringBuilder();
        var after = new StringBuilder();

        var containingRun = element.AncestorsAndSelf().FirstOrDefault(x => x.Name == w + "r");
        if (containingRun is not null)
        {
            var runIndex = RefIndex(runs, containingRun);
            if (runIndex < 0) return (string.Empty, string.Empty);
            for (var i = 0; i < runIndex; i++) before.Append(NativeDocumentReader.RunVisibleText(runs[i], w));

            var targetChild = containingRun.Elements().FirstOrDefault(child =>
                ReferenceEquals(child, element) || element.AncestorsAndSelf().Any(x => ReferenceEquals(x, child)));
            var seen = false;
            foreach (var child in containingRun.Elements())
            {
                if (targetChild is not null && ReferenceEquals(child, targetChild))
                {
                    seen = true;
                    continue;
                }
                if (!seen) before.Append(RunChildVisibleText(child, w));
                else after.Append(RunChildVisibleText(child, w));
            }
            for (var i = runIndex + 1; i < runs.Count; i++) after.Append(NativeDocumentReader.RunVisibleText(runs[i], w));
        }
        else
        {
            var inside = runs.Select((run, index) => (run, index))
                .Where(x => x.run.AncestorsAndSelf().Any(a => ReferenceEquals(a, element)))
                .Select(x => x.index).ToList();
            if (inside.Count > 0)
            {
                for (var i = 0; i < inside[0]; i++) before.Append(NativeDocumentReader.RunVisibleText(runs[i], w));
                for (var i = inside[^1] + 1; i < runs.Count; i++) after.Append(NativeDocumentReader.RunVisibleText(runs[i], w));
            }
            else
            {
                // Zero-width markers such as bookmarkStart/commentRangeStart do not own runs.
                // Partition the visible runs by actual document order instead of returning an
                // empty boundary, otherwise moving the marker within the paragraph is invisible.
                foreach (var run in runs)
                {
                    var order = XNode.DocumentOrderComparer.Compare(run, element);
                    if (order < 0) before.Append(NativeDocumentReader.RunVisibleText(run, w));
                    else if (order > 0) after.Append(NativeDocumentReader.RunVisibleText(run, w));
                }
            }
        }

        return (BoundaryToken(before.ToString(), before: true), BoundaryToken(after.ToString(), before: false));
    }

    private static bool SameBoundary(string xb, string xa, string yb, string ya) =>
        string.Equals(xb, yb, StringComparison.Ordinal) && string.Equals(xa, ya, StringComparison.Ordinal);

    private static bool StableBoundaryAcrossTextEdit(string xb, string xa, string yb, string ya)
    {
        var beforeStable = xb.Length > 0 && xb == yb;
        var afterStable = xa.Length > 0 && xa == ya;
        return beforeStable || afterStable;
    }

    private static XElement RelationshipAnchor(XElement element, XNamespace w)
    {
        return element.AncestorsAndSelf().FirstOrDefault(x =>
                   x.Name == w + "hyperlink" || x.Name == w + "drawing" ||
                   x.Name == w + "object" || x.Name == w + "pict" ||
                   x.Name == w + "fldSimple")
               ?? element;
    }

    private static string ElementVisibleText(XElement element, XNamespace w)
    {
        if (element.Name == w + "r") return NativeDocumentReader.RunVisibleText(element, w);
        var sb = new StringBuilder();
        foreach (var run in element.Descendants(w + "r"))
            if (NativeDocumentReader.IsVisibleRun(run, w))
                sb.Append(NativeDocumentReader.RunVisibleText(run, w));
        return NativeComparisonEngine.Normalize(sb.ToString());
    }

    private static UnsupportedMainContentSnapshot ReadMainDocumentUnsupportedContentSnapshot(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        var documentEntry = zip.GetEntry("word/document.xml");
        if (documentEntry is null) return new(new(), new());
        XNamespace w = W;
        XDocument doc; using (var input = documentEntry.Open()) doc = XDocument.Load(input, LoadOptions.PreserveWhitespace);
        var relationships = ReadPartRelationshipSignatures(zip, "word/document.xml");
        var relationRefs = new List<UnsupportedRelationshipRef>();
        var otherTokens = new List<UnsupportedOtherRef>();
        var body = doc.Root?.Element(w + "body");
        var bodyParagraphs = body is null ? new List<XElement>() : BodyParagraphs(body, w).ToList();
        var internalHyperlinkTargets = doc.Descendants(w + "hyperlink")
            .Select(x => x.Attribute(w + "anchor")?.Value ?? string.Empty)
            .Where(x => x.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var element in doc.Descendants())
        {
            if (element.Name == w + "headerReference" || element.Name == w + "footerReference")
                continue;

            var ownerParagraph = element.Ancestors(w + "p").FirstOrDefault();
            var paragraphIndex = ownerParagraph is null ? -1 : RefIndex(bodyParagraphs, ownerParagraph);
            var ownerText = ownerParagraph is null
                ? string.Empty
                : NativeComparisonEngine.Normalize(ParagraphVisibleText(ownerParagraph, w));
            var ownerOccurrence = ParagraphTextOccurrence(bodyParagraphs, paragraphIndex, ownerText, w);
            var previousParagraph = ParagraphContextWindow(bodyParagraphs, paragraphIndex, -1, w);
            var nextParagraph = ParagraphContextWindow(bodyParagraphs, paragraphIndex, 1, w);

            foreach (var attribute in element.Attributes().Where(a => a.Name.NamespaceName == R))
            {
                var resolved = relationships.TryGetValue(attribute.Value, out var signature)
                    ? signature
                    : "missing-rel|" + attribute.Value;
                var anchor = RelationshipAnchor(element, w);
                var anchorText = ElementVisibleText(anchor, w);
                relationRefs.Add(new UnsupportedRelationshipRef(
                    element.Name.LocalName,
                    attribute.Name.LocalName,
                    resolved,
                    anchor.Name.LocalName,
                    anchorText,
                    AnchorTextOccurrence(anchor, ownerParagraph, anchorText, w),
                    ownerText,
                    ownerOccurrence,
                    OutsideAnchorText(ownerText, anchorText),
                    previousParagraph,
                    nextParagraph,
                    StructuralPathWithinParagraph(anchor, ownerParagraph)));
            }

            if (element.Name == w + "instrText")
            {
                var value = NativeComparisonEngine.Normalize(element.Value);
                if (value.Length > 0)
                {
                    var boundary = VisibleBoundaryTokens(element, ownerParagraph, w);
                    otherTokens.Add(new UnsupportedOtherRef(
                        "FIELD", value, ownerText, ownerOccurrence, previousParagraph, nextParagraph,
                        boundary.Before, boundary.After, StructuralPathWithinParagraph(element, ownerParagraph)));
                }
            }
            else if (element.Name == w + "fldSimple")
            {
                var value = NativeComparisonEngine.Normalize(element.Attribute(w + "instr")?.Value ?? string.Empty);
                if (value.Length > 0)
                {
                    var boundary = VisibleBoundaryTokens(element, ownerParagraph, w);
                    otherTokens.Add(new UnsupportedOtherRef(
                        "FIELD", value, ownerText, ownerOccurrence, previousParagraph, nextParagraph,
                        boundary.Before, boundary.After, StructuralPathWithinParagraph(element, ownerParagraph)));
                }
            }
            else if (element.Name == w + "sym")
            {
                var attrs = string.Join(";", element.Attributes()
                    .Where(a => !a.IsNamespaceDeclaration)
                    .OrderBy(a => a.Name.NamespaceName, StringComparer.Ordinal)
                    .ThenBy(a => a.Name.LocalName, StringComparer.Ordinal)
                    .Select(a => $"{{{a.Name.NamespaceName}}}{a.Name.LocalName}={a.Value}"));
                var boundary = VisibleBoundaryTokens(element, ownerParagraph, w);
                otherTokens.Add(new UnsupportedOtherRef(
                    "SYM", attrs, ownerText, ownerOccurrence, previousParagraph, nextParagraph,
                    boundary.Before, boundary.After, StructuralPathWithinParagraph(element, ownerParagraph)));
            }
            else if (element.Name == w + "br")
            {
                var type = element.Attribute(w + "type")?.Value ?? "textWrapping";
                var clear = element.Attribute(w + "clear")?.Value ?? "none";
                var ordinaryManualBreak =
                    type.Equals("textWrapping", StringComparison.OrdinalIgnoreCase) &&
                    clear.Equals("none", StringComparison.OrdinalIgnoreCase);
                if (!ordinaryManualBreak)
                {
                    // The logical text model collapses page/column/clear breaks to '\n'.
                    // They cannot be faithfully reconstructed from deleted text, so unchanged
                    // special breaks may pass but structural changes must be rejected.
                    var boundary = VisibleBoundaryTokens(element, ownerParagraph, w);
                    otherTokens.Add(new UnsupportedOtherRef(
                        "SPECIAL_BREAK", $"type={type};clear={clear}",
                        ownerText, ownerOccurrence, previousParagraph, nextParagraph,
                        boundary.Before, boundary.After, StructuralPathWithinParagraph(element, ownerParagraph)));
                }
            }
            else if (element.Name == w + "softHyphen" || element.Name == w + "noBreakHyphen")
            {
                // softHyphen is absent from visible text and noBreakHyphen collapses to an ordinary
                // '-' in the logical text model. Their add/remove/move cannot be reconstructed
                // faithfully from strings alone, so preserve unchanged markers but block structural
                // changes until native marker revisions are supported.
                var boundary = VisibleBoundaryTokens(element, ownerParagraph, w);
                var kind = element.Name == w + "softHyphen" ? "SOFT_HYPHEN" : "NO_BREAK_HYPHEN";
                otherTokens.Add(new UnsupportedOtherRef(
                    kind, "present", ownerText, ownerOccurrence, previousParagraph, nextParagraph,
                    boundary.Before, boundary.After, StructuralPathWithinParagraph(element, ownerParagraph)));
            }
            else if (element.Name == w + "hyperlink")
            {
                var anchorValue = element.Attribute(w + "anchor")?.Value ?? string.Empty;
                var docLocation = element.Attribute(w + "docLocation")?.Value ?? string.Empty;
                var targetFrame = element.Attribute(w + "tgtFrame")?.Value ?? string.Empty;
                var tooltip = element.Attribute(w + "tooltip")?.Value ?? string.Empty;
                if (anchorValue.Length > 0 || docLocation.Length > 0 || targetFrame.Length > 0 || tooltip.Length > 0)
                {
                    var boundary = VisibleBoundaryTokens(element, ownerParagraph, w);
                    var value = $"anchor={anchorValue}|docLocation={docLocation}|frame={targetFrame}|tooltip={tooltip}";
                    otherTokens.Add(new UnsupportedOtherRef(
                        "HYPERLINK_META", value, ownerText, ownerOccurrence, previousParagraph, nextParagraph,
                        boundary.Before, boundary.After, StructuralPathWithinParagraph(element, ownerParagraph)));
                }
            }
            else if (element.Name == w + "bookmarkStart")
            {
                var name = element.Attribute(w + "name")?.Value ?? string.Empty;
                if (name.Length > 0 && internalHyperlinkTargets.Contains(name))
                {
                    var boundary = VisibleBoundaryTokens(element, ownerParagraph, w);
                    otherTokens.Add(new UnsupportedOtherRef(
                        "BOOKMARK_TARGET", name, ownerText, ownerOccurrence, previousParagraph, nextParagraph,
                        boundary.Before, boundary.After, StructuralPathWithinParagraph(element, ownerParagraph)));
                }
            }
            else if (element.Name == w + "commentReference" ||
                     element.Name == w + "commentRangeStart" ||
                     element.Name == w + "commentRangeEnd")
            {
                var idValue = element.Attribute(w + "id")?.Value ?? string.Empty;
                var boundary = VisibleBoundaryTokens(element, ownerParagraph, w);
                otherTokens.Add(new UnsupportedOtherRef(
                    "COMMENT_MARKER", element.Name.LocalName + "|" + idValue,
                    ownerText, ownerOccurrence, previousParagraph, nextParagraph,
                    boundary.Before, boundary.After, StructuralPathWithinParagraph(element, ownerParagraph)));
            }
        }
        return new(relationRefs, otherTokens);
    }

    private static bool UnsupportedRelationshipRefsEquivalent(
        IReadOnlyList<UnsupportedRelationshipRef> a, IReadOnlyList<UnsupportedRelationshipRef> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            var x = a[i]; var y = b[i];
            if (x.ElementName != y.ElementName || x.AttributeName != y.AttributeName ||
                x.Resolved != y.Resolved || x.AnchorKind != y.AnchorKind)
                return false;

            var sameContext =
                ContextOverlap(x.PreviousParagraph, y.PreviousParagraph) ||
                ContextOverlap(x.NextParagraph, y.NextParagraph) ||
                (x.PreviousParagraph.Length == 0 && x.NextParagraph.Length == 0 &&
                 y.PreviousParagraph.Length == 0 && y.NextParagraph.Length == 0);
            var sameOwner = SameSemanticOwner(
                x.OwnerText, x.OwnerOccurrence, x.PreviousParagraph, x.NextParagraph,
                y.OwnerText, y.OwnerOccurrence, y.PreviousParagraph, y.NextParagraph);

            if (sameOwner)
            {
                if (x.AnchorKind == "hyperlink")
                {
                    if (x.AnchorText.Length > 0 && x.AnchorText == y.AnchorText &&
                        x.AnchorOccurrence == y.AnchorOccurrence)
                        continue;
                    return false;
                }
                if (x.StructuralPath == y.StructuralPath)
                    continue;
                return false;
            }

            // Hyperlink display text can be edited while its surrounding paragraph identity stays
            // stable.  Likewise ordinary text can be inserted around the same linked text.
            if (x.AnchorKind == "hyperlink" && sameContext)
            {
                if (x.StructuralPath == y.StructuralPath && x.OutsideText == y.OutsideText)
                    continue;
                if (x.AnchorText.Length > 0 && x.AnchorText == y.AnchorText &&
                    x.AnchorOccurrence == y.AnchorOccurrence &&
                    (x.OutsideText.Length == 0 || y.OutsideText.Length == 0 ||
                     x.OutsideText.Contains(y.OutsideText, StringComparison.Ordinal) ||
                     y.OutsideText.Contains(x.OutsideText, StringComparison.Ordinal)))
                    continue;
            }

            // For drawings/objects/charts an owner-paragraph change is intentionally conservative:
            // silently moving non-text content cannot be represented by our text revision model.
            return false;
        }
        return true;
    }

    private static bool UnsupportedOtherRefsEquivalent(
        IReadOnlyList<UnsupportedOtherRef> a, IReadOnlyList<UnsupportedOtherRef> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            var x = a[i]; var y = b[i];
            if (x.Kind != y.Kind || x.Value != y.Value)
                return false;
            // These inline markers can remain at the same structural slot while surrounding
            // visible text changes. Same structural path is the strongest available identity.
            if ((x.Kind is "SOFT_HYPHEN" or "NO_BREAK_HYPHEN") &&
                x.StructuralPath == y.StructuralPath)
                continue;
            var sameOwner = SameSemanticOwner(
                x.OwnerText, x.OwnerOccurrence, x.PreviousParagraph, x.NextParagraph,
                y.OwnerText, y.OwnerOccurrence, y.PreviousParagraph, y.NextParagraph);
            if (sameOwner)
            {
                if (x.OwnerText.Length == 0)
                {
                    if (x.StructuralPath != y.StructuralPath) return false;
                }
                else if (!SameBoundary(x.BeforeToken, x.AfterToken, y.BeforeToken, y.AfterToken))
                    return false;
                continue;
            }
            var sameContext =
                ContextOverlap(x.PreviousParagraph, y.PreviousParagraph) ||
                ContextOverlap(x.NextParagraph, y.NextParagraph) ||
                (x.PreviousParagraph.Length == 0 && x.NextParagraph.Length == 0 &&
                 y.PreviousParagraph.Length == 0 && y.NextParagraph.Length == 0);

            // Zero-width semantic markers and internal hyperlinks may stay at exactly the same
            // structural slot while the surrounding visible text is fully rewritten.  In that
            // case the unchanged kind/value + same paragraph context + same structural path is
            // stronger evidence than lexical containment of the owner text.  We still require
            // boundary equality above whenever the visible owner text itself did not change, so
            // moving a marker/link within an otherwise unchanged paragraph remains detectable.
            if (x.OwnerText != y.OwnerText && x.StructuralPath == y.StructuralPath &&
                (x.Kind is "HYPERLINK_META" or "BOOKMARK_TARGET" or "COMMENT_MARKER") &&
                SameImmediateParagraphContext(
                    x.PreviousParagraph, x.NextParagraph, y.PreviousParagraph, y.NextParagraph))
                continue;

            if (x.OwnerText == y.OwnerText)
                return false;
            var ownerContainment = x.OwnerText.Length > 0 && y.OwnerText.Length > 0 &&
                (x.OwnerText.Contains(y.OwnerText, StringComparison.Ordinal) ||
                 y.OwnerText.Contains(x.OwnerText, StringComparison.Ordinal));
            if (!sameContext || !ownerContainment ||
                !StableBoundaryAcrossTextEdit(x.BeforeToken, x.AfterToken, y.BeforeToken, y.AfterToken))
                return false;
        }
        return true;
    }

    private static string ReadMainDocumentUnsupportedContentSignature(string path)
    {
        var snapshot = ReadMainDocumentUnsupportedContentSnapshot(path);
        var rels = snapshot.Relationships.Select(x =>
            $"RELREF|{x.ElementName}|{x.AttributeName}|{x.Resolved}|{x.AnchorKind}|{x.AnchorText}#{x.AnchorOccurrence}|owner={x.OwnerText}#{x.OwnerOccurrence}|outside={x.OutsideText}|{x.StructuralPath}");
        var others = snapshot.FieldsAndSymbols.Select(x =>
            $"{x.Kind}|{x.Value}|owner={x.OwnerText}#{x.OwnerOccurrence}|before={x.BeforeToken}|after={x.AfterToken}|{x.StructuralPath}");
        return string.Join("\n", rels.Concat(others));
    }

    private sealed record ParagraphFormattingSnapshot(
        string Text,
        string Signature,
        string NonNumberingSignature,
        string DirectNumberingReference);

    private static string CanonicalFormattingElement(XElement? element)
    {
        if (element is null) return string.Empty;
        var clone = new XElement(element);
        XNamespace w = W;

        // Numbering has its own supported Track Changes path and must not be mistaken for a
        // formatting-only edit.
        if (clone.Name == w + "pPr")
            clone.Element(w + "numPr")?.Remove();

        foreach (var attribute in clone.DescendantsAndSelf().Attributes().ToList())
        {
            if (attribute.IsNamespaceDeclaration)
            {
                attribute.Remove();
                continue;
            }
            if (attribute.Name.NamespaceName == W &&
                attribute.Name.LocalName.StartsWith("rsid", StringComparison.Ordinal))
                attribute.Remove();
            else if (attribute.Name.NamespaceName == "http://schemas.microsoft.com/office/word/2010/wordml" &&
                     attribute.Name.LocalName is "paraId" or "textId")
                attribute.Remove();
        }

        // After removing supported/volatile children, an empty property container is equivalent
        // to having no direct properties at all (e.g. pPr containing only numPr).
        if (!clone.HasAttributes && !clone.Elements().Any() && string.IsNullOrWhiteSpace(clone.Value))
            return string.Empty;

        return clone.ToString(SaveOptions.DisableFormatting);
    }

    private static Dictionary<string, XElement> ReadStyleElements(ZipArchive zip, XNamespace w, out string docDefaults)
    {
        var result = new Dictionary<string, XElement>(StringComparer.Ordinal);
        docDefaults = string.Empty;
        var entry = zip.GetEntry("word/styles.xml");
        if (entry is null) return result;
        if (entry.Length > 16L * 1024 * 1024)
            throw new InvalidDataException($"DOCX styles.xml이 너무 큽니다: {entry.Length:N0} bytes");

        XDocument doc;
        using (var input = entry.Open())
            doc = XDocument.Load(input, LoadOptions.PreserveWhitespace);
        var root = doc.Root;
        if (root is null) return result;

        docDefaults = CanonicalFormattingElement(root.Element(w + "docDefaults"));
        foreach (var style in root.Elements(w + "style"))
        {
            var id = style.Attribute(w + "styleId")?.Value;
            if (!string.IsNullOrEmpty(id))
                result[id] = style;
        }
        return result;
    }

    private sealed record NumberingFormattingContext(
        Dictionary<int, XElement> Abstracts,
        Dictionary<int, XElement> Instances);

    private static NumberingFormattingContext ReadNumberingFormattingContext(ZipArchive zip, XNamespace w)
    {
        var abstracts = new Dictionary<int, XElement>();
        var instances = new Dictionary<int, XElement>();
        var entry = zip.GetEntry("word/numbering.xml");
        if (entry is null) return new NumberingFormattingContext(abstracts, instances);
        if (entry.Length > 16L * 1024 * 1024)
            throw new InvalidDataException($"DOCX numbering.xml이 너무 큽니다: {entry.Length:N0} bytes");
        XDocument doc;
        using (var input = entry.Open())
            doc = XDocument.Load(input, LoadOptions.PreserveWhitespace);
        if (doc.Root is null) return new NumberingFormattingContext(abstracts, instances);
        foreach (var abstractNum in doc.Root.Elements(w + "abstractNum"))
            if (int.TryParse(abstractNum.Attribute(w + "abstractNumId")?.Value, out var abstractId))
                abstracts[abstractId] = abstractNum;
        foreach (var num in doc.Root.Elements(w + "num"))
            if (int.TryParse(num.Attribute(w + "numId")?.Value, out var numId))
                instances[numId] = num;
        return new NumberingFormattingContext(abstracts, instances);
    }

    private static (int NumId, int Level)? ParagraphNumberingReference(
        XElement paragraph, IReadOnlyDictionary<string, XElement> styles, XNamespace w)
    {
        static (int NumId, int Level)? ReadNumPr(XElement? pPr, XNamespace w)
        {
            var numPr = pPr?.Element(w + "numPr");
            if (numPr is null) return null;
            if (!int.TryParse(numPr.Element(w + "numId")?.Attribute(w + "val")?.Value, out var numId))
                return null;
            var level = 0;
            _ = int.TryParse(numPr.Element(w + "ilvl")?.Attribute(w + "val")?.Value, out level);
            return (numId, level);
        }
        var pPr = paragraph.Element(w + "pPr");
        var direct = ReadNumPr(pPr, w);
        if (direct is not null) return direct;
        var styleId = pPr?.Element(w + "pStyle")?.Attribute(w + "val")?.Value;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (!string.IsNullOrEmpty(styleId) && seen.Add(styleId) && styles.TryGetValue(styleId, out var style))
        {
            var inherited = ReadNumPr(style.Element(w + "pPr"), w);
            if (inherited is not null) return inherited;
            styleId = style.Element(w + "basedOn")?.Attribute(w + "val")?.Value;
        }
        return null;
    }

    private static string NumberingFormattingSignature(
        XElement paragraph, IReadOnlyDictionary<string, XElement> styles,
        NumberingFormattingContext numbering, XNamespace w)
    {
        var reference = ParagraphNumberingReference(paragraph, styles, w);
        if (reference is null || reference.Value.NumId == 0) return string.Empty;
        var (numId, level) = reference.Value;
        if (!numbering.Instances.TryGetValue(numId, out var instance))
            return $"MISSING-NUM:{level}";
        if (!int.TryParse(instance.Element(w + "abstractNumId")?.Attribute(w + "val")?.Value, out var abstractId) ||
            !numbering.Abstracts.TryGetValue(abstractId, out var abstractNum))
            return $"MISSING-ABSTRACT:{level}";
        static bool SameLevel(XElement element, XNamespace w, int level) =>
            int.TryParse(element.Attribute(w + "ilvl")?.Value, out var parsed) && parsed == level;
        static string FormattingOnly(XElement? source, XNamespace w)
        {
            if (source is null) return string.Empty;
            var clone = new XElement(source);
            // Label semantics are already handled by the comparison/numberingChange path.
            // This guard is only for layout/appearance that would otherwise be inherited
            // silently from physical base B.
            foreach (var lvl in clone.Name == w + "lvl"
                         ? new[] { clone }
                         : clone.DescendantsAndSelf(w + "lvl").ToArray())
            {
                lvl.Element(w + "start")?.Remove();
                lvl.Element(w + "numFmt")?.Remove();
                lvl.Element(w + "lvlText")?.Remove();
                lvl.Element(w + "lvlRestart")?.Remove();
            }
            clone.Element(w + "startOverride")?.Remove();
            return CanonicalFormattingElement(clone);
        }

        var levelDefinition = abstractNum.Elements(w + "lvl").FirstOrDefault(x => SameLevel(x, w, level));
        var levelOverride = instance.Elements(w + "lvlOverride").FirstOrDefault(x => SameLevel(x, w, level));
        return "LEVEL|" + FormattingOnly(levelDefinition, w) +
               "|OVERRIDE|" + FormattingOnly(levelOverride, w);
    }

    private static string ReadThemeFormattingSignature(ZipArchive zip)
    {
        var entry = zip.GetEntry("word/theme/theme1.xml");
        if (entry is null) return string.Empty;
        if (entry.Length > 16L * 1024 * 1024)
            throw new InvalidDataException($"DOCX theme1.xml이 너무 큽니다: {entry.Length:N0} bytes");
        XDocument doc;
        using (var input = entry.Open())
            doc = XDocument.Load(input, LoadOptions.PreserveWhitespace);
        return doc.Root is null ? string.Empty : CanonicalFormattingElement(doc.Root);
    }

    private static bool FormattingSignatureUsesTheme(string signature)
    {
        if (string.IsNullOrEmpty(signature)) return false;
        string[] themeAttributes =
        {
            "themeColor=", "themeTint=", "themeShade=",
            "themeFill=", "themeFillTint=", "themeFillShade=",
            "asciiTheme=", "hAnsiTheme=", "eastAsiaTheme=", "cstheme="
        };
        return themeAttributes.Any(x =>
            signature.Contains(x, StringComparison.OrdinalIgnoreCase));
    }

    private static string ReferencedStyleFormattingSignature(
        XElement paragraph, IReadOnlyDictionary<string, XElement> styles, string docDefaults, XNamespace w)
    {
        var needed = new HashSet<string>(StringComparer.Ordinal);
        var paragraphStyle = paragraph.Element(w + "pPr")?.Element(w + "pStyle")?.Attribute(w + "val")?.Value;
        if (!string.IsNullOrEmpty(paragraphStyle)) needed.Add(paragraphStyle);

        foreach (var run in NativeDocumentReader.VisibleRunsInParagraph(paragraph, w))
        {
            var runStyle = run.Element(w + "rPr")?.Element(w + "rStyle")?.Attribute(w + "val")?.Value;
            if (!string.IsNullOrEmpty(runStyle)) needed.Add(runStyle);
        }

        // Unstyled text still inherits Word's default paragraph/character styles. A table can
        // likewise inherit an explicit/default table style. Include these dependencies so a
        // styles.xml-only change cannot silently alter B-based export rendering.
        var insideTable = paragraph.Ancestors(w + "tbl").Any();
        foreach (var (id, style) in styles)
        {
            var type = style.Attribute(w + "type")?.Value ?? string.Empty;
            var defaultValue = style.Attribute(w + "default")?.Value ?? string.Empty;
            var isDefault = defaultValue is "1" or "true" or "on";
            if (isDefault && (type is "paragraph" or "character" || (insideTable && type == "table")))
                needed.Add(id);
        }
        foreach (var table in paragraph.Ancestors(w + "tbl"))
        {
            var tableStyle = table.Element(w + "tblPr")?.Element(w + "tblStyle")?.Attribute(w + "val")?.Value;
            if (!string.IsNullOrEmpty(tableStyle)) needed.Add(tableStyle);
        }

        var queue = new Queue<string>(needed);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!styles.TryGetValue(id, out var style)) continue;
            foreach (var relation in new[] { style.Element(w + "basedOn"), style.Element(w + "link") })
            {
                var parent = relation?.Attribute(w + "val")?.Value;
                if (!string.IsNullOrEmpty(parent) && needed.Add(parent))
                    queue.Enqueue(parent);
            }
        }

        var sb = new StringBuilder();
        if (docDefaults.Length > 0)
            sb.Append("DEFAULTS|").Append(docDefaults).Append('|');
        foreach (var id in needed.OrderBy(x => x, StringComparer.Ordinal))
        {
            sb.Append("STYLE:").Append(id).Append('=');
            if (styles.TryGetValue(id, out var style))
                sb.Append(CanonicalFormattingElement(style));
            else
                sb.Append("<missing>");
            sb.Append('|');
        }
        return sb.ToString();
    }

    private static string StructuralFormattingSignature(XElement paragraph, XNamespace w)
    {
        var sb = new StringBuilder();
        // Nearest container first, then outer containers. This makes nested-table ownership part
        // of the formatting identity without relying on raw document-wide table ordinals.
        foreach (var ancestor in paragraph.Ancestors())
        {
            if (ancestor.Name == w + "tc")
                sb.Append("TC|").Append(CanonicalFormattingElement(ancestor.Element(w + "tcPr"))).Append('|');
            else if (ancestor.Name == w + "tr")
                sb.Append("TR|").Append(CanonicalFormattingElement(ancestor.Element(w + "trPr"))).Append('|');
            else if (ancestor.Name == w + "tbl")
                sb.Append("TBL|").Append(CanonicalFormattingElement(ancestor.Element(w + "tblPr"))).Append('|')
                  .Append("GRID|").Append(CanonicalFormattingElement(ancestor.Element(w + "tblGrid"))).Append('|');
        }
        return sb.ToString();
    }

    private static List<string> ReadSectionFormattingSignatures(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        var entry = zip.GetEntry("word/document.xml");
        if (entry is null) return new();

        XNamespace w = W;
        XDocument doc;
        using (var input = entry.Open())
            doc = XDocument.Load(input, LoadOptions.PreserveWhitespace);

        var result = new List<string>();
        foreach (var source in doc.Descendants(w + "sectPr"))
        {
            var clone = new XElement(source);
            // Header/footer identity and relationship targets are guarded separately by the
            // ancillary-reference checks. Do not compare volatile rIds here.
            clone.Elements(w + "headerReference").Remove();
            clone.Elements(w + "footerReference").Remove();
            result.Add(CanonicalFormattingElement(clone));
        }
        return result;
    }

    private static List<ParagraphFormattingSnapshot> ReadParagraphFormattingSnapshots(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        var entry = zip.GetEntry("word/document.xml");
        if (entry is null) return new();

        XNamespace w = W;
        XDocument doc;
        using (var input = entry.Open())
            doc = XDocument.Load(input, LoadOptions.PreserveWhitespace);
        var body = doc.Root?.Element(w + "body");
        if (body is null) return new();

        var styles = ReadStyleElements(zip, w, out var docDefaults);
        var numbering = ReadNumberingFormattingContext(zip, w);
        var theme = ReadThemeFormattingSignature(zip);
        var result = new List<ParagraphFormattingSnapshot>();
        foreach (var paragraph in BodyParagraphs(body, w))
        {
            var text = ParagraphVisibleText(paragraph, w);
            if (string.IsNullOrWhiteSpace(text)) continue;

            var sb = new StringBuilder();
            var directNumberingReference =
                CanonicalFormattingElement(paragraph.Element(w + "pPr")?.Element(w + "numPr"));
            sb.Append("P|").Append(CanonicalFormattingElement(paragraph.Element(w + "pPr"))).Append('|');

            string? previousFormat = null;
            var previousLength = 0;
            void Flush()
            {
                if (previousFormat is null) return;
                sb.Append("R").Append(previousLength).Append(':')
                  .Append(previousFormat.Length).Append(':').Append(previousFormat).Append(';');
            }

            foreach (var run in NativeDocumentReader.VisibleRunsInParagraph(paragraph, w))
            {
                var length = NativeDocumentReader.RunVisibleText(run, w).Length;
                if (length == 0) continue;
                var format = CanonicalFormattingElement(run.Element(w + "rPr"));
                if (previousFormat == format)
                    previousLength += length;
                else
                {
                    Flush();
                    previousFormat = format;
                    previousLength = length;
                }
            }
            Flush();
            sb.Append("STRUCT|").Append(StructuralFormattingSignature(paragraph, w)).Append('|');
            sb.Append("STYLES|").Append(ReferencedStyleFormattingSignature(paragraph, styles, docDefaults, w));
            var nonNumberingUsesTheme = FormattingSignatureUsesTheme(sb.ToString());
            if (nonNumberingUsesTheme)
                sb.Append("|THEME|").Append(theme);
            var nonNumberingSignature = sb.ToString();
            var numberingSignature = NumberingFormattingSignature(paragraph, styles, numbering, w);
            sb.Append("|NUMBERING|").Append(numberingSignature);
            if (!nonNumberingUsesTheme && FormattingSignatureUsesTheme(numberingSignature))
                sb.Append("|NUMBERING_THEME|").Append(theme);
            result.Add(new ParagraphFormattingSnapshot(
                text, sb.ToString(), nonNumberingSignature, directNumberingReference));
        }
        return result;
    }

    private static void EnsureNoUntrackedFormattingOnlyChanges(string originalPath, string revisedPath)
    {
        var a = ReadParagraphFormattingSnapshots(originalPath);
        var b = ReadParagraphFormattingSnapshots(revisedPath);

        // Repeated boilerplate text is common in contracts and tables. Pair exact text+format
        // survivors first, so a newly inserted duplicate does not steal the old paragraph merely
        // because it occurs earlier in B. Only the residual monotonic gaps are candidates for a
        // true formatting-only rewrite of otherwise identical text.
        static string ExactFormattingKey(ParagraphFormattingSnapshot x) =>
            $"{x.Text.Length}:{x.Text}{x.Signature.Length}:{x.Signature}" +
            $"|NUMPR|{x.DirectNumberingReference.Length}:{x.DirectNumberingReference}";
        var exactPairs = Lcs(a.Select(ExactFormattingKey).ToArray(), b.Select(ExactFormattingKey).ToArray());
        var anchors = new List<(int A, int B)> { (-1, -1) };
        anchors.AddRange(exactPairs);
        anchors.Add((a.Count, b.Count));

        for (var k = 0; k < anchors.Count - 1; k++)
        {
            var left = anchors[k];
            var right = anchors[k + 1];
            var a0 = left.A + 1; var a1 = right.A;
            var b0 = left.B + 1; var b1 = right.B;
            if (a0 >= a1 || b0 >= b1) continue;
            var residual = Lcs(a.Skip(a0).Take(a1 - a0).Select(x => x.Text).ToArray(),
                               b.Skip(b0).Take(b1 - b0).Select(x => x.Text).ToArray());
            foreach (var (localA, localB) in residual)
            {
                var ai = a0 + localA; var bi = b0 + localB;
                if (string.Equals(a[ai].Signature, b[bi].Signature, StringComparison.Ordinal) &&
                    string.Equals(a[ai].DirectNumberingReference, b[bi].DirectNumberingReference,
                        StringComparison.Ordinal))
                    continue;

                var supportedDirectNumberingChange =
                    !string.Equals(a[ai].DirectNumberingReference, b[bi].DirectNumberingReference,
                        StringComparison.Ordinal) &&
                    (a[ai].DirectNumberingReference.Length == 0 ||
                     b[bi].DirectNumberingReference.Length == 0) &&
                    string.Equals(a[ai].NonNumberingSignature, b[bi].NonNumberingSignature,
                        StringComparison.Ordinal);
                if (!supportedDirectNumberingChange)
                    throw new InvalidOperationException(
                        "A와 B의 동일 본문 문단 또는 그 문단이 속한 Word 서식/스타일 구조가 다릅니다. 현재 변경추적 내보내기는 해당 서식 변경을 안전하게 추적하지 않으므로 저장을 중단했습니다.");
            }
        }

        var aSections = ReadSectionFormattingSignatures(originalPath);
        var bSections = ReadSectionFormattingSignatures(revisedPath);
        if (!aSections.SequenceEqual(bSections, StringComparer.Ordinal))
            throw new InvalidOperationException(
                "A와 B의 Word 구역/페이지 설정이 다릅니다. 현재 변경추적 내보내기는 구역 서식 변경을 안전하게 추적하지 않으므로 저장을 중단했습니다.");
    }

    private static void EnsureMainDocumentUnsupportedContentEquivalent(string originalPath, string revisedPath)
    {
        var a = ReadMainDocumentUnsupportedContentSnapshot(originalPath);
        var b = ReadMainDocumentUnsupportedContentSnapshot(revisedPath);
        if (!UnsupportedRelationshipRefsEquivalent(a.Relationships, b.Relationships) ||
            !UnsupportedOtherRefsEquivalent(a.FieldsAndSymbols, b.FieldsAndSymbols))
            throw new InvalidOperationException("A와 B의 Word 본문 관계/필드/기호 같은 비텍스트 내용이 서로 다릅니다. 현재 변경추적 엔진이 이 변경을 안전하게 표현하지 못하므로 저장을 중단했습니다.");
    }

    private sealed record AncillaryNoteRef(
        string Kind, string Id, int LocalIndex,
        string OwnerText, int OwnerOccurrence, string PreviousParagraph, string NextParagraph,
        string BeforeToken, string AfterToken, string StructuralPath);

    private sealed record MainAncillaryReferenceSnapshot(
        List<string> SectionReferences, List<AncillaryNoteRef> NoteReferences);

    private static MainAncillaryReferenceSnapshot ReadMainDocumentAncillaryReferenceSnapshot(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        var documentEntry = zip.GetEntry("word/document.xml");
        if (documentEntry is null) return new(new(), new());
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

        var sectionTokens = new List<string>();
        var noteRefs = new List<AncillaryNoteRef>();
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
                sectionTokens.Add($"{element.Name.LocalName}|section={sectionIndex}|{element.Attribute(w + "type")?.Value ?? string.Empty}|{target}");
            }
            else if (element.Name == w + "footnoteReference" || element.Name == w + "endnoteReference")
            {
                var ownerParagraph = element.Ancestors(w + "p").FirstOrDefault();
                var paragraphIndex = ownerParagraph is null ? -1 : RefIndex(bodyParagraphs, ownerParagraph);
                var ownerText = ownerParagraph is null
                    ? string.Empty
                    : NativeComparisonEngine.Normalize(ParagraphVisibleText(ownerParagraph, w));
                var ownerOccurrence = ParagraphTextOccurrence(bodyParagraphs, paragraphIndex, ownerText, w);
                var previousParagraph = ParagraphContextWindow(bodyParagraphs, paragraphIndex, -1, w);
                var nextParagraph = ParagraphContextWindow(bodyParagraphs, paragraphIndex, 1, w);
                var localIndex = ownerParagraph is null ? -1 : RefIndex(
                    ownerParagraph.Descendants(element.Name)
                        .Where(x => ReferenceEquals(x.Ancestors(w + "p").FirstOrDefault(), ownerParagraph)), element);
                var boundary = VisibleBoundaryTokens(element, ownerParagraph, w);
                noteRefs.Add(new AncillaryNoteRef(
                    element.Name.LocalName,
                    element.Attribute(w + "id")?.Value ?? string.Empty,
                    localIndex,
                    ownerText,
                    ownerOccurrence,
                    previousParagraph,
                    nextParagraph,
                    boundary.Before,
                    boundary.After,
                    StructuralPathWithinParagraph(element, ownerParagraph)));
            }
        }
        return new(sectionTokens, noteRefs);
    }

    private static bool AncillaryNoteRefsEquivalent(
        IReadOnlyList<AncillaryNoteRef> a, IReadOnlyList<AncillaryNoteRef> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            var x = a[i]; var y = b[i];
            if (x.Kind != y.Kind || x.Id != y.Id || x.LocalIndex != y.LocalIndex)
                return false;
            var sameOwner = SameSemanticOwner(
                x.OwnerText, x.OwnerOccurrence, x.PreviousParagraph, x.NextParagraph,
                y.OwnerText, y.OwnerOccurrence, y.PreviousParagraph, y.NextParagraph);
            if (sameOwner)
            {
                if (x.OwnerText.Length == 0)
                {
                    if (x.StructuralPath != y.StructuralPath) return false;
                }
                else if (!SameBoundary(x.BeforeToken, x.AfterToken, y.BeforeToken, y.AfterToken))
                    return false;
                continue;
            }
            if (x.OwnerText == y.OwnerText)
                return false;
            var sameContext =
                ContextOverlap(x.PreviousParagraph, y.PreviousParagraph) ||
                ContextOverlap(x.NextParagraph, y.NextParagraph) ||
                (x.PreviousParagraph.Length == 0 && x.NextParagraph.Length == 0 &&
                 y.PreviousParagraph.Length == 0 && y.NextParagraph.Length == 0);
            var ownerContainment = x.OwnerText.Length > 0 && y.OwnerText.Length > 0 &&
                (x.OwnerText.Contains(y.OwnerText, StringComparison.Ordinal) ||
                 y.OwnerText.Contains(x.OwnerText, StringComparison.Ordinal));
            if (!sameContext || !ownerContainment ||
                !StableBoundaryAcrossTextEdit(x.BeforeToken, x.AfterToken, y.BeforeToken, y.AfterToken))
                return false;
        }
        return true;
    }

    private static string ReadMainDocumentAncillaryReferenceSignature(string path)
    {
        var snapshot = ReadMainDocumentAncillaryReferenceSnapshot(path);
        var notes = snapshot.NoteReferences.Select(x =>
            $"{x.Kind}|{x.Id}|local={x.LocalIndex}|owner={x.OwnerText}#{x.OwnerOccurrence}|before={x.BeforeToken}|after={x.AfterToken}|{x.StructuralPath}");
        return string.Join("\n", snapshot.SectionReferences.Concat(notes));
    }

    private static void EnsureAncillaryWordPartsEquivalent(string originalPath, string revisedPath)
    {
        var aRefs = ReadMainDocumentAncillaryReferenceSnapshot(originalPath);
        var bRefs = ReadMainDocumentAncillaryReferenceSnapshot(revisedPath);
        if (!aRefs.SectionReferences.SequenceEqual(bRefs.SectionReferences, StringComparer.Ordinal) ||
            !AncillaryNoteRefsEquivalent(aRefs.NoteReferences, bRefs.NoteReferences))
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
        return new XElement(w + "p", properties is null ? null : new XElement(properties),
            RunFromLogicalText(text, w));
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
            new XElement(w + "ins", RevisionAttrs(w, id++, author), RunFromLogicalText(text, w)));
        MarkParagraphMarkRevision(p, inserted: true, author, ref id, w);
        return p;
    }

    private static XElement DeletedParagraph(string text, string author, ref int id, XElement? properties)
    {
        XNamespace w = W;
        var p = new XElement(w + "p", properties is null ? null : new XElement(properties),
            new XElement(w + "del", RevisionAttrs(w, id++, author), RunFromLogicalText(text, w, deleted: true)));
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
                if (oldChunk.Length > 0) p.Add(new XElement(w + "del", RevisionAttrs(w, id++, author),
                    RunFromLogicalText(oldChunk, w, deleted: true)));
                if (newChunk.Length > 0) p.Add(new XElement(w + "ins", RevisionAttrs(w, id++, author),
                    RunFromLogicalText(newChunk, w)));
            }
            if (right.A < a.Count && right.B < b.Count) p.Add(RunFromLogicalText(b[right.B].Text, w));
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
