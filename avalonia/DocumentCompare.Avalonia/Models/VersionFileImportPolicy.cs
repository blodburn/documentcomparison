using System.Security.Cryptography;

namespace DocumentCompare.Avalonia.Models;

public sealed class VersionFileImportResult
{
    public int Added { get; init; }
    public int Duplicates { get; init; }
    public int Unsupported { get; init; }
    public int Missing { get; init; }
}

public static class VersionFileImportPolicy
{
    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".rs", ".cs", ".py", ".js", ".jsx", ".ts", ".tsx", ".java",
        ".c", ".h", ".cc", ".cpp", ".hpp", ".go", ".rb", ".php",
        ".swift", ".kt", ".kts", ".sql", ".sh", ".ps1", ".json",
        ".yaml", ".yml", ".toml", ".xml", ".html", ".css", ".md"
    };

    public static bool IsCodeFile(string path) => CodeExtensions.Contains(Path.GetExtension(path));
    public static bool IsSupported(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".docx", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".txt", StringComparison.OrdinalIgnoreCase)
            || CodeExtensions.Contains(ext);
    }

    public static VersionFileImportResult AddPaths(VersionHistoryProjectVm project, IEnumerable<string> paths, VersionSnapshotStore? snapshotStore = null)
    {
        var added = 0; var duplicates = 0; var unsupported = 0; var missing = 0;
        var known = project.Versions.Select(x => Path.GetFullPath(x.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var path = Path.GetFullPath(raw);
            if (!File.Exists(path)) { missing++; continue; }
            if (!IsSupported(path)) { unsupported++; continue; }
            var blobId = snapshotStore is null ? Hash(path) : snapshotStore.Import(path);
            // Legacy imports preserve the original path-only duplicate rule. Snapshot-enabled
            // projects can capture a changed file at the same path as a distinct version.
            if (snapshotStore is null && !known.Add(path) || project.Versions.Any(v =>
                Path.GetFullPath(v.Path).Equals(path, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(v.Sha256, blobId, StringComparison.OrdinalIgnoreCase)))
            {
                duplicates++;
                continue;
            }
            known.Add(path);
            project.Versions.Add(new VersionFileVm
            {
                Label = Path.GetFileNameWithoutExtension(path),
                Path = path,
                Sha256 = blobId,
                SnapshotBlobId = snapshotStore is null ? null : blobId,
                AddedAtUtc = DateTime.UtcNow
            });
            added++;
        }
        return new VersionFileImportResult { Added = added, Duplicates = duplicates, Unsupported = unsupported, Missing = missing };
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
