//! Word review generic text change hunk selection.
//! Rust owns every stable-token anchor and the pair of changed source and
//! target UTF-16 slices. C# remains the model/marker presenter.
use crate::doc_token_match::{self, Input as TokenInput};
use serde::{Deserialize, Serialize};
#[derive(Debug, Deserialize, Clone)]
pub struct Token {
    pub start: usize,
    pub end: usize,
    pub key: String,
}
#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Input {
    pub old_length: usize,
    pub new_length: usize,
    pub old: Vec<Token>,
    pub new: Vec<Token>,
}
#[derive(Debug, PartialEq, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Hunk {
    pub old_start: usize,
    pub old_end: usize,
    pub new_start: usize,
    pub new_end: usize,
    pub hunk_order: usize,
}
#[derive(Debug, Serialize)]
pub struct Result {
    pub hunks: Vec<Hunk>,
}
pub fn validate(v: &Input) -> bool {
    v.old_length <= 131072
        && v.new_length <= 131072
        && v.old.len() <= 512
        && v.new.len() <= 512
        && v.old.len().saturating_mul(v.new.len()) <= 100_000
        && v.old
            .iter()
            .all(|x| x.start <= x.end && x.end <= v.old_length && x.key.len() <= 8192)
        && v.new
            .iter()
            .all(|x| x.start <= x.end && x.end <= v.new_length && x.key.len() <= 8192)
}
pub fn select(v: &Input) -> Result {
    let a = &v.old;
    let b = &v.new;
    let blocks = doc_token_match::blocks(&TokenInput {
        old: a.iter().map(|x| x.key.clone()).collect(),
        new: b.iter().map(|x| x.key.clone()).collect(),
    });
    let mut anchors: Vec<(isize, isize)> = vec![(-1, -1)];
    for block in blocks.blocks {
        for k in 0..block.size {
            anchors.push(((block.a + k) as isize, (block.b + k) as isize));
        }
    }
    anchors.push((a.len() as isize, b.len() as isize));
    let mut hunks = vec![];
    for (k, pair) in anchors.windows(2).enumerate() {
        let left = pair[0];
        let right = pair[1];
        let (ai, aj, bi, bj) = (
            (left.0 + 1) as usize,
            right.0 as usize,
            (left.1 + 1) as usize,
            right.1 as usize,
        );
        if ai >= aj && bi >= bj {
            continue;
        }
        let old_start = if ai < a.len() {
            a[ai].start
        } else if left.0 >= 0 {
            a[left.0 as usize].end
        } else {
            0
        };
        let old_end = if aj > ai { a[aj - 1].end } else { old_start };
        let new_start = if bi < b.len() {
            b[bi].start
        } else if left.1 >= 0 {
            b[left.1 as usize].end
        } else {
            0
        };
        let new_end = if bj > bi { b[bj - 1].end } else { new_start };
        hunks.push(Hunk {
            old_start,
            old_end,
            new_start,
            new_end,
            hunk_order: k,
        });
    }
    Result { hunks }
}
#[cfg(test)]
mod tests {
    use super::*;
    fn t(s: usize, e: usize, k: &str) -> Token {
        Token {
            start: s,
            end: e,
            key: k.into(),
        }
    }
    #[test]
    fn replaces_mid_sentence_only() {
        let req = Input {
            old_length: 10,
            new_length: 10,
            old: vec![t(0, 3, "one"), t(4, 6, "is"), t(7, 10, "old")],
            new: vec![t(0, 3, "one"), t(4, 6, "is"), t(7, 10, "new")],
        };
        assert_eq!(
            select(&req).hunks,
            vec![Hunk {
                old_start: 7,
                old_end: 10,
                new_start: 7,
                new_end: 10,
                hunk_order: 2
            }]
        );
    }
    #[test]
    fn insertion_when_other_empty() {
        let req = Input {
            old_length: 0,
            new_length: 4,
            old: vec![],
            new: vec![t(0, 4, "word")],
        };
        assert_eq!(
            select(&req).hunks,
            vec![Hunk {
                old_start: 0,
                old_end: 0,
                new_start: 0,
                new_end: 4,
                hunk_order: 0
            }]
        );
    }
}
