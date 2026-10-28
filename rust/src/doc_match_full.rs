#![allow(clippy::needless_range_loop)]
//! Entire MatchParts match-choice orchestration ported from C#.
//! C# supplies normalized per-item contents, hierarchy and numeric pair
//! similarity/containment. Rust owns edge and sibling/ordinal shape decisions.
//! Full final pair lists are parity-checked against the reference C# engine.
use crate::{doc_cross_level, doc_move_candidates, doc_same_label, doc_same_ordinal};
use serde::{Deserialize, Serialize};
use std::cmp::Ordering;

const MAX_ITEMS: usize = 64;

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Item {
    pub label: String,
    pub key: String,
    pub head: String,
    pub level: usize,
    pub parent: i32,
    pub ordinal: Option<i32>,
    pub normalized: String,
    pub family: String,
}
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PairFeatures {
    pub similarity: f64,
    pub containment: f64,
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
    pub features: Vec<PairFeatures>,
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

fn ordinal_shapes(items: &[Item]) -> Vec<Option<Vec<i32>>> {
    items
        .iter()
        .map(|item| {
            let sequence: Vec<Option<i32>> = items
                .iter()
                .filter(|other| {
                    other.level == item.level
                        && other.parent == item.parent
                        && structural(other)
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

fn sibling_shapes(items: &[Item]) -> Vec<Vec<&str>> {
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

fn derive_features(input: &Input) -> Vec<DerivedFeatures> {
    let (a, b) = (&input.old, &input.new);
    let nb = b.len();
    let oa = ordinal_shapes(a);
    let ob = ordinal_shapes(b);
    let sa = sibling_shapes(a);
    let sb = sibling_shapes(b);
    input
        .features
        .iter()
        .enumerate()
        .map(|(pos, score)| {
            let (i, j) = (pos / nb, pos % nb);
            let same_level = a[i].level == b[j].level;
            DerivedFeatures {
                similarity: score.similarity,
                containment: score.containment,
                edge: stable_edge(&a[i].normalized, &b[j].normalized),
                ordinal_shape: same_level
                    && oa[i].is_some()
                    && oa[i] == ob[j],
                sibling_shape: same_level && sa[i] == sb[j],
                exact_normalized: a[i].normalized == b[j].normalized,
            }
        })
        .collect()
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
fn collapsed(i: usize, j: usize, a: &[Item], b: &[Item], matched: &[Match]) -> bool {
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
fn structural(i: &Item) -> bool {
    i.label != "본문" && !i.label.is_empty()
}

pub fn compare(input: &Input) -> Result {
    let a = &input.old;
    let b = &input.new;
    let (na, nb) = (a.len(), b.len());
    let features = derive_features(input);
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
            if used_b[j] || !structural(&b[j]) || !collapsed(i, j, a, b, &matched) {
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

pub fn checked(input: &Input) -> Result {
    // FFI validation is performed before selecting the matcher.
    compare(input)
}

pub fn validate(input: &Input) -> bool {
    let (na, nb) = (input.old.len(), input.new.len());
    if na > MAX_ITEMS || nb > MAX_ITEMS || input.features.len() != na * nb {
        return false;
    }
    input
        .old
        .iter()
        .chain(input.new.iter())
        .enumerate()
        .all(|(i, item)| {
            item.label.len() <= 256
                && item.key.len() <= 8192
                && item.head.len() <= 8192
                && item.normalized.len() <= 131_072
                && item.family.len() <= 128
                && item.level <= 4
                && item.parent >= -1
                && (if i < na {
                    item.parent < i as i32
                } else {
                    item.parent < (i - na) as i32
                })
        })
        && input.features.iter().all(|p| {
            p.similarity.is_finite()
                && p.containment.is_finite()
                && (0.0..=1.0).contains(&p.similarity)
                && (0.0..=1.0).contains(&p.containment)
        })
}

#[cfg(test)]
mod tests {
    use super::*;
    fn item(label: &str, key: &str, level: usize) -> Item {
        Item {
            label: label.into(),
            key: key.into(),
            head: String::new(),
            level,
            parent: -1,
            ordinal: None,
            normalized: key.into(),
            family: if label == "본문" { "body".into() } else { "number".into() },
        }
    }
    fn feat(score: f64) -> PairFeatures {
        PairFeatures {
            similarity: score,
            containment: score,
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
        let features = (0..3)
            .flat_map(|i| {
                (0..4).map(move |j| {
                    feat(
                        if (i == 0 && j == 0) || (i == 1 && j == 2) || (i == 2 && j == 3) {
                            1.0
                        } else {
                            0.0
                        },
                    )
                })
            })
            .collect();
        let result = compare(&Input {
            old: a,
            new: b,
            features,
        });
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
        let x = vec![
            Item { ordinal: Some(1), ..item("1.", "original-a", 1) },
            Item { ordinal: Some(2), ..item("2.", "original-b", 1) },
        ];
        let y = vec![
            Item { ordinal: Some(1), ..item("1.", "revised-a", 1) },
            Item { ordinal: Some(2), ..item("2.", "revised-b", 1) },
        ];
        let derived = derive_features(&Input {
            old: x,
            new: y,
            features: vec![feat(0.6); 4],
        });
        assert!(derived[0].ordinal_shape && derived[0].sibling_shape);
        assert!(!derived[1].exact_normalized);
        assert!(derived[1].ordinal_shape);
        assert!(derived[1].sibling_shape);
    }
    #[test]
    fn one_sided_structure_does_not_invent_pairs() {
        let a = vec![item("본문", "x", 0)];
        let b = vec![item("1.", "first", 1), item("2.", "second", 1)];
        let result = compare(&Input {
            old: a,
            new: b,
            features: vec![feat(0.1), feat(0.2)],
        });
        assert!(result.matches.is_empty());
    }
}
