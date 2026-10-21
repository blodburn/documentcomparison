//! Detect uniquely identifiable code declarations moved between source files.
//! The native folder diff still owns all file-level results: this is a separate,
//! bounded syntactic layer and never removes ordinary additions/deletions.
use crate::{
    ast::{self, Language, Symbol},
    folder::{self, FileChange},
};
use serde::Serialize;
use std::{
    collections::{BTreeMap, BTreeSet},
    fs,
    io::{self, Read},
    path::{Path, PathBuf},
};

const MAX_CANDIDATE_FILES: usize = 512;
const MAX_AST_TOTAL_BYTES: u64 = 32 * 1024 * 1024;
const MAX_AST_FILE_BYTES: u64 = 2 * 1024 * 1024;
const MAX_SYMBOLS: usize = 15_000;

#[derive(Debug, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct CrossFileMove {
    pub symbol_kind: String,
    pub symbol_name: String,
    pub before_path: String,
    pub after_path: String,
    pub before_start: usize,
    pub before_end: usize,
    pub after_start: usize,
    pub after_end: usize,
}

#[derive(Debug, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct CrossFileReport {
    pub moves: Vec<CrossFileMove>,
    pub analyzed_files: usize,
    pub skipped_files: usize,
}

struct Located {
    path: String,
    language: Language,
    symbol: Symbol,
}

fn code_language(path: &str) -> Option<Language> {
    let path = path.rsplit('.').next()?.to_ascii_lowercase();
    match path.as_str() {
        "rs" => Some(Language::Rust),
        "cs" => Some(Language::CSharp),
        _ => None,
    }
}

fn collect(
    paths: &BTreeSet<String>,
    inventory: &BTreeMap<String, PathBuf>,
    used_bytes: &mut u64,
    analyzed_files: &mut usize,
    skipped_files: &mut usize,
    output: &mut Vec<Located>,
) -> io::Result<()> {
    for path in paths {
        let Some(language) = code_language(path) else {
            continue;
        };
        let absolute = &inventory[path];
        let length = fs::metadata(absolute)?.len();
        if length > MAX_AST_FILE_BYTES {
            *skipped_files += 1;
            continue;
        }
        if used_bytes.saturating_add(length) > MAX_AST_TOTAL_BYTES {
            return Err(io::Error::other("cross-file AST byte budget exceeded"));
        }
        // File metadata can be stale if an editor writes during comparison.
        // Bound the actual read, not just the pre-read metadata value.
        let mut reader = fs::File::open(absolute)?.take(MAX_AST_FILE_BYTES + 1);
        let mut source = String::new();
        match reader.read_to_string(&mut source) {
            Ok(_) => {}
            Err(error) if error.kind() == io::ErrorKind::InvalidData => {
                *skipped_files += 1;
                continue;
            }
            Err(error) => return Err(error),
        }
        if source.len() as u64 > MAX_AST_FILE_BYTES {
            *skipped_files += 1;
            continue;
        }
        *used_bytes += source.len() as u64;
        if *used_bytes > MAX_AST_TOTAL_BYTES {
            return Err(io::Error::other("cross-file AST byte budget exceeded"));
        }
        let symbols = match ast::declarations(language, &source) {
            Ok(symbols) => symbols,
            Err(_) => {
                *skipped_files += 1;
                continue;
            }
        };
        *analyzed_files += 1;
        if output.len() + symbols.len() > MAX_SYMBOLS {
            return Err(io::Error::other("cross-file AST symbol budget exceeded"));
        }
        output.extend(symbols.into_iter().map(|symbol| Located {
            path: path.clone(),
            language,
            symbol,
        }));
    }
    Ok(())
}

pub fn compare_folders(before: &Path, after: &Path) -> io::Result<CrossFileReport> {
    let changes = folder::compare_directories(before, after)?;
    let mut old_paths = BTreeSet::new();
    let mut new_paths = BTreeSet::new();
    // An exact-byte file rename was already captured by the file-level engine;
    // don't flood the review with redundant per-declaration move reports.
    for change in changes {
        match change {
            FileChange::Added(after) => {
                new_paths.insert(after);
            }
            FileChange::Deleted(before) => {
                old_paths.insert(before);
            }
            FileChange::Modified(path) => {
                old_paths.insert(path.clone());
                new_paths.insert(path);
            }
            FileChange::Renamed { .. } => {}
        }
    }
    // The AST budget is about code candidates, not unrelated binary assets
    // that may be part of the same folder comparison.
    old_paths.retain(|path| code_language(path).is_some());
    new_paths.retain(|path| code_language(path).is_some());
    let count = old_paths.len() + new_paths.len();
    if count > MAX_CANDIDATE_FILES {
        return Err(io::Error::other(
            "cross-file AST candidate file budget exceeded",
        ));
    }
    if count == 0 {
        return Ok(CrossFileReport {
            moves: Vec::new(),
            analyzed_files: 0,
            skipped_files: 0,
        });
    }
    let (old_files, new_files) = (folder::gather(before)?, folder::gather(after)?);
    let mut bytes = 0_u64;
    let (mut analyzed_files, mut skipped_files) = (0, 0);
    let (mut old, mut new) = (Vec::new(), Vec::new());
    collect(
        &old_paths,
        &old_files,
        &mut bytes,
        &mut analyzed_files,
        &mut skipped_files,
        &mut old,
    )?;
    collect(
        &new_paths,
        &new_files,
        &mut bytes,
        &mut analyzed_files,
        &mut skipped_files,
        &mut new,
    )?;

    // Exclude source declarations that remain at their original
    // path/qualified identity. Otherwise a modified method could be
    // mistaken for a newly moved method elsewhere.
    let mut old_same: BTreeMap<(&str, &str), Vec<usize>> = BTreeMap::new();
    let mut new_same: BTreeMap<(&str, &str), Vec<usize>> = BTreeMap::new();
    for (i, item) in old.iter().enumerate() {
        old_same
            .entry((&item.path, &item.symbol.identity))
            .or_default()
            .push(i);
    }
    for (i, item) in new.iter().enumerate() {
        new_same
            .entry((&item.path, &item.symbol.identity))
            .or_default()
            .push(i);
    }
    let (mut excluded_old, mut excluded_new) = (BTreeSet::new(), BTreeSet::new());
    for (identity, old_index) in old_same {
        let Some(new_index) = new_same.get(&identity) else {
            continue;
        };
        if old_index.len() == 1 && new_index.len() == 1 {
            excluded_old.insert(old_index[0]);
            excluded_new.insert(new_index[0]);
        }
    }

    // Strict complete-declaration text match: no inferred semantic equivalence.
    // Require language, declaration kind/name, and bytes all to match, and
    // a unique candidate in both versions. Empty/trivial bodies are excluded.
    type MatchKey<'a> = (u8, &'a str, &'a str, &'a str);
    let mut before_groups: BTreeMap<MatchKey<'_>, Vec<usize>> = BTreeMap::new();
    let mut after_groups: BTreeMap<MatchKey<'_>, Vec<usize>> = BTreeMap::new();
    fn key(item: &Located) -> MatchKey<'_> {
        (
            if item.language == Language::Rust {
                1
            } else {
                2
            },
            &item.symbol.kind,
            &item.symbol.name,
            &item.symbol.source,
        )
    }
    for (i, item) in old.iter().enumerate() {
        if !excluded_old.contains(&i) && ast::has_meaningful_body(&item.symbol.body) {
            before_groups.entry(key(item)).or_default().push(i);
        }
    }
    for (i, item) in new.iter().enumerate() {
        if !excluded_new.contains(&i) && ast::has_meaningful_body(&item.symbol.body) {
            after_groups.entry(key(item)).or_default().push(i);
        }
    }

    let mut moves = Vec::new();
    for (signature, left) in before_groups {
        let Some(right) = after_groups.get(&signature) else {
            continue;
        };
        if left.len() != 1 || right.len() != 1 {
            continue;
        }
        let a = &old[left[0]];
        let b = &new[right[0]];
        if a.path == b.path {
            continue;
        }
        moves.push(CrossFileMove {
            symbol_kind: a.symbol.kind.clone(),
            symbol_name: a.symbol.name.clone(),
            before_path: a.path.clone(),
            after_path: b.path.clone(),
            before_start: a.symbol.start,
            before_end: a.symbol.end,
            after_start: b.symbol.start,
            after_end: b.symbol.end,
        });
    }
    // A moved class contains unchanged method declarations; report the outer
    // class move and suppress nested method moves inside its same pair of files.
    moves.sort_by_key(|m| {
        (
            m.before_path.clone(),
            m.before_start,
            std::cmp::Reverse(m.before_end),
        )
    });
    let mut filtered: Vec<CrossFileMove> = Vec::new();
    for candidate in moves {
        let nested = filtered.iter().any(|outer| {
            outer.before_path == candidate.before_path
                && outer.after_path == candidate.after_path
                && outer.before_start <= candidate.before_start
                && candidate.before_end <= outer.before_end
                && outer.after_start <= candidate.after_start
                && candidate.after_end <= outer.after_end
        });
        if !nested {
            filtered.push(candidate);
        }
    }
    filtered.sort_by(|a, b| {
        (
            &a.after_path,
            a.after_start,
            &a.before_path,
            a.before_start,
            &a.symbol_name,
        )
            .cmp(&(
                &b.after_path,
                b.after_start,
                &b.before_path,
                b.before_start,
                &b.symbol_name,
            ))
    });

    Ok(CrossFileReport {
        moves: filtered,
        analyzed_files,
        skipped_files,
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::{AtomicU64, Ordering};
    static NEXT: AtomicU64 = AtomicU64::new(0);

    fn directories() -> (PathBuf, PathBuf) {
        let root = std::env::temp_dir().join(format!(
            "dc-cross-file-{}-{}",
            std::process::id(),
            NEXT.fetch_add(1, Ordering::Relaxed)
        ));
        let a = root.join("old");
        let b = root.join("new");
        fs::create_dir_all(&a).unwrap();
        fs::create_dir_all(&b).unwrap();
        (a, b)
    }
    fn cleanup(a: &Path) {
        fs::remove_dir_all(a.parent().unwrap()).unwrap();
    }

    #[test]
    fn function_moves_between_modified_files() {
        let (a, b) = directories();
        fs::write(
            a.join("left.rs"),
            "fn move_me() { let x = 42; }\nfn stay() { 1; }\n",
        )
        .unwrap();
        fs::write(a.join("right.rs"), "fn keep() { 2; }\n").unwrap();
        fs::write(b.join("left.rs"), "fn stay() { 1; }\n").unwrap();
        fs::write(
            b.join("right.rs"),
            "fn keep() { 2; }\nfn move_me() { let x = 42; }\n",
        )
        .unwrap();
        let result = compare_folders(&a, &b).unwrap();
        assert_eq!(result.moves.len(), 1);
        assert_eq!(result.moves[0].symbol_name, "move_me");
        assert_eq!(result.moves[0].before_path, "left.rs");
        assert_eq!(result.moves[0].after_path, "right.rs");
        cleanup(&a);
    }

    #[test]
    fn class_moves_without_redundant_nested_methods() {
        let (a, b) = directories();
        fs::write(
            a.join("old.cs"),
            "class Moving { void Work() { int n = 5; } }\nclass Stay {}\n",
        )
        .unwrap();
        fs::write(
            b.join("new.cs"),
            "class Moving { void Work() { int n = 5; } }\nclass Other {}\n",
        )
        .unwrap();
        let report = compare_folders(&a, &b).unwrap();
        assert_eq!(report.moves.len(), 1);
        assert_eq!(report.moves[0].symbol_kind, "class_declaration");
        cleanup(&a);
    }

    #[test]
    fn duplicate_declarations_cannot_prove_file_movement() {
        let (a, b) = directories();
        fs::write(a.join("a.rs"), "fn same() { let a = 9; }\n").unwrap();
        fs::write(a.join("b.rs"), "fn same() { let a = 9; }\n").unwrap();
        fs::write(b.join("c.rs"), "fn same() { let a = 9; }\n").unwrap();
        let report = compare_folders(&a, &b).unwrap();
        assert!(report.moves.is_empty());
        cleanup(&a);
    }

    #[test]
    fn changed_definition_is_not_a_move() {
        let (a, b) = directories();
        fs::write(a.join("a.rs"), "fn hello() { let a = 4; }\n").unwrap();
        fs::write(b.join("b.rs"), "fn hello() { let a = 5; }\n").unwrap();
        assert!(compare_folders(&a, &b).unwrap().moves.is_empty());
        cleanup(&a);
    }

    #[test]
    fn exact_file_rename_does_not_repeat_function_moves() {
        let (a, b) = directories();
        fs::write(a.join("a.rs"), "fn hello() { let a = 4; }\n").unwrap();
        fs::write(b.join("b.rs"), "fn hello() { let a = 4; }\n").unwrap();
        assert!(compare_folders(&a, &b).unwrap().moves.is_empty());
        cleanup(&a);
    }

    #[test]
    fn same_file_modified_declaration_is_not_misreported_as_moved() {
        let (a, b) = directories();
        fs::write(a.join("a.rs"), "fn same() { let x = 5; }\n").unwrap();
        fs::write(b.join("a.rs"), "fn same() { let x = 6; }\n").unwrap();
        assert!(compare_folders(&a, &b).unwrap().moves.is_empty());
        cleanup(&a);
    }

    #[test]
    fn non_code_file_changes_do_not_use_ast_budget() {
        let (a, b) = directories();
        for i in 0..260 {
            fs::write(b.join(format!("asset-{i}.png")), b"binary").unwrap();
        }
        let report = compare_folders(&a, &b).unwrap();
        assert!(report.moves.is_empty());
        assert_eq!(report.analyzed_files, 0);
        assert_eq!(report.skipped_files, 0);
        cleanup(&a);
    }

    #[test]
    fn malformed_source_partial_scan_not_claimed_complete() {
        let (a, b) = directories();
        fs::write(a.join("from.rs"), "fn old() {").unwrap();
        fs::write(b.join("to.rs"), "fn old() { let y = 100; }").unwrap();
        let report = compare_folders(&a, &b).unwrap();
        assert!(report.moves.is_empty());
        assert_eq!(report.skipped_files, 1);
        cleanup(&a);
    }

    #[test]
    fn excessive_code_file_candidates_stop_without_partial_move_claims() {
        let (a, b) = directories();
        for i in 0..513 {
            fs::write(b.join(format!("item_{i}.rs")), "fn work() { 123; }").unwrap();
        }
        assert!(compare_folders(&a, &b).is_err());
        cleanup(&a);
    }

    #[test]
    fn oversized_source_is_skipped_not_analyzed() {
        let (a, b) = directories();
        let oversized = format!(
            "//{}\\nfn large() {{ let n = 1; }}",
            "x".repeat((2 * 1024 * 1024) + 1)
        );
        fs::write(a.join("large.rs"), &oversized).unwrap();
        fs::write(b.join("large.rs"), "fn large() { let n = 2; }").unwrap();
        let report = compare_folders(&a, &b).unwrap();
        assert_eq!(report.skipped_files, 1);
        assert!(report.moves.is_empty());
        cleanup(&a);
    }

    #[test]
    fn syntax_errors_are_counted_not_silent_no_changes() {
        let (a, b) = directories();
        fs::write(a.join("a.rs"), "fn oops() {").unwrap();
        fs::write(b.join("a.rs"), "fn oops() { let n = 2; }").unwrap();
        let report = compare_folders(&a, &b).unwrap();
        assert!(report.moves.is_empty());
        assert_eq!(report.skipped_files, 1);
        assert_eq!(report.analyzed_files, 1);
        cleanup(&a);
    }
}
