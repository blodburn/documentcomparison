using System.Security.Cryptography;
using System.Text;
using DocumentCompare.Avalonia.Models;

namespace DocumentCompare.Avalonia.Engine;

/// <summary>
/// Adapts the existing half-open line comparison to the common event contract.
/// Locations refer to source FILE ranges, not AST nodes. UTF-8 and UTF-16
/// offsets include original line terminators (LF/CRLF/BOM) without normalization.
/// </summary>
public static class CodeVersionChangeMapper
{
    public static string ContentId(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    public static IReadOnlyList<UnifiedChangeEvent> Map(
        string beforeText, string afterText, CodeVersionComparer.Result comparison,
        string beforeVersionId, string afterVersionId, string beforeRelativePath, string afterRelativePath)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        var before = Boundaries(beforeText);
        var after = Boundaries(afterText);
        var events = new List<UnifiedChangeEvent>(comparison.Changes.Count);
        for (var i = 0; i < comparison.Changes.Count; i++)
        {
            var change = comparison.Changes[i];
            if (change.BeforeStart < 0 || change.BeforeEnd < change.BeforeStart ||
                change.BeforeEnd >= before.Count || change.AfterStart < 0 ||
                change.AfterEnd < change.AfterStart || change.AfterEnd >= after.Count)
                throw new InvalidDataException("코드 비교 결과의 위치가 실제 파일 범위를 벗어났습니다.");

            var kind = change.Kind switch
            {
                "추가" => UnifiedChangeKind.Insert,
                "삭제" => UnifiedChangeKind.Delete,
                "교체" => UnifiedChangeKind.Replace,
                _ => throw new InvalidDataException("지원하지 않는 코드 변경 유형입니다.")
            };
            var beforeLocation = new UnifiedChangeLocation(
                beforeVersionId, beforeRelativePath, UnifiedLocationKind.File,
                StartByte: before[change.BeforeStart].Byte,
                EndByte: before[change.BeforeEnd].Byte,
                StartUtf16: before[change.BeforeStart].Utf16,
                EndUtf16: before[change.BeforeEnd].Utf16);
            var afterLocation = new UnifiedChangeLocation(
                afterVersionId, afterRelativePath, UnifiedLocationKind.File,
                StartByte: after[change.AfterStart].Byte,
                EndByte: after[change.AfterEnd].Byte,
                StartUtf16: after[change.AfterStart].Utf16,
                EndUtf16: after[change.AfterEnd].Utf16);
            events.Add(UnifiedChangeEvents.Create(kind, beforeLocation, afterLocation,
                $"코드 줄 {change.Kind}", "bounded-line-diff", i));
        }
        return events;
    }

    private sealed record Boundary(int Byte, int Utf16);

    private static List<Boundary> Boundaries(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = new List<Boundary> { new(0, 0) };
        var offset = 0;
        var bytes = 0;
        while (offset < text.Length)
        {
            var newline = text.IndexOf('\n', offset);
            var end = newline < 0 ? text.Length : newline + 1;
            bytes = checked(bytes + Encoding.UTF8.GetByteCount(text.AsSpan(offset, end - offset)));
            result.Add(new Boundary(bytes, end));
            offset = end;
        }
        return result;
    }
}
