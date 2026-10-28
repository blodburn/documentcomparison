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
    public sealed record DocumentSameOrdinalCandidate(
        int Old, int New, double Similarity, double Containment,
        bool Shape, bool Edge);
    public sealed record DocumentCrossLevelCandidate(
        int Old, int New, double Similarity, double Containment,
        bool SameOrdinal, bool Edge);
    public sealed record FullDocumentMatchItem(
        string Label, string Key, string Head, int Level, int Parent, int? Ordinal,
        string Normalized, string Family);
    // Rust calculates edge, sibling/ordinal shape and normalized equality
    // from these per-item fields. Managed code supplies numeric scores only.
    public sealed record FullDocumentMatchFeatures(
        double Similarity, double Containment);
    public sealed record FullDocumentMatch(int Old, int New, double Score);
    public sealed record DocumentSentenceReviewEvent(
        string Kind, int OldStart, int OldEnd, int NewStart, int NewEnd,
        string Text, int Hunk);
    public sealed record DocumentSentenceReview(
        bool Supported, List<DocumentSentenceReviewEvent> Events);
    public sealed record DocumentMovementLocation(
        int Index,int? Table,int? Row,int? Column);
    public sealed record DocumentMovementPair(
        DocumentMovementLocation Old,DocumentMovementLocation New);
    public sealed record DocumentMovementGroup(
        int OldDoc,int NewDoc,IReadOnlyList<DocumentMovementPair> Pairs);
    public sealed record DocumentMovementMarker(
        int OldDoc,int NewDoc,int OldIndex,int NewIndex,string Reason);
    private sealed record DocumentMovementWire(
        List<DocumentMovementMarker> Moves);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DocumentTableMovementsDelegate(
        [In] byte[] input,nuint length,out NativeAstResult result);
    public sealed record DocumentExportLcsPair(int A,int B);
    private sealed record DocumentExportLcsWire(List<DocumentExportLcsPair> Pairs);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DocumentExportLcsDelegate(
        [In] byte[] input,nuint length,out NativeAstResult result);
    public sealed record DocumentGenericUnitMatch(
        int Base, int Other, double Score, string Mode);
    public sealed record RawLineageUnit(
        string Kind,string Number,string Title,string Header,string Body);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DocumentRawLineageDelegate(
        [In] byte[] input,nuint length,out NativeAstResult result);
    public sealed record DocumentUnitLineageUnit(
        bool Article, string Number, string Title, string TitleKey,
        string BodyKey, bool LowInformationTitle);
    public sealed record DocumentUnitLineageFeature(
        double Title, double Body, double Lineage,
        double NonArticleScore, double NonArticleThreshold);
    public sealed record DocumentTableCell(int Index, int Table, int Row, string Key);
    public sealed record DocumentTablePair(int Base, int Other);
    public sealed record DocumentTokenBlock(int A, int B, int Size);
    public sealed record RawCompareCell(
        int Span,string VerticalMerge,int ParagraphCount);
    public sealed record RawCompareUnit(
        int Index,string Kind,string Number,string Title,string Header,string Body,
        RawCompareCell? Cell);
    public sealed record RawCompareMarker(
        string Kind,string OldText,string NewText,
        int OldStart,int OldEnd,int NewStart,int NewEnd,
        string Part,int ItemOrder,int HunkOrder);
    public sealed record RawCompareResult(
        List<RawCompareMarker> Markers,List<string> Structural);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DocumentSingleUnitDelegate(
        [In] byte[] input,nuint length,out NativeAstResult result);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int RawComparePairDelegate(
        [In] byte[] input,nuint length,out NativeAstResult result);
    public sealed record DocumentReviewDiffEvent(
        string Kind,string OldText,string NewText,int OldStart,int OldEnd,
        int NewStart,int NewEnd,int HunkOrder);
    private sealed record DocumentReviewDiffWire(List<DocumentReviewDiffEvent> Events);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DocumentReviewDiffDelegate(
        [In] byte[] input,nuint length,out NativeAstResult result);
    public sealed record DocumentDiffToken(int Start,int End,string Key);
    public sealed record DocumentDiffHunk(
        int OldStart,int OldEnd,int NewStart,int NewEnd,int HunkOrder);
    private sealed record DocumentDiffHunksResult(List<DocumentDiffHunk> Hunks);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DocumentDiffHunksDelegate(
        [In] byte[] input,nuint length,out NativeAstResult result);
    public sealed record DocumentBoundarySpan(int Start,int End,string Key);
    private sealed record DocumentBoundaryResult(int Mapped);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DocumentBoundaryDelegate(
        [In] byte[] input,nuint length,out NativeAstResult result);
    public sealed record DocumentReplacementMarker(
        int Index,string Kind,string Part,string Visible,bool Lexical,
        int TextLength,int ItemOrder,int OldStart,int OldEnd,int NewStart,int NewEnd);
    public sealed record DocumentReplacementPair(int Deletion,int Addition);
    public sealed record DocumentReviewEventPiece(int Start,int End,string Text);
    public sealed record DocumentReviewPieceResult(
        List<DocumentReviewEventPiece> Pieces,string Old,string New,
        int OldDelta,int NewDelta);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DocumentReviewPieceDelegate(
        [In] byte[] input,nuint length,out NativeAstResult result);

    private sealed record DocumentReplacementPairsWire(
        List<DocumentReplacementPair> Pairs);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DocumentReplacementCoalesceDelegate(
        [In] byte[] input,nuint length,out NativeAstResult result);

    public sealed record DocumentReviewSpan(int Start,int End,int Role);
    public sealed record DocumentReviewSegment(string Text,string Style);
    private sealed record DocumentNormalizedTextWire(string Text);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DocumentNormalizeDelegate(
        [In] byte[] input,nuint length,out NativeAstResult result);
    public sealed record ParsedDocumentUnit(
        int Index,string Number,string Title,string Header,
        string Body,string Section,string Kind);
    private sealed record DocumentUnitParserWire(List<ParsedDocumentUnit> Units);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DocumentRawUnitParserDelegate(
        [In] byte[] input,nuint length,out NativeAstResult result);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DocumentUnitParserDelegate(
        [In] byte[] input,nuint length,out NativeAstResult result);
    public sealed record DocumentRowMatch(int Doc,int Base,int Other);
    public sealed record DocumentRowAddition(int Doc,int Other,int Slot,int UnitIndex);
    public sealed record DocumentRowMerge(
        int LeftDoc,int LeftOther,int RightDoc,int RightOther);
    private sealed record DocumentAlignedRowsWire(List<List<int?>> Rows);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DocumentRawRowAlignmentDelegate(
        [In] byte[] input,nuint length,out NativeAstResult result);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DocumentRowAlignmentDelegate(
        [In] byte[] input,nuint length,out NativeAstResult result);

    private sealed record DocumentReviewSegmentsWire(List<DocumentReviewSegment> Segments);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DocumentReviewSegmentsDelegate(
        [In] byte[] input,nuint length,out NativeAstResult result);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DocumentCharacterScoreDelegate(
        [In] byte[] input, nuint length, out NativeAstResult result);
    private sealed record DocumentCharacterScoreWire(double Score);
    public sealed record DocumentKoreanWordRelation(string Kind,double Cost);
    public sealed record DocumentKoreanWordOp(string Kind,int? OldIndex,int? NewIndex);
    private sealed record DocumentKoreanWordWire(
        bool Supported,List<DocumentKoreanWordOp> Ops);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DocumentKoreanWordDelegate(
        [In] byte[] input,nuint length,out NativeAstResult result);


    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DocumentTokenMatcherDelegate(
        [In] byte[] input, nuint length, out NativeAstResult result);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DocumentTableMatcherDelegate(
        [In] byte[] input, nuint length, out NativeAstResult result);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DocumentUnitLineageDelegate(
        [In] byte[] input, nuint length, out NativeAstResult result);


    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DocumentGenericUnitsRawDelegate(
        [In] byte[] input,nuint length,out NativeAstResult result);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DocumentGenericUnitsDelegate(
        [In] byte[] input, nuint length, out NativeAstResult result);


    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DocumentSentenceReviewDelegate(
        [In] byte[] input, nuint length, out NativeAstResult result);


    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int FullDocumentMatchDelegate(
        [In] byte[] input, nuint length, out NativeAstResult result);


    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DocumentCrossLevelSelectionDelegate(
        [In] byte[] input, nuint length, out NativeAstResult result);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DocumentSameOrdinalSelectionDelegate(
        [In] byte[] input, nuint length, out NativeAstResult result);

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
        public readonly DocumentSameOrdinalSelectionDelegate? DocumentSameOrdinalSelection;
        public readonly DocumentCrossLevelSelectionDelegate? DocumentCrossLevelSelection;
        public readonly FullDocumentMatchDelegate? FullDocumentMatch;
        public readonly DocumentSentenceReviewDelegate? DocumentSentenceReview;
        public readonly DocumentGenericUnitsDelegate? DocumentGenericUnits;
        public readonly DocumentGenericUnitsRawDelegate? DocumentGenericUnitsRaw;
        public readonly DocumentExportLcsDelegate? DocumentExportLcs;
        public readonly DocumentTableMovementsDelegate? DocumentTableMovements;
        public readonly DocumentUnitLineageDelegate? DocumentUnitLineage;
        public readonly DocumentRawLineageDelegate? DocumentRawLineage;
        public readonly DocumentTableMatcherDelegate? DocumentTableMatcher;
        public readonly DocumentTokenMatcherDelegate? DocumentTokenMatcher;
        public readonly DocumentCharacterScoreDelegate? DocumentCharacterScore;
        public readonly DocumentKoreanWordDelegate? DocumentKoreanWord;
        public readonly DocumentReviewSegmentsDelegate? DocumentReviewSegments;
        public readonly DocumentRowAlignmentDelegate? DocumentRowAlignment;
        public readonly DocumentRawRowAlignmentDelegate? DocumentRawRowAlignment;
        public readonly DocumentUnitParserDelegate? DocumentUnitParser;
        public readonly DocumentRawUnitParserDelegate? DocumentRawUnitParser;
        public readonly DocumentNormalizeDelegate? DocumentNormalize;
        public readonly DocumentReplacementCoalesceDelegate? DocumentReplacementCoalesce;
        public readonly DocumentReviewPieceDelegate? DocumentReviewPiece;
        public readonly DocumentBoundaryDelegate? DocumentBoundary;
        public readonly DocumentDiffHunksDelegate? DocumentDiffHunks;
        public readonly DocumentReviewDiffDelegate? DocumentReviewDiff;
        public readonly RawComparePairDelegate? RawComparePair;
        public readonly DocumentSingleUnitDelegate? DocumentSingleUnit;

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
            if (NativeLibrary.TryGetExport(handle,
                "dc_select_document_same_ordinal_parts_utf8", out var sameOrdinalExport))
                DocumentSameOrdinalSelection =
                    Marshal.GetDelegateForFunctionPointer<DocumentSameOrdinalSelectionDelegate>(
                        sameOrdinalExport);
            if (NativeLibrary.TryGetExport(handle,
                "dc_select_document_cross_level_parts_utf8", out var crossLevelExport))
                DocumentCrossLevelSelection =
                    Marshal.GetDelegateForFunctionPointer<DocumentCrossLevelSelectionDelegate>(
                        crossLevelExport);
            if (NativeLibrary.TryGetExport(handle,
                "dc_match_document_parts_full_utf8", out var fullDocumentExport))
                FullDocumentMatch =
                    Marshal.GetDelegateForFunctionPointer<FullDocumentMatchDelegate>(
                        fullDocumentExport);
            if (NativeLibrary.TryGetExport(handle,
                "dc_review_document_sentences_utf8", out var sentenceReviewExport))
                DocumentSentenceReview =
                    Marshal.GetDelegateForFunctionPointer<DocumentSentenceReviewDelegate>(
                        sentenceReviewExport);
            if (NativeLibrary.TryGetExport(handle,
                "dc_match_generic_document_units_utf8", out var genericUnitsExport))
                DocumentGenericUnits =
                    Marshal.GetDelegateForFunctionPointer<DocumentGenericUnitsDelegate>(
                        genericUnitsExport);
            if (NativeLibrary.TryGetExport(handle,
                "dc_match_generic_document_raw_utf8",out var rawUnitsExport))
                DocumentGenericUnitsRaw =
                    Marshal.GetDelegateForFunctionPointer<DocumentGenericUnitsRawDelegate>(
                        rawUnitsExport);
            if (NativeLibrary.TryGetExport(handle,
                "dc_export_docx_lcs_utf8",out var exportLcs))
                DocumentExportLcs =
                    Marshal.GetDelegateForFunctionPointer<DocumentExportLcsDelegate>(
                        exportLcs);
            if (NativeLibrary.TryGetExport(handle,
                "dc_find_docx_table_movements_utf8",out var tableMovesExport))
                DocumentTableMovements =
                    Marshal.GetDelegateForFunctionPointer<DocumentTableMovementsDelegate>(
                        tableMovesExport);
            if (NativeLibrary.TryGetExport(handle,
                "dc_match_document_unit_lineage_utf8", out var lineageExport))
                DocumentUnitLineage =
                    Marshal.GetDelegateForFunctionPointer<DocumentUnitLineageDelegate>(
                        lineageExport);
            if (NativeLibrary.TryGetExport(handle,
                "dc_match_unit_lineage_raw_utf8",out var rawLineageExport))
                DocumentRawLineage =
                    Marshal.GetDelegateForFunctionPointer<DocumentRawLineageDelegate>(
                        rawLineageExport);
            if (NativeLibrary.TryGetExport(handle,
                "dc_match_docx_table_cells_utf8", out var tableMatcherExport))
                DocumentTableMatcher = Marshal.GetDelegateForFunctionPointer<DocumentTableMatcherDelegate>(
                    tableMatcherExport);
            if (NativeLibrary.TryGetExport(handle,
                "dc_match_document_tokens_utf8", out var tokenMatcherExport))
                DocumentTokenMatcher = Marshal.GetDelegateForFunctionPointer<DocumentTokenMatcherDelegate>(
                    tokenMatcherExport);
            if (NativeLibrary.TryGetExport(handle,
                "dc_score_document_characters_utf8", out var characterScoreExport))
                DocumentCharacterScore =
                    Marshal.GetDelegateForFunctionPointer<DocumentCharacterScoreDelegate>(
                        characterScoreExport);
            if (NativeLibrary.TryGetExport(handle,
                "dc_align_document_korean_review_utf8", out var koreanReviewExport))
                DocumentKoreanWord =
                    Marshal.GetDelegateForFunctionPointer<DocumentKoreanWordDelegate>(
                        koreanReviewExport);
            if (NativeLibrary.TryGetExport(handle,
                "dc_segment_document_review_utf8", out var segmentsExport))
                DocumentReviewSegments =
                    Marshal.GetDelegateForFunctionPointer<DocumentReviewSegmentsDelegate>(
                        segmentsExport);
            if (NativeLibrary.TryGetExport(handle,
                "dc_align_document_rows_utf8", out var rowAlignmentExport))
                DocumentRowAlignment =
                    Marshal.GetDelegateForFunctionPointer<DocumentRowAlignmentDelegate>(
                        rowAlignmentExport);
            if (NativeLibrary.TryGetExport(handle,
                "dc_align_document_rows_raw_utf8",out var rawRowsExport))
                DocumentRawRowAlignment =
                    Marshal.GetDelegateForFunctionPointer<DocumentRawRowAlignmentDelegate>(
                        rawRowsExport);
            if (NativeLibrary.TryGetExport(handle,
                "dc_parse_document_units_utf8", out var unitParserExport))
                DocumentUnitParser =
                    Marshal.GetDelegateForFunctionPointer<DocumentUnitParserDelegate>(
                        unitParserExport);
            if (NativeLibrary.TryGetExport(handle,
                "dc_parse_document_raw_units_utf8",out var rawParserExport))
                DocumentRawUnitParser =
                    Marshal.GetDelegateForFunctionPointer<DocumentRawUnitParserDelegate>(
                        rawParserExport);
            if (NativeLibrary.TryGetExport(handle,
                "dc_normalize_document_text_utf8", out var normalizeExport))
                DocumentNormalize =
                    Marshal.GetDelegateForFunctionPointer<DocumentNormalizeDelegate>(
                        normalizeExport);
            if (NativeLibrary.TryGetExport(handle,
                "dc_coalesce_document_replacements_utf8", out var coalesceExport))
                DocumentReplacementCoalesce =
                    Marshal.GetDelegateForFunctionPointer<DocumentReplacementCoalesceDelegate>(
                        coalesceExport);
            if (NativeLibrary.TryGetExport(handle,
                "dc_review_document_text_pieces_utf8", out var reviewPieceExport))
                DocumentReviewPiece =
                    Marshal.GetDelegateForFunctionPointer<DocumentReviewPieceDelegate>(
                        reviewPieceExport);
            if (NativeLibrary.TryGetExport(handle,
                "dc_map_document_boundary_utf8", out var boundaryExport))
                DocumentBoundary =
                    Marshal.GetDelegateForFunctionPointer<DocumentBoundaryDelegate>(
                        boundaryExport);
            if (NativeLibrary.TryGetExport(handle,
                "dc_document_diff_hunks_utf8", out var diffHunksExport))
                DocumentDiffHunks =
                    Marshal.GetDelegateForFunctionPointer<DocumentDiffHunksDelegate>(
                        diffHunksExport);
            if (NativeLibrary.TryGetExport(handle,
                "dc_diff_document_review_utf8", out var fullReviewExport))
                DocumentReviewDiff =
                    Marshal.GetDelegateForFunctionPointer<DocumentReviewDiffDelegate>(
                        fullReviewExport);
            if (NativeLibrary.TryGetExport(handle,
                "dc_compare_document_pair_raw_utf8", out var rawPairExport))
                RawComparePair =
                    Marshal.GetDelegateForFunctionPointer<RawComparePairDelegate>(
                        rawPairExport);
            if (NativeLibrary.TryGetExport(handle,
                "dc_document_single_unit_utf8",out var singleUnitExport))
                DocumentSingleUnit =
                    Marshal.GetDelegateForFunctionPointer<DocumentSingleUnitDelegate>(
                        singleUnitExport);
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
    /// Parity-gated drafting-convention change: ① and 1. or (1) and 1.
    /// share an ordinal only when C# has already approved corresponding
    /// structural parents, hierarchy levels, and ordinal values.
    /// </summary>
    public static IReadOnlyList<DocumentMoveSelected>? SelectSameOrdinalDocumentParts(
        int oldCount, int newCount, IReadOnlyList<DocumentSameOrdinalCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var exports = Native.Value;
        if (exports?.DocumentSameOrdinalSelection is null ||
            oldCount is < 0 or > 256 || newCount is < 0 or > 256 ||
            candidates.Count > 8192 ||
            candidates.Any(x => x.Old < 0 || x.New < 0 ||
                x.Old >= oldCount || x.New >= newCount ||
                !double.IsFinite(x.Similarity) || !double.IsFinite(x.Containment) ||
                x.Similarity is < 0 or > 1 || x.Containment is < 0 or > 1))
            return null;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new {oldCount, newCount, candidates},
            new JsonSerializerOptions {PropertyNamingPolicy = JsonNamingPolicy.CamelCase});
        if (bytes.Length > 1024 * 1024) return null;
        var status = exports.DocumentSameOrdinalSelection(
            bytes, (nuint)bytes.Length, out var output);
        try
        {
            if (status is 2 or 3) return null;
            if (status != 0)
                throw new InvalidDataException($"Rust 동일 논리 번호 대응 실패: {status}");
            if (output.Data == IntPtr.Zero || output.Length == 0 ||
                output.Length > 1024UL * 1024)
                throw new InvalidDataException("Rust 동일 논리 번호 결과 크기 오류");
            var raw = new byte[checked((int)output.Length)];
            Marshal.Copy(output.Data, raw, 0, raw.Length);
            var parsed = JsonSerializer.Deserialize<DocumentMoveWire>(raw,
                new JsonSerializerOptions {PropertyNameCaseInsensitive = true});
            if (parsed?.Selected is null ||
                parsed.Selected.Count > Math.Min(oldCount, newCount))
                throw new InvalidDataException("Rust 동일 논리 번호 대응 개수 오류");
            var usedOld = new HashSet<int>();
            var usedNew = new HashSet<int>();
            foreach (var item in parsed.Selected)
            {
                if (item.Old < 0 || item.Old >= oldCount ||
                    item.New < 0 || item.New >= newCount ||
                    !usedOld.Add(item.Old) || !usedNew.Add(item.New) ||
                    !double.IsFinite(item.Score) || item.Score is < .52 or > 1 ||
                    !candidates.Any(c => c.Old == item.Old && c.New == item.New))
                    throw new InvalidDataException("Rust 동일 논리 번호 인덱스 또는 점수 오류");
            }
            return parsed.Selected;
        }
        finally
        {
            exports.AstFree(ref output);
        }
    }

    /// <summary>
    /// Narrow Rust port of CrossLevel selection: the canonical C# matcher
    /// has already checked parent collapse/expansion and semantic scores.
    /// Callers must parity-check the ordered pairs and scores before use.
    /// </summary>
    public static IReadOnlyList<DocumentMoveSelected>? SelectCrossLevelDocumentParts(
        int oldCount, int newCount, IReadOnlyList<DocumentCrossLevelCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var exports = Native.Value;
        if (exports?.DocumentCrossLevelSelection is null ||
            oldCount is < 0 or > 256 || newCount is < 0 or > 256 ||
            candidates.Count > 8192 ||
            candidates.Any(c => c.Old < 0 || c.New < 0 ||
                c.Old >= oldCount || c.New >= newCount ||
                !double.IsFinite(c.Similarity) || !double.IsFinite(c.Containment) ||
                c.Similarity is < 0 or > 1 || c.Containment is < 0 or > 1))
            return null;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new {oldCount, newCount, candidates},
            new JsonSerializerOptions {PropertyNamingPolicy = JsonNamingPolicy.CamelCase});
        if (bytes.Length > 1024 * 1024) return null;
        var status = exports.DocumentCrossLevelSelection(
            bytes, (nuint)bytes.Length, out var output);
        try
        {
            if (status is 2 or 3) return null;
            if (status != 0)
                throw new InvalidDataException($"Rust 교차 계층 대응 실패: {status}");
            if (output.Data == IntPtr.Zero || output.Length == 0 ||
                output.Length > 1024UL * 1024)
                throw new InvalidDataException("Rust 교차 계층 결과 크기 오류");
            var raw = new byte[checked((int)output.Length)];
            Marshal.Copy(output.Data, raw, 0, raw.Length);
            var parsed = JsonSerializer.Deserialize<DocumentMoveWire>(raw,
                new JsonSerializerOptions {PropertyNameCaseInsensitive = true});
            if (parsed?.Selected is null ||
                parsed.Selected.Count > Math.Min(oldCount, newCount))
                throw new InvalidDataException("Rust 교차 계층 결과 개수 오류");
            var usedOld = new HashSet<int>();
            var usedNew = new HashSet<int>();
            foreach (var item in parsed.Selected)
            {
                if (item.Old < 0 || item.Old >= oldCount ||
                    item.New < 0 || item.New >= newCount ||
                    !usedOld.Add(item.Old) || !usedNew.Add(item.New) ||
                    !double.IsFinite(item.Score) || item.Score is < .56 or > 1 ||
                    !candidates.Any(c => c.Old == item.Old && c.New == item.New))
                    throw new InvalidDataException("Rust 교차 계층 인덱스 또는 점수 오류");
            }
            return parsed.Selected;
        }
        finally
        {
            exports.AstFree(ref output);
        }
    }

    /// <summary>
    /// Full structural MatchParts selection pipeline. Source-derived
    /// normalization/semantic similarity remain C# inputs and callers
    /// must compare the complete result to the canonical C# engine.
    /// </summary>
    public static IReadOnlyList<FullDocumentMatch>? MatchDocumentPartsFull(
        IReadOnlyList<FullDocumentMatchItem> oldItems,
        IReadOnlyList<FullDocumentMatchItem> newItems,
        IReadOnlyList<FullDocumentMatchFeatures> features)
    {
        ArgumentNullException.ThrowIfNull(oldItems);
        ArgumentNullException.ThrowIfNull(newItems);
        ArgumentNullException.ThrowIfNull(features);
        var exports = Native.Value;
        if (exports?.FullDocumentMatch is null ||
            oldItems.Count > 64 || newItems.Count > 64 ||
            features.Count != oldItems.Count * newItems.Count ||
            oldItems.Any(x => x.Label is null || x.Key is null || x.Head is null ||
                x.Normalized is null || x.Family is null ||
                x.Key.Length > 8192 || x.Head.Length > 8192 ||
                x.Normalized.Length > 32768 || x.Family.Length > 32) ||
            newItems.Any(x => x.Label is null || x.Key is null || x.Head is null ||
                x.Normalized is null || x.Family is null ||
                x.Key.Length > 8192 || x.Head.Length > 8192 ||
                x.Normalized.Length > 32768 || x.Family.Length > 32))
            return null;
        var raw = JsonSerializer.SerializeToUtf8Bytes(
            new { old = oldItems, @new = newItems, features },
            new JsonSerializerOptions {PropertyNamingPolicy = JsonNamingPolicy.CamelCase});
        if (raw.Length > 4 * 1024 * 1024) return null;
        var status = exports.FullDocumentMatch(raw, (nuint)raw.Length, out var output);
        try
        {
            if (status is 2 or 3) return null;
            if (status != 0)
                throw new InvalidDataException($"Rust MatchParts 전체 후보 판정 실패: {status}");
            if (output.Data == IntPtr.Zero || output.Length == 0 ||
                output.Length > 4UL * 1024 * 1024)
                throw new InvalidDataException("Rust MatchParts 전체 결과 크기 오류");
            var resultBytes = new byte[checked((int)output.Length)];
            Marshal.Copy(output.Data, resultBytes, 0, resultBytes.Length);
            var result = JsonSerializer.Deserialize<FullDocumentMatchWire>(resultBytes,
                new JsonSerializerOptions {PropertyNameCaseInsensitive=true});
            if (result?.Matches is null ||
                result.Matches.Count > Math.Min(oldItems.Count, newItems.Count))
                throw new InvalidDataException("Rust MatchParts 전체 대응 개수 오류");
            var usedOld = new HashSet<int>(); var usedNew = new HashSet<int>();
            var lastOld = -1;
            foreach (var match in result.Matches)
            {
                if (match.Old <= lastOld || match.Old < 0 || match.Old >= oldItems.Count ||
                    match.New < 0 || match.New >= newItems.Count ||
                    !usedOld.Add(match.Old) || !usedNew.Add(match.New) ||
                    !double.IsFinite(match.Score) || match.Score is < 0 or > 1.2)
                    throw new InvalidDataException("Rust MatchParts 전체 대응 값 오류");
                lastOld = match.Old;
            }
            return result.Matches;
        }
        finally
        {
            exports.AstFree(ref output);
        }
    }

    private sealed record FullDocumentMatchWire(List<FullDocumentMatch> Matches);

    /// <summary>
    /// Optional independent Rust detection of preserved unchanged legal
    /// sentences and strictly one-sided deleted/inserted sentence events.
    /// A Supported=false result requests the full C# comparison.
    /// </summary>
    public static DocumentSentenceReview? ReviewDocumentSentences(
        string oldText, string newText, bool includePunctuation)
    {
        ArgumentNullException.ThrowIfNull(oldText);
        ArgumentNullException.ThrowIfNull(newText);
        var exports = Native.Value;
        if (exports?.DocumentSentenceReview is null ||
            oldText.Length > 65_536 || newText.Length > 65_536)
            return null;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new {oldText,newText,includePunctuation});
        if (bytes.Length > 256 * 1024) return null;
        var status = exports.DocumentSentenceReview(bytes,(nuint)bytes.Length,out var result);
        try
        {
            if (status is 2 or 3) return null;
            if (status != 0)
                throw new InvalidDataException($"Rust 문장 비교 오류: {status}");
            if (result.Data == IntPtr.Zero || result.Length == 0 ||
                result.Length > 1024UL * 1024)
                throw new InvalidDataException("Rust 문장 비교 결과 크기 오류");
            var json = new byte[checked((int)result.Length)];
            Marshal.Copy(result.Data,json,0,json.Length);
            var parsed = JsonSerializer.Deserialize<DocumentSentenceReview>(json,
                new JsonSerializerOptions {PropertyNameCaseInsensitive=true});
            if (parsed?.Events is null || parsed.Events.Count > 256 ||
                (!parsed.Supported && parsed.Events.Count != 0) ||
                (parsed.Supported && parsed.Events.Count == 0))
                throw new InvalidDataException("Rust 문장 비교 결과 개수 오류");
            var oldBoundary=0; var newBoundary=0;
            foreach(var item in parsed.Events)
            {
                if (item.Kind is not ("insert" or "delete") ||
                    item.OldStart < oldBoundary || item.NewStart < newBoundary ||
                    item.OldEnd < item.OldStart || item.NewEnd < item.NewStart ||
                    item.OldEnd > oldText.Length || item.NewEnd > newText.Length ||
                    item.Hunk < 0 ||
                    (item.Kind == "delete" &&
                      (item.NewStart != item.NewEnd ||
                       item.Text != oldText[item.OldStart..item.OldEnd])) ||
                    (item.Kind == "insert" &&
                      (item.OldStart != item.OldEnd ||
                       item.Text != newText[item.NewStart..item.NewEnd])))
                    throw new InvalidDataException("Rust 문장 비교 텍스트 위치 오류");
                oldBoundary=item.OldEnd; newBoundary=item.NewEnd;
            }
            return parsed;
        }
        finally
        {
            exports.AstFree(ref result);
        }
    }

    /// <summary>
    /// Rust-first general-document block matching using C# similarity
    /// scores; original C# matcher stays available for audit and fallback.
    /// </summary>
    public static IReadOnlyList<DocumentGenericUnitMatch>? MatchGenericDocumentUnits(
        int oldCount, int newCount, IReadOnlyList<double> similarities)
    {
        ArgumentNullException.ThrowIfNull(similarities);
        var exports = Native.Value;
        if (exports?.DocumentGenericUnits is null ||
            oldCount is < 0 or > 256 || newCount is < 0 or > 256 ||
            similarities.Count != oldCount * newCount ||
            similarities.Any(s => !double.IsFinite(s) || s is < 0 or > 1))
            return null;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new {oldCount,newCount,similarities});
        if (bytes.Length > 1024 * 1024) return null;
        var status = exports.DocumentGenericUnits(bytes,(nuint)bytes.Length,out var result);
        try
        {
            if (status is 2 or 3) return null;
            if (status != 0)
                throw new InvalidDataException($"Rust 일반 블록 매칭 오류: {status}");
            if (result.Data == IntPtr.Zero || result.Length == 0 ||
                result.Length > 1024UL * 1024)
                throw new InvalidDataException("Rust 일반 블록 매칭 결과 크기 오류");
            var raw = new byte[checked((int)result.Length)];
            Marshal.Copy(result.Data,raw,0,raw.Length);
            var parsed = JsonSerializer.Deserialize<GenericUnitsWire>(raw,
                new JsonSerializerOptions {PropertyNameCaseInsensitive=true});
            if (parsed?.Matches is null ||
                parsed.Matches.Count > Math.Min(oldCount,newCount))
                throw new InvalidDataException("Rust 일반 블록 매칭 결과 개수 오류");
            var lastOld=-1; var usedNew=new HashSet<int>();
            foreach (var m in parsed.Matches)
            {
                if (m.Base <= lastOld || m.Base >= oldCount || m.Base < 0 ||
                    m.Other < 0 || m.Other >= newCount ||
                    !usedNew.Add(m.Other) || m.Mode!="same" ||
                    !double.IsFinite(m.Score) || m.Score is < .48 or > 1)
                    throw new InvalidDataException("Rust 일반 블록 매칭 인덱스 오류");
                lastOld=m.Base;
            }
            return parsed.Matches;
        }
        finally
        {
            exports.AstFree(ref result);
        }
    }

    /// <summary>
    /// Entire generic document matching from original bodies. Rust owns
    /// normalization, lexical token scores, proximity ranking and pairing.
    /// </summary>
    public static IReadOnlyList<DocumentGenericUnitMatch>? MatchGenericDocumentRaw(
        IReadOnlyList<string> old,IReadOnlyList<string> revised)
    {
        ArgumentNullException.ThrowIfNull(old);
        ArgumentNullException.ThrowIfNull(revised);
        var exports=Native.Value;
        if(exports?.DocumentGenericUnitsRaw is null ||
            old.Count>1024 || revised.Count>1024 ||
            old.Any(x=>x is null || x.Length>8192) ||
            revised.Any(x=>x is null || x.Length>8192))
            return null;
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new{old,@new=revised});
        if(bytes.Length>2*1024*1024)return null;
        var status=exports.DocumentGenericUnitsRaw(bytes,(nuint)bytes.Length,out var result);
        try
        {
            if(status is 2 or 3)return null;
            if(status!=0)
                throw new InvalidDataException($"Rust 일반 원문 매칭 오류: {status}");
            if(result.Data==IntPtr.Zero || result.Length==0 ||
                result.Length>1024UL*1024)
                throw new InvalidDataException("Rust 일반 원문 매칭 크기 오류");
            var data=new byte[checked((int)result.Length)];
            Marshal.Copy(result.Data,data,0,data.Length);
            var parsed=JsonSerializer.Deserialize<GenericUnitsWire>(data,
                new JsonSerializerOptions{PropertyNameCaseInsensitive=true});
            if(parsed?.Matches is null ||
                parsed.Matches.Count>Math.Min(old.Count,revised.Count))
                throw new InvalidDataException("Rust 일반 원문 매칭 개수 오류");
            var usedNew=new HashSet<int>();var last=-1;
            foreach(var m in parsed.Matches)
            {
                if(m.Base<=last || m.Base>=old.Count ||
                    m.Other<0 || m.Other>=revised.Count ||
                    !usedNew.Add(m.Other) || m.Mode!="same" ||
                    !double.IsFinite(m.Score) || m.Score is < .48 or > 1)
                    throw new InvalidDataException("Rust 일반 원문 매칭 결과 오류");
                last=m.Base;
            }
            return parsed.Matches;
        }
        finally{exports.AstFree(ref result);}
    }
    /// <summary>
    /// Word tracked-changes paragraph/formatting matching. Rust owns
    /// all exact LCS decisions and long-document block fallback.
    /// </summary>
    public static IReadOnlyList<DocumentExportLcsPair>? MatchExportLcs(
        IReadOnlyList<string> old,IReadOnlyList<string> revised)
    {
        ArgumentNullException.ThrowIfNull(old);
        ArgumentNullException.ThrowIfNull(revised);
        var exports=Native.Value;
        if(exports?.DocumentExportLcs is null ||
            old.Count>16_384 || revised.Count>16_384 ||
            old.Any(x=>x is null || x.Length>65_536) ||
            revised.Any(x=>x is null || x.Length>65_536))
            return null;
        var raw=JsonSerializer.SerializeToUtf8Bytes(new{old,@new=revised});
        if(raw.Length>8*1024*1024)return null;
        var status=exports.DocumentExportLcs(raw,(nuint)raw.Length,out var result);
        try
        {
            if(status is 2 or 3)return null;
            if(status!=0)
                throw new InvalidDataException($"Rust Word 문단 매칭 실패: {status}");
            if(result.Data==IntPtr.Zero || result.Length==0 ||
                result.Length>1024UL*1024)
                throw new InvalidDataException("Rust Word 문단 매칭 크기 오류");
            var bytes=new byte[checked((int)result.Length)];
            Marshal.Copy(result.Data,bytes,0,bytes.Length);
            var parsed=JsonSerializer.Deserialize<DocumentExportLcsWire>(bytes,
                new JsonSerializerOptions{PropertyNameCaseInsensitive=true});
            if(parsed?.Pairs is null ||
                parsed.Pairs.Count>Math.Min(old.Count,revised.Count))
                throw new InvalidDataException("Rust Word 문단 매칭 결과 오류");
            var previousA=-1;var previousB=-1;
            foreach(var entry in parsed.Pairs)
            {
                if(entry.A<=previousA || entry.B<=previousB ||
                    entry.A>=old.Count || entry.B>=revised.Count ||
                    old[entry.A]!=revised[entry.B])
                    throw new InvalidDataException("Rust Word 문단 매칭 좌표 오류");
                previousA=entry.A;previousB=entry.B;
            }
            return parsed.Pairs;
        }
        finally{exports.AstFree(ref result);}
    }

    /// <summary>
    /// Source-aligned table and row crossing movement classification in Rust.
    /// </summary>
    public static IReadOnlyList<DocumentMovementMarker>? FindDocxTableMovements(
        IReadOnlyList<DocumentMovementGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        var exports=Native.Value;
        if(exports?.DocumentTableMovements is null || groups.Count>3 ||
            groups.Any(g=>g.Pairs.Count>4096))
            return null;
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new{groups},
            new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase});
        if(bytes.Length>4*1024*1024)return null;
        var status=exports.DocumentTableMovements(bytes,(nuint)bytes.Length,
            out var output);
        try
        {
            if(status is 2 or 3)return null;
            if(status!=0)
                throw new InvalidDataException($"Rust 표 이동 판정 오류: {status}");
            if(output.Data==IntPtr.Zero || output.Length==0 ||
                output.Length>1024UL*1024)
                throw new InvalidDataException("Rust 표 이동 응답 오류");
            var raw=new byte[checked((int)output.Length)];
            Marshal.Copy(output.Data,raw,0,raw.Length);
            var parsed=JsonSerializer.Deserialize<DocumentMovementWire>(raw,
                new JsonSerializerOptions{PropertyNameCaseInsensitive=true});
            if(parsed?.Moves is null || parsed.Moves.Count>4096 ||
                parsed.Moves.Any(x=>x.Reason is not ("표 이동" or "표 행 이동") ||
                    !groups.Any(g=>g.OldDoc==x.OldDoc && g.NewDoc==x.NewDoc &&
                        g.Pairs.Any(p=>p.Old.Index==x.OldIndex &&
                            p.New.Index==x.NewIndex))))
                throw new InvalidDataException("Rust 표 이동 입력 불일치");
            return parsed.Moves;
        }
        finally{exports.AstFree(ref output);}
    }

    private sealed record GenericUnitsWire(List<DocumentGenericUnitMatch> Matches);
    /// <summary>
    /// Match DOCX table rows and columns via Rust using original C#
    /// normalized cell keys and structural coordinates.
    /// </summary>
    public static IReadOnlyList<DocumentTablePair>? MatchDocumentTableCells(
        IReadOnlyList<DocumentTableCell> oldCells,
        IReadOnlyList<DocumentTableCell> newCells)
    {
        ArgumentNullException.ThrowIfNull(oldCells);
        ArgumentNullException.ThrowIfNull(newCells);
        var exports=Native.Value;
        if (exports?.DocumentTableMatcher is null ||
            oldCells.Count>256 || newCells.Count>256 ||
            oldCells.Any(c=>c.Index<0 || c.Table<0 || c.Row<0 || c.Key is null || c.Key.Length>8192) ||
            newCells.Any(c=>c.Index<0 || c.Table<0 || c.Row<0 || c.Key is null || c.Key.Length>8192))
            return null;
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new {old=oldCells,@new=newCells},
            new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase});
        if (bytes.Length>4*1024*1024) return null;
        var status=exports.DocumentTableMatcher(bytes,(nuint)bytes.Length,out var output);
        try
        {
            if (status is 2 or 3) return null;
            if (status!=0) throw new InvalidDataException($"Rust Word 표 셀 대응 실패: {status}");
            if (output.Data==IntPtr.Zero || output.Length==0 ||
                output.Length>1024UL*1024)
                throw new InvalidDataException("Rust Word 표 셀 대응 크기 오류");
            var raw=new byte[checked((int)output.Length)];
            Marshal.Copy(output.Data,raw,0,raw.Length);
            var parsed=JsonSerializer.Deserialize<DocumentTablePairsWire>(raw,
                new JsonSerializerOptions{PropertyNameCaseInsensitive=true});
            if (parsed?.Pairs is null ||
                parsed.Pairs.Count>Math.Min(oldCells.Count,newCells.Count))
                throw new InvalidDataException("Rust Word 표 셀 대응 개수 오류");
            var oldSet=oldCells.Select(c=>c.Index).ToHashSet();
            var newSet=newCells.Select(c=>c.Index).ToHashSet();
            var usedNew=new HashSet<int>();
            var previous=-1;
            foreach (var item in parsed.Pairs)
            {
                if (item.Base<=previous || !oldSet.Contains(item.Base) ||
                    !newSet.Contains(item.Other) || !usedNew.Add(item.Other))
                    throw new InvalidDataException("Rust Word 표 셀 대응 인덱스 오류");
                previous=item.Base;
            }
            return parsed.Pairs;
        }
        finally
        {
            exports.AstFree(ref output);
        }
    }

    private sealed record DocumentTablePairsWire(List<DocumentTablePair> Pairs);
    /// <summary>
    /// Token-level sentence/word review anchors. Same longest-block semantics
    /// as the unchanged C# MatcherBlocks reference, validated by caller.
    /// </summary>
    public static IReadOnlyList<DocumentTokenBlock>? MatchDocumentTokenBlocks(
        IReadOnlyList<string> oldTokens, IReadOnlyList<string> newTokens)
    {
        ArgumentNullException.ThrowIfNull(oldTokens);
        ArgumentNullException.ThrowIfNull(newTokens);
        var exports=Native.Value;
        if (exports?.DocumentTokenMatcher is null ||
            oldTokens.Count>512 || newTokens.Count>512 ||
            (long)oldTokens.Count * newTokens.Count>100_000 ||
            oldTokens.Any(t=>t is null || t.Length>2048) ||
            newTokens.Any(t=>t is null || t.Length>2048))
            return null;
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new {old=oldTokens,@new=newTokens});
        if (bytes.Length>2*1024*1024) return null;
        var status=exports.DocumentTokenMatcher(bytes,(nuint)bytes.Length,out var output);
        try
        {
            if (status is 2 or 3) return null;
            if (status!=0) throw new InvalidDataException($"Rust 단어 매칭 실패: {status}");
            if (output.Data==IntPtr.Zero || output.Length==0 ||
                output.Length>1024UL*1024)
                throw new InvalidDataException("Rust 단어 매칭 결과 크기 오류");
            var raw=new byte[checked((int)output.Length)];
            Marshal.Copy(output.Data,raw,0,raw.Length);
            var parsed=JsonSerializer.Deserialize<DocumentTokenBlocksWire>(raw,
                new JsonSerializerOptions{PropertyNameCaseInsensitive=true});
            if (parsed?.Blocks is null || parsed.Blocks.Count>Math.Min(oldTokens.Count,newTokens.Count)+1 ||
                parsed.Blocks.Count==0 ||
                parsed.Blocks[^1] != new DocumentTokenBlock(oldTokens.Count,newTokens.Count,0))
                throw new InvalidDataException("Rust 단어 매칭 결과 개수/종료 기준 오류");
            var prevOld=0;var prevNew=0;
            foreach(var block in parsed.Blocks)
            {
                if (block.A<prevOld || block.B<prevNew ||
                    block.Size<0 || block.A+block.Size>oldTokens.Count ||
                    block.B+block.Size>newTokens.Count)
                    throw new InvalidDataException("Rust 단어 매칭 범위 오류");
                for(var k=0;k<block.Size;k++)
                    if (oldTokens[block.A+k]!=newTokens[block.B+k])
                        throw new InvalidDataException("Rust 단어 매칭 내용 불일치");
                prevOld=block.A+block.Size;prevNew=block.B+block.Size;
            }
            return parsed.Blocks;
        }
        finally
        {
            exports.AstFree(ref output);
        }
    }

    private sealed record DocumentTokenBlocksWire(List<DocumentTokenBlock> Blocks);
    /// <summary>
    /// ComparePair Rust implementation accepting only raw unit text and
    /// metadata: no C# matching/scoring/structural decisions are inputs.
    /// Returns null for unsupported input; the production caller explicitly
    /// raises an error rather than executing any C# comparison fallback.
    /// </summary>
    public static RawCompareResult? CompareRawMissingUnit(
        RawCompareUnit unit,string action)
    {
        ArgumentNullException.ThrowIfNull(unit);
        var exports=Native.Value;
        if(exports?.DocumentSingleUnit is null ||
            action is not ("insert" or "delete") ||
            unit.Body.Length>2048 || unit.Header.Length>512)
            return null;
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new{unit,action},
            new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase});
        if(bytes.Length>1024*1024)return null;
        var status=exports.DocumentSingleUnit(bytes,(nuint)bytes.Length,out var output);
        try
        {
            if(status is 2 or 3)return null;
            if(status!=0)
                throw new InvalidDataException($"Rust 신규/삭제 문서 판정 오류: {status}");
            if(output.Data==IntPtr.Zero || output.Length==0 ||
                output.Length>1024UL*1024)
                throw new InvalidDataException("Rust 신규/삭제 마커 응답 오류");
            var data=new byte[checked((int)output.Length)];
            Marshal.Copy(output.Data,data,0,data.Length);
            var result=JsonSerializer.Deserialize<RawCompareResult>(data,
                new JsonSerializerOptions{PropertyNameCaseInsensitive=true});
            if(result?.Markers is null || result.Structural is null ||
                result.Structural.Count!=2 || result.Markers.Count>2 ||
                result.Markers.Any(m=>m.Kind!=action ||
                    m.Part is not ("header" or "body") ||
                    m.OldStart<0 || m.OldStart>m.OldEnd ||
                    m.NewStart<0 || m.NewStart>m.NewEnd))
                throw new InvalidDataException("Rust 신규/삭제 마커 검증 오류");
            return result;
        }
        finally{exports.AstFree(ref output);}
    }

    public static RawCompareResult? CompareRawDocumentPair(
        RawCompareUnit old,RawCompareUnit revised,bool includePunctuation)
    {
        ArgumentNullException.ThrowIfNull(old);
        ArgumentNullException.ThrowIfNull(revised);
        var exports=Native.Value;
        if(exports?.RawComparePair is null ||
            old.Body.Length>2048 || revised.Body.Length>2048 ||
            old.Header.Length>512 || revised.Header.Length>512 ||
            old.Title.Length>256 || revised.Title.Length>256)
            return null;
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new{
            old,revised,includePunctuation
        },new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase});
        if(bytes.Length>1024*1024)return null;
        var status=exports.RawComparePair(bytes,(nuint)bytes.Length,out var output);
        try
        {
            if(status is 2 or 3)return null;
            if(status!=0)
                throw new InvalidDataException($"Rust 원문 조문 비교 실패: {status}");
            if(output.Data==IntPtr.Zero || output.Length==0 ||
                output.Length>2UL*1024*1024)
                throw new InvalidDataException("Rust 원문 조문 비교 데이터 오류");
            var data=new byte[checked((int)output.Length)];
            Marshal.Copy(output.Data,data,0,data.Length);
            var result=JsonSerializer.Deserialize<RawCompareResult>(data,
                new JsonSerializerOptions{PropertyNameCaseInsensitive=true});
            if(result?.Markers is null || result.Structural is null ||
                result.Markers.Count>1024 || result.Structural.Count>256)
                throw new InvalidDataException("Rust 원문 비교 마커 개수 오류");
            foreach(var m in result.Markers)
                if(m.Kind is not ("number" or "struct" or "change" or "delete" or "insert") ||
                    m.Part is not ("body" or "header") ||
                    m.OldStart<0 || m.OldEnd<m.OldStart ||
                    m.NewStart<0 || m.NewEnd<m.NewStart ||
                    m.OldEnd>(m.Part=="header"?old.Header.Length:old.Header.Length+1+old.Body.Length) ||
                    m.NewEnd>(m.Part=="header"?revised.Header.Length:revised.Header.Length+1+revised.Body.Length) ||
                    m.OldText is null || m.NewText is null)
                    throw new InvalidDataException("Rust 원문 비교 마커 오프셋 오류");
            return result;
        }
        finally{exports.AstFree(ref output);}
    }


    /// <summary>
    /// Full Rust generic DiffText event planner: matching token anchors,
    /// punctuation filtering, word segmentation, Hangul prefix and
    /// relative source/target endpoint mapping are all native.
    /// </summary>
    public static IReadOnlyList<DocumentReviewDiffEvent>? BuildDocumentReviewEvents(
        string oldText,string newText,
        IReadOnlyList<DocumentDiffToken> oldTokens,
        IReadOnlyList<DocumentDiffToken> newTokens,
        IReadOnlyList<DocumentBoundarySpan> oldBoundary,
        IReadOnlyList<DocumentBoundarySpan> newBoundary,
        IReadOnlyList<bool> oldWord,
        IReadOnlyList<bool> newWord,
        bool includePunctuation)
    {
        ArgumentNullException.ThrowIfNull(oldText);
        ArgumentNullException.ThrowIfNull(newText);
        var exports=Native.Value;
        if(exports?.DocumentReviewDiff is null ||
            oldText.Length>2048 || newText.Length>2048 ||
            (long)oldText.Length*newText.Length>1_000_000 ||
            oldWord.Count!=oldText.Length || newWord.Count!=newText.Length ||
            oldTokens.Count>512 || newTokens.Count>512 ||
            oldBoundary.Count>256 || newBoundary.Count>256)
            return null;
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new {
            oldText,newText,oldTokens,newTokens,oldBoundary,newBoundary,
            oldWord,newWord,includePunctuation
        },new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase});
        if(bytes.Length>2*1024*1024)return null;
        var status=exports.DocumentReviewDiff(bytes,(nuint)bytes.Length,out var output);
        try
        {
            if(status is 2 or 3)return null;
            if(status!=0)
                throw new InvalidDataException($"Rust DiffText 전체 이벤트 실패: {status}");
            if(output.Data==IntPtr.Zero || output.Length==0 ||
                output.Length>2UL*1024*1024)
                throw new InvalidDataException("Rust DiffText 이벤트 응답 크기 오류");
            var raw=new byte[checked((int)output.Length)];
            Marshal.Copy(output.Data,raw,0,raw.Length);
            var result=JsonSerializer.Deserialize<DocumentReviewDiffWire>(raw,
                new JsonSerializerOptions{PropertyNameCaseInsensitive=true});
            if(result?.Events is null || result.Events.Count>1024)
                throw new InvalidDataException("Rust DiffText 이벤트 수 오류");
            var lastOrder=-1;
            foreach(var e in result.Events)
            {
                if(e.Kind is not ("change" or "delete" or "insert") ||
                    e.OldText is null || e.NewText is null ||
                    e.OldStart<0 || e.OldEnd<e.OldStart || e.OldEnd>oldText.Length ||
                    e.NewStart<0 || e.NewEnd<e.NewStart || e.NewEnd>newText.Length ||
                    e.HunkOrder<lastOrder ||
                    (e.Kind=="delete" && e.NewEnd!=e.NewStart) ||
                    (e.Kind=="insert" && e.OldEnd!=e.OldStart))
                    throw new InvalidDataException("Rust DiffText 이벤트 좌표 오류");
                lastOrder=e.HunkOrder;
            }
            return result.Events;
        }
        finally
        {
            exports.AstFree(ref output);
        }
    }


    /// <summary>
    /// Rust standard text review hunk selection from normalized token
    /// anchors, preserving the exact original absolute UTF-16 ranges.
    /// </summary>
    public static IReadOnlyList<DocumentDiffHunk>? SelectDocumentDiffHunks(
        int oldLength,int newLength,
        IReadOnlyList<DocumentDiffToken> oldTokens,
        IReadOnlyList<DocumentDiffToken> newTokens)
    {
        ArgumentNullException.ThrowIfNull(oldTokens);
        ArgumentNullException.ThrowIfNull(newTokens);
        var exports=Native.Value;
        if (exports?.DocumentDiffHunks is null ||
            oldLength is < 0 or > 131072 || newLength is < 0 or > 131072 ||
            oldTokens.Count>512 || newTokens.Count>512 ||
            (long)oldTokens.Count*newTokens.Count>100_000 ||
            oldTokens.Any(t=>t.Start<0 || t.End<t.Start || t.End>oldLength ||
                t.Key is null || t.Key.Length>8192) ||
            newTokens.Any(t=>t.Start<0 || t.End<t.Start || t.End>newLength ||
                t.Key is null || t.Key.Length>8192))
            return null;
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new{
            oldLength,newLength,old=oldTokens,@new=newTokens
        },new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase});
        if (bytes.Length>2*1024*1024) return null;
        var status=exports.DocumentDiffHunks(bytes,(nuint)bytes.Length,out var result);
        try
        {
            if (status is 2 or 3) return null;
            if (status!=0)
                throw new InvalidDataException($"Rust 변경 구간 계산 실패: {status}");
            if (result.Data==IntPtr.Zero || result.Length==0 ||
                result.Length>1024UL*1024)
                throw new InvalidDataException("Rust 변경 구간 데이터 크기 오류");
            var raw=new byte[checked((int)result.Length)];
            Marshal.Copy(result.Data,raw,0,raw.Length);
            var parsed=JsonSerializer.Deserialize<DocumentDiffHunksResult>(raw,
                new JsonSerializerOptions{PropertyNameCaseInsensitive=true});
            if (parsed?.Hunks is null ||
                parsed.Hunks.Count>oldTokens.Count+newTokens.Count+1)
                throw new InvalidDataException("Rust 변경 구간 개수 오류");
            var prevOld=-1;var prevNew=-1;var prevOrder=-1;
            foreach(var h in parsed.Hunks)
            {
                if(h.OldStart<0 || h.NewStart<0 ||
                    h.OldStart>h.OldEnd || h.NewStart>h.NewEnd ||
                    h.OldEnd>oldLength || h.NewEnd>newLength ||
                    h.OldStart<prevOld || h.NewStart<prevNew ||
                    h.HunkOrder<=prevOrder)
                    throw new InvalidDataException("Rust 변경 구간 위치 오류");
                prevOld=h.OldEnd;prevNew=h.NewEnd;prevOrder=h.HunkOrder;
            }
            return parsed.Hunks;
        }
        finally
        {
            exports.AstFree(ref result);
        }
    }


    /// <summary>
    /// Projects a full review UTF-16 offset through native lexical anchors
    /// and SequenceMatcher punctuation-boundary rules.
    /// </summary>
    public static int? MapDocumentBoundary(
        string src,string dst,int pos,
        IReadOnlyList<DocumentBoundarySpan> srcSpans,
        IReadOnlyList<DocumentBoundarySpan> dstSpans,
        IReadOnlyList<bool> srcWord,
        IReadOnlyList<bool> dstWord)
    {
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(dst);
        var exports=Native.Value;
        if (exports?.DocumentBoundary is null ||
            src.Length>2048 || dst.Length>2048 ||
            (long)src.Length * dst.Length>1_000_000 ||
            srcSpans.Count>256 || dstSpans.Count>256 ||
            srcWord.Count!=src.Length || dstWord.Count!=dst.Length)
            return null;
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new {
            src,dst,pos,srcSpans,dstSpans,srcWord,dstWord
        }, new JsonSerializerOptions {
            PropertyNamingPolicy=JsonNamingPolicy.CamelCase
        });
        if(bytes.Length>1024*1024)return null;
        var status=exports.DocumentBoundary(bytes,(nuint)bytes.Length,out var output);
        try
        {
            if (status is 2 or 3) return null;
            if (status!=0)
                throw new InvalidDataException($"Rust 텍스트 경계 매핑 실패: {status}");
            if(output.Data==IntPtr.Zero || output.Length==0 ||
                output.Length>1024UL*1024)
                throw new InvalidDataException("Rust 텍스트 경계 매핑 크기 오류");
            var raw=new byte[checked((int)output.Length)];
            Marshal.Copy(output.Data,raw,0,raw.Length);
            var result=JsonSerializer.Deserialize<DocumentBoundaryResult>(raw,
                new JsonSerializerOptions{PropertyNameCaseInsensitive=true});
            if(result is null || result.Mapped<0 || result.Mapped>dst.Length)
                throw new InvalidDataException("Rust 텍스트 경계 매핑 인덱스 오류");
            return result.Mapped;
        }
        finally
        {
            exports.AstFree(ref output);
        }
    }


    /// <summary>
    /// Rust review event splitting and Hangul-prefix trimming, with exact
    /// UTF-16 boundaries and input-preserving validation.
    /// </summary>
    public static DocumentReviewPieceResult? ReviewDocumentTextPieces(
        string mode,string text,string other,int start,int end)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(other);
        var exports=Native.Value;
        if(exports?.DocumentReviewPiece is null ||
            mode is not ("events" or "hangul") ||
            text.Length>131072 || other.Length>131072 ||
            start<0 || end<0 || start>131072 || end>131072)
            return null;
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new{mode,text,other,start,end});
        if(bytes.Length>1024*1024) return null;
        var status=exports.DocumentReviewPiece(bytes,(nuint)bytes.Length,out var output);
        try
        {
            if(status is 2 or 3) return null;
            if(status!=0)
                throw new InvalidDataException($"Rust 변경 문구 분할 실패: {status}");
            if(output.Data==IntPtr.Zero || output.Length==0 ||
                output.Length>1024UL*1024)
                throw new InvalidDataException("Rust 변경 문구 분할 응답 크기 오류");
            var raw=new byte[checked((int)output.Length)];
            Marshal.Copy(output.Data,raw,0,raw.Length);
            var parsed=JsonSerializer.Deserialize<DocumentReviewPieceResult>(raw,
                new JsonSerializerOptions{PropertyNameCaseInsensitive=true});
            if(parsed?.Pieces is null || parsed.Pieces.Count>2 ||
                parsed.Old is null || parsed.New is null ||
                parsed.OldDelta<0 || parsed.NewDelta<0 ||
                parsed.OldDelta>text.Length || parsed.NewDelta>other.Length ||
                parsed.Pieces.Any(x=>x.Text is null || x.Start<0 ||
                    x.End<x.Start || x.End>text.Length ||
                    text.Substring(x.Start,x.End-x.Start)!=x.Text))
                throw new InvalidDataException("Rust 변경 문구 분할 위치 오류");
            if(mode=="hangul" &&
                (parsed.Pieces.Count!=0 ||
                    text[parsed.OldDelta..]!=parsed.Old ||
                    other[parsed.NewDelta..]!=parsed.New))
                throw new InvalidDataException("Rust 한글 접두사 오프셋 오류");
            return parsed;
        }
        finally
        {
            exports.AstFree(ref output);
        }
    }


    /// <summary>
    /// Exact Rust-first replacement pairing for nearby deletion/insertion
    /// markers, using managed-compatible text normalization and UTF-16 spans.
    /// </summary>
    public static IReadOnlyList<DocumentReplacementPair>? CoalesceDocumentReplacements(
        IReadOnlyList<DocumentReplacementMarker> markers)
    {
        ArgumentNullException.ThrowIfNull(markers);
        var exports=Native.Value;
        if(exports?.DocumentReplacementCoalesce is null ||
            markers.Count>384 || markers.Any(m=>m.Index<0 || m.Index>=4096 ||
                m.TextLength<0 || m.TextLength>131072 ||
                m.Kind is not ("delete" or "add") ||
                m.Part is null || m.Visible is null ||
                m.Part.Length>512 || m.Visible.Length>131072))
            return null;
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new{markers},
            new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase});
        if(bytes.Length>1024*1024) return null;
        var status=exports.DocumentReplacementCoalesce(bytes,(nuint)bytes.Length,out var output);
        try
        {
            if(status is 2 or 3) return null;
            if(status!=0)
                throw new InvalidDataException($"Rust 변경 교체 병합 실패: {status}");
            if(output.Data==IntPtr.Zero || output.Length==0 ||
                output.Length>1024UL*1024)
                throw new InvalidDataException("Rust 변경 교체 병합 응답 크기 오류");
            var raw=new byte[checked((int)output.Length)];
            Marshal.Copy(output.Data,raw,0,raw.Length);
            var parsed=JsonSerializer.Deserialize<DocumentReplacementPairsWire>(raw,
                new JsonSerializerOptions{PropertyNameCaseInsensitive=true});
            if(parsed?.Pairs is null ||
                parsed.Pairs.Count>markers.Count/2)
                throw new InvalidDataException("Rust 변경 교체 병합 쌍 오류");
            var dels=markers.Where(x=>x.Kind=="delete").Select(x=>x.Index).ToHashSet();
            var adds=markers.Where(x=>x.Kind=="add").Select(x=>x.Index).ToHashSet();
            var usedD=new HashSet<int>();
            var usedA=new HashSet<int>();
            var previous=-1;
            foreach(var pair in parsed.Pairs)
                if(pair.Deletion<=previous ||
                    !dels.Contains(pair.Deletion) || !adds.Contains(pair.Addition) ||
                    !usedD.Add(pair.Deletion) || !usedA.Add(pair.Addition))
                    throw new InvalidDataException("Rust 변경 교체 병합 중복 인덱스");
                else previous=pair.Deletion;
            return parsed.Pairs;
        }
        finally
        {
            exports.AstFree(ref output);
        }
    }


    /// <summary>
    /// Unicode NFC/NFKC matching, article lineage and visible-text
    /// normalization supplied by Rust on supported UTF-16 strings.
    /// </summary>
    public static string? NormalizeDocumentText(string text,string mode)
    {
        ArgumentNullException.ThrowIfNull(text);
        var exports=Native.Value;
        if (exports?.DocumentNormalize is null ||
            text.Length>8192 ||
            mode is not ("normalize" or "lineage" or "visible"))
            return null;
        // C# can carry invalid unpaired UTF-16 surrogates, while Rust
        // strings cannot; those cases must remain on the managed path.
        for(var i=0;i<text.Length;i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                if (i+1>=text.Length || !char.IsLowSurrogate(text[i+1]))
                    return null;
                i++;
            }
            else if (char.IsLowSurrogate(text[i])) return null;
        }
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new{text,mode});
        if (bytes.Length>128*1024) return null;
        var status=exports.DocumentNormalize(bytes,(nuint)bytes.Length,out var output);
        try
        {
            if (status is 2 or 3) return null;
            if (status!=0)
                throw new InvalidDataException($"Rust Unicode 정규화 오류: {status}");
            if (output.Data==IntPtr.Zero || output.Length==0 ||
                output.Length>256UL*1024)
                throw new InvalidDataException("Rust Unicode 정규화 응답 크기 오류");
            var raw=new byte[checked((int)output.Length)];
            Marshal.Copy(output.Data,raw,0,raw.Length);
            var parsed=JsonSerializer.Deserialize<DocumentNormalizedTextWire>(raw,
                new JsonSerializerOptions{PropertyNameCaseInsensitive=true});
            if (parsed?.Text is null || parsed.Text.Length>65536)
                throw new InvalidDataException("Rust Unicode 정규화 응답 무결성 오류");
            return parsed.Text;
        }
        finally
        {
            exports.AstFree(ref output);
        }
    }


    /// <summary>
    /// Native legal and general text-to-unit parsing. The input text is
    /// preprocessed by the existing flattened-article/newline guard.
    /// </summary>
    /// <summary>
    /// Rust-owned source unit parser. C# supplies raw normalized newlines
    /// only; structural title boundaries and legal classification are Rust.
    /// </summary>
    public static IReadOnlyList<ParsedDocumentUnit>? ParseRawDocumentUnits(
        string text,string mode)
    {
        ArgumentNullException.ThrowIfNull(text);
        var exports=Native.Value;
        if(exports?.DocumentRawUnitParser is null || text.Length>131072)
            return null;
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new{text,mode});
        if(bytes.Length>1024*1024)return null;
        var status=exports.DocumentRawUnitParser(bytes,(nuint)bytes.Length,
            out var output);
        try
        {
            if(status is 2 or 3)return null;
            if(status!=0)
                throw new InvalidDataException($"Rust 원문 단위 파서 오류: {status}");
            if(output.Data==IntPtr.Zero || output.Length==0 ||
                output.Length>4UL*1024*1024)
                throw new InvalidDataException("Rust 원문 파서 응답 크기 오류");
            var raw=new byte[checked((int)output.Length)];
            Marshal.Copy(output.Data,raw,0,raw.Length);
            var parsed=JsonSerializer.Deserialize<DocumentUnitParserWire>(raw,
                new JsonSerializerOptions{PropertyNameCaseInsensitive=true});
            if(parsed?.Units is null || parsed.Units.Count>4096 ||
                parsed.Units.Where((u,i)=>u.Index!=i ||
                    u.Number is null || u.Title is null || u.Header is null ||
                    u.Body is null || u.Section is null ||
                    u.Kind is not ("article" or "section" or "block")).Any())
                throw new InvalidDataException("Rust 원문 단위 파서 응답 범위 오류");
            return parsed.Units;
        }
        finally{exports.AstFree(ref output);}
    }

    public static IReadOnlyList<ParsedDocumentUnit>? ParseDocumentUnits(
        string text, bool legal)
    {
        ArgumentNullException.ThrowIfNull(text);
        var exports=Native.Value;
        if (exports?.DocumentUnitParser is null || text.Length>131072) return null;
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new{text,legal});
        if (bytes.Length>1024*1024) return null;
        var status=exports.DocumentUnitParser(bytes,(nuint)bytes.Length,out var output);
        try
        {
            if (status is 2 or 3) return null;
            if (status!=0) throw new InvalidDataException($"Rust 문서 단위 파싱 실패: {status}");
            if (output.Data==IntPtr.Zero || output.Length==0 ||
                output.Length>4UL*1024*1024)
                throw new InvalidDataException("Rust 문서 단위 파싱 응답 크기 오류");
            var raw=new byte[checked((int)output.Length)];
            Marshal.Copy(output.Data,raw,0,raw.Length);
            var parsed=JsonSerializer.Deserialize<DocumentUnitParserWire>(raw,
                new JsonSerializerOptions{PropertyNameCaseInsensitive=true});
            if (parsed?.Units is null || parsed.Units.Count>4096 ||
                parsed.Units.Where((u,i)=>u.Index!=i ||
                    u.Number is null || u.Title is null || u.Header is null ||
                    u.Body is null || u.Section is null ||
                    u.Kind is not ("article" or "section" or "block")).Any())
                throw new InvalidDataException("Rust 문서 단위 파싱 결과 값 오류");
            return parsed.Units;
        }
        finally
        {
            exports.AstFree(ref output);
        }
    }


    /// <summary>
    /// Rust A/B/C row ordering, baseline axis and cross-version addition
    /// placement. The original managed engine provides unit matches and
    /// item-level merge eligibility, native code owns the output row grid.
    /// </summary>
    /// <summary>
    /// A/B/C alignment from raw unit texts, native cross-version merge
    /// scoring, ranked merge predicates and final row grid.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<int?>>? AlignDocumentRawRows(
        IReadOnlyList<IReadOnlyList<RawLineageUnit>> units,int baseDoc,
        IReadOnlyList<DocumentRowMatch> matched,
        IReadOnlyList<DocumentRowAddition> additions)
    {
        var exports=Native.Value;
        if(exports?.DocumentRawRowAlignment is null ||
            units.Count is < 2 or > 3 || baseDoc<0 ||
            baseDoc>=units.Count || units.Any(u=>u.Count>512) ||
            units.SelectMany(u=>u).Any(x=>x.Body.Length>512 ||
                x.Title.Length>256 || x.Header.Length>512) ||
            matched.Count>1024 || additions.Count>192)
            return null;
        var bytes=JsonSerializer.SerializeToUtf8Bytes(
            new{units,baseDoc,matched,additions},
            new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase});
        if(bytes.Length>4*1024*1024)return null;
        var status=exports.DocumentRawRowAlignment(bytes,(nuint)bytes.Length,
            out var output);
        try
        {
            if(status is 2 or 3)return null;
            if(status!=0)
                throw new InvalidDataException($"Rust 원문 A/B/C 행 매칭 오류: {status}");
            if(output.Data==IntPtr.Zero || output.Length==0 ||
                output.Length>2UL*1024*1024)
                throw new InvalidDataException("Rust 원문 A/B/C 행 결과 크기 오류");
            var raw=new byte[checked((int)output.Length)];
            Marshal.Copy(output.Data,raw,0,raw.Length);
            var parsed=JsonSerializer.Deserialize<DocumentAlignedRowsWire>(raw,
                new JsonSerializerOptions{PropertyNameCaseInsensitive=true});
            if(parsed?.Rows is null ||
                parsed.Rows.Count>units.Sum(x=>x.Count) ||
                parsed.Rows.Any(row=>row.Count!=units.Count))
                throw new InvalidDataException("Rust 원문 A/B/C 행 출력 형식 오류");
            var used=new HashSet<(int,int)>();
            foreach(var row in parsed.Rows)
                for(var d=0;d<units.Count;d++)
                    if(row[d] is int index && (index<0 ||
                        index>=units[d].Count || !used.Add((d,index))))
                        throw new InvalidDataException("Rust A/B/C 행 누락/중복 오류");
            if(used.Count!=units.Sum(x=>x.Count))
                throw new InvalidDataException("Rust A/B/C 행 단위 누락");
            return parsed.Rows;
        }
        finally{exports.AstFree(ref output);}
    }

    public static IReadOnlyList<IReadOnlyList<int?>>? AlignDocumentRows(
        IReadOnlyList<int> counts,int baseDoc,
        IReadOnlyList<DocumentRowMatch> matched,
        IReadOnlyList<DocumentRowAddition> additions,
        IReadOnlyList<DocumentRowMerge> allowedMerge)
    {
        var exports=Native.Value;
        if (exports?.DocumentRowAlignment is null ||
            counts.Count is < 2 or > 3 ||
            baseDoc<0 || baseDoc>=counts.Count ||
            counts.Any(c=>c is < 0 or > 512) ||
            additions.Count>192 || matched.Count>1024 ||
            allowedMerge.Count>18432)
            return null;
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new{
            counts,baseDoc,matched,additions,allowedMerge},
            new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase});
        if (bytes.Length>1024*1024) return null;
        var status=exports.DocumentRowAlignment(bytes,(nuint)bytes.Length,out var output);
        try
        {
            if (status is 2 or 3) return null;
            if (status!=0) throw new InvalidDataException($"Rust A/B/C 행 정렬 오류: {status}");
            if (output.Data==IntPtr.Zero || output.Length==0 ||
                output.Length>2UL*1024*1024)
                throw new InvalidDataException("Rust A/B/C 행 정렬 응답 크기 오류");
            var raw=new byte[checked((int)output.Length)];
            Marshal.Copy(output.Data,raw,0,raw.Length);
            var parsed=JsonSerializer.Deserialize<DocumentAlignedRowsWire>(raw,
                new JsonSerializerOptions{PropertyNameCaseInsensitive=true});
            if (parsed?.Rows is null ||
                parsed.Rows.Count>counts.Sum() ||
                parsed.Rows.Any(row=>row.Count!=counts.Count))
                throw new InvalidDataException("Rust A/B/C 행 정렬 행 수 오류");
            var used=new HashSet<(int Doc,int Index)>();
            foreach(var row in parsed.Rows)
            for(var d=0;d<counts.Count;d++)
                if (row[d] is int index && (index<0 || index>=counts[d] ||
                    !used.Add((d,index))))
                    throw new InvalidDataException("Rust A/B/C 행 정렬 중복/범위 오류");
            if (used.Count!=counts.Sum())
                throw new InvalidDataException("Rust A/B/C 행 정렬 문서 단위 누락");
            return parsed.Rows;
        }
        finally
        {
            exports.AstFree(ref output);
        }
    }


    /// <summary>
    /// Rust review-span overlay segmentation, C# fallback for unsupported
    /// surrogate boundaries or oversized review texts.
    /// </summary>
    public static IReadOnlyList<DocumentReviewSegment>? SegmentDocumentReview(
        string text, IReadOnlyList<DocumentReviewSpan> spans)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(spans);
        var exports=Native.Value;
        if (exports?.DocumentReviewSegments is null ||
            text.Length>131072 || spans.Count>8192 ||
            spans.Any(s=>s.Start<0 || s.End<s.Start || s.Role is not (1 or 2)))
            return null;
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new{text,spans},
            new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase});
        if (bytes.Length>1024*1024) return null;
        var status=exports.DocumentReviewSegments(bytes,(nuint)bytes.Length,out var output);
        try
        {
            if (status is 2 or 3) return null;
            if (status!=0)
                throw new InvalidDataException($"Rust 문서 변경 음영 생성 실패: {status}");
            if (output.Data==IntPtr.Zero || output.Length==0 ||
                output.Length>2UL*1024*1024)
                throw new InvalidDataException("Rust 문서 변경 음영 응답 크기 오류");
            var raw=new byte[checked((int)output.Length)];
            Marshal.Copy(output.Data,raw,0,raw.Length);
            var parsed=JsonSerializer.Deserialize<DocumentReviewSegmentsWire>(raw,
                new JsonSerializerOptions{PropertyNameCaseInsensitive=true});
            if (parsed?.Segments is null ||
                parsed.Segments.Count>text.Length+1 ||
                parsed.Segments.Any(x=>x.Style is not
                    ("normal" or "delete" or "insert" or "both")) ||
                string.Concat(parsed.Segments.Select(x=>x.Text))!=text)
                throw new InvalidDataException("Rust 문서 변경 음영 범위 오류");
            return parsed.Segments;
        }
        finally
        {
            exports.AstFree(ref output);
        }
    }


    /// <summary>
    /// Selects Korean legal review word alignments in Rust. Word lexing
    /// and morphological relation costs are supplied from the C# source.
    /// </summary>
    public static IReadOnlyList<DocumentKoreanWordOp>? AlignKoreanReview(
        int oldCount,int newCount,
        IReadOnlyList<DocumentKoreanWordRelation> relations)
    {
        ArgumentNullException.ThrowIfNull(relations);
        var exports=Native.Value;
        if (exports?.DocumentKoreanWord is null ||
            oldCount is < 1 or > 96 || newCount is < 1 or > 96 ||
            relations.Count!=oldCount*newCount)
            return null;
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new{oldCount,newCount,relations},
            new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase});
        if (bytes.Length>256*1024) return null;
        var status=exports.DocumentKoreanWord(bytes,(nuint)bytes.Length,out var output);
        try
        {
            if (status is 2 or 3) return null;
            if (status!=0) throw new InvalidDataException($"Rust 한국어 문구 대응 실패: {status}");
            if (output.Data==IntPtr.Zero || output.Length==0 ||
                output.Length>1024UL*1024)
                throw new InvalidDataException("Rust 한국어 문구 응답 크기 오류");
            var raw=new byte[checked((int)output.Length)];
            Marshal.Copy(output.Data,raw,0,raw.Length);
            var parsed=JsonSerializer.Deserialize<DocumentKoreanWordWire>(raw,
                new JsonSerializerOptions{PropertyNameCaseInsensitive=true});
            if (parsed?.Ops is null || parsed.Ops.Count>oldCount+newCount ||
                (!parsed.Supported && parsed.Ops.Count!=0))
                throw new InvalidDataException("Rust 한국어 문구 응답 개수 오류");
            if (!parsed.Supported) return null;
            var oldIndex=0;var newIndex=0;
            foreach(var op in parsed.Ops)
            {
                if (op.Kind is not ("equal" or "fuzzy" or "delete" or "insert") ||
                    op.OldIndex!=(op.Kind=="insert"?(int?)null:oldIndex) ||
                    op.NewIndex!=(op.Kind=="delete"?(int?)null:newIndex))
                    throw new InvalidDataException("Rust 한국어 문구 순서 오류");
                if (op.OldIndex.HasValue) oldIndex++;
                if (op.NewIndex.HasValue) newIndex++;
            }
            if (oldIndex!=oldCount || newIndex!=newCount ||
                parsed.Ops.Count(x=>x.Kind=="equal")<2 ||
                parsed.Ops.Count(x=>x.Kind=="fuzzy")<2)
                throw new InvalidDataException("Rust 한국어 문구 종료 경계 오류");
            return parsed.Ops;
        }
        finally
        {
            exports.AstFree(ref output);
        }
    }


    /// <summary>
    /// Legal C#-normalized UTF-16 character similarity, calculated by Rust.
    /// Only the supported small scoring domain is sent to the native core.
    /// </summary>
    public static double? ScoreDocumentCharacters(
        string oldText, string newText, string mode,
        IReadOnlyList<string>? oldTokens=null, IReadOnlyList<string>? newTokens=null)
    {
        ArgumentNullException.ThrowIfNull(oldText);
        ArgumentNullException.ThrowIfNull(newText);
        if (mode is not ("indel" or "sequence" or "token") ||
            oldText.Length > 512 || newText.Length > 512 ||
            (mode=="token" && (oldTokens is null || newTokens is null ||
                oldTokens.Count>256 || newTokens.Count>256)))
            return null;
        var exports=Native.Value;
        if (exports?.DocumentCharacterScore is null) return null;
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new {
            old=oldText,@new=newText,mode,
            oldTokens=oldTokens ?? Array.Empty<string>(),
            newTokens=newTokens ?? Array.Empty<string>()
        });
        if (bytes.Length>128*1024) return null;
        var status=exports.DocumentCharacterScore(bytes,(nuint)bytes.Length,out var output);
        try
        {
            if (status is 2 or 3) return null;
            if (status!=0) throw new InvalidDataException($"Rust 문자 유사도 실패: {status}");
            if (output.Data==IntPtr.Zero || output.Length==0 ||
                output.Length>1024UL*1024)
                throw new InvalidDataException("Rust 문자 유사도 결과 크기 오류");
            var raw=new byte[checked((int)output.Length)];
            Marshal.Copy(output.Data,raw,0,raw.Length);
            var parsed=JsonSerializer.Deserialize<DocumentCharacterScoreWire>(raw,
                new JsonSerializerOptions{PropertyNameCaseInsensitive=true});
            if (parsed is null || !double.IsFinite(parsed.Score) ||
                parsed.Score is < 0 or > 1)
                throw new InvalidDataException("Rust 문자 유사도 점수 오류");
            return parsed.Score;
        }
        finally
        {
            exports.AstFree(ref output);
        }
    }





    /// <summary>
    /// Rust whole legal-unit/article lineage including non-article block
    /// alignment. C# supplies canonical text-based score matrix.
    /// </summary>
    /// <summary>
    /// Legal article lineage calculated exclusively from source title,
    /// article number, body and section text on the Rust side.
    /// </summary>
    public static IReadOnlyList<DocumentGenericUnitMatch>? MatchDocumentRawLineage(
        IReadOnlyList<RawLineageUnit> old,IReadOnlyList<RawLineageUnit> revised)
    {
        ArgumentNullException.ThrowIfNull(old);
        ArgumentNullException.ThrowIfNull(revised);
        var exports=Native.Value;
        if(exports?.DocumentRawLineage is null ||
            old.Count>48 || revised.Count>48 ||
            (long)old.Count*revised.Count>1024 ||
            old.Concat(revised).Any(x=>x.Body.Length>512 ||
                x.Header.Length>512 || x.Title.Length>256))
            return null;
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new{old,@new=revised},
            new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase});
        if(bytes.Length>2*1024*1024)return null;
        var status=exports.DocumentRawLineage(bytes,(nuint)bytes.Length,out var result);
        try
        {
            if(status is 2 or 3)return null;
            if(status!=0)
                throw new InvalidDataException($"Rust 원문 조문 계보 분석 실패: {status}");
            if(result.Data==IntPtr.Zero || result.Length==0 ||
                result.Length>1024UL*1024)
                throw new InvalidDataException("Rust 원문 조문 계보 응답 크기 오류");
            var data=new byte[checked((int)result.Length)];
            Marshal.Copy(result.Data,data,0,data.Length);
            var parsed=JsonSerializer.Deserialize<GenericUnitsWire>(data,
                new JsonSerializerOptions{PropertyNameCaseInsensitive=true});
            if(parsed?.Matches is null ||
                parsed.Matches.Count>Math.Min(old.Count,revised.Count))
                throw new InvalidDataException("Rust 원문 조문 계보 결과 오류");
            var previous=-1;var usedNew=new HashSet<int>();
            foreach(var m in parsed.Matches)
            {
                if(m.Base<=previous || m.Base>=old.Count || m.Base<0 ||
                    m.Other<0 || m.Other>=revised.Count ||
                    !usedNew.Add(m.Other) ||
                    m.Mode is not ("same" or "moved" or "renumbered") ||
                    !double.IsFinite(m.Score) || m.Score is < 0 or > 1)
                    throw new InvalidDataException("Rust 원문 조문 계보 인덱스 오류");
                previous=m.Base;
            }
            return parsed.Matches;
        }
        finally{exports.AstFree(ref result);}
    }

    public static IReadOnlyList<DocumentGenericUnitMatch>? MatchDocumentUnitLineage(
        IReadOnlyList<DocumentUnitLineageUnit> oldUnits,
        IReadOnlyList<DocumentUnitLineageUnit> newUnits,
        IReadOnlyList<DocumentUnitLineageFeature> features)
    {
        ArgumentNullException.ThrowIfNull(oldUnits);
        ArgumentNullException.ThrowIfNull(newUnits);
        ArgumentNullException.ThrowIfNull(features);
        var exports = Native.Value;
        if (exports?.DocumentUnitLineage is null ||
            oldUnits.Count > 48 || newUnits.Count > 48 ||
            features.Count != oldUnits.Count * newUnits.Count ||
            features.Count > 1024 ||
            features.Any(f =>
                !double.IsFinite(f.Title) || !double.IsFinite(f.Body) ||
                !double.IsFinite(f.Lineage) || !double.IsFinite(f.NonArticleScore) ||
                f.Title is < 0 or > 1 || f.Body is < 0 or > 1 ||
                f.Lineage is < 0 or > 1 || f.NonArticleScore is < 0 or > 1 ||
                f.NonArticleThreshold is < 0 or > 2))
            return null;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new {old=oldUnits,@new=newUnits,features},
            new JsonSerializerOptions {PropertyNamingPolicy=JsonNamingPolicy.CamelCase});
        if (bytes.Length > 4 * 1024 * 1024) return null;
        var status=exports.DocumentUnitLineage(bytes,(nuint)bytes.Length,out var result);
        try
        {
            if (status is 2 or 3) return null;
            if (status != 0)
                throw new InvalidDataException($"Rust 조문 계보 분석 실패: {status}");
            if (result.Data == IntPtr.Zero || result.Length == 0 ||
                result.Length > 1024UL * 1024)
                throw new InvalidDataException("Rust 조문 계보 결과 크기 오류");
            var raw=new byte[checked((int)result.Length)];
            Marshal.Copy(result.Data,raw,0,raw.Length);
            var parsed=JsonSerializer.Deserialize<GenericUnitsWire>(raw,
                new JsonSerializerOptions{PropertyNameCaseInsensitive=true});
            if (parsed?.Matches is null ||
                parsed.Matches.Count > Math.Min(oldUnits.Count,newUnits.Count))
                throw new InvalidDataException("Rust 조문 계보 매칭 개수 오류");
            var lastOld=-1;
            var usedNew=new HashSet<int>();
            foreach(var match in parsed.Matches)
            {
                if (match.Base <= lastOld || match.Base < 0 ||
                    match.Base >= oldUnits.Count || match.Other < 0 ||
                    match.Other >= newUnits.Count ||
                    !usedNew.Add(match.Other) ||
                    match.Mode is not ("same" or "moved" or "renumbered") ||
                    !double.IsFinite(match.Score) ||
                    match.Score is < 0 or > 1.0)
                    throw new InvalidDataException("Rust 조문 계보 인덱스/점수 오류");
                lastOld=match.Base;
            }
            return parsed.Matches;
        }
        finally
        {
            exports.AstFree(ref result);
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
