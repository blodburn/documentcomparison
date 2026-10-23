using System.Text;

namespace DocumentCompare.Avalonia.Engine;

/// <summary>
/// Materializes only relevant immutable Git blobs into disposable temporary
/// directories and runs the same bounded native Tree-sitter cross-file engine.
/// Neither Git checkout/index nor user's working tree is mutated.
/// </summary>
public static class GitAstLineageComparer
{
    private const int MaxCandidatesPerSide = 128;
    private const int MaxBlobBytes = 2 * 1024 * 1024;
    private const long MaxTotalBytes = 32L * 1024 * 1024;

    public static NativeCodeVersionComparer.CrossFileAstReport Compare(
        GitCommitLineageComparer.Result lineage)
    {
        ArgumentNullException.ThrowIfNull(lineage);
        if (!NativeCodeVersionComparer.IsAvailable)
            return Unsupported("Rust 네이티브 엔진이 설치되지 않았습니다.");

        var old = new Dictionary<string, string>(StringComparer.Ordinal);
        var next = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var change in lineage.Changes)
        {
            if (IsCodeFile(change.BeforePath) && change.BeforeBlob.Length > 0)
                old.Add(change.BeforePath, change.BeforeBlob);
            if (IsCodeFile(change.AfterPath) && change.AfterBlob.Length > 0)
                next.Add(change.AfterPath, change.AfterBlob);
        }
        if (old.Count + next.Count == 0)
            return new NativeCodeVersionComparer.CrossFileAstReport(
                Array.Empty<NativeCodeVersionComparer.CrossFileMove>(), 0, 0);
        if (old.Count > MaxCandidatesPerSide || next.Count > MaxCandidatesPerSide)
            return Unsupported("Git AST 분석 파일 제한(전/후 각각 128개)을 초과했습니다.");

        var stagingRoot = Path.Combine(Path.GetTempPath(), "DocumentCompare", "git-ast",
            Guid.NewGuid().ToString("N"));
        var beforeDir = Path.Combine(stagingRoot, "before");
        var afterDir = Path.Combine(stagingRoot, "after");
        var usedBytes = 0L;
        var skipped = 0;
        try
        {
            Directory.CreateDirectory(beforeDir);
            Directory.CreateDirectory(afterDir);
            CopyCommittedFiles(old, lineage.RepositoryRoot, beforeDir, ref usedBytes, ref skipped);
            CopyCommittedFiles(next, lineage.RepositoryRoot, afterDir, ref usedBytes, ref skipped);
            var report = NativeCodeVersionComparer.CompareCrossFileAst(beforeDir, afterDir);
            return report with { SkippedFiles = report.SkippedFiles + skipped };
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or
            UnauthorizedAccessException or TimeoutException or ArgumentException)
        {
            return Unsupported("Git AST 분석 중 건너뛴 항목이 있습니다: " + ex.Message);
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingRoot))
                    Directory.Delete(stagingRoot, recursive: true);
            }
            catch (IOException) { /* Cleanup best-effort; process has no open writers. */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static NativeCodeVersionComparer.CrossFileAstReport Unsupported(string reason) =>
        new(Array.Empty<NativeCodeVersionComparer.CrossFileMove>(), 0, 0, false, reason);

    private static bool IsCodeFile(string path) =>
        path.EndsWith(".rs", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    private static void CopyCommittedFiles(
        Dictionary<string, string> files, string repository, string destination,
        ref long usedBytes, ref int skipped)
    {
        foreach (var (relative, blob) in files.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!TrySafePath(destination, relative, out var file))
            {
                skipped++;
                continue;
            }
            byte[] content;
            try
            {
                content = GitCommitLineageComparer.ReadBlob(repository, blob, MaxBlobBytes + 1);
            }
            catch (InvalidDataException ex) when (ex.Message.Contains("제한 용량", StringComparison.Ordinal))
            {
                skipped++;
                continue;
            }
            if (content.Length > MaxBlobBytes)
            {
                skipped++;
                continue;
            }
            try { _ = new UTF8Encoding(false, true).GetString(content); }
            catch (DecoderFallbackException)
            {
                skipped++;
                continue;
            }
            usedBytes += content.Length;
            if (usedBytes > MaxTotalBytes)
                throw new InvalidDataException("Git AST 전체 32MB 분석 제한을 초과했습니다.");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, content);
        }
    }

    private static bool TrySafePath(string destination, string relative, out string full)
    {
        full = "";
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) ||
            relative.Contains('\\') || relative.Contains(':') || relative.Contains('\0') ||
            relative.Split('/').Any(component => component is "" or "." or "..") ||
            relative.Split('/').Any(component => component is ".git" or "target" or
                "node_modules" or "bin" or "obj"))
            return false;
        var candidate = Path.GetFullPath(Path.Combine(destination,
            relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return false;
        full = candidate;
        return true;
    }
}
