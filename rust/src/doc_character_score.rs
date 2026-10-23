//! Rust implementation of the legal unit and item character-level scores.
//! Inputs are already normalized by the managed Unicode contract.
//! Scores use UTF-16 code units like C# (not Rust Unicode scalar counts).
use serde::{Deserialize, Serialize};
use std::collections::HashMap;

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Input {
    pub old: String,
    pub new: String,
    pub mode: String,
    #[serde(default)]
    pub old_tokens: Vec<String>,
    #[serde(default)]
    pub new_tokens: Vec<String>,
}
#[derive(Debug, Serialize)]
pub struct Result {
    pub score: f64,
}
pub fn validate(v: &Input) -> bool {
    v.old.encode_utf16().count() <= 512
        && v.new.encode_utf16().count() <= 512
        && (v.mode == "indel"
            || v.mode == "sequence"
            || (v.mode == "token"
                && v.old_tokens.len() <= 256
                && v.new_tokens.len() <= 256
                && v.old_tokens
                    .iter()
                    .chain(v.new_tokens.iter())
                    .all(|t| t.len() <= 4096)))
}
pub fn compare(v: &Input) -> Result {
    if v.mode == "token" {
        let a = &v.old_tokens;
        let b = &v.new_tokens;
        if a.is_empty() || b.is_empty() {
            return Result { score: 0.0 };
        }
        let common = lcs_length(a, b);
        return Result {
            score: 2.0 * common as f64 / (a.len() + b.len()) as f64,
        };
    }
    let a: Vec<u16> = v.old.encode_utf16().collect();
    let b: Vec<u16> = v.new.encode_utf16().collect();
    if a == b {
        return Result { score: 1.0 };
    }
    if a.is_empty() || b.is_empty() {
        return Result { score: 0.0 };
    }
    let common = if v.mode == "indel" {
        lcs_length(&a, &b)
    } else {
        sequence_matching_length(&a, &b)
    };
    Result {
        score: 2.0 * common as f64 / (a.len() + b.len()) as f64,
    }
}
fn lcs_length<T: Eq>(a: &[T], b: &[T]) -> usize {
    let mut previous = vec![0usize; b.len() + 1];
    let mut row = vec![0usize; b.len() + 1];
    for x in a {
        for (i, y) in b.iter().enumerate() {
            row[i + 1] = if x == y {
                previous[i] + 1
            } else {
                row[i].max(previous[i + 1])
            };
        }
        std::mem::swap(&mut row, &mut previous);
    }
    previous[b.len()]
}
fn sequence_matching_length(a: &[u16], b: &[u16]) -> usize {
    let mut b2j: HashMap<u16, Vec<usize>> = HashMap::new();
    for (j, &x) in b.iter().enumerate() {
        b2j.entry(x).or_default().push(j);
    }
    let mut stack = vec![(0, a.len(), 0, b.len())];
    let mut matched = 0usize;
    while let Some((alo, ahi, blo, bhi)) = stack.pop() {
        let (mut best_i, mut best_j, mut best_size) = (alo, blo, 0);
        let mut prior: HashMap<usize, usize> = HashMap::new();
        let mut next: HashMap<usize, usize> = HashMap::new();
        for (i, &x) in a.iter().enumerate().take(ahi).skip(alo) {
            next.clear();
            if let Some(js) = b2j.get(&x) {
                for &j in js {
                    if j < blo {
                        continue;
                    }
                    if j >= bhi {
                        break;
                    }
                    let k = j
                        .checked_sub(1)
                        .and_then(|q| prior.get(&q).copied())
                        .unwrap_or(0)
                        + 1;
                    next.insert(j, k);
                    if k > best_size {
                        best_i = i + 1 - k;
                        best_j = j + 1 - k;
                        best_size = k;
                    }
                }
            }
            std::mem::swap(&mut prior, &mut next);
        }
        if best_size == 0 {
            continue;
        }
        matched += best_size;
        if alo < best_i && blo < best_j {
            stack.push((alo, best_i, blo, best_j));
        }
        if best_i + best_size < ahi && best_j + best_size < bhi {
            stack.push((best_i + best_size, ahi, best_j + best_size, bhi));
        }
    }
    matched
}
#[cfg(test)]
mod tests {
    use super::*;
    fn score(a: &str, b: &str, mode: &str) -> f64 {
        compare(&Input {
            old: a.to_owned(),
            new: b.to_owned(),
            mode: mode.into(),
            old_tokens: vec![],
            new_tokens: vec![],
        })
        .score
    }
    #[test]
    fn utf16_surrogates_and_ambiguous_reorder() {
        assert_eq!(score("A😀B", "A😃B", "indel"), 0.75);
        assert!(score("ABAB", "BABA", "sequence") > 0.0);
        assert_eq!(score("", "", "indel"), 1.0);
    }
    #[test]
    fn normalization_is_caller_owned() {
        assert_eq!(score("동의 없이", "동의없이", "indel"), 8.0 / 9.0);
        assert_eq!(score("title", "title", "sequence"), 1.0);
    }
}
