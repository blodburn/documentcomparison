//! DOCX export paragraph/review/formatting exact-sequence correspondence.
//! Replaces managed NativeOfficeExporter.Lcs without altering Word XML IO.
use serde::{Deserialize, Serialize};
use std::collections::HashMap;
#[derive(Debug, Deserialize)]
pub struct Input {
    pub old: Vec<String>,
    pub new: Vec<String>,
}
#[derive(Debug, Serialize, PartialEq, Eq)]
pub struct Pair {
    pub a: usize,
    pub b: usize,
}
#[derive(Debug, Serialize)]
pub struct Result {
    pub pairs: Vec<Pair>,
}
pub fn validate(v: &Input) -> bool {
    v.old.len() <= 16_384
        && v.new.len() <= 16_384
        && v.old.iter().chain(&v.new).all(|x| x.len() <= 65_536)
        && v.old.iter().chain(&v.new).map(|x| x.len()).sum::<usize>() <= 8 * 1024 * 1024
}
pub fn align(v: &Input) -> Result {
    let (a, b) = (&v.old, &v.new);
    if a.len().saturating_add(1).saturating_mul(b.len() + 1) > 4_000_000 {
        return Result {
            pairs: sequence_pairs(a, b),
        };
    }
    let n = a.len();
    let m = b.len();
    let mut dp = vec![0u32; (n + 1) * (m + 1)];
    for i in (0..n).rev() {
        for j in (0..m).rev() {
            let at = i * (m + 1) + j;
            dp[at] = if a[i] == b[j] {
                dp[(i + 1) * (m + 1) + j + 1] + 1
            } else {
                dp[(i + 1) * (m + 1) + j].max(dp[i * (m + 1) + j + 1])
            };
        }
    }
    let (mut i, mut j) = (0, 0);
    let mut out = vec![];
    while i < n && j < m {
        if a[i] == b[j] {
            out.push(Pair { a: i, b: j });
            i += 1;
            j += 1;
        } else if dp[(i + 1) * (m + 1) + j] >= dp[i * (m + 1) + j + 1] {
            i += 1;
        } else {
            j += 1;
        }
    }
    Result { pairs: out }
}
fn sequence_pairs(a: &[String], b: &[String]) -> Vec<Pair> {
    let mut b2j: HashMap<&str, Vec<usize>> = HashMap::new();
    for (j, x) in b.iter().enumerate() {
        b2j.entry(x).or_default().push(j);
    }
    let mut stack = vec![(0, a.len(), 0, b.len())];
    let mut blocks = vec![];
    while let Some((alo, ahi, blo, bhi)) = stack.pop() {
        let (mut best_i, mut best_j, mut best_len) = (alo, blo, 0usize);
        let mut previous = HashMap::new();
        let mut current = HashMap::new();
        for (i, x) in a.iter().enumerate().take(ahi).skip(alo) {
            current.clear();
            if let Some(js) = b2j.get(x.as_str()) {
                for &j in js {
                    if j < blo {
                        continue;
                    }
                    if j >= bhi {
                        break;
                    }
                    let len = j
                        .checked_sub(1)
                        .and_then(|prev| previous.get(&prev).copied())
                        .unwrap_or(0)
                        + 1;
                    current.insert(j, len);
                    if len > best_len {
                        best_i = i + 1 - len;
                        best_j = j + 1 - len;
                        best_len = len;
                    }
                }
            }
            std::mem::swap(&mut previous, &mut current);
        }
        if best_len == 0 {
            continue;
        }
        blocks.push((best_i, best_j, best_len));
        if alo < best_i && blo < best_j {
            stack.push((alo, best_i, blo, best_j));
        }
        let end_i = best_i + best_len;
        let end_j = best_j + best_len;
        if end_i < ahi && end_j < bhi {
            stack.push((end_i, ahi, end_j, bhi));
        }
    }
    blocks.sort_by_key(|x| (x.0, x.1));
    blocks
        .into_iter()
        .flat_map(|(i, j, n)| (0..n).map(move |k| Pair { a: i + k, b: j + k }))
        .collect()
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn duplicate_tie_prefers_skipping_old() {
        let v = Input {
            old: vec!["a".into(), "b".into(), "a".into()],
            new: vec!["b".into(), "a".into(), "a".into()],
        };
        assert_eq!(
            align(&v).pairs,
            vec![Pair { a: 1, b: 0 }, Pair { a: 2, b: 1 }]
        );
    }
    #[test]
    fn identical_and_no_shared() {
        let v = Input {
            old: vec!["한글".into(), "same".into()],
            new: vec!["한글".into(), "same".into()],
        };
        assert_eq!(align(&v).pairs.len(), 2);
        let v = Input {
            old: vec!["A".into()],
            new: vec!["B".into()],
        };
        assert!(align(&v).pairs.is_empty());
    }
    #[test]
    fn long_document_uses_block_strategy() {
        let before = (0..2010).map(|i| format!("unique-{i}")).collect();
        let after = (0..2010).map(|i| format!("unique-{i}")).collect();
        let v = Input {
            old: before,
            new: after,
        };
        assert_eq!(align(&v).pairs.len(), 2010);
    }
}
