# Unified Version Phase 2: parity and folder-comparison hardening

## Changes in v5.21.15

- The Avalonia C# fallback now compares line tokens including their terminators, like Rust split_inclusive newline, instead of silently equating x and x plus LF. On-screen line text removes the terminator only for display. A newline-only replacement receives an explicit label.
- Code-preview markers retain physical source row numbers and include every changed row as an actual scroll target, even when the first 10,000 lines are followed by skipped unchanged regions. Deletion at EOF stays anchored to the last available row or empty-file placeholder.
- Rust directory move/rename detection groups (size, fingerprint) candidates with ordered maps rather than scanning candidate collections quadratically. Exact bytes are compared before a move is reported. Repeated identical candidates remain additions/deletions.
- C# SourceTextOffsets supplies strict UTF-8 byte to UTF-16 code-unit boundaries to prepare native engine integration. Offset conversion does not itself connect the native engine.
- Restored damaged Windows build-script properties and README architecture links.

## Testing

- Rust unit tests cover Unicode paths, unique renames, duplicate/ambiguous candidates, add/delete/modify, build-directory exclusions, oversized files and symlink exclusion, plus code diff/newline cases.
- C# regression covers LF/CRLF, terminal newline, final-line deletion, trailing blank lines, very long preview, and UTF-8 byte/UTF-16 offset conversion (Korean + emoji); existing DOCX/Word/Excel regression fixtures remain in the same run.
- Synthetic folder benchmark: 3,000 unique identical moves from 3,000 before and 3,000 after files, 0.09 seconds elapsed / 5,120 KB maximum resident set on the project server (single sample, no baseline comparison). This is not a production benchmark.
- Native Windows Rust DLL/FFI integration and AST-aware analysis remain NOT IMPLEMENTED. Windows desktop code diff continues to use C#.

## Next milestones

1. Shared machine-readable change-event interchange and a cross-language golden corpus with explicit UTF-8 and UTF-16 coordinates.
2. Versioned project directory snapshots; Git commit/tree snapshots, branch and tag history.
3. Tree-sitter function/class-level AST comparison and conservative code-symbol relocation.
4. Native Windows Rust linkage only after correctness equivalence and packaging can be demonstrated.
