namespace DocumentCompare.Avalonia.Engine;

/// <summary>
/// Adjacent-commit Tree-sitter symbol lineage on the first-parent path,
/// using committed Git blobs only. Does not conflate endpoint inference
/// with which intermediate commit introduced the movement.
/// </summary>
public static class GitAstHistoryComparer
{
    public sealed record Step(
        string BeforeCommit, string AfterCommit,
        NativeCodeVersionComparer.CrossFileAstReport Ast);

    public sealed record Timeline(
        bool Supported, IReadOnlyList<Step> Steps,
        string? Reason = null);

    private static bool IsCode(string path) =>
        path.EndsWith(".rs", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    public static Timeline Compare(GitCommitLineageComparer.Result lineage)
    {
        ArgumentNullException.ThrowIfNull(lineage);
        try
        {
            var commits = GitCommitLineageComparer.FirstParentPath(lineage);
            // Limit the *total* analyzed file endpoints across all commits,
            // rather than permitting 16 times the per-snapshot limit.
            const int maxChangedCodeEndpoints = 128;
            var pairs = new List<GitCommitLineageComparer.Result>();
            var total = 0;
            for (var i = 1; i < commits.Count; i++)
            {
                var pair = GitCommitLineageComparer.Compare(
                    lineage.RepositoryRoot, commits[i - 1], commits[i]);
                total += pair.Changes.Sum(change =>
                    (IsCode(change.BeforePath) ? 1 : 0) +
                    (IsCode(change.AfterPath) ? 1 : 0));
                if (total > maxChangedCodeEndpoints)
                    return new Timeline(false, Array.Empty<Step>(),
                        "Git 커밋 단계 AST 누적 파일 제한(128개)을 초과했습니다.");
                pairs.Add(pair);
            }
            var steps = new List<Step>(pairs.Count);
            foreach (var pair in pairs)
            {
                var ast = GitAstLineageComparer.Compare(pair);
                steps.Add(new Step(pair.BeforeCommit, pair.AfterCommit, ast));
            }
            return new Timeline(true, steps);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or
            UnauthorizedAccessException or TimeoutException or ArgumentException)
        {
            return new Timeline(false, Array.Empty<Step>(), ex.Message);
        }
    }
}
