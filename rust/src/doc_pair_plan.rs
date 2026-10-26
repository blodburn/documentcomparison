//! Document ComparePair matched-part structural and rewrite decision engine.
//! C# presents NativeMarker models, but Rust owns classification and labels.
use serde::{Deserialize, Serialize};
use std::collections::HashSet;
#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct StructuralPart {
    pub label: String,
    pub level: i32,
    pub family: String,
    pub start: usize,
    pub end: usize,
}
fn is_structural(s: &str) -> bool {
    s != "본문" && !s.is_empty()
}
fn summary<'a>(parts: impl IntoIterator<Item = &'a StructuralPart>) -> String {
    let labels = parts
        .into_iter()
        .filter(|p| is_structural(&p.label))
        .map(|p| p.label.as_str())
        .collect::<Vec<_>>();
    if labels.is_empty() {
        "본문".into()
    } else if labels.len() <= 5 {
        labels.join(", ")
    } else {
        format!(
            "{}~{} ({}개)",
            labels[0],
            labels[labels.len() - 1],
            labels.len()
        )
    }
}
fn structure_description(old: &[StructuralPart], new: &[StructuralPart]) -> Vec<String> {
    let oa = old
        .iter()
        .filter(|p| is_structural(&p.label))
        .collect::<Vec<_>>();
    let nb = new
        .iter()
        .filter(|p| is_structural(&p.label))
        .collect::<Vec<_>>();
    if oa.len() >= 2 && nb.is_empty() {
        vec![format!(
            "항/호 구조 변경: {} 열거 구조가 본문으로 통합",
            summary(oa)
        )]
    } else if nb.len() >= 2 && oa.is_empty() {
        vec![format!(
            "항/호 구조 변경: 본문이 {} 열거 구조로 분할",
            summary(nb)
        )]
    } else if !oa.is_empty() && !nb.is_empty() {
        let old_depth = old.iter().map(|p| p.level).max().unwrap_or(0);
        let new_depth = new.iter().map(|p| p.level).max().unwrap_or(0);
        let old_family = old
            .iter()
            .filter(|p| p.level > 0)
            .map(|p| &p.family)
            .collect::<HashSet<_>>();
        let new_family = new
            .iter()
            .filter(|p| p.level > 0)
            .map(|p| &p.family)
            .collect::<HashSet<_>>();
        if old_depth != new_depth || old_family != new_family {
            vec![format!(
                "항/호 구조 변경: {} → {}",
                summary(oa),
                summary(nb)
            )]
        } else {
            vec![]
        }
    } else {
        vec![]
    }
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Item {
    pub old: usize,
    pub new: usize,
    pub old_label: String,
    pub new_label: String,
    pub old_level: i32,
    pub new_level: i32,
    pub old_ordinal: Option<i64>,
    pub new_ordinal: Option<i64>,
    pub parent_equivalent: bool,
    pub content_equal: bool,
    pub shared_edge: bool,
    pub substantial_sentence: bool,
    pub review_anchors: bool,
    pub score: f64,
}
#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Unmatched {
    pub index: usize,
    pub has_content: bool,
    pub moved: bool,
    pub source: String,
    pub target: String,
}
#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct UnmatchedDecision {
    pub index: usize,
    pub action: String,
    pub message: String,
    pub gap_anchor: usize,
}
fn gap_anchor(
    index: usize,
    source_is_new: bool,
    items: &[Item],
    target: &[StructuralPart],
) -> usize {
    let mut candidates = items
        .iter()
        .map(|i| {
            if source_is_new {
                (i.new, i.old)
            } else {
                (i.old, i.new)
            }
        })
        .collect::<Vec<_>>();
    candidates.sort_by_key(|x| x.0);
    if let Some(&(_, target_idx)) = candidates.iter().find(|(source, _)| *source > index) {
        return target.get(target_idx).map_or(0, |p| p.start);
    }
    if let Some(&(_, target_idx)) = candidates.iter().rev().find(|(source, _)| *source < index) {
        return target.get(target_idx).map_or(0, |p| p.end);
    }
    0
}
fn classify_unmatched(
    v: &[Unmatched],
    pair: &str,
    kind: &str,
    source_is_new: bool,
    items: &[Item],
    target: &[StructuralPart],
) -> Vec<UnmatchedDecision> {
    v.iter()
        .map(|u| {
            let (action, message) = if !u.has_content {
                ("skip".into(), String::new())
            } else if u.moved {
                (
                    "move".into(),
                    format!("{pair} · 항/호 이동: {} → {}", u.source, u.target),
                )
            } else {
                (kind.into(), String::new())
            };
            UnmatchedDecision {
                index: u.index,
                action,
                message,
                gap_anchor: gap_anchor(u.index, source_is_new, items, target),
            }
        })
        .collect()
}
#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Input {
    pub pair: String,
    pub old_kind: String,
    pub new_kind: String,
    pub old_number: String,
    pub new_number: String,
    pub old_title: String,
    pub new_title: String,
    pub old_header: String,
    pub new_header: String,
    pub legal_article: bool,
    pub titles_equivalent: bool,
    pub headers_equivalent: bool,
    pub headers_semantically_equal: bool,
    pub old_parts: Vec<StructuralPart>,
    pub new_parts: Vec<StructuralPart>,
    pub old_part_count: usize,
    pub new_part_count: usize,
    pub items: Vec<Item>,
    pub unmatched_old: Vec<Unmatched>,
    pub unmatched_new: Vec<Unmatched>,
}
#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Decision {
    pub old: usize,
    pub new: usize,
    pub label_changed: bool,
    pub content_changed: bool,
    pub rewrite: bool,
    pub message: String,
}
#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Result {
    pub article_moved: bool,
    pub title_changed: bool,
    pub body_changed: bool,
    pub structure_changed: bool,
    pub structure_messages: Vec<String>,
    pub unmatched_old: Vec<UnmatchedDecision>,
    pub unmatched_new: Vec<UnmatchedDecision>,
    pub status: String,
    pub number_message: String,
    pub title_message: String,
    pub decisions: Vec<Decision>,
}
pub fn validate(v: &Input) -> bool {
    v.items.len() <= 256
        && v.old_part_count <= 256
        && v.new_part_count <= 256
        && v.pair.len() <= 128
        && v.old_parts.len() == v.old_part_count
        && v.new_parts.len() == v.new_part_count
        && v.old_parts
            .iter()
            .chain(v.new_parts.iter())
            .all(|p| p.label.len() <= 512 && p.family.len() <= 128 && p.start <= p.end)
        && v.unmatched_old.len() <= 256
        && v.unmatched_new.len() <= 256
        && v.unmatched_old
            .iter()
            .all(|x| x.index < v.old_part_count && x.source.len() < 8192 && x.target.len() < 8192)
        && v.unmatched_new
            .iter()
            .all(|x| x.index < v.new_part_count && x.source.len() < 8192 && x.target.len() < 8192)
        && v.items.iter().all(|i| {
            i.old < v.old_part_count
                && i.new < v.new_part_count
                && i.score.is_finite()
                && (0.0..=1.5).contains(&i.score)
                && i.old_label.len() <= 512
                && i.new_label.len() <= 512
        })
}
pub fn plan(v: &Input) -> Result {
    let article_moved = v.legal_article && !v.old_number.eq_ignore_ascii_case(&v.new_number);
    let title_changed = if v.legal_article {
        !v.titles_equivalent
    } else {
        !v.headers_equivalent
            && !v.headers_semantically_equal
            && (!v.old_header.is_empty() || !v.new_header.is_empty())
    };
    let number_message = if article_moved {
        format!(
            "{} · 조 이동/재번호화: {} → {}",
            v.pair, v.old_header, v.new_header
        )
    } else {
        String::new()
    };
    let title_message = if v.legal_article && title_changed {
        format!(
            "{} · 조 제목 변경: {} → {}",
            v.pair, v.old_title, v.new_title
        )
    } else {
        String::new()
    };
    let unmatched_old = classify_unmatched(
        &v.unmatched_old,
        &v.pair,
        "delete",
        false,
        &v.items,
        &v.new_parts,
    );
    let unmatched_new = classify_unmatched(
        &v.unmatched_new,
        &v.pair,
        "insert",
        true,
        &v.items,
        &v.old_parts,
    );
    let mut body_changed = unmatched_old
        .iter()
        .chain(unmatched_new.iter())
        .any(|d| d.action == "delete" || d.action == "insert");
    let structure_messages = structure_description(&v.old_parts, &v.new_parts);
    let structure_changed = !structure_messages.is_empty();
    let mut decisions = Vec::new();
    for i in &v.items {
        let label_changed = i.old_label != "본문"
            && i.new_label != "본문"
            && !i.old_label.is_empty()
            && !i.new_label.is_empty()
            && i.old_label != i.new_label;
        let same_ordinal =
            label_changed && i.old_ordinal.is_some() && i.old_ordinal == i.new_ordinal;
        let level_changed = i.old_level != i.new_level;
        let item_moved = !i.parent_equivalent || (label_changed && !same_ordinal);
        let content_changed = !i.content_equal;
        let message = if (label_changed && same_ordinal) || level_changed {
            format!(
                "{} · 항/호 표기{} 변경: {} → {}",
                v.pair,
                if level_changed { "/레벨" } else { "" },
                i.old_label,
                i.new_label
            )
        } else if item_moved {
            format!(
                "{} · {}: {} → {}",
                v.pair,
                if content_changed {
                    "항/호 이동+변경"
                } else {
                    "항/호 이동"
                },
                i.old_label,
                i.new_label
            )
        } else {
            String::new()
        };
        if content_changed {
            body_changed = true;
        }
        let plain = i.old_label == "본문"
            && i.new_label == "본문"
            && v.old_part_count == 1
            && v.new_part_count == 1
            && i.score < 0.72
            && !i.shared_edge
            && !i.substantial_sentence;
        let full_item = i.old_label != "본문"
            && i.new_label != "본문"
            && !i.old_label.is_empty()
            && !i.new_label.is_empty()
            && i.score <= 0.56
            && !i.review_anchors
            && !i.shared_edge;
        decisions.push(Decision {
            old: i.old,
            new: i.new,
            label_changed,
            content_changed,
            rewrite: content_changed && (plain || full_item),
            message,
        });
    }
    let substantive = title_changed || body_changed || structure_changed;
    let status = if article_moved {
        format!(
            "{} · 상태: {}",
            v.pair,
            if substantive {
                "조 이동+변경"
            } else {
                "조 이동"
            }
        )
    } else if v.legal_article {
        if structure_changed {
            format!("{} · 상태: 조 구조변경", v.pair)
        } else if title_changed || body_changed {
            format!("{} · 상태: 조 변경", v.pair)
        } else {
            String::new()
        }
    } else if substantive {
        let kind = if v.old_kind == "section" || v.new_kind == "section" {
            "장/절"
        } else {
            "본문 블록"
        };
        format!("{} · 상태: {} 변경", v.pair, kind)
    } else {
        String::new()
    };
    Result {
        article_moved,
        title_changed,
        body_changed,
        structure_changed,
        structure_messages,
        unmatched_old,
        unmatched_new,
        status,
        number_message,
        title_message,
        decisions,
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn unmatched_anchors_follow_existing_matched_neighbors() {
        let matched = vec![
            Item {
                old: 0,
                new: 0,
                old_label: "①".into(),
                new_label: "①".into(),
                old_level: 1,
                new_level: 1,
                old_ordinal: Some(1),
                new_ordinal: Some(1),
                parent_equivalent: true,
                content_equal: true,
                shared_edge: true,
                substantial_sentence: true,
                review_anchors: true,
                score: 1.0,
            },
            Item {
                old: 2,
                new: 1,
                old_label: "③".into(),
                new_label: "③".into(),
                old_level: 1,
                new_level: 1,
                old_ordinal: Some(3),
                new_ordinal: Some(3),
                parent_equivalent: true,
                content_equal: true,
                shared_edge: true,
                substantial_sentence: true,
                review_anchors: true,
                score: 1.0,
            },
        ];
        let target = vec![
            StructuralPart {
                label: "①".into(),
                level: 1,
                family: "circled".into(),
                start: 0,
                end: 9,
            },
            StructuralPart {
                label: "③".into(),
                level: 1,
                family: "circled".into(),
                start: 18,
                end: 26,
            },
        ];
        assert_eq!(gap_anchor(1, false, &matched, &target), 18);
        assert_eq!(gap_anchor(3, false, &matched, &target), 26);
        assert_eq!(gap_anchor(0, true, &matched, &target), 0);
        let unmatched = vec![Unmatched {
            index: 1,
            has_content: true,
            moved: false,
            source: "①".into(),
            target: "".into(),
        }];
        let result = classify_unmatched(&unmatched, "A↔B", "delete", false, &matched, &target);
        assert_eq!(result[0].action, "delete");
        assert_eq!(result[0].gap_anchor, 18);
    }
    #[test]
    fn structure_messages_detect_body_merge_and_level_transition() {
        let old = vec![
            StructuralPart {
                label: "①".into(),
                level: 1,
                family: "circled".into(),
                start: 0,
                end: 10,
            },
            StructuralPart {
                label: "②".into(),
                level: 1,
                family: "circled".into(),
                start: 10,
                end: 20,
            },
        ];
        let plain = vec![StructuralPart {
            label: "본문".into(),
            level: 0,
            family: "body".into(),
            start: 0,
            end: 20,
        }];
        assert!(structure_description(&old, &plain)[0].contains("본문으로 통합"));
        assert!(structure_description(&plain, &old)[0].contains("열거 구조로 분할"));
        let hierarchical = vec![StructuralPart {
            label: "①".into(),
            level: 2,
            family: "circled".into(),
            start: 0,
            end: 20,
        }];
        assert!(structure_description(&old, &hierarchical)[0].contains("구조 변경"));
    }
    #[test]
    fn moved_article_with_renumbered_item() {
        let req = Input {
            pair: "A↔B".into(),
            old_kind: "article".into(),
            new_kind: "article".into(),
            old_number: "8".into(),
            new_number: "7".into(),
            old_title: "Privacy".into(),
            new_title: "Privacy".into(),
            old_header: "Article 8".into(),
            new_header: "Article 7".into(),
            legal_article: true,
            titles_equivalent: true,
            headers_equivalent: false,
            headers_semantically_equal: false,
            old_parts: vec![StructuralPart {
                label: "①".into(),
                level: 1,
                family: "circled".into(),
                start: 0,
                end: 10,
            }],
            new_parts: vec![StructuralPart {
                label: "②".into(),
                level: 1,
                family: "circled".into(),
                start: 0,
                end: 10,
            }],
            old_part_count: 1,
            new_part_count: 1,
            unmatched_old: vec![],
            unmatched_new: vec![],
            items: vec![Item {
                old: 0,
                new: 0,
                old_label: "①".into(),
                new_label: "②".into(),
                old_level: 1,
                new_level: 1,
                old_ordinal: Some(1),
                new_ordinal: Some(2),
                parent_equivalent: true,
                content_equal: true,
                shared_edge: false,
                substantial_sentence: false,
                review_anchors: false,
                score: 1.0,
            }],
        };
        let r = plan(&req);
        assert!(r.article_moved);
        assert_eq!(r.status, "A↔B · 상태: 조 이동");
        assert!(r.decisions[0].label_changed);
        assert!(r.decisions[0].message.contains("항/호 이동"));
    }
}
