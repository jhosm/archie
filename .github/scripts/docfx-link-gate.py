#!/usr/bin/env python3
"""Fail the docs-site build on a broken DocFX site link or dead bookmark.

ADR-PC-026 §P6. DocFX's own ``--warningsAsErrors`` flag is too blunt for this
repo: run under the mise-pinned SDK (ADR-PC-010), DocFX 2.78.5's Roslyn metadata
pass emits environment-only warnings — ``FailedToLoadAnalyzer`` (the SDK ships a
Razor source-generator newer than the compiler docfx bundles) and
``Duplicate source file`` for the deliberately-excluded analyzer projects — that
say nothing about link integrity and would fail the lane for the wrong reason.

This gate reads DocFX's structured JSON log (``docfx … -l <log> --logLevel
warning``) and fails only on the link/bookmark warning codes, so a *new* corpus
link that escapes the content root, or a dead heading anchor, breaks the build
while the environmental noise is ignored.
"""
from __future__ import annotations

import json
import sys

# DocFX warning codes that mean "a link/bookmark on the published site is broken".
# InvalidFileLink         — a relative link whose target is not in the built site.
# InvalidBookmark         — a link to a #fragment that no element on the page owns.
# InvalidInternalBookmark — same, for an in-page (same-file) fragment.
LINK_CODES = {"InvalidFileLink", "InvalidBookmark", "InvalidInternalBookmark"}


def main(argv: list[str]) -> int:
    if len(argv) != 2:
        print("usage: docfx-link-gate.py <docfx-json-log>", file=sys.stderr)
        return 2
    log_path = argv[1]

    offenders: list[str] = []
    try:
        with open(log_path, encoding="utf-8") as fh:
            for line in fh:
                line = line.strip()
                if not line:
                    continue
                try:
                    record = json.loads(line)
                except json.JSONDecodeError:
                    continue  # DocFX writes one JSON object per line; skip stray text
                if record.get("code") in LINK_CODES:
                    where = record.get("file") or ""
                    line_no = record.get("line") or ""
                    loc = f"{where}:{line_no}" if where else ""
                    offenders.append(
                        f"  {record.get('code')}: {record.get('message', '').strip()}"
                        + (f"  [{loc}]" if loc else "")
                    )
    except FileNotFoundError:
        print(f"docfx-link-gate: log not found: {log_path}", file=sys.stderr)
        return 2

    if offenders:
        print(
            "docfx-link-gate: broken site link(s) / dead bookmark(s) "
            "(ADR-PC-026 §P6):",
            file=sys.stderr,
        )
        for line in offenders:
            print(line, file=sys.stderr)
        return 1

    print("docfx-link-gate: no broken site links or dead bookmarks.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
