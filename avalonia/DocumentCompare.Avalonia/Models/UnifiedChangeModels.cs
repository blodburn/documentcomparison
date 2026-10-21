using System.Security.Cryptography;
using System.Text;

namespace DocumentCompare.Avalonia.Models;

/// <summary>Storage/engine event identifiers never depend on transient review badge numbers.</summary>
public enum UnifiedChangeKind
{
    Insert, Delete, Replace, Move, Rename, Format, Structure, Unknown
}

public enum UnifiedLocationKind { File, DocumentNode, CodeNode }

public sealed record UnifiedChangeLocation(
    string VersionId,
    string RelativePath,
    UnifiedLocationKind Kind,
    string? NodeId = null,
    int? StartByte = null,
    int? EndByte = null,
    int? StartUtf16 = null,
    int? EndUtf16 = null);

public sealed record UnifiedChangeEvent(
    string EventId,
    UnifiedChangeKind Kind,
    UnifiedChangeLocation? Before,
    UnifiedChangeLocation? After,
    string Description,
    string Evidence,
    double? Confidence = null);

public static class UnifiedChangeEvents
{
    public static UnifiedChangeEvent Create(
        UnifiedChangeKind kind,
        UnifiedChangeLocation? before,
        UnifiedChangeLocation? after,
        string description,
        string evidence,
        int ordinal = 0,
        double? confidence = null)
    {
        if (before is null && after is null)
            throw new ArgumentException("변경 이벤트에 전/후 위치 중 하나는 필요합니다.");
        if (confidence is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(confidence));
        // Stable within the same pair/structure; ordinal disambiguates identical text hunks.
        var fields = System.Text.Json.JsonSerializer.Serialize(new { kind, before, after, description, evidence, ordinal });
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fields))).ToLowerInvariant();
        return new UnifiedChangeEvent(id, kind, before, after, description, evidence, confidence);
    }
}
