//! C# NativeComparisonEngine.ParseParts parity slice.
//! No heuristic lineage judgement; only structural list segmentation.
//! Positions match C# UTF-16 indices AFTER NativeDocumentReader.NormalizeNewlines.
use regex::{Captures, Regex};
use serde::Serialize;
use std::{collections::BTreeMap, sync::LazyLock};
use unicode_general_category::{GeneralCategory as Category, get_general_category};

const CIRCLED: &str = "①②③④⑤⑥⑦⑧⑨⑩⑪⑫⑬⑭⑮⑯⑰⑱⑲⑳";
const KOREAN: &str = "가나다라마바사아자차카타파하";
const MAX_UTF16: usize = 131_072;
const MAX_PARTS: usize = 4096;

static ITEM: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r"(?m)^[\t\p{Zs}\p{Cf}]*(?P<label>(?:[①-⑳]|\(\p{Nd}+\)|\([A-Za-z]\)|\([IVXLCDMivxlcdm]{2,}\)|\p{Nd}+[.)]|[가-하A-Za-z][.)]))(?P<ws>[\t\p{Zs}]+)").unwrap()
});
static EMBED: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r"(?m)(?P<gap>^[\t\p{Zs}]*|[\t\p{Zs}]+)(?P<label>(?:[①-⑳]|\(\p{Nd}+\)|\([A-Za-z]\)|\([IVXLCDMivxlcdm]{2,}\)|\p{Nd}+[.)]|[가-하A-Za-z][.)]))").unwrap()
});

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Part {
    pub label: String,
    pub start: usize,
    pub end: usize,
    pub core_start: usize,
    pub core_end: usize,
    pub core: String,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Result {
    pub parts: Vec<Part>,
}

fn length_utf16(text: &str) -> usize {
    text.encode_utf16().count()
}
fn utf16_offset(text: &str, byte: usize) -> usize {
    length_utf16(&text[..byte])
}
fn is_whitespace(ch: char) -> bool {
    ch.is_whitespace() || ch == '\u{0085}'
}
fn format_view(text: &str) -> String {
    text.chars()
        .map(|ch| {
            if ch.len_utf16() == 1 && get_general_category(ch) == Category::Format {
                ' '
            } else {
                ch
            }
        })
        .collect()
}

fn roman_value(text: &str) -> Option<usize> {
    fn digit(ch: char) -> Option<usize> {
        match ch.to_ascii_uppercase() {
            'I' => Some(1),
            'V' => Some(5),
            'X' => Some(10),
            'L' => Some(50),
            'C' => Some(100),
            'D' => Some(500),
            'M' => Some(1000),
            _ => None,
        }
    }
    let values: Vec<usize> = text.chars().map(digit).collect::<Option<_>>()?;
    if values.is_empty() {
        return None;
    }
    let mut total = 0_i64;
    for (i, value) in values.iter().enumerate() {
        if i + 1 < values.len() && *value < values[i + 1] {
            total -= *value as i64;
        } else {
            total += *value as i64;
        }
    }
    if !(1..=3999).contains(&total) {
        return None;
    }
    // The C# RomanValue rejects noncanonical strings (IIX, IC, MMMM).
    let mut remainder = total as usize;
    let mut canonical = String::new();
    for (value, glyph) in [
        (1000, "M"),
        (900, "CM"),
        (500, "D"),
        (400, "CD"),
        (100, "C"),
        (90, "XC"),
        (50, "L"),
        (40, "XL"),
        (10, "X"),
        (9, "IX"),
        (5, "V"),
        (4, "IV"),
        (1, "I"),
    ] {
        while remainder >= value {
            canonical.push_str(glyph);
            remainder -= value;
        }
    }
    (canonical == text.to_ascii_uppercase()).then_some(total as usize)
}

fn enum_key(label: &str) -> Option<(String, usize)> {
    if label.chars().count() == 1
        && let Some(i) = CIRCLED.chars().position(|ch| label.starts_with(ch))
    {
        return Some(("circled".into(), i + 1));
    }
    if label.starts_with('(') && label.ends_with(')') {
        let inner = &label[1..label.len() - 1];
        if let Ok(n) = inner.parse::<usize>() {
            return Some(("paren-number".into(), n));
        }
        if inner.len() == 1 && inner.as_bytes()[0].is_ascii_alphabetic() {
            return Some((
                "paren-alpha".into(),
                (inner.as_bytes()[0].to_ascii_uppercase() - b'A' + 1) as usize,
            ));
        }
        if inner.len() >= 2
            && let Some(n) = roman_value(inner)
        {
            return Some(("paren-roman".into(), n));
        }
    }
    if (label.ends_with('.') || label.ends_with(')')) && label.len() > 1 {
        let head = &label[..label.len() - 1];
        if let Ok(n) = head.parse::<usize>() {
            return Some((format!("number-{}", &label[label.len() - 1..]), n));
        }
        if head.len() == 1 && head.as_bytes()[0].is_ascii_alphabetic() {
            return Some((
                format!("alpha-{}", &label[label.len() - 1..]),
                (head.as_bytes()[0].to_ascii_uppercase() - b'A' + 1) as usize,
            ));
        }
        if let Some(i) = KOREAN.find(head) {
            let index = KOREAN[..i].chars().count();
            return Some((format!("korean-{}", &label[label.len() - 1..]), index + 1));
        }
    }
    None
}

#[derive(Clone)]
struct Candidate {
    boundary: Option<usize>,
    line_start: usize,
    at_line_start: bool,
    label: String,
    family: String,
    ordinal: usize,
}

fn group<'a>(caps: &'a Captures<'_>, name: &str) -> regex::Match<'a> {
    caps.name(name).unwrap()
}

fn recover_boundaries(text: &str) -> String {
    let mut chars: Vec<char> = text.chars().collect();
    let mut groups: BTreeMap<usize, Vec<Candidate>> = BTreeMap::new();
    for caps in EMBED.captures_iter(text) {
        let gap = group(&caps, "gap");
        let label = group(&caps, "label");
        // The original .NET regex uses a positive lookahead for following
        // whitespace. Rust regex does not support lookarounds.
        if !text[label.end()..]
            .chars()
            .next()
            .is_some_and(|ch| ch == '\t' || get_general_category(ch) == Category::SpaceSeparator)
        {
            continue;
        }
        let Some((family, ordinal)) = enum_key(label.as_str()) else {
            continue;
        };
        let before_gap = &text[..gap.start()];
        let line_start = before_gap.rfind('\n').map_or(0, |idx| idx + 1);
        let line_start = text[..line_start].chars().count();
        let at_line_start = text[..gap.start()].chars().count() == line_start;
        let boundary =
            (!at_line_start && !gap.is_empty()).then(|| text[..gap.end()].chars().count() - 1);
        if let Some(pos) = boundary
            && gap.as_str().chars().count() >= 2
        {
            chars[pos] = '\n';
        }
        groups.entry(line_start).or_default().push(Candidate {
            boundary,
            line_start,
            at_line_start,
            label: label.as_str().into(),
            family,
            ordinal,
        });
    }
    for (_, mut items) in groups {
        items.sort_by_key(|v| v.boundary.unwrap_or(v.line_start));
        // "(i)" is alphabetic unless next to a genuine multi-letter Roman
        // item such as "(ii)", "(iii)".
        for x in 0..items.len() {
            if items[x].family != "paren-alpha" || items[x].label.len() != 3 {
                continue;
            }
            let ch = items[x].label.as_bytes()[1] as char;
            if !matches!(ch, 'i' | 'I' | 'v' | 'V' | 'x' | 'X') {
                continue;
            }
            let Some(rv) = roman_value(&ch.to_string()) else {
                continue;
            };
            let neighbor = [x.checked_sub(1), (x + 1 < items.len()).then_some(x + 1)]
                .iter()
                .flatten()
                .any(|&y| items[y].family == "paren-roman" && items[y].ordinal.abs_diff(rv) == 1);
            if neighbor {
                items[x].family = "paren-roman".into();
                items[x].ordinal = rv;
            }
        }
        let mut i = 0;
        while i < items.len() {
            let mut j = i + 1;
            while j < items.len()
                && items[j].family == items[j - 1].family
                && items[j].ordinal == items[j - 1].ordinal + 1
            {
                j += 1;
            }
            let first = &items[i];
            let lead = if first.at_line_start {
                true
            } else if let Some(boundary) = first.boundary {
                let mut q = boundary;
                while q > first.line_start && is_whitespace(chars[q - 1]) {
                    q -= 1;
                }
                q == first.line_start
                    || matches!(chars[q - 1], '.' | ':' | ';' | '!' | '?' | ')' | ']' | '}')
            } else {
                false
            };
            let qualifies = (j - i >= 3 && lead) || (j - i >= 2 && first.at_line_start);
            if qualifies {
                for item in &items[i..j] {
                    if let Some(pos) = item.boundary {
                        chars[pos] = '\n';
                    }
                }
            }
            i = j;
        }
    }
    chars.into_iter().collect()
}

fn trim_segment(text: &str, start: usize, end: usize) -> (usize, usize) {
    let mut st = start;
    let mut en = end;
    while st < en {
        let ch = text[st..en].chars().next().unwrap();
        if !is_whitespace(ch) {
            break;
        }
        st += ch.len_utf8();
    }
    while en > st {
        let ch = text[st..en].chars().next_back().unwrap();
        if !is_whitespace(ch) {
            break;
        }
        en -= ch.len_utf8();
    }
    (st, en)
}

fn make_part(text: &str, label: &str, st: usize, en: usize, cs: usize, ce: usize) -> Part {
    Part {
        label: label.to_string(),
        start: utf16_offset(text, st),
        end: utf16_offset(text, en),
        core_start: utf16_offset(text, cs),
        core_end: utf16_offset(text, ce),
        core: text[cs..ce].to_owned(),
    }
}

/// Return exact .NET ParseParts segments. Requires valid UTF-8 and bounded text.
/// Normalized offsets point to the text after CRLF and CR conversion to LF.
pub fn parse_parts(text: &str) -> std::result::Result<Result, &'static str> {
    let source = text.replace("\r\n", "\n").replace('\r', "\n");
    if length_utf16(&source) > MAX_UTF16 {
        return Err("too many UTF-16 units");
    }
    let scan = recover_boundaries(&format_view(&source));
    // Both formatting-character substitution and inline-boundary recovery
    // preserve the number of Unicode scalars but NOT UTF-8 byte lengths.
    // Map scan byte offsets through scalar indices before indexing source.
    let mut scan_bytes: Vec<usize> = scan.char_indices().map(|(i, _)| i).collect();
    scan_bytes.push(scan.len());
    let mut source_bytes: Vec<usize> = source.char_indices().map(|(i, _)| i).collect();
    source_bytes.push(source.len());
    let original_byte = |scan_byte: usize| -> usize {
        let idx = scan_bytes
            .binary_search(&scan_byte)
            .expect("regex match indices must be UTF-8 char boundaries");
        source_bytes[idx]
    };
    let matches: Vec<_> = ITEM.captures_iter(&scan).collect();
    if matches.len() > MAX_PARTS {
        return Err("too many structured parts");
    }
    if matches.is_empty() {
        let (st, en) = trim_segment(&source, 0, source.len());
        return Ok(Result {
            parts: if en > st {
                vec![make_part(&source, "본문", st, en, st, en)]
            } else {
                Vec::new()
            },
        });
    }
    let mut result = Vec::new();
    let first = matches[0].get(0).unwrap();
    if first.start() > 0 {
        let (st, en) = trim_segment(&source, 0, original_byte(first.start()));
        if en > st {
            result.push(make_part(&source, "본문", st, en, st, en));
        }
    }
    for (i, m) in matches.iter().enumerate() {
        let whole = m.get(0).unwrap();
        let label = group(m, "label");
        let seg_start = original_byte(label.start());
        let seg_end = if i + 1 < matches.len() {
            original_byte(matches[i + 1].get(0).unwrap().start())
        } else {
            source.len()
        };
        let (core_start, core_end) = trim_segment(&source, original_byte(whole.end()), seg_end);
        result.push(make_part(
            &source,
            label.as_str(),
            seg_start,
            seg_end,
            core_start,
            core_end,
        ));
    }
    Ok(Result { parts: result })
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn simple_circled() {
        let text = "① 동의 없이 처리한다.\n② 다른 목적이 있다.";
        let parsed = parse_parts(text).unwrap();
        assert_eq!(parsed.parts.len(), 2);
        assert_eq!(parsed.parts[0].label, "①");
        assert_eq!(parsed.parts[1].core, "다른 목적이 있다.");
    }
    #[test]
    fn single_reference_not_list() {
        let text = "Article 5 (1) Company policies.";
        let parsed = parse_parts(text).unwrap();
        assert_eq!(parsed.parts.len(), 1);
        assert_eq!(parsed.parts[0].label, "본문");
    }
    #[test]
    fn single_space_sequence_can_be_recovered() {
        let text = "(1) Company A (2) Company B (3) Company C";
        let parsed = parse_parts(text).unwrap();
        assert_eq!(parsed.parts.len(), 3);
    }
    #[test]
    fn normalizes_crlf_and_preserves_utf16() {
        let r = parse_parts("😀설명\r\n① 동의 없이").unwrap();
        assert_eq!(r.parts.len(), 2);
        assert_eq!(r.parts[0].end, 4);
        assert_eq!(r.parts[1].start, 5);
    }
}
