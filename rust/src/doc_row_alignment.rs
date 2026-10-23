//! Whole A/B/C document row placement. C# owns the input unit objects,
//! source text matching and cross-document merge predicate, but Rust owns
//! all baseline axis placement, insertion grouping, coalescing and sorting.
use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, HashSet};

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Matched {
    pub doc: usize,
    pub base: usize,
    pub other: usize,
}
#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Addition {
    pub doc: usize,
    pub other: usize,
    pub slot: usize,
    pub unit_index: usize,
}
#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AllowedMerge {
    pub left_doc: usize,
    pub left_other: usize,
    pub right_doc: usize,
    pub right_other: usize,
}
#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Input {
    pub counts: Vec<usize>,
    pub base_doc: usize,
    pub matched: Vec<Matched>,
    pub additions: Vec<Addition>,
    pub allowed_merge: Vec<AllowedMerge>,
}
#[derive(Debug, Serialize)]
pub struct Result {
    pub rows: Vec<Vec<Option<usize>>>,
}
pub fn validate(input: &Input) -> bool {
    let docs = input.counts.len();
    if !(2..=3).contains(&docs)
        || input.base_doc >= docs
        || input.counts.iter().any(|&v| v > 512)
        || input.additions.len() > 192
        || input.allowed_merge.len() > 18432
        || input.matched.len() > 1024
    {
        return false;
    }
    let mut occupied = HashSet::new();
    for m in &input.matched {
        if m.doc >= docs
            || m.doc == input.base_doc
            || m.base >= input.counts[input.base_doc]
            || m.other >= input.counts[m.doc]
            || !occupied.insert((m.doc, m.base))
        {
            return false;
        }
    }
    let mut other_used = HashSet::new();
    for m in &input.matched {
        if !other_used.insert((m.doc, m.other)) {
            return false;
        }
    }
    for a in &input.additions {
        if a.doc >= docs
            || a.doc == input.base_doc
            || a.other >= input.counts[a.doc]
            || a.slot > input.counts[input.base_doc]
            || !other_used.insert((a.doc, a.other))
        {
            return false;
        }
    }
    input.allowed_merge.iter().all(|m| {
        m.left_doc < docs
            && m.right_doc < docs
            && m.left_doc != m.right_doc
            && m.left_other < input.counts[m.left_doc]
            && m.right_other < input.counts[m.right_doc]
    })
}
pub fn align(input: &Input) -> Result {
    let docs = input.counts.len();
    let base = input.base_doc;
    let mut rows: Vec<Vec<Option<usize>>> = (0..input.counts[base])
        .map(|i| {
            let mut row = vec![None; docs];
            row[base] = Some(i);
            row
        })
        .collect();
    for m in &input.matched {
        rows[m.base][m.doc] = Some(m.other);
    }
    let mut grouped: BTreeMap<usize, Vec<&Addition>> = BTreeMap::new();
    for a in &input.additions {
        grouped.entry(a.slot).or_default().push(a);
    }
    let merges: HashSet<((usize, usize), (usize, usize))> = input
        .allowed_merge
        .iter()
        .map(|m| ((m.left_doc, m.left_other), (m.right_doc, m.right_other)))
        .collect();
    for (slot, mut additions) in grouped.into_iter().rev() {
        additions.sort_by_key(|a| (a.unit_index, a.doc));
        let mut pending: Vec<Vec<Option<usize>>> = Vec::new();
        for a in additions {
            let selected = pending.iter().position(|r| {
                if r[a.doc].is_some() {
                    return false;
                }
                let Some((existing_doc, other)) = r
                    .iter()
                    .enumerate()
                    .find_map(|(d, &x)| x.map(|other| (d, other)))
                else {
                    return false;
                };
                merges.contains(&((existing_doc, other), (a.doc, a.other)))
            });
            if let Some(index) = selected {
                pending[index][a.doc] = Some(a.other);
            } else {
                let mut row = vec![None; docs];
                row[a.doc] = Some(a.other);
                pending.push(row);
            }
        }
        let at = slot.min(rows.len());
        rows.splice(at..at, pending);
    }
    Result { rows }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn merges_shared_third_document_addition() {
        let input = Input {
            counts: vec![2, 3, 3],
            base_doc: 0,
            matched: vec![
                Matched {
                    doc: 1,
                    base: 0,
                    other: 0,
                },
                Matched {
                    doc: 1,
                    base: 1,
                    other: 2,
                },
                Matched {
                    doc: 2,
                    base: 0,
                    other: 0,
                },
                Matched {
                    doc: 2,
                    base: 1,
                    other: 2,
                },
            ],
            additions: vec![
                Addition {
                    doc: 1,
                    other: 1,
                    slot: 1,
                    unit_index: 1,
                },
                Addition {
                    doc: 2,
                    other: 1,
                    slot: 1,
                    unit_index: 1,
                },
            ],
            allowed_merge: vec![AllowedMerge {
                left_doc: 1,
                left_other: 1,
                right_doc: 2,
                right_other: 1,
            }],
        };
        assert_eq!(
            align(&input).rows,
            vec![
                vec![Some(0), Some(0), Some(0)],
                vec![None, Some(1), Some(1)],
                vec![Some(1), Some(2), Some(2)]
            ]
        );
    }
    #[test]
    fn preserves_separate_additions_and_base_order() {
        let input = Input {
            counts: vec![2, 3],
            base_doc: 0,
            matched: vec![
                Matched {
                    doc: 1,
                    base: 0,
                    other: 0,
                },
                Matched {
                    doc: 1,
                    base: 1,
                    other: 2,
                },
            ],
            additions: vec![Addition {
                doc: 1,
                other: 1,
                slot: 1,
                unit_index: 1,
            }],
            allowed_merge: vec![],
        };
        assert_eq!(align(&input).rows.len(), 3);
        assert_eq!(align(&input).rows[1], vec![None, Some(1)]);
    }
}
