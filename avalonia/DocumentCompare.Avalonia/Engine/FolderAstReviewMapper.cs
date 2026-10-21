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
            var evt = UnifiedChangeEvents.Create(
                UnifiedChangeKind.Move, before, after, "파일 간 선언 이동",
                "tree-sitter exact full declaration unique match", i);
            all.Add(new NativeCodeVersionComparer.FolderChange(
                "선언 이동(AST)",
                $"{move.BeforePath} · {move.SymbolKind} {move.SymbolName} · byte {move.BeforeStart}~{move.BeforeEnd}",
                $"{move.AfterPath} · {move.SymbolKind} {move.SymbolName} · byte {move.AfterStart}~{move.AfterEnd}",
                evt.EventId));
        }
        return all;
    }
}
