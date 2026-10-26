//! Full legal article lineage features derived from original unit text.
//! No C#-calculated title/body similarity scores cross this API.
use crate::{doc_normalize, doc_unit_lineage};
use regex::Regex;
use serde::Deserialize;
use std::collections::HashSet;
use std::sync::LazyLock;
static CONCEPT: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"[A-Za-z]+|[가-힣]+").unwrap());
static WORD: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"[가-힣A-Za-z]+|\d+(?:,\d{3})*(?:\.\d+)?%?").unwrap());
static TOKEN: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"[가-힣A-Za-z0-9_]+|[^\s]").unwrap());
static NUMERIC: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"(?m)^\s*\d+\s*[.)]\s*").unwrap());
static KOREAN: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"(?m)^\s*[가-하]\s*[.)]\s*").unwrap());
#[derive(Debug, Deserialize)]
pub struct RawUnit {
    pub kind: String,
    pub number: String,
    pub title: String,
    pub header: String,
    pub body: String,
}
#[derive(Debug, Deserialize)]
pub struct Input {
    pub old: Vec<RawUnit>,
    pub new: Vec<RawUnit>,
}
pub fn validate(v: &Input) -> bool {
    v.old.len() <= 48
        && v.new.len() <= 48
        && v.old.len().saturating_mul(v.new.len()) <= 1024
        && v.old.iter().chain(&v.new).all(|u| {
            u.title.encode_utf16().count() <= 256
                && u.body.encode_utf16().count() <= 512
                && u.header.encode_utf16().count() <= 512
                && u.number.len() <= 120
                && u.kind.len() <= 32
        })
}
pub(crate) fn convert(s: &str, mode: &str) -> String {
    doc_normalize::convert(&doc_normalize::Input {
        text: s.into(),
        mode: mode.into(),
    })
    .text
}
fn score_indel(a: &str, b: &str) -> f64 {
    let aa: Vec<u16> = a.encode_utf16().collect();
    let bb: Vec<u16> = b.encode_utf16().collect();
    if aa == bb {
        return 1.0;
    }
    if aa.is_empty() || bb.is_empty() {
        return 0.0;
    }
    let mut prev = vec![0usize; bb.len() + 1];
    let mut row = vec![0usize; bb.len() + 1];
    for x in &aa {
        for (j, y) in bb.iter().enumerate() {
            row[j + 1] = if x == y {
                prev[j] + 1
            } else {
                row[j].max(prev[j + 1])
            };
        }
        std::mem::swap(&mut prev, &mut row);
    }
    2.0 * prev[bb.len()] as f64 / (aa.len() + bb.len()) as f64
}
fn wordset(s: &str) -> HashSet<String> {
    WORD.find_iter(&s.to_lowercase())
        .map(|m| m.as_str().to_owned())
        .collect()
}
fn concepts(s: &str) -> HashSet<String> {
    CONCEPT
        .find_iter(&s.to_lowercase())
        .filter_map(|m| {
            let mut w = m.as_str().to_owned();
            if matches!(
                w.as_str(),
                "the"
                    | "a"
                    | "an"
                    | "of"
                    | "for"
                    | "to"
                    | "and"
                    | "or"
                    | "etc"
                    | "etcetera"
                    | "on"
                    | "in"
                    | "regarding"
                    | "concerning"
                    | "article"
                    | "section"
                    | "clause"
            ) {
                return None;
            }
            for (prefix, canonical) in [
                ("provid", "provide"),
                ("provis", "provide"),
                ("inform", "information"),
                ("operat", "operation"),
                ("policy", "operation"),
                ("protect", "privacy"),
                ("privacy", "privacy"),
                ("person", "privacy"),
                ("obligat", "obligation"),
                ("duti", "obligation"),
                ("terminat", "termination"),
                ("cancel", "termination"),
                ("compensat", "damages"),
                ("damage", "damages"),
                ("confidenti", "confidentiality"),
                ("secret", "confidentiality"),
                ("use", "use"),
                ("usage", "use"),
                ("pay", "payment"),
                ("payment", "payment"),
                ("applic", "scope"),
                ("scope", "scope"),
                ("defin", "definition"),
                ("meaning", "definition"),
                ("amend", "amendment"),
                ("revis", "amendment"),
                ("modif", "amendment"),
                ("chang", "amendment"),
                ("company", "company"),
                ("corporat", "company"),
                ("member", "user"),
                ("user", "user"),
            ] {
                if w.starts_with(prefix) {
                    w = canonical.into();
                    break;
                }
            }
            for suffix in ["관련", "관하여", "관한", "사항", "등"] {
                if w.ends_with(suffix) {
                    w.truncate(w.len() - suffix.len());
                    break;
                }
            }
            if w.is_empty() { None } else { Some(w) }
        })
        .collect()
}
fn overlap(a: &HashSet<String>, b: &HashSet<String>) -> (usize, f64, f64) {
    let shared = a.intersection(b).count();
    let jac = shared as f64 / a.union(b).count().max(1) as f64;
    let contain = shared as f64 / a.len().min(b.len()).max(1) as f64;
    (shared, jac, contain)
}
pub(crate) fn title_sim(a: &RawUnit, b: &RawUnit) -> f64 {
    let raw = score_indel(&convert(&a.title, "lineage"), &convert(&b.title, "lineage"));
    let ca = concepts(&a.title);
    let cb = concepts(&b.title);
    if ca.is_empty() || cb.is_empty() {
        return raw;
    }
    let (_, jac, contain) = overlap(&ca, &cb);
    raw.max(jac.max(0.94 * contain))
}
pub(crate) fn body_sim(a: &RawUnit, b: &RawUnit) -> f64 {
    let x = convert(&a.body, "lineage");
    let y = convert(&b.body, "lineage");
    if !x.is_empty() && x == y {
        return 1.0;
    }
    let seq = score_indel(&x, &y);
    let aa = wordset(&a.body);
    let bb = wordset(&b.body);
    if aa.is_empty() || bb.is_empty() {
        return seq;
    }
    let (_, jac, contain) = overlap(&aa, &bb);
    seq.max((0.88 * contain).max(0.82 * jac))
}
fn structure_signature(s: &str) -> [usize; 3] {
    [
        s.chars().filter(|c| ('①'..='⑳').contains(c)).count(),
        NUMERIC.find_iter(s).count(),
        KOREAN.find_iter(s).count(),
    ]
}
fn structure_sim(a: &str, b: &str) -> f64 {
    let x = structure_signature(a);
    let y = structure_signature(b);
    let diff = x.iter().zip(y).map(|(a, b)| a.abs_diff(b)).sum::<usize>();
    let den = x.iter().sum::<usize>() + y.iter().sum::<usize>();
    (1.0 - diff as f64 / den.max(1) as f64).max(0.0)
}
pub(crate) fn lineage_sim(a: &RawUnit, b: &RawUnit, title: f64, body: f64) -> f64 {
    let st = structure_sim(&a.body, &b.body);
    let titles = !a.title.is_empty() && !b.title.is_empty();
    let mut score = if titles {
        0.54 * title + 0.40 * body + 0.06 * st
    } else {
        0.90 * body + 0.10 * st
    };
    if !a.number.is_empty() && a.number.eq_ignore_ascii_case(&b.number) {
        score += 0.015;
    }
    if title >= 0.88 || body >= 0.88 {
        score = score.max(0.84);
    }
    if title >= 0.72 && body >= 0.35 {
        score = score.max(0.74);
    }
    score.min(1.0)
}
fn generic_sim(a: &str, b: &str) -> f64 {
    let x = convert(a, "normalize");
    let y = convert(b, "normalize");
    if x == y {
        return 1.0;
    }
    if x.is_empty() || y.is_empty() {
        return 0.0;
    }
    let a = TOKEN.find_iter(&x).map(|x| x.as_str()).collect::<Vec<_>>();
    let b = TOKEN.find_iter(&y).map(|x| x.as_str()).collect::<Vec<_>>();
    let mut prev = vec![0usize; b.len() + 1];
    let mut row = vec![0usize; b.len() + 1];
    for v in &a {
        for (j, z) in b.iter().enumerate() {
            row[j + 1] = if v == z {
                prev[j] + 1
            } else {
                row[j].max(prev[j + 1])
            };
        }
        std::mem::swap(&mut prev, &mut row);
    }
    2.0 * prev[b.len()] as f64 / (a.len() + b.len()).max(1) as f64
}
pub(crate) fn low_information(s: &str) -> bool {
    matches!(
        convert(s, "lineage").as_str(),
        "general"
            | "generalprovisions"
            | "other"
            | "others"
            | "miscellaneous"
            | "miscellaneousprovisions"
            | "provisions"
            | "supplementaryprovisions"
            | "etc"
            | "기타"
            | "기타사항"
            | "일반"
            | "일반사항"
            | "총칙"
            | "부칙"
    )
}
pub fn match_raw(v: &Input) -> doc_unit_lineage::Result {
    let to_unit = |u: &RawUnit| doc_unit_lineage::Unit {
        article: u.kind == "article",
        number: u.number.clone(),
        title: u.title.clone(),
        title_key: convert(&u.title, "lineage"),
        body_key: convert(&u.body, "lineage"),
        low_information_title: low_information(&u.title),
    };
    let old = v.old.iter().map(to_unit).collect();
    let new = v.new.iter().map(to_unit).collect();
    let features = v
        .old
        .iter()
        .flat_map(|a| {
            v.new.iter().map(move |b| {
                let article = a.kind == "article" && b.kind == "article";
                let (title, body, lineage) = if article {
                    let t = title_sim(a, b);
                    let c = body_sim(a, b);
                    (t, c, lineage_sim(a, b, t, c))
                } else {
                    (0.0, 0.0, 0.0)
                };
                let (non_article_score, non_article_threshold) =
                    if !article && a.kind != "article" && b.kind != "article" && a.kind == b.kind {
                        let text = |u: &RawUnit| {
                            if u.header.is_empty() {
                                u.body.clone()
                            } else if u.body.is_empty() {
                                u.header.clone()
                            } else {
                                format!("{}\n{}", u.header, u.body)
                            }
                        };
                        let mut score = generic_sim(&text(a), &text(b));
                        let mut threshold = 0.48;
                        if a.kind == "section" {
                            if a.number.eq_ignore_ascii_case(&b.number) {
                                score = score.max(0.88);
                                threshold = 0.30;
                            } else if !a.title.is_empty() && !b.title.is_empty() {
                                score = score.max(0.70 * generic_sim(&a.title, &b.title));
                            }
                        }
                        (score, threshold)
                    } else {
                        (0.0, 2.0)
                    };
                doc_unit_lineage::Feature {
                    title,
                    body,
                    lineage,
                    non_article_score,
                    non_article_threshold,
                }
            })
        })
        .collect();
    doc_unit_lineage::match_units(&doc_unit_lineage::Input { old, new, features })
}
#[cfg(test)]
mod tests {
    use super::*;
    fn u(n: &str, t: &str, b: &str) -> RawUnit {
        RawUnit {
            kind: "article".into(),
            number: n.into(),
            title: t.into(),
            header: format!("Article {n} ({t})"),
            body: b.into(),
        }
    }
    #[test]
    fn direct_raw_lineage_follows_moved_article() {
        let v = Input {
            old: vec![
                u("8", "Privacy", "Personal data handling."),
                u("7", "Payments", "Payments shall be processed."),
            ],
            new: vec![
                u("7", "Privacy", "Personal data handling."),
                u("8", "Payments", "Payments shall be processed."),
            ],
        };
        let result = match_raw(&v);
        assert_eq!(result.matches.len(), 2);
        assert_eq!(result.matches[0].other, 0);
        assert_eq!(result.matches[1].other, 1);
    }
}
