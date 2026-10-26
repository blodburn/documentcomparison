//! Full review boundary projection from C# BaseMapBoundary/MapBoundary.
//! UTF-16 positions and source lexical spans are passed without changing
//! the original C# Regex matching and lineage-key normalization rules.
use crate::doc_token_match::{self, Input as TokenInput};
use serde::{Deserialize, Serialize};
use std::collections::HashMap;
#[derive(Deserialize, Debug, Clone)]
#[serde(rename_all = "camelCase")]
pub struct Span {
    pub start: usize,
    pub end: usize,
    pub key: String,
}
#[derive(Deserialize, Debug)]
#[serde(rename_all = "camelCase")]
pub struct Input {
    pub src: String,
    pub dst: String,
    pub pos: usize,
    pub src_spans: Vec<Span>,
    pub dst_spans: Vec<Span>,
    pub src_word: Vec<bool>,
    pub dst_word: Vec<bool>,
}
#[derive(Serialize, Debug)]
pub struct Result {
    pub mapped: usize,
}
pub fn validate(i: &Input) -> bool {
    let n = i.src.encode_utf16().count();
    let m = i.dst.encode_utf16().count();
    n <= 2048
        && m <= 2048
        && n.saturating_mul(m) <= 1_000_000
        && i.src_word.len() == n
        && i.dst_word.len() == m
        && i.src_spans.len() <= 256
        && i.dst_spans.len() <= 256
        && i.src_spans
            .iter()
            .all(|s| s.start <= s.end && s.end <= n && s.key.len() <= 2048)
        && i.dst_spans
            .iter()
            .all(|s| s.start <= s.end && s.end <= m && s.key.len() <= 2048)
}
fn clamp(i: usize, max: usize) -> usize {
    i.min(max)
}
fn char_matches(a: &[u16], b: &[u16]) -> Vec<(usize, usize, usize)> {
    let mut index: HashMap<u16, Vec<usize>> = HashMap::new();
    for (j, &v) in b.iter().enumerate() {
        index.entry(v).or_default().push(j);
    }
    let mut stack = vec![(0, a.len(), 0, b.len())];
    let mut matches = vec![];
    while let Some((alo, ahi, blo, bhi)) = stack.pop() {
        let (mut best_i, mut best_j, mut best_len) = (alo, blo, 0);
        let mut previous = HashMap::new();
        let mut next = HashMap::new();
        for (i, &v) in a.iter().enumerate().take(ahi).skip(alo) {
            next.clear();
            if let Some(js) = index.get(&v) {
                for &j in js {
                    if j < blo {
                        continue;
                    }
                    if j >= bhi {
                        break;
                    }
                    let k = j
                        .checked_sub(1)
                        .and_then(|jj| previous.get(&jj).copied())
                        .unwrap_or(0)
                        + 1;
                    next.insert(j, k);
                    if k > best_len {
                        best_i = i + 1 - k;
                        best_j = j + 1 - k;
                        best_len = k;
                    }
                }
            }
            std::mem::swap(&mut previous, &mut next);
        }
        if best_len == 0 {
            continue;
        }
        matches.push((best_i, best_j, best_len));
        if alo < best_i && blo < best_j {
            stack.push((alo, best_i, blo, best_j));
        }
        let after_i = best_i + best_len;
        let after_j = best_j + best_len;
        if after_i < ahi && after_j < bhi {
            stack.push((after_i, ahi, after_j, bhi));
        }
    }
    matches.sort_by_key(|x| (x.0, x.1));
    let mut merged: Vec<(usize, usize, usize)> = vec![];
    for (i, j, n) in matches {
        if let Some(last) = merged.last_mut()
            && last.0 + last.2 == i
            && last.1 + last.2 == j
        {
            last.2 += n;
            continue;
        }
        merged.push((i, j, n));
    }
    merged.push((a.len(), b.len(), 0));
    merged
}
fn base_map(i: &Input, pos: usize) -> usize {
    let src: Vec<u16> = i.src.encode_utf16().collect();
    let dst: Vec<u16> = i.dst.encode_utf16().collect();
    let n = src.len();
    let m = dst.len();
    let inside = pos > 0 && pos < n && i.src_word[pos - 1] && i.src_word[pos];
    if !inside && !i.src_spans.is_empty() && !i.dst_spans.is_empty() {
        let block = doc_token_match::blocks(&TokenInput {
            old: i.src_spans.iter().map(|x| x.key.clone()).collect(),
            new: i.dst_spans.iter().map(|x| x.key.clone()).collect(),
        });
        let mut map = HashMap::new();
        for b in block.blocks {
            for k in 0..b.size {
                map.insert(b.a + k, b.b + k);
            }
        }
        let mut left: Vec<usize> = i
            .src_spans
            .iter()
            .enumerate()
            .filter(|(j, s)| s.end <= pos && map.contains_key(j))
            .map(|(j, _)| j)
            .collect();
        let mut right: Vec<usize> = i
            .src_spans
            .iter()
            .enumerate()
            .filter(|(j, s)| s.start >= pos && map.contains_key(j))
            .map(|(j, _)| j)
            .collect();
        left.sort_by_key(|&j| std::cmp::Reverse(i.src_spans[j].end));
        right.sort_by_key(|&j| i.src_spans[j].start);
        if let (Some(&l), Some(&r)) = (left.first(), right.first()) {
            let dl = map[&l];
            let dr = map[&r];
            if dl <= dr && i.dst_spans[dl].end <= i.dst_spans[dr].start {
                return clamp(i.dst_spans[dl].end, m);
            }
        }
        if let Some(&l) = left.first() {
            return clamp(i.dst_spans[map[&l]].end, m);
        }
        if let Some(&r) = right.first() {
            return clamp(i.dst_spans[map[&r]].start, m);
        }
    }
    let mut ai = 0usize;
    let mut bj = 0usize;
    for (x, y, size) in char_matches(&src, &dst) {
        if ai < x || bj < y {
            if ai <= pos && pos <= x {
                if x == ai {
                    return clamp(bj, m);
                }
                let frac = (pos - ai) as f64 / (x - ai) as f64;
                return clamp(
                    (bj as f64 + frac * (y - bj) as f64).round_ties_even() as usize,
                    m,
                );
            }
            if pos < ai {
                return clamp(bj, m);
            }
        }
        if size > 0 && x <= pos && pos <= x + size {
            return clamp(y + (pos - x).min(size), m);
        }
        ai = x + size;
        bj = y + size;
    }
    m
}
pub fn map(i: &Input) -> Result {
    let src: Vec<u16> = i.src.encode_utf16().collect();
    let dst: Vec<u16> = i.dst.encode_utf16().collect();
    let pos = clamp(i.pos, src.len());
    let original = base_map(i, pos);
    let mut left = pos;
    while left > 0 && !i.src_word[left - 1] {
        left -= 1;
    }
    let sep = &src[left..pos];
    let punct = sep
        .iter()
        .any(|&ch| matches!(ch, 10 | 46 | 33 | 63 | 59 | 58 | 0x3002 | 0xff01 | 0xff1f));
    if !punct {
        return Result { mapped: original };
    }
    let mut q = clamp(original, dst.len());
    while q < dst.len() && !i.dst_word[q] {
        q += 1;
    }
    Result { mapped: q }
}
#[cfg(test)]
mod tests {
    use super::*;
    fn sample(src: &str, dst: &str, pos: usize) -> Input {
        Input {
            src: src.into(),
            dst: dst.into(),
            pos,
            src_spans: vec![],
            dst_spans: vec![],
            src_word: src
                .encode_utf16()
                .map(|c| (c as u8 as char).is_ascii_alphanumeric())
                .collect(),
            dst_word: dst
                .encode_utf16()
                .map(|c| (c as u8 as char).is_ascii_alphanumeric())
                .collect(),
        }
    }
    #[test]
    fn same_text_identity() {
        for x in 0..5 {
            assert_eq!(map(&sample("abcde", "abcde", x)).mapped, x);
        }
    }
    #[test]
    fn deleted_prefix_anchors_to_adjacent() {
        assert_eq!(map(&sample("abc", "xc", 3)).mapped, 2);
    }
}
