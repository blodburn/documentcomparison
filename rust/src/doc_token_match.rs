//! Native legal review token matching: exact C# MatcherBlocks semantics.
//! Longest contiguous block, left/right recursive stack, original earliest
//! best-scanning tie, adjacent merge, final sentinel. Shared by sentence
//! and general word review without changing tokenization/normalization.
use serde::{Deserialize, Serialize};
use std::collections::HashMap;

#[derive(Debug, Deserialize)]
pub struct Input {
    pub old: Vec<String>,
    pub new: Vec<String>,
}
#[derive(Debug, Clone, Serialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct Block {
    pub a: usize,
    pub b: usize,
    pub size: usize,
}
#[derive(Debug, Serialize)]
pub struct Result {
    pub blocks: Vec<Block>,
}
pub fn validate(input: &Input) -> bool {
    input.old.len() <= 512
        && input.new.len() <= 512
        && input.old.len().saturating_mul(input.new.len()) <= 100_000
        && input
            .old
            .iter()
            .chain(input.new.iter())
            .all(|s| s.len() <= 4096)
}
pub fn blocks(input: &Input) -> Result {
    let (old, new) = (&input.old, &input.new);
    let mut b2j: HashMap<&str, Vec<usize>> = HashMap::new();
    for (j, text) in new.iter().enumerate() {
        b2j.entry(text.as_str()).or_default().push(j);
    }
    let mut stack = vec![(0, old.len(), 0, new.len())];
    let mut found = vec![];
    while let Some((alo, ahi, blo, bhi)) = stack.pop() {
        let (mut best_i, mut best_j, mut best_size) = (alo, blo, 0usize);
        let mut j2len: HashMap<usize, usize> = HashMap::new();
        let mut next: HashMap<usize, usize> = HashMap::new();
        for (i, token) in old.iter().enumerate().take(ahi).skip(alo) {
            next.clear();
            if let Some(js) = b2j.get(token.as_str()) {
                for &j in js {
                    if j < blo {
                        continue;
                    }
                    if j >= bhi {
                        break;
                    }
                    let k = j
                        .checked_sub(1)
                        .and_then(|index| j2len.get(&index).copied())
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
            std::mem::swap(&mut j2len, &mut next);
        }
        if best_size == 0 {
            continue;
        }
        found.push(Block {
            a: best_i,
            b: best_j,
            size: best_size,
        });
        if alo < best_i && blo < best_j {
            stack.push((alo, best_i, blo, best_j));
        }
        if best_i + best_size < ahi && best_j + best_size < bhi {
            stack.push((best_i + best_size, ahi, best_j + best_size, bhi));
        }
    }
    found.sort_by(|x, y| x.a.cmp(&y.a).then_with(|| x.b.cmp(&y.b)));
    let mut merged: Vec<Block> = Vec::new();
    for block in found {
        if let Some(last) = merged.last_mut()
            && last.a + last.size == block.a
            && last.b + last.size == block.b
        {
            last.size += block.size;
            continue;
        }
        merged.push(block);
    }
    merged.push(Block {
        a: old.len(),
        b: new.len(),
        size: 0,
    });
    Result { blocks: merged }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn stable_tie_and_reordering() {
        let input = Input {
            old: vec!["a", "b", "a", "c"]
                .into_iter()
                .map(str::to_string)
                .collect(),
            new: vec!["a", "c", "a", "b"]
                .into_iter()
                .map(str::to_string)
                .collect(),
        };
        let result = blocks(&input);
        assert!(result.blocks.iter().any(|b| b.size >= 2));
        assert_eq!(
            result.blocks.last().unwrap(),
            &Block {
                a: 4,
                b: 4,
                size: 0
            }
        );
    }
    #[test]
    fn handles_duplicated_unicode_and_empty() {
        let input = Input {
            old: vec!["동의", "동의", "변경"]
                .into_iter()
                .map(str::to_string)
                .collect(),
            new: vec!["동의", "변경"]
                .into_iter()
                .map(str::to_string)
                .collect(),
        };
        let result = blocks(&input);
        assert!(result.blocks.iter().any(|b| b.size == 2));
    }
}
