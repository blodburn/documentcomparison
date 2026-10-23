//! DOCX table/row/cell lineage correspondence port from MatchStructuredUnits.
//! Input cells carry C#-normalized body keys and original table/row indices.
//! This Rust matcher only matches table cells to table cells, using unique
//! row/cell content as anchors and equal-length monotonic gaps positionally.
use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, HashSet};

const MAX_CELLS: usize = 256;
#[derive(Debug, Deserialize, Clone)]
#[serde(rename_all = "camelCase")]
pub struct Cell {
    pub index: usize,
    pub table: i32,
    pub row: i32,
    pub key: String,
}
#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Input {
    pub old: Vec<Cell>,
    pub new: Vec<Cell>,
}
#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Pair {
    pub base: usize,
    pub other: usize,
}
#[derive(Debug, Serialize)]
pub struct Result {
    pub pairs: Vec<Pair>,
}
pub fn validate(v: &Input) -> bool {
    v.old.len() <= MAX_CELLS
        && v.new.len() <= MAX_CELLS
        && v.old
            .iter()
            .chain(v.new.iter())
            .all(|c| c.index < 10_000 && c.table >= 0 && c.row >= 0 && c.key.len() <= 16_384)
        && {
            let a: HashSet<_> = v.old.iter().map(|c| c.index).collect();
            let b: HashSet<_> = v.new.iter().map(|c| c.index).collect();
            a.len() == v.old.len() && b.len() == v.new.len()
        }
}
fn pair_group(old: &[String], new: &[String]) -> Vec<(usize, usize)> {
    let mut matched = vec![];
    let mut used_b = HashSet::new();
    for (i, key) in old.iter().enumerate() {
        if key.is_empty()
            || old.iter().filter(|x| *x == key).count() != 1
            || new.iter().filter(|x| *x == key).count() != 1
        {
            continue;
        }
        let Some(j) = new.iter().position(|v| v == key) else {
            continue;
        };
        matched.push((i, j));
        used_b.insert(j);
    }
    let mut anchors = vec![(None, None)];
    anchors.extend(matched.iter().map(|&(i, j)| (Some(i), Some(j))));
    anchors.push((Some(old.len()), Some(new.len())));
    for k in 1..anchors.len() {
        let prev = anchors[k - 1];
        let next = anchors[k];
        let pa = prev.0.map_or(-1, |x| x as i32);
        let pb = prev.1.map_or(-1, |x| x as i32);
        let qa = next.0.unwrap_or(old.len()) as i32;
        let qb = next.1.unwrap_or(new.len()) as i32;
        if qb <= pb {
            continue;
        }
        let old_gap: Vec<_> = ((pa + 1)..qa)
            .map(|x| x as usize)
            .filter(|i| !matched.iter().any(|x| x.0 == *i))
            .collect();
        let new_gap: Vec<_> = ((pb + 1)..qb)
            .map(|x| x as usize)
            .filter(|j| !used_b.contains(j))
            .collect();
        if old_gap.len() != new_gap.len() {
            continue;
        }
        for (i, j) in old_gap.into_iter().zip(new_gap) {
            if used_b.insert(j) {
                matched.push((i, j));
            }
        }
    }
    matched
}
#[derive(Debug)]
struct Table {
    cells: Vec<Cell>,
}
impl Table {
    fn rows(&self) -> Vec<Vec<&Cell>> {
        let mut groups: BTreeMap<i32, Vec<&Cell>> = BTreeMap::new();
        for c in &self.cells {
            groups.entry(c.row).or_default().push(c);
        }
        groups.into_values().collect()
    }
    fn keys(&self) -> HashSet<&str> {
        self.cells
            .iter()
            .map(|c| c.key.as_str())
            .filter(|v| !v.is_empty())
            .collect()
    }
}
fn tables(items: &[Cell]) -> Vec<Table> {
    let mut grouped: BTreeMap<i32, Vec<Cell>> = BTreeMap::new();
    for cell in items {
        grouped.entry(cell.table).or_default().push(cell.clone());
    }
    grouped.into_values().map(|cells| Table { cells }).collect()
}
pub fn match_tables(v: &Input) -> Result {
    let a = tables(&v.old);
    let b = tables(&v.new);
    let mut candidates = vec![];
    for (i, old) in a.iter().enumerate() {
        for (j, new) in b.iter().enumerate() {
            let oa = old.keys();
            let nb = new.keys();
            let shared = oa.intersection(&nb).count();
            let overlap = if oa.is_empty() && nb.is_empty() {
                1.0
            } else {
                shared as f64 / oa.len().max(nb.len()) as f64
            };
            if overlap < 0.20 && !(a.len() == 1 && b.len() == 1) {
                continue;
            }
            candidates.push((i, j, overlap + if i == j { 0.01 } else { 0.0 }));
        }
    }
    candidates.sort_by(|x, y| {
        y.2.total_cmp(&x.2)
            .then_with(|| x.0.abs_diff(x.1).cmp(&y.0.abs_diff(y.1)))
            .then_with(|| x.0.cmp(&y.0))
            .then_with(|| x.1.cmp(&y.1))
    });
    let mut used_a = HashSet::new();
    let mut used_b = HashSet::new();
    let mut pairs = vec![];
    for (i, j, _) in candidates {
        if used_a.contains(&i) || used_b.contains(&j) {
            continue;
        }
        used_a.insert(i);
        used_b.insert(j);
        let old_rows = a[i].rows();
        let new_rows = b[j].rows();
        let rowkey = |cells: &Vec<&Cell>| {
            cells
                .iter()
                .map(|c| c.key.as_str())
                .collect::<Vec<_>>()
                .join("¦")
        };
        let old_keys = old_rows.iter().map(&rowkey).collect::<Vec<_>>();
        let new_keys = new_rows.iter().map(&rowkey).collect::<Vec<_>>();
        for (ar, br) in pair_group(&old_keys, &new_keys) {
            let old_cells = &old_rows[ar];
            let new_cells = &new_rows[br];
            let old_keys = old_cells.iter().map(|c| c.key.clone()).collect::<Vec<_>>();
            let new_keys = new_cells.iter().map(|c| c.key.clone()).collect::<Vec<_>>();
            for (ac, bc) in pair_group(&old_keys, &new_keys) {
                pairs.push(Pair {
                    base: old_cells[ac].index,
                    other: new_cells[bc].index,
                });
            }
        }
    }
    pairs.sort_by_key(|p| p.base);
    Result { pairs }
}
#[cfg(test)]
mod tests {
    use super::*;
    fn c(i: usize, t: i32, r: i32, k: &str) -> Cell {
        Cell {
            index: i,
            table: t,
            row: r,
            key: k.into(),
        }
    }
    #[test]
    fn moved_unique_rows_and_inserted_columns() {
        let input = Input {
            old: vec![
                c(0, 0, 0, "name"),
                c(1, 0, 0, "value"),
                c(2, 0, 1, "A"),
                c(3, 0, 1, "1"),
                c(4, 0, 2, "B"),
                c(5, 0, 2, "2"),
            ],
            new: vec![
                c(0, 0, 0, "name"),
                c(1, 0, 0, "value"),
                c(2, 0, 1, "B"),
                c(3, 0, 1, "2"),
                c(4, 0, 2, "A"),
                c(5, 0, 2, "1"),
            ],
        };
        let result = match_tables(&input);
        assert_eq!(
            result
                .pairs
                .iter()
                .map(|x| (x.base, x.other))
                .collect::<Vec<_>>(),
            vec![(0, 0), (1, 1), (2, 4), (3, 5), (4, 2), (5, 3)]
        );
    }
    #[test]
    fn duplicated_ambiguous_cells_never_match_through_new_column() {
        let input = Input {
            old: vec![c(0, 0, 0, "X"), c(1, 0, 0, "X")],
            new: vec![c(0, 0, 0, "X"), c(1, 0, 0, "new"), c(2, 0, 0, "X")],
        };
        assert!(match_tables(&input).pairs.is_empty());
    }
}
