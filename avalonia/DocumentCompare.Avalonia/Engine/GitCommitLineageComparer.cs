using System.Diagnostics;
using System.Text;

namespace DocumentCompare.Avalonia.Engine;

/// <summary>
/// Read-only Git commit/tree lineage. Content-addressed, unique blob identity
/// is proof of exact rename; Git's -M similarity results are explicitly only
/// candidates. Never checks out files or changes working-tree state.
/// </summary>
public static class GitCommitLineageComparer
{
    public sealed record Change(
        string Kind, string BeforePath, string AfterPath,
        string BeforeBlob, string AfterBlob, int? SimilarityPercent = null);

    public sealed record Result(
        string RepositoryRoot, string BeforeCommit, string AfterCommit,
        IReadOnlyList<Change> Changes);

    private sealed record GitOutput(int ExitCode, byte[] Data);
    private const int MaxFiles = 20_000;
    private const int MaxStdout = 16 * 1024 * 1024;

    public static Result Compare(string repositoryPath, string beforeRef, string afterRef)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        if (!Directory.Exists(repositoryPath))
            throw new DirectoryNotFoundException("선택한 Git 저장소 폴더가 존재하지 않습니다.");
        var root = Run(repositoryPath, "rev-parse", "--show-toplevel");
        var repositoryRoot = Decode(root.Data).TrimEnd('\n', '\r');
        if (!Path.IsPathFullyQualified(repositoryRoot) || !Directory.Exists(repositoryRoot))
            throw new InvalidDataException("유효하지 않은 Git 저장소 경로입니다.");

        var before = Resolve(repositoryRoot, beforeRef);
        var after = Resolve(repositoryRoot, afterRef);
        var ancestry = Run(repositoryRoot, true, "merge-base", "--is-ancestor", before, after);
        if (ancestry.ExitCode != 0)
            throw new InvalidDataException("변경 전 커밋은 변경 후 커밋의 조상이어야 합니다.");

        var oldTree = Tree(repositoryRoot, before);
        var newTree = Tree(repositoryRoot, after);
        var changes = new List<Change>();
        var removed = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var added = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var (path, blob) in oldTree)
        {
            if (!newTree.TryGetValue(path, out var next))
                removed.Add(path, blob);
            else if (blob != next)
                changes.Add(new Change("수정(Git)", path, path, blob, next));
        }
        foreach (var (path, blob) in newTree)
            if (!oldTree.ContainsKey(path)) added.Add(path, blob);

        // Strong proof: a unique content hash on each unmatched side.
        static Dictionary<string, List<string>> GroupByBlob(
            SortedDictionary<string, string> paths)
        {
            var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var (path, blob) in paths)
            {
                if (!groups.TryGetValue(blob, out var entries))
                    groups[blob] = entries = new List<string>();
                entries.Add(path);
            }
            return groups;
        }
        var oldBlobGroups = GroupByBlob(removed);
        var newBlobGroups = GroupByBlob(added);
        // Proof of one-to-one identity requires unique blobs in the FULL
        // commit trees, not only among added/deleted paths. An unchanged
        // copy elsewhere makes an apparent move ambiguous.
        var fullOldCounts = oldTree.Values.GroupBy(x => x, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var fullNewCounts = newTree.Values.GroupBy(x => x, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        foreach (var (blob, paths) in oldBlobGroups)
        {
            if (paths.Count != 1 || fullOldCounts[blob] != 1 ||
                !fullNewCounts.TryGetValue(blob, out var targetTotal) || targetTotal != 1 ||
                !newBlobGroups.TryGetValue(blob, out var targets) || targets.Count != 1)
                continue;
            var left = paths[0];
            var right = targets[0];
            changes.Add(new Change("정확한 파일 이동(Git)", left, right, blob, blob, 100));
            removed.Remove(left);
            added.Remove(right);
        }

        // The Git heuristic is evidence, not verified identity. A candidate
        // may be inaccurate after a major rewrite, even with a high score.
        var renameData = Run(repositoryRoot, "diff-tree", "--no-commit-id", "-r",
            "--no-ext-diff", "--no-textconv", "--name-status", "-z", "-M75%",
            before, after).Data;
        var fields = SplitNul(renameData);
        for (var i = 0; i < fields.Count;)
        {
            var status = fields[i++];
            if (status.Length == 0) continue;
            if (status[0] is 'R' && status.Length == 4 &&
                int.TryParse(status.AsSpan(1), out var score))
            {
                if (i + 1 >= fields.Count)
                    throw new InvalidDataException("Git rename 결과가 잘렸습니다.");
                var from = fields[i++];
                var to = fields[i++];
                if (score < 75 || !removed.TryGetValue(from, out var leftBlob) ||
                    !added.TryGetValue(to, out var rightBlob) || leftBlob == rightBlob)
                    continue;
                changes.Add(new Change("이름 변경 후보(Git)", from, to, leftBlob, rightBlob, score));
                removed.Remove(from);
                added.Remove(to);
            }
            else
            {
                // --name-status -z has exactly one path for non-renames.
                if (i >= fields.Count)
                    throw new InvalidDataException("Git diff 결과가 잘렸습니다.");
                i++;
            }
        }

        foreach (var (path, blob) in removed)
            changes.Add(new Change("삭제(Git)", path, "", blob, ""));
        foreach (var (path, blob) in added)
            changes.Add(new Change("추가(Git)", "", path, "", blob));
        changes.Sort((a, b) =>
        {
            var location = StringComparer.Ordinal.Compare(
                a.AfterPath.Length == 0 ? a.BeforePath : a.AfterPath,
                b.AfterPath.Length == 0 ? b.BeforePath : b.AfterPath);
            return location != 0 ? location :
                StringComparer.Ordinal.Compare(a.BeforePath, b.BeforePath);
        });
        return new Result(repositoryRoot, before, after, changes);
    }

    /// <summary>
    /// Enumerate each commit on the selected first-parent history, oldest
    /// to newest. Reject sideways ancestry and very long ranges rather than
    /// pretend that a two-snapshot diff proves an intermediate move.
    /// </summary>
    public static IReadOnlyList<string> FirstParentPath(Result lineage, int maxSteps = 16)
    {
        if (maxSteps is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(maxSteps));
        var lines = Decode(Run(lineage.RepositoryRoot, "rev-list",
                "--first-parent", "-n", (maxSteps + 2).ToString(),
                lineage.AfterCommit).Data)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var match = Array.FindIndex(lines,
            sha => string.Equals(sha, lineage.BeforeCommit, StringComparison.OrdinalIgnoreCase));
        if (match < 0)
            throw new InvalidDataException(
                $"변경 전 커밋이 후손의 최초 부모 계보 {maxSteps}단계 내에 없습니다.");
        if (match > maxSteps)
            throw new InvalidDataException("Git 커밋 단계 추적 제한을 초과했습니다.");
        var path = lines.Take(match + 1).Reverse().ToArray();
        if (path[0] != lineage.BeforeCommit || path[^1] != lineage.AfterCommit)
            throw new InvalidDataException("Git 최초 부모 계보 검증에 실패했습니다.");
        return path;
    }

    /// <summary>
    /// Retrieve an immutable Git object by its resolved blob ID; this never
    /// consults the working tree. Bounded for AST snapshot extraction.
    /// </summary>
    internal static byte[] ReadBlob(string root, string blobId, int maxBytes)
    {
        if (blobId.Length is not (40 or 64) || !blobId.All(Uri.IsHexDigit) ||
            maxBytes is <= 0 or > 2 * 1024 * 1024 + 1)
            throw new ArgumentException("잘못된 Git blob ID 또는 크기 제한입니다.");
        // Git writes only the raw object bytes to stdout.
        return Run(root, maxBytes, "cat-file", "blob", blobId).Data;
    }

    private static string Resolve(string root, string revision)
    {
        if (string.IsNullOrWhiteSpace(revision) || revision.Length > 128 ||
            revision[0] == '-' || revision.Any(char.IsControl) ||
            revision.Any(char.IsWhiteSpace))
            throw new ArgumentException("Git 커밋 참조 형식이 올바르지 않습니다.");
        var value = Decode(Run(root, "rev-parse", "--verify", "--end-of-options",
            revision + "^{commit}").Data).TrimEnd('\n', '\r');
        if (value.Length is not (40 or 64) || !value.All(Uri.IsHexDigit))
            throw new InvalidDataException("Git 커밋 ID 확인에 실패했습니다.");
        return value;
    }

    private static SortedDictionary<string, string> Tree(string root, string commit)
    {
        var bytes = Run(root, "ls-tree", "-r", "-z", "--full-tree", commit).Data;
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in SplitNul(bytes))
        {
            var tab = entry.IndexOf('\t');
            if (tab < 0) throw new InvalidDataException("Git 트리 항목이 잘못되었습니다.");
            var info = entry[..tab].Split(' ');
            if (info.Length != 3)
                throw new InvalidDataException("Git 트리 항목의 속성이 잘못되었습니다.");
            // Git submodules and symlinks aren't directly comparable file data.
            if (info[1] != "blob" || info[0] is not ("100644" or "100755"))
                continue;
            var path = entry[(tab + 1)..];
            if (path.Length == 0 || path.StartsWith('/') ||
                path.Split('/').Any(part => part is "" or "." or "..") ||
                info[2].Length is not (40 or 64) || !info[2].All(Uri.IsHexDigit))
                throw new InvalidDataException("Git 트리 경로 또는 blob ID가 잘못되었습니다.");
            if (!files.TryAdd(path, info[2]))
                throw new InvalidDataException("Git 트리에서 중복된 파일 경로를 발견했습니다.");
            if (files.Count > MaxFiles)
                throw new InvalidDataException("Git 커밋당 최대 20,000개의 파일을 비교할 수 있습니다.");
        }
        return files;
    }

    private static List<string> SplitNul(byte[] bytes)
    {
        if (bytes.Length != 0 && bytes[^1] != 0)
            throw new InvalidDataException("Git의 NUL 구분 출력이 잘렸습니다.");
        var decoder = new UTF8Encoding(false, true);
        var fields = new List<string>();
        var start = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != 0) continue;
            fields.Add(decoder.GetString(bytes, start, i - start));
            start = i + 1;
        }
        return fields;
    }

    private static string Decode(byte[] bytes) =>
        new UTF8Encoding(false, true).GetString(bytes);

    private static GitOutput Run(string root, params string[] arguments) =>
        Run(root, false, MaxStdout, arguments);

    private static GitOutput Run(string root, int maxStdout, params string[] arguments) =>
        Run(root, false, maxStdout, arguments);

    private static GitOutput Run(string root, bool allowExitOne, params string[] arguments) =>
        Run(root, allowExitOne, MaxStdout, arguments);

    private static GitOutput Run(string root, bool allowExitOne, int maxStdout, params string[] arguments)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        process.StartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        process.StartInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        process.StartInfo.Environment["GIT_NO_REPLACE_OBJECTS"] = "1";
        process.StartInfo.Environment["GIT_PAGER"] = "cat";
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var started = false;
        try
        {
            process.Start();
            started = true;
            var outputTask = ReadBoundedAsync(process.StandardOutput.BaseStream, maxStdout, timeout.Token);
            var errorTask = ReadBoundedAsync(process.StandardError.BaseStream, 16 * 1024, timeout.Token);
            Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync(timeout.Token))
                .GetAwaiter().GetResult();
            var result = new GitOutput(process.ExitCode, outputTask.Result);
            if (process.ExitCode != 0 && !(allowExitOne && process.ExitCode == 1))
                throw new InvalidDataException(
                    "Git 명령 실패: " + Decode(errorTask.Result).Trim());
            return result;
        }
        catch (OperationCanceledException ex)
        {
            throw new TimeoutException("Git 계보 조회 제한시간을 초과했습니다.", ex);
        }
        finally
        {
            if (started && !process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream input, int max, CancellationToken token)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, token);
            if (read == 0) return stream.ToArray();
            if (stream.Length + read > max)
                throw new InvalidDataException("Git 트리 조회 결과가 제한 용량을 초과했습니다.");
            stream.Write(buffer, 0, read);
        }
    }
}
