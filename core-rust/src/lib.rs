pub mod ast;
pub mod cross_file;
pub mod ffi;
pub mod folder;
// First Rust engine slice: deterministic text-line comparison.
// It is intentionally separate from the existing C# document judgement engine.
// AST declaration comparisons live in ast.rs; line comparison remains exact.

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ChangeKind {
    Insert,
    Delete,
    Replace,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LineChange {
    pub kind: ChangeKind,
    /// Half-open, zero-based line spans in the before/after inputs.
    pub before: std::ops::Range<usize>,
    pub after: std::ops::Range<usize>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Step {
    Keep,
    Remove,
    Add,
}

/// Compare UTF-8 text lines. Uses an LCS for inputs within its memory budget,
/// and returns a conservative replacement for bigger divergent regions.
/// This deliberately does not infer semantic equivalence or code moves.
pub fn diff_lines(before: &str, after: &str) -> Vec<LineChange> {
    let a: Vec<&str> = before.split_inclusive('\n').collect();
    let b: Vec<&str> = after.split_inclusive('\n').collect();
    let mut prefix = 0;
    while prefix < a.len() && prefix < b.len() && a[prefix] == b[prefix] {
        prefix += 1;
    }
    let mut suffix = 0;
    while suffix < a.len() - prefix
        && suffix < b.len() - prefix
        && a[a.len() - suffix - 1] == b[b.len() - suffix - 1]
    {
        suffix += 1;
    }
    let aa = &a[prefix..a.len() - suffix];
    let bb = &b[prefix..b.len() - suffix];
    if aa.is_empty() && bb.is_empty() {
        return Vec::new();
    }
    if aa.is_empty() {
        return vec![LineChange {
            kind: ChangeKind::Insert,
            before: prefix..prefix,
            after: prefix..prefix + bb.len(),
        }];
    }
    if bb.is_empty() {
        return vec![LineChange {
            kind: ChangeKind::Delete,
            before: prefix..prefix + aa.len(),
            after: prefix..prefix,
        }];
    }

    // Bounded LCS: don't allocate huge matrices on large repositories.
    if aa.len().saturating_mul(bb.len()) > 4_000_000 {
        return vec![LineChange {
            kind: ChangeKind::Replace,
            before: prefix..prefix + aa.len(),
            after: prefix..prefix + bb.len(),
        }];
    }

    let cols = bb.len() + 1;
    let mut lcs = vec![0_u32; (aa.len() + 1) * cols];
    for i in (0..aa.len()).rev() {
        for j in (0..bb.len()).rev() {
            lcs[i * cols + j] = if aa[i] == bb[j] {
                1 + lcs[(i + 1) * cols + j + 1]
            } else {
                lcs[(i + 1) * cols + j].max(lcs[i * cols + j + 1])
            };
        }
    }

    let mut steps = Vec::new();
    let (mut i, mut j) = (0, 0);
    while i < aa.len() || j < bb.len() {
        if i < aa.len() && j < bb.len() && aa[i] == bb[j] {
            steps.push(Step::Keep);
            i += 1;
            j += 1;
        } else if i < aa.len()
            && (j == bb.len() || lcs[(i + 1) * cols + j] >= lcs[i * cols + j + 1])
        {
            steps.push(Step::Remove);
            i += 1;
        } else {
            steps.push(Step::Add);
            j += 1;
        }
    }

    let (mut ai, mut bj) = (prefix, prefix);
    let mut result = Vec::new();
    let mut k = 0;
    while k < steps.len() {
        if steps[k] == Step::Keep {
            ai += 1;
            bj += 1;
            k += 1;
            continue;
        }
        let (begin_a, begin_b) = (ai, bj);
        while k < steps.len() && steps[k] != Step::Keep {
            match steps[k] {
                Step::Remove => ai += 1,
                Step::Add => bj += 1,
                Step::Keep => {}
            }
            k += 1;
        }
        let kind = match (ai > begin_a, bj > begin_b) {
            (true, true) => ChangeKind::Replace,
            (true, false) => ChangeKind::Delete,
            (false, true) => ChangeKind::Insert,
            (false, false) => unreachable!(),
        };
        result.push(LineChange {
            kind,
            before: begin_a..ai,
            after: begin_b..bj,
        });
    }
    result
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn same_file() {
        assert!(diff_lines("한글\nhello\n", "한글\nhello\n").is_empty());
    }
    #[test]
    fn replace_middle_line() {
        assert_eq!(
            diff_lines("a\nb\nc\n", "a\nX\nc\n"),
            vec![LineChange {
                kind: ChangeKind::Replace,
                before: 1..2,
                after: 1..2
            }]
        );
    }
    #[test]
    fn append_line() {
        assert_eq!(
            diff_lines("a\n", "a\nb\n"),
            vec![LineChange {
                kind: ChangeKind::Insert,
                before: 1..1,
                after: 1..2
            }]
        );
    }
    #[test]
    fn remove_first_line() {
        assert_eq!(
            diff_lines("a\nb\n", "b\n"),
            vec![LineChange {
                kind: ChangeKind::Delete,
                before: 0..1,
                after: 0..0
            }]
        );
    }
    #[test]
    fn unicode_and_newlines() {
        assert_eq!(
            diff_lines("가😀\n", "가😀\r\n"),
            vec![LineChange {
                kind: ChangeKind::Replace,
                before: 0..1,
                after: 0..1
            }]
        );
    }
    #[test]
    fn empty_files() {
        assert!(diff_lines("", "").is_empty());
    }
    #[test]
    fn large_file_safe_fallback() {
        let old = "a\n".repeat(2200);
        let new = "b\n".repeat(2200);
        let events = diff_lines(&old, &new);
        assert_eq!(events.len(), 1);
        assert_eq!(events[0].kind, ChangeKind::Replace);
        assert_eq!(events[0].before, 0..2200);
    }
    #[test]
    fn terminal_newline_is_significant() {
        assert_eq!(
            diff_lines("abc\n", "abc"),
            vec![LineChange {
                kind: ChangeKind::Replace,
                before: 0..1,
                after: 0..1
            }]
        );
    }
    #[test]
    fn crlf_to_lf_is_a_replacement() {
        assert_eq!(
            diff_lines("hello\r\n", "hello\n"),
            vec![LineChange {
                kind: ChangeKind::Replace,
                before: 0..1,
                after: 0..1
            }]
        );
    }
    #[test]
    fn deleting_last_line_anchors_end() {
        assert_eq!(
            diff_lines("a\nb\n", "a\n"),
            vec![LineChange {
                kind: ChangeKind::Delete,
                before: 1..2,
                after: 1..1
            }]
        );
    }
    #[test]
    fn utf8_bom_is_not_silently_stripped() {
        assert_eq!(
            diff_lines("x\n", "\u{feff}x\n"),
            vec![LineChange {
                kind: ChangeKind::Replace,
                before: 0..1,
                after: 0..1
            }]
        );
    }

    #[test]
    fn trailing_blank_line_is_a_line() {
        assert_eq!(
            diff_lines("a\n", "a\n\n"),
            vec![LineChange {
                kind: ChangeKind::Insert,
                before: 1..1,
                after: 1..2
            }]
        );
    }
}
