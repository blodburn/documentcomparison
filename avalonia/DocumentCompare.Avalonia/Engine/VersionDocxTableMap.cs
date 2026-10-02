using System.IO.Compression;
using System.Xml.Linq;

namespace DocumentCompare.Avalonia.Engine;

public sealed class VersionTableCellVisual
{
    public string Text { get; init; } = "";
    public int GridSpan { get; init; } = 1;
    public string? Shading { get; init; }
    public VersionParagraphVisualStyle? ParagraphStyle { get; init; }
}

public sealed class VersionTableRowVisual
{
    public string FlatText { get; init; } = "";
    public IReadOnlyList<double> ColumnWidths { get; init; } = Array.Empty<double>();
    public IReadOnlyList<VersionTableCellVisual> Cells { get; init; } = Array.Empty<VersionTableCellVisual>();
}

public sealed class VersionDocxTableMap
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private readonly Dictionary<string, Queue<VersionTableRowVisual>> _rows;

    private VersionDocxTableMap(IEnumerable<VersionTableRowVisual> rows)
    {
        _rows = rows.GroupBy(x => VersionDocumentStyleMap.Normalize(x.FlatText), StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => new Queue<VersionTableRowVisual>(x), StringComparer.Ordinal);
    }

    public static VersionDocxTableMap? Load(string path)
    {
        if (!path.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var entry = archive.GetEntry("word/document.xml");
            if (entry is null) return null;
            XDocument doc;
            using (var stream = entry.Open()) doc = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
            var body = doc.Root?.Element(W + "body");
            if (body is null) return null;
            var numbering = NativeDocumentReader.BuildNumberingContext(archive, W);
            var styles = VersionDocumentStyleMap.Load(path);
            var result = new List<VersionTableRowVisual>();
            foreach (var table in EnumerateTables(body))
            {
                var widths = ReadGridWidths(table);
                foreach (var row in NativeDocumentReader.EnumerateTransparentChildren(table, W + "tr", W))
                {
                    var cells = new List<VersionTableCellVisual>();
                    var flatCells = new List<string>();
                    foreach (var cell in NativeDocumentReader.EnumerateTransparentChildren(row, W + "tc", W))
                    {
                        var paragraphs = cell.Descendants(W + "p")
                            .Where(p => ReferenceEquals(p.Ancestors(W + "tc").FirstOrDefault(), cell))
                            .Select(p => VisibleParagraph(p, numbering))
                            .Where(x => x.Length > 0).ToList();
                        var text = string.Join("\n", paragraphs);
                        var style = paragraphs.Count == 1 ? styles?.Take(paragraphs[0]) : null;
                        var tcPr = cell.Element(W + "tcPr");
                        var span = 1;
                        if (int.TryParse(tcPr?.Element(W + "gridSpan")?.Attribute(W + "val")?.Value, out var parsed) && parsed > 0) span = parsed;
                        var fill = tcPr?.Element(W + "shd")?.Attribute(W + "fill")?.Value;
                        if (string.Equals(fill, "auto", StringComparison.OrdinalIgnoreCase) || string.Equals(fill, "nil", StringComparison.OrdinalIgnoreCase)) fill = null;
                        if (fill is not null && (fill.Length != 6 || fill.Any(c => !Uri.IsHexDigit(c)))) fill = null;
                        cells.Add(new VersionTableCellVisual { Text = text, GridSpan = span, Shading = fill is null ? null : "#" + fill, ParagraphStyle = style });
                        flatCells.Add(text);
                    }
                    var flat = string.Join(" | ", flatCells);
                    if (flatCells.Any(x => x.Length > 0)) result.Add(new VersionTableRowVisual { FlatText = flat, ColumnWidths = widths, Cells = cells });
                }
            }
            return result.Count == 0 ? null : new VersionDocxTableMap(result);
        }
        catch { return null; }
    }

    public VersionTableRowVisual? Take(string flatText)
    {
        var key = VersionDocumentStyleMap.Normalize(flatText);
        return _rows.TryGetValue(key, out var q) && q.Count > 0 ? q.Dequeue() : null;
    }

    private static IEnumerable<XElement> EnumerateTables(XElement container)
    {
        foreach (var child in container.Elements())
        {
            if (child.Name == W + "tbl") { yield return child; continue; }
            if (child.Name == W + "sdt")
            {
                var content = child.Element(W + "sdtContent");
                if (content is not null)
                    foreach (var table in EnumerateTables(content)) yield return table;
            }
            else if (child.Name == W + "customXml")
            {
                foreach (var table in EnumerateTables(child)) yield return table;
            }
        }
    }

    private static List<double> ReadGridWidths(XElement table)
    {
        var widths = table.Element(W + "tblGrid")?.Elements(W + "gridCol")
            .Select(x => double.TryParse(x.Attribute(W + "w")?.Value, out var v) && v > 0 ? v : 1d)
            .ToList() ?? new List<double>();
        return widths.Count > 0 ? widths : new List<double> { 1d };
    }

    private static string VisibleParagraph(XElement paragraph, NativeDocumentReader.NumberingContext numbering)
    {
        var pieces = new List<string>();
        foreach (var node in paragraph.Descendants())
        {
            if (!ReferenceEquals(node.Ancestors(W + "p").FirstOrDefault(), paragraph)) continue;
            if (node.Name == W + "t" || node.Name == W + "delText") pieces.Add(node.Value);
            else if (node.Name == W + "tab") pieces.Add("\t");
            else if (node.Name == W + "br" || node.Name == W + "cr") pieces.Add("\n");
        }
        var text = string.Concat(pieces).Trim();
        if (text.Length == 0) return "";
        var info = NativeDocumentReader.NumberInfo(paragraph, numbering, W);
        if (!string.IsNullOrWhiteSpace(info.Label) && !text.StartsWith(info.Label, StringComparison.Ordinal))
            text = info.Label + " " + text;
        return text;
    }
}
