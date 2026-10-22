using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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

    public sealed record FolderChange(string Kind, string BeforePath, string AfterPath,
        string? EventId = null, string? Section = null);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int FolderDiffDelegate(
        [In] byte[] before, nuint beforeLength, [In] byte[] after,
        nuint afterLength, out NativeFolderResult result);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FolderFreeDelegate(ref NativeFolderResult result);

    public sealed record AstChange(
        string Kind, string SymbolKind, string BeforeName, string AfterName,
        int BeforeStart, int BeforeEnd, int AfterStart, int AfterEnd);
    public sealed record AstComparison(bool Supported, IReadOnlyList<AstChange> Changes, string? Reason = null);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeAstResult
    {
        public IntPtr Data;
        public nuint Length;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AstDiffDelegate(
        uint language, [In] byte[] before, nuint beforeLength,
        [In] byte[] after, nuint afterLength, out NativeAstResult result);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AstFreeDelegate(ref NativeAstResult result);

    public sealed record CrossFileMove(
        string SymbolKind, string SymbolName,
        string BeforePath, string AfterPath,
        int BeforeStart, int BeforeEnd, int AfterStart, int AfterEnd,
        bool ContentChanged = false, int SimilarityPermille = 1000,
        string? BeforeName = null, string? AfterName = null,
        bool Renamed = false, bool SignatureChanged = false);

    public sealed record CrossFileAstReport(
        IReadOnlyList<CrossFileMove> Moves,
        int AnalyzedFiles, int SkippedFiles,
        bool Supported = true, string? Reason = null,
        bool RefactorStageSkipped = false);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CrossFileAstDelegate(
        [In] byte[] before, nuint beforeLength,
        [In] byte[] after, nuint afterLength,
        out NativeAstResult result);

    public sealed record DocumentWhitespaceSpan(
        int OldStart, int OldEnd, int NewStart, int NewEnd);

    public sealed record DocumentWhitespaceResult(
        bool Eligible, IReadOnlyList<DocumentWhitespaceSpan> Spans);

    public sealed record DocumentPart(
        string Label, int Start, int End,
        int CoreStart, int CoreEnd, string Core);

    public sealed record DocumentHierarchyNode(
        int Index, int Level, int Parent, string Family, int? Ordinal);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DocumentHierarchyDelegate(
        [In] byte[] input, nuint length, out NativeAstResult result);

    public sealed record ExactDocumentPartPair(int Old, int New);
    public sealed record DocumentMoveCandidate(
        int Old, int New, double Similarity, double Containment, int ChildSupport);
    public sealed record DocumentMoveSelected(int Old, int New, double Score);
    public sealed record DocumentSameLabelCandidate(
        int Old, int New, double Similarity, bool Shape, bool Edge);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DocumentSameLabelSelectionDelegate(
        [In] byte[] input, nuint length, out NativeAstResult result);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DocumentMoveSelectionDelegate(
        [In] byte[] input, nuint length, out NativeAstResult result);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ExactDocumentPartsDelegate(
        [In] byte[] input, nuint length, out NativeAstResult result);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DocumentPartsDelegate(
        [In] byte[] source, nuint length, out NativeAstResult result);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DocumentWhitespaceDiffDelegate(
        [In] byte[] before, nuint beforeLength,
        [In] byte[] after, nuint afterLength,
        out NativeAstResult result);

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
        public readonly AstDiffDelegate AstDiff;
        public readonly AstFreeDelegate AstFree;
        public readonly CrossFileAstDelegate CrossFileAst;
        public readonly DocumentWhitespaceDiffDelegate? DocumentWhitespaceDiff;
        public readonly DocumentPartsDelegate? DocumentParts;
        public readonly DocumentHierarchyDelegate? DocumentHierarchy;
        public readonly ExactDocumentPartsDelegate? ExactDocumentParts;
        public readonly DocumentMoveSelectionDelegate? DocumentMoveSelection;
        public readonly DocumentSameLabelSelectionDelegate? DocumentSameLabelSelection;

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
            AstDiff = Marshal.GetDelegateForFunctionPointer<AstDiffDelegate>(
                NativeLibrary.GetExport(handle, "dc_compare_ast_json_utf8"));
            AstFree = Marshal.GetDelegateForFunctionPointer<AstFreeDelegate>(
                NativeLibrary.GetExport(handle, "dc_free_ast_json_result"));
            CrossFileAst = Marshal.GetDelegateForFunctionPointer<CrossFileAstDelegate>(
                NativeLibrary.GetExport(handle, "dc_compare_cross_file_ast_json_utf8"));
            // Optional ABI v1 extension: older Rust DLLs continue to run
            // code/folder/AST comparisons; document whitespace stays on C#.
            if (NativeLibrary.TryGetExport(handle,
                "dc_compare_document_whitespace_utf8", out var whitespaceExport))
                DocumentWhitespaceDiff = Marshal.GetDelegateForFunctionPointer<DocumentWhitespaceDiffDelegate>(
                    whitespaceExport);
            if (NativeLibrary.TryGetExport(handle, "dc_parse_document_parts_utf8",
                out var partsExport))
                DocumentParts = Marshal.GetDelegateForFunctionPointer<DocumentPartsDelegate>(
                    partsExport);
            if (NativeLibrary.TryGetExport(handle,
                "dc_document_part_hierarchy_json_utf8", out var hierarchyExport))
                DocumentHierarchy = Marshal.GetDelegateForFunctionPointer<DocumentHierarchyDelegate>(
                    hierarchyExport);
            if (NativeLibrary.TryGetExport(handle,
                "dc_match_exact_document_part_keys_utf8", out var exactExport))
                ExactDocumentParts = Marshal.GetDelegateForFunctionPointer<ExactDocumentPartsDelegate>(
                    exactExport);
            if (NativeLibrary.TryGetExport(handle,
                "dc_select_document_part_move_candidates_utf8", out var moveExport))
                DocumentMoveSelection = Marshal.GetDelegateForFunctionPointer<DocumentMoveSelectionDelegate>(
                    moveExport);
            if (NativeLibrary.TryGetExport(handle,
                "dc_select_document_same_label_parts_utf8", out var sameLabelExport))
                DocumentSameLabelSelection =
                    Marshal.GetDelegateForFunctionPointer<DocumentSameLabelSelectionDelegate>(
                        sameLabelExport);
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
    /// Narrow document-engine Rust port: whitespace-only change ranges in
    /// UTF-16 code units. Null signals no native payload or analysis budget
    /// exclusion; a returned ineligible result means not whitespace-only.
    /// The C# document matcher remains authoritative during parity rollout.
    /// </summary>
    public static DocumentWhitespaceResult? CompareDocumentWhitespace(string before, string after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var exports = Native.Value;
        if (exports?.DocumentWhitespaceDiff is null) return null;
        if (before.Length > 1_048_576 || after.Length > 1_048_576)
            return null;
        var encoding = new UTF8Encoding(false, true);
        var left = encoding.GetBytes(before);
        var right = encoding.GetBytes(after);
        var status = exports.DocumentWhitespaceDiff(left, (nuint)left.Length,
            right, (nuint)right.Length, out var output);
        try
        {
            if (status is 2 or 3) return null;
            if (status != 0)
                throw new InvalidDataException($"Rust 문서 공백 비교 실패: {status}");
            if (output.Data == IntPtr.Zero || output.Length == 0 ||
                output.Length > 8UL * 1024 * 1024)
                throw new InvalidDataException("Rust 문서 공백 비교 결과 메모리가 유효하지 않습니다.");
            var json = new byte[checked((int)output.Length)];
            Marshal.Copy(output.Data, json, 0, json.Length);
            var parsed = JsonSerializer.Deserialize<DocumentWhitespaceWire>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("Rust 문서 공백 결과 JSON 오류");
            if (parsed.Spans is null || parsed.Spans.Count > 1_048_576 ||
                (!parsed.Eligible && parsed.Spans.Count > 0) ||
                (parsed.Eligible && parsed.Spans.Count == 0))
                throw new InvalidDataException("Rust 문서 공백 결과 개수 오류");
            var lastOld = 0;
            var lastNew = 0;
            foreach (var span in parsed.Spans)
            {
                if (span.OldStart < lastOld || span.NewStart < lastNew ||
                    span.OldEnd < span.OldStart || span.NewEnd < span.NewStart ||
                    span.OldEnd > before.Length || span.NewEnd > after.Length ||
                    string.Equals(before[span.OldStart..span.OldEnd],
                        after[span.NewStart..span.NewEnd], StringComparison.Ordinal))
                    throw new InvalidDataException("Rust 문서 공백 결과 범위가 유효하지 않습니다.");
                lastOld = span.OldEnd;
                lastNew = span.NewEnd;
            }
            return new DocumentWhitespaceResult(parsed.Eligible, parsed.Spans);
        }
        finally
        {
            exports.AstFree(ref output);
        }
    }

    private sealed record DocumentWhitespaceWire(
        bool Eligible, List<DocumentWhitespaceSpan> Spans);

    /// <summary>
    /// Optional Rust port of C# ParseParts. Coordinates are UTF-16 and refer
    /// to newline-normalized input. Validation/mismatch never replaces C#
    /// document structure judgment.
    /// </summary>
    public static IReadOnlyList<DocumentPart>? ParseDocumentParts(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var exports = Native.Value;
        if (exports?.DocumentParts is null || source.Length > 131_072)
            return null;
        var input = new UTF8Encoding(false, true).GetBytes(source);
        var status = exports.DocumentParts(input, (nuint)input.Length, out var output);
        try
        {
            if (status is 2 or 3) return null;
            if (status != 0)
                throw new InvalidDataException($"Rust 항·호 분석 실패 (상태 {status}).");
            if (output.Data == IntPtr.Zero || output.Length == 0 ||
                output.Length > 16UL * 1024 * 1024)
                throw new InvalidDataException("Rust 항·호 구조 분석 결과가 올바르지 않습니다.");
            var bytes = new byte[checked((int)output.Length)];
            Marshal.Copy(output.Data, bytes, 0, bytes.Length);
            var parsed = JsonSerializer.Deserialize<DocumentPartsWire>(bytes,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (parsed?.Parts is null || parsed.Parts.Count > 4097)
                throw new InvalidDataException("Rust 항·호 구조 분석 결과 개수 오류");
            var normalized = source.Replace("\r\n", "\n").Replace('\r', '\n');
            var lastStart = 0;
            foreach (var part in parsed.Parts)
            {
                if (string.IsNullOrEmpty(part.Label) || part.Start < lastStart ||
                    part.Start < 0 || part.End < part.Start ||
                    part.CoreStart < part.Start || part.CoreEnd < part.CoreStart ||
                    part.CoreEnd > part.End || part.End > normalized.Length ||
                    !string.Equals(part.Core,
                        normalized[part.CoreStart..part.CoreEnd], StringComparison.Ordinal))
                    throw new InvalidDataException("Rust 항·호 UTF-16 위치 오류");
                lastStart = part.End;
            }
            return parsed.Parts;
        }
        finally
        {
            exports.AstFree(ref output);
        }
    }

    private sealed record DocumentPartsWire(List<DocumentPart> Parts);

    public static IReadOnlyList<DocumentHierarchyNode>? AnalyzeDocumentHierarchy(
        IReadOnlyList<string> labels)
    {
        ArgumentNullException.ThrowIfNull(labels);
        var exports = Native.Value;
        if (exports?.DocumentHierarchy is null || labels.Count > 4096 ||
            labels.Any(l => l is null || l.Length > 128))
            return null;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { labels });
        if (bytes.Length > 131_072) return null;
        var status = exports.DocumentHierarchy(bytes, (nuint)bytes.Length, out var output);
        try
        {
            if (status is 2 or 3) return null;
            if (status != 0)
                throw new InvalidDataException($"Rust 항·호 계층 분석 실패: {status}");
            if (output.Data == IntPtr.Zero || output.Length == 0 ||
                output.Length > 1024UL * 1024)
                throw new InvalidDataException("Rust 항·호 계층 결과 크기 오류");
            var data = new byte[checked((int)output.Length)];
            Marshal.Copy(output.Data, data, 0, data.Length);
            var parsed = JsonSerializer.Deserialize<DocumentHierarchyWire>(data,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (parsed?.Nodes is null || parsed.Nodes.Count != labels.Count)
                throw new InvalidDataException("Rust 항·호 계층 개수 불일치");
            for (var i = 0; i < parsed.Nodes.Count; i++)
            {
                var node = parsed.Nodes[i];
                if (node.Index != i || node.Level is < 0 or > 4 ||
                    node.Parent < -1 || node.Parent >= i ||
                    string.IsNullOrWhiteSpace(node.Family) ||
                    node.Ordinal is < 0)
                    throw new InvalidDataException("Rust 항·호 계층 노드가 올바르지 않습니다");
            }
            return parsed.Nodes;
        }
        finally
        {
            exports.AstFree(ref output);
        }
    }

    private sealed record DocumentHierarchyWire(List<DocumentHierarchyNode> Nodes);

    /// <summary>
    /// C# prepares canonical LineageNormalize keys, then Rust applies the
    /// exact-match greedy nearest-index stage of MatchParts. The caller
    /// must independently compare the results with C# before accepting.
    /// </summary>
    public static IReadOnlyList<ExactDocumentPartPair>? MatchExactDocumentParts(
        IReadOnlyList<string> oldKeys, IReadOnlyList<string> newKeys)
    {
        ArgumentNullException.ThrowIfNull(oldKeys);
        ArgumentNullException.ThrowIfNull(newKeys);
        var exports = Native.Value;
        if (exports?.ExactDocumentParts is null ||
            oldKeys.Count > 256 || newKeys.Count > 256 ||
            oldKeys.Any(x => x is null || x.Length > 16384) ||
            newKeys.Any(x => x is null || x.Length > 16384))
            return null;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { oldKeys, newKeys });
        if (bytes.Length > 512 * 1024) return null;
        var status = exports.ExactDocumentParts(bytes, (nuint)bytes.Length, out var output);
        try
        {
            if (status is 2 or 3) return null;
            if (status != 0)
                throw new InvalidDataException($"Rust 항·호 정확한 내용 대응 실패: {status}");
            if (output.Data == IntPtr.Zero || output.Length == 0 ||
                output.Length > 256UL * 1024)
                throw new InvalidDataException("Rust 항·호 대응 결과 크기 오류");
            var raw = new byte[checked((int)output.Length)];
            Marshal.Copy(output.Data, raw, 0, raw.Length);
            var parsed = JsonSerializer.Deserialize<ExactDocumentPartsWire>(raw,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (parsed?.Pairs is null || parsed.Pairs.Count > Math.Min(oldKeys.Count, newKeys.Count))
                throw new InvalidDataException("Rust 항·호 대응 개수 오류");
            var lastOld = -1;
            var usedNew = new HashSet<int>();
            foreach (var pair in parsed.Pairs)
            {
                if (pair.Old <= lastOld || pair.Old < 0 || pair.Old >= oldKeys.Count ||
                    pair.New < 0 || pair.New >= newKeys.Count ||
                    !usedNew.Add(pair.New) ||
                    string.IsNullOrEmpty(oldKeys[pair.Old]) ||
                    !string.Equals(oldKeys[pair.Old], newKeys[pair.New], StringComparison.Ordinal))
                    throw new InvalidDataException("Rust 항·호 대응 인덱스가 올바르지 않습니다.");
                lastOld = pair.Old;
            }
            return parsed.Pairs;
        }
        finally
        {
            exports.AstFree(ref output);
        }
    }

    private sealed record ExactDocumentPartsWire(List<ExactDocumentPartPair> Pairs);

    /// <summary>
    /// Rust applies unchanged C# MatchParts thresholds/ranking to C#-computed
    /// scores and parent-filtered candidate pairs. The caller still verifies
    /// exact selected ordered pairs and floating scores against managed code.
    /// </summary>
    public static IReadOnlyList<DocumentMoveSelected>? SelectDocumentPartMoves(
        int oldCount, int newCount, IReadOnlyList<DocumentMoveCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var exports = Native.Value;
        if (exports?.DocumentMoveSelection is null ||
            oldCount is < 0 or > 256 || newCount is < 0 or > 256 ||
            candidates.Count > 8192 ||
            candidates.Any(x => x.Old < 0 || x.New < 0 ||
                x.Old >= oldCount || x.New >= newCount ||
                !double.IsFinite(x.Similarity) || !double.IsFinite(x.Containment) ||
                x.Similarity is < 0 or > 1 || x.Containment is < 0 or > 1 ||
                x.ChildSupport is < 0 or > 256))
            return null;
        var input = JsonSerializer.SerializeToUtf8Bytes(
            new {oldCount, newCount, candidates},
            new JsonSerializerOptions {PropertyNamingPolicy = JsonNamingPolicy.CamelCase});
        if (input.Length > 1024 * 1024) return null;
        var status = exports.DocumentMoveSelection(input, (nuint)input.Length, out var output);
        try
        {
            if (status is 2 or 3) return null;
            if (status != 0)
                throw new InvalidDataException($"Rust 항·호 이동 후보 판정 실패: {status}");
            if (output.Data == IntPtr.Zero || output.Length == 0 ||
                output.Length > 1024UL * 1024)
                throw new InvalidDataException("Rust 항·호 이동 후보 결과 크기 오류");
            var raw = new byte[checked((int)output.Length)];
            Marshal.Copy(output.Data, raw, 0, raw.Length);
            var parsed = JsonSerializer.Deserialize<DocumentMoveWire>(raw,
                new JsonSerializerOptions {PropertyNameCaseInsensitive = true});
            if (parsed?.Selected is null ||
                parsed.Selected.Count > Math.Min(oldCount, newCount))
                throw new InvalidDataException("Rust 항·호 이동 후보 결과 개수 오류");
            var oldUsed = new HashSet<int>();
            var newUsed = new HashSet<int>();
            foreach (var selected in parsed.Selected)
            {
                if (selected.Old < 0 || selected.Old >= oldCount ||
                    selected.New < 0 || selected.New >= newCount ||
                    !double.IsFinite(selected.Score) ||
                    selected.Score is < 0 or > 1.12 ||
                    !oldUsed.Add(selected.Old) || !newUsed.Add(selected.New) ||
                    !candidates.Any(x => x.Old == selected.Old && x.New == selected.New))
                    throw new InvalidDataException("Rust 항·호 이동 후보 선택 인덱스 오류");
            }
            return parsed.Selected;
        }
        finally
        {
            exports.AstFree(ref output);
        }
    }

    private sealed record DocumentMoveWire(List<DocumentMoveSelected> Selected);

    /// <summary>
    /// Parity-gated C# MatchParts same-label sibling-shape fallback.
    /// Only score/rank/selection is delegated; candidate eligibility and
    /// parent relation remain managed. Optional export for older Rust DLLs.
    /// </summary>
    public static IReadOnlyList<DocumentMoveSelected>? SelectSameLabelDocumentParts(
        int oldCount, int newCount, IReadOnlyList<DocumentSameLabelCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var exports = Native.Value;
        if (exports?.DocumentSameLabelSelection is null ||
            oldCount is < 0 or > 256 || newCount is < 0 or > 256 ||
            candidates.Count > 8192 ||
            candidates.Any(x => x.Old < 0 || x.New < 0 ||
                x.Old >= oldCount || x.New >= newCount ||
                !double.IsFinite(x.Similarity) || x.Similarity is < 0 or > 1))
            return null;
        var input = JsonSerializer.SerializeToUtf8Bytes(new { oldCount, newCount, candidates },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        if (input.Length > 1024 * 1024) return null;
        var status = exports.DocumentSameLabelSelection(
            input, (nuint)input.Length, out var output);
        try
        {
            if (status is 2 or 3) return null;
            if (status != 0)
                throw new InvalidDataException($"Rust 동일 번호 항·호 대응 실패: {status}");
            if (output.Data == IntPtr.Zero || output.Length == 0 ||
                output.Length > 1024UL * 1024)
                throw new InvalidDataException("Rust 동일 번호 대응 결과 크기 오류");
            var bytes = new byte[checked((int)output.Length)];
            Marshal.Copy(output.Data, bytes, 0, bytes.Length);
            var parsed = JsonSerializer.Deserialize<DocumentMoveWire>(bytes,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (parsed?.Selected is null ||
                parsed.Selected.Count > Math.Min(oldCount, newCount))
                throw new InvalidDataException("Rust 동일 번호 대응 개수 오류");
            var seenOld = new HashSet<int>();
            var seenNew = new HashSet<int>();
            foreach (var match in parsed.Selected)
            {
                if (match.Old < 0 || match.Old >= oldCount ||
                    match.New < 0 || match.New >= newCount ||
                    !seenOld.Add(match.Old) || !seenNew.Add(match.New) ||
                    !double.IsFinite(match.Score) || match.Score is < .52 or > 1 ||
                    !candidates.Any(x => x.Old == match.Old && x.New == match.New))
                    throw new InvalidDataException("Rust 동일 번호 대응 위치·점수 오류");
            }
            return parsed.Selected;
        }
        finally
        {
            exports.AstFree(ref output);
        }
    }











    /// <summary>
    /// Returns syntax-tree declaration changes only for Rust/C#. Unsupported
    /// languages, malformed syntax, and parser budget errors are explicitly
    /// marked unsupported so the already-computed exact line diff still works.
    /// </summary>
    public static AstComparison CompareAst(string extension, string before, string after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var language = extension.ToLowerInvariant() switch
        {
            ".rs" => 1U,
            ".cs" => 2U,
            _ => 0U
        };
        if (language == 0)
            return new AstComparison(false, Array.Empty<AstChange>(), "AST 지원 언어는 Rust와 C#입니다.");
        var exports = Native.Value;
        if (exports is null)
            return new AstComparison(false, Array.Empty<AstChange>(), "Rust 네이티브 엔진이 없습니다.");
        var encoding = new UTF8Encoding(false, true);
        var beforeBytes = encoding.GetBytes(before);
        var afterBytes = encoding.GetBytes(after);
        var status = exports.AstDiff(language, beforeBytes,
            (nuint)beforeBytes.Length, afterBytes, (nuint)afterBytes.Length, out var output);
        try
        {
            if (status == 3)
                return new AstComparison(false, Array.Empty<AstChange>(),
                    "AST 구문 오류 또는 분석 한도 초과 · 줄 비교만 표시합니다.");
            if (status != 0)
                throw new InvalidDataException($"Rust AST 비교 실패 (상태 {status}).");
            if (output.Length == 0 || output.Length > 4UL * 1024 * 1024 ||
                output.Data == IntPtr.Zero)
                throw new InvalidDataException("Rust AST JSON 결과의 길이 또는 포인터가 잘못되었습니다.");
            var json = new byte[checked((int)output.Length)];
            Marshal.Copy(output.Data, json, 0, json.Length);
            var changes = JsonSerializer.Deserialize<List<AstChange>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("Rust AST 결과가 비어 있습니다.");
            if (changes.Count > 15000)
                throw new InvalidDataException("Rust AST 결과가 예상 범위를 초과했습니다.");
            foreach (var c in changes)
            {
                if (c.Kind is not ("Add" or "Delete" or "Modify" or "Move" or "Rename") ||
                    c.BeforeStart < 0 || c.BeforeEnd < c.BeforeStart ||
                    c.AfterStart < 0 || c.AfterEnd < c.AfterStart ||
                    c.BeforeEnd > beforeBytes.Length || c.AfterEnd > afterBytes.Length)
                    throw new InvalidDataException("Rust AST 변경 이벤트의 유형 또는 위치가 잘못되었습니다.");
            }
            return new AstComparison(true, changes);
        }
        finally
        {
            exports.AstFree(ref output);
        }
    }

    /// <summary>
    /// Optional, conservative Tree-sitter layer over the complete native
    /// folder comparison: only unambiguous full-declaration cross-file moves.
    /// A failed/budget-limited AST stage never hides the file-level changes.
    /// </summary>
    public static CrossFileAstReport CompareCrossFileAst(string before, string after)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(before);
        ArgumentException.ThrowIfNullOrWhiteSpace(after);
        var exports = Native.Value ?? throw new NotSupportedException(
            "파일 간 AST 분석에는 Rust 네이티브 엔진이 필요합니다.");
        if (!Directory.Exists(before) || !Directory.Exists(after))
            throw new DirectoryNotFoundException("파일 간 AST 비교 대상 폴더가 없습니다.");
        var encoding = new UTF8Encoding(false, true);
        var left = encoding.GetBytes(Path.GetFullPath(before));
        var right = encoding.GetBytes(Path.GetFullPath(after));
        var status = exports.CrossFileAst(left, (nuint)left.Length,
            right, (nuint)right.Length, out var output);
        try
        {
            if (status == 3)
                return new CrossFileAstReport(Array.Empty<CrossFileMove>(), 0, 0, false,
                    "소스 폴더 AST 분석 한도 또는 파일 접근 오류");
            if (status != 0)
                throw new InvalidDataException($"Rust 파일 간 AST 분석 실패 (상태 {status}).");
            if (output.Data == IntPtr.Zero || output.Length == 0 || output.Length > 4UL * 1024 * 1024)
                throw new InvalidDataException("Rust 파일 간 AST 결과 길이가 잘못되었습니다.");

            var bytes = new byte[checked((int)output.Length)];
            Marshal.Copy(output.Data, bytes, 0, bytes.Length);
            var report = JsonSerializer.Deserialize<CrossFileAstWire>(bytes,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("Rust 파일 간 AST 결과가 비어 있습니다.");
            if (report.Moves is null || report.Moves.Count > 15000 ||
                report.AnalyzedFiles < 0 || report.AnalyzedFiles > 512 ||
                report.SkippedFiles < 0 || report.SkippedFiles > 512)
                throw new InvalidDataException("Rust 파일 간 AST 결과 수가 허용 범위를 벗어났습니다.");
            foreach (var move in report.Moves)
            {
                if (string.IsNullOrWhiteSpace(move.SymbolName) ||
                    string.IsNullOrWhiteSpace(move.SymbolKind) ||
                    !IsSafeRelativePath(move.BeforePath) ||
                    !IsSafeRelativePath(move.AfterPath) ||
                    move.BeforePath == move.AfterPath ||
                    move.BeforeStart < 0 || move.BeforeEnd <= move.BeforeStart ||
                    move.AfterStart < 0 || move.AfterEnd <= move.AfterStart ||
                    move.SimilarityPermille is < 0 or > 1000 ||
                    (move.ContentChanged && move.SimilarityPermille < 880) ||
                    (!move.ContentChanged && move.SimilarityPermille != 1000) ||
                    (move.Renamed && (string.IsNullOrWhiteSpace(move.BeforeName) ||
                        string.IsNullOrWhiteSpace(move.AfterName) ||
                        move.BeforeName == move.AfterName)) ||
                    ((move.Renamed || move.SignatureChanged) &&
                        (!move.ContentChanged || move.SimilarityPermille < 950)))
                    throw new InvalidDataException("Rust 파일 간 AST 이동 정보가 올바르지 않습니다.");
            }
            return new CrossFileAstReport(report.Moves, report.AnalyzedFiles, report.SkippedFiles,
                RefactorStageSkipped: report.RefactorStageSkipped);
        }
        finally
        {
            exports.AstFree(ref output);
        }
    }

    private sealed record CrossFileAstWire(
        List<CrossFileMove> Moves, int AnalyzedFiles, int SkippedFiles,
        bool RefactorStageSkipped = false);

    private static bool IsSafeRelativePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        !Path.IsPathRooted(path) &&
        !path.Split('/', '\\').Any(part => part is "" or "." or "..");

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
