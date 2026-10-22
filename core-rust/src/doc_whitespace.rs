//! Python/C# document-engine parity slice: *whitespace-only* text changes.
//! The canonical contract is NativeComparisonEngine.TryWhitespaceBoundaryDiff.
//! All positions are .NET UTF-16 code-unit offsets, not UTF-8 byte offsets.
//! This module does NOT attempt to parse legal structure or DOCX tables.
use serde::Serialize;

const MAX_UTF16_UNITS: usize = 1_048_576;

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Span {
    pub old_start: usize,
    pub old_end: usize,
    pub new_start: usize,
    pub new_end: usize,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Result {
    pub eligible: bool,
    pub spans: Vec<Span>,
}

fn is_whitespace(unit: u16) -> bool {
    matches!(unit,
        0x0009..=0x000D | 0x0020 | 0x0085 | 0x00A0 | 0x1680 |
        0x2000..=0x200A | 0x2028 | 0x2029 | 0x202F | 0x205F | 0x3000)
}

fn context_char(unit: u16) -> bool {
    if unit == b'_' as u16 {
        return true;
    }
    // A UTF-16 surrogate is NOT independently a letter/digit in .NET.
    let Some(ch) = char::from_u32(u32::from(unit)) else {
        return false;
    };
    use unicode_general_category::{GeneralCategory as Category, get_general_category};
    matches!(
        get_general_category(ch),
        Category::UppercaseLetter
            | Category::LowercaseLetter
            | Category::TitlecaseLetter
            | Category::ModifierLetter
            | Category::OtherLetter
            | Category::DecimalNumber
    )
}

fn gap(positions: &[usize], length: usize, boundary: usize) -> (usize, usize) {
    (
        if boundary == 0 {
            0
        } else {
            positions[boundary - 1] + 1
        },
        if boundary == positions.len() {
            length
        } else {
            positions[boundary]
        },
    )
}

fn context(text: &[u16], gap_start: usize, gap_end: usize) -> (usize, usize) {
    let mut start = gap_start;
    while start > 0 && context_char(text[start - 1]) {
        start -= 1;
    }
    let mut end = gap_end;
    while end < text.len() && context_char(text[end]) {
        end += 1;
    }
    (start, end)
}

/// Equivalent to C# TryWhitespaceBoundaryDiff's change range computation.
/// Unsupported/irrelevant inputs return eligible=false and no spans.
pub fn compare(before: &str, after: &str) -> std::result::Result<Result, &'static str> {
    let old: Vec<u16> = before.encode_utf16().collect();
    let new: Vec<u16> = after.encode_utf16().collect();
    if old.len() > MAX_UTF16_UNITS || new.len() > MAX_UTF16_UNITS {
        return Err("document whitespace fragment exceeds budget");
    }
    let old_positions: Vec<_> = old
        .iter()
        .enumerate()
        .filter_map(|(i, &ch)| (!is_whitespace(ch)).then_some(i))
        .collect();
    let new_positions: Vec<_> = new
        .iter()
        .enumerate()
        .filter_map(|(i, &ch)| (!is_whitespace(ch)).then_some(i))
        .collect();
    if old_positions.is_empty()
        || old_positions.len() != new_positions.len()
        || old_positions
            .iter()
            .zip(new_positions.iter())
            .any(|(&i, &j)| old[i] != new[j])
    {
        return Ok(Result {
            eligible: false,
            spans: Vec::new(),
        });
    }
    let mut spans: Vec<Span> = Vec::new();
    for boundary in 0..=old_positions.len() {
        let (os, oe) = gap(&old_positions, old.len(), boundary);
        let (ns, ne) = gap(&new_positions, new.len(), boundary);
        if old[os..oe] == new[ns..ne] {
            continue;
        }
        if old[os..oe].iter().any(|&c| !is_whitespace(c))
            || new[ns..ne].iter().any(|&c| !is_whitespace(c))
        {
            return Ok(Result {
                eligible: false,
                spans: Vec::new(),
            });
        }
        let (old_start, old_end) = context(&old, os, oe);
        let (new_start, new_end) = context(&new, ns, ne);
        if let Some(previous) = spans.last_mut()
            && old_start <= previous.old_end
            && new_start <= previous.new_end
        {
            previous.old_end = previous.old_end.max(old_end);
            previous.new_end = previous.new_end.max(new_end);
        } else {
            spans.push(Span {
                old_start,
                old_end,
                new_start,
                new_end,
            });
        }
    }
    spans.retain(|c| old[c.old_start..c.old_end] != new[c.new_start..c.new_end]);
    Ok(Result {
        eligible: !spans.is_empty(),
        spans,
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn consent_whitespace_change_including_prefix() {
        let r = compare("동의없이", "동의 없이").unwrap();
        assert!(r.eligible);
        assert_eq!(
            r.spans,
            vec![Span {
                old_start: 0,
                old_end: 4,
                new_start: 0,
                new_end: 5,
            }]
        );
    }
    #[test]
    fn whitespace_reversed() {
        let r = compare("동의 없이", "동의없이").unwrap();
        assert!(r.eligible);
        assert_eq!(r.spans[0].old_end, 5);
        assert_eq!(r.spans[0].new_end, 4);
    }
    #[test]
    fn multiple_distinct_words() {
        let r = compare("제 1 조 · 동의없이", "제1조 · 동의 없이").unwrap();
        assert!(r.eligible);
        assert!(r.spans.len() >= 2);
    }
    #[test]
    fn text_rewrite_is_outside_this_slice() {
        assert!(!compare("변경 전", "변경 후").unwrap().eligible);
    }
    #[test]
    fn all_spaces_is_outside_this_slice() {
        assert!(!compare("   ", "\t").unwrap().eligible);
    }
    #[test]
    fn identical_input_is_not_a_change() {
        assert!(!compare("변경 없이", "변경 없이").unwrap().eligible);
    }
    #[test]
    fn supplementary_unicode_preserves_utf16_offsets() {
        let r = compare("😀동의없이", "😀동의 없이").unwrap();
        assert!(r.eligible);
        assert_eq!(r.spans[0].old_start, 2);
        assert_eq!(r.spans[0].new_start, 2);
    }
    #[test]
    fn underscore_is_a_context_character() {
        let r = compare("code_a", "code_ a").unwrap();
        assert!(r.eligible);
        assert_eq!(r.spans[0].old_start, 0);
    }
    #[test]
    fn unicode_whitespace_sets() {
        for ch in ['\u{00A0}', '\u{3000}', '\u{2007}', '\u{202F}', '\u{0085}'] {
            let r = compare("동의없이", &format!("동의{ch}없이")).unwrap();
            assert!(r.eligible);
        }
    }
}
