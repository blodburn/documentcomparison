# DocumentCompare

DocumentCompare is a Windows desktop application for visually comparing two or three documents side by side.
It is designed for general documents as well as structured legal, policy, and regulation documents.

**Copyright © 2026 blodburn. All rights reserved.**

> This repository is publicly viewable source code, but it is **not open source**. No permission is granted to use, copy, modify, redistribute, sublicense, sell, or create derivative works without prior written permission from the copyright holder. See `LICENSE` for details. Third-party components remain subject to their respective licenses.

## Current release

**V5.21.3**

- Reduced version-tree file-name text size so long file names fit more comfortably.
- Widened the central document preview by narrowing the left version panel and right change-summary panel.
- The three Version History columns remain resizable with splitters, but the new default layout gives the document view substantially more horizontal space.

## Features

- Compare 2 or 3 documents in aligned A / B / C columns
- Manage sequential document versions in the Version History workspace
  - Assign Before / After independently; click an assigned file again to clear that role
  - Before / After markers make the active comparison pair explicit in the version tree
  - Add versions by file picker or by dropping DOCX/TXT files onto the Version History panel
  - Save and reopen `.dcv.json` version projects
  - Preview DOCX font/paragraph styles while reviewing text, structure, and formatting changes
- Highlight deletions with red strikethrough and additions with blue underline
- Preserve decoration across spaces inside one changed phrase
- Article/paragraph-aware alignment for structured documents
- Clickable `[n]` markers that align matching positions across document columns and the Changes pane
- Search across document content and comparison results
- Optional punctuation-aware comparison
- Export comparison results to Excel
- Generate Word Track Changes output for a selected document pair
- Korean / English UI
  - Detects the Windows display language at startup
  - KR / EN can also be switched manually from the menu
- Self-contained Windows x64 distribution
  - Final package contains a single visible `DocumentCompare.exe`
  - The native C# comparison engine is compiled directly into the main executable

## Comparison logic

DocumentCompare follows a **structure-first** comparison model. The engine does not start by running a single flat text diff across the whole file. Instead, it first understands the document structure, aligns corresponding structural units, and only then performs detailed text comparison inside the smallest matched unit.

The intended comparison order is:

1. **Read the entire document**
   - The whole visible document is part of the comparison: title, dates, preamble/introduction, chapter/section headings, articles, numbered items, ordinary paragraphs, lists, and tables.
   - In legal/article mode, an article is an alignment boundary; it is **not** a filter that excludes non-article content.

2. **Detect document type and structure**
   - General documents are interpreted roughly as: `document → heading → subheading → paragraph → list → sentence → word/punctuation`.
   - Legal/policy/regulation documents are interpreted roughly as: `document → chapter → section → article → paragraph/item → sub-item → sentence → word/punctuation`.
   - Tables are treated as structural content rather than flattened into surrounding prose.

3. **Align large structural units first**
   - Match equivalent units such as `chapter ↔ chapter`, `article ↔ article`, `heading ↔ heading`, and `paragraph ↔ paragraph`.
   - Alignment considers number/label, title, text similarity, surrounding order, and internal structure instead of relying only on physical position.
   - Renumbered or moved articles can still be treated as the same logical unit when title/content lineage supports it.

4. **Align lower-level structure inside matched units**
   - After an article is matched, its internal structure is aligned again at progressively smaller levels such as paragraph/item → sub-item → lower numbered item.
   - Added units remain additions; missing units remain deletions instead of being force-matched to unrelated content.

5. **Run text diff only after structural alignment**
   - Detailed comparison is performed inside the smallest matched structural unit.
   - The comparison then descends through sentence/phrase/word/punctuation level as appropriate.
   - This avoids treating the entire document as one undifferentiated LCS/diff stream.

6. **Classify the change**
   - Changes are classified as modification, insertion, deletion, movement, renumbering, or structural change where applicable.
   - A deletion + insertion that is clearly one logical rewrite can be presented as a single `old → new` modification, while unrelated structural items remain separate.

7. **Build the on-screen review model**
   - A/B/C columns show aligned content with shared `[n]` change markers.
   - Deleted text is shown with red strikethrough and inserted text with blue underline.
   - Review presentation is optimized for human inspection and may group changes differently from exported files.

8. **Generate exports from the same comparison judgement, with output-specific presentation**
   - **Excel** presents the comparison as a review table, splitting structured legal content into article/item-level rows and arranging comparison columns for spreadsheet review.
   - **Word Track Changes** uses the selected revised/final document as the physical baseline and reconstructs the previous state as tracked revisions, so accepting all revisions should reproduce the revised document.
   - Screen, Excel, and Word output therefore share the same comparison judgement, but do not have to use identical visual grouping.

For three-document comparison, DocumentCompare evaluates the relevant document pairs (normally `A↔B`, `B↔C`, and optionally `A↔C`) while using the selected base document as the alignment axis for the review view.

> Core principle: **read the whole document → detect structure → align large units → align lower-level units → run detailed text diff → classify changes → render/export the result.**

## Supported input

- `.docx`
- `.txt`

## Build

### Requirements

For building from source:

- Windows 10/11 x64
- .NET 8 SDK

### Build the release

Run:

```bat
BUILD_AVALONIA_RELEASE.cmd
```

The distributable executable is created at:

```text
DEPLOY_PACKAGE\DocumentCompare\DocumentCompare.exe
```

A portable ZIP is also generated under `DEPLOY_PACKAGE`.

To check only the Avalonia project build, run:

```bat
CHECK_AVALONIA_BUILD.cmd
```

## Source layout

```text
avalonia/DocumentCompare.Avalonia/Engine/NativeComparisonEngine.cs   Native C# document/diff engine
avalonia/DocumentCompare.Avalonia/Engine/NativeDocumentReader.cs    Native DOCX/TXT reader
avalonia/DocumentCompare.Avalonia/Engine/NativeOfficeExporter.cs    Excel / Word exporter
avalonia/DocumentCompare.Avalonia/                                  Avalonia desktop application
BUILD_AVALONIA_RELEASE.cmd                                            Final Windows one-file build script
CHECK_AVALONIA_BUILD.cmd                                             Avalonia/C# build check
```

## License / Copyright

Copyright © 2026 blodburn. All rights reserved.

This software and its source code are proprietary. Public availability of this repository does not grant an open-source license or any right to reuse, modify, redistribute, sublicense, sell, or create derivative works. See `LICENSE` for the complete notice.

## Regression tests

Run the native comparison/export regression suite after engine or OpenXML changes:

```text
dotnet run --project tests/DocumentCompare.Regression/DocumentCompare.Regression.csproj -c Release
```

On Windows, `RUN_REGRESSION_TESTS.cmd` runs the same suite. It covers legal article lineage and item notation, DOCX visible-text consistency, table row track changes, textbox extraction, existing-revision safeguards, BOM-less UTF-16 input, Roman article headings, and OpenXML validation.
