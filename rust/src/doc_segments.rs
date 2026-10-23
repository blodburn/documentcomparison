//! Word review text marker overlays and final unmodified/insert/delete
//! display segment builder. UTF-16 coordinates match C# marker offsets.
use serde::{Deserialize, Serialize};
#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Span {
    pub start: usize,
    pub end: usize,
    pub role: u8,
}
#[derive(Debug, Deserialize)]
pub struct Input {
    pub text: String,
    pub spans: Vec<Span>,
}
#[derive(Debug, Serialize, Clone, PartialEq)]
pub struct Segment {
    pub text: String,
    pub style: String,
}
#[derive(Debug, Serialize)]
pub struct Result {
    pub segments: Vec<Segment>,
}
pub fn validate(v: &Input) -> bool {
    let len = v.text.encode_utf16().count();
    len <= 131072
        && v.spans.len() <= 8192
        && v.spans.iter().all(|s| s.role == 1 || s.role == 2)
        && v.spans.iter().all(|s| s.start <= 131072 && s.end <= 131072)
}
pub fn build(v: &Input) -> Option<Result> {
    let units: Vec<u16> = v.text.encode_utf16().collect();
    let len = units.len();
    if len == 0 {
        return Some(Result { segments: vec![] });
    }
    let mut changes = vec![0u8; len];
    for s in &v.spans {
        let start = s.start.min(len);
        let end = s.end.min(len).max(start);
        for r in changes.iter_mut().take(end).skip(start) {
            *r |= s.role;
        }
    }
    let mut segments = Vec::new();
    let mut p = 0;
    while p < len {
        let flag = changes[p];
        let mut q = p + 1;
        while q < len && changes[q] == flag {
            q += 1;
        }
        // C# can represent a single UTF-16 surrogate code unit in
        // strings. Rust cannot represent it alone as valid UTF-8;
        // explicitly defer these cases to the canonical C# renderer.
        let content = String::from_utf16(&units[p..q]).ok()?;
        segments.push(Segment {
            text: content,
            style: match flag {
                1 => "delete",
                2 => "insert",
                3 => "both",
                _ => "normal",
            }
            .into(),
        });
        p = q;
    }
    Some(Result { segments })
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn overlapping_review_roles_make_both() {
        let v = Input {
            text: "abcdef".into(),
            spans: vec![
                Span {
                    start: 1,
                    end: 4,
                    role: 1,
                },
                Span {
                    start: 3,
                    end: 5,
                    role: 2,
                },
            ],
        };
        let got = build(&v).unwrap();
        let rendered = got
            .segments
            .into_iter()
            .map(|x| (x.text, x.style))
            .collect::<Vec<_>>();
        assert_eq!(
            rendered,
            vec![
                ("a".into(), "normal".into()),
                ("bc".into(), "delete".into()),
                ("d".into(), "both".into()),
                ("e".into(), "insert".into()),
                ("f".into(), "normal".into())
            ]
        );
    }
    #[test]
    fn surrogate_pair_cannot_be_sliced_midpoint() {
        let v = Input {
            text: "a😀b".into(),
            spans: vec![Span {
                start: 1,
                end: 2,
                role: 1,
            }],
        };
        assert!(build(&v).is_none());
        let v = Input {
            text: "a😀b".into(),
            spans: vec![Span {
                start: 1,
                end: 3,
                role: 1,
            }],
        };
        assert_eq!(build(&v).unwrap().segments[1].text, "😀");
    }
}
