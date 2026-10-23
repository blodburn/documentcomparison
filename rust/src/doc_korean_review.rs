//! Korean short-review word edit alignment (V5.19.4.4k behavior).
//! The managed source still supplies UTF-16 lexing and morphological
//! pair relation costs; Rust independently performs the entire DP,
//! ordered traceback and evidence gate. C# owns only marker rendering.
use serde::{Deserialize, Serialize};
const MAX_WORDS: usize = 96;
#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Relation {
    pub kind: String,
    pub cost: f64,
}
#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Input {
    pub old_count: usize,
    pub new_count: usize,
    pub relations: Vec<Relation>,
}
#[derive(Debug, Serialize, PartialEq, Clone)]
#[serde(rename_all = "camelCase")]
pub struct WordOp {
    pub kind: String,
    pub old_index: Option<usize>,
    pub new_index: Option<usize>,
}
#[derive(Debug, Serialize)]
pub struct Result {
    pub supported: bool,
    pub ops: Vec<WordOp>,
}
pub fn validate(v: &Input) -> bool {
    v.old_count > 0
        && v.new_count > 0
        && v.old_count <= MAX_WORDS
        && v.new_count <= MAX_WORDS
        && v.relations.len() == v.old_count * v.new_count
        && v.relations.iter().all(|r| {
            matches!(r.kind.as_str(), "equal" | "fuzzy" | "none")
                && r.cost.is_finite()
                && (0.0..=3.0).contains(&r.cost)
        })
}
pub fn select(v: &Input) -> Result {
    let n = v.old_count;
    let m = v.new_count;
    let mut dp = vec![vec![0.0f64; m + 1]; n + 1];
    let mut previous = vec![vec![b' '; m + 1]; n + 1];
    const GAP: f64 = 0.68;
    for i in 1..=n {
        dp[i][0] = dp[i - 1][0] + GAP;
        previous[i][0] = b'D';
    }
    for j in 1..=m {
        dp[0][j] = dp[0][j - 1] + GAP;
        previous[0][j] = b'I';
    }
    for i in 1..=n {
        for j in 1..=m {
            let cost = v.relations[(i - 1) * m + (j - 1)].cost;
            let matching = dp[i - 1][j - 1] + cost;
            let deleting = dp[i - 1][j] + GAP;
            let inserting = dp[i][j - 1] + GAP;
            if matching <= deleting && matching <= inserting {
                dp[i][j] = matching;
                previous[i][j] = b'M';
            } else if deleting <= inserting {
                dp[i][j] = deleting;
                previous[i][j] = b'D';
            } else {
                dp[i][j] = inserting;
                previous[i][j] = b'I';
            }
        }
    }
    let (mut x, mut y) = (n, m);
    let mut ops = vec![];
    while x > 0 || y > 0 {
        match previous[x][y] {
            b'M' => {
                ops.push(WordOp {
                    kind: v.relations[(x - 1) * m + y - 1].kind.clone(),
                    old_index: Some(x - 1),
                    new_index: Some(y - 1),
                });
                x -= 1;
                y -= 1;
            }
            b'D' => {
                ops.push(WordOp {
                    kind: "delete".into(),
                    old_index: Some(x - 1),
                    new_index: None,
                });
                x -= 1;
            }
            _ => {
                ops.push(WordOp {
                    kind: "insert".into(),
                    old_index: None,
                    new_index: Some(y - 1),
                });
                y -= 1;
            }
        }
    }
    ops.reverse();
    let equal = ops.iter().filter(|op| op.kind == "equal").count();
    let fuzzy = ops.iter().filter(|op| op.kind == "fuzzy").count();
    if equal < 2 || fuzzy < 2 || ops.iter().any(|op| op.kind == "none") {
        return Result {
            supported: false,
            ops: vec![],
        };
    }
    Result {
        supported: true,
        ops,
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn korean_fuzzy_multiple_changes() {
        let a = vec!["개인정보", "동의서", "제출기한", "요구"];
        let b = vec!["개인정보", "동의서는", "제출기한을", "요구"];
        let mut relations = vec![];
        for x in &a {
            for y in &b {
                relations.push(Relation {
                    kind: if x == y {
                        "equal"
                    } else if x.starts_with("동의") && y.starts_with("동의")
                        || x.starts_with("제출") && y.starts_with("제출")
                    {
                        "fuzzy"
                    } else {
                        "none"
                    }
                    .into(),
                    cost: if x == y {
                        0.0
                    } else if x.starts_with("동의") && y.starts_with("동의")
                        || x.starts_with("제출") && y.starts_with("제출")
                    {
                        0.27
                    } else {
                        1.45
                    },
                });
            }
        }
        let out = select(&Input {
            old_count: 4,
            new_count: 4,
            relations,
        });
        assert!(out.supported);
        assert_eq!(out.ops.len(), 4);
    }
}
