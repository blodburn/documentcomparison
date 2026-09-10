"""Document Compare engine sidecar for the Avalonia front-end.

Two protocols are supported:

One shot (stdin -> stdout):
  {"command":"compare","paths":[...],"baseIndex":0,"mode":"auto","includeAC":false}

Persistent server mode:
  DocumentCompare.Engine.exe --server
  One compact JSON request per stdin line, one compact JSON response per stdout line.

Commands:
  ping
  compare
  export_xlsx
  export_word

Only protocol JSON is written to stdout. Diagnostics go to stderr.
"""
from __future__ import annotations

import json
import sys
from pathlib import Path
import app


def _configure_stdio() -> None:
    """Force UTF-8 for the redirected .NET <-> Python pipe on Windows.

    Korean Windows commonly defaults Python stdio to CP949.  The compare result
    can contain characters such as U+2022 BULLET, which CP949 cannot encode.
    """
    for stream in (sys.stdin, sys.stdout, sys.stderr):
        reconfigure = getattr(stream, "reconfigure", None)
        if reconfigure is None:
            continue
        try:
            if stream is sys.stdin:
                reconfigure(encoding="utf-8")
            else:
                reconfigure(encoding="utf-8", errors="backslashreplace")
        except Exception:
            pass


_configure_stdio()

MAX_FILE_BYTES = 40 * 1024 * 1024


def _read_path(path: Path) -> str:
    # Reuse the warm-process DOCX/TXT cache already maintained by the comparison engine.
    # This is especially important when the same files are compared again after only a
    # base/mode/option change.  The cache key includes size+mtime, so changed files are
    # automatically reparsed.
    cached_reader = getattr(app, "_v45_read_path_cached", None)
    if callable(cached_reader):
        text, _cache_hit = cached_reader(path)
        return text
    raw = path.read_bytes()
    if len(raw) > MAX_FILE_BYTES:
        raise ValueError(f"{path.name}: file exceeds 40 MB")
    return app.read_document(path.name, raw)


def _validated_paths(req: dict, *, min_count=2, max_count=3):
    paths = [Path(x) for x in req.get("paths", [])]
    if not (min_count <= len(paths) <= max_count):
        raise ValueError(f"requires {min_count} to {max_count} paths")
    for p in paths:
        if not p.is_file():
            raise FileNotFoundError(str(p))
    return paths


def _mode(req: dict) -> str:
    mode = str(req.get("mode", "auto"))
    return mode if mode in ("auto", "general", "legal") else "auto"


def _compare(req: dict, progress=None):
    if progress:
        progress(3)
    paths = _validated_paths(req)
    base_index = int(req.get("baseIndex", 0))
    if base_index < 0 or base_index >= len(paths):
        raise ValueError("invalid baseIndex")
    app.ACTIVE_COMPARE_MODE = _mode(req)
    app.ACTIVE_INCLUDE_PUNCTUATION = bool(req.get("includePunctuation", True))
    names = [p.name for p in paths]
    texts = []
    for index, path in enumerate(paths):
        texts.append(_read_path(path))
        if progress:
            progress(10 + round((index + 1) / len(paths) * 30))
    if progress:
        progress(45)
    result = app.compare_documents(
        names, texts, base_index=base_index, include_ac=bool(req.get("includeAC", False))
    )
    if progress:
        progress(94)
    return result


def handle(req: dict, progress=None) -> dict:
    cmd = str(req.get("command", "compare"))
    if cmd == "ping":
        return {
            "ok": True,
            "engine": "python",
            "version": getattr(app, "APP_VERSION", "unknown"),
            "protocol": 2,
        }
    if cmd == "compare":
        return {"ok": True, "result": _compare(req, progress)}
    if cmd == "export_xlsx":
        result = _compare(req, progress)
        out = Path(req.get("outputPath", ""))
        if not out:
            raise ValueError("outputPath is required")
        out.parent.mkdir(parents=True, exist_ok=True)
        out.write_bytes(app.make_xlsx(result))
        return {"ok": True, "outputPath": str(out)}
    if cmd == "export_word":
        app.ACTIVE_COMPARE_MODE = _mode(req)
        app.ACTIVE_INCLUDE_PUNCTUATION = bool(req.get("includePunctuation", True))
        original = Path(req.get("originalPath", ""))
        revised = Path(req.get("revisedPath", ""))
        output = Path(req.get("outputPath", ""))
        if not original.is_file():
            raise FileNotFoundError(str(original))
        if not revised.is_file():
            raise FileNotFoundError(str(revised))
        if not str(output):
            raise ValueError("outputPath is required")
        output.parent.mkdir(parents=True, exist_ok=True)
        author = str(req.get("author") or revised.stem)
        app.create_word_tracked_compare(original, revised, output, revised_author=author)
        return {"ok": True, "outputPath": str(output)}
    raise ValueError(f"unknown command: {cmd}")


def _respond(req: dict, progress=None) -> str:
    try:
        out = handle(req, progress)
    except Exception as exc:
        out = {"ok": False, "error": str(exc)}
    return json.dumps(out, ensure_ascii=True, separators=(",", ":"))


def server_main() -> int:
    # line-buffered protocol so the .NET process can reuse the warm Python engine
    for line in sys.stdin:
        if not line.strip():
            continue
        # Be tolerant of a UTF-8 BOM from a Windows/.NET writer.  Protocol
        # messages are JSON-lines, so a BOM is never semantically meaningful.
        line = line.lstrip("\ufeff")
        try:
            req = json.loads(line)
        except Exception as exc:
            sys.stdout.write(json.dumps({"ok": False, "error": f"invalid JSON: {exc}"}, ensure_ascii=True) + "\n")
            sys.stdout.flush()
            continue
        def emit_progress(value: int) -> None:
            sys.stdout.write(json.dumps({"type": "progress", "value": int(value)}, ensure_ascii=True, separators=(",", ":")) + "\n")
            sys.stdout.flush()

        progress_callback = emit_progress if bool(req.get("wantProgress", False)) else None
        sys.stdout.write(_respond(req, progress_callback) + "\n")
        sys.stdout.flush()
    return 0


def main() -> int:
    if "--server" in sys.argv:
        return server_main()
    raw = sys.stdin.read().lstrip("\ufeff")
    try:
        req = json.loads(raw or "{}")
    except Exception as exc:
        sys.stdout.write(json.dumps({"ok": False, "error": f"invalid JSON: {exc}"}, ensure_ascii=True))
        return 1
    text = _respond(req)
    sys.stdout.write(text)
    try:
        return 0 if json.loads(text).get("ok") else 1
    except Exception:
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
