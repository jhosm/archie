#!/usr/bin/env bash
#
# PostToolUse (surfacing, not a gate) — event/contract schema edited: scan for
# PII-shaped field names and surface the no-PII-on-the-durable-bus rule. The bus
# carries REFERENCES; PII resolves internally (ADR-PC-004 crypto-shredding,
# ADR-PC-014 references-only; ADR-PC-020 §P3 contract-reviewer is the judgement layer).

input="$(cat)"
file_path="$(printf '%s' "$input" | jq -r '.tool_input.file_path // empty')"
case "$file_path" in
  */contracts/*|contracts/*|*.avsc|*.avro|*.proto) ;;
  *) exit 0 ;;
esac
[ -f "$file_path" ] || exit 0

# Deliberately specific tokens (avoid matching eventName / fieldName).
pattern='iban|bic|swift|nif|niss|vat[_-]?id|tax[_-]?id|passport|msisdn|phone|telephone|e[-_]?mail|first[_-]?name|last[_-]?name|surname|full[_-]?name|given[_-]?name|date[_-]?of[_-]?birth|birth[_-]?date|dob|postal|street|address|card[_-]?number|cvv|account[_-]?holder'
hits="$(grep -ioE "$pattern" "$file_path" 2>/dev/null | tr '[:upper:]' '[:lower:]' | sort -u | tr '\n' ' ' | sed 's/ *$//')"
[ -n "$hits" ] || exit 0

msg="Possible PII field name(s) in $(basename "$file_path"): ${hits}. NEVER put PII (cleartext OR ciphertext) on the durable bus — carry a reference/token and resolve internally (ADR-PC-004, ADR-PC-014). If these are genuinely references (a token id, not a value) this is fine; otherwise replace with a reference. The contract-reviewer agent is the authoritative check."

jq -n --arg c "$msg" '{hookSpecificOutput:{hookEventName:"PostToolUse",additionalContext:$c}}'
exit 0
