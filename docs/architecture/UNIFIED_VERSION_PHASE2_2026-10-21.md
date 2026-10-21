# DocumentCompare — Phase 2: Source File Version UI and Rust Folder Comparison

## Baseline and scope

- Based on `origin/main` at `v5.21.13` (initial HEAD `81f3fdb`).
- No replacement of the C# document comparison / tracked Word exporter.
- Phase 2 contains **experimental Rust folder diff** and a **working desktop code-file version comparison**. It does not yet make Rust the engine behind the Windows UI and it does not claim AST/function semantics.

## Windows UI (Avalonia)

- Version History imports common code file extensions including `.rs`, `.cs`, `.py`, `.js`, `.ts`, `.tsx`, `.go`, `.java`, `.sql`, and configuration-code formats.
- Imports use the existing content-addressed immutable version store, so repeated imports at the same path with different contents become separate versions. The source may then be removed without losing the saved versions.
- Selecting source-code Before/After versions opens a numbered monospace preview of the *After* file with change markers; clicking the marker navigates to the right-side change card and vice versa.
- Code comparison is line-based and separates insertion/deletion/replacement hunks. The desktop adapter is bounded in memory: large unmatched LCS regions are conservatively reported as a replace hunk. It is not AST-based.
- A source-code file cannot be compared to a DOCX/TXT file in the code path. Long previews show the first 10,000 lines and a truncation notice. The diff retains its full set of hunks, but preview navigation cannot reach lines past the visible cutoff yet.
- Code file text is read as strict UTF-8 (optionally BOM). Non-UTF-8 code will show a read error rather than silently replacing characters.

## Experimental Rust folder CLI

From repo root:

```sh
cargo test --manifest-path core-rust/Cargo.toml
cargo run --manifest-path core-rust/Cargo.toml --bin dc-code-diff -- /path/old-project /path/new-project
```

Folder events: `Added`, `Deleted`, `Modified`, and `Renamed`. Renames are reported only on a **unique exact-byte match**, not merely a similarly named file. Multiple identical candidates remain additions/deletions. To bound work and avoid traversing outside user-selected trees, the scanner ignores symlinks and standard build folders (`.git`, `node_modules`, `target`, `bin`, `obj`, `.vs`, `.idea`); other supported regular files over 40 MiB produce a clear error. This CLI does not yet understand `.gitignore`, parse code ASTs, or integrate with the desktop version tree.

The Rust and C# line comparison algorithms use a bounded LCS strategy, with Rust/unit and C#/regression tests covering matching, changes and Unicode. The shared serialized interop contract, Windows native binary packaging, and full cross-language golden-parity runner are still pending.

## Correctness / Source repair

The upstream `v5.21.13` source checked into GitHub contained literal `[REDACTED]` substitutions in C# identifiers, the Avalonia project XML, and some OpenXML relationship fixture URLs. These were restored against the corresponding `v5.21.12` source, preserving the newer table comparison enhancements and their tests. This is an upstream build/fixture recovery, not a change to the Word exporter.

## Next milestone

1. Immutable project-*folder* snapshots, folder version selection UI, and per-file navigation.
2. Production Rust/C# bridge with Windows x64 single-EXE packaging verification and cross-language exact output parity tests.
3. AST function/class move/rename detection for the initial languages using a real grammar parser (e.g. tree-sitter), distinguishing syntax facts from semantic impact predictions.
4. Git commit/branch/tag adapter and multiple-version symbol lineage.
