#!/usr/bin/env bash
#
# PostToolUse (surfacing, not a gate) — engine/family C# edited: surface the
# AUTHORITATIVE determinism + Money/decimal analyser gate. ADR-PC-020 §P1 /
# ADR-PC-010 §P1–§P2,§P5. Never runs the analyser (may not exist yet) — it points
# at the CI gate so enforcement lives in the analyser, not the model's memory.

input="$(cat)"
file_path="$(printf '%s' "$input" | jq -r '.tool_input.file_path // empty')"
case "$file_path" in
  */engine/*.cs|engine/*.cs|*/families/*.cs|families/*.cs) ;;
  *) exit 0 ;;
esac

msg="Engine source edited ($(basename "$file_path")). Authoritative gate = the CI determinism gate + Money/decimal Roslyn analysers (ADR-PC-010 §P1–§P2,§P5), not this hook. Hold the invariants: handlers PURE (no clock, no I/O, no randomness — inject time/values), and Money rounds HALF_EVEN exactly ONCE at the decimal→cents boundary. A handler that reads the clock or rounds mid-calculation will fail CI."

jq -n --arg c "$msg" '{hookSpecificOutput:{hookEventName:"PostToolUse",additionalContext:$c}}'
exit 0
