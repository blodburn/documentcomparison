# DocumentCompare

DocumentCompare is a Windows desktop application for visually comparing two or three documents side by side.
It is designed for general documents as well as structured legal, policy, and regulation documents.

**Copyright © 2026 blodburn. All rights reserved.**

> This repository is publicly viewable source code, but it is **not open source**. No permission is granted to use, copy, modify, redistribute, sublicense, sell, or create derivative works without prior written permission from the copyright holder. See `LICENSE` for details. Third-party components remain subject to their respective licenses.

## Current release

**V5.20.17**

- Reject all existing Word Track Changes classes before export, including property-format revisions such as `pPrChange`, `rPrChange`, `tblPrChange`, `trPrChange`, `tcPrChange`, `sectPrChange`, and `numberingChange`.
- Read table rows/cells through transparent `w:sdt` and `w:customXml` wrappers so content-controlled table text is no longer omitted from comparison.
- Preserve those SDT/customXml wrappers during in-place Word export and keep structural row/cell addressing valid through wrapped table structures.
- Keep fully changed nested tables anchored inside their matched parent cell instead of leaking deleted content into the document body, without falling back to unsafe global table-index pairing.
- Track automatic-numbering-only changes with valid Word `w:numberingChange` metadata while keeping B's numbering/formatting as the final state.
- Tighten Roman article/item recognition to canonical Roman numerals without word-specific exceptions; invalid forms such as `CIVIL`, `IIV`, and `VX` are rejected while valid canonical forms remain supported.
- Fix SpreadsheetML rich-text property serialization order (`strike -> color -> underline`) for stricter XLSX consumers.
- Replace the large Word paragraph-gap positional fallback with Hirschberg linear-space alignment, preserving similarity-based pairing without quadratic traceback memory.
- Reuse automatic-number labels in Word paragraph anchoring so repeated numbered clauses follow the same logical lineage as the on-screen comparison.
- Add Word numbering insertion/removal regressions and canonical Roman validation for general-document headings.
- Expand the repository regression suite beyond the V5.20.15 coverage, including repeated-number anchors, revision-ID collision safety, wrapped row/cell structural revisions, nested-table export targeting, numbering add/remove, canonical Roman generic headings, property revisions, XLSX rich-text order, large-gap alignment, and in-place SDT export.

## Features

- Compare 2 or 3 documents in aligned A / B / C columns
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
