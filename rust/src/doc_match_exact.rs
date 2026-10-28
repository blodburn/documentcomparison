//! Exact-key matching used by the standalone first-pass API and the
//! Rust-owned full MatchParts fast path. Keys use canonical lineage rules.
//! Duplicate keys choose the nearest unmatched destination, breaking
//! equal-distance ties by the lowest new index.
use serde::{Deserialize, Serialize};
use std::collections::{BTreeSet, HashMap};

const MAX_PARTS: usize = 1_024;
const MAX_KEY_LENGTH: usize = 16_384;
const MAX_TOTAL_CHARS: usize = 2_097_152;

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Input {
    pub old_keys: Vec<String>,
    pub new_keys: Vec<String>,
}

#[derive(Debug, Serialize, Clone, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct Pair {
    pub old: usize,
    pub new: usize,
}

#[derive(Debug, Serialize)]
pub struct Result {
    pub pairs: Vec<Pair>,
}

pub fn pair(old: &[String], new: &[String]) -> std::result::Result<Result, &'static str> {
    if old.len() > MAX_PARTS
        || new.len() > MAX_PARTS
        || old
            .iter()
            .chain(new.iter())
            .any(|x| x.encode_utf16().count() > MAX_KEY_LENGTH)
        || old.iter().chain(new.iter()).map(|x| x.len()).sum::<usize>() > MAX_TOTAL_CHARS
    {
        return Err("too many document part keys");
    }
    // Per-key ordered positions replace repeated full scans while
    // preserving canonical C# nearest-unmatched, lower-index tie behavior.
    let mut destinations: HashMap<&str, BTreeSet<usize>> = HashMap::new();
    for (j, key) in new.iter().enumerate() {
        if !key.is_empty() {
            destinations.entry(key.as_str()).or_default().insert(j);
        }
    }
    let mut pairs = Vec::with_capacity(old.len().min(new.len()));
    for (i, old_key) in old.iter().enumerate() {
        if old_key.is_empty() {
            continue;
        }
        let Some(positions) = destinations.get_mut(old_key.as_str()) else {
            continue;
        };
        let previous = positions.range(..=i).next_back().copied();
        let next = positions.range(i..).next().copied();
        let selected = match (previous, next) {
            (Some(left), Some(right)) if i.abs_diff(left) <= i.abs_diff(right) => Some(left),
            (Some(_), Some(right)) => Some(right),
            (Some(left), None) => Some(left),
            (None, Some(right)) => Some(right),
            (None, None) => None,
        };
        if let Some(j) = selected {
            positions.remove(&j);
            pairs.push(Pair { old: i, new: j });
        }
    }
    Ok(Result { pairs })
}

#[cfg(test)]
mod tests {
    use super::*;

    fn keys(values: &[&str]) -> Vec<String> {
        values.iter().map(|value| (*value).to_owned()).collect()
    }

    #[test]
    fn insertion_before_renumbered_existing_item() {
        let old = keys(&["조문가", "조문나", "조문다"]);
        let new = keys(&["조문가", "새조문", "조문나", "조문다"]);
        let result = pair(&old, &new).unwrap();
        assert_eq!(
            result.pairs,
            vec![
                Pair { old: 0, new: 0 },
                Pair { old: 1, new: 2 },
                Pair { old: 2, new: 3 },
            ]
        );
    }

    #[test]
    fn duplicate_body_chooses_nearest_unmatched_with_first_index_tie() {
        let result = pair(
            &keys(&["중복", "중복", "", "중복"]),
            &keys(&["중복", "", "중복", "중복"]),
        )
        .unwrap();
        assert_eq!(
            result.pairs,
            vec![
                Pair { old: 0, new: 0 },
                Pair { old: 1, new: 2 },
                Pair { old: 3, new: 3 },
            ]
        );
    }

    #[test]
    fn empty_keys_are_not_paired() {
        assert!(
            pair(&keys(&["", ""]), &keys(&["", ""]))
                .unwrap()
                .pairs
                .is_empty()
        );
    }

    #[test]
    fn rejects_unbounded_input() {
        let oversized = vec!["x".to_string(); 1_025];
        assert!(pair(&oversized, &[]).is_err());
    }

    #[test]
    fn duplicate_key_ties_choose_lower_unmatched_index() {
        let old = keys(&["a", "x", "duplicate", "x", "duplicate"]);
        let new = keys(&["a", "duplicate", "x", "duplicate", "x"]);
        let matched = pair(&old, &new).unwrap().pairs;
        assert_eq!(matched[2], Pair { old: 2, new: 1 });
        assert_eq!(matched[4], Pair { old: 4, new: 3 });
    }
    #[test]
    fn one_thousand_twenty_four_reordered_keys_are_exact() {
        let old: Vec<_> = (0..1_024).map(|i| format!("clause{i:04}")).collect();
        let new: Vec<_> = old.iter().cloned().rev().collect();
        let matched = pair(&old, &new).unwrap().pairs;
        assert_eq!(matched.len(), 1_024);
        assert!(
            matched
                .iter()
                .enumerate()
                .all(|(i, pair)| pair.old == i && pair.new == 1_023 - i)
        );
    }
    #[test]
    fn does_not_infer_semantically_similar_but_changed_content() {
        assert!(
            pair(&keys(&["동의없이"]), &keys(&["동의합니다"]))
                .unwrap()
                .pairs
                .is_empty()
        );
    }
}
