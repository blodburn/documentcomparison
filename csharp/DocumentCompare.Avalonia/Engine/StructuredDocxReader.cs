using System.IO.Compression;
using System.Xml.Linq;

namespace DocumentCompare.Avalonia.Engine;

// Comparison representation, deliberately separate from the Word exporter and the A/B/C UI grid.
// Coordinates are physical OpenXML table/row/column coordinates; spans and vertical merges remain typed.
internal sealed record DocxComparisonCell(
    int Table, int Row, int Column, int Span, string VerticalMerge, string Text,
    int ParentTable, int ParentRow, int ParentColumn, int ParagraphCount);

internal sealed record DocxComparisonBlock(string? Paragraph, DocxComparisonCell? Cell);

internal static class StructuredDocxReader
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    public static Task<List<DocxComparisonBlock>> ReadAsync(string path, CancellationToken token) =>
        Task.Run(() => Read(path, token), token);

    private static List<DocxComparisonBlock> Read(string path, CancellationToken token)
    {
        using var zip = ZipFile.OpenRead(path);
        var document = zip.GetEntry("word/document.xml")
            ?? throw new InvalidDataException("DOCX word/document.xml is missing");
        if (document.Length > 128L * 1024 * 1024)
            throw new InvalidDataException("DOCX document XML exceeds safety limit");
        using var stream = document.Open();
        var body = XDocument.Load(stream).Root?.Element(W + "body")
            ?? throw new InvalidDataException("DOCX body is missing");
        var result = new List<DocxComparisonBlock>();
        var tableNumber = 0;
        var numbering = NativeDocumentReader.BuildNumberingContext(zip, W);

        string Paragraph(XElement p)
        {
            var text = NativeDocumentReader.TrimStructuralEdges(NativeDocumentReader.ParagraphText(p, W));
            if (text.Length == 0) return "";
            var label = NativeDocumentReader.NumberLabel(p, numbering, W);
            if (label.Length > 0 &&
                !NativeDocumentReader.Compact(text).StartsWith(NativeDocumentReader.Compact(label),
                    StringComparison.OrdinalIgnoreCase))
                return (label + " " + text).Trim();
            return text;
        }

        IEnumerable<XElement> Visible(XElement container)
        {
            foreach (var element in container.Elements())
            {
                if (element.Name == W + "p" || element.Name == W + "tbl")
                    yield return element;
                else if (element.Name == W + "sdt")
                {
                    var content = element.Element(W + "sdtContent");
                    if (content is not null)
                        foreach (var descendant in Visible(content)) yield return descendant;
                }
                else if (element.Name == W + "customXml")
                    foreach (var descendant in Visible(element)) yield return descendant;
            }
        }

        IEnumerable<XElement> Children(XElement container, XName desired)
        {
            foreach (var element in container.Elements())
            {
                if (element.Name == desired) yield return element;
                else if (element.Name == W + "sdt")
                {
                    var content = element.Element(W + "sdtContent");
                    if (content is not null)
                        foreach (var descendant in Children(content, desired)) yield return descendant;
                }
                else if (element.Name == W + "customXml")
                    foreach (var descendant in Children(element, desired)) yield return descendant;
            }
        }

        void Table(XElement table, int parentTable, int parentRow, int parentColumn, int depth)
        {
            token.ThrowIfCancellationRequested();
            if (depth > 16) throw new InvalidDataException("Nested DOCX table depth exceeded");
            var id = tableNumber++;
            var rowIndex = 0;
            foreach (var row in Children(table, W + "tr"))
            {
                var column = 0;
                var nested = new List<(XElement Table, int Column)>();
                foreach (var cell in Children(row, W + "tc"))
                {
                    token.ThrowIfCancellationRequested();
                    var rawSpan = cell.Element(W + "tcPr")?.Element(W + "gridSpan")?.Attribute(W + "val")?.Value;
                    var span = int.TryParse(rawSpan, out var parsed) ? Math.Clamp(parsed, 1, 256) : 1;
                    var mergeElement = cell.Element(W + "tcPr")?.Element(W + "vMerge");
                    var verticalMerge = mergeElement is null ? "" : mergeElement.Attribute(W + "val")?.Value ?? "continue";
                    var paragraphs = new List<string>();
                    foreach (var child in Visible(cell))
                    {
                        if (child.Name == W + "p") paragraphs.Add(Paragraph(child));
                        else if (child.Name == W + "tbl") nested.Add((child, column));
                    }
                    result.Add(new DocxComparisonBlock(null, new DocxComparisonCell(
                        id, rowIndex, column, span, verticalMerge, string.Join("\n", paragraphs),
                        parentTable, parentRow, parentColumn, paragraphs.Count)));
                    column += span;
                }
                // A nested table is a separate hierarchy, never a second copy of its parent cell text.
                foreach (var item in nested) Table(item.Table, id, rowIndex, item.Column, depth + 1);
                rowIndex++;
            }
            if (rowIndex == 0)
                result.Add(new DocxComparisonBlock(null,
                    new DocxComparisonCell(id, -1, -1, 1, "", "", parentTable, parentRow, parentColumn, 0)));
        }

        foreach (var block in Visible(body))
        {
            token.ThrowIfCancellationRequested();
            if (block.Name == W + "p")
            {
                var text = Paragraph(block);
                if (text.Length > 0) result.Add(new DocxComparisonBlock(text, null));
            }
            else Table(block, -1, -1, -1, 0);
        }
        return result;
    }
}
