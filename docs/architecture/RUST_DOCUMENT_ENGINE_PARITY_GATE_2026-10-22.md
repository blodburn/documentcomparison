# DOCUMENTCOMPARE — verified C# to Rust document-engine scope (2026-10-22)

## Current decision: Rust-first for the complete MatchParts selection, not the complete document engine

The original policy in UNIFIED_VERSION_CODE_RUST_REVIEW_2026-10-20.md
requires exact golden-output equivalence, source-location parity, no
regressions in A/B/C matching or Word/Excel exports, and separate performance
and packaging checks BEFORE replacing C# document judgment.

MainWindow and VersionHistoryControl still use NativeComparisonEngine
for the Word/TXT document pipeline. That includes article/unit lineage,
paragraph/item matching, table/row/cell pairing, text diff, marker rendering,
and Word/Excel export. The full MatchParts *candidate/selection orchestration* is now migrated
and runs in Rust first on bounded item arrays, with C# as fallback.
Article/unit lineage, text segmentation and score calculation, table
alignment, marker rendering and export are not completely migrated. NativeDocumentReader,
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
| Same-label sibling-shape fallback | C# SelectSameLabelCandidatesCSharp | Rust doc_same_label | 8 frozen Golden plus 850 direct cases; live guarded |
| Same ordinal, different notation | C# SelectSameOrdinalCandidatesCSharp | Rust doc_same_ordinal | 8 frozen Golden plus 850 direct cases; live guarded |
| Collapsed/expanded one-level selection | C# SelectCrossLevelCandidatesCSharp | Rust doc_cross_level | 8 frozen Golden and 900 direct cases; live guarded |
| Full item pairing orchestration (all 6 phases) | MatchPartsCSharp | Rust doc_match_full | 336/336 full ordered old/new matches and scores; default Rust-first <=32 items/side, C# fallback, CI C# shadow parity |
| Whole sentence preserved review anchors | TryPreservedSentenceDiffCSharp | Rust doc_sentence_review | v5.21.9 article 26->27 DOCX Golden; independent Rust sentence offsets and C# shadow parity for supported changes |
| Full text normalization and fuzzy scores | C# PartSimilarity, WordSet, Normalize, Containment | Not ported | Rust full matcher currently consumes C#-supplied pair features |
| Generic non-article unit greedy matching | C# MatchGenericUnitsCSharp | Rust doc_generic_units | 500/500 pair-rank Golden; Rust-first <=64 blocks/side, C# fallback and CI shadow parity |
| DOCX table cell/merge/row matching | C# structured reader and matcher | Not ported | No replacement |
| Word formatting and tracked revisions | C# NativeOfficeExporter | Not ported | No replacement |
| Excel A/B/C and change narrative | C# exporter | Not ported | No replacement |
| Rust/C# source-code AST, folder, Git | C# document engine has no matching API | Tree-sitter/FFI | Separate features, not document-engine parity |

## First narrow port: whitespace-only changes

The historical C# bug was presenting 동의없이 -> 동의 없이 as
없이 -> 없이 or skipping whitespace. The reference rule lives in
NativeComparisonEngine.TryWhitespaceBoundaryDiff and deliberately runs
before Korean morphology token-diff processing.

Rust implements this SPECIFIC rule in rust/src/doc_whitespace.rs.
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

STATUS: eight narrow document rules (whitespace changes, structural part
segmentation, parent/child hierarchy, exact-body pairing, changed-label
move selection, same-label sibling-shape fallback, same-ordinal
drafting convention, and adjacent-level collapse/expansion selection)
are ported with guarded live execution.
This is NOT a claim that the complete C# legal/Word engine has migrated
to Rust or equals any missing historical Python implementation.

## Second narrow port: legal paragraph/항·호 structural segmentation

The source-of-truth C# NativeComparisonEngine.ParsePartsCSharp has been
retained unchanged, alongside a guarded wrapper ParseParts. The Rust module
rust/src/doc_parts.rs ports the structural segmentation rules:

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

The new Rust module rust/src/doc_match_exact.rs implements only the
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

## Sixth narrow port: same-label sibling-shape fallback in MatchParts

After exact bodies, definition heads, strong changed-label moves, and
different-label same-ordinal candidates are consumed, the existing C#
MatchParts matcher attempts to pair remaining items bearing the same
enumerator label at the same hierarchy level and equivalent matched
parents. It must preserve ①->①, ②->②, ③->③, etc. even for complete text
rewrites when the sibling label sequence remains stable.

Rust doc_same_label.rs now performs ONLY the candidate ranking and
greedy selection for this stage. C# still generates candidate pairs,
calculates PartSimilarity, SiblingShapeEquivalent, HasStableEdgeContext,
and handles ParentsEquivalent and used pairs from earlier stages.
For each unmatched old item in ascending order, the highest-ranked
available new candidate is selected by Shape DESC then Similarity DESC
(stable original new-index order on exact ties). If that best candidate
has Shape, Similarity >= 0.18, or Edge, it is matched with score
max(Similarity, 0.52). An insufficient best candidate does not trigger
fallback to the second-ranked candidate. The selected new index is
removed before processing the next old item.

The optional ABI v1 FFI dc_select_document_same_label_parts_utf8 accepts
bounded JSON (old/new <=256 items, <=8192 candidate pairs), returns
index/score pairs freed with dc_free_ast_json_result. The canonical C#
implementation computes this stage independently; Rust is used only
if every ordered index and score agrees within 1e-12. Absent DLLs,
limits, errors and discrepancies retain C# behavior. Parity counters:
SameLabelRustParityMatches and SameLabelRustParityMismatches.

document_same_label_golden.json freezes eight source-of-truth candidate
contracts, including total word rewrites with stable sibling structure,
shape precedence, a stable duplicate tie, a best-candidate threshold
failure with no second chance, an edge-only match, and greedy ownership
of the destination. 850 deterministic C# vs Rust matrices and an actual
three-item Word-style legal paragraph comparison passed the same gate.
All previous document, source-code, Windows EXE and export regressions
remain mandatory.

Still managed: candidate semantic metrics, same ordinal with different
drafting convention, cross-level matching, residual matching, final
move/add/delete/replace classification, article lineage, DOCX table-cell
alignment and Word tracked-change export. This is NOT full MatchParts
or full document-engine Rust equivalence.

## Seventh narrow port: same logical ordinal, different numbering notation

C# MatchParts identifies pairs where the logical ordinal is identical but
the displayed glyph differs, such as ① -> 1. or (1) -> 1. This is performed
after strong content moves and before same-label sibling fallback.

Rust rust/src/doc_same_ordinal.rs implements ONLY candidate ranking
and greedy matching. C# still calculates PartOrdinalAt, parent equivalence,
hierarchy level, OrdinalSiblingShapeEquivalent, PartSimilarity,
Containment, and HasStableEdgeContext. For each unmatched old item in
ascending index order, candidates are ordered with true Shape first, then
max(Similarity, .94 * Containment), keeping original new-item order on ties.
Only the top candidate is evaluated. It is accepted if:
  - Shape AND (Similarity >= .20 OR Containment >= .35 OR stable edge), OR
  - Similarity >= .62, OR
  - Containment >= .76.
Accepted score is max(Similarity, .58 when Shape else .52).
Unused destinations are reserved greedily after acceptance.

New optional ABI v1 FFI: dc_select_document_same_ordinal_parts_utf8.
It is limited to 256 items per side, 8192 candidates and JSON <=1 MiB.
C# computes an independent canonical candidate selection before Rust;
only ordered indices and scores agreeing within absolute 1e-12 can be
adopted. A missing native export, malformed result, failure, or
mismatch retains the original managed result. The match/mismatch counters
are SameOrdinalRustParityMatches and SameOrdinalRustParityMismatches.

The checked-in document_same_ordinal_golden.json freezes 8 original C#
contracts: matching by numbering convention, structural shape precedence,
insufficient top-candidate evidence without second-chance selection,
containment thresholds, tied scores, index exclusivity, boundary cases.
The regression additionally tests 850 deterministic candidate matrices
against BOTH C# and Rust, and checks an actual revised
①② -> 1.2. document comparison through MatchParts.

This does NOT migrate same-ordinal candidate eligibility, legal article
lineage, collapsed parent matching, residual add/delete/replace judgment,
DOCX table matching, A/B/C visualization, or Word tracked-change export.

## Eighth narrow port: adjacent-level collapse/expansion candidates

Rust rust/src/doc_cross_level.rs implements the candidate threshold,
rank sorting, and greedy one-to-one pairing for document 항/호 structures
that differ by exactly one hierarchy level.

The C# document engine remains authoritative for:
- Part segmentation, parent IDs, and level difference being exactly 1;
- CollapsedLevelParentsEquivalent (the skipped/interposed parent MUST
  remain unmatched and its grandparent must correspond);
- PartOrdinalAt, PartSimilarity, Containment, and HasStableEdgeContext;
- All pairing passes before/after the cross-level candidate stage.

Rust accepts a cross-level pair when either
(same logical ordinal AND (similarity>=.34 OR containment>=.50
OR stable edge)), OR similarity>=.74, OR containment>=.86.
Rank=max(similarity,.94*containment)+(sameOrdinal?.08:0).
Selection sorts by rank descending, old/new index distance ascending,
then original encounter order; each index is used at most once.
Returned score=max(similarity,.56).

The new optional ABI v1 FFI dc_select_document_cross_level_parts_utf8
accepts bounded JSON (<=256 items per side, <=8192 candidates,
<=1MiB input), and uses the existing Rust-owned result-free function.
NativeComparisonEngine.SelectCrossLevelCandidatesCSharp independently
computes the canonical source result, and the live wrapper adopts Rust
ONLY when every ordered index and score matches within absolute 1e-12.
A missing DLL/export, invalid input, FFI error or mismatch retains C#.
CrossLevelRustParityMatches/Mismatches report acceptance/fallback.

Frozen document_cross_level_golden.json includes 8 baseline cases
(rank order, thresholds, stable tie, 1:1 ownership, missing evidence).
Seeded direct C# vs Rust testing covers 900 candidate matrices.
An actual flattened legal article verifies a removed wrapper is not
erroneously matched to its children, while nested 1./2. map to new
(1)/(2). Existing healthy-hierarchy tests prevent false flattening.

Remaining managed functionality: collapsed-parent eligibility, residual
candidate matching, definition-head heuristics, one-sided lists, final
item add/delete/replace, full article lineage, Word table/row/cell
correspondence, A/B/C mapping and Word tracked-change export.
Passing this leaf gate does NOT prove full MatchParts parity.

## Ninth port: whole MatchParts orchestration, not just candidate stages

NativeComparisonEngine.MatchParts is now an optional Rust-first pipeline:
Rust doc_match_full.rs performs *all six ordered matching passes*, not just
individual ranking helpers:
1. One-sided prose/structural lists and exact normalized text;
2. Definition-head matches;
3. Parent-first strong moves, same ordinal notation and same label;
4. An omitted or added parent layer;
5. Remaining body alignment;
6. Residual high-confidence similarity and cross-parent matching.

C# supplies original normalized item keys, definition heads, hierarchy,
parent IDs, logical ordinals, similarities, containment, stable edges and
sibling-shape flags. Rust owns the complete resulting pair selection and
scores. The full-result FFI is dc_match_document_parts_full_utf8.

For arrays <=32 items/side, <=512 pair combinations, <=32,768 total
original body UTF-16 code units and <=8,192 units per individual body,
the application now calls Rust first.
Missing DLL/export, malformed result, FFI failure and larger arrays
use the unchanged MatchPartsCSharp fallback. When either
DOCUMENTCOMPARE_REQUIRE_NATIVE=1 or
DOCUMENTCOMPARE_VERIFY_DOCUMENT_MATCHES=1 is set (including Windows CI),
the full canonical C# result is also computed and every ordered pair and
score is compared before Rust can be selected.
The deterministic test suite checks 336 full old/new document structure
pairs, including random numbering and reordering, frozen parser fixtures
and adjacent-level parent changes, all with zero mismatches.
WholeMatchRustParityMatches/Mismatches track native adoption/verification.
No source-level equivalence to unverified historical Python is implied.

This is a whole *matching orchestration* port, not an independent complete
Rust semantic/lineage engine. Text scoring, input cleanup and parent
hierarchy detection remain C#-provided features. The public document
model and final A/B/C review messages remain managed.

## Tenth port: original v5.21.9 fee article preserved sentence boundaries

Rust doc_sentence_review.rs independently segments Korean/English
sentences, finds exact unchanged substantial sentence anchors and emits
UTF-16-aligned standalone deleted/inserted sentence events.
The optional FFI symbol is dc_review_document_sentences_utf8.
Complex two-sided rewrites or repeated identical sentence ambiguities
explicitly return unsupported and fall back to C# DiffText.
When DOCUMENTCOMPARE_REQUIRE_NATIVE=1 or
DOCUMENTCOMPARE_VERIFY_DOCUMENT_MATCHES=1 is set, the matching C# rules
must agree on every action, text and both endpoints before using Rust.
The actual DOCX fixture for the reported v5.21.9 article 26 -> 27 error
asserts Rust execution, unchanged original sentences, exactly one
deleted price-change sentence and one inserted refund sentence, and
preserved article-renumbering markers.

The final remaining NativeComparisonEngine portions are article/unit
lineage, generic unit matching, rich Word table/row/cell alignment,
short-text Korean word relation, token LCS and overall A/B/C review
construction. Those are NOT replaced by this commit.

## Eleventh port: generic non-article unit matching

Rust doc_generic_units.rs now owns general-document block match ranking,
nearest-position proximity weighting, stable candidate ordering and
one-to-one greedy selection, including the exact previous C# usedA/usedB
side-effect contract. It is exposed as an optional ABI v1 extension
dc_match_generic_document_units_utf8. C# still provides the original
Similarity score for every pair, cancellation, input classification,
and the C# fallback for more than 64 blocks on either side or unsupported
native operation.

In normal use it is Rust-first. CI (DOCUMENTCOMPARE_REQUIRE_NATIVE=1)
and optional verification mode re-run MatchGenericUnitsCSharp and compare
all indices, scores and action modes. A deterministic random corpus
verifies 500 direct C# vs Rust decisions. Independent
--smoke-native-production regression mode executes a real DOCX legal
article (whole item + sentence review) and an actual general-document
block reorder without the shadow-mode flag, asserting native acceptance
counters for all three paths. Windows CI runs BOTH shadow and production
smoke before packaging the portable executable.

Remaining in C#: article-level unit lineage/renumbering, structured
Word table/row/cell alignment, Korean fuzzy short-word changes, generic
word/token spans outside long exact-sentence anchors, full A/B/C event
construction. The broader NativeComparisonEngine.cs is NOT yet replaced
or deleted, and the legacy data model and export contract remain managed.

## Whole legal article lineage (Rust-first) — 2026-10-23

Rust src/doc_unit_lineage.rs now owns the complete legal article / non-article
unit correspondence flow previously executed by MatchUnitSequence:
- mutual strong/unique title anchors, normalized-title safeguards,
  boilerplate/low-information title exclusions and repeated-body ambiguity;
- monotonic gap-cost dynamic programming, article renumbering and stable ties;
- residual high-confidence non-monotonic article moves and inverted move
  labeling; and
- non-article/section DP pairing with original per-kind thresholds.

NativeComparisonEngine supplies text-derived input scores
(TitleSimilarity, BodySimilarity, UnitLineageSimilarity, non-article
Similarity) and normalized metadata. It does not own the final pairing
decision for bounded inputs (<=48 units per side and <=1024 comparisons).
A missing DLL/export, invalid input or oversized document falls back to
MatchUnitSequenceCSharp. In CI/verification mode it compares every index,
score and mode with the original C# output and rejects differences.

The test suite includes 120 real and deterministic legal-document
before/after pairs, checking that *every* bounded case adopts Rust
without mismatch. An independent production smoke uses moved/renumbered
Korean articles with C# shadow verification turned off.

This change is not the full migration of legal text similarity,
Unicode normalization, and review marker creation.

## Whole DOCX table/row/cell correspondence (Rust-first) — 2026-10-23

Rust src/doc_table_alignment.rs now owns table-to-table correspondence
ranked by unique normalized cell overlap and proximity, unique row/cell
content anchors, moved rows and columns, repeated/ambiguous keys and
same-size unanchored monotonic gap filling. It never pairs cells to prose
and explicitly declines unsupported oversized input (>256 cells/side).

The production MatchStructuredUnits retains ordinary non-table content
matching and delegates table cell pairing to Rust. Every complete
pair index, score and mode is independently compared with
MatchStructuredUnitsCSharp in CI/verification mode; on any mismatch
the original C# result wins. 19 actual DOCX table fixture comparisons
were accepted by Rust with zero parity mismatch, including row reorder,
column addition/deletion, tables inserted/moved, merged and nested cells.
Rust independently verifies table reorders/ambiguous inserted columns.
Production smoke asserts a moved real DOCX table uses the Rust path.

The document reader and Word exporter remain unchanged C# contracts.

## Generic sentence/word review anchors (Rust-first) — 2026-10-23

Rust src/doc_token_match.rs now implements the exact longest-contiguous
token matching-block algorithm used by DiffText/MatcherPairs/MatcherOpcodes.
It preserves first-found longest-match ties, stack traversal ordering,
contiguous-block merging and the terminal sentinel. C# remains responsible
for lexical tokenization, UTF-16 offsets, marker text/style and any final
display/excel representation. For <=512 tokens per side and <=100000
candidate pairs, Rust is used first; older DLLs or unsupported inputs
retain MatcherBlocksCSharp.

The complete C# ordered matching-block result is re-executed and
compared in CI mode. 700 seeded cases containing repeated common
words, Korean and numbered items passed 1:1, with no mismatches.
Production smoke confirms an actual nontrivial Word text rewrite uses
the Rust anchor path without C# shadow mode.

## Remaining C# concerns and release gate

**Not entirely migrated:** ParseLegalUnits/ParseGeneralUnits (source
document parsing and segment boundary extraction), text normalization,
Unicode semantic equality, IndelRatio/SequenceRatio and final confidence
scores, short-Korean-word morph DP, output marker construction (A/B/C
review UI, changes column), and integration with Word/Excel exporting.
These are intentionally preserved as C# while their Rust replacements
are not equivalence-proven. NativeComparisonEngine.cs remains present
and must not be deleted or called wholly rewritten.

CI must pass: Rust fmt/clippy/tests, C# regression suite with native
shadow parity, production Rust-first smoke (parts, legal-unit lineage,
generic units, table rows, token anchors, preserved sentences),
code C#/Rust parity, Windows portable single EXE. Keep the installed
version at v5.21.15 and do not publish a Release without a request.

## Core text scoring and Korean review DP (Rust-first, 2026-10-23)

New Rust doc_character_score.rs now executes both C#-normalized
UTF-16 character score algorithms required by legal article/item matching:
- Indel normalized LCS (IndelRatio, called by TitleSimilarity and
  BodySimilarity), and
- difflib-style longest contiguous matching-block ratio
  (SequenceRatio, used by same-item PartSimilarity).
It also executes token-array LCS similarity (Similarity) after C# has
normalized Unicode and lexed the original token sequences. Native scores
use the optional ABI v1 dc_score_document_characters_utf8 entrypoint,
limited to <=512 UTF-16 code units, plus <=256 normalized tokens per side
in token mode. Unsupported/oversized requests use the original managed
algorithm. When native parity verification is enabled, the exact C#
score is calculated and compared (absolute <=1e-12); divergence
retains the original result. Unit tests explicitly cover Korean,
combining marks, astral surrogate pairs and transposed/repeated text.

Deterministic live tests check 840/840 C# vs native character score pairs
and 480/480 token-LCS similarity scores. An independent production
smoke confirms both native score paths actually execute without C#
shadow verification.

New Rust doc_korean_review.rs executes the entire short-Korean-word
dynamic programming alignment, tie-breaking, traceback and exact/fuzzy
evidence gate from TryKoreanReviewDiff. C# continues to supply original
Korean word tokens, morphological pair relation costs, and the text/UTF-16
marker presentation. The optional ABI v1 symbol is
dc_align_document_korean_review_utf8. Original managed DP is kept as a
fallback and golden reference. Four real fuzzy-changed Korean sentence
cases pass source parity on marker actions, text and positions; the
production smoke requires successful native DP adoption.

## Completion boundary and honest limitations

Comparison matching decisions now use Rust-first for bounded article
lineage, list parts, generic blocks and DOCX table rows/cells, and
most review word anchors. Character scores, token LCS scoring, long
preserved-sentence anchors and eligible short Korean morphology also
have Rust-first implementations.

However, NativeComparisonEngine.cs is still an essential C# file:
- Unicode FormC/FormKC normalization and original word/token lexing;
- DOM/Word paragraph identification, legal article and structure
  recovery, text unit creation, structural numbering and reference IDs;
- global cross-unit part movements, markers, coalescing, absolute source
  offsets, A/B/C row/display/Excel alignment; and
- unsupported very large inputs and existing managed-compatible fallbacks.

Do not delete the file or claim a standalone 100% Rust implementation.
This is a verifiable functional port of large comparison subpipelines,
not a source-file-for-source-file replacement. Full retirement requires
the remaining parser/normalization/marker model and their reference
tests to move into Rust in a separately reviewed API boundary.

## Rust-first A/B/C row placement and display shading (2026-10-24)

NativeComparisonEngine.BuildRows now delegates the baseline-aligned
cross-document row grid, per-gap insertion ordering, and compatible
unmatched addition coalescing to doc_row_alignment.rs. The managed
input supplies previously selected article/unit correspondence and the
original semantic coalescing-predicate outcomes. The Rust function
dc_align_document_rows_utf8 owns the resulting complete A/B/C index
grid, which is checked against the original BuildRowsCSharp output
for exact row/document object identity when native parity is enabled.
540 deterministic 2- and 3-way row placement cases passed with no
mismatch, and the live regression asserts the Rust path was used.

NativeComparisonEngine.BuildSegments now delegates final deletion,
addition and replacement shading segments to doc_segments.rs,
including overlapping markers (both), UTF-16 absolute offsets,
unchanged spans, and exact text concatenation. Splits inside a UTF-16
surrogate pair are an intentional fallback to the original C# path.
550 source-equivalent randomized test vectors passed; live document
regressions confirm zero shading mismatches and production smoke
asserts both shading and A/B/C row placement run in Rust.

These additions do NOT complete a 100% Rust rewrite of the class:
document normalization, legal/general structural parsing, semantic
unit/text review event construction, Word-specific data model
adaptation and unmanaged C#-compatibility fallbacks remain.
100%-completion claims require separately proving these behaviors.

## Rust-first legal/general unit parser and full Unicode normalizers (2026-10-24)

- rust/src/doc_unit_parser.rs now parses complete legal and generic text
  streams into article, section, and body units. It handles article and
  chapter/section numbering, generic heading detection, clause titles,
  Roman numerals and false English article references. The caller keeps
  one pre-processing step: newline normalization and recovery of
  flattened inline strong article headers. All resulting units, metadata,
  sections and display headers are checked in their entirety against
  ParseUnitsCSharp. A 282-case deterministic legal/general fixture suite
  requires every native result to be adopted with zero C# mismatches.
- rust/src/doc_normalize.rs ports the canonical Normalizer, article
  LineageNormalize and visible-text SemanticEqual normalization using
  Unicode NFC/NFKC. It deliberately preserves .NET UTF-16 char
  semantics for astral codepoints and uses simple one-codepoint invariant
  lowercasing rather than Unicode full expanding casing. Unpaired UTF-16
  surrogate strings use the existing C# fallback. The three complete
  normalizer contracts were compared to the C# reference on 1548
  deterministic and Unicode stress cases, with zero mismatches.
- A bounded concurrent cache prevents repeated single-string native FFI
  overhead in scoring and matching loops. The Windows production smoke
  requires native parser and normalization calls to actually execute.
- The native parser is available under the ABI v1 optional export
  dc_parse_document_units_utf8; normalizers are exposed via
  dc_normalize_document_text_utf8.

The source contract is still not 100%-ported. C# owns final marker
planning and text change event coalescing, the file state and IO/Word
document adapter, and the model-building output facade. The optional
C# compatibility fallbacks remain present and tested for older native
DLLs and supported-input upper bounds.

## Rust-first final change event coalescing and review phrase spans (2026-10-26)

The remaining review/event workflow gained two production Rust-first
engines. Both use optional ABI-v1 exports and must pass exact managed
result parity under DOCUMENTCOMPARE_REQUIRE_NATIVE=1.

- doc_replacement_coalesce.rs receives typed deletion/addition markers
  with their canonical visible text, UTF-16 lengths, positions, legal
  item numbers and counterpart endpoint coordinates. Rust owns the
  candidate distance/tolerance/structure test, score/order, exclusive
  one-to-one replacement selection and the exact legacy C# short-circuit
  reservation rule. The C# facade only converts chosen pairs to the
  already-established NativeMarker.Change models; a zero-mismatch
  650-case randomized test checks the complete emitted marker stream
  (actions, text, messages, indices, ordering and endpoints) against
  CoalesceLocationReplacementsCSharp. It is used by the actual
  ComparePair and Word review path.
- doc_review_pieces.rs implements EventParts punctuation/whitespace
  splitting and TrimSharedHangulPrefix shared lexical prefix clipping.
  Both preserve .NET UTF-16 text boundaries and return an unsupported
  status if a supplied span bisects a surrogate pair. All downstream
  action constructors remain unchanged. 450 seeded piece vectors
  plus 450 Hangul-prefix cases must select native code without any
  C# parity fallback.
- The existing C# normalization and surrogate fallback protects
  unsupported inputs and remains the parity reference.
- New entrypoints: dc_coalesce_document_replacements_utf8 and
  dc_review_document_text_pieces_utf8.

**Not 100% standalone Rust yet:** DiffText marker action planning,
MapBoundary's semantic anchor projection, global cross-unit change
classification, and final output model/Word review adaptation still
execute in C#. Do not delete the managed engine or label the entire
source file fully migrated solely from these narrower passing tests.


## Rust full generic review and UTF-16 boundary projection — 2026-10-26

A new native boundary engine (rust/src/doc_boundary.rs) takes existing
C# Regex-derived lexical tokens, normalization keys and word character
classification and performs the complete source-to-target MapBoundary
algorithm, including contextual word anchors, SequenceMatcher character
opcodes and punctuation-adjacent counterpart correction. MapBoundary
calls Rust first, with managed fallback for unsupported long inputs or
older DLLs. A deterministic 3,072-boundary corpus passed source parity
with zero drift and complete native adoption.

rust/src/doc_diff_hunks.rs replaces generic DiffText's repeated
common-token hunk reconstruction, retaining exact UTF-16 token offsets
and original hunk order. It passed 520/520 independent source comparisons
without fallback.

rust/src/doc_diff_review.rs is a complete generic-text DiffText
review event decision API: the Rust program owns matching anchors,
changed-hunk selection, punctuation handling, Korean shared-prefix
clipping, phrase decomposition, and source/target boundary location.
The managed facade only converts the resulting typed events into
the existing NativeMarker presentation/Word export model. Unlike
the prior narrow helpers, the general path is one native call.
Rust emits change, deletion and insertion events, and the managed
parity gate compares the complete final marker list, including
actions, original/new text, offsets, endpoints, hunk order, and
message representation. All 350 deterministic stress cases passed
with full native adoption and no parity drift.

During testing, the original C# char.IsLetterOrDigit numeric-category
contract uncovered that Circled Digits (e.g. ③ and ⑥) are treated as
non-decimal symbol/number characters for punctuation-only filtering.
The native implementation was corrected, reducing 4 event mismatches
to zero. Keep those seeded cases as release safeguards.

Remaining for full-file 100%-Rust retirement: ComparePair's global
legal article/part structural classification, final structural-movement
message creation, complete marker/action planning for specialized
exceptions and model/Word review formatting. The source-reader and
exporter may remain C#, but calling this a fully Rust-owned
NativeComparisonEngine before the above ownership transitions
would be incorrect.
