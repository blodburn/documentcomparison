using System.IO.Compression;
using System.Xml.Linq;
using Avalonia.Media;

namespace DocumentCompare.Avalonia.Engine;

public sealed class VersionRunVisualStyle
{
    public string? FontFamily { get; init; }
    public double? FontSize { get; init; }
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public bool Underline { get; init; }
    public bool Strike { get; init; }
    public string? Color { get; init; }
}

public sealed class VersionRunVisualSpan
{
    public int Start { get; init; }
    public int End { get; init; }
    public VersionRunVisualStyle Style { get; init; } = new();
}

public sealed class VersionParagraphVisualStyle
{
    public string Text { get; init; } = "";
    public string Key { get; init; } = "";
    public TextAlignment Alignment { get; init; } = TextAlignment.Left;
    public double LeftIndent { get; init; }
    public double Before { get; init; }
    public double After { get; init; }
    public double? LineHeight { get; init; }
    public VersionRunVisualStyle DefaultRun { get; init; } = new();
    public List<VersionRunVisualSpan> Runs { get; init; } = new();
}

public sealed class VersionDocumentStyleMap
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private sealed record StyleDef(string? BasedOn, XElement? PPr, XElement? RPr);
    private readonly Dictionary<string, Queue<VersionParagraphVisualStyle>> _byText;
    public IReadOnlyList<VersionParagraphVisualStyle> Paragraphs { get; }

    private VersionDocumentStyleMap(List<VersionParagraphVisualStyle> paragraphs)
    {
        Paragraphs = paragraphs;
        _byText = paragraphs.Where(p => p.Key.Length > 0)
            .GroupBy(p => p.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => new Queue<VersionParagraphVisualStyle>(g), StringComparer.Ordinal);
    }

    public static VersionDocumentStyleMap? Load(string path)
    {
        if (!path.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var doc = Load(zip, "word/document.xml");
            if (doc?.Root is null) return null;
            var stylesDoc = Load(zip, "word/styles.xml");
            var styles = ParseStyles(stylesDoc);
            var defaultRPr = stylesDoc?.Root?.Element(W + "docDefaults")?.Element(W + "rPrDefault")?.Element(W + "rPr");
            var defaultPPr = stylesDoc?.Root?.Element(W + "docDefaults")?.Element(W + "pPrDefault")?.Element(W + "pPr");
            var list = new List<VersionParagraphVisualStyle>();
            foreach (var p in doc.Descendants(W + "p"))
            {
                if (p.Ancestors(W + "p").Any()) continue;
                var item = ReadParagraph(p, styles, defaultPPr, defaultRPr);
                if (item is not null) list.Add(item);
            }
            return new VersionDocumentStyleMap(list);
        }
        catch { return null; }
    }

    public VersionParagraphVisualStyle? Take(string text)
    {
        var key = Normalize(text);
        if (_byText.TryGetValue(key, out var q) && q.Count > 0) return q.Dequeue();
        var stripped = StripListLabel(key);
        if (stripped != key && _byText.TryGetValue(stripped, out q) && q.Count > 0) return q.Dequeue();
        return null;
    }

    public static string Normalize(string? text) => string.IsNullOrWhiteSpace(text)
        ? ""
        : string.Join(" ", text.Replace('\u00A0', ' ').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string StripListLabel(string text)
    {
        var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return text;
        var h = parts[0];
        var looks = h.EndsWith('.') || h.EndsWith(')') || h.StartsWith('(') || (h.Length > 0 && "①②③④⑤⑥⑦⑧⑨⑩".Contains(h[0]));
        return looks ? parts[1] : text;
    }

    private static VersionParagraphVisualStyle? ReadParagraph(XElement p, Dictionary<string, StyleDef> styles, XElement? defaultPPr, XElement? defaultRPr)
    {
        var pPr = p.Element(W + "pPr");
        var pStyle = pPr?.Element(W + "pStyle")?.Attribute(W + "val")?.Value;
        var pProps = MergeProps(defaultPPr, StyleChain(pStyle, styles).Select(x => x.PPr).Append(pPr));
        var baseRPr = MergeProps(defaultRPr, StyleChain(pStyle, styles).Select(x => x.RPr));
        var defaultRun = ReadRunStyle(baseRPr);
        var spans = new List<VersionRunVisualSpan>();
        var text = "";
        foreach (var run in p.Descendants(W + "r"))
        {
            if (!ReferenceEquals(run.Ancestors(W + "p").FirstOrDefault(), p)) continue;
            var vanish = run.Element(W + "rPr")?.Element(W + "vanish");
            if (vanish is not null && !IsOff(vanish)) continue;
            var runText = string.Concat(run.Descendants().Select(n => n.Name == W + "t" || n.Name == W + "delText" ? n.Value : n.Name == W + "tab" ? "\t" : n.Name == W + "br" || n.Name == W + "cr" ? "\n" : ""));
            if (runText.Length == 0) continue;
            var rPr = run.Element(W + "rPr");
            var rStyle = rPr?.Element(W + "rStyle")?.Attribute(W + "val")?.Value;
            var merged = MergeProps(baseRPr, StyleChain(rStyle, styles).Select(x => x.RPr).Append(rPr));
            var start = text.Length;
            text += runText;
            spans.Add(new VersionRunVisualSpan { Start = start, End = text.Length, Style = ReadRunStyle(merged) });
        }
        if (text.Length == 0) return null;
        return new VersionParagraphVisualStyle
        {
            Text = text,
            Key = Normalize(text),
            Alignment = ReadAlignment(pProps),
            LeftIndent = TwipsToDip(pProps?.Element(W + "ind")?.Attribute(W + "left")?.Value ?? pProps?.Element(W + "ind")?.Attribute(W + "start")?.Value),
            Before = TwipsToDip(pProps?.Element(W + "spacing")?.Attribute(W + "before")?.Value),
            After = TwipsToDip(pProps?.Element(W + "spacing")?.Attribute(W + "after")?.Value),
            LineHeight = ReadLineHeight(pProps, defaultRun.FontSize),
            DefaultRun = defaultRun,
            Runs = spans
        };
    }

    private static XDocument? Load(ZipArchive zip, string name)
    {
        var e = zip.GetEntry(name); if (e is null) return null;
        using var s = e.Open(); return XDocument.Load(s, LoadOptions.PreserveWhitespace);
    }

    private static Dictionary<string, StyleDef> ParseStyles(XDocument? doc)
    {
        var map = new Dictionary<string, StyleDef>(StringComparer.Ordinal);
        if (doc?.Root is null) return map;
        foreach (var s in doc.Root.Elements(W + "style"))
        {
            var id = s.Attribute(W + "styleId")?.Value; if (string.IsNullOrWhiteSpace(id)) continue;
            map[id] = new StyleDef(s.Element(W + "basedOn")?.Attribute(W + "val")?.Value, s.Element(W + "pPr"), s.Element(W + "rPr"));
        }
        return map;
    }

    private static IEnumerable<StyleDef> StyleChain(string? id, Dictionary<string, StyleDef> styles)
    {
        var stack = new Stack<StyleDef>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        while (!string.IsNullOrWhiteSpace(id) && seen.Add(id) && styles.TryGetValue(id, out var def))
        {
            stack.Push(def); id = def.BasedOn;
        }
        while (stack.Count > 0) yield return stack.Pop();
    }

    private static XElement? MergeProps(XElement? seed, IEnumerable<XElement?> sequence)
    {
        XElement? merged = seed is null ? null : new XElement(seed);
        foreach (var props in sequence)
        {
            if (props is null) continue;
            merged ??= new XElement(props.Name);
            foreach (var child in props.Elements())
            {
                merged.Elements(child.Name).Remove();
                merged.Add(new XElement(child));
            }
        }
        return merged;
    }

    private static VersionRunVisualStyle ReadRunStyle(XElement? rPr)
    {
        var fonts = rPr?.Element(W + "rFonts");
        var font = fonts?.Attribute(W + "eastAsia")?.Value ?? fonts?.Attribute(W + "ascii")?.Value ?? fonts?.Attribute(W + "hAnsi")?.Value;
        var sz = rPr?.Element(W + "sz")?.Attribute(W + "val")?.Value ?? rPr?.Element(W + "szCs")?.Attribute(W + "val")?.Value;
        double? fontSize = null;
        if (double.TryParse(sz, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var halfPt))
            fontSize = halfPt / 2.0 * 96.0 / 72.0;
        var color = rPr?.Element(W + "color")?.Attribute(W + "val")?.Value;
        if (string.Equals(color, "auto", StringComparison.OrdinalIgnoreCase) || color?.Length != 6 || color.Any(c => !Uri.IsHexDigit(c))) color = null;
        return new VersionRunVisualStyle
        {
            FontFamily = font,
            FontSize = fontSize,
            Bold = IsOn(rPr?.Element(W + "b")),
            Italic = IsOn(rPr?.Element(W + "i")),
            Underline = rPr?.Element(W + "u") is XElement u && !IsOff(u) && !string.Equals(u.Attribute(W + "val")?.Value, "none", StringComparison.OrdinalIgnoreCase),
            Strike = IsOn(rPr?.Element(W + "strike")),
            Color = color is null ? null : "#" + color
        };
    }

    private static TextAlignment ReadAlignment(XElement? pPr) => pPr?.Element(W + "jc")?.Attribute(W + "val")?.Value?.ToLowerInvariant() switch
    {
        "center" => TextAlignment.Center,
        "right" or "end" => TextAlignment.Right,
        "both" or "distribute" => TextAlignment.Justify,
        _ => TextAlignment.Left
    };

    private static double TwipsToDip(string? value)
    {
        if (!double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var t)) return 0;
        return t / 20.0 * 96.0 / 72.0;
    }

    private static double? ReadLineHeight(XElement? pPr, double? fontSize)
    {
        var spacing = pPr?.Element(W + "spacing");
        var raw = spacing?.Attribute(W + "line")?.Value;
        if (!double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var line)) return null;
        var rule = spacing?.Attribute(W + "lineRule")?.Value;
        if (rule is "exact" or "atLeast") return line / 20.0 * 96.0 / 72.0;
        return fontSize is double f ? Math.Max(f * 1.05, f * 1.2 * line / 240.0) : null;
    }

    private static bool IsOn(XElement? e) => e is not null && !IsOff(e);
    private static bool IsOff(XElement e)
    {
        var v = e.Attribute(W + "val")?.Value;
        return v is "0" or "false" or "off";
    }
}
