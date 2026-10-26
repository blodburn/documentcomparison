//! Join nearby deletion + insertion events into a replacement.
//!
//! This is the exact selection contract of C#
//! NativeComparisonEngine.CoalesceLocationReplacements. Original UTF-16
//! lexical classification and CanonicalVisibleText normalization are
//! supplied to Rust as a stable lexical compatibility boundary.
use serde::{Deserialize, Serialize};
use std::collections::{HashMap, HashSet};

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Marker {
    pub index: usize,
    pub kind: String,
    pub part: String,
    pub visible: String,
    pub lexical: bool,
    pub text_length: usize,
    pub item_order: i32,
    pub old_start: i32,
    pub old_end: i32,
    pub new_start: i32,
    pub new_end: i32,
}

#[derive(Debug, Deserialize)]
pub struct Input {
    pub markers: Vec<Marker>,
}

#[derive(Debug, PartialEq, Eq, Serialize)]
pub struct Pair {
    pub deletion: usize,
    pub addition: usize,
}

#[derive(Debug, Serialize)]
pub struct Result {
    pub pairs: Vec<Pair>,
}

pub fn validate(v: &Input) -> bool {
    v.markers.len() <= 384
        && v.markers.iter().all(|m| {
            (m.kind == "delete" || m.kind == "add")
                && m.index < 4096
                && m.text_length <= 131_072
                && m.part.len() <= 512
                && m.visible.len() <= 131_072
                && [m.old_start, m.old_end, m.new_start, m.new_end]
                    .iter()
                    .all(|&x| (-1..=1_000_000).contains(&x))
        })
        && v.markers
            .iter()
            .map(|m| m.index)
            .collect::<HashSet<_>>()
            .len()
            == v.markers.len()
}

fn span_distance(pos: i32, start: i32, end: i32) -> i32 {
    let lo = start.min(end);
    let hi = start.max(end);
    if (lo..=hi).contains(&pos) {
        0
    } else {
        (pos - lo).abs().min((pos - hi).abs())
    }
}

pub fn select(v: &Input) -> Result {
    let deletes: Vec<_> = v
        .markers
        .iter()
        .filter(|m| {
            m.kind == "delete" && m.lexical && m.old_end > m.old_start && m.new_end == m.new_start
        })
        .collect();
    let adds: Vec<_> = v
        .markers
        .iter()
        .filter(|m| {
            m.kind == "add" && m.lexical && m.old_end == m.old_start && m.new_end > m.new_start
        })
        .collect();
    let mut candidates = Vec::new();
    for d in deletes {
        for a in &adds {
            if d.part != a.part || d.visible == a.visible {
                continue;
            }
            let old_dist = span_distance(a.old_start, d.old_start, d.old_end);
            let new_dist = span_distance(d.new_start, a.new_start, a.new_end);
            let tol = (0.1 * (d.text_length + a.text_length) as f64).round_ties_even() as i32;
            let tolerance = 6.max(tol.min(28));
            let same_struct = d.item_order == a.item_order;
            if (old_dist > tolerance || new_dist > tolerance)
                && (!same_struct || old_dist > tolerance * 2 || new_dist > tolerance * 2)
            {
                continue;
            }
            let score = old_dist + new_dist - if same_struct { 4 } else { 0 };
            candidates.push((score, d.index, a.index));
        }
    }
    candidates.sort_by_key(|&(score, d, a)| (score, d, a));
    let mut used_d = HashSet::new();
    let mut used_a = HashSet::new();
    let mut chosen = HashMap::new();
    for (_, d, a) in candidates {
        // Keep the original C# short-circuit mutation order: a failed
        // used_a insertion still consumes the just-added deletion index.
        if !used_d.insert(d) || !used_a.insert(a) {
            continue;
        }
        chosen.insert(d, a);
    }
    let mut pairs = chosen
        .into_iter()
        .map(|(deletion, addition)| Pair { deletion, addition })
        .collect::<Vec<_>>();
    pairs.sort_by_key(|x| x.deletion);
    Result { pairs }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn m(index: usize, kind: &str, visible: &str, old: (i32, i32), new: (i32, i32)) -> Marker {
        Marker {
            index,
            kind: kind.to_string(),
            part: "body".into(),
            visible: visible.into(),
            lexical: true,
            text_length: 8,
            item_order: 0,
            old_start: old.0,
            old_end: old.1,
            new_start: new.0,
            new_end: new.1,
        }
    }
    #[test]
    fn merges_simple_replace() {
        let v = Input {
            markers: vec![
                m(0, "delete", "old", (10, 14), (10, 10)),
                m(1, "add", "new", (10, 10), (10, 15)),
            ],
        };
        assert_eq!(
            select(&v).pairs,
            vec![Pair {
                deletion: 0,
                addition: 1
            }]
        );
    }
    #[test]
    fn refuses_semantic_equal_and_distant_edits() {
        let v = Input {
            markers: vec![
                m(0, "delete", "same", (10, 14), (10, 10)),
                m(1, "add", "same", (10, 10), (10, 15)),
                m(2, "add", "far", (100, 100), (100, 105)),
            ],
        };
        assert!(select(&v).pairs.is_empty());
    }
    #[test]
    fn matches_non_greedy_add_without_reusing_deletions() {
        let v = Input {
            markers: vec![
                m(0, "delete", "a", (10, 14), (20, 20)),
                m(1, "delete", "b", (13, 17), (20, 20)),
                m(2, "add", "c", (10, 10), (20, 25)),
            ],
        };
        let pairs = select(&v).pairs;
        assert_eq!(pairs.len(), 1);
        assert_eq!(pairs[0].addition, 2);
    }
}
