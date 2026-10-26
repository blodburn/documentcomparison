//! Final text change event-piece splitting and shared Hangul prefix clipping.
//! Both operations use original .NET UTF-16 offsets, preserving review spans.
use regex::Regex;
use serde::{Deserialize, Serialize};
use std::sync::LazyLock;
use unicode_general_category::{GeneralCategory, get_general_category};

static LEADING_PUNCT: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"(?s)^([.!?;:]+)(\s+)(.+)$").unwrap());

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Input {
    pub mode: String,
    pub text: String,
    #[serde(default)]
    pub other: String,
    #[serde(default)]
    pub start: usize,
    #[serde(default)]
    pub end: usize,
}
#[derive(Debug, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Piece {
    pub start: usize,
    pub end: usize,
    pub text: String,
}
#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Result {
    pub pieces: Vec<Piece>,
    pub old: String,
    pub new: String,
    pub old_delta: usize,
    pub new_delta: usize,
}
pub fn validate(v: &Input) -> bool {
    (v.mode == "events" || v.mode == "hangul")
        && v.text.encode_utf16().count() <= 131072
        && v.other.encode_utf16().count() <= 131072
        && v.start <= 131072
        && v.end <= 131072
}
fn whitespace(ch: char) -> bool {
    ch.is_whitespace()
}
fn alnum(ch: char) -> bool {
    ch.len_utf16() == 1
        && matches!(
            get_general_category(ch),
            GeneralCategory::UppercaseLetter
                | GeneralCategory::LowercaseLetter
                | GeneralCategory::TitlecaseLetter
                | GeneralCategory::ModifierLetter
                | GeneralCategory::OtherLetter
                | GeneralCategory::DecimalNumber
                | GeneralCategory::LetterNumber
                | GeneralCategory::OtherNumber
        )
}
fn utf16len(s: &str) -> usize {
    s.encode_utf16().count()
}
pub fn split_events(v: &Input) -> Option<Result> {
    let units: Vec<_> = v.text.encode_utf16().collect();
    let mut a = v.start.min(units.len());
    let mut b = v.end.max(a).min(units.len());
    while a < b && char::from_u32(units[a] as u32).is_some_and(whitespace) {
        a += 1;
    }
    while b > a && char::from_u32(units[b - 1] as u32).is_some_and(whitespace) {
        b -= 1;
    }
    let mut pieces = Vec::new();
    if a < b {
        let raw = String::from_utf16(&units[a..b]).ok()?;
        if raw.chars().any(alnum) {
            if let Some(m) = LEADING_PUNCT.captures(&raw) {
                let pun = m.get(1).unwrap().as_str();
                let separator = m.get(2).unwrap().as_str();
                let pun_end = a + utf16len(pun);
                let lex_start = pun_end + utf16len(separator);
                pieces.push(Piece {
                    start: a,
                    end: pun_end,
                    text: String::from_utf16(&units[a..pun_end]).ok()?,
                });
                pieces.push(Piece {
                    start: lex_start,
                    end: b,
                    text: String::from_utf16(&units[lex_start..b]).ok()?,
                });
            } else {
                pieces.push(Piece {
                    start: a,
                    end: b,
                    text: raw,
                });
            }
        } else {
            pieces.push(Piece {
                start: a,
                end: b,
                text: raw,
            });
        }
    }
    Some(Result {
        pieces,
        old: String::new(),
        new: String::new(),
        old_delta: 0,
        new_delta: 0,
    })
}
pub fn trim_hangul(v: &Input) -> Option<Result> {
    let a: Vec<_> = v.text.encode_utf16().collect();
    let b: Vec<_> = v.other.encode_utf16().collect();
    let (mut index, limit) = (0, a.len().min(b.len()));
    while index < limit && a[index] == b[index] {
        index += 1;
    }
    let original = || Result {
        pieces: vec![],
        old: v.text.clone(),
        new: v.other.clone(),
        old_delta: 0,
        new_delta: 0,
    };
    if index < 2 {
        return Some(original());
    }
    let prefix = String::from_utf16(&a[..index]).ok()?;
    if prefix.chars().any(whitespace)
        || prefix
            .chars()
            .filter(|&ch| ('가'..='힣').contains(&ch))
            .count()
            < 2
    {
        return Some(original());
    }
    let old = String::from_utf16(&a[index..]).ok()?;
    let new = String::from_utf16(&b[index..]).ok()?;
    if old.trim().is_empty() || new.trim().is_empty() {
        return Some(original());
    }
    let old_trim = old.trim_start_matches(whitespace);
    let new_trim = new.trim_start_matches(whitespace);
    Some(Result {
        pieces: vec![],
        old: old_trim.into(),
        new: new_trim.into(),
        old_delta: index + utf16len(&old[..old.len() - old_trim.len()]),
        new_delta: index + utf16len(&new[..new.len() - new_trim.len()]),
    })
}
pub fn process(v: &Input) -> Option<Result> {
    match v.mode.as_str() {
        "events" => split_events(v),
        "hangul" => trim_hangul(v),
        _ => None,
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn delimiter_and_word_split() {
        let v = Input {
            mode: "events".into(),
            text: "  .;   changed word  ".into(),
            other: String::new(),
            start: 0,
            end: 21,
        };
        let p = process(&v).unwrap().pieces;
        assert_eq!(p.len(), 2);
        assert_eq!(
            (p[0].text.as_str(), p[1].text.as_str()),
            (".;", "changed word")
        );
    }
    #[test]
    fn keep_shared_hangul_two_letters() {
        let v = Input {
            mode: "hangul".into(),
            text: "개인정보".into(),
            other: "개인계약".into(),
            start: 0,
            end: 0,
        };
        let r = process(&v).unwrap();
        assert_eq!(r.old, "정보");
        assert_eq!(r.new, "계약");
        assert_eq!(r.old_delta, 2);
        assert_eq!(r.new_delta, 2);
    }
    #[test]
    fn do_not_split_mid_surrogate() {
        let v = Input {
            mode: "events".into(),
            text: "a😀b".into(),
            other: String::new(),
            start: 2,
            end: 4,
        };
        assert!(process(&v).is_none());
    }
}
