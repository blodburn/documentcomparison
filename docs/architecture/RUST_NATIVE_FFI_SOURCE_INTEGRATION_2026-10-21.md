# Rust native code-diff integration (source only)

## Execution path

The code-version view in VersionHistoryControl calls
NativeCodeVersionComparer.Compare, not the managed LCS directly.
The native library implements a versioned C ABI in core-rust/src/ffi.rs;
C# verifies ABI major version 1, uses explicit UTF-8 bytes and zero-based,
half-open line spans, and frees the native result with dc_free_diff_result
in a finally block. CodeVersionChangeMapper receives the resulting spans
and produces stable UnifiedChangeEvent records. The existing
CodeVersionComparer remains a managed fallback only when the native
library has not been embedded. An incompatible or broken embedded
library raises an error rather than silently changing comparison engines.

## Build

1. Install .NET 8 SDK and stable Rust.
2. Run cargo build --manifest-path core-rust/Cargo.toml --release.
3. Run dotnet run --project tests/DocumentCompare.Regression/DocumentCompare.Regression.csproj -c Release
   with DOCUMENTCOMPARE_REQUIRE_NATIVE=1 to verify native use.
4. Run python3 tests/code_diff_parity.py dotnet cargo with
   DOCUMENTCOMPARE_REQUIRE_NATIVE=1 for cross-language hunk parity.

Windows x64 produces core-rust/target/release/documentcompare_core.dll.
Linux x64 produces core-rust/target/release/libdocumentcompare_core.so.
MSBuild embeds the matching binary as a managed resource. The Windows
build script and Windows CI job compile Rust before C# compilation.
Single-file packaging includes the resource, so no sidecar DLL has to be
distributed. At run time the native binary is extracted into a
SHA-256-addressed temporary folder and validated before being loaded.
The C# document comparison engine and Word/Excel exporters are unchanged.

## Scope and remaining constraints

- Rust now uses the actual Tree-sitter Rust and C# grammars to compare
  bounded declaration syntax trees, in parallel with unchanged exact line
  diff results. The AST layer can identify uniquely named functions/classes,
  track changes to a declaration, and detect moves/renames only with unique
  byte-identical, nontrivial method bodies. Empty/duplicated bodies never
  prove a move. Unsupported languages, parsing errors, or AST limits produce
  an explicit AST-unavailable state while exact line comparison continues.
  Tree-sitter AST results are transferred as Rust-owned UTF-8 JSON with
  dc_compare_ast_json_utf8 and dc_free_ast_json_result.
  The view adds numbered "구조" items alongside the pre-existing "코드" items,
  with UTF-8 byte span provenance and anchors to original physical lines.
- AST results are *syntactic*, not full semantic equivalence or dependency
  analysis. Declarations that also contain changed nested members may
  produce both parent and child changes. Cross-file AST move detection, Git
  rename history, and language-aware refactoring analysis are not implemented.
  Exact line changes remain authoritative for character-level content diffs.
- AST parser hard limits are 2 MB per source, 5,000 declarations and 128
  levels of nesting; no AST claims are emitted when those limits are hit.
- Native Rust folder comparison is now callable through the same ABI.
  The version-history screen offers a separate "폴더 비교" action and
  displays added/deleted/modified/exact-byte moved or renamed files.
  Results use batch rendering (250 entries at a time) to avoid freezing
  the UI for large folders; all findings remain accessible. The folder
  comparison is read-only and is not saved as an immutable folder snapshot.
  It rejects a missing native engine instead of inventing an approximate
  managed comparison.
- Rust folder rename/move detection requires unique byte-identical files
  on both sides. Modified-and-moved files are not inferred as renames.
  Build folders are excluded; maximum 20,000 files and 20,000 directories
  per side, maximum 40 MB per file.
- Folder ABI appends functions to ABI v1 and returns explicit length-delimited
  UTF-8 paths in Rust-owned buffers, released by dc_free_folder_result.
  C# validates count, buffer bounds, type codes, UTF-8, and layout.
- Build-time Rust is mandatory for Windows builds that must use Rust;
  manually building only the C# project without a native binary produces
  the intentional C# compatibility fallback.
- Runtime extraction needs temp-directory write access; a corrupt
  extracted library triggers a visible error.
- This change does not create a release or alter the app version.
