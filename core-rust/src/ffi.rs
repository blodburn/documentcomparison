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
