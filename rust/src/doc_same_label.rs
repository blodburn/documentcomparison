//! NativeComparisonEngine.MatchParts same-label/same-level sibling-shape
//! fallback, applied AFTER moved candidates and ordinal-notation matching.
//! C# supplies eligible parents, same-label candidates, text similarity,
//! sibling-shape equivalence and unchanged edge context. No independent
//! Word article/row/cell or semantic-text inference occurs here.
use serde::{Deserialize, Serialize};

const MAX_PARTS: usize = 256;
const MAX_CANDIDATES: usize = 8192;

#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Candidate {
    pub old: usize,
    pub new: usize,
    pub similarity: f64,
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

/// C# semantics: traverse old items ascending; take the highest-ranked
/// *available* candidate by Shape DESC, Similarity DESC, original index
/// order as tie-break. If its shape/similarity/edge threshold fails, do
/// not try the runner-up. Matching removes the selected new item.
pub fn select(
    old_count: usize,
    new_count: usize,
    candidates: &[Candidate],
) -> std::result::Result<Result, &'static str> {
    if old_count > MAX_PARTS || new_count > MAX_PARTS || candidates.len() > MAX_CANDIDATES {
        return Err("same-label candidate capacity exceeded");
    }
    if candidates.iter().any(|candidate| {
        candidate.old >= old_count
            || candidate.new >= new_count
            || !candidate.similarity.is_finite()
            || !(0.0..=1.0).contains(&candidate.similarity)
    }) {
        return Err("invalid same-label candidate");
    }
    let mut used_new = vec![false; new_count];
    let mut selected = Vec::new();
    for old in 0..old_count {
        let mut best: Option<&Candidate> = None;
        for candidate in candidates
            .iter()
            .filter(|c| c.old == old && !used_new[c.new])
        {
            if best.is_none_or(|prev| {
                (candidate.shape && !prev.shape)
                    || (candidate.shape == prev.shape && candidate.similarity > prev.similarity)
            }) {
                best = Some(candidate);
            }
        }
        if let Some(candidate) = best
            && (candidate.shape || candidate.similarity >= 0.18 || candidate.edge)
        {
            used_new[candidate.new] = true;
            selected.push(Selected {
                old,
                new: candidate.new,
                score: candidate.similarity.max(0.52),
            });
        }
    }
    Ok(Result { selected })
}

#[cfg(test)]
mod tests {
    use super::*;
    fn c(old: usize, new: usize, similarity: f64, shape: bool, edge: bool) -> Candidate {
        Candidate {
            old,
            new,
            similarity,
            shape,
            edge,
        }
    }
    #[test]
    fn same_labels_keep_structure_even_after_total_rewrite() {
        let result = select(
            3,
            3,
            &[
                c(0, 0, 0.0, true, false),
                c(1, 1, 0.0, true, false),
                c(2, 2, 0.0, true, false),
            ],
        )
        .unwrap();
        assert_eq!(
            result.selected,
            vec![
                Selected {
                    old: 0,
                    new: 0,
                    score: 0.52
                },
                Selected {
                    old: 1,
                    new: 1,
                    score: 0.52
                },
                Selected {
                    old: 2,
                    new: 2,
                    score: 0.52
                },
            ]
        );
    }
    #[test]
    fn sibling_shape_beats_higher_text_similarity() {
        let result = select(
            1,
            2,
            &[c(0, 0, 0.98, false, false), c(0, 1, 0.04, true, false)],
        )
        .unwrap();
        assert_eq!(
            result.selected,
            vec![Selected {
                old: 0,
                new: 1,
                score: 0.52
            }]
        );
    }
    #[test]
    fn stable_tie_and_used_destination_are_greedy() {
        let result = select(
            2,
            2,
            &[
                c(0, 0, 0.3, false, false),
                c(0, 1, 0.3, false, false),
                c(1, 0, 0.95, false, false),
                c(1, 1, 0.4, false, false),
            ],
        )
        .unwrap();
        assert_eq!(result.selected[0].new, 0);
        assert_eq!(result.selected[1].new, 1);
    }
    #[test]
    fn no_second_chance_when_best_candidate_fails_threshold() {
        let result = select(
            1,
            2,
            &[c(0, 0, 0.15, false, false), c(0, 1, 0.12, false, true)],
        )
        .unwrap();
        assert!(result.selected.is_empty());
    }
    #[test]
    fn stable_edge_alone_can_pair() {
        let result = select(1, 1, &[c(0, 0, 0.0, false, true)]).unwrap();
        assert_eq!(result.selected[0].score, 0.52);
    }
    #[test]
    fn invalid_input_is_rejected() {
        assert!(select(1, 1, &[c(0, 1, 0.5, true, false)]).is_err());
        assert!(select(1, 1, &[c(0, 0, f64::NAN, true, false)]).is_err());
        assert!(select(257, 1, &[]).is_err());
    }
}
