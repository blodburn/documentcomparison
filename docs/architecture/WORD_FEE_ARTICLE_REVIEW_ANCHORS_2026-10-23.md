# Legal paragraph change ranges: fee article 26 -> 27

## User-reported reproduction

The screenshot from application v5.21.9 shows a recognized article-number
change (26 -> 27), but its whole Korean fee paragraph struck through on the
left and entirely underlined on the right. In reality, the first and third
long sentences are verbatim-identical; a sentence allowing price changes
has been removed and a statutory refund/withdrawal safeguard has been added.

This was NOT caused by the 2026-10 Rust document-engine migration.
The same wholePlainRewrite low-global-similarity threshold was already
present in the immutable Git tag v5.21.9; the current main tree retained it.
In the screenshot case, the canonical C# ComparePair function bypassed
DiffText and generated one whole-body replacement.

## Repair

1. Do not apply the whole-plain-paragraph rewrite shortcut when substantial
   unchanged edge context or an identical long sentence survives elsewhere.
   Keep that shortcut for genuinely unrelated long plain-body rewrites.
2. Within DiffText, before generic token LCS, identify verbatim identical
   substantial sentences (40+ UTF-16 code units) as stable anchors in longer
   paragraphs. Compare only unmatched ranges between anchors, preserving
   punctuation attached to the same original sentence. This fixes a second
   reproduced bug: when falling back to token LCS alone, a shared sentence
   period was incorrectly flagged as a separate deletion.
3. Respect the punctuation-inclusion option, and do not normalize away an
   actual whitespace change when deciding sentence anchors. Reuse the
   existing managed UTF-16 offsets and event/segment rendering contract.
4. Avoid any changes to Rust ABI, item matching, article lineage or the
   Word exporter. The fix is in the authoritative managed document
   display/review range judgment; Word tracked-change export has its
   own separately tested algorithm.

## Regression contract

tests/DocumentCompare.Regression/Program.cs contains the exact
Korean three-sentence before/after reproduction using generated DOCX.
It asserts the header-number marker 26 -> 27, exactly two body markers,
the full deleted price-change sentence and added refund safeguard,
no unchanged sentence in either marker or the change summary, and precise
per-side UTF-16 endpoint offsets.

It also tests a long shared sentence in the middle of two changed
sentences, no false anchoring for unrelated short text, and no treating
sentences with changed internal whitespace as identical.

## Deployment

Code-only correction. The application project version and latest published
Release remain v5.21.15 unless explicitly updated. A user running the
older v5.21.9 binary will continue to see the issue until running a
future release that includes this fix.
