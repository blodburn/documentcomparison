using System.Security.Cryptography;

namespace DocumentCompare.Avalonia.Models;

/// <summary>
/// Content-addressed immutable copies of imported document versions.
/// Saved projects keep their blobs in a sidecar next to the .dcv.json file.
/// Unsaved projects use a temporary staging folder until the first Save.
/// </summary>
public sealed class VersionSnapshotStore
{
    public string Root { get; }
    private readonly bool _temporary;

    private VersionSnapshotStore(string root, bool temporary = false)
    {
        Root = Path.GetFullPath(root);
        _temporary = temporary;
    }

    public static VersionSnapshotStore CreateTemporary() =>
        new(Path.Combine(Path.GetTempPath(), "DocumentCompare", "staging", Guid.NewGuid().ToString("N")), true);

    public void CleanupTemporary()
    {
        if (_temporary && Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }

    public static VersionSnapshotStore ForProject(string projectPath) =>
        new(Path.GetFullPath(projectPath) + ".assets");

    public string Import(string originalPath)
    {
        if (!VersionFileImportPolicy.IsSupported(originalPath))
            throw new InvalidDataException("지원하지 않는 버전 파일 형식입니다.");
        var extension = Path.GetExtension(originalPath).ToLowerInvariant();
        var length = new FileInfo(originalPath).Length;
        if (length > 40L * 1024 * 1024)
            throw new InvalidDataException("버전 파일당 40MB까지 지원합니다.");
        var objects = Path.Combine(Root, "objects");
        Directory.CreateDirectory(objects);
        var temp = Path.Combine(objects, ".staging-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var input = new FileStream(originalPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                input.CopyTo(output);

            var blobId = HashFile(temp);
            var target = ObjectPath(blobId, extension);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target))
            {
                if (HashFile(target) != blobId)
                    throw new InvalidDataException("버전 저장소의 기존 파일이 손상되었습니다.");
            }
            else
            {
                File.Move(temp, target);
            }
            return blobId;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public string Resolve(VersionFileVm version)
    {
        if (string.IsNullOrWhiteSpace(version.SnapshotBlobId))
        {
            // Legacy projects retain file paths, but their recorded checksum
            // must still protect against silently displaying newer file bytes
            // as an older revision.
            if (!string.IsNullOrWhiteSpace(version.Sha256) && File.Exists(version.Path) &&
                !HashFile(version.Path).Equals(version.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("이전 형식 버전의 원본 내용이 기록된 해시와 다릅니다.");
            return version.Path;
        }
        if (!string.IsNullOrWhiteSpace(version.Sha256) &&
            !version.Sha256.Equals(version.SnapshotBlobId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("버전 메타데이터와 스냅샷 해시가 일치하지 않습니다.");
        var path = ObjectPath(version.SnapshotBlobId, Path.GetExtension(version.Path).ToLowerInvariant());
        if (!File.Exists(path)) throw new FileNotFoundException("저장된 버전 스냅샷을 찾지 못했습니다.", path);
        if (HashFile(path) != version.SnapshotBlobId.ToLowerInvariant())
            throw new InvalidDataException("저장된 버전 스냅샷의 무결성 검증에 실패했습니다.");
        return path;
    }

    public void CopyTo(VersionSnapshotStore target, VersionFileVm version)
    {
        if (string.IsNullOrWhiteSpace(version.SnapshotBlobId)) return;
        var source = Resolve(version);
        var copied = target.Import(source);
        if (!string.Equals(copied, version.SnapshotBlobId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("스냅샷 복사 중 파일 해시가 달라졌습니다.");
    }

    private string ObjectPath(string blobId, string extension)
    {
        if (blobId.Length != 64 || blobId.Any(c => !Uri.IsHexDigit(c)) ||
            !VersionFileImportPolicy.IsSupported("file" + extension))
            throw new InvalidDataException("스냅샷 식별자 또는 확장자가 올바르지 않습니다.");
        var hash = blobId.ToLowerInvariant();
        return Path.Combine(Root, "objects", hash[..2], hash + extension);
    }

    public static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
