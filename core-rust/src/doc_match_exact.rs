//! Port of the FIRST MatchParts pairing pass, not of the full matcher.
//! Keys are prepared by the canonical C# LineageNormalize rule.
//! Unique/duplicate content is paired greedily with nearest unmatched
//! destination (index distance), with lowest new index breaking ties.
//! All downstream definition-head, hierarchy, residual and move rules
//! still use the authoritative C# matcher.
use serde::{Deserialize, Serialize};

const MAX_PARTS: usize = 256;
const MAX_KEY_LENGTH: usize = 16_384;
const MAX_TOTAL_CHARS: usize = 131_072;

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
    let mut used_new = vec![false; new.len()];
    let mut pairs = Vec::new();
    for (i, old_key) in old.iter().enumerate() {
        if old_key.is_empty() {
            continue;
        }
        let mut best: Option<usize> = None;
        for (j, new_key) in new.iter().enumerate() {
            if used_new[j] || new_key != old_key {
                continue;
            }
            if best.is_none_or(|prev| i.abs_diff(j) < i.abs_diff(prev)) {
                best = Some(j);
            }
        }
        if let Some(j) = best {
            used_new[j] = true;
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
        let oversized = vec!["x".to_string(); 257];
        assert!(pair(&oversized, &[]).is_err());
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
