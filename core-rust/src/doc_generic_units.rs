//! Full greedy general-block matching selection from
//! NativeComparisonEngine.MatchGenericUnits (non-article fallback).
//! C# provides the original text similarity matrix; Rust owns ranking,
//! stable ordering, one-to-one selection and resulting indices/scores.
use serde::{Deserialize, Serialize};
use std::cmp::Ordering;

const MAX_COUNT: usize = 64;
#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Input {
    pub old_count: usize,
    pub new_count: usize,
    pub similarities: Vec<f64>,
}
#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct Matched {
    pub base: usize,
    pub other: usize,
    pub score: f64,
    pub mode: String,
}
#[derive(Debug, Serialize)]
pub struct Result {
    pub matches: Vec<Matched>,
}
pub fn select(input: &Input) -> Result {
    let (na, nb) = (input.old_count, input.new_count);
    let length = na.max(nb).max(1) as f64;
    let mut candidates = Vec::new();
    for i in 0..na {
        for j in 0..nb {
            let score = input.similarities[i * nb + j];
            if score < 0.48 {
                continue;
            }
            let proximity = 1.0 - (i.abs_diff(j) as f64 / length).min(1.0);
            let rank = score + proximity * 0.015;
            candidates.push((i, j, score, rank));
        }
    }
    // Stable C# OrderByDescending ranking: initial old/new iteration order
    // breaks all exact rank ties. C# usedA.Add is intentionally evaluated
    // before usedB.Add, so failed B ownership also consumes the old index.
    candidates.sort_by(|a, b| b.3.partial_cmp(&a.3).unwrap_or(Ordering::Equal));
    let mut used_a = vec![false; na];
    let mut used_b = vec![false; nb];
    let mut matches = Vec::new();
    for (i, j, score, _) in candidates {
        if used_a[i] {
            continue;
        }
        used_a[i] = true;
        if used_b[j] {
            continue;
        }
        used_b[j] = true;
        matches.push(Matched {
            base: i,
            other: j,
            score,
            mode: "same".into(),
        });
    }
    matches.sort_by_key(|m| m.base);
    Result { matches }
}
pub fn validate(input: &Input) -> bool {
    input.old_count <= MAX_COUNT
        && input.new_count <= MAX_COUNT
        && input.similarities.len() == input.old_count * input.new_count
        && input
            .similarities
            .iter()
            .all(|s| s.is_finite() && (0.0..=1.0).contains(s))
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn greedy_prefers_semantics_over_nearest() {
        let data = Input {
            old_count: 2,
            new_count: 2,
            similarities: vec![0.55, 0.95, 0.92, 0.45],
        };
        let out = select(&data);
        assert_eq!(
            out.matches
                .iter()
                .map(|m| (m.base, m.other))
                .collect::<Vec<_>>(),
            vec![(0, 1), (1, 0)]
        );
    }
    #[test]
    fn threshold_and_unmatched_block() {
        let data = Input {
            old_count: 2,
            new_count: 2,
            similarities: vec![0.4799, 0.1, 0.2, 0.9],
        };
        let out = select(&data);
        assert_eq!(out.matches.len(), 1);
        assert_eq!(out.matches[0].base, 1);
    }
}
