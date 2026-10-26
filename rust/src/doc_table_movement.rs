//! Table and row reorder classification from already aligned document rows.
//! All crossing / semantic movement selection is native.
use serde::{Deserialize, Serialize};
use std::collections::HashSet;
#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Location {
    pub index: i32,
    pub table: Option<i32>,
    pub row: Option<i32>,
    pub column: Option<i32>,
}
#[derive(Clone, Debug, Deserialize)]
pub struct Pair {
    pub old: Location,
    pub new: Location,
}
#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Group {
    pub old_doc: usize,
    pub new_doc: usize,
    pub pairs: Vec<Pair>,
}
#[derive(Debug, Deserialize)]
pub struct Input {
    pub groups: Vec<Group>,
}
#[derive(Debug, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct Move {
    pub old_doc: usize,
    pub new_doc: usize,
    pub old_index: i32,
    pub new_index: i32,
    pub reason: String,
}
#[derive(Debug, Serialize)]
pub struct Result {
    pub moves: Vec<Move>,
}
pub fn validate(v: &Input) -> bool {
    v.groups.len() <= 3
        && v.groups.iter().all(|g| {
            g.old_doc < 3
                && g.new_doc < 3
                && g.old_doc != g.new_doc
                && g.pairs.len() <= 4096
                && g.pairs.iter().all(|p| {
                    [p.old.index, p.new.index].iter().all(|x| *x >= 0)
                        && [&p.old, &p.new]
                            .iter()
                            .all(|l| l.table.is_none() || (l.row.is_some() && l.column.is_some()))
                })
        })
}
pub fn classify(v: &Input) -> Result {
    let mut moves = vec![];
    for g in &v.groups {
        let paired = &g.pairs;
        let plain = paired
            .iter()
            .filter(|p| p.old.table.is_none() && p.new.table.is_none())
            .collect::<Vec<_>>();
        let cells = paired
            .iter()
            .filter(|p| p.old.table.is_some() && p.new.table.is_some())
            .collect::<Vec<_>>();
        let mut groups: Vec<(i32, i32, Vec<&Pair>)> = vec![];
        for p in &cells {
            let pair = (p.old.table.unwrap(), p.new.table.unwrap());
            if let Some(entry) = groups.iter_mut().find(|x| x.0 == pair.0 && x.1 == pair.1) {
                entry.2.push(p);
            } else {
                groups.push((pair.0, pair.1, vec![p]));
            }
        }
        for (ot, nt, group) in groups {
            let first = group[0];
            let moved_across_text = plain
                .iter()
                .any(|p| (first.old.index < p.old.index) != (first.new.index < p.new.index));
            let moved_across_table = cells.iter().any(|p| {
                p.old.table != Some(ot)
                    && p.new.table != Some(nt)
                    && (first.old.index < p.old.index) != (first.new.index < p.new.index)
            });
            let table_moved = moved_across_text || moved_across_table;
            let mut rows = vec![];
            let mut seen = HashSet::new();
            for p in &group {
                let key = (p.old.row.unwrap(), p.new.row.unwrap());
                if seen.insert(key) {
                    rows.push((key, p));
                }
            }
            for (row, _) in &rows {
                let moved_row = rows.iter().any(|(other, _)| {
                    other.0 != row.0 && other.1 != row.1 && (other.0 < row.0) != (other.1 < row.1)
                });
                let reason = if table_moved {
                    "표 이동"
                } else if moved_row {
                    "표 행 이동"
                } else {
                    continue;
                };
                let first_cell = group
                    .iter()
                    .filter(|p| p.old.row == Some(row.0) && p.new.row == Some(row.1))
                    .min_by_key(|p| p.old.column.unwrap())
                    .unwrap();
                moves.push(Move {
                    old_doc: g.old_doc,
                    new_doc: g.new_doc,
                    old_index: first_cell.old.index,
                    new_index: first_cell.new.index,
                    reason: reason.into(),
                });
            }
        }
    }
    Result { moves }
}
#[cfg(test)]
mod tests {
    use super::*;
    fn c(index: i32, row: i32) -> Location {
        Location {
            index,
            table: Some(0),
            row: Some(row),
            column: Some(0),
        }
    }
    #[test]
    fn swapped_rows_have_one_movement_marker_each() {
        let v = Input {
            groups: vec![Group {
                old_doc: 0,
                new_doc: 1,
                pairs: vec![
                    Pair {
                        old: c(0, 0),
                        new: c(1, 1),
                    },
                    Pair {
                        old: c(1, 1),
                        new: c(0, 0),
                    },
                ],
            }],
        };
        let result = classify(&v);
        assert_eq!(result.moves.len(), 2);
        assert!(result.moves.iter().all(|m| m.reason == "표 행 이동"));
    }
    #[test]
    fn table_crossing_prose_marks_table_move() {
        let txt = |index| Location {
            index,
            table: None,
            row: None,
            column: None,
        };
        let v = Input {
            groups: vec![Group {
                old_doc: 1,
                new_doc: 0,
                pairs: vec![
                    Pair {
                        old: c(0, 0),
                        new: c(2, 0),
                    },
                    Pair {
                        old: txt(2),
                        new: txt(0),
                    },
                ],
            }],
        };
        let result = classify(&v);
        assert_eq!(result.moves[0].reason, "표 이동");
    }
}
