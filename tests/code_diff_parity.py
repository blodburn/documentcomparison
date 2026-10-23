#!/usr/bin/env python3
"""Run identical UTF-8 code-diff inputs through Rust CLI and .NET/native FFI, comparing exact hunks.
Usage: python3 tests/code_diff_parity.py [dotnet_path] [cargo_path]
The probe is opt-in and does not change the Windows UI or shipped runtime.
"""
import json
import random
import re
import subprocess
import sys
import tempfile
from pathlib import Path

root = Path(__file__).resolve().parents[1]
dotnet = sys.argv[1] if len(sys.argv) > 1 else "dotnet"
cargo = sys.argv[2] if len(sys.argv) > 2 else "cargo"
project = "tests/DocumentCompare.Regression/DocumentCompare.Regression.csproj"
manifest = "rust/Cargo.toml"
subprocess.run([cargo, "build", "--manifest-path", manifest], cwd=root, check=True)
subprocess.run([dotnet, "build", project, "-c", "Release"], cwd=root, check=True)

rnd = random.Random(20261021)
atoms = ["a\n", "a\r\n", "한글\n", "😀\n", "\ufeffheader\n", "\n", "a", "a\rb\n", "same\n"]
corpus = [
    {"before": "", "after": ""},
    {"before": "\ufeffhello\n", "after": "hello\n"},
    {"before": "a\n", "after": "a\n\n"},
    {"before": "a\r\nb\r\n", "after": "a\nb\n"},
    {"before": "a\nb\n", "after": "a\n"},
    {"before": "one\n" * 2200, "after": "two\n" * 2200},
]
for _ in range(250):
    corpus.append({
        "before": "".join(rnd.choices(atoms, k=rnd.randrange(0, 17))),
        "after": "".join(rnd.choices(atoms, k=rnd.randrange(0, 17))),
    })

pattern = re.compile(r"^(Insert|Delete|Replace)\tbefore:(\d+)-(\d+)\tafter:(\d+)-(\d+)$")
with tempfile.TemporaryDirectory(prefix="dc-code-parity-") as tmp:
    tmp = Path(tmp)
    cases = tmp / "cases.json"
    cases.write_text(json.dumps(corpus, ensure_ascii=False), encoding="utf-8")
    cs = subprocess.run(
        [dotnet, "run", "--project", project, "-c", "Release", "--no-build",
         "--", "--code-diff-corpus", str(cases)],
        cwd=root, capture_output=True, text=True, check=True,
    )
    cs_results = json.loads(cs.stdout)
    assert len(cs_results) == len(corpus)
    rust_exe = root / "rust" / "target" / "debug" / ("dc-code-diff.exe" if sys.platform == "win32" else "dc-code-diff")
    for index, pair in enumerate(corpus):
        before, after = tmp / "before.rs", tmp / "after.rs"
        before.write_bytes(pair["before"].encode("utf-8"))
        after.write_bytes(pair["after"].encode("utf-8"))
        rust = subprocess.run([str(rust_exe), str(before), str(after)],
                              capture_output=True, text=True, check=True)
        expected = []
        for line in rust.stdout.splitlines():
            match = pattern.fullmatch(line)
            if not match:
                raise AssertionError(f"Unexpected Rust output for case {index}: {line!r}")
            kind, bs, be, as_, ae = match.groups()
            expected.append({
                "kind": kind, "beforeStart": int(bs) - 1,
                "beforeEnd": int(be), "afterStart": int(as_) - 1,
                "afterEnd": int(ae),
            })
        if cs_results[index] != expected:
            raise AssertionError(
                f"Code parity mismatch at case {index}\n"
                f"input={pair!r}\nRust={expected}\nC#={cs_results[index]}"
            )
print(f"PASS exact C#/Rust code-change parity: {len(corpus)} cases")
