# DocumentCompare Unified Version Transition — Phase 1 (2026-10-21)

## Scope and status

The first implementation milestone creates reusable version identity and snapshot storage in the existing C# desktop app, and an **independent experimental Rust text diff core**. It does not replace the production document comparison/exporter with Rust, and it does not yet supply AST comparison or folder/Git version history in the desktop app.

## Implemented

- `Models/VersionSnapshotStore.cs`: immutable SHA-256-addressed blobs under `<project>.dcv.json.assets/objects/<two-character-prefix>/<hash><extension>`; identical content and extension share one stored object; staged imports use a temporary folder until the first save; verify the bytes before a comparison and reject malformed blob identifiers.
- `Models/VersionHistoryProjectStore.cs`: copy staged objects into the project's sidecar and atomically replace the JSON manifest on Save; migrate legacy path-only entries if the source still exists; never claim a missing legacy original was preserved.
- `Models/VersionHistoryModels.cs` and `Models/VersionFileImportPolicy.cs`: version entry gains `SnapshotBlobId`; snapshot-backed imports allow the same source path to be added again after its content has changed while same-content duplicates are ignored.
- `Controls/VersionHistoryControl.cs`: normal version addition and drag/drop capture snapshots; preview and comparison read stored blobs; save/open bind the correct project sidecar. The visible version names stay the user's original names.
- `Models/UnifiedChangeModels.cs`: shared, domain-neutral event type with optional pre/post locations, byte/UTF-16 offsets, and deterministic `EventId` separate from review badge numbers. This is a **contract introduction**, not yet a replacement for current UI markers.
- `core-rust/`: standalone dependency-free Rust library and `dc-code-diff` CLI performing bounded LCS line comparison for UTF-8 code/text. Extremely large divergent regions are conservatively returned as one replacement event rather than allocating an unbounded matrix.

## Important usage detail

A saved project is the **pair** `example.dcv.json` and `example.dcv.json.assets/`. Both must travel together for reliable offline recovery. The stored originals are immutable copies, but the application does not yet offer a one-file project archive/export. Do not delete the `.assets` folder.

Existing `.dcv.json` files continue to be read. On the first save, readable legacy originals are copied into the sidecar. Legacy entries whose originals no longer exist remain unavailable; a SHA256 hash alone cannot restore missing content.

## Verification

C#:

```sh
dotnet build tests/DocumentCompare.Regression/DocumentCompare.Regression.csproj -c Release
dotnet run --project tests/DocumentCompare.Regression/DocumentCompare.Regression.csproj -c Release --no-build
```

Rust (toolchain currently installed at `/home/blodburn/.cargo/bin/` on the build server):

```sh
cargo fmt --manifest-path core-rust/Cargo.toml --check
cargo test --manifest-path core-rust/Cargo.toml
cargo run --manifest-path core-rust/Cargo.toml --bin dc-code-diff -- before.rs after.rs
```

New C# regression coverage: re-import after source changes, same-bytes duplicate detection, source deletion with historic recovery, corruption detection, hostile blob-ID rejection, legacy conversion (including missing source), stable event IDs. Rust coverage: identical text, insertion/deletion, replacement, Unicode, empty files and bounded large-input fallback.

## Next development milestones

1. Folder snapshot manifest and portable project bundle (P0), including directories, file moves, symlinks and exclusions.
2. Code file comparison UI and per-file change navigation (P1): language-specific lexer/AST adapters rather than treating code as legal clauses.
3. Rust AST analysis for initial languages, with precise byte-range/UTF-16 conversion contracts and golden parity tests against C#.
4. Git commit/branch/tag import adapter, then multi-version symbol lineage.
5. Only after correctness and deployment gates: decide whether and how to move existing DOCX/HWPX logic to Rust.

## Compatibility and release rules

- Keep current C# document/Excel/Word Track Changes behavior as the reference implementation.
- Do not ship experimental Rust executables in the Windows one-EXE release until a native packaging/FFI plan is fully tested.
- Any future snapshot storage schema change needs a versioned migration. No existing `.dcv.json` file should be silently overwritten except on explicit Save.
