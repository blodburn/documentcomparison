using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace DocumentCompare.Avalonia.Engine;

/// <summary>
/// Native Rust code-line comparator. If no native payload was built, the C#
/// fallback remains available. A present but invalid ABI fails loudly.
/// Payloads are embedded in the managed app, including single-file builds.
/// </summary>
public static class NativeCodeVersionComparer
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeResult
    {
        public IntPtr Changes;
        public nuint Count;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeLineChange
    {
        public uint Kind;
        public uint Reserved;
        public ulong BeforeStart;
        public ulong BeforeEnd;
        public ulong AfterStart;
        public ulong AfterEnd;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFolderResult
    {
        public IntPtr Entries;
        public nuint Count;
        public IntPtr Names;
        public nuint NamesLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFolderChange
    {
        public uint Kind;
        public uint Reserved;
        public uint BeforeOffset;
        public uint BeforeLength;
        public uint AfterOffset;
        public uint AfterLength;
    }

    public sealed record FolderChange(string Kind, string BeforePath, string AfterPath);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int FolderDiffDelegate(
        [In] byte[] before, nuint beforeLength, [In] byte[] after,
        nuint afterLength, out NativeFolderResult result);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FolderFreeDelegate(ref NativeFolderResult result);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint VersionDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DiffDelegate(
        [In] byte[] before, nuint beforeLength, [In] byte[] after,
        nuint afterLength, out NativeResult result);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FreeDelegate(ref NativeResult result);

    private sealed class Exports
    {
        // Keep the loaded library and its delegates rooted for the process life.
        public readonly IntPtr Handle;
        public readonly DiffDelegate Diff;
        public readonly FreeDelegate Free;
        public readonly FolderDiffDelegate FolderDiff;
        public readonly FolderFreeDelegate FolderFree;

        public Exports(IntPtr handle)
        {
            Handle = handle;
            var abi = Marshal.GetDelegateForFunctionPointer<VersionDelegate>(
                NativeLibrary.GetExport(handle, "dc_diff_abi_version"));
            if (abi() != 1)
                throw new InvalidDataException("Rust 코드 비교 ABI 버전이 일치하지 않습니다.");
            Diff = Marshal.GetDelegateForFunctionPointer<DiffDelegate>(
                NativeLibrary.GetExport(handle, "dc_diff_lines_utf8"));
            Free = Marshal.GetDelegateForFunctionPointer<FreeDelegate>(
                NativeLibrary.GetExport(handle, "dc_free_diff_result"));
            FolderDiff = Marshal.GetDelegateForFunctionPointer<FolderDiffDelegate>(
                NativeLibrary.GetExport(handle, "dc_compare_folders_utf8"));
            FolderFree = Marshal.GetDelegateForFunctionPointer<FolderFreeDelegate>(
                NativeLibrary.GetExport(handle, "dc_free_folder_result"));
        }
    }

    private static readonly Lazy<Exports?> Native = new(Load, true);

    public static bool IsAvailable => Native.Value is not null;

    public static CodeVersionComparer.Result Compare(
        string before, string after, out bool usedNative)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var exports = Native.Value;
        if (exports is null)
        {
            usedNative = false;
            return CodeVersionComparer.Compare(before, after);
        }

        usedNative = true;
        // Throw rather than replacing malformed UTF-16; Rust receives exact text.
        var encoding = new UTF8Encoding(false, true);
        var oldBytes = encoding.GetBytes(before);
        var newBytes = encoding.GetBytes(after);
        var status = exports.Diff(oldBytes, (nuint)oldBytes.Length,
            newBytes, (nuint)newBytes.Length, out var nativeResult);
        try
        {
            if (status != 0)
                throw new InvalidDataException($"Rust 코드 비교에 실패했습니다 (상태 {status}).");
            if (nativeResult.Count > (nuint)(oldBytes.Length + newBytes.Length + 1))
                throw new InvalidDataException("Rust 비교 결과 개수가 파일 크기를 초과합니다.");
            if (nativeResult.Count > 0 && nativeResult.Changes == IntPtr.Zero)
                throw new InvalidDataException("Rust 비교 결과 포인터가 비어 있습니다.");

            var changes = new List<CodeVersionComparer.LineChange>(checked((int)nativeResult.Count));
            var itemSize = Marshal.SizeOf<NativeLineChange>();
            if (itemSize != 40)
                throw new InvalidDataException("Rust 변경 이벤트 메모리 레이아웃이 일치하지 않습니다.");
            for (var i = 0; i < (int)nativeResult.Count; i++)
            {
                var raw = Marshal.PtrToStructure<NativeLineChange>(
                    IntPtr.Add(nativeResult.Changes, checked(i * itemSize)));
                if (raw.Reserved != 0)
                    throw new InvalidDataException("Rust ABI 예약 필드 값이 올바르지 않습니다.");
                var kind = raw.Kind switch
                {
                    1 => "추가",
                    2 => "삭제",
                    3 => "교체",
                    _ => throw new InvalidDataException("지원하지 않는 Rust 변경 유형입니다.")
                };
                changes.Add(new CodeVersionComparer.LineChange(kind,
                    checked((int)raw.BeforeStart), checked((int)raw.BeforeEnd),
                    checked((int)raw.AfterStart), checked((int)raw.AfterEnd)));
            }
            return new CodeVersionComparer.Result(CodeVersionComparer.DisplayLines(before),
                CodeVersionComparer.DisplayLines(after), changes);
        }
        finally
        {
            exports.Free(ref nativeResult);
        }
    }

    /// <summary>
    /// Compare two entire source folders through the native Rust engine.
    /// Folder mode intentionally has no C# fallback; a missing or mismatched
    /// native DLL is reported instead of silently omitting file moves.
    /// </summary>
    public static IReadOnlyList<FolderChange> CompareFolders(string before, string after)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(before);
        ArgumentException.ThrowIfNullOrWhiteSpace(after);
        var exports = Native.Value ?? throw new NotSupportedException(
            "폴더 비교에는 Rust 네이티브 엔진이 필요합니다. Rust를 포함해 빌드하세요.");
        if (!Directory.Exists(before) || !Directory.Exists(after))
            throw new DirectoryNotFoundException("비교할 폴더가 존재하지 않습니다.");
        var encoding = new UTF8Encoding(false, true);
        var left = encoding.GetBytes(Path.GetFullPath(before));
        var right = encoding.GetBytes(Path.GetFullPath(after));
        // Marshal an explicit pointer+length ABI, never a zero-terminated path.
        var status = exports.FolderDiff(left, (nuint)left.Length,
            right, (nuint)right.Length, out var output);
        try
        {
            if (status != 0)
                throw new InvalidDataException(
                    $"Rust 폴더 비교 실패 (상태 {status}, 파일·디렉터리 제한/권한/경로 확인).");
            if (output.Count > 40000 || output.NamesLength > 64UL * 1024 * 1024 ||
                (output.Count > 0 && output.Entries == IntPtr.Zero) ||
                (output.NamesLength > 0 && output.Names == IntPtr.Zero))
                throw new InvalidDataException("Rust 폴더 비교 결과가 허용 범위를 벗어났습니다.");

            var names = new byte[checked((int)output.NamesLength)];
            if (names.Length > 0) Marshal.Copy(output.Names, names, 0, names.Length);
            var size = Marshal.SizeOf<NativeFolderChange>();
            if (size != 24)
                throw new InvalidDataException("Rust 폴더 이벤트 ABI 크기가 일치하지 않습니다.");
            string Name(uint offset, uint length)
            {
                if ((ulong)offset + length > (ulong)names.Length)
                    throw new InvalidDataException("Rust 폴더 이벤트 경로의 범위가 잘못되었습니다.");
                return encoding.GetString(names, (int)offset, (int)length);
            }
            var events = new List<FolderChange>(checked((int)output.Count));
            for (var i = 0; i < (int)output.Count; i++)
            {
                var entry = Marshal.PtrToStructure<NativeFolderChange>(
                    IntPtr.Add(output.Entries, checked(i * size)));
                if (entry.Reserved != 0)
                    throw new InvalidDataException("Rust 폴더 이벤트 예약 필드가 잘못되었습니다.");
                var kind = entry.Kind switch
                {
                    1 => "추가",
                    2 => "삭제",
                    3 => "수정",
                    4 => "이동/이름 변경",
                    _ => throw new InvalidDataException("지원하지 않는 Rust 폴더 이벤트 유형입니다.")
                };
                var beforePath = Name(entry.BeforeOffset, entry.BeforeLength);
                var afterPath = Name(entry.AfterOffset, entry.AfterLength);
                if ((entry.Kind == 1 && (beforePath.Length != 0 || afterPath.Length == 0)) ||
                    (entry.Kind == 2 && (beforePath.Length == 0 || afterPath.Length != 0)) ||
                    ((entry.Kind == 3 || entry.Kind == 4) &&
                        (beforePath.Length == 0 || afterPath.Length == 0)))
                    throw new InvalidDataException("Rust 폴더 이벤트 경로가 누락되었습니다.");
                events.Add(new FolderChange(kind, beforePath, afterPath));
            }
            return events;
        }
        finally
        {
            exports.FolderFree(ref output);
        }
    }

    private static Exports? Load()
    {
        string? resourceName;
        string filename;
        if (OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            resourceName = "DocumentCompare.Native.win-x64.documentcompare_core.dll";
            filename = "documentcompare_core.dll";
        }
        else if (OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            resourceName = "DocumentCompare.Native.linux-x64.libdocumentcompare_core.so";
            filename = "libdocumentcompare_core.so";
        }
        else
        {
            return null;
        }

        using var stream = typeof(NativeCodeVersionComparer).Assembly
            .GetManifestResourceStream(resourceName);
        if (stream is null)
            return null; // Native core not built; intentional C# fallback.

        using var contents = new MemoryStream();
        stream.CopyTo(contents);
        var bytes = contents.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var folder = Path.Combine(Path.GetTempPath(), "DocumentCompare", "native", hash);
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, filename);
        if (!File.Exists(target))
        {
            var staging = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllBytes(staging, bytes);
                try
                {
                    File.Move(staging, target);
                }
                catch (IOException) when (File.Exists(target))
                {
                    // Another process extracted the same embedded native image.
                    // Verify that it did not publish unrelated library bytes.
                }
            }
            finally
            {
                if (File.Exists(staging)) File.Delete(staging);
            }
        }
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(File.ReadAllBytes(target)),
                SHA256.HashData(bytes)))
            throw new InvalidDataException("추출된 Rust 네이티브 라이브러리의 해시가 일치하지 않습니다.");
        var handle = NativeLibrary.Load(target);
        try
        {
            return new Exports(handle);
        }
        catch
        {
            NativeLibrary.Free(handle);
            throw;
        }
    }
}
