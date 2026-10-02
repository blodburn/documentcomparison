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
    public static bool IsSupported(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".docx", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".txt", StringComparison.OrdinalIgnoreCase);
    }

    public static VersionFileImportResult AddPaths(VersionHistoryProjectVm project, IEnumerable<string> paths)
    {
        var added = 0; var duplicates = 0; var unsupported = 0; var missing = 0;
        var known = project.Versions.Select(x => Path.GetFullPath(x.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var path = Path.GetFullPath(raw);
            if (!File.Exists(path)) { missing++; continue; }
            if (!IsSupported(path)) { unsupported++; continue; }
            if (!known.Add(path)) { duplicates++; continue; }
            project.Versions.Add(new VersionFileVm
            {
                Label = Path.GetFileNameWithoutExtension(path),
                Path = path,
                Sha256 = Hash(path),
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
