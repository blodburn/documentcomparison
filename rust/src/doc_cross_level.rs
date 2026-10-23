//! Cross-level item matching for one omitted or inserted 항/호 parent layer.
//!
//! The C# engine still calculates collapsed-parent eligibility, structural
//! levels, text similarity, containment, stable edge and logical ordinal.
//! Rust ports only the MatchParts crossLevel threshold, ranking and greedy
//! one-to-one selection. This is not a full document-lineage implementation.
use serde::{Deserialize, Serialize};
use std::cmp::Ordering;
const MAX_PARTS: usize = 256;
const MAX_CANDIDATES: usize = 8192;

#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Candidate {
    pub old: usize,
    pub new: usize,
    pub similarity: f64,
    pub containment: f64,
    pub same_ordinal: bool,
    pub edge: bool,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Input {
    pub old_count: usize,
    pub new_count: usize,
    pub candidates: Vec<Candidate>,
}

#[derive(Debug, Clone, PartialEq, Serialize)]
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
        return Err("cross-level candidate capacity exceeded");
    }
    if candidates.iter().any(|c| {
        c.old >= old_count
            || c.new >= new_count
            || !c.similarity.is_finite()
            || !c.containment.is_finite()
            || !(0.0..=1.0).contains(&c.similarity)
            || !(0.0..=1.0).contains(&c.containment)
    }) {
        return Err("invalid cross-level candidate");
    }
    let mut ranked: Vec<(usize, &Candidate, f64)> = candidates
        .iter()
        .enumerate()
        .filter_map(|(position, c)| {
            let accepted = (c.same_ordinal
                && (c.similarity >= 0.34 || c.containment >= 0.50 || c.edge))
                || c.similarity >= 0.74
                || c.containment >= 0.86;
            if !accepted {
                return None;
            }
            let rank =
                c.similarity.max(0.94 * c.containment) + if c.same_ordinal { 0.08 } else { 0.0 };
            Some((position, c, rank))
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
    let mut used_old = vec![false; old_count];
    let mut used_new = vec![false; new_count];
    let mut selected = Vec::new();
    for (_, c, _) in ranked {
        if !used_old[c.old] && !used_new[c.new] {
            used_old[c.old] = true;
            used_new[c.new] = true;
            selected.push(Selected {
                old: c.old,
                new: c.new,
                score: c.similarity.max(0.56),
            });
        }
    }
    Ok(Result { selected })
}

#[cfg(test)]
mod tests {
    use super::*;
    fn c(
        old: usize,
        new: usize,
        similarity: f64,
        containment: f64,
        same_ordinal: bool,
        edge: bool,
    ) -> Candidate {
        Candidate {
            old,
            new,
            similarity,
            containment,
            same_ordinal,
            edge,
        }
    }
    #[test]
    fn collapsed_parent_single_level_ordinals() {
        let result = select(
            3,
            4,
            &[
                c(1, 2, 0.36, 0.20, true, false),
                c(2, 3, 0.10, 0.52, true, false),
                c(0, 1, 0.90, 0.20, false, false),
            ],
        )
        .unwrap();
        assert_eq!(
            result
                .selected
                .iter()
                .map(|s| (s.old, s.new))
                .collect::<Vec<_>>(),
            vec![(0, 1), (2, 3), (1, 2)]
        );
    }
    #[test]
    fn ordinal_requires_evidence_unless_other_threshold_passes() {
        assert!(
            select(1, 1, &[c(0, 0, 0.339, 0.499, true, false)])
                .unwrap()
                .selected
                .is_empty()
        );
        assert_eq!(
            select(1, 1, &[c(0, 0, 0.01, 0.01, true, true)])
                .unwrap()
                .selected[0]
                .score,
            0.56
        );
        assert!(
            select(1, 1, &[c(0, 0, 0.739, 0.859, false, true)])
                .unwrap()
                .selected
                .is_empty()
        );
    }
    #[test]
    fn greedy_high_rank_wins_one_to_one() {
        let result = select(
            2,
            2,
            &[
                c(0, 0, 0.82, 0.10, false, false),
                c(1, 0, 0.93, 0.10, false, false),
                c(0, 1, 0.78, 0.10, false, false),
                c(1, 1, 0.77, 0.10, false, false),
            ],
        )
        .unwrap();
        assert_eq!(
            result
                .selected
                .iter()
                .map(|s| (s.old, s.new))
                .collect::<Vec<_>>(),
            vec![(1, 0), (0, 1)]
        );
    }
    #[test]
    fn rank_tie_prefers_nearest_then_original_candidate_order() {
        let result = select(
            2,
            2,
            &[
                c(0, 1, 0.75, 0.0, false, false),
                c(1, 1, 0.75, 0.0, false, false),
                c(0, 0, 0.75, 0.0, false, false),
            ],
        )
        .unwrap();
        assert_eq!(result.selected[0].old, 1);
        assert_eq!(result.selected[0].new, 1);
        assert_eq!(result.selected[1].old, 0);
        assert_eq!(result.selected[1].new, 0);
    }
    #[test]
    fn invalid_scores_and_out_of_bounds_rejected() {
        assert!(select(1, 1, &[c(0, 0, f64::NAN, 0.8, true, false)]).is_err());
        assert!(select(1, 1, &[c(0, 1, 0.8, 0.8, true, false)]).is_err());
        assert!(select(257, 1, &[]).is_err());
    }
}
