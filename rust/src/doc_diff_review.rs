//! End-to-end generic Word/text change-event decision path.
//! The pre-existing specialized whitespace/sentence/Korean review modes
//! have their own Rust subengines. This function owns *all* general token
//! hunk, punctuation, Hangul trim, event split and boundary decisions.
use crate::doc_boundary::{self, Span as BoundarySpan};
use crate::doc_diff_hunks::{self, Token as DiffToken};
use crate::doc_review_pieces::{self, Input as PieceInput};
use serde::{Deserialize, Serialize};
use unicode_general_category::{GeneralCategory, get_general_category};
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Input {
    pub old_text: String,
    pub new_text: String,
    pub old_tokens: Vec<DiffToken>,
    pub new_tokens: Vec<DiffToken>,
    pub old_boundary: Vec<BoundarySpan>,
    pub new_boundary: Vec<BoundarySpan>,
    pub old_word: Vec<bool>,
    pub new_word: Vec<bool>,
    pub include_punctuation: bool,
}
#[derive(Serialize, Debug, Clone, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct Event {
    pub kind: String,
    pub old_text: String,
    pub new_text: String,
    pub old_start: usize,
    pub old_end: usize,
    pub new_start: usize,
    pub new_end: usize,
    pub hunk_order: usize,
}
#[derive(Serialize)]
pub struct Result {
    pub events: Vec<Event>,
}
pub fn validate(v: &Input) -> bool {
    let a = v.old_text.encode_utf16().count();
    let b = v.new_text.encode_utf16().count();
    let h = doc_diff_hunks::Input {
        old_length: a,
        new_length: b,
        old: v.old_tokens.clone(),
        new: v.new_tokens.clone(),
    };
    doc_diff_hunks::validate(&h)
        && a <= 2048
        && b <= 2048
        && a.saturating_mul(b) <= 1_000_000
        && v.old_word.len() == a
        && v.new_word.len() == b
        && v.old_boundary.len() <= 256
        && v.new_boundary.len() <= 256
        && v.old_boundary
            .iter()
            .all(|s| s.start <= s.end && s.end <= a)
        && v.new_boundary
            .iter()
            .all(|s| s.start <= s.end && s.end <= b)
}
fn visible_alnum(ch: char) -> bool {
    ch.len_utf16() == 1
        && matches!(
            get_general_category(ch),
            GeneralCategory::UppercaseLetter
                | GeneralCategory::LowercaseLetter
                | GeneralCategory::TitlecaseLetter
                | GeneralCategory::ModifierLetter
                | GeneralCategory::OtherLetter
                | GeneralCategory::DecimalNumber
        )
}
fn only_punctuation(s: &str) -> bool {
    !s.trim().is_empty()
        && s.chars()
            .filter(|c| !c.is_whitespace())
            .all(|c| !visible_alnum(c))
}
fn substring(input: &[u16], start: usize, end: usize) -> Option<String> {
    String::from_utf16(&input[start..end]).ok()
}
fn boundary(v: &Input, from_old: bool, pos: usize) -> usize {
    let make = |from_old: bool| {
        if from_old {
            doc_boundary::Input {
                src: v.old_text.clone(),
                dst: v.new_text.clone(),
                pos,
                src_spans: v.old_boundary.clone(),
                dst_spans: v.new_boundary.clone(),
                src_word: v.old_word.clone(),
                dst_word: v.new_word.clone(),
            }
        } else {
            doc_boundary::Input {
                src: v.new_text.clone(),
                dst: v.old_text.clone(),
                pos,
                src_spans: v.new_boundary.clone(),
                dst_spans: v.old_boundary.clone(),
                src_word: v.new_word.clone(),
                dst_word: v.old_word.clone(),
            }
        }
    };
    doc_boundary::map(&make(from_old)).mapped
}
pub fn build(v: &Input) -> Option<Result> {
    let old: Vec<u16> = v.old_text.encode_utf16().collect();
    let new: Vec<u16> = v.new_text.encode_utf16().collect();
    let request = doc_diff_hunks::Input {
        old_length: old.len(),
        new_length: new.len(),
        old: v.old_tokens.clone(),
        new: v.new_tokens.clone(),
    };
    let hunks = doc_diff_hunks::select(&request).hunks;
    let mut events = vec![];
    for h in hunks {
        let ot = substring(&old, h.old_start, h.old_end)?.trim().to_owned();
        let nt = substring(&new, h.new_start, h.new_end)?.trim().to_owned();
        if !v.include_punctuation
            && (ot.is_empty() || only_punctuation(&ot))
            && (nt.is_empty() || only_punctuation(&nt))
        {
            continue;
        }
        if !ot.is_empty() && !nt.is_empty() {
            let trimmed = doc_review_pieces::trim_hangul(&PieceInput {
                mode: "hangul".into(),
                text: ot,
                other: nt,
                start: 0,
                end: 0,
            })?;
            events.push(Event {
                kind: "change".into(),
                old_text: trimmed.old,
                new_text: trimmed.new,
                old_start: h.old_start + trimmed.old_delta,
                old_end: h.old_end,
                new_start: h.new_start + trimmed.new_delta,
                new_end: h.new_end,
                hunk_order: h.hunk_order,
            });
        } else if !ot.is_empty() {
            let pieces = doc_review_pieces::split_events(&PieceInput {
                mode: "events".into(),
                text: v.old_text.clone(),
                other: String::new(),
                start: h.old_start,
                end: h.old_end,
            })?;
            for p in pieces.pieces {
                if !v.include_punctuation && only_punctuation(&p.text) {
                    continue;
                }
                events.push(Event {
                    kind: "delete".into(),
                    old_text: p.text.trim().into(),
                    new_text: String::new(),
                    old_start: p.start,
                    old_end: p.end,
                    new_start: boundary(v, true, p.start),
                    new_end: boundary(v, true, p.start),
                    hunk_order: h.hunk_order,
                });
            }
        } else if !nt.is_empty() {
            let pieces = doc_review_pieces::split_events(&PieceInput {
                mode: "events".into(),
                text: v.new_text.clone(),
                other: String::new(),
                start: h.new_start,
                end: h.new_end,
            })?;
            for p in pieces.pieces {
                if !v.include_punctuation && only_punctuation(&p.text) {
                    continue;
                }
                let pos = boundary(v, false, p.start);
                events.push(Event {
                    kind: "insert".into(),
                    old_text: String::new(),
                    new_text: p.text.trim().into(),
                    old_start: pos,
                    old_end: pos,
                    new_start: p.start,
                    new_end: p.end,
                    hunk_order: h.hunk_order,
                });
            }
        }
    }
    Some(Result { events })
}
#[cfg(test)]
mod tests {
    use super::*;
    fn t(s: usize, e: usize, key: &str) -> DiffToken {
        DiffToken {
            start: s,
            end: e,
            key: key.into(),
        }
    }
    #[test]
    fn simple_replacement_spans() {
        let input = Input {
            old_text: "one is old".into(),
            new_text: "one is new".into(),
            old_tokens: vec![t(0, 3, "one"), t(4, 6, "is"), t(7, 10, "old")],
            new_tokens: vec![t(0, 3, "one"), t(4, 6, "is"), t(7, 10, "new")],
            old_boundary: vec![],
            new_boundary: vec![],
            old_word: vec![false; 10],
            new_word: vec![false; 10],
            include_punctuation: true,
        };
        let result = build(&input).unwrap();
        assert_eq!(result.events.len(), 1);
        assert_eq!(result.events[0].old_text, "old");
        assert_eq!(result.events[0].new_text, "new");
        assert_eq!(result.events[0].old_start, 7);
    }
}
