//! Full greedy general-block matching selection from
//! NativeComparisonEngine.MatchGenericUnits (non-article fallback).
//! C# provides the original text similarity matrix; Rust owns ranking,
//! stable ordering, one-to-one selection and resulting indices/scores.
use serde::{Deserialize, Serialize};
use std::cmp::Ordering;

const MAX_COUNT: usize = 256;
// Raw-text scoring is budgeted separately and can accept larger documents.
const MAX_RAW_COUNT: usize = 1024;
// Exact, position-preserving large documents do not need a similarity matrix.
const MAX_RAW_EXACT_COUNT: usize = 4096;
// Bound pairwise LCS work for raw blocks before building the similarity matrix.
const MAX_RAW_TOKEN_COMPARISONS: usize = 8_000_000;
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

// Complete generic block matching from original raw document bodies.
// C# does not tokenize, normalize or supply scores to this engine.
#[derive(Debug, Deserialize)]
pub struct RawInput {
    pub old: Vec<String>,
    pub new: Vec<String>,
}
pub fn validate_raw(v: &RawInput) -> bool {
    v.old.len() <= MAX_RAW_EXACT_COUNT
        && v.new.len() <= MAX_RAW_EXACT_COUNT
        && v.old
            .iter()
            .chain(&v.new)
            .all(|t| t.encode_utf16().count() <= 8192)
}
pub fn match_raw(v: &RawInput) -> Option<Result> {
    if !validate_raw(v) {
        return None;
    }
    // Each identical diagonal pair has rank 1.015, strictly greater than
    // any off-diagonal candidate (including duplicate bodies). This is
    // exactly the C# greedy selection without quadratic pair scoring.
    if v.old.len() > MAX_RAW_COUNT || v.new.len() > MAX_RAW_COUNT {
        if v.old != v.new {
            return None;
        }
        return Some(Result {
            matches: (0..v.old.len())
                .map(|i| Matched {
                    base: i,
                    other: i,
                    score: 1.0,
                    mode: "same".into(),
                })
                .collect(),
        });
    }
    use crate::doc_normalize::{Input as NormalInput, convert};
    use regex::Regex;
    use std::sync::LazyLock;
    static TOKEN: LazyLock<Regex> =
        LazyLock::new(|| Regex::new(r"[가-힣A-Za-z0-9_]+|[^\s]").unwrap());
    let prep = |s: &str| -> Option<(String, Vec<String>)> {
        let normalized = convert(&NormalInput {
            text: s.into(),
            mode: "normalize".into(),
        })
        .text;
        let tokens = TOKEN
            .find_iter(&normalized)
            .map(|m| m.as_str().to_owned())
            .collect::<Vec<_>>();
        if tokens.len() > 256 {
            return None;
        }
        Some((normalized, tokens))
    };
    let old = v.old.iter().map(|s| prep(s)).collect::<Option<Vec<_>>>()?;
    let new = v.new.iter().map(|s| prep(s)).collect::<Option<Vec<_>>>()?;
    let old_tokens: usize = old.iter().map(|(_, tokens)| tokens.len()).sum();
    let new_tokens: usize = new.iter().map(|(_, tokens)| tokens.len()).sum();
    if old_tokens.saturating_mul(new_tokens) > MAX_RAW_TOKEN_COMPARISONS {
        return None;
    }
    let score = |a: &(String, Vec<String>), b: &(String, Vec<String>)| -> f64 {
        if a.0 == b.0 {
            return 1.0;
        }
        if a.0.is_empty() || b.0.is_empty() {
            return 0.0;
        }
        let mut previous = vec![0usize; b.1.len() + 1];
        let mut current = vec![0usize; b.1.len() + 1];
        for x in &a.1 {
            for (j, y) in b.1.iter().enumerate() {
                current[j + 1] = if x == y {
                    previous[j] + 1
                } else {
                    current[j].max(previous[j + 1])
                };
            }
            std::mem::swap(&mut previous, &mut current);
        }
        2.0 * previous[b.1.len()] as f64 / (a.1.len() + b.1.len()).max(1) as f64
    };
    let similarities = old
        .iter()
        .flat_map(|a| new.iter().map(move |b| score(a, b)))
        .collect();
    Some(select(&Input {
        old_count: old.len(),
        new_count: new.len(),
        similarities,
    }))
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn source_owned_semantics_and_compatibility() {
        let case = RawInput {
            old: vec![
                "the contract".into(),
                "개인정보 동의 없이".into(),
                "Article 5".into(),
            ],
            new: vec![
                "Article 5".into(),
                "the contract".into(),
                "개인정보 동의 없이".into(),
            ],
        };
        let v = match_raw(&case).unwrap();
        assert_eq!(v.matches[0].other, 1);
        assert_eq!(v.matches[1].other, 2);
        assert_eq!(v.matches[2].other, 0);
    }
    #[test]
    fn generic_source_ignores_whitespace_like_dotnet() {
        let case = RawInput {
            old: vec!["A  B".into()],
            new: vec!["A B".into()],
        };
        let v = match_raw(&case).unwrap();
        assert_eq!(v.matches[0].score, 1.0);
    }
    #[test]
    fn source_owned_matching_handles_ninety_six_blocks() {
        let bodies = (0..96)
            .map(|i| format!("unique_clause_{i:03}"))
            .collect::<Vec<_>>();
        let result = match_raw(&RawInput {
            old: bodies.clone(),
            new: bodies,
        })
        .expect("bounded large input must use Rust");
        assert_eq!(result.matches.len(), 96);
        assert!(
            result
                .matches
                .iter()
                .enumerate()
                .all(|(i, pair)| pair.base == i && pair.other == i && pair.score == 1.0)
        );
    }
    #[test]
    fn source_owned_matching_handles_three_hundred_twenty_blocks() {
        let bodies = (0..320)
            .map(|i| format!("unique_clause_{i:03}"))
            .collect::<Vec<_>>();
        let result = match_raw(&RawInput {
            old: bodies.clone(),
            new: bodies,
        })
        .expect("320 simple units must be supported by Rust");
        assert_eq!(result.matches.len(), 320);
        assert!(
            result
                .matches
                .iter()
                .enumerate()
                .all(|(i, pair)| pair.base == i && pair.other == i && pair.score == 1.0)
        );
    }
    #[test]
    fn raw_matching_rejects_excessive_token_comparison_work() {
        let bodies = vec!["token ".repeat(256); 96];
        assert!(
            match_raw(&RawInput {
                old: bodies.clone(),
                new: bodies
            })
            .is_none()
        );
    }
    #[test]
    fn large_identical_documents_match_in_rust_without_pair_matrix() {
        let old: Vec<_> = (0..1300)
            .map(|i| format!("제{i}조 Article {i} 개인 정보"))
            .collect();
        let input = RawInput {
            old: old.clone(),
            new: old,
        };
        assert!(validate_raw(&input));
        let result = match_raw(&input).expect("large exact document must run in Rust");
        assert_eq!(result.matches.len(), 1300);
        assert!(
            result
                .matches
                .iter()
                .enumerate()
                .all(|(i, m)| m.base == i && m.other == i && m.score == 1.0)
        );
    }
    #[test]
    fn large_identical_duplicate_bodies_preserve_positional_pairs() {
        let bodies = vec!["repeated paragraph".into(); 1300];
        let input = RawInput {
            old: bodies.clone(),
            new: bodies,
        };
        let result = match_raw(&input).unwrap();
        assert!(
            result
                .matches
                .iter()
                .enumerate()
                .all(|(i, m)| m.base == i && m.other == i)
        );
    }
    #[test]
    fn large_changed_documents_still_reject_unsupported_semantic_matrix() {
        let old: Vec<_> = (0..1300).map(|i| format!("clause_{i:04}")).collect();
        let mut new = old.clone();
        new[700].push_str(" changed");
        assert!(validate_raw(&RawInput {
            old: old.clone(),
            new: new.clone(),
        }));
        assert!(match_raw(&RawInput { old, new }).is_none());
    }
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
