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

    private static string ReadDocx(string path, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(path);
        var entry = archive.GetEntry("word/document.xml")
            ?? throw new InvalidDataException("DOCX 본문(word/document.xml)을 찾을 수 없습니다.");
        using var stream = entry.Open();
        var doc = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var body = doc.Root?.Element(w + "body")
            ?? throw new InvalidDataException("DOCX 본문 구조를 읽을 수 없습니다.");

        var lines = new List<string>();
        foreach (var block in body.Elements())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (block.Name == w + "p")
            {
                var text = ParagraphText(block, w).Trim();
                if (text.Length > 0) lines.Add(text);
            }
            else if (block.Name == w + "tbl")
            {
                foreach (var row in block.Elements(w + "tr"))
                {
                    var cells = row.Elements(w + "tc")
                        .Select(tc => string.Join(" ", tc.Descendants(w + "p")
                            .Select(p => CollapseSpaces(ParagraphText(p, w).Trim()))
                            .Where(x => x.Length > 0)))
                        .ToList();
                    if (cells.Any(x => x.Length > 0))
                        lines.Add(string.Join(" | ", cells));
                }
            }
        }
        return string.Join("\n", lines);
    }

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
