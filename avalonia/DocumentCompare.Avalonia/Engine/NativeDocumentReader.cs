using System.IO.Compression;
using System.Text;
using System.Globalization;
using System.Xml.Linq;

namespace DocumentCompare.Avalonia.Engine;

internal static class NativeDocumentReader
{
    static NativeDocumentReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static Encoding[] StrictTextEncodings() => new Encoding[]
    {
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
        Encoding.GetEncoding(949, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
        Encoding.GetEncoding(51949, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
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
        if (TryDecodeBom(bytes, out var bomText)) return NormalizeNewlines(bomText);
        if (TryDecodeBomlessUtf16(bytes, out var utf16Text)) return NormalizeNewlines(utf16Text);

        foreach (var encoding in StrictTextEncodings())
        {
            try { return NormalizeNewlines(encoding.GetString(bytes)); }
            catch (DecoderFallbackException) { }
        }

        // Pure CJK UTF-16 without a BOM can contain almost no NUL bytes, so the fast lane-bias
        // heuristic above cannot identify it.  Only after every normal text encoding failed,
        // compare strict LE/BE UTF-16 candidates by textual-script plausibility.
        if (TryDecodeBomlessUtf16ByTextPlausibility(bytes, out var cjkUtf16Text))
            return NormalizeNewlines(cjkUtf16Text);

        // Last-resort replacement fallback only after strict UTF-8 / CP949 / EUC-KR / UTF-16 fail.
        return NormalizeNewlines(Encoding.UTF8.GetString(bytes));
    }

    private static bool TryDecodeBom(byte[] bytes, out string text)
    {
        text = string.Empty;
        try
        {
            if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
            { text = new UTF32Encoding(bigEndian: true, byteOrderMark: false, throwOnInvalidCharacters: true).GetString(bytes, 4, bytes.Length - 4); return true; }
            if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
            { text = new UTF32Encoding(bigEndian: false, byteOrderMark: false, throwOnInvalidCharacters: true).GetString(bytes, 4, bytes.Length - 4); return true; }
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            { text = new UTF8Encoding(false, true).GetString(bytes, 3, bytes.Length - 3); return true; }
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            { text = new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true).GetString(bytes, 2, bytes.Length - 2); return true; }
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            { text = new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true).GetString(bytes, 2, bytes.Length - 2); return true; }
        }
        catch (DecoderFallbackException) { }
        return false;
    }

    private static bool TryDecodeBomlessUtf16(byte[] bytes, out string text)
    {
        text = string.Empty;
        if (bytes.Length < 4 || (bytes.Length & 1) != 0) return false;
        var pairs = Math.Min(bytes.Length / 2, 4096);
        var evenZero = 0; var oddZero = 0;
        for (var i = 0; i < pairs; i++)
        {
            if (bytes[i * 2] == 0) evenZero++;
            if (bytes[i * 2 + 1] == 0) oddZero++;
        }
        var evenRatio = evenZero / (double)pairs;
        var oddRatio = oddZero / (double)pairs;
        bool little;
        // UTF-16 text does not guarantee that the opposite byte lane is zero-free (for example
        // U+AE00 has a 0x00 low byte in LE).  Detect a strong lane bias rather than requiring an
        // unrealistically clean lane.
        if (oddRatio >= .25 && oddRatio - evenRatio >= .20 && oddRatio >= evenRatio * 2.0) little = true;
        else if (evenRatio >= .25 && evenRatio - oddRatio >= .20 && evenRatio >= oddRatio * 2.0) little = false;
        else return false;
        try
        {
            text = new UnicodeEncoding(bigEndian: !little, byteOrderMark: false, throwOnInvalidBytes: true)
                .GetString(bytes);
            return !text.Contains('\uFFFD');
        }
        catch (DecoderFallbackException) { text = string.Empty; return false; }
    }

    private static double TextPlausibility(string text)
    {
        if (string.IsNullOrEmpty(text)) return double.NegativeInfinity;
        double score = 0;
        var count = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            count++;
            var v = rune.Value;
            if (v is '\t' or '\n' or '\r' || v == 0x20) { score += 2.5; continue; }
            if (v < 0x20 || v is 0xFFFE or 0xFFFF) { score -= 12; continue; }
            if (v is >= 0x21 and <= 0x7E) { score += 3.5; continue; }
            if (v is >= 0xAC00 and <= 0xD7A3 || v is >= 0x1100 and <= 0x11FF || v is >= 0x3130 and <= 0x318F)
            { score += 6.0; continue; }
            if (v is >= 0x4E00 and <= 0x9FFF || v is >= 0x3400 and <= 0x4DBF)
            { score += 5.0; continue; }
            if (v is >= 0x3040 and <= 0x30FF) { score += 5.0; continue; }
            if (v is >= 0x00A0 and <= 0x024F || v is >= 0x2000 and <= 0x206F)
            { score += 2.5; continue; }
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or
                UnicodeCategory.TitlecaseLetter or UnicodeCategory.OtherLetter or
                UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber)
                score += 0.8;
            else if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate or
                     UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned)
                score -= 8.0;
            else score -= 0.5;
        }
        return score / Math.Max(1, count);
    }

    private static bool TryDecodeBomlessUtf16ByTextPlausibility(byte[] bytes, out string text)
    {
        text = string.Empty;
        if (bytes.Length < 4 || (bytes.Length & 1) != 0) return false;
        try
        {
            var le = new UnicodeEncoding(false, false, true).GetString(bytes);
            var be = new UnicodeEncoding(true, false, true).GetString(bytes);
            var leScore = TextPlausibility(le);
            var beScore = TextPlausibility(be);
            var best = Math.Max(leScore, beScore);
            var margin = Math.Abs(leScore - beScore);
            // This path runs only after all supported single-byte/UTF-8 decoders failed.  Still
            // require a strongly text-like result and a clear byte-order winner.
            if (best < 2.2 || margin < 0.75) return false;
            text = leScore > beScore ? le : be;
            return true;
        }
        catch (DecoderFallbackException) { return false; }
    }

    internal sealed record NumberLevel(int Start, string Format, string Pattern, int RestartAfterLevel);
    internal sealed record NumberInstance(int AbstractId, Dictionary<int, int> StartOverrides,
        Dictionary<int, NumberLevel> LevelOverrides);
    internal sealed record StyleNumber(string? BasedOn, int? NumId, int Level);
    internal sealed record ParagraphNumberInfo(string Label, int? NumId, int Level);

    internal sealed class NumberingContext
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
        var doc = LoadXmlPart(archive, "word/document.xml", 128L * 1024 * 1024)
            ?? throw new InvalidDataException("DOCX 본문(word/document.xml)을 찾을 수 없습니다.");
        var body = doc.Root?.Element(w + "body")
            ?? throw new InvalidDataException("DOCX 본문 구조를 읽을 수 없습니다.");
        var numbering = BuildNumberingContext(archive, w);

        string VisibleParagraph(XElement paragraph)
        {
            var text = TrimStructuralEdges(ParagraphText(paragraph, w));
            if (text.Length == 0) return string.Empty;
            var label = NumberLabel(paragraph, numbering, w);
            if (label.Length > 0 && !Compact(text).StartsWith(Compact(label), StringComparison.OrdinalIgnoreCase))
                text = (label + " " + text).Trim();
            return text;
        }

        var lines = new List<string>();
        foreach (var block in EnumerateVisibleBlocks(body, w))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (block.Name == w + "p")
            {
                var text = VisibleParagraph(block);
                if (text.Length > 0) lines.Add(text);
                // Textbox paragraphs are nested under a drawing inside the owner paragraph.
                // Read them once as their own logical paragraphs; the owner's direct-run scan
                // deliberately excludes them to prevent Alpha+BOX+BOX style duplication.
                foreach (var textBoxParagraph in block.Descendants(w + "txbxContent")
                             .SelectMany(x => x.Descendants(w + "p")))
                {
                    var nested = VisibleParagraph(textBoxParagraph);
                    if (nested.Length > 0) lines.Add(nested);
                }
            }
            else if (block.Name == w + "tbl")
            {
                foreach (var row in EnumerateTransparentChildren(block, w + "tr", w))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var cells = EnumerateTransparentChildren(row, w + "tc", w)
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

    private static IEnumerable<XElement> EnumerateVisibleBlocks(XElement container, XNamespace w)
    {
        foreach (var child in container.Elements())
        {
            if (child.Name == w + "p" || child.Name == w + "tbl")
            {
                yield return child;
                continue;
            }
            if (child.Name == w + "sdt")
            {
                var content = child.Element(w + "sdtContent");
                if (content is null) continue;
                foreach (var nested in EnumerateVisibleBlocks(content, w)) yield return nested;
                continue;
            }
            // customXml is a transparent block wrapper in many form/contract documents.  Its
            // paragraphs/tables are visible Word content and must participate in comparison.
            if (child.Name == w + "customXml")
            {
                foreach (var nested in EnumerateVisibleBlocks(child, w)) yield return nested;
            }
        }
    }

    internal static IEnumerable<XElement> EnumerateTransparentChildren(XElement container, XName target, XNamespace w)
    {
        foreach (var child in container.Elements())
        {
            if (child.Name == target) { yield return child; continue; }
            if (child.Name == w + "sdt")
            {
                var content = child.Element(w + "sdtContent");
                if (content is not null)
                    foreach (var nested in EnumerateTransparentChildren(content, target, w)) yield return nested;
                continue;
            }
            if (child.Name == w + "customXml")
                foreach (var nested in EnumerateTransparentChildren(child, target, w)) yield return nested;
        }
    }

    private static XDocument? LoadXmlPart(ZipArchive archive, string name, long maxUncompressedBytes = 32L * 1024 * 1024)
    {
        var entry = archive.GetEntry(name);
        if (entry is null) return null;
        if (entry.Length > maxUncompressedBytes)
            throw new InvalidDataException($"DOCX 내부 XML이 너무 큽니다: {name} ({entry.Length:N0} bytes)");
        using var stream = entry.Open();
        return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
    }

    internal static NumberingContext BuildNumberingContext(ZipArchive archive, XNamespace w)
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
                    var restartRaw = lvl.Element(w + "lvlRestart") is XElement lr && int.TryParse(Val(lr, w), out var rv)
                        ? rv : (ilvl > 0 ? ilvl : 0);
                    var restartAfter = restartRaw == 0 ? -1 : restartRaw - 1;
                    levels[ilvl] = new NumberLevel(start, fmt, pattern, restartAfter);
                }
                ctx.Abstracts[aid] = levels;
            }

            foreach (var num in numbering.Root.Elements(w + "num"))
            {
                if (!IntAttr(num, w + "numId", out var nid)) continue;
                var abs = num.Element(w + "abstractNumId");
                if (abs is null || !int.TryParse(Val(abs, w), out var aid)) continue;
                var startOverrides = new Dictionary<int, int>();
                var levelOverrides = new Dictionary<int, NumberLevel>();
                foreach (var ov in num.Elements(w + "lvlOverride"))
                {
                    if (!IntAttr(ov, w + "ilvl", out var ilvl)) continue;
                    var so = ov.Element(w + "startOverride");
                    if (so is not null && int.TryParse(Val(so, w), out var sv)) startOverrides[ilvl] = sv;
                    var lvl = ov.Element(w + "lvl");
                    if (lvl is null) continue;
                    NumberLevel? baseDef = null;
                    if (ctx.Abstracts.TryGetValue(aid, out var baseLevels) && baseLevels.TryGetValue(ilvl, out var foundBase))
                        baseDef = foundBase;
                    var start = IntVal(lvl.Element(w + "start"), w, baseDef?.Start ?? 1);
                    var fmt = Val(lvl.Element(w + "numFmt"), w) ?? baseDef?.Format ?? "decimal";
                    var pattern = Val(lvl.Element(w + "lvlText"), w) ?? baseDef?.Pattern ?? $"%{ilvl + 1}.";
                    var restartRaw = lvl.Element(w + "lvlRestart") is XElement lr && int.TryParse(Val(lr, w), out var rv)
                        ? rv : (baseDef is not null ? (baseDef.RestartAfterLevel < 0 ? 0 : baseDef.RestartAfterLevel + 1) : (ilvl > 0 ? ilvl : 0));
                    levelOverrides[ilvl] = new NumberLevel(start, fmt, pattern, restartRaw == 0 ? -1 : restartRaw - 1);
                }
                ctx.Instances[nid] = new NumberInstance(aid, startOverrides, levelOverrides);
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

    internal static ParagraphNumberInfo NumberInfo(XElement paragraph, NumberingContext ctx, XNamespace w)
    {
        var got = ParagraphNumPr(paragraph, ctx, w);
        if (got is null || got.Value.NumId == 0) return new ParagraphNumberInfo(string.Empty, null, 0);
        var (numId, level) = got.Value;
        if (!ctx.Instances.TryGetValue(numId, out var instance) ||
            !ctx.Abstracts.TryGetValue(instance.AbstractId, out var levels))
            return new ParagraphNumberInfo(string.Empty, numId, level);
        NumberLevel? EffectiveLevel(int lv) => instance.LevelOverrides.TryGetValue(lv, out var od)
            ? od : levels.TryGetValue(lv, out var bd) ? bd : null;
        var def = EffectiveLevel(level);
        if (def is null) return new ParagraphNumberInfo(string.Empty, numId, level);

        if (!ctx.Counters.TryGetValue(numId, out var counters)) ctx.Counters[numId] = counters = new();
        var changed = new HashSet<int> { level };
        var progress = true;
        while (progress)
        {
            progress = false;
            foreach (var k in counters.Keys.Where(k => k > level).ToList())
            {
                var kd = EffectiveLevel(k);
                if (kd is not null && kd.RestartAfterLevel >= 0 && changed.Contains(kd.RestartAfterLevel))
                { counters.Remove(k); changed.Add(k); progress = true; }
            }
        }
        var start = instance.StartOverrides.TryGetValue(level, out var ov) ? ov : def.Start;
        counters[level] = counters.TryGetValue(level, out var cur) ? cur + 1 : start;

        var label = System.Text.RegularExpressions.Regex.Replace(def.Pattern, @"%(\d+)", m =>
        {
            var lv = int.Parse(m.Groups[1].Value) - 1;
            var ld = EffectiveLevel(lv);
            var value = counters.TryGetValue(lv, out var v) ? v : (ld?.Start ?? 1);
            return FormatNumber(value, ld?.Format ?? "decimal");
        }).Trim();
        return new ParagraphNumberInfo(label, numId, level);
    }

    internal static string NumberLabel(XElement paragraph, NumberingContext ctx, XNamespace w) =>
        NumberInfo(paragraph, ctx, w).Label;

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

    private static string TrimStructuralEdges(string value)
    {
        value ??= string.Empty;
        var start = 0; var end = value.Length;
        static bool Noise(char c) => char.IsWhiteSpace(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format;
        while (start < end && Noise(value[start])) start++;
        while (end > start && Noise(value[end - 1])) end--;
        return start == 0 && end == value.Length ? value : value[start..end];
    }

    internal static bool IsVisibleRun(XElement run, XNamespace w)
    {
        if (run.Ancestors().Any(a => a.Name == w + "del" || a.Name == w + "moveFrom" || a.Name == w + "sdtPr"))
            return false;
        var rPr = run.Element(w + "rPr");
        return rPr?.Element(w + "vanish") is null && rPr?.Element(w + "webHidden") is null;
    }

    internal static IEnumerable<XElement> VisibleRunsInParagraph(XElement paragraph, XNamespace w)
    {
        foreach (var run in paragraph.Descendants(w + "r"))
        {
            // A run in a textbox/drawing has its own nested w:p.  It must be consumed by that
            // paragraph, not again by the outer paragraph that owns the drawing.
            if (!ReferenceEquals(run.Ancestors(w + "p").FirstOrDefault(), paragraph)) continue;
            if (IsVisibleRun(run, w)) yield return run;
        }
    }

    internal static string RunVisibleText(XElement run, XNamespace w)
    {
        var sb = new StringBuilder();
        // Direct run children only. Descendants would pull nested textbox text into the owner run.
        foreach (var node in run.Elements())
        {
            if (node.Name == w + "t") sb.Append(node.Value);
            else if (node.Name == w + "tab") sb.Append('\t');
            else if (node.Name == w + "br" || node.Name == w + "cr") sb.Append('\n');
            else if (node.Name == w + "noBreakHyphen") sb.Append('-');
        }
        return sb.ToString();
    }

    internal static string ParagraphVisibleText(XElement paragraph, XNamespace w)
    {
        var sb = new StringBuilder();
        foreach (var run in VisibleRunsInParagraph(paragraph, w))
            sb.Append(RunVisibleText(run, w));
        return sb.ToString();
    }

    private static string ParagraphText(XElement paragraph, XNamespace w) => ParagraphVisibleText(paragraph, w);

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
