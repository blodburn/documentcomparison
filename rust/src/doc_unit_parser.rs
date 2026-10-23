//! Full text-to-document-unit structural parser in Rust.
//! Input text is newline and flattened article-boundary normalized by C#
//! until these source contracts have their own exact Rust equivalents.
//! No table reading: parsed DOCX paragraph text is supplied by C#.
use regex::Regex;
use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, HashSet};
use std::sync::LazyLock;
static KARTICLE: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r"^\s*제\s*(\d+)\s*조(?:\s*의\s*(\d+))?\s*(?:\(([^\n)]{1,120})\))?\s*(.*)$").unwrap()
});
static EARTICLE: LazyLock<Regex> =
    LazyLock::new(|| {
        Regex::new(
r"(?i)^\s*(?:Article|Section)\s+((?:\d+(?:[-.]\d+)*)|(?:[IVXLCDM]+))\s*(?:[.:-])?\s*(.*)$").unwrap()
    });
static EHIER: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(
r"(?i)^\s*(?:[•●▪◦·*]+\s*)?(?P<level>Chapter|Part)\s+(?P<num>(?:\d+(?:[-.]\d+)*)|[IVXLCDM]+)\s*[.\-:–—]?\s*(?P<title>.*?)\s*$").unwrap()
});
static KHIER: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(
r"^\s*(?:[•●▪◦·*]+\s*)?제\s*(?P<num>\d+)\s*(?P<level>장|절|관)\s*(?:\((?P<p>[^)\n]*)\)|\[(?P<b>[^]\n]*)\]|(?P<title>.*?))\s*$").unwrap()
});
static WORDS: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"[A-Za-z가-힣]+").unwrap());
static GENERAL_WORDS: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"[A-Za-z가-힣0-9]+").unwrap());
static GNUM: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r"^\s*((?:\d+(?:\.\d+){0,5})|(?:[A-Za-z]+))[.)]?\s+(.{1,140})$").unwrap()
});
static EXPLICIT: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(
r"^(?:[①②③④⑤⑥⑦⑧⑨⑩⑪⑫⑬⑭⑮⑯⑰⑱⑲⑳]|\(\d+\)|\([A-Za-z]\)|\([IVXLCDMivxlcdm]{2,}\)|\d+[.)]|[가-하A-Za-z][.)])\s").unwrap()
});
static SHALLET: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"(?i)\b(?:shall|must|may|means|will|hereby)\b").unwrap());
static LONG_START: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(
r"(?i)^(?:the|a|an|if|when|where|provided|notwithstanding|company|member|members|user|users)\b").unwrap()
});
static ENGLISH_REST: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r"^[\(\{\[](?P<title>[^\)\}\]\n]{1,180})[\)\}\]]\s*(?P<tail>.*)$").unwrap()
});

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Input {
    pub text: String,
    pub legal: bool,
}
#[derive(Debug, Serialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct Unit {
    pub index: usize,
    pub number: String,
    pub title: String,
    pub header: String,
    pub body: String,
    pub section: String,
    pub kind: String,
}
#[derive(Debug, Serialize)]
pub struct Result {
    pub units: Vec<Unit>,
}
pub fn validate(v: &Input) -> bool {
    v.text.encode_utf16().count() <= 131072
}
fn unit(
    units: &mut Vec<Unit>,
    number: String,
    title: String,
    header: String,
    body: String,
    section: String,
    kind: &str,
) {
    units.push(Unit {
        index: units.len(),
        number,
        title,
        header,
        body,
        section,
        kind: kind.into(),
    });
}
fn clean_title(s: &str) -> String {
    s.trim()
        .trim_start_matches(|c: char| c.is_whitespace() || ".:-–—".contains(c))
        .trim()
        .trim_matches(|c: char| "()[]{} \t.:-–—".contains(c))
        .trim()
        .to_owned()
}
fn title_candidate(s: &str) -> bool {
    let s = s.trim_matches(|c: char| " \t|:-–—".contains(c));
    if s.is_empty() || s.encode_utf16().count() > 160 {
        return false;
    }
    if KARTICLE.is_match(s)
        || EARTICLE.is_match(s)
        || KHIER.is_match(s)
        || EHIER.is_match(s)
        || EXPLICIT.is_match(s)
    {
        return false;
    }
    let n = WORDS.find_iter(s).count();
    if n == 0 || n > 16 {
        return false;
    }
    if s.trim_end().ends_with(&['!', '?', ';'][..]) {
        return false;
    }
    if n > 8 && LONG_START.is_match(s) {
        return false;
    }
    !SHALLET.is_match(s)
}
fn next_title(lines: &[&str], i: &mut usize) -> Option<String> {
    let start = *i;
    for (j, item) in lines
        .iter()
        .enumerate()
        .take(lines.len().min(start + 3))
        .skip(start + 1)
    {
        let cand = item.trim_matches(|c| " \t|".contains(c));
        if cand.is_empty() {
            continue;
        }
        if !title_candidate(cand) {
            return None;
        }
        *i = j;
        return Some(
            cand.trim_matches(|c| "()[]{} \t|:-–—".contains(c))
                .to_owned(),
        );
    }
    None
}
fn roman_value(text: &str) -> i32 {
    let s = text.trim().to_uppercase();
    if s.is_empty() {
        return 0;
    }
    let mut sum = 0;
    let mut prev = 0;
    for c in s.chars().rev() {
        let k = match c {
            'I' => 1,
            'V' => 5,
            'X' => 10,
            'L' => 50,
            'C' => 100,
            'D' => 500,
            'M' => 1000,
            _ => return 0,
        };
        if k < prev {
            sum -= k;
        } else {
            sum += k;
            prev = k;
        }
    }
    if !(1..=3999).contains(&sum) {
        return 0;
    }
    let mut x = sum;
    let mut canonical = String::new();
    for (v, t) in [
        (1000, "M"),
        (900, "CM"),
        (500, "D"),
        (400, "CD"),
        (100, "C"),
        (90, "XC"),
        (50, "L"),
        (40, "XL"),
        (10, "X"),
        (9, "IX"),
        (5, "V"),
        (4, "IV"),
        (1, "I"),
    ] {
        while x >= v {
            canonical.push_str(t);
            x -= v;
        }
    }
    if canonical == s { sum } else { 0 }
}
fn value(s: &str) -> Option<f64> {
    if roman_value(s) > 0 {
        return Some(roman_value(s) as f64);
    }
    let r = s.split(['.', '-']).collect::<Vec<_>>();
    let n = r.first()?.parse::<f64>().ok()?;
    if r.len() > 1 {
        return Some(n + r[1].parse::<f64>().ok()? / 1000.0);
    }
    Some(n)
}
fn english_article(line: &str) -> Option<(String, String)> {
    let caps = EARTICLE.captures(line)?;
    let number = caps.get(1)?.as_str();
    if number.chars().all(|c| c.is_ascii_alphabetic()) && roman_value(number) == 0 {
        return None;
    }
    Some((
        number.to_owned(),
        caps.get(2).map_or("", |v| v.as_str()).to_owned(),
    ))
}
fn false_article_lines(lines: &[&str]) -> HashSet<usize> {
    let markers = lines
        .iter()
        .enumerate()
        .filter_map(|(i, t)| english_article(t.trim()).map(|(number, _)| (i, number)))
        .collect::<Vec<_>>();
    if markers.len() <= 2 {
        return HashSet::new();
    }
    let mut keep = vec![true; markers.len()];
    loop {
        let active = (0..markers.len()).filter(|&i| keep[i]).collect::<Vec<_>>();
        if active.len() <= 2 {
            break;
        }
        let mut updated = false;
        for pos in 1..active.len() - 1 {
            let (pi, i, ni) = (active[pos - 1], active[pos], active[pos + 1]);
            let (Some(p), Some(c), Some(n)) = (
                value(&markers[pi].1),
                value(&markers[i].1),
                value(&markers[ni].1),
            ) else {
                continue;
            };
            if ((n - p) - 1.0).abs() > 1e-9 || !(c < p || c > n) {
                continue;
            }
            let jump = (c - p).abs().min((c - n).abs());
            let duplicate = active
                .iter()
                .skip(pos + 1)
                .any(|&j| markers[j].1.eq_ignore_ascii_case(&markers[i].1));
            if jump >= 3.0 && (duplicate || jump >= 6.0) {
                keep[i] = false;
                updated = true;
                break;
            }
        }
        if !updated {
            break;
        }
    }
    keep.iter()
        .enumerate()
        .filter_map(|(i, &x)| if x { None } else { Some(markers[i].0) })
        .collect()
}
fn current_section(sections: &BTreeMap<String, String>) -> String {
    let mut used = HashSet::new();
    let mut items = vec![];
    for level in ["part", "chapter", "장", "절", "관"] {
        if let Some(label) = sections.get(level)
            && used.insert(label.clone())
        {
            items.push(label.clone());
        }
    }
    items.join(" > ")
}
fn set_section(sections: &mut BTreeMap<String, String>, level: &str, label: String) {
    sections.insert(level.into(), label);
    if level == "part" || level == "장" {
        sections.remove("chapter");
        sections.remove("절");
        sections.remove("관");
    } else if level == "chapter" || level == "절" {
        sections.remove("관");
    }
}
fn flush_loose(units: &mut Vec<Unit>, loose: &mut Vec<String>, seq: &mut usize, section: &str) {
    if loose.is_empty() {
        return;
    }
    let body = loose.join("\n").trim().to_owned();
    loose.clear();
    if body.is_empty() {
        return;
    }
    *seq += 1;
    unit(
        units,
        format!("p{seq}"),
        String::new(),
        String::new(),
        body,
        section.into(),
        "block",
    );
}
fn flush_article(
    units: &mut Vec<Unit>,
    header: &mut Option<(String, String, String)>,
    body: &mut Vec<String>,
    section: &str,
) {
    let Some((number, title, heading)) = header.take() else {
        return;
    };
    let content = body.join("\n").trim().to_owned();
    body.clear();
    unit(
        units,
        number,
        title,
        heading,
        content,
        section.into(),
        "article",
    );
}
fn legal_units(text: &str) -> Result {
    let mut units = vec![];
    let mut body = vec![];
    let mut loose = vec![];
    let (mut header, mut seq) = (None, 0);
    let mut sections = BTreeMap::new();
    let lines = text.split('\n').collect::<Vec<_>>();
    let false_lines = false_article_lines(&lines);
    let mut i = 0;
    while i < lines.len() {
        let line = lines[i].trim();
        if line.is_empty() {
            i += 1;
            continue;
        }
        let eh = EHIER.captures(line);
        let kh = KHIER.captures(line);
        if eh.is_some() || kh.is_some() {
            let section = current_section(&sections);
            flush_article(&mut units, &mut header, &mut body, &section);
            flush_loose(&mut units, &mut loose, &mut seq, &section);
            let (level, num, mut title, english) = if let Some(c) = eh {
                (
                    c.name("level").unwrap().as_str().to_lowercase(),
                    c.name("num").unwrap().as_str().to_owned(),
                    c.name("title").map_or("", |v| v.as_str()).trim().to_owned(),
                    true,
                )
            } else {
                let c = kh.unwrap();
                (
                    c.name("level").unwrap().as_str().to_owned(),
                    c.name("num").unwrap().as_str().to_owned(),
                    ["p", "b", "title"]
                        .into_iter()
                        .map(|name| c.name(name).map_or("", |v| v.as_str()))
                        .collect::<Vec<_>>()
                        .join("")
                        .trim()
                        .to_owned(),
                    false,
                )
            };
            if title.is_empty() {
                title = next_title(&lines, &mut i).unwrap_or_default();
            }
            let label = if english {
                format!(
                    "{} {}{}",
                    if level == "part" { "Part" } else { "Chapter" },
                    num,
                    if title.is_empty() {
                        String::new()
                    } else {
                        format!(". {title}")
                    }
                )
            } else {
                format!(
                    "제{num}{level}{}",
                    if title.is_empty() {
                        String::new()
                    } else {
                        format!(" {title}")
                    }
                )
            };
            set_section(&mut sections, &level, label.clone());
            let section = current_section(&sections);
            unit(
                &mut units,
                format!("{level}:{num}"),
                title,
                label,
                String::new(),
                section,
                "section",
            );
            i += 1;
            continue;
        }
        let km = KARTICLE.captures(line);
        let em = if false_lines.contains(&i) {
            None
        } else {
            english_article(line)
        };
        if km.is_some() || em.is_some() {
            let section = current_section(&sections);
            flush_article(&mut units, &mut header, &mut body, &section);
            flush_loose(&mut units, &mut loose, &mut seq, &section);
            let (number, mut title, mut tail, english) = if let Some(c) = km {
                let n = c.get(1).unwrap().as_str();
                let sub = c.get(2).map(|v| v.as_str());
                (
                    if let Some(v) = sub {
                        format!("{n}의{v}")
                    } else {
                        n.into()
                    },
                    c.get(3).map_or("", |v| v.as_str()).trim().to_owned(),
                    c.get(4).map_or("", |v| v.as_str()).trim().to_owned(),
                    false,
                )
            } else {
                let (number, rest) = em.unwrap();
                let raw = rest.trim();
                let (title, tail) = if let Some(c) = ENGLISH_REST.captures(raw) {
                    (
                        clean_title(c.name("title").unwrap().as_str()),
                        c.name("tail").map_or("", |v| v.as_str()).trim().to_owned(),
                    )
                } else {
                    (clean_title(raw), String::new())
                };
                (number, title, tail, true)
            };
            if title.is_empty() && tail.is_empty() {
                title = next_title(&lines, &mut i).unwrap_or_default();
            } else if !english && title.is_empty() && title_candidate(&tail) {
                title = clean_title(&tail);
                tail.clear();
            }
            let label = if english {
                format!(
                    "Article {number}{}",
                    if title.is_empty() {
                        String::new()
                    } else {
                        format!(" ({title})")
                    }
                )
            } else {
                format!(
                    "제{number}조{}",
                    if title.is_empty() {
                        String::new()
                    } else {
                        format!("({title})")
                    }
                )
            };
            header = Some((number, title, label));
            if !tail.is_empty() {
                body.push(tail);
            }
        } else if header.is_some() {
            body.push(line.to_owned());
        } else {
            loose.push(line.to_owned());
        }
        i += 1;
    }
    let section = current_section(&sections);
    flush_article(&mut units, &mut header, &mut body, &section);
    flush_loose(&mut units, &mut loose, &mut seq, &section);
    Result { units }
}
fn general_heading(line: &str) -> Option<(String, String)> {
    let s = line.trim();
    if s.is_empty() || s.encode_utf16().count() > 180 {
        return None;
    }
    if let Some(c) = GNUM.captures(s) {
        let marker = c.get(1)?.as_str();
        let title = c.get(2)?.as_str().trim();
        let numeric = marker.chars().next()?.is_ascii_digit();
        let one_upper = marker.len() == 1 && marker.chars().next()?.is_ascii_uppercase();
        let roman = roman_value(marker) > 0;
        if (numeric || one_upper || roman)
            && title.encode_utf16().count() <= 110
            && !title.trim_end().ends_with(&['.', '!', '?', ';'][..])
        {
            return Some((marker.into(), s.into()));
        }
    }
    let words = GENERAL_WORDS
        .find_iter(s)
        .map(|m| m.as_str())
        .collect::<Vec<_>>();
    if (1..=10).contains(&words.len())
        && s.encode_utf16().count() <= 80
        && !s.trim_end().ends_with(&['.', '!', '?', ';', ':'][..])
    {
        let latin = s.chars().any(|c| c.is_ascii_alphabetic());
        let uppercase = s.chars().any(char::is_alphabetic)
            && s.chars()
                .filter(|c| c.is_alphabetic())
                .all(|c| !c.is_lowercase());
        let titlecase = latin
            && words
                .iter()
                .filter(|w| w.chars().next().is_some_and(char::is_uppercase))
                .count()
                >= 1.max(words.len() / 2);
        if uppercase || titlecase {
            return Some((String::new(), s.into()));
        }
    }
    None
}
fn flush_general(
    units: &mut Vec<Unit>,
    current: &mut Option<(String, String)>,
    body: &mut Vec<String>,
    seq: &mut usize,
) {
    if current.is_none() && body.is_empty() {
        return;
    }
    *seq += 1;
    let content = body.join("\n").trim().to_owned();
    body.clear();
    if let Some((number, header)) = current.take() {
        let title = if number.is_empty() {
            header.clone()
        } else {
            let prefix = Regex::new(&format!(r"^\s*{}[.)]?\s*", regex::escape(&number))).unwrap();
            prefix.replace(&header, "").trim().to_owned()
        };
        unit(
            units,
            if number.is_empty() {
                format!("g{seq}")
            } else {
                number
            },
            title,
            header,
            content,
            String::new(),
            "block",
        );
    } else {
        unit(
            units,
            format!("g{seq}"),
            String::new(),
            String::new(),
            content,
            String::new(),
            "block",
        );
    }
}
fn general_units(text: &str) -> Result {
    let mut units = vec![];
    let paras = text.split('\n').map(str::trim).filter(|s| !s.is_empty());
    let (mut current, mut body, mut seq) = (None, Vec::new(), 0);
    for para in paras {
        if let Some(heading) = general_heading(para) {
            flush_general(&mut units, &mut current, &mut body, &mut seq);
            current = Some(heading);
        } else {
            if current.is_none() && !body.is_empty() {
                flush_general(&mut units, &mut current, &mut body, &mut seq);
            }
            body.push(para.to_owned());
        }
    }
    flush_general(&mut units, &mut current, &mut body, &mut seq);
    Result { units }
}
pub fn parse(input: &Input) -> Result {
    if input.legal {
        legal_units(&input.text)
    } else {
        general_units(&input.text)
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn article_title_and_reorder() {
        let doc = "제1조(목적)\n본 약관은 회원의 권리를 규정한다.\n제2조(보호)\n회원은 개인정보를 보호받는다.";
        let parsed = parse(&Input {
            text: doc.into(),
            legal: true,
        });
        assert_eq!(parsed.units.len(), 2);
        assert_eq!(parsed.units[0].title, "목적");
        assert_eq!(parsed.units[1].number, "2");
    }
    #[test]
    fn general_title_and_paragraph() {
        let doc = "PURPOSE\nThis is a simple paragraph.\nPRIVACY\nDetails.";
        let parsed = parse(&Input {
            text: doc.into(),
            legal: false,
        });
        assert_eq!(parsed.units.len(), 2);
        assert_eq!(parsed.units[0].title, "PURPOSE");
    }
}
