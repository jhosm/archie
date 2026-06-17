#!/usr/bin/env bash
#
# PostToolUse (surfacing, not a gate) — the commitment catalogue or an ADR was
# edited: run the authoritative §P6 coverage checker as a FAST MIRROR and surface
# any violation inline, before commit. The CI gate (spec-coverage.yml ->
# .github/scripts/spec-coverage-check.sh) is the authority; this only shortens the
# feedback loop so a broken Test-ID reference is caught at edit time, not in CI.
# ADR-PC-020 §P1 (hooks mirror authoritative gates) / §P6.

input="$(cat)"
file_path="$(printf '%s' "$input" | jq -r '.tool_input.file_path // empty')"
case "$file_path" in
  */adrs/ADR-*.md|*/adrs/commitment-catalogue.md) ;;
  *) exit 0 ;;
esac

root="$(git -C "$(dirname "$file_path")" rev-parse --show-toplevel 2>/dev/null || echo "${CLAUDE_PROJECT_DIR:-.}")"
checker="$root/.github/scripts/spec-coverage-check.sh"
[ -x "$checker" ] || exit 0

# Clean -> stay silent (a surface hook should be quiet when there is nothing to say).
out="$(bash "$checker" 2>&1)" && exit 0

msg="spec-coverage (fast mirror of the CI gate) flagged a violation after editing $(basename "$file_path"). CI is authoritative — fix before commit:
$(printf '%s' "$out" | grep -E '^::error' | sed 's/::error file=//; s/::/: /')"
jq -n --arg c "$msg" '{hookSpecificOutput:{hookEventName:"PostToolUse",additionalContext:$c}}'
exit 0
