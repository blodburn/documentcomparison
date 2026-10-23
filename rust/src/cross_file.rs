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
const MAX_REFACTOR_CANDIDATES: usize = 256;

#[derive(Debug, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct CrossFileMove {
    pub symbol_kind: String,
    pub symbol_name: String,
    pub before_name: String,
    pub after_name: String,
    pub signature_changed: bool,
    pub renamed: bool,
    pub before_path: String,
    pub after_path: String,
    pub before_start: usize,
    pub before_end: usize,
    pub after_start: usize,
    pub after_end: usize,
    /// True only for a unique signature match with a high-scoring edited body.
    pub content_changed: bool,
    /// Deterministic lexical Dice score in 0..=1000; exact text = 1000.
    pub similarity_permille: u16,
}

#[derive(Debug, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct CrossFileReport {
    pub moves: Vec<CrossFileMove>,
    pub analyzed_files: usize,
    pub skipped_files: usize,
    /// Matching renamed+signature-changed methods was omitted due to cost.
    pub refactor_stage_skipped: bool,
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

// Only an unchanged declaration header can link an edited function reliably;
// classes/interfaces, constructors and short stubs remain exact-only moves.
fn eligible_modified_move(symbol: &Symbol) -> bool {
    matches!(symbol.kind.as_str(), "function_item" | "method_declaration")
        && !symbol.signature.is_empty()
        && ast::has_meaningful_body(&symbol.body)
}

fn eligible_refactor_candidate(symbol: &Symbol) -> bool {
    eligible_modified_move(symbol) && {
        let tokens = lexical_tokens(&symbol.body);
        tokens.len() >= 20 && tokens.iter().collect::<BTreeSet<_>>().len() >= 12
    }
}

fn lexical_tokens(body: &str) -> Vec<String> {
    // Keep punctuation operators: comparing only identifier words would
    // consider e.g. == and != equivalent.
    let mut tokens = Vec::new();
    let mut current = String::new();
    let mut mode = 0_u8;
    for ch in body.chars() {
        let next_mode = if ch.is_alphanumeric() || ch == '_' {
            1
        } else if ch.is_whitespace() {
            0
        } else {
            2
        };
        if next_mode != mode && !current.is_empty() {
            tokens.push(std::mem::take(&mut current));
        }
        if next_mode != 0 {
            current.push(ch);
        }
        mode = next_mode;
    }
    if !current.is_empty() {
        tokens.push(current);
    }
    tokens
}

fn body_similarity(before: &str, after: &str) -> u16 {
    let left = lexical_tokens(before);
    let right = lexical_tokens(after);
    // Small generic functions are far too easy to correlate falsely.
    if left.len() < 12 || right.len() < 12 {
        return 0;
    }
    let mut counts = BTreeMap::<&str, usize>::new();
    for token in &left {
        *counts.entry(token).or_default() += 1;
    }
    let distinct = counts.len();
    if distinct < 8 {
        return 0;
    }
    let mut common = 0_usize;
    for token in &right {
        if let Some(count) = counts.get_mut(token.as_str())
            && *count > 0
        {
            *count -= 1;
            common += 1;
        }
    }
    (common * 2000 / (left.len() + right.len())) as u16
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
            refactor_stage_skipped: false,
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
    let (mut paired_old, mut paired_new) = (BTreeSet::new(), BTreeSet::new());
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
        paired_old.insert(left[0]);
        paired_new.insert(right[0]);
        moves.push(CrossFileMove {
            symbol_kind: a.symbol.kind.clone(),
            symbol_name: b.symbol.name.clone(),
            before_name: a.symbol.name.clone(),
            after_name: b.symbol.name.clone(),
            signature_changed: a.symbol.signature != b.symbol.signature,
            renamed: a.symbol.name != b.symbol.name,
            before_path: a.path.clone(),
            after_path: b.path.clone(),
            before_start: a.symbol.start,
            before_end: a.symbol.end,
            after_start: b.symbol.start,
            after_end: b.symbol.end,
            content_changed: false,
            similarity_permille: 1000,
        });
    }

    // Edited moves are intentionally a separate, stricter pass: require
    // exact declaration headers (name + arguments/modifiers), identical
    // language/kind, and a unique candidate on *both* sides. Never guess a
    // match by name or path alone, nor accept a short boilerplate body.
    type SignatureKey<'a> = (u8, &'a str, &'a str);
    fn signature_key(item: &Located) -> SignatureKey<'_> {
        (
            if item.language == Language::Rust {
                1
            } else {
                2
            },
            &item.symbol.kind,
            &item.symbol.signature,
        )
    }
    let mut old_signatures: BTreeMap<SignatureKey<'_>, Vec<usize>> = BTreeMap::new();
    let mut new_signatures: BTreeMap<SignatureKey<'_>, Vec<usize>> = BTreeMap::new();
    for (i, item) in old.iter().enumerate() {
        if !excluded_old.contains(&i)
            && !paired_old.contains(&i)
            && eligible_modified_move(&item.symbol)
        {
            old_signatures
                .entry(signature_key(item))
                .or_default()
                .push(i);
        }
    }
    for (i, item) in new.iter().enumerate() {
        if !excluded_new.contains(&i)
            && !paired_new.contains(&i)
            && eligible_modified_move(&item.symbol)
        {
            new_signatures
                .entry(signature_key(item))
                .or_default()
                .push(i);
        }
    }
    for (signature, left) in old_signatures {
        let Some(right) = new_signatures.get(&signature) else {
            continue;
        };
        if left.len() != 1 || right.len() != 1 {
            continue;
        }
        let a = &old[left[0]];
        let b = &new[right[0]];
        if a.path == b.path || a.symbol.source == b.symbol.source {
            continue;
        }
        let score = body_similarity(&a.symbol.body, &b.symbol.body);
        if score < 880 {
            continue;
        }
        paired_old.insert(left[0]);
        paired_new.insert(right[0]);
        moves.push(CrossFileMove {
            symbol_kind: a.symbol.kind.clone(),
            symbol_name: b.symbol.name.clone(),
            before_name: a.symbol.name.clone(),
            after_name: b.symbol.name.clone(),
            signature_changed: a.symbol.signature != b.symbol.signature,
            renamed: a.symbol.name != b.symbol.name,
            before_path: a.path.clone(),
            after_path: b.path.clone(),
            before_start: a.symbol.start,
            before_end: a.symbol.end,
            after_start: b.symbol.start,
            after_end: b.symbol.end,
            content_changed: true,
            similarity_permille: score,
        });
    }

    // Stage three: simultaneous name/signature changes. Only the *full
    // declaration body* may link these, and never an empty/boilerplate body.
    // Require globally unique body matches to prevent identical copied code
    // from being mistaken for a rename. For edited bodies use stricter lexical
    // matching and reject any competing high-similarity partner.
    let remaining_old: Vec<_> = old
        .iter()
        .enumerate()
        .filter(|(i, item)| {
            !excluded_old.contains(i)
                && !paired_old.contains(i)
                && eligible_refactor_candidate(&item.symbol)
        })
        .collect();
    let remaining_new: Vec<_> = new
        .iter()
        .enumerate()
        .filter(|(i, item)| {
            !excluded_new.contains(i)
                && !paired_new.contains(i)
                && eligible_refactor_candidate(&item.symbol)
        })
        .collect();
    let refactor_stage_skipped = !remaining_old.is_empty()
        && !remaining_new.is_empty()
        && (remaining_old.len() > MAX_REFACTOR_CANDIDATES
            || remaining_new.len() > MAX_REFACTOR_CANDIDATES);
    if !refactor_stage_skipped {
        let mut proposals: Vec<(usize, usize, u16)> = Vec::new();
        for &(i, a) in &remaining_old {
            let mut candidates = Vec::new();
            for &(j, b) in &remaining_new {
                if a.path == b.path
                    || a.language != b.language
                    || a.symbol.kind != b.symbol.kind
                    || (a.symbol.name == b.symbol.name && a.symbol.signature == b.symbol.signature)
                {
                    continue;
                }
                let score = body_similarity(&a.symbol.body, &b.symbol.body);
                if score >= 950 {
                    candidates.push((j, score));
                }
            }
            // Any ambiguity on the before side means no claim.
            if candidates.len() == 1 {
                proposals.push((i, candidates[0].0, candidates[0].1));
            }
        }
        let mut new_counts = BTreeMap::<usize, usize>::new();
        for &(_, j, _) in &proposals {
            *new_counts.entry(j).or_default() += 1;
        }
        for (i, j, score) in proposals {
            if new_counts[&j] != 1 {
                continue;
            }
            let a = &old[i];
            let b = &new[j];
            // Must also be the *only* strong contender for the new declaration.
            let reverse_candidates = remaining_old
                .iter()
                .filter(|(_, candidate)| {
                    candidate.path != b.path
                        && candidate.language == b.language
                        && candidate.symbol.kind == b.symbol.kind
                        && candidate.symbol.name != b.symbol.name
                        && candidate.symbol.signature != b.symbol.signature
                        && body_similarity(&candidate.symbol.body, &b.symbol.body) >= 950
                })
                .count();
            if reverse_candidates != 1 {
                continue;
            }
            moves.push(CrossFileMove {
                symbol_kind: a.symbol.kind.clone(),
                symbol_name: b.symbol.name.clone(),
                before_name: a.symbol.name.clone(),
                after_name: b.symbol.name.clone(),
                signature_changed: a.symbol.signature != b.symbol.signature,
                renamed: a.symbol.name != b.symbol.name,
                before_path: a.path.clone(),
                after_path: b.path.clone(),
                before_start: a.symbol.start,
                before_end: a.symbol.end,
                after_start: b.symbol.start,
                after_end: b.symbol.end,
                content_changed: true,
                similarity_permille: score,
            });
        }
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
        refactor_stage_skipped,
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

    const LONG_BODY: &str = "{ let offset = 10; let doubled = input * 2; let total = doubled + offset; println!(\"answer: {}\", total); let result = total + 42; println!(\"result: {}\", result); result }";

    #[test]
    fn renamed_and_signature_changed_rust_function_is_only_candidate() {
        let (a, b) = directories();
        fs::write(
            a.join("legacy.rs"),
            format!("fn calculate(input: i32) -> i32 {}\n", LONG_BODY),
        )
        .unwrap();
        fs::write(
            b.join("new.rs"),
            format!("fn sum_value(input: i64) -> i64 {}\n", LONG_BODY),
        )
        .unwrap();
        let result = compare_folders(&a, &b).unwrap();
        assert_eq!(result.moves.len(), 1);
        let m = &result.moves[0];
        assert!(m.content_changed && m.renamed && m.signature_changed);
        assert_eq!(m.before_name, "calculate");
        assert_eq!(m.after_name, "sum_value");
        assert_eq!(m.symbol_name, "sum_value");
        assert_eq!(m.similarity_permille, 1000);
        cleanup(&a);
    }

    #[test]
    fn renamed_signature_and_edited_body_is_candidate_only_if_highly_similar() {
        let (a, b) = directories();
        fs::write(
            a.join("old.rs"),
            format!("fn calculate(input: i32) -> i32 {}\n", LONG_BODY),
        )
        .unwrap();
        let updated = LONG_BODY.replace("offset = 10", "offset = 11");
        fs::write(
            b.join("new.rs"),
            format!("fn new_calculation(input: i64) -> i64 {}\n", updated),
        )
        .unwrap();
        let result = compare_folders(&a, &b).unwrap();
        assert_eq!(result.moves.len(), 1);
        assert!(
            result.moves[0].content_changed
                && result.moves[0].renamed
                && result.moves[0].signature_changed
                && result.moves[0].similarity_permille >= 950
        );
        cleanup(&a);
    }

    #[test]
    fn ambiguous_renamed_duplicate_bodies_do_not_get_claimed() {
        let (a, b) = directories();
        fs::write(
            a.join("one.rs"),
            format!("fn original(x: i32) -> i32 {}", LONG_BODY),
        )
        .unwrap();
        fs::write(
            a.join("two.rs"),
            format!("fn original2(x: i32) -> i32 {}", LONG_BODY),
        )
        .unwrap();
        fs::write(
            b.join("dest.rs"),
            format!("fn replaced(x: i64) -> i64 {}", LONG_BODY),
        )
        .unwrap();
        assert!(compare_folders(&a, &b).unwrap().moves.is_empty());
        cleanup(&a);
    }

    #[test]
    fn csharp_renamed_and_parameter_type_changed_method_is_candidate() {
        let (a, b) = directories();
        let body = "{ var offset = 10; var doubled = input * 2; var total = doubled + offset; System.Console.WriteLine(total); var output = total + 42; System.Console.WriteLine(output); return output; }";
        fs::write(
            a.join("Original.cs"),
            format!("class Original {{ int Calculate(int input) {} }}", body),
        )
        .unwrap();
        fs::write(
            b.join("After.cs"),
            format!("class After {{ long Sum(long input) {} }}", body),
        )
        .unwrap();
        let report = compare_folders(&a, &b).unwrap();
        assert_eq!(report.moves.len(), 1);
        let m = &report.moves[0];
        assert!(m.renamed && m.signature_changed && m.content_changed);
        assert_eq!(m.before_name, "Calculate");
        assert_eq!(m.after_name, "Sum");
        cleanup(&a);
    }

    #[test]
    fn competing_new_body_candidates_do_not_prove_refactor() {
        let (a, b) = directories();
        fs::write(
            a.join("old.rs"),
            format!("fn first(input: i32) -> i32 {}", LONG_BODY),
        )
        .unwrap();
        fs::write(
            b.join("new.rs"),
            format!("fn second(input: i64) -> i64 {}", LONG_BODY),
        )
        .unwrap();
        fs::write(
            b.join("other.rs"),
            format!("fn third(input: u64) -> u64 {}", LONG_BODY),
        )
        .unwrap();
        let report = compare_folders(&a, &b).unwrap();
        assert!(report.moves.is_empty());
        cleanup(&a);
    }

    #[test]
    fn too_many_refactor_candidates_skip_only_heuristic_stage() {
        let (a, b) = directories();
        for i in 0..258 {
            fs::write(
                a.join(format!("source_{i}.rs")),
                format!("fn before_{i}(input: i32) -> i32 {}", LONG_BODY),
            )
            .unwrap();
        }
        fs::write(
            b.join("destination.rs"),
            format!("fn after(input: i64) -> i64 {}", LONG_BODY),
        )
        .unwrap();
        let report = compare_folders(&a, &b).unwrap();
        assert!(report.moves.is_empty());
        assert!(report.refactor_stage_skipped);
        assert_eq!(report.analyzed_files, 259);
        cleanup(&a);
    }

    #[test]
    fn tiny_identical_function_body_cannot_prove_simultaneous_refactor() {
        let (a, b) = directories();
        fs::write(a.join("old.rs"), "fn old(x: i32) -> i32 { x + 1 }").unwrap();
        fs::write(b.join("new.rs"), "fn changed(x: i64) -> i64 { x + 1 }").unwrap();
        assert!(compare_folders(&a, &b).unwrap().moves.is_empty());
        cleanup(&a);
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
    fn detects_unique_edited_function_moved_between_rust_files() {
        let (a, b) = directories();
        fs::write(a.join("src.rs"),
            "fn calculate(input: i32) -> i32 {\n let offset = 10;\n let doubled = input * 2;\n let output = doubled + offset;\n println!(\"result: {}\", output);\n output\n}\nfn remain() { let z=3; }").unwrap();
        fs::write(b.join("src.rs"), "fn remain() { let z=3; }").unwrap();
        fs::write(b.join("dest.rs"),
            "fn calculate(input: i32) -> i32 {\n let offset = 11;\n let doubled = input * 2;\n let output = doubled + offset;\n println!(\"result: {}\", output);\n output\n}\n").unwrap();
        let report = compare_folders(&a, &b).unwrap();
        assert_eq!(report.moves.len(), 1);
        let change = &report.moves[0];
        assert_eq!(change.symbol_name, "calculate");
        assert_eq!(change.before_path, "src.rs");
        assert_eq!(change.after_path, "dest.rs");
        assert!(change.content_changed);
        assert!((880..1000).contains(&change.similarity_permille));
        cleanup(&a);
    }

    #[test]
    fn detects_unique_edited_csharp_method_moved_between_files() {
        let (a, b) = directories();
        fs::write(a.join("a.cs"),
            "class A { int Compute(int value) { var offset = 10; var doubled = value * 2; var answer = doubled + offset; System.Console.WriteLine(answer); return answer; } }").unwrap();
        fs::write(b.join("b.cs"),
            "class B { int Compute(int value) { var offset = 12; var doubled = value * 2; var answer = doubled + offset; System.Console.WriteLine(answer); return answer; } }").unwrap();
        let report = compare_folders(&a, &b).unwrap();
        assert_eq!(report.moves.len(), 1);
        assert_eq!(report.moves[0].symbol_kind, "method_declaration");
        assert_eq!(report.moves[0].symbol_name, "Compute");
        assert!(report.moves[0].content_changed);
        cleanup(&a);
    }

    #[test]
    fn same_name_unrelated_body_is_not_edited_move() {
        let (a, b) = directories();
        fs::write(a.join("a.rs"),
            "fn process(value: i32) -> i32 { let alpha = value * 2; let beta = alpha + 5; println!(\"old {}\", beta); beta }").unwrap();
        fs::write(b.join("b.rs"),
            "fn process(value: i32) -> i32 { let list = vec![2,3,4,5]; let largest = list.iter().max().unwrap(); println!(\"new {}\", largest); *largest }").unwrap();
        assert!(compare_folders(&a, &b).unwrap().moves.is_empty());
        cleanup(&a);
    }

    #[test]
    fn duplicate_same_signature_does_not_claim_edited_move() {
        let (a, b) = directories();
        let original = "fn process(value: i32) -> i32 { let alpha = value * 2; let beta = alpha + 5; println!(\"old {}\", beta); beta }";
        fs::write(a.join("a.rs"), original).unwrap();
        fs::write(a.join("b.rs"), original).unwrap();
        fs::write(b.join("c.rs"),
            "fn process(value: i32) -> i32 { let alpha = value * 2; let beta = alpha + 6; println!(\"old {}\", beta); beta }").unwrap();
        assert!(compare_folders(&a, &b).unwrap().moves.is_empty());
        cleanup(&a);
    }

    #[test]
    fn changed_function_signature_is_not_a_move() {
        let (a, b) = directories();
        fs::write(a.join("a.rs"), "fn compute(x: i32) -> i32 { let base = x * 2; let sum = base + 10; println!(\"sum {}\", sum); sum }").unwrap();
        fs::write(b.join("b.rs"), "fn compute(x: u64) -> u64 { let base = x * 2; let sum = base + 11; println!(\"sum {}\", sum); sum }").unwrap();
        assert!(compare_folders(&a, &b).unwrap().moves.is_empty());
        cleanup(&a);
    }

    #[test]
    fn short_generic_function_must_not_be_guessed_as_edited_move() {
        let (a, b) = directories();
        fs::write(a.join("a.rs"), "fn work() { let x = 1; }").unwrap();
        fs::write(b.join("b.rs"), "fn work() { let x = 2; }").unwrap();
        assert!(compare_folders(&a, &b).unwrap().moves.is_empty());
        cleanup(&a);
    }

    #[test]
    fn whitespace_only_change_is_still_a_modified_move_candidate() {
        let (a, b) = directories();
        fs::write(a.join("a.rs"),
            "fn process(value: i32) -> i32 { let doubled = value * 2; let offset = 10; let answer = doubled + offset; println!(\"answer {}\", answer); answer }").unwrap();
        fs::write(b.join("b.rs"),
            "fn process(value: i32) -> i32 {\n  let doubled = value * 2;\n  let offset = 10;\n  let answer = doubled + offset;\n  println!(\"answer {}\", answer);\n  answer\n}").unwrap();
        let report = compare_folders(&a, &b).unwrap();
        assert_eq!(report.moves.len(), 1);
        assert!(report.moves[0].content_changed);
        assert_eq!(report.moves[0].similarity_permille, 1000);
        cleanup(&a);
    }

    #[test]
    fn lexical_similarity_preserves_operator_change() {
        assert_ne!(
            body_similarity(
                "{ let a = x == y; let b = x != y; }",
                "{ let a = x == y; let b = x == y; }"
            ),
            1000
        );
        assert_eq!(body_similarity("{ let x = 1; }", "{ let x = 2; }"), 0);
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
