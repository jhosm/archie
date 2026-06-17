#!/usr/bin/env bash
#
# PostToolUse (fast mirror) — re-render an edited *.puml to SVG immediately.
# ADR-PC-020 §P1: the .githooks/pre-commit renderer is AUTHORITATIVE; this is
# "optional faster feedback, not a second authority". GitHub renders Mermaid but
# not PlantUML, so the committed SVG is what readers see (CLAUDE.md).

input="$(cat)"
file_path="$(printf '%s' "$input" | jq -r '.tool_input.file_path // empty')"
case "$file_path" in *.puml) ;; *) exit 0 ;; esac
[ -f "$file_path" ] || exit 0

if ! command -v plantuml >/dev/null 2>&1; then
  jq -n '{hookSpecificOutput:{hookEventName:"PostToolUse",additionalContext:"Edited a .puml but plantuml is not installed (brew install graphviz plantuml; see INSTALL.md). GitHub will not render PlantUML — regenerate the committed .svg before commit; .githooks/pre-commit is authoritative."}}'
  exit 0
fi

svg="${file_path%.puml}.svg"
if plantuml -tsvg "$file_path" >/dev/null 2>&1 && [ -f "$svg" ]; then
  jq -n --arg s "$(basename "$svg")" '{hookSpecificOutput:{hookEventName:"PostToolUse",additionalContext:("Re-rendered " + $s + " from the edited PlantUML source — stage it alongside the .puml (.githooks/pre-commit is the authoritative renderer at commit; ensure @startuml id matches the filename).")}}'
else
  jq -n '{hookSpecificOutput:{hookEventName:"PostToolUse",additionalContext:"PlantUML render failed — check @startuml <id> matches the filename (CLAUDE.md convention) and that graphviz/dot is installed. .githooks/pre-commit is authoritative."}}'
fi
exit 0
