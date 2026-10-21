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
        var left = Split(before);
        var right = Split(after);
        var prefix = 0;
        while (prefix < left.Length && prefix < right.Length && left[prefix] == right[prefix]) prefix++;
        var suffix = 0;
        while (suffix < left.Length - prefix && suffix < right.Length - prefix &&
               left[left.Length - suffix - 1] == right[right.Length - suffix - 1]) suffix++;
        var a = left.Length - prefix - suffix;
        var b = right.Length - prefix - suffix;
        if (a == 0 && b == 0) return new(left, right, Array.Empty<LineChange>());
        if ((long)a * b > 4_000_000)
            return new(left, right, new[] { new LineChange("교체", prefix, prefix + a, prefix, prefix + b) });

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
        return new(left, right, changes);
    }

    public static string ReadCodeFile(string path)
    {
        if (!DocumentCompare.Avalonia.Models.VersionFileImportPolicy.IsCodeFile(path))
            throw new InvalidDataException("지원하지 않는 코드 파일 형식입니다.");
        if (new FileInfo(path).Length > 40 * 1024 * 1024)
            throw new InvalidDataException("코드 파일은 최대 40MB까지 지원합니다.");
        return new UTF8Encoding(false, true).GetString(File.ReadAllBytes(path)).TrimStart('\uFEFF');
    }

    private static string[] Split(string value)
    {
        if (value.Length == 0) return Array.Empty<string>();
        var lines = value.Split('\n');
        // A final LF terminator isn't an extra source line.
        return value.EndsWith('\n') ? lines[..^1] : lines;
    }
}
