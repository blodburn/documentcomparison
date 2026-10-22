using DocumentCompare.Avalonia.Models;

namespace DocumentCompare.Avalonia.Engine;

/// <summary>Adapts read-only Git commits/blob identities to existing review UI.</summary>
public static class GitLineageReviewMapper
{
    public static IReadOnlyList<NativeCodeVersionComparer.FolderChange> Map(
        GitCommitLineageComparer.Result lineage)
    {
        ArgumentNullException.ThrowIfNull(lineage);
        var results = new List<NativeCodeVersionComparer.FolderChange>(lineage.Changes.Count);
        for (var i = 0; i < lineage.Changes.Count; i++)
        {
            var change = lineage.Changes[i];
            var kind = change.Kind switch
            {
                "추가(Git)" => UnifiedChangeKind.Insert,
                "삭제(Git)" => UnifiedChangeKind.Delete,
                "수정(Git)" => UnifiedChangeKind.Replace,
                "정확한 파일 이동(Git)" => UnifiedChangeKind.Move,
                "이름 변경 후보(Git)" => UnifiedChangeKind.Rename,
                _ => throw new InvalidDataException("지원하지 않는 Git 계보 변경 유형입니다.")
            };
            var before = change.BeforePath.Length == 0 ? null : new UnifiedChangeLocation(
                lineage.BeforeCommit, change.BeforePath, UnifiedLocationKind.File,
                change.BeforeBlob);
            var after = change.AfterPath.Length == 0 ? null : new UnifiedChangeLocation(
                lineage.AfterCommit, change.AfterPath, UnifiedLocationKind.File,
                change.AfterBlob);
            var evidence = change.Kind == "이름 변경 후보(Git)"
                ? $"Git rename heuristic {change.SimilarityPercent}% (not identity proof)"
                : "Git immutable commit tree and blob ID";
            var evt = UnifiedChangeEvents.Create(
                kind, before, after, change.Kind, evidence, i);
            results.Add(new NativeCodeVersionComparer.FolderChange(
                change.Kind, change.BeforePath,
                change.AfterPath + (change.Kind == "이름 변경 후보(Git)"
                    ? $" · Git 유사도 {change.SimilarityPercent}% (후보)" : ""),
                evt.EventId));
        }
        return results;
    }
}
