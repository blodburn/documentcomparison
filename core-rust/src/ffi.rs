//! Versioned and explicit C ABI for the line-diff engine.
//! The caller owns its input bytes. The result allocation is owned by Rust and
//! must be released with dc_free_diff_result.
use crate::{ChangeKind, diff_lines};
use std::{panic::AssertUnwindSafe, ptr, slice};

const MAX_INPUT_BYTES: usize = 40 * 1024 * 1024;

#[repr(C)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct DcLineChange {
    pub kind: u32, // 1=insert, 2=delete, 3=replace
    pub reserved: u32,
    pub before_start: u64,
    pub before_end: u64,
    pub after_start: u64,
    pub after_end: u64,
}

#[repr(C)]
pub struct DcDiffResult {
    pub changes: *mut DcLineChange,
    pub count: usize,
}

impl DcDiffResult {
    fn empty() -> Self {
        Self {
            changes: ptr::null_mut(),
            count: 0,
        }
    }
}

/// Returns the major ABI version. Never change the layout or semantics for v1.
#[unsafe(no_mangle)]
pub extern "C" fn dc_diff_abi_version() -> u32 {
    1
}

/// Compare validated UTF-8 text. Zero-based, half-open line ranges.
/// Status: 0 success, 1 invalid UTF-8, 2 null/oversized argument, 3 internal panic.
///
/// # Safety
/// The non-null byte pointers must refer to readable memory of the supplied
/// lengths for the duration of the call. The output pointer must refer to one
/// writable DcDiffResult. Never call concurrently using the same output.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dc_diff_lines_utf8(
    before: *const u8,
    before_len: usize,
    after: *const u8,
    after_len: usize,
    output: *mut DcDiffResult,
) -> i32 {
    if output.is_null() {
        return 2;
    }
    // SAFETY: validated writable output pointer is a caller precondition.
    unsafe { ptr::write(output, DcDiffResult::empty()) };
    if before_len > MAX_INPUT_BYTES
        || after_len > MAX_INPUT_BYTES
        || (before_len > 0 && before.is_null())
        || (after_len > 0 && after.is_null())
    {
        return 2;
    }

    // Prevent panics from unwinding through a foreign caller. No Rust-owned
    // allocation escapes on an error.
    let compared = std::panic::catch_unwind(AssertUnwindSafe(|| {
        let old_bytes = if before_len == 0 {
            &[][..]
        } else {
            // SAFETY: readable input pointer and size are caller preconditions.
            unsafe { slice::from_raw_parts(before, before_len) }
        };
        let new_bytes = if after_len == 0 {
            &[][..]
        } else {
            // SAFETY: readable input pointer and size are caller preconditions.
            unsafe { slice::from_raw_parts(after, after_len) }
        };
        let old = std::str::from_utf8(old_bytes).map_err(|_| 1)?;
        let new = std::str::from_utf8(new_bytes).map_err(|_| 1)?;
        let changes: Vec<DcLineChange> = diff_lines(old, new)
            .into_iter()
            .map(|change| DcLineChange {
                kind: match change.kind {
                    ChangeKind::Insert => 1,
                    ChangeKind::Delete => 2,
                    ChangeKind::Replace => 3,
                },
                reserved: 0,
                before_start: change.before.start as u64,
                before_end: change.before.end as u64,
                after_start: change.after.start as u64,
                after_end: change.after.end as u64,
            })
            .collect();
        Ok::<_, i32>(changes.into_boxed_slice())
    }));
    match compared {
        Ok(Ok(changes)) => {
            if changes.is_empty() {
                return 0;
            }
            let count = changes.len();
            let changes = Box::into_raw(changes) as *mut DcLineChange;
            // SAFETY: output is writable and changes is a valid Rust allocation.
            unsafe { ptr::write(output, DcDiffResult { changes, count }) };
            0
        }
        Ok(Err(code)) => code,
        Err(_) => 3,
    }
}

/// Release the result's Rust allocation. Idempotent for the same result record.
///
/// # Safety
/// Output must be null or a valid writable DcDiffResult previously filled by
/// dc_diff_lines_utf8. No other thread may use it during this call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dc_free_diff_result(output: *mut DcDiffResult) {
    if output.is_null() {
        return;
    }
    // SAFETY: caller guarantees valid result memory; take ownership once.
    let old = unsafe { ptr::replace(output, DcDiffResult::empty()) };
    if !old.changes.is_null() && old.count != 0 {
        let slice = ptr::slice_from_raw_parts_mut(old.changes, old.count);
        // SAFETY: pointer and count originate from Box::into_raw above.
        unsafe { drop(Box::from_raw(slice)) };
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn line_ffi_roundtrip_multibyte_and_eof() {
        let before = "가😀\r\nkeep\n";
        let after = "가😀\nkeep\nextra\n";
        let mut output = DcDiffResult::empty();
        let result = unsafe {
            dc_diff_lines_utf8(
                before.as_ptr(),
                before.len(),
                after.as_ptr(),
                after.len(),
                &mut output,
            )
        };
        assert_eq!(result, 0);
        assert_eq!(dc_diff_abi_version(), 1);
        let changes = unsafe { slice::from_raw_parts(output.changes, output.count) };
        assert_eq!(changes.len(), 2);
        assert_eq!(changes[0].kind, 3);
        assert_eq!((changes[0].before_start, changes[0].after_start), (0, 0));
        assert_eq!(changes[1].kind, 1);
        assert_eq!((changes[1].before_start, changes[1].after_end), (2, 3));
        unsafe { dc_free_diff_result(&mut output) };
        assert!(output.changes.is_null());
        assert_eq!(output.count, 0);
        unsafe { dc_free_diff_result(&mut output) };
    }

    #[test]
    fn rejects_invalid_utf8_and_null_pointer() {
        let invalid = [0xff_u8];
        let mut output = DcDiffResult::empty();
        assert_eq!(
            unsafe {
                dc_diff_lines_utf8(invalid.as_ptr(), invalid.len(), ptr::null(), 0, &mut output)
            },
            1
        );
        assert!(output.changes.is_null());
        assert_eq!(
            unsafe { dc_diff_lines_utf8(ptr::null(), 1, ptr::null(), 0, &mut output) },
            2
        );
        assert_eq!(
            unsafe { dc_diff_lines_utf8(ptr::null(), 0, ptr::null(), 0, ptr::null_mut()) },
            2
        );
        assert_eq!(
            unsafe {
                dc_diff_lines_utf8(
                    ptr::null(),
                    MAX_INPUT_BYTES + 1,
                    ptr::null(),
                    0,
                    &mut output,
                )
            },
            2
        );
        unsafe { dc_free_diff_result(&mut output) };
    }
}

/// UTF-8 path byte offsets into DcFolderResult.names (not zero-terminated).
#[repr(C)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct DcFolderChange {
    pub kind: u32, // 1=added, 2=deleted, 3=modified, 4=renamed
    pub reserved: u32,
    pub before_offset: u32,
    pub before_len: u32,
    pub after_offset: u32,
    pub after_len: u32,
}

#[repr(C)]
pub struct DcFolderResult {
    pub entries: *mut DcFolderChange,
    pub count: usize,
    pub names: *mut u8,
    pub names_len: usize,
}

impl DcFolderResult {
    fn empty() -> Self {
        Self {
            entries: ptr::null_mut(),
            count: 0,
            names: ptr::null_mut(),
            names_len: 0,
        }
    }
}

const MAX_FOLDER_PATH_BYTES: usize = 32768;
const MAX_RESULT_NAME_BYTES: usize = 64 * 1024 * 1024;

fn append_name(buffer: &mut Vec<u8>, name: &str) -> Result<(u32, u32), i32> {
    let offset = u32::try_from(buffer.len()).map_err(|_| 2)?;
    let len = u32::try_from(name.len()).map_err(|_| 2)?;
    if buffer.len().saturating_add(name.len()) > MAX_RESULT_NAME_BYTES {
        return Err(2);
    }
    buffer.extend_from_slice(name.as_bytes());
    Ok((offset, len))
}

/// Compare complete directories using the same deterministic Rust logic as the
/// CLI. Output owns its memory and must be released with dc_free_folder_result.
/// Status: 0 success, 1 invalid UTF-8, 2 invalid/oversized input,
/// 3 filesystem error, 4 Rust panic.
///
/// # Safety
/// The path pointers must be readable for the supplied lengths; output must be
/// a writable DcFolderResult. No concurrent mutation of output is allowed.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dc_compare_folders_utf8(
    before: *const u8,
    before_len: usize,
    after: *const u8,
    after_len: usize,
    output: *mut DcFolderResult,
) -> i32 {
    if output.is_null() {
        return 2;
    }
    // SAFETY: caller guarantees a valid writable output pointer.
    unsafe { ptr::write(output, DcFolderResult::empty()) };
    if before.is_null()
        || after.is_null()
        || before_len == 0
        || after_len == 0
        || before_len > MAX_FOLDER_PATH_BYTES
        || after_len > MAX_FOLDER_PATH_BYTES
    {
        return 2;
    }

    let compared = std::panic::catch_unwind(AssertUnwindSafe(|| {
        // SAFETY: caller guarantees both input pointer lengths.
        let old = unsafe { slice::from_raw_parts(before, before_len) };
        let new = unsafe { slice::from_raw_parts(after, after_len) };
        let old = std::str::from_utf8(old).map_err(|_| 1)?;
        let new = std::str::from_utf8(new).map_err(|_| 1)?;
        if old.contains('\0') || new.contains('\0') {
            return Err(2);
        }
        let changes = crate::folder::compare_directories(
            std::path::Path::new(old),
            std::path::Path::new(new),
        )
        .map_err(|_| 3)?;
        let mut entries = Vec::with_capacity(changes.len());
        let mut names = Vec::new();
        for change in changes {
            use crate::folder::FileChange;
            let (kind, before, after) = match change {
                FileChange::Added(path) => (1, String::new(), path),
                FileChange::Deleted(path) => (2, path, String::new()),
                FileChange::Modified(path) => (3, path.clone(), path),
                FileChange::Renamed { before, after } => (4, before, after),
            };
            let (before_offset, before_len) = append_name(&mut names, &before)?;
            let (after_offset, after_len) = append_name(&mut names, &after)?;
            entries.push(DcFolderChange {
                kind,
                reserved: 0,
                before_offset,
                before_len,
                after_offset,
                after_len,
            });
        }
        Ok::<_, i32>((entries.into_boxed_slice(), names.into_boxed_slice()))
    }));

    match compared {
        Ok(Ok((entries, names))) => {
            let count = entries.len();
            let names_len = names.len();
            let entries = if count == 0 {
                ptr::null_mut()
            } else {
                Box::into_raw(entries) as *mut DcFolderChange
            };
            let names = if names_len == 0 {
                ptr::null_mut()
            } else {
                Box::into_raw(names) as *mut u8
            };
            // SAFETY: valid result pointer and owned allocations.
            unsafe {
                ptr::write(
                    output,
                    DcFolderResult {
                        entries,
                        count,
                        names,
                        names_len,
                    },
                )
            };
            0
        }
        Ok(Err(status)) => status,
        Err(_) => 4,
    }
}

/// # Safety
/// Output must be a valid writable result previously produced by the native
/// folder comparison API, and must not be concurrently accessed.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dc_free_folder_result(output: *mut DcFolderResult) {
    if output.is_null() {
        return;
    }
    // SAFETY: output is a valid writable result by caller contract.
    let result = unsafe { ptr::replace(output, DcFolderResult::empty()) };
    if !result.entries.is_null() && result.count != 0 {
        let raw = ptr::slice_from_raw_parts_mut(result.entries, result.count);
        // SAFETY: allocated by Box::into_raw in dc_compare_folders_utf8.
        unsafe { drop(Box::from_raw(raw)) };
    }
    if !result.names.is_null() && result.names_len != 0 {
        let raw = ptr::slice_from_raw_parts_mut(result.names, result.names_len);
        // SAFETY: allocated by Box::into_raw in dc_compare_folders_utf8.
        unsafe { drop(Box::from_raw(raw)) };
    }
}

#[cfg(test)]
mod folder_ffi_tests {
    use super::*;
    use std::fs;

    #[test]
    fn folder_ffi_moves_and_unicode_paths() {
        let root = std::env::temp_dir().join(format!("dc-ffi-folder-{}", std::process::id()));
        let _ = fs::remove_dir_all(&root);
        let a = root.join("before");
        let b = root.join("after");
        fs::create_dir_all(&a).unwrap();
        fs::create_dir_all(&b).unwrap();
        fs::write(a.join("옛날.rs"), "a").unwrap();
        fs::write(b.join("새로운.rs"), "a").unwrap();
        fs::write(b.join("added.py"), "b").unwrap();
        let old = a.to_string_lossy();
        let new = b.to_string_lossy();
        let mut result = DcFolderResult::empty();
        let status = unsafe {
            dc_compare_folders_utf8(
                old.as_ptr(),
                old.len(),
                new.as_ptr(),
                new.len(),
                &mut result,
            )
        };
        assert_eq!(status, 0);
        assert_eq!(result.count, 2);
        let entries = unsafe { slice::from_raw_parts(result.entries, result.count) };
        let data = unsafe { slice::from_raw_parts(result.names, result.names_len) };
        let tuples: Vec<_> = entries
            .iter()
            .map(|entry| {
                let before = &data[entry.before_offset as usize
                    ..(entry.before_offset + entry.before_len) as usize];
                let after = &data
                    [entry.after_offset as usize..(entry.after_offset + entry.after_len) as usize];
                (
                    entry.kind,
                    std::str::from_utf8(before).unwrap().to_owned(),
                    std::str::from_utf8(after).unwrap().to_owned(),
                )
            })
            .collect();
        assert_eq!(
            tuples,
            vec![
                (1, "".to_string(), "added.py".to_string()),
                (4, "옛날.rs".to_string(), "새로운.rs".to_string()),
            ]
        );
        unsafe { dc_free_folder_result(&mut result) };
        assert_eq!(result.count, 0);
        assert!(result.names.is_null());
        unsafe { dc_free_folder_result(&mut result) };
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn folder_abi_has_fixed_x64_layout() {
        assert_eq!(std::mem::size_of::<DcFolderChange>(), 24);
        #[cfg(target_pointer_width = "64")]
        assert_eq!(std::mem::size_of::<DcFolderResult>(), 32);
    }

    #[test]
    fn folder_ffi_rejects_invalid_paths_and_empty_result() {
        let mut result = DcFolderResult::empty();
        let invalid = [0xff_u8];
        let name = b"abc";
        assert_eq!(
            unsafe {
                dc_compare_folders_utf8(
                    invalid.as_ptr(),
                    invalid.len(),
                    name.as_ptr(),
                    name.len(),
                    &mut result,
                )
            },
            1
        );
        assert_eq!(
            unsafe {
                dc_compare_folders_utf8(ptr::null(), 0, name.as_ptr(), name.len(), &mut result)
            },
            2
        );
        assert_eq!(result.count, 0);
        unsafe { dc_free_folder_result(&mut result) };
    }
}

/// Rust-owned JSON representation of bounded AST declaration changes.
#[repr(C)]
pub struct DcAstJsonResult {
    pub data: *mut u8,
    pub length: usize,
}

impl DcAstJsonResult {
    fn empty() -> Self {
        Self {
            data: ptr::null_mut(),
            length: 0,
        }
    }
}

/// Language: 1 Rust, 2 C#. Returns a UTF-8 JSON array of declaration events.
/// 0 success; 1 invalid UTF-8; 2 invalid args; 3 syntax/budget unsupported;
/// 4 internal serialization error; 5 unexpected Rust panic.
/// The syntax error case is NOT a false "no changes" result.
///
/// # Safety
/// Input pointers must be readable for supplied lengths. Output must be
/// writable and not concurrently used. Free successful results with
/// dc_free_ast_json_result.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dc_compare_ast_json_utf8(
    language: u32,
    before: *const u8,
    before_len: usize,
    after: *const u8,
    after_len: usize,
    output: *mut DcAstJsonResult,
) -> i32 {
    if output.is_null() {
        return 2;
    }
    // SAFETY: writable output memory is guaranteed by caller.
    unsafe { ptr::write(output, DcAstJsonResult::empty()) };
    let language = match language {
        1 => crate::ast::Language::Rust,
        2 => crate::ast::Language::CSharp,
        _ => return 2,
    };
    if before_len > MAX_INPUT_BYTES
        || after_len > MAX_INPUT_BYTES
        || (before_len != 0 && before.is_null())
        || (after_len != 0 && after.is_null())
    {
        return 2;
    }
    let compared = std::panic::catch_unwind(AssertUnwindSafe(|| {
        let old = if before_len == 0 {
            &[][..]
        } else {
            // SAFETY: caller guarantees valid readable bytes.
            unsafe { slice::from_raw_parts(before, before_len) }
        };
        let new = if after_len == 0 {
            &[][..]
        } else {
            // SAFETY: caller guarantees valid readable bytes.
            unsafe { slice::from_raw_parts(after, after_len) }
        };
        let old = std::str::from_utf8(old).map_err(|_| 1)?;
        let new = std::str::from_utf8(new).map_err(|_| 1)?;
        let changes = crate::ast::compare(language, old, new).map_err(|_| 3)?;
        serde_json::to_vec(&changes).map_err(|_| 4)
    }));
    match compared {
        Ok(Ok(json)) => {
            let raw = json.into_boxed_slice();
            let length = raw.len();
            let data = Box::into_raw(raw) as *mut u8;
            // SAFETY: writable output memory is guaranteed by caller.
            unsafe { ptr::write(output, DcAstJsonResult { data, length }) };
            0
        }
        Ok(Err(code)) => code,
        Err(_) => 5,
    }
}

/// # Safety
/// Output must be null or a writable result previously returned by
/// dc_compare_ast_json_utf8, with exclusive ownership during this call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dc_free_ast_json_result(output: *mut DcAstJsonResult) {
    if output.is_null() {
        return;
    }
    // SAFETY: valid writable result memory is guaranteed by caller.
    let old = unsafe { ptr::replace(output, DcAstJsonResult::empty()) };
    if !old.data.is_null() && old.length != 0 {
        // SAFETY: allocated by Box::into_raw in dc_compare_ast_json_utf8.
        unsafe {
            drop(Box::from_raw(ptr::slice_from_raw_parts_mut(
                old.data, old.length,
            )))
        };
    }
}

#[cfg(test)]
mod ast_ffi_tests {
    use super::*;

    #[test]
    fn serializes_real_ast_changes_and_frees_memory() {
        let before = "fn first() { 1; }\n";
        let after = "fn second() { 1; }\n";
        let mut output = DcAstJsonResult::empty();
        let code = unsafe {
            dc_compare_ast_json_utf8(
                1,
                before.as_ptr(),
                before.len(),
                after.as_ptr(),
                after.len(),
                &mut output,
            )
        };
        assert_eq!(code, 0);
        let bytes = unsafe { slice::from_raw_parts(output.data, output.length) };
        let events: serde_json::Value = serde_json::from_slice(bytes).unwrap();
        assert_eq!(events[0]["kind"], "Rename");
        assert_eq!(events[0]["beforeName"], "first");
        assert_eq!(events[0]["afterName"], "second");
        unsafe { dc_free_ast_json_result(&mut output) };
        assert_eq!(output.length, 0);
        unsafe { dc_free_ast_json_result(&mut output) };
    }

    #[test]
    fn rejects_unknown_language_and_malformed_source() {
        let old = "fn x() {";
        let new = "fn x() {}";
        let mut output = DcAstJsonResult::empty();
        assert_eq!(
            unsafe {
                dc_compare_ast_json_utf8(
                    0,
                    old.as_ptr(),
                    old.len(),
                    new.as_ptr(),
                    new.len(),
                    &mut output,
                )
            },
            2
        );
        assert_eq!(
            unsafe {
                dc_compare_ast_json_utf8(
                    1,
                    old.as_ptr(),
                    old.len(),
                    new.as_ptr(),
                    new.len(),
                    &mut output,
                )
            },
            3
        );
        assert_eq!(output.length, 0);
        unsafe { dc_free_ast_json_result(&mut output) };
    }
}

/// Compare changed Rust/C# declarations across entire folder revisions.
/// Reuses DcAstJsonResult and dc_free_ast_json_result. The returned JSON
/// contains moves, analyzedFiles, and skippedFiles (partial-analysis warning).
/// Status: 0 success, 1 invalid UTF-8, 2 invalid path, 3 filesystem/budget
/// error, 4 JSON error, 5 Rust panic.
///
/// # Safety
/// Input pointers must be readable for the provided lengths and output must
/// be one writable, exclusively owned DcAstJsonResult.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dc_compare_cross_file_ast_json_utf8(
    before: *const u8,
    before_len: usize,
    after: *const u8,
    after_len: usize,
    output: *mut DcAstJsonResult,
) -> i32 {
    if output.is_null() {
        return 2;
    }
    // SAFETY: writable memory is guaranteed by the caller.
    unsafe { ptr::write(output, DcAstJsonResult::empty()) };
    if before.is_null()
        || after.is_null()
        || before_len == 0
        || after_len == 0
        || before_len > MAX_FOLDER_PATH_BYTES
        || after_len > MAX_FOLDER_PATH_BYTES
    {
        return 2;
    }
    let compared = std::panic::catch_unwind(AssertUnwindSafe(|| {
        // SAFETY: readable pointers are guaranteed by the caller.
        let before_bytes = unsafe { slice::from_raw_parts(before, before_len) };
        let after_bytes = unsafe { slice::from_raw_parts(after, after_len) };
        let before = std::str::from_utf8(before_bytes).map_err(|_| 1)?;
        let after = std::str::from_utf8(after_bytes).map_err(|_| 1)?;
        if before.contains('\0') || after.contains('\0') {
            return Err(2);
        }
        let report = crate::cross_file::compare_folders(
            std::path::Path::new(before),
            std::path::Path::new(after),
        )
        .map_err(|_| 3)?;
        serde_json::to_vec(&report).map_err(|_| 4)
    }));
    match compared {
        Ok(Ok(json)) => {
            let bytes = json.into_boxed_slice();
            let length = bytes.len();
            let data = Box::into_raw(bytes) as *mut u8;
            // SAFETY: valid writable result pointer is guaranteed by caller.
            unsafe { ptr::write(output, DcAstJsonResult { data, length }) };
            0
        }
        Ok(Err(status)) => status,
        Err(_) => 5,
    }
}

#[cfg(test)]
mod cross_file_ffi_tests {
    use super::*;
    use std::fs;
    use std::sync::atomic::{AtomicU64, Ordering};
    static NEXT: AtomicU64 = AtomicU64::new(0);

    #[test]
    fn real_folder_symbol_move_via_ffi() {
        let root = std::env::temp_dir().join(format!(
            "dc-cross-ffi-{}-{}",
            std::process::id(),
            NEXT.fetch_add(1, Ordering::Relaxed)
        ));
        let (a, b) = (root.join("old"), root.join("new"));
        fs::create_dir_all(&a).unwrap();
        fs::create_dir_all(&b).unwrap();
        fs::write(
            a.join("old.rs"),
            "fn test() { let n = 14; }\nfn stay() { 1; }\n",
        )
        .unwrap();
        fs::write(b.join("old.rs"), "fn stay() { 1; }\n").unwrap();
        fs::write(b.join("new.rs"), "fn test() { let n = 14; }\n").unwrap();
        let a = a.to_string_lossy();
        let b = b.to_string_lossy();
        let mut output = DcAstJsonResult::empty();
        assert_eq!(
            unsafe {
                dc_compare_cross_file_ast_json_utf8(
                    a.as_ptr(),
                    a.len(),
                    b.as_ptr(),
                    b.len(),
                    &mut output,
                )
            },
            0
        );
        let data = unsafe { slice::from_raw_parts(output.data, output.length) };
        let json: serde_json::Value = serde_json::from_slice(data).unwrap();
        assert_eq!(json["moves"][0]["beforePath"], "old.rs");
        assert_eq!(json["moves"][0]["afterPath"], "new.rs");
        assert_eq!(json["moves"][0]["symbolName"], "test");
        assert_eq!(json["skippedFiles"], 0);
        unsafe { dc_free_ast_json_result(&mut output) };
        assert_eq!(output.length, 0);
        fs::remove_dir_all(root).unwrap();
    }
}

/// The bounded document whitespace parity slice. Reads strict UTF-8 source,
/// returns UTF-16 position spans in Rust-owned JSON; free using
/// dc_free_ast_json_result. 0=success, 1=invalid UTF8, 2=invalid arguments,
/// 3=unsupported/oversized, 4=serialization failure, 5=Rust panic.
///
/// # Safety
/// Input pointers must refer to readable byte buffers for the given lengths
/// and output to a writable DcAstJsonResult with exclusive ownership.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dc_compare_document_whitespace_utf8(
    before: *const u8,
    before_len: usize,
    after: *const u8,
    after_len: usize,
    output: *mut DcAstJsonResult,
) -> i32 {
    if output.is_null() {
        return 2;
    }
    // SAFETY: valid writable output pointer is guaranteed by caller.
    unsafe { ptr::write(output, DcAstJsonResult::empty()) };
    const MAX_DOCUMENT_FRAGMENT_BYTES: usize = 3 * 1024 * 1024;
    if before_len > MAX_DOCUMENT_FRAGMENT_BYTES
        || after_len > MAX_DOCUMENT_FRAGMENT_BYTES
        || (before_len > 0 && before.is_null())
        || (after_len > 0 && after.is_null())
    {
        return 2;
    }
    let result = std::panic::catch_unwind(AssertUnwindSafe(|| {
        let left = if before_len == 0 {
            &[][..]
        } else {
            // SAFETY: input bytes are valid for duration of call.
            unsafe { slice::from_raw_parts(before, before_len) }
        };
        let right = if after_len == 0 {
            &[][..]
        } else {
            // SAFETY: input bytes are valid for duration of call.
            unsafe { slice::from_raw_parts(after, after_len) }
        };
        let before = std::str::from_utf8(left).map_err(|_| 1)?;
        let after = std::str::from_utf8(right).map_err(|_| 1)?;
        let compared = crate::doc_whitespace::compare(before, after).map_err(|_| 3)?;
        serde_json::to_vec(&compared).map_err(|_| 4)
    }));
    match result {
        Ok(Ok(json)) => {
            let bytes = json.into_boxed_slice();
            let length = bytes.len();
            let data = Box::into_raw(bytes) as *mut u8;
            // SAFETY: valid writable output pointer is guaranteed by caller.
            unsafe { ptr::write(output, DcAstJsonResult { data, length }) };
            0
        }
        Ok(Err(code)) => code,
        Err(_) => 5,
    }
}

#[cfg(test)]
mod document_whitespace_ffi_tests {
    use super::*;

    #[test]
    fn whitespace_ffi_json_utf16_contract_and_free() {
        let old = "😀동의없이";
        let new = "😀동의 없이";
        let mut output = DcAstJsonResult::empty();
        assert_eq!(
            unsafe {
                dc_compare_document_whitespace_utf8(
                    old.as_ptr(),
                    old.len(),
                    new.as_ptr(),
                    new.len(),
                    &mut output,
                )
            },
            0
        );
        let bytes = unsafe { slice::from_raw_parts(output.data, output.length) };
        let data: serde_json::Value = serde_json::from_slice(bytes).unwrap();
        assert_eq!(data["eligible"], true);
        assert_eq!(data["spans"][0]["oldStart"], 2);
        assert_eq!(data["spans"][0]["newEnd"], 7);
        unsafe { dc_free_ast_json_result(&mut output) };
        assert!(output.data.is_null());
        assert_eq!(output.length, 0);
    }

    #[test]
    fn whitespace_ffi_rejects_invalid_utf8() {
        let invalid = [0xFF];
        let mut output = DcAstJsonResult::empty();
        assert_eq!(
            unsafe {
                dc_compare_document_whitespace_utf8(
                    invalid.as_ptr(),
                    invalid.len(),
                    std::ptr::null(),
                    0,
                    &mut output,
                )
            },
            1
        );
        assert!(output.data.is_null());
    }
}

/// Parse document 항/호 list segments using C#-compatible UTF-16 offsets.
/// This is only a document structure segmentation parity slice: no pairing,
/// re-numbering, structural movement or Word table matching is inferred.
/// Free returned Rust-owned JSON with dc_free_ast_json_result.
/// Status 0=ok, 1=invalid UTF-8, 2=invalid args, 3=budget, 4=JSON, 5=panic.
///
/// # Safety
/// Non-null input byte pointers must remain readable for their lengths;
/// output must point to one writable DcAstJsonResult.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dc_parse_document_parts_utf8(
    input: *const u8,
    input_len: usize,
    output: *mut DcAstJsonResult,
) -> i32 {
    if output.is_null() {
        return 2;
    }
    // SAFETY: writable result required by caller.
    unsafe { ptr::write(output, DcAstJsonResult::empty()) };
    if input_len > 3 * 131_072 || (input_len > 0 && input.is_null()) {
        return 2;
    }
    let result = std::panic::catch_unwind(AssertUnwindSafe(|| {
        let bytes = if input_len == 0 {
            &[][..]
        } else {
            // SAFETY: readable input required by caller.
            unsafe { slice::from_raw_parts(input, input_len) }
        };
        let source = std::str::from_utf8(bytes).map_err(|_| 1)?;
        let parsed = crate::doc_parts::parse_parts(source).map_err(|_| 3)?;
        serde_json::to_vec(&parsed).map_err(|_| 4)
    }));
    match result {
        Ok(Ok(json)) => {
            let memory = json.into_boxed_slice();
            let length = memory.len();
            let data = Box::into_raw(memory) as *mut u8;
            // SAFETY: writable result required by caller.
            unsafe { ptr::write(output, DcAstJsonResult { data, length }) };
            0
        }
        Ok(Err(code)) => code,
        Err(_) => 5,
    }
}

#[cfg(test)]
mod document_parts_ffi_tests {
    use super::*;
    #[test]
    fn ffi_utf16_offsets_and_free() {
        let input = "😀설명\n① 동의 없이\n② 두 번째 내용";
        let mut result = DcAstJsonResult::empty();
        assert_eq!(
            unsafe { dc_parse_document_parts_utf8(input.as_ptr(), input.len(), &mut result) },
            0
        );
        let bytes = unsafe { slice::from_raw_parts(result.data, result.length) };
        let json: serde_json::Value = serde_json::from_slice(bytes).unwrap();
        assert_eq!(json["parts"][0]["label"], "본문");
        assert_eq!(json["parts"][1]["label"], "①");
        assert_eq!(json["parts"][1]["start"], 5);
        unsafe { dc_free_ast_json_result(&mut result) };
        assert!(result.data.is_null());
    }
    #[test]
    fn ffi_invalid_utf8() {
        let input = [0xff_u8];
        let mut result = DcAstJsonResult::empty();
        assert_eq!(
            unsafe { dc_parse_document_parts_utf8(input.as_ptr(), input.len(), &mut result) },
            1
        );
        assert!(result.data.is_null());
    }
}

/// Compute the hierarchy topology of a bounded collection of document
/// enumerator labels; the result is an optional C# parity slice.
/// Input is a UTF-8 JSON object containing a labels array.
/// Free with dc_free_ast_json_result.
///
/// # Safety
/// Non-null input must point to readable bytes; output must be writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dc_document_part_hierarchy_json_utf8(
    input: *const u8,
    length: usize,
    output: *mut DcAstJsonResult,
) -> i32 {
    if output.is_null() {
        return 2;
    }
    // SAFETY: caller supplies exclusive writable output.
    unsafe { ptr::write(output, DcAstJsonResult::empty()) };
    if length > 131_072 || (length > 0 && input.is_null()) {
        return 2;
    }
    let result = std::panic::catch_unwind(AssertUnwindSafe(|| {
        let bytes = if length == 0 {
            &[][..]
        } else {
            // SAFETY: caller supplies readable input bytes.
            unsafe { slice::from_raw_parts(input, length) }
        };
        let json: crate::doc_parts::HierarchyInput =
            serde_json::from_slice(bytes).map_err(|_| 1)?;
        let hierarchy = crate::doc_parts::hierarchy(&json.labels).map_err(|_| 3)?;
        serde_json::to_vec(&hierarchy).map_err(|_| 4)
    }));
    match result {
        Ok(Ok(json)) => {
            let memory = json.into_boxed_slice();
            let length = memory.len();
            let data = Box::into_raw(memory) as *mut u8;
            // SAFETY: caller supplies exclusive writable output.
            unsafe { ptr::write(output, DcAstJsonResult { data, length }) };
            0
        }
        Ok(Err(code)) => code,
        Err(_) => 5,
    }
}

/// The exact-normalized-content first pass of the C# MatchParts matcher.
/// JSON Input {"oldKeys":[...],"newKeys":[...]} contains keys normalized
/// by the original C# engine, not untrusted claimed raw legal equivalence.
/// The returned Rust-owned JSON uses dc_free_ast_json_result.
///
/// # Safety
/// input points to readable bytes for length, and output is writable and
/// exclusively owned by the caller for the duration of this function.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dc_match_exact_document_part_keys_utf8(
    input: *const u8,
    length: usize,
    output: *mut DcAstJsonResult,
) -> i32 {
    if output.is_null() {
        return 2;
    }
    // SAFETY: guaranteed writable pointer under the contract above.
    unsafe { ptr::write(output, DcAstJsonResult::empty()) };
    if length > 512 * 1024 || (length > 0 && input.is_null()) {
        return 2;
    }
    let result = std::panic::catch_unwind(AssertUnwindSafe(|| {
        let bytes = if length == 0 {
            &[][..]
        } else {
            // SAFETY: guaranteed readable pointer under the contract above.
            unsafe { slice::from_raw_parts(input, length) }
        };
        let payload: crate::doc_match_exact::Input =
            serde_json::from_slice(bytes).map_err(|_| 1)?;
        let pairs =
            crate::doc_match_exact::pair(&payload.old_keys, &payload.new_keys).map_err(|_| 3)?;
        serde_json::to_vec(&pairs).map_err(|_| 4)
    }));
    match result {
        Ok(Ok(json)) => {
            let bytes = json.into_boxed_slice();
            let length = bytes.len();
            let data = Box::into_raw(bytes) as *mut u8;
            // SAFETY: guaranteed writable pointer under the contract above.
            unsafe { ptr::write(output, DcAstJsonResult { data, length }) };
            0
        }
        Ok(Err(code)) => code,
        Err(_) => 5,
    }
}

#[cfg(test)]
mod exact_document_part_ffi_tests {
    use super::*;
    #[test]
    fn exact_part_ffi_and_free() {
        let input = br#"{"oldKeys":["first","second"],"newKeys":["extra","first","second"]}"#;
        let mut output = DcAstJsonResult::empty();
        assert_eq!(
            unsafe {
                dc_match_exact_document_part_keys_utf8(input.as_ptr(), input.len(), &mut output)
            },
            0
        );
        let raw = unsafe { slice::from_raw_parts(output.data, output.length) };
        let result: serde_json::Value = serde_json::from_slice(raw).unwrap();
        assert_eq!(result["pairs"][0]["old"], 0);
        assert_eq!(result["pairs"][0]["new"], 1);
        unsafe { dc_free_ast_json_result(&mut output) };
        assert!(output.data.is_null());
    }
}

/// Select high-similarity changed-enumerator move candidates from C#-prepared
/// legal part similarities, containment scores and child support.
/// # Safety
/// Caller must supply readable input bytes and writable exclusive output.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dc_select_document_part_move_candidates_utf8(
    input: *const u8,
    length: usize,
    output: *mut DcAstJsonResult,
) -> i32 {
    if output.is_null() {
        return 2;
    }
    // SAFETY: exclusive writable output guaranteed by the caller.
    unsafe { ptr::write(output, DcAstJsonResult::empty()) };
    if length > 1024 * 1024 || (length > 0 && input.is_null()) {
        return 2;
    }
    let result = std::panic::catch_unwind(AssertUnwindSafe(|| {
        let bytes = if length == 0 {
            &[][..]
        } else {
            // SAFETY: readable input guaranteed by the caller.
            unsafe { slice::from_raw_parts(input, length) }
        };
        let data: crate::doc_move_candidates::Input =
            serde_json::from_slice(bytes).map_err(|_| 1)?;
        let moves =
            crate::doc_move_candidates::select(data.old_count, data.new_count, &data.candidates)
                .map_err(|_| 3)?;
        serde_json::to_vec(&moves).map_err(|_| 4)
    }));
    match result {
        Ok(Ok(json)) => {
            let allocation = json.into_boxed_slice();
            let length = allocation.len();
            let data = Box::into_raw(allocation) as *mut u8;
            // SAFETY: exclusive writable output guaranteed by the caller.
            unsafe { ptr::write(output, DcAstJsonResult { data, length }) };
            0
        }
        Ok(Err(code)) => code,
        Err(_) => 5,
    }
}

/// C#-gated same-label sibling-shape pairing, within a bounded set of
/// C#-prepared same-label, same-level, parent-compatible candidates.
/// # Safety
/// Caller must provide readable input bytes and exclusive writable output.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dc_select_document_same_label_parts_utf8(
    input: *const u8,
    length: usize,
    output: *mut DcAstJsonResult,
) -> i32 {
    if output.is_null() {
        return 2;
    }
    // SAFETY: exclusive writable result guaranteed by the caller.
    unsafe { ptr::write(output, DcAstJsonResult::empty()) };
    if length > 1024 * 1024 || (length > 0 && input.is_null()) {
        return 2;
    }
    let result = std::panic::catch_unwind(AssertUnwindSafe(|| {
        let bytes = if length == 0 {
            &[][..]
        } else {
            // SAFETY: readable input guaranteed by the caller.
            unsafe { slice::from_raw_parts(input, length) }
        };
        let data: crate::doc_same_label::Input = serde_json::from_slice(bytes).map_err(|_| 1)?;
        let selection =
            crate::doc_same_label::select(data.old_count, data.new_count, &data.candidates)
                .map_err(|_| 3)?;
        serde_json::to_vec(&selection).map_err(|_| 4)
    }));
    match result {
        Ok(Ok(json)) => {
            let allocated = json.into_boxed_slice();
            let length = allocated.len();
            let data = Box::into_raw(allocated) as *mut u8;
            // SAFETY: exclusive writable result guaranteed by the caller.
            unsafe { ptr::write(output, DcAstJsonResult { data, length }) };
            0
        }
        Ok(Err(code)) => code,
        Err(_) => 5,
    }
}

/// Same logical ordinal, different numbering convention part selection.
/// C# still provides eligible candidates and structural/semantic scores.
/// # Safety
/// Input pointer/length must designate readable bytes. Output must point
/// to exclusive writable DcAstJsonResult; free via dc_free_ast_json_result.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dc_select_document_same_ordinal_parts_utf8(
    input: *const u8,
    length: usize,
    output: *mut DcAstJsonResult,
) -> i32 {
    if output.is_null() {
        return 2;
    }
    // SAFETY: writable exclusive result required by caller.
    unsafe { ptr::write(output, DcAstJsonResult::empty()) };
    if length > 1024 * 1024 || (length > 0 && input.is_null()) {
        return 2;
    }
    let result = std::panic::catch_unwind(AssertUnwindSafe(|| {
        let bytes = if length == 0 {
            &[][..]
        } else {
            // SAFETY: readable input bytes required by caller.
            unsafe { slice::from_raw_parts(input, length) }
        };
        let data: crate::doc_same_ordinal::Input = serde_json::from_slice(bytes).map_err(|_| 1)?;
        let result =
            crate::doc_same_ordinal::select(data.old_count, data.new_count, &data.candidates)
                .map_err(|_| 3)?;
        serde_json::to_vec(&result).map_err(|_| 4)
    }));
    match result {
        Ok(Ok(json)) => {
            let allocation = json.into_boxed_slice();
            let length = allocation.len();
            let data = Box::into_raw(allocation) as *mut u8;
            // SAFETY: writable exclusive result required by caller.
            unsafe { ptr::write(output, DcAstJsonResult { data, length }) };
            0
        }
        Ok(Err(status)) => status,
        Err(_) => 5,
    }
}
