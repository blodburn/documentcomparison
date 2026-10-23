//! Native sentence-level unchanged-span anchors for legal review markers.
//! Only emits standalone deleted/inserted sentences around exact retained
//! sentences; complex two-sided rewrites fall back to the C# full diff.
use serde::{Deserialize, Serialize};

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Input {
    pub old_text: String,
    pub new_text: String,
    pub include_punctuation: bool,
}
#[derive(Debug, Clone)]
struct Sentence {
    start: usize,
    end: usize,
    text: String,
}
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Event {
    pub kind: String,
    pub old_start: usize,
    pub old_end: usize,
    pub new_start: usize,
    pub new_end: usize,
    pub text: String,
    pub hunk: usize,
}
#[derive(Debug, Serialize)]
pub struct Result {
    pub supported: bool,
    pub events: Vec<Event>,
}

fn spans(text: &str) -> Vec<Sentence> {
    if text.encode_utf16().count() > 65_536 {
        return vec![];
    }
    let mut result = vec![];
    let mut from = 0;
    let add = |start: usize, end: usize, items: &mut Vec<Sentence>| {
        let t = &text[start..end];
        let trim = t.trim();
        if trim.is_empty() {
            return;
        }
        let prefix = t.len() - t.trim_start().len();
        let first = start + prefix;
        let last = first + trim.len();
        items.push(Sentence {
            start: text[..first].encode_utf16().count(),
            end: text[..last].encode_utf16().count(),
            text: trim.to_owned(),
        });
    };
    for (pos, ch) in text.char_indices() {
        if !matches!(ch, '.' | '!' | '?' | '。' | '！' | '？') {
            continue;
        }
        let end = pos + ch.len_utf8();
        if end < text.len() && !text[end..].chars().next().is_some_and(char::is_whitespace) {
            continue;
        }
        add(from, end, &mut result);
        from = end;
        if result.len() >= 128 {
            return vec![];
        }
    }
    add(from, text.len(), &mut result);
    result
}
fn utf16slice(text: &str, start: usize, end: usize) -> Option<String> {
    let chars: Vec<u16> = text.encode_utf16().collect();
    String::from_utf16(chars.get(start..end)?).ok()
}
fn trim_span(text: &str, mut start: usize, mut end: usize) -> (usize, usize) {
    // UTF-16 index handling is performed without byte slicing.
    let u: Vec<u16> = text.encode_utf16().collect();
    while start < end && char::from_u32(u[start] as u32).is_some_and(char::is_whitespace) {
        start += 1;
    }
    while start < end && char::from_u32(u[end - 1] as u32).is_some_and(char::is_whitespace) {
        end -= 1;
    }
    (start, end)
}
fn lcs(a: &[Sentence], b: &[Sentence]) -> Vec<(usize, usize)> {
    let (n, m) = (a.len(), b.len());
    let mut dp = vec![vec![0u16; m + 1]; n + 1];
    for i in (0..n).rev() {
        for j in (0..m).rev() {
            dp[i][j] = if a[i].text == b[j].text {
                dp[i + 1][j + 1] + 1
            } else {
                dp[i + 1][j].max(dp[i][j + 1])
            };
        }
    }
    let mut pairs = vec![];
    let (mut i, mut j) = (0, 0);
    while i < n && j < m {
        if a[i].text == b[j].text {
            pairs.push((i, j));
            i += 1;
            j += 1;
        } else if dp[i + 1][j] >= dp[i][j + 1] {
            i += 1;
        } else {
            j += 1;
        }
    }
    pairs
}
pub fn review(input: &Input) -> Result {
    let unsupported = || Result {
        supported: false,
        events: vec![],
    };
    let (old, new) = (&input.old_text, &input.new_text);
    let (old_size, new_size) = (old.encode_utf16().count(), new.encode_utf16().count());
    if old_size.min(new_size) < 96 {
        return unsupported();
    }
    let a = spans(old);
    let b = spans(new);
    if a.len() < 2 || b.len() < 2 {
        return unsupported();
    }
    // Repeated sentences are ambiguous as standalone anchors. Let the
    // original C# SequenceMatcher decide those cases.
    if a.iter()
        .enumerate()
        .any(|(i, x)| a.iter().skip(i + 1).any(|y| y.text == x.text))
        || b.iter()
            .enumerate()
            .any(|(i, x)| b.iter().skip(i + 1).any(|y| y.text == x.text))
    {
        return unsupported();
    }
    let pairs = lcs(&a, &b);
    if !pairs.iter().any(|&(i, j)| {
        a[i].text.encode_utf16().count() >= 40 && b[j].text.encode_utf16().count() >= 40
    }) {
        return unsupported();
    }
    let mut anchors = vec![(None, None)];
    anchors.extend(pairs.iter().map(|&(i, j)| (Some(i), Some(j))));
    anchors.push((Some(a.len()), Some(b.len())));
    let mut events = vec![];
    for k in 0..anchors.len() - 1 {
        let (pi, pj) = anchors[k];
        let (ni, nj) = anchors[k + 1];
        let os = pi.map_or(0, |i| a[i].end);
        let ns = pj.map_or(0, |j| b[j].end);
        let oe = if ni == Some(a.len()) {
            old_size
        } else {
            a[ni.unwrap()].start
        };
        let ne = if nj == Some(b.len()) {
            new_size
        } else {
            b[nj.unwrap()].start
        };
        let (os, oe) = trim_span(old, os, oe);
        let (ns, ne) = trim_span(new, ns, ne);
        if os == oe && ns == ne {
            continue;
        }
        let Some(ot) = utf16slice(old, os, oe) else {
            return unsupported();
        };
        let Some(nt) = utf16slice(new, ns, ne) else {
            return unsupported();
        };
        if !input.include_punctuation
            && (ot.is_empty() || !ot.chars().any(char::is_alphanumeric))
            && (nt.is_empty() || !nt.chars().any(char::is_alphanumeric))
        {
            continue;
        }
        if !ot.is_empty() && !nt.is_empty() {
            return unsupported();
        }
        events.push(Event {
            kind: if ot.is_empty() {
                "insert".into()
            } else {
                "delete".into()
            },
            old_start: os,
            old_end: oe,
            new_start: ns,
            new_end: ne,
            text: if ot.is_empty() { nt } else { ot },
            hunk: k,
        });
    }
    if events.is_empty() {
        return unsupported();
    }
    Result {
        supported: true,
        events,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn retains_common_fee_sentences() {
        let a = "동의 내용과 절차를 상세하게 안내하며 계약서 본문에도 동일한 조항이 기재되어 있습니다. 삭제되는 기존 서비스 가격 인상 규정입니다. 이용자에게는 해당 내용이 계속 동일하게 적용되며 변동 사항이 없는 조건입니다.";
        let b = "동의 내용과 절차를 상세하게 안내하며 계약서 본문에도 동일한 조항이 기재되어 있습니다. 이용자에게는 해당 내용이 계속 동일하게 적용되며 변동 사항이 없는 조건입니다. 다만, 관련 법령에 따른 청약철회와 환불 권리는 제한되지 않습니다.";
        let result = review(&Input {
            old_text: a.into(),
            new_text: b.into(),
            include_punctuation: true,
        });
        assert!(result.supported);
        assert_eq!(result.events.len(), 2);
        assert_eq!(result.events[0].kind, "delete");
        assert_eq!(result.events[1].kind, "insert");
    }
}
