using System.Text.Json;

namespace DocumentCompare.Avalonia.Models;

/// <summary>Persists a version project and its immutable content sidecar without modifying source files.</summary>
public static class VersionHistoryProjectStore
{
    public static async Task<VersionHistoryProjectVm> SaveAsync(
        VersionHistoryProjectVm project, VersionSnapshotStore sourceStore, string projectPath,
        CancellationToken cancellationToken = default)
    {
        var target = VersionSnapshotStore.ForProject(projectPath);
        var clone = JsonSerializer.Deserialize<VersionHistoryProjectVm>(JsonSerializer.Serialize(project))
            ?? throw new InvalidDataException("버전 프로젝트를 복제하지 못했습니다.");
        foreach (var version in clone.Versions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(version.SnapshotBlobId))
                sourceStore.CopyTo(target, version);
            else if (File.Exists(version.Path) && VersionFileImportPolicy.IsSupported(version.Path))
            {
                // A legacy path may now point at different bytes from the historical
                // version. Import the exact candidate, but never relabel a changed
                // source as the previous version when an original hash is recorded.
                var capturedHash = target.Import(version.Path);
                if (string.IsNullOrWhiteSpace(version.Sha256) ||
                    string.Equals(version.Sha256, capturedHash, StringComparison.OrdinalIgnoreCase))
                {
                    version.SnapshotBlobId = capturedHash;
                    version.Sha256 = capturedHash;
                }
                // A mismatched legacy entry remains unresolved. The original SHA
                // stays intact so the caller can report that the history was not
                // recovered instead of silently inventing an old revision.
            }
            // Missing legacy originals remain legacy entries rather than silently claiming
            // that the unavailable content was preserved.
        }
        var full = Path.GetFullPath(projectPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temp,
                JsonSerializer.Serialize(clone, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, full, overwrite: true);
            return clone;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
