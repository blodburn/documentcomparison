//! MatchParts same-logical-ordinal, different-enumerator-style selection.
//! Candidate eligibility (matching ordinal, structural level, parent lineage)
//! and semantic scores are prepared by C#. Rust exactly replicates the
//! rank/threshold/greedy selection, NOT the complete legal item matcher.
use serde::{Deserialize, Serialize};
const MAX_ITEMS: usize = 256;
const MAX_CANDIDATES: usize = 16_384;

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Candidate {
    pub old: usize,
    pub new: usize,
    pub similarity: f64,
    pub containment: f64,
    pub shape: bool,
    pub edge: bool,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Input {
    pub old_count: usize,
    pub new_count: usize,
    pub candidates: Vec<Candidate>,
}

#[derive(Debug, Clone, Serialize, PartialEq)]
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

/// C# MatchParts convention-move semantics:
/// old ascending, rank by shape first, then max(similarity, 0.94*containment),
/// stable original destination ordering on exact ties. Evaluate ONLY the
/// highest ranked eligible candidate. Accepted scores use a .58 shape floor
/// or .52 non-shape floor.
pub fn select(
    old_count: usize,
    new_count: usize,
    candidates: &[Candidate],
) -> std::result::Result<Result, &'static str> {
    if old_count > MAX_ITEMS || new_count > MAX_ITEMS || candidates.len() > MAX_CANDIDATES {
        return Err("ordinal candidates exceed limit");
    }
    if candidates.iter().any(|c| {
        c.old >= old_count
            || c.new >= new_count
            || !c.similarity.is_finite()
            || !c.containment.is_finite()
            || !(0.0..=1.0).contains(&c.similarity)
            || !(0.0..=1.0).contains(&c.containment)
    }) {
        return Err("invalid ordinal candidate");
    }
    let mut used_new = vec![false; new_count];
    let mut selected = Vec::new();
    for old in 0..old_count {
        let mut best: Option<&Candidate> = None;
        for c in candidates
            .iter()
            .filter(|c| c.old == old && !used_new[c.new])
        {
            let rank = c.similarity.max(0.94 * c.containment);
            if best.is_none_or(|prev| {
                (c.shape && !prev.shape)
                    || (c.shape == prev.shape
                        && rank > prev.similarity.max(0.94 * prev.containment))
            }) {
                best = Some(c);
            }
        }
        if let Some(c) = best
            && ((c.shape && (c.similarity >= 0.20 || c.containment >= 0.35 || c.edge))
                || c.similarity >= 0.62
                || c.containment >= 0.76)
        {
            used_new[c.new] = true;
            selected.push(Selected {
                old,
                new: c.new,
                score: c.similarity.max(if c.shape { 0.58 } else { 0.52 }),
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
        shape: bool,
        edge: bool,
    ) -> Candidate {
        Candidate {
            old,
            new,
            similarity,
            containment,
            shape,
            edge,
        }
    }
    #[test]
    fn equivalent_number_conventions_with_rewrite() {
        let pairs = select(
            3,
            3,
            &[
                c(0, 0, 0.22, 0.15, true, false),
                c(1, 1, 0.0, 0.0, true, true),
                c(2, 2, 0.40, 0.20, true, false),
            ],
        )
        .unwrap();
        assert_eq!(
            pairs.selected,
            vec![
                Selected {
                    old: 0,
                    new: 0,
                    score: 0.58
                },
                Selected {
                    old: 1,
                    new: 1,
                    score: 0.58
                },
                Selected {
                    old: 2,
                    new: 2,
                    score: 0.58
                },
            ]
        );
    }
    #[test]
    fn shape_beats_similarity_even_when_top_shape_rejected() {
        let result = select(
            1,
            2,
            &[
                c(0, 0, 0.95, 0.0, false, false),
                c(0, 1, 0.0, 0.0, true, false),
            ],
        )
        .unwrap();
        // C# inspects only highest ranked candidate: no second chance.
        assert!(result.selected.is_empty());
    }
    #[test]
    fn containment_threshold_and_similarity_without_shape() {
        let result = select(
            2,
            2,
            &[
                c(0, 0, 0.1, 0.76, false, false),
                c(1, 1, 0.62, 0.1, false, false),
            ],
        )
        .unwrap();
        assert_eq!(result.selected[0].score, 0.52);
        assert_eq!(result.selected[1].score, 0.62);
    }
    #[test]
    fn stable_ties_and_destination_exclusivity() {
        let result = select(
            2,
            2,
            &[
                c(0, 0, 0.8, 0.4, false, false),
                c(0, 1, 0.8, 0.4, false, false),
                c(1, 0, 0.9, 0.0, false, false),
                c(1, 1, 0.7, 0.0, false, false),
            ],
        )
        .unwrap();
        assert_eq!(result.selected[0].new, 0);
        assert_eq!(result.selected[1].new, 1);
    }
    #[test]
    fn rejects_invalid_nonfinite_or_out_of_bounds() {
        assert!(select(1, 1, &[c(0, 0, f64::NAN, 0.0, true, false)]).is_err());
        assert!(select(1, 1, &[c(0, 1, 0.8, 0.0, true, false)]).is_err());
        assert!(select(257, 0, &[]).is_err());
    }
}
