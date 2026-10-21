using System.Text;

namespace DocumentCompare.Avalonia.Engine;

/// <summary>
/// UI fallback for code versions while Rust's native Windows binary and interop contract
/// are being validated. Byte-identical logic is tested against the Rust line-diff corpus.
/// Does not claim to infer semantic equivalence or AST changes.
/// </summary>
public static class CodeVersionComparer
{
    public sealed record LineChange(string Kind, int BeforeStart, int BeforeEnd, int AfterStart, int AfterEnd);
    public sealed record Result(string[] BeforeLines, string[] AfterLines, IReadOnlyList<LineChange> Changes);

    public static Result Compare(string before, string after)
    {
        var left = Tokenize(before);
        var right = Tokenize(after);
        var prefix = 0;
        while (prefix < left.Length && prefix < right.Length && left[prefix] == right[prefix]) prefix++;
        var suffix = 0;
        while (suffix < left.Length - prefix && suffix < right.Length - prefix &&
               left[left.Length - suffix - 1] == right[right.Length - suffix - 1]) suffix++;
        var a = left.Length - prefix - suffix;
        var b = right.Length - prefix - suffix;
        if (a == 0 && b == 0) return new(Display(left), Display(right), Array.Empty<LineChange>());
        if ((long)a * b > 4_000_000)
            return new(Display(left), Display(right), new[] { new LineChange("교체", prefix, prefix + a, prefix, prefix + b) });

        var cols = b + 1;
        var dp = new int[(a + 1) * cols];
        for (var i = a - 1; i >= 0; i--)
        for (var j = b - 1; j >= 0; j--)
            dp[i * cols + j] = left[prefix + i] == right[prefix + j]
                ? 1 + dp[(i + 1) * cols + j + 1]
                : Math.Max(dp[(i + 1) * cols + j], dp[i * cols + j + 1]);

        var changes = new List<LineChange>();
        var (ai, bj) = (0, 0);
        while (ai < a || bj < b)
        {
            if (ai < a && bj < b && left[prefix + ai] == right[prefix + bj])
            { ai++; bj++; continue; }
            var startA = ai;
            var startB = bj;
            while (ai < a || bj < b)
            {
                if (ai < a && bj < b && left[prefix + ai] == right[prefix + bj]) break;
                if (ai < a && (bj == b || dp[(ai + 1) * cols + bj] >= dp[ai * cols + bj + 1])) ai++;
                else bj++;
            }
            changes.Add(new LineChange(ai > startA && bj > startB ? "교체" : ai > startA ? "삭제" : "추가",
                prefix + startA, prefix + ai, prefix + startB, prefix + bj));
        }
        return new(Display(left), Display(right), changes);
    }

    public static string ReadCodeFile(string path)
    {
        if (!DocumentCompare.Avalonia.Models.VersionFileImportPolicy.IsCodeFile(path))
            throw new InvalidDataException("지원하지 않는 코드 파일 형식입니다.");
        if (new FileInfo(path).Length > 40 * 1024 * 1024)
            throw new InvalidDataException("코드 파일은 최대 40MB까지 지원합니다.");
        return new UTF8Encoding(false, true).GetString(File.ReadAllBytes(path)).TrimStart('\uFEFF');
    }

    // Rust split_inclusive('\n') preserves the terminator, which is significant:
    // changing CRLF to LF or removing the terminal LF must be a real change.
    private static string[] Tokenize(string text)
    {
        if (text.Length == 0) return Array.Empty<string>();
        var result = new List<string>();
        var offset = 0;
        while (offset < text.Length)
        {
            var end = text.IndexOf('\n', offset);
            if (end < 0) { result.Add(text[offset..]); break; }
            result.Add(text[offset..(end + 1)]);
            offset = end + 1;
        }
        return result.ToArray();
    }

    // UI lines intentionally hide the line ending; comparison tokens do not.
    private static string[] Display(string[] tokens) => tokens.Select(token =>
    {
        if (!token.EndsWith('\n')) return token;
        var text = token[..^1];
        return text.EndsWith('\r') ? text[..^1] : text;
    }).ToArray();

    public static string[] DisplayLines(string text) => Display(Tokenize(text));

    // Preview the first rows and every changed location, including changes after row 10,000.
    // Preserve original (not compacted) line indices so badge navigation remains accurate.
    public static IReadOnlyList<int> PreviewLineIndices(int lineCount, IReadOnlyList<LineChange>? changes,
        int firstLines = 10000)
    {
        if (lineCount < 0 || firstLines < 0) throw new ArgumentOutOfRangeException();
        var visible = new SortedSet<int>();
        for (var i = 0; i < Math.Min(firstLines, lineCount); i++) visible.Add(i);
        if (changes is not null && lineCount > 0)
            foreach (var change in changes)
            {
                var anchor = Math.Clamp(change.AfterStart, 0, lineCount - 1);
                // All change markers must have a real source-code anchor, even beyond the cut-off.
                visible.Add(anchor);
                if (anchor > 0) visible.Add(anchor - 1);
                if (anchor + 1 < lineCount) visible.Add(anchor + 1);
            }
        return visible.ToArray();
    }
}
