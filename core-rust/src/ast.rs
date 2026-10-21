//! Bounded Tree-sitter declaration comparison for Rust and C# source.
//! Exact identity/body matching only. This intentionally does NOT claim
//! semantic equivalence of rewritten code or infer ambiguous moves.
use serde::Serialize;
use std::collections::{BTreeMap, BTreeSet};
use tree_sitter::{Node, Parser};

const MAX_AST_BYTES: usize = 2 * 1024 * 1024;
const MAX_NODES: usize = 5_000;
const MAX_DEPTH: usize = 128;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Language {
    Rust,
    CSharp,
}

#[derive(Debug, Clone)]
pub(crate) struct Symbol {
    pub(crate) kind: String,
    pub(crate) name: String,
    pub(crate) parent: String,
    pub(crate) identity: String,
    pub(crate) source: String,
    pub(crate) body: String,
    pub(crate) start: usize,
    pub(crate) end: usize,
}

#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct AstChange {
    pub kind: &'static str, // Add, Delete, Modify, Move, Rename
    pub symbol_kind: String,
    pub before_name: String,
    pub after_name: String,
    pub before_start: usize,
    pub before_end: usize,
    pub after_start: usize,
    pub after_end: usize,
}

fn declaration_kind(language: Language, kind: &str) -> bool {
    match language {
        Language::Rust => matches!(
            kind,
            "function_item"
                | "struct_item"
                | "enum_item"
                | "trait_item"
                | "type_item"
                | "const_item"
        ),
        Language::CSharp => matches!(
            kind,
            "class_declaration"
                | "struct_declaration"
                | "interface_declaration"
                | "enum_declaration"
                | "record_declaration"
                | "method_declaration"
                | "constructor_declaration"
        ),
    }
}

fn scope_name(language: Language, node: Node<'_>, source: &str) -> Option<String> {
    let kind = node.kind();
    let is_scope = match language {
        Language::Rust => matches!(kind, "mod_item" | "impl_item" | "trait_item"),
        Language::CSharp => matches!(
            kind,
            "namespace_declaration"
                | "file_scoped_namespace_declaration"
                | "class_declaration"
                | "struct_declaration"
                | "interface_declaration"
                | "record_declaration"
        ),
    };
    if !is_scope {
        return None;
    }
    let name = if kind == "impl_item" {
        node.child_by_field_name("type")
    } else {
        node.child_by_field_name("name")
    }?;
    let value = name.utf8_text(source.as_bytes()).ok()?;
    Some(format!("{}:{}", kind, value))
}

pub(crate) fn declarations(language: Language, source: &str) -> Result<Vec<Symbol>, &'static str> {
    if source.len() > MAX_AST_BYTES {
        return Err("source exceeds 2 MB AST parsing budget");
    }
    let mut parser = Parser::new();
    let grammar = match language {
        Language::Rust => tree_sitter_rust::LANGUAGE.into(),
        Language::CSharp => tree_sitter_c_sharp::LANGUAGE.into(),
    };
    parser
        .set_language(&grammar)
        .map_err(|_| "grammar mismatch")?;
    let tree = parser.parse(source, None).ok_or("AST parser failed")?;
    if tree.root_node().has_error() {
        return Err("invalid source syntax: falling back to line diff");
    }
    let mut symbols = Vec::new();
    let mut stack = vec![(tree.root_node(), String::new(), 0_usize)];
    while let Some((node, parent, depth)) = stack.pop() {
        if depth > MAX_DEPTH {
            return Err("AST nesting exceeds limit");
        }
        let kind = node.kind();
        if declaration_kind(language, kind)
            && let Some(name_node) = node.child_by_field_name("name")
            && let Ok(name) = name_node.utf8_text(source.as_bytes())
        {
            let identity = format!("{kind}\0{parent}\0{name}");
            let body = node
                .child_by_field_name("body")
                .and_then(|n| n.utf8_text(source.as_bytes()).ok())
                .unwrap_or("")
                .to_owned();
            let span = node.byte_range();
            symbols.push(Symbol {
                kind: kind.to_owned(),
                name: name.to_owned(),
                parent: parent.clone(),
                identity,
                source: source[span.clone()].to_owned(),
                body,
                start: span.start,
                end: span.end,
            });
            if symbols.len() > MAX_NODES {
                return Err("AST declaration count exceeds limit");
            }
        }
        let child_parent = match scope_name(language, node, source) {
            Some(scope) if parent.is_empty() => scope,
            Some(scope) => format!("{parent}/{scope}"),
            None => parent,
        };
        let mut cursor = node.walk();
        let children: Vec<_> = node.named_children(&mut cursor).collect();
        for child in children.into_iter().rev() {
            stack.push((child, child_parent.clone(), depth + 1));
        }
    }
    Ok(symbols)
}

// Empty bodies are common across unrelated declarations and cannot be
// trusted to establish a rename or move even with unique candidates.
pub(crate) fn has_meaningful_body(body: &str) -> bool {
    body.chars()
        .any(|c| !c.is_whitespace() && c != '{' && c != '}' && c != ';')
}

/// Unchanged declarations are omitted. Same-qualified declaration changing
/// bytes is Modify. Only unique exact body matches may produce Rename/Move;
/// all ambiguous candidates remain Add/Delete, never speculative moves.
pub fn compare(
    language: Language,
    before: &str,
    after: &str,
) -> Result<Vec<AstChange>, &'static str> {
    let old = declarations(language, before)?;
    let new = declarations(language, after)?;
    let mut matched_old = BTreeSet::new();
    let mut matched_new = BTreeSet::new();
    let mut changes = Vec::new();

    let mut left_keys: BTreeMap<&str, Vec<usize>> = BTreeMap::new();
    let mut right_keys: BTreeMap<&str, Vec<usize>> = BTreeMap::new();
    for (i, item) in old.iter().enumerate() {
        left_keys.entry(&item.identity).or_default().push(i);
    }
    for (i, item) in new.iter().enumerate() {
        right_keys.entry(&item.identity).or_default().push(i);
    }
    for (key, left) in &left_keys {
        let Some(right) = right_keys.get(key) else {
            continue;
        };
        if left.len() != 1 || right.len() != 1 {
            continue;
        }
        let (i, j) = (left[0], right[0]);
        matched_old.insert(i);
        matched_new.insert(j);
        if old[i].source != new[j].source {
            changes.push(event("Modify", &old[i], &new[j]));
        }
    }

    // Match unmatched declarations by unique identical body and kind. This
    // only detects symbol name or scope movement, not semantic equivalence.
    // Empty bodies cannot be used to identify a function reliably.
    let mut left_body: BTreeMap<(&str, &str), Vec<usize>> = BTreeMap::new();
    let mut right_body: BTreeMap<(&str, &str), Vec<usize>> = BTreeMap::new();
    for (i, item) in old.iter().enumerate() {
        if !matched_old.contains(&i) && has_meaningful_body(&item.body) {
            left_body
                .entry((&item.kind, &item.body))
                .or_default()
                .push(i);
        }
    }
    for (i, item) in new.iter().enumerate() {
        if !matched_new.contains(&i) && has_meaningful_body(&item.body) {
            right_body
                .entry((&item.kind, &item.body))
                .or_default()
                .push(i);
        }
    }
    for (key, left) in left_body {
        let Some(right) = right_body.get(&key) else {
            continue;
        };
        if left.len() != 1 || right.len() != 1 {
            continue;
        }
        let (i, j) = (left[0], right[0]);
        if old[i].parent == new[j].parent && old[i].name == new[j].name {
            continue;
        }
        matched_old.insert(i);
        matched_new.insert(j);
        changes.push(event(
            if old[i].parent == new[j].parent {
                "Rename"
            } else {
                "Move"
            },
            &old[i],
            &new[j],
        ));
    }

    for (i, item) in old.iter().enumerate() {
        if !matched_old.contains(&i) {
            changes.push(AstChange {
                kind: "Delete",
                symbol_kind: item.kind.clone(),
                before_name: item.name.clone(),
                after_name: String::new(),
                before_start: item.start,
                before_end: item.end,
                after_start: 0,
                after_end: 0,
            });
        }
    }
    for (i, item) in new.iter().enumerate() {
        if !matched_new.contains(&i) {
            changes.push(AstChange {
                kind: "Add",
                symbol_kind: item.kind.clone(),
                before_name: String::new(),
                after_name: item.name.clone(),
                before_start: 0,
                before_end: 0,
                after_start: item.start,
                after_end: item.end,
            });
        }
    }

    changes.sort_by(|a, b| {
        (
            a.after_start,
            a.before_start,
            a.kind,
            &a.after_name,
            &a.before_name,
        )
            .cmp(&(
                b.after_start,
                b.before_start,
                b.kind,
                &b.after_name,
                &b.before_name,
            ))
    });
    Ok(changes)
}

fn event(kind: &'static str, old: &Symbol, new: &Symbol) -> AstChange {
    AstChange {
        kind,
        symbol_kind: new.kind.clone(),
        before_name: old.name.clone(),
        after_name: new.name.clone(),
        before_start: old.start,
        before_end: old.end,
        after_start: new.start,
        after_end: new.end,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn rust_function_move_across_modules() {
        let a = "mod x { fn hello() { println!(\"hi\"); } }\nmod y {}\n";
        let b = "mod x {}\nmod y { fn hello() { println!(\"hi\"); } }\n";
        let result = compare(Language::Rust, a, b).unwrap();
        assert_eq!(result.len(), 1);
        assert_eq!(result[0].kind, "Move");
        assert_eq!(result[0].before_name, "hello");
    }
    #[test]
    fn rust_renamed_function_and_modified_body() {
        let a = "fn first() { println!(\"가\"); }\nfn other() { 1; }\n";
        let b = "fn renamed() { println!(\"가\"); }\nfn other() { 2; }\n";
        let result = compare(Language::Rust, a, b).unwrap();
        assert_eq!(result.len(), 2);
        assert!(
            result
                .iter()
                .any(|c| c.kind == "Rename" && c.after_name == "renamed")
        );
        assert!(
            result
                .iter()
                .any(|c| c.kind == "Modify" && c.after_name == "other")
        );
    }
    #[test]
    fn csharp_method_rename_and_body_change() {
        let a = "class C { void First() { int x = 1; } void Keep() { return; } }";
        let b = "class C { void After() { int x = 1; } void Keep() { int z=2; } }";
        let result = compare(Language::CSharp, a, b).unwrap();
        assert!(
            result
                .iter()
                .any(|c| c.kind == "Rename" && c.after_name == "After")
        );
        assert!(
            result
                .iter()
                .any(|c| c.kind == "Modify" && c.after_name == "Keep")
        );
    }
    #[test]
    fn duplicated_bodies_are_not_claimed_as_moves() {
        let a = "mod a { fn one() {} fn two() {} }";
        let b = "mod b { fn one() {} fn two() {} }";
        let result = compare(Language::Rust, a, b).unwrap();
        assert!(
            !result
                .iter()
                .any(|c| c.kind == "Move" || c.kind == "Rename")
        );
    }
    #[test]
    fn unchanged_declarations_and_comments_outside_are_ignored() {
        let a = "fn example() { 1; }\n";
        let b = "// changed header\nfn example() { 1; }\n";
        assert!(compare(Language::Rust, a, b).unwrap().is_empty());
    }
    #[test]
    fn csharp_method_move_between_classes_is_explicit() {
        let before = "class A { void Work() { int n = 7; } } class B {}";
        let after = "class A {} class B { void Work() { int n = 7; } }";
        let changes = compare(Language::CSharp, before, after).unwrap();
        assert!(changes.iter().any(|c| c.kind == "Move"
            && c.symbol_kind == "method_declaration"
            && c.after_name == "Work"));
    }
    #[test]
    fn rust_implementation_methods_are_scoped_to_type() {
        let before = "struct A; struct B; impl A { fn work(&self) { let n = 123; } }";
        let after = "struct A; struct B; impl B { fn work(&self) { let n = 123; } }";
        let changes = compare(Language::Rust, before, after).unwrap();
        assert!(changes.iter().any(|c| c.kind == "Move"
            && c.symbol_kind == "function_item"
            && c.after_name == "work"));
    }
    #[test]
    fn short_empty_function_body_cannot_prove_rename() {
        let before = "fn previous() {}";
        let after = "fn later() {}";
        let changes = compare(Language::Rust, before, after).unwrap();
        assert_eq!(changes.len(), 2);
        assert!(!changes.iter().any(|c| c.kind == "Rename"));
    }
    #[test]
    fn huge_input_yields_bounded_parse_error() {
        let huge = "a".repeat(2 * 1024 * 1024 + 1);
        assert!(compare(Language::Rust, &huge, "").is_err());
    }
    #[test]
    fn malformed_syntax_is_not_parsed_as_valid_ast() {
        assert!(compare(Language::Rust, "fn f() {", "fn f() {}").is_err());
    }
}
