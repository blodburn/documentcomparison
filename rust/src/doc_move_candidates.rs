//! The high-similarity, changed-enumerator move-candidate selection stage
//! from NativeComparisonEngine.MatchParts. Canonical C# still determines
//! parent equivalence, part similarity, containment, child support and the
//! existing used indices. Rust applies the exact decision thresholds,
//! C# rank formula, stable ordering, and greedy 1:1 selection.
//! This is NOT the full legal-document movement/renumbering matcher.
use serde::{Deserialize, Serialize};
use std::cmp::Ordering;

const MAX_CANDIDATES: usize = 16_384;
const MAX_PARTS: usize = 256;

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Candidate {
    pub old: usize,
    pub new: usize,
    pub similarity: f64,
    pub containment: f64,
    pub child_support: usize,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Input {
    pub old_count: usize,
    pub new_count: usize,
    pub candidates: Vec<Candidate>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct Selected {
    pub old: usize,
    pub new: usize,
    pub score: f64,
}

#[derive(Debug, Serialize)]
pub struct Result {
    pub selected: Vec<Selected>,
}

pub fn select(
    old_count: usize,
    new_count: usize,
    candidates: &[Candidate],
) -> std::result::Result<Result, &'static str> {
    if old_count > MAX_PARTS || new_count > MAX_PARTS || candidates.len() > MAX_CANDIDATES {
        return Err("document move candidates exceed analysis limit");
    }
    if candidates.iter().any(|c| {
        c.old >= old_count
            || c.new >= new_count
            || !c.similarity.is_finite()
            || !c.containment.is_finite()
            || !(0.0..=1.0).contains(&c.similarity)
            || !(0.0..=1.0).contains(&c.containment)
            || c.child_support > MAX_PARTS
    }) {
        return Err("document move candidate score or index invalid");
    }
    let mut ranked: Vec<(usize, &Candidate, f64)> = candidates
        .iter()
        .enumerate()
        .filter_map(|(position, candidate)| {
            let s = candidate.similarity;
            let contain = candidate.containment;
            let child = candidate.child_support;
            if s >= 0.74 || contain >= 0.88 || child >= 2 || (child >= 1 && s >= 0.55) {
                let rank = s.max(0.94 * contain) + (0.12_f64).min(child as f64 * 0.06);
                Some((position, candidate, rank))
            } else {
                None
            }
        })
        .collect();
    ranked.sort_by(|left, right| {
        right
            .2
            .partial_cmp(&left.2)
            .unwrap_or(Ordering::Equal)
            .then_with(|| {
                left.1
                    .old
                    .abs_diff(left.1.new)
                    .cmp(&right.1.old.abs_diff(right.1.new))
            })
            .then_with(|| left.0.cmp(&right.0))
    });
    let mut old_used = vec![false; old_count];
    let mut new_used = vec![false; new_count];
    let mut selected = Vec::new();
    for (_, candidate, rank) in ranked {
        if !old_used[candidate.old] && !new_used[candidate.new] {
            old_used[candidate.old] = true;
            new_used[candidate.new] = true;
            selected.push(Selected {
                old: candidate.old,
                new: candidate.new,
                score: candidate.similarity.max(rank),
            });
        }
    }
    Ok(Result { selected })
}

#[cfg(test)]
mod tests {
    use super::*;
    fn move_candidate(old: usize, new: usize, similarity: f64) -> Candidate {
        Candidate {
            old,
            new,
            similarity,
            containment: 0.0,
            child_support: 0,
        }
    }
    #[test]
    fn modified_and_moved_item_beats_wrong_old_ordinal() {
        let candidates = vec![
            move_candidate(2, 2, 0.10),
            move_candidate(2, 3, 0.93),
            move_candidate(1, 1, 0.12),
            move_candidate(1, 2, 0.90),
        ];
        assert_eq!(
            select(3, 4, &candidates).unwrap().selected,
            vec![
                Selected {
                    old: 2,
                    new: 3,
                    score: 0.93
                },
                Selected {
                    old: 1,
                    new: 2,
                    score: 0.90
                },
            ]
        );
    }
    #[test]
    fn threshold_and_child_support_match_csharp() {
        let candidates = vec![
            move_candidate(0, 0, 0.739),
            Candidate {
                old: 0,
                new: 1,
                similarity: 0.30,
                containment: 0.88,
                child_support: 0,
            },
            Candidate {
                old: 1,
                new: 2,
                similarity: 0.10,
                containment: 0.0,
                child_support: 2,
            },
        ];
        let result = select(2, 3, &candidates).unwrap();
        assert_eq!(result.selected.len(), 2);
        assert_eq!(result.selected[0].old, 0);
        assert_eq!(result.selected[1].old, 1);
        assert!(result.selected[0].score > 0.827);
    }
    #[test]
    fn tie_uses_original_candidate_iteration_order() {
        let candidates = vec![move_candidate(0, 1, 0.8), move_candidate(1, 0, 0.8)];
        assert_eq!(select(2, 2, &candidates).unwrap().selected[0].old, 0);
    }
    #[test]
    fn rejects_invalid_scores_and_bounds() {
        assert!(select(1, 1, &[move_candidate(0, 0, f64::NAN)]).is_err());
        assert!(select(1, 1, &[move_candidate(0, 1, 0.9)]).is_err());
        assert!(select(257, 1, &[]).is_err());
    }
}
