//! Article/unit lineage selection. All ordering, anchor, dynamic programming,
//! non-monotonic movement and non-article gap alignment happens here.
//! C# supplies each source unit and its canonical fuzzy text scores;
//! the full selected result is verified against the previous C# algorithm.
use serde::{Deserialize, Serialize};
use std::cmp::Ordering;
use std::collections::{HashMap, HashSet};

const MAX_UNITS: usize = 64;
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Unit {
    pub article: bool,
    pub number: String,
    pub title: String,
    pub title_key: String,
    pub body_key: String,
    pub low_information_title: bool,
}
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Feature {
    pub title: f64,
    pub body: f64,
    pub lineage: f64,
    pub non_article_score: f64,
    pub non_article_threshold: f64,
}
#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Input {
    pub old: Vec<Unit>,
    pub new: Vec<Unit>,
    pub features: Vec<Feature>,
}
#[derive(Debug, Serialize, Clone, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct Match {
    pub base: usize,
    pub other: usize,
    pub score: f64,
    pub mode: String,
}
#[derive(Debug, Serialize)]
pub struct Result {
    pub matches: Vec<Match>,
}
pub fn validate(v: &Input) -> bool {
    let n = v.old.len();
    let m = v.new.len();
    n <= MAX_UNITS
        && m <= MAX_UNITS
        && v.features.len() == n * m
        && v.old.iter().chain(v.new.iter()).all(|u| {
            u.number.len() <= 120
                && u.title.len() <= 8_192
                && u.title_key.len() <= 8_192
                && u.body_key.len() <= 8_192
        })
        && v.features.iter().all(|f| {
            [f.title, f.body, f.lineage, f.non_article_score]
                .iter()
                .all(|x| x.is_finite() && (0.0..=1.0).contains(x))
                && f.non_article_threshold.is_finite()
                && (0.0..=2.0).contains(&f.non_article_threshold)
        })
}
fn name_eq(a: &str, b: &str) -> bool {
    // C# OrdinalIgnoreCase: safe exact for common ASCII article numbers.
    a.eq_ignore_ascii_case(b) || a == b
}
fn ranked(a: f64, b: f64) -> Ordering {
    b.partial_cmp(&a).unwrap_or(Ordering::Equal)
}
#[derive(Clone, Copy)]
struct Evidence {
    sim: f64,
    title: f64,
    body: f64,
    ambiguous: bool,
}
fn calculate_evidence(
    v: &Input,
    i: usize,
    j: usize,
    best_old: &[f64],
    best_new: &[f64],
    repeated_a: &HashSet<&str>,
    repeated_b: &HashSet<&str>,
) -> Evidence {
    let a = &v.old[i];
    let b = &v.new[j];
    let f = &v.features[i * v.new.len() + j];
    let same = name_eq(&a.number, &b.number);
    let weak = f.title >= 0.82
        && a.low_information_title
        && b.low_information_title
        && !same
        && f.body < 0.72;
    let title = if weak { f.title.min(0.34) } else { f.title };
    let mut sim = if weak { f.lineage.min(0.42) } else { f.lineage };
    let repeated = !a.body_key.is_empty()
        && a.body_key == b.body_key
        && (repeated_a.contains(a.body_key.as_str()) || repeated_b.contains(b.body_key.as_str()));
    let title_conflict = !a.title.is_empty() && !b.title.is_empty() && title < 0.35;
    let elsewhere = title_conflict
        && ((best_old[i] >= 0.72 && best_old[i] >= title + 0.20)
            || (best_new[j] >= 0.72 && best_new[j] >= title + 0.20));
    let ambiguous = weak || (title_conflict && f.body >= 0.84 && (repeated || elsewhere));
    if ambiguous {
        sim = sim.min(0.42);
    }
    Evidence {
        sim,
        title,
        body: f.body,
        ambiguous,
    }
}
fn align_dp<F>(a: &[usize], b: &[usize], mut match_cost: F) -> Vec<(usize, usize)>
where
    F: FnMut(usize, usize) -> f64,
{
    const GAP: f64 = 0.48;
    let (n, m) = (a.len(), b.len());
    let mut prev = vec![vec![0u8; m + 1]; n + 1];
    let mut prior = vec![0.0; m + 1];
    let mut current = vec![0.0; m + 1];
    for j in 1..=m {
        prior[j] = prior[j - 1] + GAP;
        prev[0][j] = 3;
    }
    for i in 1..=n {
        current[0] = prior[0] + GAP;
        prev[i][0] = 2;
        for j in 1..=m {
            let mc = prior[j - 1] + match_cost(a[i - 1], b[j - 1]);
            let dc = prior[j] + GAP;
            let ic = current[j - 1] + GAP;
            if mc <= dc && mc <= ic {
                current[j] = mc;
                prev[i][j] = 1;
            } else if dc <= ic {
                current[j] = dc;
                prev[i][j] = 2;
            } else {
                current[j] = ic;
                prev[i][j] = 3;
            }
        }
        std::mem::swap(&mut prior, &mut current);
    }
    let (mut x, mut y) = (n, m);
    let mut matches = vec![];
    while x > 0 || y > 0 {
        match prev[x][y] {
            1 => {
                matches.push((a[x - 1], b[y - 1]));
                x -= 1;
                y -= 1;
            }
            2 => x -= 1,
            _ => y -= 1,
        }
    }
    matches.reverse();
    matches
}
fn repeated_keys<'a>(v: &'a [Unit], indices: &[usize]) -> HashSet<&'a str> {
    let mut seen = HashSet::new();
    let mut repeated = HashSet::new();
    for &i in indices {
        let key = v[i].body_key.as_str();
        if !key.is_empty() && !seen.insert(key) {
            repeated.insert(key);
        }
    }
    repeated
}
pub fn match_units(v: &Input) -> Result {
    let (n, m) = (v.old.len(), v.new.len());
    let f = |i: usize, j: usize| &v.features[i * m + j];
    let aa: Vec<usize> = (0..n).filter(|&i| v.old[i].article).collect();
    let bb: Vec<usize> = (0..m).filter(|&j| v.new[j].article).collect();
    if aa.is_empty() || bb.is_empty() {
        return Result { matches: vec![] }; // source caller uses MatchGenericUnits
    }
    let repeated_a = repeated_keys(&v.old, &aa);
    let repeated_b = repeated_keys(&v.new, &bb);
    let mut best_old = vec![0.0_f64; n];
    let mut best_new = vec![0.0_f64; m];
    for &i in &aa {
        for &j in &bb {
            best_old[i] = best_old[i].max(f(i, j).title);
            best_new[j] = best_new[j].max(f(i, j).title);
        }
    }
    let evidence = |i: usize, j: usize| {
        calculate_evidence(v, i, j, &best_old, &best_new, &repeated_a, &repeated_b)
    };
    let mut strong = vec![];
    for &i in &aa {
        if v.old[i].title.is_empty() {
            continue;
        }
        let mut ranked_b = bb.clone();
        ranked_b.sort_by(|&j, &k| ranked(f(i, j).title, f(i, k).title).then_with(|| j.cmp(&k)));
        let Some(&j) = ranked_b.first() else {
            continue;
        };
        if f(i, j).title < 0.82 || v.new[j].title.is_empty() {
            continue;
        }
        let mut ranked_a = aa.clone();
        ranked_a.sort_by(|&x, &y| ranked(f(x, j).title, f(y, j).title).then_with(|| x.cmp(&y)));
        if ranked_a.first() != Some(&i) {
            continue;
        }
        let unique_a = ranked_b.len() == 1 || f(i, j).title - f(i, ranked_b[1]).title >= 0.10;
        let unique_b = ranked_a.len() == 1 || f(i, j).title - f(ranked_a[1], j).title >= 0.10;
        let key = v.old[i].title_key.as_str();
        let exact = !key.is_empty()
            && key == v.new[j].title_key
            && aa.iter().filter(|&&x| v.old[x].title_key == key).count() == 1
            && bb.iter().filter(|&&y| v.new[y].title_key == key).count() == 1;
        let low = v.old[i].low_information_title && v.new[j].low_information_title;
        let same = name_eq(&v.old[i].number, &v.new[j].number);
        if ((unique_a && unique_b) || exact) && (!low || same || f(i, j).body >= 0.72) {
            strong.push((i, j, f(i, j).title));
        }
    }
    let strong_a: HashSet<usize> = strong.iter().map(|x| x.0).collect();
    let strong_b: HashSet<usize> = strong.iter().map(|x| x.1).collect();
    let dp_a: Vec<usize> = aa
        .iter()
        .copied()
        .filter(|i| !strong_a.contains(i))
        .collect();
    let dp_b: Vec<usize> = bb
        .iter()
        .copied()
        .filter(|j| !strong_b.contains(j))
        .collect();
    let ops = align_dp(&dp_a, &dp_b, |i, j| {
        let sim = evidence(i, j).sim;
        let mut cost = if sim >= 0.43 {
            0.94 * (1.0 - sim)
        } else {
            1.08
        };
        if name_eq(&v.old[i].number, &v.new[j].number) {
            cost -= 0.015;
        }
        cost
    });
    let mut mapping = HashMap::new();
    let mut used_b = strong_b.clone();
    for (i, j, title_score) in strong {
        mapping.insert(
            i,
            Match {
                base: i,
                other: j,
                score: title_score.max(f(i, j).lineage),
                mode: if name_eq(&v.old[i].number, &v.new[j].number) {
                    "same".into()
                } else {
                    "moved".into()
                },
            },
        );
    }
    for (i, j) in ops {
        let sim = evidence(i, j).sim;
        if sim < 0.43 {
            continue;
        }
        mapping.insert(
            i,
            Match {
                base: i,
                other: j,
                score: sim,
                mode: if name_eq(&v.old[i].number, &v.new[j].number) {
                    "same".into()
                } else {
                    "renumbered".into()
                },
            },
        );
        used_b.insert(j);
    }
    let mut residual = vec![];
    for &i in &aa {
        if mapping.contains_key(&i) {
            continue;
        }
        for &j in &bb {
            if used_b.contains(&j) {
                continue;
            }
            let e = evidence(i, j);
            if e.sim >= 0.70
                || e.title >= 0.82
                || (e.title >= 0.68 && e.body >= 0.48)
                || (!e.ambiguous && e.body >= 0.84)
            {
                residual.push((e.sim + 0.10 * e.title + 0.04 * e.body, i, j, e.sim));
            }
        }
    }
    residual.sort_by(|a, b| ranked(a.0, b.0));
    for (_, i, j, sim) in residual {
        if mapping.contains_key(&i) || used_b.contains(&j) {
            continue;
        }
        mapping.insert(
            i,
            Match {
                base: i,
                other: j,
                score: sim,
                mode: "moved".into(),
            },
        );
        used_b.insert(j);
    }
    let mut ordered: Vec<_> = mapping.values().cloned().collect();
    ordered.sort_by_key(|m| m.base);
    let mut moved = HashSet::new();
    for (x, i) in ordered.iter().enumerate() {
        for j in ordered.iter().skip(x + 1) {
            if i.other > j.other {
                moved.insert(i.base);
                moved.insert(j.base);
            }
        }
    }
    for i in moved {
        if let Some(entry) = mapping.get_mut(&i) {
            entry.mode = "moved".into();
        }
    }
    // C# MatchNonArticleUnits: align preamble and section nodes separately
    // with their original two-row DP and per-pair threshold.
    let non_a: Vec<usize> = (0..n).filter(|&i| !v.old[i].article).collect();
    let non_b: Vec<usize> = (0..m)
        .filter(|&j| !v.new[j].article && !used_b.contains(&j))
        .collect();
    let non_ops = align_dp(&non_a, &non_b, |i, j| {
        let feat = f(i, j);
        if feat.non_article_score >= feat.non_article_threshold {
            0.94 * (1.0 - feat.non_article_score)
        } else {
            1.08
        }
    });
    for (i, j) in non_ops {
        let feat = f(i, j);
        if feat.non_article_score >= feat.non_article_threshold {
            mapping.insert(
                i,
                Match {
                    base: i,
                    other: j,
                    score: feat.non_article_score,
                    mode: "same".into(),
                },
            );
        }
    }
    let mut matches: Vec<_> = mapping.into_values().collect();
    matches.sort_by_key(|m| m.base);
    Result { matches }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn u(n: &str, t: &str) -> Unit {
        Unit {
            article: true,
            number: n.into(),
            title: t.into(),
            title_key: t.into(),
            body_key: String::new(),
            low_information_title: false,
        }
    }
    fn f(title: f64, body: f64, lineage: f64) -> Feature {
        Feature {
            title,
            body,
            lineage,
            non_article_score: 0.0,
            non_article_threshold: 2.0,
        }
    }
    #[test]
    fn inserts_and_renumbers_articles_using_exact_titles() {
        let a = vec![u("1", "Purposes"), u("2", "Terms")];
        let b = vec![u("1", "New"), u("2", "Purposes"), u("3", "Terms")];
        let features = vec![
            f(0.0, 0.0, 0.1),
            f(1.0, 0.9, 0.95),
            f(0.0, 0.1, 0.1),
            f(0.0, 0.0, 0.1),
            f(0.0, 0.1, 0.1),
            f(1.0, 0.9, 0.95),
        ];
        let res = match_units(&Input {
            old: a,
            new: b,
            features,
        });
        assert_eq!(
            res.matches
                .iter()
                .map(|x| (x.base, x.other))
                .collect::<Vec<_>>(),
            vec![(0, 1), (1, 2)]
        );
    }
}
