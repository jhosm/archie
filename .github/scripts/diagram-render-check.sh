#!/usr/bin/env bash
#
# diagram-render-check.sh — CI guard: every committed C4 PlantUML source under
# docs/ or infra/ must render cleanly to SVG.
#
# Mirrors the render step in .githooks/pre-commit, but over *all* docs/**/*.puml
# and infra/**/*.puml, and as a READ-ONLY check (renders into a temp dir; never touches the working
# tree). Render-only by design: no PlantUML version is pinned across dev machines
# and CI (the Brewfile installs it unpinned; mise.toml does not manage it), and
# SVG output is not byte-stable across PlantUML versions — so this asserts each
# diagram *renders to its expected SVG*, not that committed bytes match. Detecting
# hand-edited (un-rendered) SVGs stays the doc-consistency agent's job.
#
# Convention (same as the hook): `@startuml <id>` MUST equal the .puml filename
# (without extension), so foo.puml renders to foo.svg.

set -euo pipefail

if ! command -v plantuml >/dev/null 2>&1; then
  echo "::error::plantuml not found — install plantuml + graphviz before running this check." >&2
  exit 1
fi

cd "$(git rev-parse --show-toplevel)"

status=0
count=0

while IFS= read -r puml; do
  [ -n "$puml" ] || continue
  count=$((count + 1))
  base="$(basename "${puml%.puml}")"
  tmp="$(mktemp -d)"
  if ! plantuml -tsvg -o "$tmp" "$puml" 2>"$tmp/err"; then
    echo "::error file=${puml}::PlantUML failed to render ${puml}"
    sed 's/^/    /' "$tmp/err" >&2 || true
    status=1
  elif [ ! -f "$tmp/${base}.svg" ]; then
    echo "::error file=${puml}::expected ${base}.svg but PlantUML produced none — does '@startuml' use the id '${base}' (matching the filename)?"
    status=1
  else
    echo "ok: ${puml}"
  fi
  rm -rf "$tmp"
done < <(find docs infra -name '*.puml' | sort)

echo "Rendered ${count} PlantUML source(s); exit status ${status}."
exit "$status"
