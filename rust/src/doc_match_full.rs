#![allow(clippy::needless_range_loop)]
//! Entire MatchParts match-choice orchestration ported from C#.
//! C# supplies raw item labels and text. Rust owns hierarchy, ordinals,
//! normalization, word sets, similarity scores and matching decisions.
//! Full final pair lists are parity-checked against the reference C# engine.
use crate::{
    doc_character_score, doc_cross_level, doc_match_exact, doc_move_candidates, doc_normalize,
    doc_parts, doc_same_label, doc_same_ordinal, doc_unit_lineage_raw,
};
use regex::Regex;
use serde::{Deserialize, Serialize};
use std::{cmp::Ordering, collections::HashSet, sync::LazyLock};

const MAX_ITEMS: usize = 128;
const MAX_EXACT_ITEMS: usize = 1_024;
const MAX_TOTAL_UTF16_UNITS: usize = 524_288;
const MAX_PAIRS: usize = 8_192;

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Item {
    pub label: String,
    pub core: String,
}

#[derive(Debug, Clone)]
struct PreparedItem {
    label: String,
    key: String,
    head: String,
    level: usize,
    parent: i32,
    ordinal: Option<i32>,
    normalized: String,
    family: String,
    has_trimmed_content: bool,
    word_tokens: Vec<String>,
}

static QUOTED_HEAD: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r#"^\s*["“‘]([^"”’]{1,96})["”’]"#).unwrap());

fn prepare(item: &Item, hierarchy: &doc_parts::HierarchyNode) -> PreparedItem {
    let normalized = doc_normalize::convert(&doc_normalize::Input {
        text: item.core.clone(),
        mode: "normalize".into(),
    })
    .text;
    let key = doc_normalize::convert(&doc_normalize::Input {
        text: item.core.clone(),
        mode: "lineage".into(),
    })
    .text;
    let head = QUOTED_HEAD
        .captures(&item.core)
        .and_then(|c| c.get(1))
        .filter(|m| m.as_str().encode_utf16().count() <= 96)
        .map(|m| {
            doc_normalize::convert(&doc_normalize::Input {
                text: m.as_str().into(),
                mode: "normalize".into(),
            })
            .text
        })
        .unwrap_or_default();
    let mut tokens: Vec<_> = doc_unit_lineage_raw::wordset(&item.core)
        .into_iter()
        .collect();
    tokens.sort();
    PreparedItem {
        label: item.label.clone(),
        key,
        head,
        level: hierarchy.level,
        parent: hierarchy.parent,
        ordinal: hierarchy.ordinal.map(|v| v as i32),
        normalized,
        family: hierarchy.family.clone(),
        has_trimmed_content: !item.core.trim().is_empty(),
        word_tokens: tokens,
    }
}
#[derive(Debug, Clone)]
struct DerivedFeatures {
    similarity: f64,
    containment: f64,
    edge: bool,
    ordinal_shape: bool,
    sibling_shape: bool,
    exact_normalized: bool,
}
#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Input {
    pub old: Vec<Item>,
    pub new: Vec<Item>,
}
#[derive(Debug, Clone, Serialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct Match {
    pub old: usize,
    pub new: usize,
    pub score: f64,
}
#[derive(Debug, Serialize)]
pub struct Result {
    pub matches: Vec<Match>,
}

// The managed edge rule examines UTF-16 units, not Unicode scalar values.
fn stable_edge(a: &str, b: &str) -> bool {
    let x: Vec<u16> = a.encode_utf16().collect();
    let y: Vec<u16> = b.encode_utf16().collect();
    let min = x.len().min(y.len());
    if min < 40 {
        return false;
    }
    let mut prefix = 0;
    while prefix < min && x[prefix] == y[prefix] {
        prefix += 1;
    }
    let mut suffix = 0;
    while suffix < min - prefix && x[x.len() - 1 - suffix] == y[y.len() - 1 - suffix] {
        suffix += 1;
    }
    prefix >= 18 || suffix >= 18
}

fn prepare_all(items: &[Item]) -> Vec<PreparedItem> {
    let labels: Vec<String> = items.iter().map(|item| item.label.clone()).collect();
    let hierarchy = doc_parts::hierarchy(&labels)
        .expect("hierarchy has been checked by full matcher input validation");
    items
        .iter()
        .zip(hierarchy.nodes.iter())
        .map(|(item, node)| prepare(item, node))
        .collect()
}

fn ordinal_shapes(items: &[PreparedItem]) -> Vec<Option<Vec<i32>>> {
    items
        .iter()
        .map(|item| {
            let sequence: Vec<Option<i32>> = items
                .iter()
                .filter(|other| {
                    other.level == item.level && other.parent == item.parent && structural(other)
                })
                .map(|other| other.ordinal)
                .collect();
            if sequence.len() < 2 {
                return None;
            }
            sequence.into_iter().collect::<Option<Vec<i32>>>()
        })
        .collect()
}

fn sibling_shapes(items: &[PreparedItem]) -> Vec<Vec<&str>> {
    items
        .iter()
        .map(|item| {
            items
                .iter()
                .filter(|other| {
                    other.level == item.level
                        && other.parent == item.parent
                        && other.family == item.family
                })
                .map(|other| other.label.as_str())
                .collect()
        })
        .collect()
}

fn derive_features(a: &[PreparedItem], b: &[PreparedItem]) -> Vec<DerivedFeatures> {
    let oa = ordinal_shapes(a);
    let ob = ordinal_shapes(b);
    let sa = sibling_shapes(a);
    let sb = sibling_shapes(b);
    let aw: Vec<HashSet<&str>> = a
        .iter()
        .map(|item| item.word_tokens.iter().map(String::as_str).collect())
        .collect();
    let bw: Vec<HashSet<&str>> = b
        .iter()
        .map(|item| item.word_tokens.iter().map(String::as_str).collect())
        .collect();
    let mut features = Vec::with_capacity(a.len() * b.len());
    for i in 0..a.len() {
        for j in 0..b.len() {
            let shared = aw[i].intersection(&bw[j]).count();
            let smaller = aw[i].len().min(bw[j].len());
            let containment = if smaller == 0 {
                0.0
            } else {
                shared as f64 / smaller as f64
            };
            let similarity = if !a[i].has_trimmed_content || !b[j].has_trimmed_content {
                0.0
            } else if a[i].key == b[j].key {
                1.0
            } else {
                let sequence = doc_character_score::compare(&doc_character_score::Input {
                    old: a[i].key.clone(),
                    new: b[j].key.clone(),
                    mode: "sequence".into(),
                    old_tokens: vec![],
                    new_tokens: vec![],
                })
                .score;
                let union = aw[i].union(&bw[j]).count().max(1);
                let jaccard = shared as f64 / union as f64;
                let mut score = (0.68 * sequence + 0.32 * jaccard).max(0.90 * containment);
                if a[i].label == b[j].label && a[i].label != "본문" {
                    score = (score + 0.08).min(1.0);
                }
                score
            };
            let same_level = a[i].level == b[j].level;
            features.push(DerivedFeatures {
                similarity,
                containment,
                edge: stable_edge(&a[i].normalized, &b[j].normalized),
                ordinal_shape: same_level && oa[i].is_some() && oa[i] == ob[j],
                sibling_shape: same_level && sa[i] == sb[j],
                exact_normalized: a[i].normalized == b[j].normalized,
            });
        }
    }
    features
}

fn parents(a: i32, b: i32, matches: &[Match]) -> bool {
    if a < 0 || b < 0 {
        a == b
    } else {
        matches
            .iter()
            .any(|m| m.old == a as usize && m.new == b as usize)
    }
}
fn collapsed(
    i: usize,
    j: usize,
    a: &[PreparedItem],
    b: &[PreparedItem],
    matched: &[Match],
) -> bool {
    if a[i].level.abs_diff(b[j].level) != 1 {
        return false;
    }
    if a[i].level > b[j].level {
        let p = a[i].parent;
        p >= 0
            && !matched.iter().any(|m| m.old == p as usize)
            && parents(a[p as usize].parent, b[j].parent, matched)
    } else {
        let p = b[j].parent;
        p >= 0
            && !matched.iter().any(|m| m.new == p as usize)
            && parents(a[i].parent, b[p as usize].parent, matched)
    }
}
fn structural(i: &PreparedItem) -> bool {
    i.label != "본문" && !i.label.is_empty()
}

pub fn compare(input: &Input) -> Result {
    let a = prepare_all(&input.old);
    let b = prepare_all(&input.new);
    let (na, nb) = (a.len(), b.len());
    let features = derive_features(&a, &b);
    let f = |i: usize, j: usize| &features[i * nb + j];
    let structured_a = a.iter().filter(|x| structural(x)).count();
    let structured_b = b.iter().filter(|x| structural(x)).count();
    if (structured_a >= 2 && structured_b == 0) || (structured_b >= 2 && structured_a == 0) {
        let old: Vec<_> = (0..na).filter(|&i| a[i].label == "본문").collect();
        let new: Vec<_> = (0..nb).filter(|&i| b[i].label == "본문").collect();
        if old.len() == 1 && new.len() == 1 {
            let (i, j) = (old[0], new[0]);
            let score = f(i, j).similarity;
            if score >= 0.32 || f(i, j).exact_normalized {
                return Result {
                    matches: vec![Match {
                        old: i,
                        new: j,
                        score: score.max(0.50),
                    }],
                };
            }
        }
        return Result { matches: vec![] };
    }
    let mut matched: Vec<Match> = vec![];
    let mut used_a = vec![false; na];
    let mut used_b = vec![false; nb];
    macro_rules! take {
        ($i:expr,$j:expr,$score:expr) => {{
            let i: usize = $i;
            let j: usize = $j;
            if !used_a[i] && !used_b[j] {
                used_a[i] = true;
                used_b[j] = true;
                matched.push(Match {
                    old: i,
                    new: j,
                    score: $score,
                });
            }
        }};
    }

    // Exact normalized body: ascending old, nearest unused destination.
    for i in 0..na {
        if a[i].key.is_empty() {
            continue;
        }
        let cand = (0..nb)
            .filter(|&j| !used_b[j] && a[i].key == b[j].key)
            .min_by_key(|&j| i.abs_diff(j));
        if let Some(j) = cand {
            take!(i, j, 1.0);
        }
    }
    // Definition head pairing: score desc, index distance asc.
    for i in 0..na {
        if used_a[i] || a[i].head.is_empty() {
            continue;
        }
        let cand = (0..nb)
            .filter(|&j| !used_b[j] && a[i].head == b[j].head)
            .max_by(|&j, &k| {
                f(i, j)
                    .similarity
                    .total_cmp(&f(i, k).similarity)
                    .then_with(|| i.abs_diff(k).cmp(&i.abs_diff(j)))
                    .then_with(|| k.cmp(&j))
            });
        if let Some(j) = cand
            && (j != 0 || f(i, j).similarity != 0.0)
        {
            take!(i, j, f(i, j).similarity.max(0.9));
        }
    }
    // Same-level structural pass, parent-first at levels 1 through 4.
    for level in 1..=4 {
        let mut moves = vec![];
        for i in 0..na {
            if used_a[i] || a[i].level != level || !structural(&a[i]) {
                continue;
            }
            for j in 0..nb {
                if used_b[j]
                    || b[j].level != level
                    || !structural(&b[j])
                    || a[i].label == b[j].label
                    || !parents(a[i].parent, b[j].parent, &matched)
                {
                    continue;
                }
                let children = matched
                    .iter()
                    .filter(|m| a[m.old].parent == i as i32 && b[m.new].parent == j as i32)
                    .count();
                let p = f(i, j);
                moves.push(doc_move_candidates::Candidate {
                    old: i,
                    new: j,
                    similarity: p.similarity,
                    containment: p.containment,
                    child_support: children,
                });
            }
        }
        if let Ok(selection) = doc_move_candidates::select(na, nb, &moves) {
            for item in selection.selected {
                take!(item.old, item.new, item.score);
            }
        }

        let mut ordinals = vec![];
        for i in 0..na {
            if used_a[i] || a[i].level != level || !structural(&a[i]) {
                continue;
            }
            let Some(o) = a[i].ordinal else {
                continue;
            };
            for j in 0..nb {
                if used_b[j]
                    || b[j].level != level
                    || !structural(&b[j])
                    || b[j].ordinal != Some(o)
                    || a[i].label == b[j].label
                    || !parents(a[i].parent, b[j].parent, &matched)
                {
                    continue;
                }
                let p = f(i, j);
                ordinals.push(doc_same_ordinal::Candidate {
                    old: i,
                    new: j,
                    similarity: p.similarity,
                    containment: p.containment,
                    shape: p.ordinal_shape,
                    edge: p.edge,
                });
            }
        }
        if let Ok(selection) = doc_same_ordinal::select(na, nb, &ordinals) {
            for item in selection.selected {
                take!(item.old, item.new, item.score);
            }
        }

        let mut labels = vec![];
        for i in 0..na {
            if used_a[i] || a[i].level != level || !structural(&a[i]) {
                continue;
            }
            for j in 0..nb {
                if used_b[j]
                    || b[j].level != level
                    || b[j].label != a[i].label
                    || !parents(a[i].parent, b[j].parent, &matched)
                {
                    continue;
                }
                let p = f(i, j);
                labels.push(doc_same_label::Candidate {
                    old: i,
                    new: j,
                    similarity: p.similarity,
                    shape: p.sibling_shape,
                    edge: p.edge,
                });
            }
        }
        if let Ok(selection) = doc_same_label::select(na, nb, &labels) {
            for item in selection.selected {
                take!(item.old, item.new, item.score);
            }
        }
    }

    // One-level collapsed/expanded parent matching.
    let mut cross = vec![];
    for i in 0..na {
        if used_a[i] || !structural(&a[i]) {
            continue;
        }
        for j in 0..nb {
            if used_b[j] || !structural(&b[j]) || !collapsed(i, j, &a, &b, &matched) {
                continue;
            }
            let p = f(i, j);
            cross.push(doc_cross_level::Candidate {
                old: i,
                new: j,
                similarity: p.similarity,
                containment: p.containment,
                same_ordinal: a[i].ordinal.is_some() && a[i].ordinal == b[j].ordinal,
                edge: p.edge,
            });
        }
    }
    if let Ok(selection) = doc_cross_level::select(na, nb, &cross) {
        for item in selection.selected {
            take!(item.old, item.new, item.score);
        }
    }

    // Remaining body-only pairs, first max score.
    for i in 0..na {
        if used_a[i] || a[i].label != "본문" {
            continue;
        }
        let cand = (0..nb)
            .filter(|&j| !used_b[j] && b[j].label == "본문")
            .max_by(|&j, &k| {
                f(i, j)
                    .similarity
                    .total_cmp(&f(i, k).similarity)
                    .then_with(|| k.cmp(&j))
            });
        if let Some(j) = cand
            && f(i, j).similarity >= 0.20
        {
            take!(i, j, f(i, j).similarity.max(0.50));
        }
    }

    // Residual pairing, with the original C# parent-aware threshold.
    let mut residual = vec![];
    for i in 0..na {
        if used_a[i] {
            continue;
        }
        for j in 0..nb {
            if used_b[j] {
                continue;
            }
            if !a[i].head.is_empty() && !b[j].head.is_empty() && a[i].head != b[j].head {
                continue;
            }
            let p = f(i, j);
            let parent_ok = parents(a[i].parent, b[j].parent, &matched);
            let threshold = if parent_ok { 0.68 } else { 0.82 };
            if p.similarity >= threshold || (!parent_ok && p.containment >= 0.88) {
                residual.push((i, j, p.similarity.max(0.94 * p.containment), p.similarity));
            }
        }
    }
    residual.sort_by(|x, y| {
        y.2.partial_cmp(&x.2)
            .unwrap_or(Ordering::Equal)
            .then_with(|| x.0.abs_diff(x.1).cmp(&y.0.abs_diff(y.1)))
    });
    for (i, j, rank, score) in residual {
        take!(i, j, score.max(rank));
    }
    matched.sort_by_key(|m| m.old);
    Result { matches: matched }
}

fn exact_full(input: &Input) -> Option<Result> {
    let (a, b) = (&input.old, &input.new);
    if a.is_empty() || a.len() != b.len() || a.len() > MAX_EXACT_ITEMS {
        return None;
    }
    // Preserve the canonical C# protection against one-sided list structure.
    let structured_a = a
        .iter()
        .filter(|p| p.label != "본문" && !p.label.is_empty())
        .count();
    let structured_b = b
        .iter()
        .filter(|p| p.label != "본문" && !p.label.is_empty())
        .count();
    if (structured_a >= 2 && structured_b == 0) || (structured_b >= 2 && structured_a == 0) {
        return None;
    }
    let keys = |items: &[Item]| -> Vec<String> {
        items
            .iter()
            .map(|p| {
                doc_normalize::convert(&doc_normalize::Input {
                    text: p.core.clone(),
                    mode: "lineage".into(),
                })
                .text
            })
            .collect()
    };
    let old_keys = keys(a);
    let new_keys = keys(b);
    if old_keys.iter().chain(&new_keys).any(String::is_empty) {
        return None;
    }
    let pairs = doc_match_exact::pair(&old_keys, &new_keys).ok()?.pairs;
    if pairs.len() != a.len() {
        return None;
    }
    Some(Result {
        matches: pairs
            .into_iter()
            .map(|p| Match {
                old: p.old,
                new: p.new,
                score: 1.0,
            })
            .collect(),
    })
}

pub fn checked(input: &Input) -> Result {
    // FFI validation is performed before selecting the matcher.
    exact_full(input).unwrap_or_else(|| compare(input))
}

pub fn validate(input: &Input) -> bool {
    let (na, nb) = (input.old.len(), input.new.len());
    if na > MAX_EXACT_ITEMS
        || nb > MAX_EXACT_ITEMS
        || input
            .old
            .iter()
            .chain(input.new.iter())
            .map(|p| p.core.encode_utf16().count())
            .sum::<usize>()
            > MAX_TOTAL_UTF16_UNITS
        || !input.old.iter().chain(input.new.iter()).all(|p| {
            p.label.encode_utf16().count() <= 128 && p.core.encode_utf16().count() <= 16_384
        })
    {
        return false;
    }
    if exact_full(input).is_some() {
        return true;
    }
    if na > MAX_ITEMS || nb > MAX_ITEMS || na.saturating_mul(nb) > MAX_PAIRS {
        return false;
    }
    let work_old: usize = input
        .old
        .iter()
        .map(|p| p.core.encode_utf16().count())
        .sum();
    let work_new: usize = input
        .new
        .iter()
        .map(|p| p.core.encode_utf16().count())
        .sum();
    work_old.saturating_mul(work_new) <= 32_000_000
        && input
            .old
            .iter()
            .chain(input.new.iter())
            .all(|p| p.core.encode_utf16().count() <= 8192)
}

#[cfg(test)]
mod tests {
    use super::*;
    fn item(label: &str, key: &str, _level: usize) -> Item {
        Item {
            label: label.into(),
            core: key.into(),
        }
    }
    #[test]
    fn exact_insert_moves_existing_ordinals() {
        let a = vec![
            item("1.", "one", 1),
            item("2.", "two", 1),
            item("3.", "three", 1),
        ];
        let b = vec![
            item("1.", "one", 1),
            item("2.", "new", 1),
            item("3.", "two", 1),
            item("4.", "three", 1),
        ];
        let result = compare(&Input { old: a, new: b });
        assert_eq!(
            result
                .matches
                .iter()
                .map(|m| (m.old, m.new))
                .collect::<Vec<_>>(),
            vec![(0, 0), (1, 2), (2, 3)]
        );
    }
    #[test]
    fn derives_context_and_structure_in_rust() {
        let prefix = format!("{}old", "개인정보보호".repeat(8));
        let other = format!("{}new", "개인정보보호".repeat(8));
        assert!(stable_edge(&prefix, &other));
        assert!(!stable_edge("짧음", "짧음"));
        let x = [item("1.", "original-a", 1), item("2.", "original-b", 1)];
        let y = [item("1.", "revised-a", 1), item("2.", "revised-b", 1)];
        let old = prepare_all(&x);
        let new = prepare_all(&y);
        let derived = derive_features(&old, &new);
        assert!(derived[0].ordinal_shape && derived[0].sibling_shape);
        assert!(!derived[1].exact_normalized);
        assert!(derived[1].ordinal_shape);
        assert!(derived[1].sibling_shape);
    }
    #[test]
    fn raw_cores_are_preprocessed_into_native_match_features() {
        let x = item("①", "“Personal Data”\u{200b} 이용 동의 없이 1,000원", 1);
        let y = prepare_all(&[x]);
        let y = &y[0];
        assert_eq!(y.head, "personal data");
        assert!(y.key.contains("이용동의없이"));
        assert!(!y.key.contains("\u{200b}"));
        assert!(y.word_tokens.contains(&"personal".into()));
        assert!(y.word_tokens.contains(&"1,000".into()));
        assert!(y.has_trimmed_content);
    }
    #[test]
    fn supports_exact_reorder_of_two_hundred_parts() {
        let old = (0..200)
            .map(|i| item("1.", &format!("unique_content_{i:03}"), 1))
            .collect::<Vec<_>>();
        let new = old.iter().cloned().rev().collect::<Vec<_>>();
        let request = Input { old, new };
        assert!(validate(&request));
        let result = checked(&request);
        assert_eq!(result.matches.len(), 200);
        assert!(
            result
                .matches
                .iter()
                .enumerate()
                .all(|(i, m)| { m.old == i && m.new == 199 - i && m.score == 1.0 })
        );
    }
    #[test]
    fn full_exact_match_handles_512_reordered_parts() {
        let old: Vec<_> = (0..512)
            .map(|i| item("1.", &format!("distinct clause content {i:04}"), 1))
            .collect();
        let new = old.iter().cloned().rev().collect();
        let request = Input { old, new };
        assert!(validate(&request));
        let matches = checked(&request).matches;
        assert_eq!(matches.len(), 512);
        assert!(matches
            .iter()
            .enumerate()
            .all(|(i, m)| m.old == i && m.new == 511 - i && m.score == 1.0));
    }
    #[test]
    fn validates_large_but_bounded_nonidentical_documents() {
        let request = Input {
            old: (0..96)
                .map(|i| item("1.", &format!("old_clause_{i:03}"), 1))
                .collect(),
            new: (0..80)
                .map(|i| item("1.", &format!("new_clause_{i:03}"), 1))
                .collect(),
        };
        assert!(validate(&request));
        let above_pair_budget = Input {
            old: (0..96)
                .map(|i| item("1.", &format!("old_clause_{i:03}"), 1))
                .collect(),
            new: (0..96)
                .map(|i| item("1.", &format!("new_clause_{i:03}"), 1))
                .collect(),
        };
        assert!(!validate(&above_pair_budget));
    }
    #[test]
    fn one_sided_structure_does_not_invent_pairs() {
        let a = vec![item("본문", "x", 0)];
        let b = vec![item("1.", "first", 1), item("2.", "second", 1)];
        let result = compare(&Input { old: a, new: b });
        assert!(result.matches.is_empty());
    }
}
