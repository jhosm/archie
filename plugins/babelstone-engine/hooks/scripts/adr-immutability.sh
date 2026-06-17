#!/usr/bin/env bash
#
# PreToolUse hook (fast mirror) — warn on an in-place edit to an *Accepted* ADR's
# `## Decision` section. ADR-PC-000 §D5 / ADR-PC-020 §P1,§D3: a Decision must not
# change in place; append a dated amendment or supersede. This hook only WARNS (it
# sees one edit, not the whole change, so it cannot confirm an amendment rides
# along) — the authoritative hard gate is CI (.github/workflows/adr-governance.yml).
# bash 3.2 safe (macOS system bash). Always exits 0.

input="$(cat)"
tool="$(printf '%s' "$input" | jq -r '.tool_name // empty')"
file_path="$(printf '%s' "$input" | jq -r '.tool_input.file_path // empty')"

case "$file_path" in
  */adrs/ADR-*.md) ;;
  *) exit 0 ;;
esac
[ -f "$file_path" ] || exit 0   # brand-new file = new ADR, fine

status="$(grep -m1 '^| *Status *|' "$file_path" 2>/dev/null \
  | awk -F'|' '{gsub(/^[[:space:]]+|[[:space:]]+$/,"",$3); print $3}')"
case "$status" in Accepted) ;; *) exit 0 ;; esac

decision_start="$(grep -n '^## Decision' "$file_path" 2>/dev/null | head -1 | cut -d: -f1)"
[ -n "$decision_start" ] || exit 0
rel="$(tail -n +$((decision_start + 1)) "$file_path" | grep -n '^## ' | head -1 | cut -d: -f1)"
if [ -n "$rel" ]; then decision_end=$((decision_start + rel - 1));
else decision_end="$(wc -l < "$file_path" | tr -d '[:space:]')"; fi
section="$(sed -n "${decision_start},${decision_end}p" "$file_path")"

touches=0
case "$tool" in
  Edit)
    old="$(printf '%s' "$input" | jq -r '.tool_input.old_string // empty')"
    [ -n "$old" ] && case "$section" in *"$old"*) touches=1 ;; esac ;;
  MultiEdit)
    while IFS= read -r old; do
      [ -n "$old" ] || continue
      case "$section" in *"$old"*) touches=1; break ;; esac
    done <<EOF
$(printf '%s' "$input" | jq -r '.tool_input.edits[]?.old_string // empty')
EOF
    ;;
  Write) touches=1 ;;   # full overwrite of an Accepted ADR — cannot diff; warn
esac
[ "$touches" = "1" ] || exit 0

adr_id="$(basename "$file_path" | sed -E 's/^(ADR-(PC|IC)-[0-9]+).*/\1/')"
msg="${adr_id} is Accepted and this edit changes its '## Decision' section. ADR-PC-000 §D5 / ADR-PC-020 §D3: do not edit an Accepted Decision in place — append a dated amendment (a '*Revised YYYY-MM-DD: …*' line) or supersede with a new ADR, in THIS same change. If you ARE adding that amendment, carry on. CI (adr-governance) hard-fails a Decision change with no amendment riding along."

jq -n --arg c "$msg" '{hookSpecificOutput:{hookEventName:"PreToolUse",additionalContext:$c}}'
exit 0
