//! UTF-8 Rust port of the managed legal comparison normalization
//! contracts, including NFC/NFKC and the visible-text no-op guard.
//! Unpaired UTF-16 surrogates cannot traverse JSON/UTF-8; those values
//! retain the existing managed fallback.
use regex::Regex;
use serde::{Deserialize, Serialize};
use std::sync::LazyLock;
use unicode_general_category::{GeneralCategory as Category, get_general_category};
use unicode_normalization::UnicodeNormalization;

static ARTICLE: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r"(?i)^\s*Article\s+(?:\d+(?:[-.]\d+)*[A-Za-z]?|[IVXLCDM]+)\b").unwrap()
});
static KOREAN: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"^\s*제\s*\d+\s*조(?:\s*의\s*\d+)?").unwrap());

#[derive(Debug, Deserialize)]
pub struct Input {
    pub text: String,
    pub mode: String,
}
#[derive(Debug, Serialize)]
pub struct Result {
    pub text: String,
}
pub fn validate(v: &Input) -> bool {
    v.text.encode_utf16().count() <= 8192
        && matches!(v.mode.as_str(), "normalize" | "lineage" | "visible")
}
fn ignored(c: char) -> bool {
    matches!(
        c,
        '\u{200b}' | '\u{200c}' | '\u{200d}' | '\u{2060}' | '\u{feff}'
    ) || ('\u{fe00}'..='\u{fe0f}').contains(&c)
}
fn lexical(c: char) -> bool {
    c.len_utf16() == 1
        && matches!(
            get_general_category(c),
            Category::UppercaseLetter
                | Category::LowercaseLetter
                | Category::TitlecaseLetter
                | Category::ModifierLetter
                | Category::OtherLetter
                | Category::DecimalNumber
                | Category::LetterNumber
                | Category::OtherNumber
                | Category::NonspacingMark
                | Category::SpacingMark
                | Category::EnclosingMark
        )
}
fn flush_lexical(sb: &mut String, tokens: &mut String) {
    if tokens.is_empty() {
        return;
    }
    let kc: String = tokens.nfkc().collect();
    for c in kc.chars() {
        // .NET string.ToLowerInvariant uses simple one-codepoint casing.
        // Rust Unicode full lowercase expands dotted capital I, while
        // the canonical C# string comparison leaves U+0130 unchanged.
        let lowered: Vec<char> = c.to_lowercase().collect();
        if lowered.len() == 1 {
            sb.push(lowered[0]);
        } else {
            sb.push(c);
        }
    }
    tokens.clear();
}
fn normalize(s: &str) -> String {
    let normalized: String = s.nfc().collect();
    let (mut sb, mut tokens, mut pending) = (String::new(), String::new(), false);
    for c in normalized.chars() {
        if ignored(c) {
            continue;
        }
        if c.is_whitespace() {
            flush_lexical(&mut sb, &mut tokens);
            pending = !sb.is_empty();
            continue;
        }
        if pending {
            sb.push(' ');
            pending = false;
        }
        if lexical(c) {
            tokens.push(c);
        } else {
            flush_lexical(&mut sb, &mut tokens);
            sb.push(c);
        }
    }
    flush_lexical(&mut sb, &mut tokens);
    sb.trim().into()
}
fn visible(s: &str) -> String {
    let normalized: String = s.nfc().collect();
    let (mut sb, mut pending) = (String::new(), false);
    for c in normalized.chars() {
        if ignored(c) || get_general_category(c) == Category::Format {
            continue;
        }
        if c.is_whitespace() {
            pending = !sb.is_empty();
            continue;
        }
        if pending {
            sb.push(' ');
            pending = false;
        }
        sb.push(c);
    }
    sb.trim().into()
}
fn lineage(s: &str) -> String {
    let normalized: String = s.nfc().collect();
    let rest = ARTICLE.replace(&normalized, " ");
    let rest = KOREAN.replace(&rest, " ");
    let mut out = String::new();
    for c in rest.chars() {
        if c.is_ascii_alphanumeric() || ('가'..='힣').contains(&c) || c == '%' {
            out.extend(c.to_lowercase());
        }
    }
    out
}
pub fn convert(v: &Input) -> Result {
    Result {
        text: match v.mode.as_str() {
            "normalize" => normalize(&v.text),
            "lineage" => lineage(&v.text),
            _ => visible(&v.text),
        },
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn compatibility_full_width_latin_and_whitespace() {
        assert_eq!(normalize("  ＡＢＣ \u{200b} 안녕  "), "abc 안녕");
        assert_eq!(normalize("동의 없이"), "동의 없이");
        assert_eq!(normalize("동의없이"), "동의없이");
        assert_eq!(normalize("① A"), "1 a");
    }
    #[test]
    fn no_op_visible_and_article_prefix() {
        assert_eq!(visible("A \u{200d}  B"), "A B");
        assert_eq!(lineage("Article 3 (Privacy Rights)"), "privacyrights");
        assert_eq!(lineage("제2조(개인정보)"), "개인정보");
    }
}
