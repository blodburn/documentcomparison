# DOCUMENTCOMPARE — verified C# to Rust document-engine scope (2026-10-22)

## Decision: do not replace the complete document engine

The original policy in UNIFIED_VERSION_CODE_RUST_REVIEW_2026-10-20.md
requires exact golden-output equivalence, source-location parity, no
regressions in A/B/C matching or Word/Excel exports, and separate performance
and packaging checks BEFORE replacing C# document judgment.

MainWindow and VersionHistoryControl still use NativeComparisonEngine
for the Word/TXT document pipeline. That includes article/unit lineage,
paragraph/item matching, table/row/cell pairing, text diff, marker rendering,
and Word/Excel export. None of those complete functions has been migrated
to Rust or proven semantically equivalent. NativeDocumentReader,
StructuredDocxReader, and NativeOfficeExporter remain C#.
The original Python comparison engine is not currently present as a
testable independent source in this repository; Python-equivalence claims
must not be inferred merely from matching current C# behavior.

## Current rollout gates

| Rule | Reference | Rust engine | Gate |
|---|---|---|---|
| Source-code LF/CRLF bounded LCS | CodeVersionComparer | diff_lines | 16,265 direct managed vs native cases matched in one-off audit; CLI vs FFI corpus 256/256 |
| Document whitespace-only edits | C# TryWhitespaceBoundaryDiff | doc_whitespace::compare | Exact UTF-16 boundary match, 521/521 deterministic cases plus Rust unit/FFI checks; live call parity-shadowed |
| Article/section/paragraph lineage | C# ParseLegalUnits, BuildRows etc. | Not ported | No replacement |
| 항/호 label and boundary segmentation | C# ParsePartsCSharp | Rust doc_parts | 31 committed Golden cases plus 455 direct C#/Rust cases; live gated |
| Item hierarchy and ordinal/parent relationships | C# BuildPartHierarchyCSharp, PartOrdinalAt | Rust hierarchy | 631 direct cases; live guarded |
| Exact normalized item pairing (first pass) | C# MatchExactPartKeysCSharp | Rust doc_match_exact | 9 frozen Golden + 710 direct C#/Rust; live guarded |
| Changed-label move candidate ranking and selection | C# SelectPartMoveCandidatesCSharp | Rust doc_move_candidates | 7 frozen Golden plus 900 direct cases; live gated |
| Full item fuzzy matching and movement | C# remaining MatchParts, CollapsedLevelParentsEquivalent etc. | Partially ported | No full replacement |
| DOCX table cell/merge/row matching | C# structured reader and matcher | Not ported | No replacement |
| Word formatting and tracked revisions | C# NativeOfficeExporter | Not ported | No replacement |
| Excel A/B/C and change narrative | C# exporter | Not ported | No replacement |
| Rust/C# source-code AST, folder, Git | C# document engine has no matching API | Tree-sitter/FFI | Separate features, not document-engine parity |

## First narrow port: whitespace-only changes

The historical C# bug was presenting 동의없이 -> 동의 없이 as
없이 -> 없이 or skipping whitespace. The reference rule lives in
NativeComparisonEngine.TryWhitespaceBoundaryDiff and deliberately runs
before Korean morphology token-diff processing.

Rust implements this SPECIFIC rule in core-rust/src/doc_whitespace.rs.
Inputs are strict UTF-8; output ranges are UTF-16 code-unit offsets
for identical old/new context substrings, not Rust byte offsets.
It preserves C# whitespace classification, alphanumeric/underscore
context expansion, overlapping-range merging, leading/trailing
boundaries, reverse deletion, and supplementary-character location.

The FFI extension dc_compare_document_whitespace_utf8 returns Rust-owned
bounded JSON, freed with dc_free_ast_json_result. The optional export does
not change ABI v1 and old Rust DLLs remain loadable.

RUNTIME SAFETY GATE: the C# rule continues to compute authoritative
candidate spans. For eligible fragments up to 16,384 UTF-16 units,
the engine compares Rust spans to C# spans one by one. Only exact
equivalence permits using Rust's spans to build the same native document
markers. Any mismatch, missing symbol, invalid UTF-16 encoding, budget
failure, or malformed FFI result keeps the C# result. Counters
WhitespaceRustParityMatches and WhitespaceRustParityMismatches
allow regression checks. They are not user-facing claims of full migration.

Tests cover 521 deterministic input pairs, Korean/Hangul,
whitespace insert/delete, line terminators, CJK whitespace, emoji and
UTF-16 offsets; invalid UTF-16 checks fallback. The full document
regression suite is also mandatory (Word tables, paragraphs, renumbering,
exports, A/B/C).

## Go/no-go for remaining document slices

1. Extract independent normalized JSON Golden records from 2- and
   3-document C# cases: document unit ID, table/row/cell identity,
   old/new endpoints in UTF-16, action category, and displayed text.
2. Port one leaf judgment at a time: Korean word relation, part/number
   parsing, part alignment, unit lineage, and table alignment. No
   unconditional Rust replacement before exact golden parity.
3. Include DOCX table/merged-cell, split-item/moved-article,
   whitespace-only, and tracked-change fixtures on Linux and Windows.
4. Measure performance (five runs, identical inputs) before claiming Rust
   improves runtime or memory; maintain C# fallback until justified.

STATUS: five narrow document rules (whitespace changes, structural part
segmentation, parent/child hierarchy, exact-body pairing, and changed-label
move candidate selection) are ported with guarded live execution.
This is NOT a claim that the complete C# legal/Word engine has migrated
to Rust or equals any missing historical Python implementation.

## Second narrow port: legal paragraph/항·호 structural segmentation

The source-of-truth C# NativeComparisonEngine.ParsePartsCSharp has been
retained unchanged, alongside a guarded wrapper ParseParts. The Rust module
core-rust/src/doc_parts.rs ports the structural segmentation rules:

- Explicit 항/호 labels ①..⑳, (1), (a), (ii), 1./1), 가./가) etc.
- C#-style whitespace and format-character normalization without changing
  logical character positions.
- Inline enumerator recovery for flattened Word paragraphs: 2+ spaces
  remain strong boundaries; single spaces require ascending sequences.
- Contextual Roman numeral handling: (i) stays alpha by default, and becomes
  Roman only beside matching multi-character sequence proof. Noncanonical
  Roman numerals are never used as ordinal sequence proof.
- Exact Label, Start, End, CoreStart, CoreEnd and Core contents, with
  CRLF/LF normalization and UTF-16 offsets equivalent to C#.

Native FFI dc_parse_document_parts_utf8 reuses the existing owned JSON/free
contract and is an optional ABI v1 extension. Older DLLs retain their
original behavior.

Runtime gate: C# computes the original ParseParts result first; Rust output
is used ONLY if every label, complete body and UTF-16 start/end boundary
matches it exactly. Any mismatch, unsupported input, malformed encoding
or native error retains C# unchanged. PartsRustParityMatches and
PartsRustParityMismatches track parity. The shadow execution is restricted
to 16,384 UTF-16 units per text fragment.

Reproducible regression:
- tests/DocumentCompare.Regression/Fixtures/document_parts_golden.json
  stores 31 expected-outcome records frozen from the original C# parser;
  current C# and Rust both must match these independent committed records.
- 455 generated and explicit direct C# ↔ Rust comparisons cover inline
  sequences, CJK/English numbering, Roman context, formatting and UTF-16.
- All existing A/B/C, table, and tracked-change export regressions still
  run as a gate.

The new port is segmentation ONLY, not historical article/item matching:
MatchParts, BuildPartHierarchy, MatchStructuredUnits, MatchUnitSequence,
re-numbering, DOCX table/row/cell mapping and Word/Excel exports remain
in C#. Full document-engine Rust equivalence is NOT yet demonstrated.

## Third narrow port: structural parent/child hierarchy for MatchParts

The Rust doc_parts module now provides hierarchy(labels), including
part family, contextual Roman numeral interpretation, ordinal, nesting
level and parent index. The new optional Rust ABI v1 function is
dc_document_part_hierarchy_json_utf8, using owned JSON results and the
existing free function.

NativeComparisonEngine.BuildPartHierarchyCSharp remains the unchanged
source of truth. In the actual document comparison flow the C# result
is computed first, then Rust is considered only when all nodes have the
same Index, Level, Parent, Family and logical PartOrdinalAt. If ANY
element differs, the C# result is retained; parity counters expose
matching/mismatching cases. The shadow limit is 256 parts per fragment.

The regression checks all 31 committed document-part Golden fixture
inputs and 600 deterministic randomized label sequences: 631/631
direct C# vs Rust parent/child and ordinal parity cases. The Rust test
suite also exercises nested circled, number, Korean and contextual Roman
levels. Original A/B/C, document table matching, legal item move,
and tracked-revision export regressions must continue passing.

This is the PART HIERARCHY leaf of MatchParts ONLY. The heuristic
alignment MatchParts itself, old->new item movement/renumbering,
collapsed-level parent equivalence, unit lineage, table matching,
and output generators have NOT been ported to Rust.

## Fourth narrow port: exact-normalized-content pairing in MatchParts

The new Rust module core-rust/src/doc_match_exact.rs implements only the
FIRST NativeComparisonEngine.MatchParts pairing pass. Existing C#
LineageNormalize prepares old/new content keys. Rust performs the original
greedy old-index traversal: skip empty keys, choose nearest unused new
item with exactly the same normalized body, and break ties by lowest index.

The Rust FFI symbol dc_match_exact_document_part_keys_utf8 accepts bounded
JSON arrays and returns index pairs freed via dc_free_ast_json_result.
This is an OPTIONAL export, so older DLLs remain usable; 256 parts per
side is the analysis limit.

The runtime computes MatchExactPartKeysCSharp first as canonical.
Only completely identical lists of old/new index pairs can be passed
from Rust to the rest of the C# MatchParts matcher. Mismatch/failure
keeps the canonical C# result. ExactPartsRustParityMatches/Mismatches
counters track rollout. This protects old ② -> new ③, old ③ -> new ④
when a new ② is inserted and exact content survives.

The committed document_match_exact_golden.json stores 9 independent
original C# contracts: insertion, deletion, reordering, duplicates,
nearest-unused/tie, empty keys and Unicode. A further 710 deterministic
C# vs Rust index-pair cases check all outputs, and the real MatchParts
execution path verifies guarded integration.

Definition-head pairing, fuzzy similarity, parent-first shape matching,
collapsed parent levels, residual matching, article lineage, table matching,
and A/B/C tracked-change output remain managed in C#. Key normalization
also remains C#; this is NOT full MatchParts equivalence.

## Fifth narrow port: changed-content moves with different enumerators

Rust doc_move_candidates.rs implements the high-similarity move candidate
scoring, threshold, rank ordering, and greedy one-to-one selection
previously inside the level-by-level MatchParts loop.

C# remains responsible for PartSimilarity, Containment,
MatchedChildSupport, parent equivalence, hierarchy filtering, same-label
exclusion and all later matching decisions. Rust reproduces the original
C# thresholds: similarity >= .74, containment >= .88, child support >= 2,
or child support >= 1 AND similarity >= .55. The rank is
max(similarity, .94 * containment) + min(.12, childSupport * .06),
ordered descending by rank, by nearest old/new indices on ties,
then by original candidate order. A 1:1 greedy match preserves
C# PartMatch scoring exactly. Similarity is not proof of identity.

The optional ABI v1 FFI symbol dc_select_document_part_move_candidates_utf8
is bounded to 8192 pairs and at most 256 items per side. The C# engine
independently computes the original ordered pair selection and every score.
Rust output is accepted ONLY when each index and score matches (absolute
score tolerance 1e-12); a mismatch or missing DLL falls back to C#.
MoveCandidatesRustParityMatches/Mismatches provide counters.

The new document_move_candidates_golden.json freezes seven cases including
rank thresholds, insertion/moved/edited item numbering, competing and
tied candidates, child-support and containment edge cases. The regression
also verifies 900 seeded direct C#-Rust candidate matrices and a real
edited old ③ -> new ④ legal paragraph example.

This is only a candidate-selection stage. Definition-head matching,
logical ordinal and sibling fallback, collapsed level parents,
residual matching, article lineage, Word table alignment, tracked
changes, and A/B/C output remain in C#.
