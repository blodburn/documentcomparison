using DocumentCompare.Avalonia.Models;

namespace DocumentCompare.Avalonia.Engine;

/// <summary>
/// Keep exact file changes visible alongside additive syntactic cross-file
/// findings. AST findings never replace the file-level change ledger.
/// </summary>
public static class FolderAstReviewMapper
{
    public static IReadOnlyList<NativeCodeVersionComparer.FolderChange> Merge(
        IReadOnlyList<NativeCodeVersionComparer.FolderChange> files,
        NativeCodeVersionComparer.CrossFileAstReport report)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(report);
        var all = new List<NativeCodeVersionComparer.FolderChange>(files);
        if (!report.Supported) return all;

        for (var i = 0; i < report.Moves.Count; i++)
        {
            var move = report.Moves[i];
            var before = new UnifiedChangeLocation(
                "folder-before", move.BeforePath, UnifiedLocationKind.CodeNode,
                $"{move.SymbolKind}:{move.SymbolName}", move.BeforeStart, move.BeforeEnd);
            var after = new UnifiedChangeLocation(
                "folder-after", move.AfterPath, UnifiedLocationKind.CodeNode,
                $"{move.SymbolKind}:{move.SymbolName}", move.AfterStart, move.AfterEnd);
            var evidence = move.ContentChanged
                ? $"tree-sitter matching signature + lexical Dice {move.SimilarityPermille}/1000 (candidate)"
                : "tree-sitter exact full declaration unique match";
            var evt = UnifiedChangeEvents.Create(
                UnifiedChangeKind.Move, before, after,
                move.ContentChanged ? "파일 간 선언 이동·수정 후보" : "파일 간 선언 이동",
                evidence, i, move.ContentChanged ? null : 1.0);
            all.Add(new NativeCodeVersionComparer.FolderChange(
                move.ContentChanged ? "이동·수정 후보(AST)" : "선언 이동(AST)",
                $"{move.BeforePath} · {move.SymbolKind} {move.SymbolName} · byte {move.BeforeStart}~{move.BeforeEnd}",
                $"{move.AfterPath} · {move.SymbolKind} {move.SymbolName} · byte {move.AfterStart}~{move.AfterEnd}" +
                (move.ContentChanged ? $" · 어휘 유사도 {move.SimilarityPermille / 10.0:F1}%" : ""),
                evt.EventId));
        }
        return all;
    }
}
