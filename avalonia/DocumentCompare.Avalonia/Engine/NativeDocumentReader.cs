using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace DocumentCompare.Avalonia.Engine;

internal static class NativeDocumentReader
{
    static NativeDocumentReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static Encoding[] TextEncodings() => new Encoding[]
    {
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
        Encoding.UTF8,
        Encoding.GetEncoding(949),
        Encoding.GetEncoding(51949)
    };

    public static async Task<string> ReadAsync(string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("문서를 찾을 수 없습니다.", path);
        if (info.Length > 40L * 1024 * 1024)
            throw new InvalidOperationException($"{info.Name}: 파일당 40MB까지 지원합니다.");

        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".docx" => await Task.Run(() => ReadDocx(path, cancellationToken), cancellationToken),
            ".txt" => await Task.Run(() => ReadText(path), cancellationToken),
            _ => throw new InvalidOperationException($"지원하지 않는 문서 형식입니다: {ext}")
        };
    }

    private static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        foreach (var encoding in TextEncodings())
        {
            try
            {
                return NormalizeNewlines(encoding.GetString(bytes));
            }
            catch (DecoderFallbackException) { }
        }
        return NormalizeNewlines(Encoding.UTF8.GetString(bytes));
    }

    private sealed record NumberLevel(int Start, string Format, string Pattern);
    private sealed record NumberInstance(int AbstractId, Dictionary<int, int> Overrides);
    private sealed record StyleNumber(string? BasedOn, int? NumId, int Level);

    private sealed class NumberingContext
    {
        public Dictionary<int, Dictionary<int, NumberLevel>> Abstracts { get; } = new();
        public Dictionary<int, NumberInstance> Instances { get; } = new();
        public Dictionary<string, StyleNumber> Styles { get; } = new(StringComparer.Ordinal);
        public Dictionary<int, Dictionary<int, int>> Counters { get; } = new();
    }

    private static string ReadDocx(string path, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(path);
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var doc = LoadXmlPart(archive, "word/document.xml")
            ?? throw new InvalidDataException("DOCX 본문(word/document.xml)을 찾을 수 없습니다.");
        var body = doc.Root?.Element(w + "body")
            ?? throw new InvalidDataException("DOCX 본문 구조를 읽을 수 없습니다.");
        var numbering = BuildNumberingContext(archive, w);

        string VisibleParagraph(XElement paragraph)
        {
            var text = ParagraphText(paragraph, w).Trim();
            if (text.Length == 0) return string.Empty;
            var label = NumberLabel(paragraph, numbering, w);
            if (label.Length > 0 && !Compact(text).StartsWith(Compact(label), StringComparison.OrdinalIgnoreCase))
                text = (label + " " + text).Trim();
            return text;
        }

        var lines = new List<string>();
        foreach (var block in body.Elements())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (block.Name == w + "p")
            {
                var text = VisibleParagraph(block);
                if (text.Length > 0) lines.Add(text);
            }
            else if (block.Name == w + "tbl")
            {
                foreach (var row in block.Elements(w + "tr"))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Preserve paragraph boundaries inside table cells. This keeps legal article
                    // headings first-class while still restoring Word automatic numbering.
                    var cells = row.Elements(w + "tc")
                        .Select(tc => string.Join("\n", tc.Descendants(w + "p")
                            .Select(VisibleParagraph)
                            .Where(x => x.Length > 0)))
                        .ToList();
                    if (cells.Any(x => x.Length > 0)) lines.Add(string.Join(" | ", cells));
                }
            }
        }
        return string.Join("\n", lines);
    }

    private static XDocument? LoadXmlPart(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name);
        if (entry is null) return null;
        using var stream = entry.Open();
        return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
    }

    private static NumberingContext BuildNumberingContext(ZipArchive archive, XNamespace w)
    {
        var ctx = new NumberingContext();
        var numbering = LoadXmlPart(archive, "word/numbering.xml");
        if (numbering?.Root is not null)
        {
            foreach (var abs in numbering.Root.Elements(w + "abstractNum"))
            {
                if (!IntAttr(abs, w + "abstractNumId", out var aid)) continue;
                var levels = new Dictionary<int, NumberLevel>();
                foreach (var lvl in abs.Elements(w + "lvl"))
                {
                    var ilvl = IntAttr(lvl, w + "ilvl", out var lv) ? lv : 0;
                    var start = IntVal(lvl.Element(w + "start"), w, 1);
                    var fmt = Val(lvl.Element(w + "numFmt"), w) ?? "decimal";
                    var pattern = Val(lvl.Element(w + "lvlText"), w) ?? $"%{ilvl + 1}.";
                    levels[ilvl] = new NumberLevel(start, fmt, pattern);
                }
                ctx.Abstracts[aid] = levels;
            }

            foreach (var num in numbering.Root.Elements(w + "num"))
            {
                if (!IntAttr(num, w + "numId", out var nid)) continue;
                var abs = num.Element(w + "abstractNumId");
                if (abs is null || !int.TryParse(Val(abs, w), out var aid)) continue;
                var overrides = new Dictionary<int, int>();
                foreach (var ov in num.Elements(w + "lvlOverride"))
                {
                    if (!IntAttr(ov, w + "ilvl", out var ilvl)) continue;
                    var so = ov.Element(w + "startOverride");
                    if (so is not null && int.TryParse(Val(so, w), out var sv)) overrides[ilvl] = sv;
                }
                ctx.Instances[nid] = new NumberInstance(aid, overrides);
            }
        }

        var styles = LoadXmlPart(archive, "word/styles.xml");
        if (styles?.Root is not null)
        {
            foreach (var style in styles.Root.Elements(w + "style"))
            {
                var id = style.Attribute(w + "styleId")?.Value;
                if (string.IsNullOrEmpty(id)) continue;
                var basedOn = Val(style.Element(w + "basedOn"), w);
                var np = ReadNumPr(style.Element(w + "pPr"), w);
                ctx.Styles[id] = new StyleNumber(basedOn, np?.NumId, np?.Level ?? 0);
            }
        }
        return ctx;
    }

    private static (int NumId, int Level)? ParagraphNumPr(XElement paragraph, NumberingContext ctx, XNamespace w)
    {
        var pPr = paragraph.Element(w + "pPr");
        var direct = ReadNumPr(pPr, w);
        if (direct is not null) return direct;
        var styleId = Val(pPr?.Element(w + "pStyle"), w);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (!string.IsNullOrEmpty(styleId) && seen.Add(styleId) && ctx.Styles.TryGetValue(styleId, out var style))
        {
            if (style.NumId is int nid) return (nid, style.Level);
            styleId = style.BasedOn;
        }
        return null;
    }

    private static (int NumId, int Level)? ReadNumPr(XElement? pPr, XNamespace w)
    {
        var np = pPr?.Element(w + "numPr");
        if (np is null) return null;
        var num = np.Element(w + "numId");
        if (num is null || !int.TryParse(Val(num, w), out var nid)) return null;
        var level = 0;
        var ilvl = np.Element(w + "ilvl");
        if (ilvl is not null) int.TryParse(Val(ilvl, w), out level);
        return (nid, level);
    }

    private static string NumberLabel(XElement paragraph, NumberingContext ctx, XNamespace w)
    {
        var got = ParagraphNumPr(paragraph, ctx, w);
        if (got is null || got.Value.NumId == 0) return string.Empty;
        var (numId, level) = got.Value;
        if (!ctx.Instances.TryGetValue(numId, out var instance) ||
            !ctx.Abstracts.TryGetValue(instance.AbstractId, out var levels) ||
            !levels.TryGetValue(level, out var def)) return string.Empty;

        if (!ctx.Counters.TryGetValue(numId, out var counters)) ctx.Counters[numId] = counters = new();
        foreach (var k in counters.Keys.Where(k => k > level).ToList()) counters.Remove(k);
        var start = instance.Overrides.TryGetValue(level, out var ov) ? ov : def.Start;
        counters[level] = counters.TryGetValue(level, out var cur) ? cur + 1 : start;

        return System.Text.RegularExpressions.Regex.Replace(def.Pattern, @"%(\d+)", m =>
        {
            var lv = int.Parse(m.Groups[1].Value) - 1;
            var value = counters.TryGetValue(lv, out var v) ? v : (levels.TryGetValue(lv, out var ld) ? ld.Start : 1);
            var fmt = levels.TryGetValue(lv, out var fd) ? fd.Format : "decimal";
            return FormatNumber(value, fmt);
        }).Trim();
    }

    private static string FormatNumber(int n, string format)
    {
        var f = (format ?? "decimal").ToLowerInvariant();
        if (f == "upperroman") return Roman(n);
        if (f == "lowerroman") return Roman(n).ToLowerInvariant();
        if (f == "upperletter") return Alpha(n);
        if (f == "lowerletter") return Alpha(n).ToLowerInvariant();
        return n.ToString();
    }

    private static string Roman(int n)
    {
        if (n <= 0) return n.ToString();
        var values = new (int Value, string Text)[] { (1000,"M"),(900,"CM"),(500,"D"),(400,"CD"),(100,"C"),(90,"XC"),(50,"L"),(40,"XL"),(10,"X"),(9,"IX"),(5,"V"),(4,"IV"),(1,"I") };
        var sb = new StringBuilder();
        foreach (var (value, text) in values) while (n >= value) { sb.Append(text); n -= value; }
        return sb.ToString();
    }

    private static string Alpha(int n)
    {
        if (n <= 0) return n.ToString();
        var sb = new StringBuilder();
        while (n > 0) { n--; sb.Insert(0, (char)('A' + n % 26)); n /= 26; }
        return sb.ToString();
    }

    private static bool IntAttr(XElement e, XName name, out int value) => int.TryParse(e.Attribute(name)?.Value, out value);
    private static int IntVal(XElement? e, XNamespace w, int fallback) => int.TryParse(Val(e, w), out var v) ? v : fallback;
    private static string? Val(XElement? e, XNamespace w) => e?.Attribute(w + "val")?.Value;
    private static string Compact(string value) => string.Concat((value ?? string.Empty).Where(c => !char.IsWhiteSpace(c))).ToLowerInvariant();

    private static string ParagraphText(XElement paragraph, XNamespace w)
    {
        var sb = new StringBuilder();
        // Follow the current/visible Word text only.  Directly walking every descendant used
        // to pull deleted Track-Changes text and content-control backing values such as
        // "selected"/date metadata into the comparison document.
        foreach (var run in paragraph.Descendants(w + "r"))
        {
            if (run.Ancestors().Any(a => a.Name == w + "del" || a.Name == w + "moveFrom" || a.Name == w + "sdt"))
                continue;
            var rPr = run.Element(w + "rPr");
            if (rPr?.Element(w + "vanish") is not null || rPr?.Element(w + "webHidden") is not null)
                continue;
            foreach (var node in run.Descendants())
            {
                if (node.Name == w + "t") sb.Append(node.Value);
                else if (node.Name == w + "tab") sb.Append('\t');
                else if (node.Name == w + "br" || node.Name == w + "cr") sb.Append('\n');
            }
        }
        return sb.ToString();
    }

    internal static string NormalizeNewlines(string value) =>
        (value ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');

    internal static string CollapseSpaces(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var sb = new StringBuilder(value.Length);
        var pending = false;
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch)) { pending = sb.Length > 0; continue; }
            if (pending) sb.Append(' ');
            pending = false;
            sb.Append(ch);
        }
        return sb.ToString().Trim();
    }
}
