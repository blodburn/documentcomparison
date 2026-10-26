//! Full cross-version A/B/C addition merge rule from raw Rust scoring.
//! C# supplies source unit texts and the already selected counterpart
//! lineage matches, but no C# merger eligibility or pairwise score matrix.
use crate::{doc_row_alignment, doc_unit_lineage_raw};
use serde::Deserialize;
use std::collections::BTreeMap;
#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Input {
    pub base_doc: usize,
    pub units: Vec<Vec<doc_unit_lineage_raw::RawUnit>>,
    pub matched: Vec<doc_row_alignment::Matched>,
    pub additions: Vec<doc_row_alignment::Addition>,
}
pub fn validate(v: &Input) -> bool {
    let counts = v.units.iter().map(Vec::len).collect::<Vec<_>>();
    (2..=3).contains(&counts.len())
        && v.base_doc < counts.len()
        && counts.iter().all(|x| *x <= 512)
        && v.additions.len() <= 192
        && v.matched.len() <= 1024
        && v.units.iter().flatten().all(|u| {
            u.body.encode_utf16().count() <= 512
                && u.title.encode_utf16().count() <= 256
                && u.header.encode_utf16().count() <= 512
        })
        && doc_row_alignment::validate(&doc_row_alignment::Input {
            counts,
            base_doc: v.base_doc,
            matched: v
                .matched
                .iter()
                .map(|m| doc_row_alignment::Matched {
                    doc: m.doc,
                    base: m.base,
                    other: m.other,
                })
                .collect(),
            additions: v
                .additions
                .iter()
                .map(|a| doc_row_alignment::Addition {
                    doc: a.doc,
                    other: a.other,
                    slot: a.slot,
                    unit_index: a.unit_index,
                })
                .collect(),
            allowed_merge: vec![],
        })
}
fn compatible(a: &doc_unit_lineage_raw::RawUnit, b: &doc_unit_lineage_raw::RawUnit) -> bool {
    let tr = if a.title.is_empty() || b.title.is_empty() {
        0.0
    } else {
        doc_unit_lineage_raw::title_sim(a, b)
    };
    let br = doc_unit_lineage_raw::body_sim(a, b);
    let score = doc_unit_lineage_raw::lineage_sim(a, b, tr, br);
    if score < 0.84 {
        return false;
    }
    if !a.title.is_empty() && !b.title.is_empty() && tr < 0.35 {
        return false;
    }
    if !a.number.is_empty() && !b.number.is_empty() && !a.number.eq_ignore_ascii_case(&b.number) {
        if a.title.is_empty() || b.title.is_empty() || tr < 0.35 {
            return false;
        }
        if doc_unit_lineage_raw::low_information(&a.title)
            && doc_unit_lineage_raw::low_information(&b.title)
        {
            let ak = doc_unit_lineage_raw::convert(&a.body, "lineage");
            let bk = doc_unit_lineage_raw::convert(&b.body, "lineage");
            if ak.is_empty() || ak != bk {
                return false;
            }
        }
    }
    true
}
pub fn align(v: &Input) -> doc_row_alignment::Result {
    let mut slots: BTreeMap<usize, Vec<&doc_row_alignment::Addition>> = BTreeMap::new();
    for a in &v.additions {
        slots.entry(a.slot).or_default().push(a);
    }
    let mut allowed = vec![];
    for group in slots.values() {
        let mut ordered = group.clone();
        ordered.sort_by_key(|a| (a.unit_index, a.doc));
        for i in 0..ordered.len() {
            for j in i + 1..ordered.len() {
                let (a, b) = (ordered[i], ordered[j]);
                if a.doc == b.doc {
                    continue;
                }
                if compatible(&v.units[a.doc][a.other], &v.units[b.doc][b.other]) {
                    allowed.push(doc_row_alignment::AllowedMerge {
                        left_doc: a.doc,
                        left_other: a.other,
                        right_doc: b.doc,
                        right_other: b.other,
                    });
                }
            }
        }
    }
    let counts = v.units.iter().map(Vec::len).collect::<Vec<_>>();
    doc_row_alignment::align(&doc_row_alignment::Input {
        counts,
        base_doc: v.base_doc,
        matched: v
            .matched
            .iter()
            .map(|m| doc_row_alignment::Matched {
                doc: m.doc,
                base: m.base,
                other: m.other,
            })
            .collect(),
        additions: v
            .additions
            .iter()
            .map(|a| doc_row_alignment::Addition {
                doc: a.doc,
                other: a.other,
                slot: a.slot,
                unit_index: a.unit_index,
            })
            .collect(),
        allowed_merge: allowed,
    })
}
#[cfg(test)]
mod tests {
    use super::*;
    fn unit(title: &str) -> doc_unit_lineage_raw::RawUnit {
        doc_unit_lineage_raw::RawUnit {
            kind: "article".into(),
            number: "1".into(),
            title: title.into(),
            header: format!("Article 1 ({title})"),
            body: "Identical wording about a user's private information.".into(),
        }
    }
    #[test]
    fn matching_third_document_additions_combine() {
        let v = Input {
            base_doc: 0,
            units: vec![
                vec![unit("A")],
                vec![unit("A"), unit("Privacy")],
                vec![unit("A"), unit("Privacy")],
            ],
            matched: vec![
                doc_row_alignment::Matched {
                    doc: 1,
                    base: 0,
                    other: 0,
                },
                doc_row_alignment::Matched {
                    doc: 2,
                    base: 0,
                    other: 0,
                },
            ],
            additions: vec![
                doc_row_alignment::Addition {
                    doc: 1,
                    other: 1,
                    slot: 1,
                    unit_index: 1,
                },
                doc_row_alignment::Addition {
                    doc: 2,
                    other: 1,
                    slot: 1,
                    unit_index: 1,
                },
            ],
        };
        assert!(validate(&v));
        let result = align(&v);
        assert_eq!(result.rows.len(), 2);
        assert_eq!(result.rows[1], vec![None, Some(1), Some(1)]);
    }
}
