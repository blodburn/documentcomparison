using DocumentCompare.Avalonia.Models;

namespace DocumentCompare.Avalonia.Engine;

/// <summary>Adapts read-only Git commits/blob identities to existing review UI.</summary>
public static class GitLineageReviewMapper
{
    public static IReadOnlyList<NativeCodeVersionComparer.FolderChange> WithTimeline(
        GitCommitLineageComparer.Result lineage,
        GitAstHistoryComparer.Timeline timeline)
    {
        var combined = new List<NativeCodeVersionComparer.FolderChange>(Map(lineage));
        if (!timeline.Supported) return combined;
        foreach (var step in timeline.Steps)
        {
            if (!step.Ast.Supported) continue;
            for (var i = 0; i < step.Ast.Moves.Count; i++)
            {
                var move = step.Ast.Moves[i];
                var before = new UnifiedChangeLocation(step.BeforeCommit, move.BeforePath,
                    UnifiedLocationKind.CodeNode, $"{move.SymbolKind}:{move.SymbolName}",
                    move.BeforeStart, move.BeforeEnd);
                var after = new UnifiedChangeLocation(step.AfterCommit, move.AfterPath,
                    UnifiedLocationKind.CodeNode, $"{move.SymbolKind}:{move.SymbolName}",
                    move.AfterStart, move.AfterEnd);
                var evidence = move.ContentChanged
                    ? $"Git step Tree-sitter lexical Dice {move.SimilarityPermille}/1000; candidate"
                    : "Git step Tree-sitter identical unique declaration";
                var evt = UnifiedChangeEvents.Create(UnifiedChangeKind.Move, before, after,
                    "커밋별 선언 이동", evidence, i);
                var kind = move.ContentChanged
                    ? "커밋별 이동·수정 후보(AST)" : "커밋별 선언 이동(AST)";
                combined.Add(new NativeCodeVersionComparer.FolderChange(
                    kind,
                    $"{step.BeforeCommit[..12]} · {move.BeforePath} · {move.SymbolKind} {move.SymbolName}",
                    $"{step.AfterCommit[..12]} · {move.AfterPath}" +
                        (move.ContentChanged ? $" · 유사도 {move.SimilarityPermille / 10.0:F1}% (후보)" : ""),
                    evt.EventId));
            }
        }
        return combined;
    }

    public static IReadOnlyList<NativeCodeVersionComparer.FolderChange> WithAst(
        GitCommitLineageComparer.Result lineage,
        NativeCodeVersionComparer.CrossFileAstReport report)
    {
        var changes = new List<NativeCodeVersionComparer.FolderChange>(Map(lineage));
        if (!report.Supported) return changes;
        for (var i = 0; i < report.Moves.Count; i++)
        {
            var move = report.Moves[i];
            var before = new UnifiedChangeLocation(lineage.BeforeCommit,
                move.BeforePath, UnifiedLocationKind.CodeNode,
                $"{move.SymbolKind}:{move.SymbolName}", move.BeforeStart, move.BeforeEnd);
            var after = new UnifiedChangeLocation(lineage.AfterCommit,
                move.AfterPath, UnifiedLocationKind.CodeNode,
                $"{move.SymbolKind}:{move.SymbolName}", move.AfterStart, move.AfterEnd);
            var kind = move.ContentChanged
                ? "Git 함수 이동·수정 후보(AST)" : "Git 선언 이동(AST)";
            var evidence = move.ContentChanged
                ? $"Tree-sitter Git blob lexical Dice {move.SimilarityPermille}/1000; not proven"
                : "Tree-sitter unique identical declaration from immutable Git blobs";
            var evt = UnifiedChangeEvents.Create(UnifiedChangeKind.Move, before, after,
                kind, evidence, i);
            changes.Add(new NativeCodeVersionComparer.FolderChange(
                kind,
                $"{move.BeforePath} · {move.SymbolKind} {move.SymbolName} · byte {move.BeforeStart}~{move.BeforeEnd}",
                $"{move.AfterPath} · {move.SymbolKind} {move.SymbolName} · byte {move.AfterStart}~{move.AfterEnd}" +
                (move.ContentChanged ? $" · 유사도 {move.SimilarityPermille / 10.0:F1}% (후보)" : ""),
                evt.EventId));
        }
        return changes;
    }

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
