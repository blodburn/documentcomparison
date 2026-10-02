using System.IO.Compression;
using System.Xml.Linq;
using DocumentCompare.Avalonia.Models;

namespace DocumentCompare.Avalonia.Engine;

public static class VersionFormattingInspector
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private sealed record RunInfo(string Text, string Signature, string Description);
    private sealed record ParagraphInfo(string Text, string Signature, string Description, List<RunInfo> Runs);

    public static List<VersionChangeVm> Compare(string previousPath, string currentPath)
    {
        if (!string.Equals(Path.GetExtension(previousPath), ".docx", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(currentPath), ".docx", StringComparison.OrdinalIgnoreCase))
            return new();

        try
        {
            using var oldZip = ZipFile.OpenRead(previousPath);
            using var newZip = ZipFile.OpenRead(currentPath);
            var oldDoc = Load(oldZip, "word/document.xml");
            var newDoc = Load(newZip, "word/document.xml");
            if (oldDoc?.Root is null || newDoc?.Root is null) return new();

            var result = new List<VersionChangeVm>();
            CompareParagraphs(oldDoc, newDoc, result);
            CompareTables(oldDoc, newDoc, result);
            CompareNamedStyles(oldZip, newZip, newDoc, result);
            return result;
        }
        catch
        {
            // Formatting inspection is additive. A malformed/unsupported formatting part must
            // never prevent the existing text/structure comparison from working.
            return new();
        }
    }

    private static XDocument? Load(ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(path);
        if (entry is null) return null;
        using var stream = entry.Open();
        return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
    }

    private static void CompareParagraphs(XDocument oldDoc, XDocument newDoc, List<VersionChangeVm> output)
    {
        var oldParagraphs = oldDoc.Descendants(W + "p").Select(Paragraph).Where(x => x.Text.Length > 0).ToList();
        var newParagraphs = newDoc.Descendants(W + "p").Select(Paragraph).Where(x => x.Text.Length > 0).ToList();
        var usedOld = new HashSet<int>();

        for (var ni = 0; ni < newParagraphs.Count; ni++)
        {
            var current = newParagraphs[ni];
            var oi = FindNearestExactText(oldParagraphs, current.Text, ni, usedOld);
            if (oi < 0) continue;
            usedOld.Add(oi);
            var previous = oldParagraphs[oi];

            if (!string.Equals(previous.Signature, current.Signature, StringComparison.Ordinal))
            {
                var delta = DescribeDelta(previous.Description, current.Description);
                output.Add(new VersionChangeVm
                {
                    Category = "문단서식",
                    Title = "문단 서식 변경",
                    Detail = $"{Short(current.Text)}\n{delta}",
                    AnchorText = current.Text
                });
            }

            CompareRuns(previous, current, output);
        }
    }

    private static int FindNearestExactText(List<ParagraphInfo> paragraphs, string text, int expected, HashSet<int> used)
    {
        var best = -1;
        var distance = int.MaxValue;
        for (var i = 0; i < paragraphs.Count; i++)
        {
            if (used.Contains(i) || !string.Equals(paragraphs[i].Text, text, StringComparison.Ordinal)) continue;
            var d = Math.Abs(i - expected);
            if (d < distance) { best = i; distance = d; }
        }
        return best;
    }

    private static ParagraphInfo Paragraph(XElement p)
    {
        var text = string.Concat(p.Descendants(W + "t").Select(x => x.Value));
        var pPr = p.Element(W + "pPr");
        var desc = ParagraphDescription(pPr);
        var sig = Canonical(pPr);
        var runs = p.Elements(W + "r").Select(Run).Where(x => x.Text.Length > 0).ToList();
        return new ParagraphInfo(text, sig, desc, runs);
    }

    private static RunInfo Run(XElement r)
    {
        var text = string.Concat(r.Descendants(W + "t").Select(x => x.Value));
        var rPr = r.Element(W + "rPr");
        return new RunInfo(text, Canonical(rPr), RunDescription(rPr));
    }

    private static void CompareRuns(ParagraphInfo previous, ParagraphInfo current, List<VersionChangeVm> output)
    {
        var used = new HashSet<int>();
        for (var ni = 0; ni < current.Runs.Count; ni++)
        {
            var nr = current.Runs[ni];
            var oi = -1;
            for (var j = 0; j < previous.Runs.Count; j++)
            {
                if (used.Contains(j) || previous.Runs[j].Text != nr.Text) continue;
                oi = j; break;
            }
            if (oi < 0) continue;
            used.Add(oi);
            var or = previous.Runs[oi];
            if (or.Signature == nr.Signature) continue;
            output.Add(new VersionChangeVm
            {
                Category = "문자서식",
                Title = "글자 서식 변경",
                Detail = $"“{Short(nr.Text, 48)}”\n{DescribeDelta(or.Description, nr.Description)}",
                AnchorText = current.Text
            });
        }
    }

    private static void CompareTables(XDocument oldDoc, XDocument newDoc, List<VersionChangeVm> output)
    {
        var oldTables = oldDoc.Descendants(W + "tbl").ToList();
        var newTables = newDoc.Descendants(W + "tbl").ToList();
        var max = Math.Max(oldTables.Count, newTables.Count);
        for (var i = 0; i < max; i++)
        {
            if (i >= oldTables.Count)
            {
                output.Add(new VersionChangeVm { Category = "표", Title = $"표 {i + 1} 추가", Detail = TableSummary(newTables[i]), AnchorText = TableAnchor(newTables[i]) });
                continue;
            }
            if (i >= newTables.Count)
            {
                output.Add(new VersionChangeVm { Category = "표", Title = $"표 {i + 1} 삭제", Detail = TableSummary(oldTables[i]), AnchorText = TableAnchor(oldTables[i]) });
                continue;
            }

            var oldTable = oldTables[i]; var newTable = newTables[i];
            var oldRows = oldTable.Elements(W + "tr").ToList();
            var newRows = newTable.Elements(W + "tr").ToList();
            var oldCells = oldRows.SelectMany(x => x.Elements(W + "tc")).ToList();
            var newCells = newRows.SelectMany(x => x.Elements(W + "tc")).ToList();
            var details = new List<string>();
            if (Canonical(oldTable.Element(W + "tblPr")) != Canonical(newTable.Element(W + "tblPr"))) details.Add("표 속성 변경");
            if (oldRows.Count != newRows.Count) details.Add($"행 {oldRows.Count} → {newRows.Count}");
            if (oldCells.Count != newCells.Count) details.Add($"셀 {oldCells.Count} → {newCells.Count}");
            var common = Math.Min(oldCells.Count, newCells.Count);
            var changedCells = Enumerable.Range(0, common)
                .Count(c => Canonical(oldCells[c].Element(W + "tcPr")) != Canonical(newCells[c].Element(W + "tcPr")));
            if (changedCells > 0) details.Add($"셀 서식 {changedCells}개 변경");
            if (details.Count > 0)
                output.Add(new VersionChangeVm { Category = "표", Title = $"표 {i + 1} 변경", Detail = string.Join(" · ", details), AnchorText = TableAnchor(newTable) });
        }
    }

    private static void CompareNamedStyles(ZipArchive oldZip, ZipArchive newZip, XDocument newDoc, List<VersionChangeVm> output)
    {
        var oldStyles = Load(oldZip, "word/styles.xml")?.Root;
        var newStyles = Load(newZip, "word/styles.xml")?.Root;
        if (oldStyles is null || newStyles is null) return;
        var oldMap = oldStyles.Elements(W + "style")
            .Where(x => x.Attribute(W + "styleId") is not null)
            .ToDictionary(x => x.Attribute(W + "styleId")!.Value, x => x, StringComparer.Ordinal);
        foreach (var style in newStyles.Elements(W + "style"))
        {
            var id = style.Attribute(W + "styleId")?.Value;
            if (string.IsNullOrWhiteSpace(id) || !oldMap.TryGetValue(id, out var oldStyle)) continue;
            if (Canonical(oldStyle) == Canonical(style)) continue;
            var name = style.Element(W + "name")?.Attribute(W + "val")?.Value ?? id;
            var oldDescription = StyleDescription(oldStyle);
            var newDescription = StyleDescription(style);
            output.Add(new VersionChangeVm
            {
                Category = "스타일",
                Title = "Word 스타일 정의 변경",
                Detail = $"{name}\n{DescribeDelta(oldDescription, newDescription)}",
                AnchorText = FindStyleAnchor(newDoc, id)
            });
        }
    }


    private static string? TableAnchor(XElement table)
    {
        return table.Descendants(W + "p")
            .Select(p => string.Concat(p.Descendants(W + "t").Select(x => x.Value)).Trim())
            .FirstOrDefault(x => x.Length > 0);
    }

    private static string? FindStyleAnchor(XDocument doc, string styleId)
    {
        foreach (var p in doc.Descendants(W + "p"))
        {
            var paragraphStyle = p.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val")?.Value;
            var runUsesStyle = p.Descendants(W + "rPr").Elements(W + "rStyle")
                .Any(x => string.Equals(x.Attribute(W + "val")?.Value, styleId, StringComparison.Ordinal));
            if (!string.Equals(paragraphStyle, styleId, StringComparison.Ordinal) && !runUsesStyle) continue;
            var text = string.Concat(p.Descendants(W + "t").Select(x => x.Value)).Trim();
            if (text.Length > 0) return text;
        }
        return null;
    }

    private static string StyleDescription(XElement style)
    {
        var parts = new List<string>();
        var p = ParagraphDescription(style.Element(W + "pPr"));
        var r = RunDescription(style.Element(W + "rPr"));
        if (p != "기본 문단 서식") parts.Add(p);
        if (r != "기본 문자 서식") parts.Add(r);
        var basedOn = style.Element(W + "basedOn")?.Attribute(W + "val")?.Value;
        if (!string.IsNullOrWhiteSpace(basedOn)) parts.Add($"기반 스타일={basedOn}");
        return parts.Count == 0 ? "기본 스타일" : string.Join(", ", parts);
    }

    private static string ParagraphDescription(XElement? pPr)
    {
        if (pPr is null) return "기본 문단 서식";
        var parts = new List<string>();
        Add(parts, "스타일", pPr.Element(W + "pStyle")?.Attribute(W + "val")?.Value);
        Add(parts, "정렬", pPr.Element(W + "jc")?.Attribute(W + "val")?.Value);
        var spacing = pPr.Element(W + "spacing");
        Add(parts, "앞간격", Twips(spacing?.Attribute(W + "before")?.Value));
        Add(parts, "뒤간격", Twips(spacing?.Attribute(W + "after")?.Value));
        Add(parts, "줄간격", spacing?.Attribute(W + "line")?.Value);
        var ind = pPr.Element(W + "ind");
        Add(parts, "왼쪽들여쓰기", Twips(ind?.Attribute(W + "left")?.Value));
        Add(parts, "첫줄", Twips(ind?.Attribute(W + "firstLine")?.Value));
        return parts.Count == 0 ? "기본 문단 서식" : string.Join(", ", parts);
    }

    private static string RunDescription(XElement? rPr)
    {
        if (rPr is null) return "기본 문자 서식";
        var parts = new List<string>();
        Add(parts, "글꼴", rPr.Element(W + "rFonts")?.Attribute(W + "eastAsia")?.Value ?? rPr.Element(W + "rFonts")?.Attribute(W + "ascii")?.Value);
        var halfPoints = rPr.Element(W + "sz")?.Attribute(W + "val")?.Value;
        if (double.TryParse(halfPoints, out var hp)) Add(parts, "크기", $"{hp / 2:0.##}pt");
        if (rPr.Element(W + "b") is not null) parts.Add("굵게");
        if (rPr.Element(W + "i") is not null) parts.Add("기울임");
        Add(parts, "밑줄", rPr.Element(W + "u")?.Attribute(W + "val")?.Value);
        Add(parts, "색상", rPr.Element(W + "color")?.Attribute(W + "val")?.Value);
        Add(parts, "강조", rPr.Element(W + "highlight")?.Attribute(W + "val")?.Value);
        return parts.Count == 0 ? "기본 문자 서식" : string.Join(", ", parts);
    }

    private static void Add(List<string> parts, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) parts.Add($"{name}={value}");
    }

    private static string? Twips(string? value)
    {
        if (!double.TryParse(value, out var t)) return value;
        return $"{t / 20:0.##}pt";
    }

    private static string DescribeDelta(string oldValue, string newValue) =>
        string.Equals(oldValue, newValue, StringComparison.Ordinal) ? newValue : $"{oldValue}\n→ {newValue}";

    private static string Canonical(XElement? element)
    {
        if (element is null) return "";
        var clone = new XElement(element);
        foreach (var e in clone.DescendantsAndSelf())
        {
            foreach (var attr in e.Attributes().Where(a => a.Name.LocalName.StartsWith("rsid", StringComparison.OrdinalIgnoreCase)).ToList()) attr.Remove();
        }
        clone.Descendants(W + "rPrChange").Remove();
        clone.Descendants(W + "pPrChange").Remove();
        clone.Descendants(W + "tblPrChange").Remove();
        clone.Descendants(W + "tcPrChange").Remove();
        return clone.ToString(SaveOptions.DisableFormatting);
    }

    private static string Short(string text, int max = 72)
    {
        var compact = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= max ? compact : compact[..max] + "…";
    }

    private static string TableSummary(XElement table)
    {
        var rows = table.Elements(W + "tr").ToList();
        var cells = rows.SelectMany(x => x.Elements(W + "tc")).Count();
        return $"행 {rows.Count} · 셀 {cells}";
    }
}
