#!/usr/bin/env bash
# Unit tests for adr-immutability-check.sh — the authoritative §D5 gate (ADR-PC-000 §D5 /
# ADR-PC-020 §D3,§P1) — and a smoke check of its fast PreToolUse mirror
# (plugins/babelstone-engine/hooks/scripts/adr-immutability.sh).
#
# REGRESSION GUARD (the reason this file exists): both gates classify an ADR's mutability by
# its Status cell. They used to exact-match `Accepted`, which silently skipped the three
# free-form statuses in the corpus — ADR-PC-002 `Accepted (gated by Q-Y …)`, ADR-PC-004
# `Accepted (gated by DPO …)`, ADR-PC-005 `Accepted (production-blocking …)`. Those are the
# highest-stakes Decisions (bitemporality, PII crypto-shredding, DR RTO/RPO) and were editable
# in place with no amendment and a green CI. The fix widened the match to the glob `Accepted*`.
# Case A below fails against the pre-fix `Accepted)` and passes against `Accepted*)`.
#
# Hermetic: each case builds a throwaway repo with a real `origin` remote (the gate diffs
# `origin/<base>...HEAD` and `git show origin/<base>:<path>`), so no network and no fixtures on
# disk. Pure bash + git + jq (all on the ubuntu-latest runner; bash-3.2-safe for local macOS).
#
# Run: bash .github/scripts/adr-immutability-check.test.sh   (exit 0 = all green)
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
SCRIPT="$HERE/adr-immutability-check.sh"
HOOK="$ROOT/plugins/babelstone-engine/hooks/scripts/adr-immutability.sh"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

ADR_PATH="docs/product-management/product_concepts/adrs/ADR-PC-999-immutability-test.md"

TESTS=0
FAILS=0
expect() { # <expected> <actual> <label>
  TESTS=$((TESTS + 1))
  if [ "$1" = "$2" ]; then printf '  ok   %s\n' "$3"
  else FAILS=$((FAILS + 1)); printf '  FAIL %s (expected [%s], got [%s])\n' "$3" "$1" "$2"; fi
}

# mkadr <status> <decision-body> — print a minimal but realistic ADR to stdout.
mkadr() {
  cat <<EOF
# ADR-PC-999 — immutability test fixture

| Field | Value |
|---|---|
| Status | $1 |
| Date | 2026-01-01 |

## Context

Some context prose.

## Decision

$2

## Consequences

Some consequences.
EOF
}

# gate_rc <status> <base-decision> <new-decision> [append-after-consequences]
# Builds origin+working clone, commits the base ADR, then commits a change, runs the gate,
# and echoes its exit code. Same Status across both commits — we vary only the Decision prose
# (and, optionally, append an amendment line outside the Decision section).
gate_rc() {
  local repo wc
  repo="$(mktemp -d "$WORK/repo.XXXXXX")"; wc="$repo/wc"
  git init --bare -q "$repo/o.git"
  git clone -q "$repo/o.git" "$wc" 2>/dev/null
  git -C "$wc" config user.email t@example.com
  git -C "$wc" config user.name tester
  git -C "$wc" config commit.gpgsign false
  mkdir -p "$wc/$(dirname "$ADR_PATH")"
  mkadr "$1" "$2" > "$wc/$ADR_PATH"
  git -C "$wc" add -A
  git -C "$wc" commit -qm base
  git -C "$wc" branch -M main
  git -C "$wc" push -q -u origin main
  mkadr "$1" "$3" > "$wc/$ADR_PATH"
  [ -n "${4:-}" ] && printf '%s\n' "$4" >> "$wc/$ADR_PATH"
  git -C "$wc" commit -qam change
  ( cd "$wc" && BASE_REF=main bash "$SCRIPT" ) >/dev/null 2>&1
  echo $?
}

printf 'adr-immutability-check.sh — authoritative gate\n'

# A — REGRESSION: a `Accepted (gated …)` Decision changed with NO amendment must BLOCK.
expect 1 "$(gate_rc 'Accepted (gated by DPO — production gate; see §Gate)' 'Original decision.' 'Rewritten decision.')" \
  "gated-Accepted Decision changed without amendment blocks (the fix)"

# B — the same gated-Accepted change PASSES when a dated amendment rides along.
expect 0 "$(gate_rc 'Accepted (gated by DPO — production gate; see §Gate)' 'Original decision.' 'Rewritten decision.' '*Revised 2026-01-01: clarified scope (additive).*')" \
  "gated-Accepted Decision changed WITH an amendment passes"

# C — canonical `Accepted` still blocks (behaviour unchanged by the glob widening).
expect 1 "$(gate_rc 'Accepted' 'Original decision.' 'Rewritten decision.')" \
  "canonical Accepted Decision changed without amendment still blocks"

# D — `Proposed` is still skipped (a draft Decision is freely editable).
expect 0 "$(gate_rc 'Proposed' 'Original decision.' 'Rewritten decision.')" \
  "Proposed Decision change is skipped"

# E — no false positive: a gated-Accepted ADR whose Decision is UNCHANGED (only trailing,
# non-Decision prose edited) passes.
expect 0 "$(gate_rc 'Accepted (production-blocking at cutover)' 'Stable decision.' 'Stable decision.' 'A trailing note outside the Decision section.')" \
  "gated-Accepted with an unchanged Decision (non-Decision edit) passes"

# J — REGRESSION (bd babelstone-2t16.33): a LARGE amendment must PASS. When the amendment adds
# many '+' lines with the keyword near the TOP, the pre-fix pipeline `grep '^+' | grep -qiE …`
# let `grep -qiE` close the pipe on its first match while the upstream `grep '^+'` was still
# writing — the upstream took SIGPIPE (141), and under `set -o pipefail` that 141 became the
# `if`'s status and read as a false NON-match, blocking a legitimate big amendment. This case
# changes the Decision AND rides a big amendment block whose 'Revised' keyword sits at the top.
# The block's '+' lines must exceed the OS pipe buffer (~64 KiB) so the upstream `grep '^+'` is
# still writing when the downstream `grep -qiE` exits — that is the SIGPIPE precondition. ~1000
# padded lines (~100 KiB of '+' output) clears 64 KiB with margin across GNU grep / ugrep. It
# must be read as an amendment (rc 0): fails against the pre-fix pipeline, passes against the
# decoupled (tmp-file) form.
big_amendment="$(printf '*Revised 2026-01-01: this amendment is intentionally long to overflow the pipe buffer.*\n')"
for _i in $(seq 1 1000); do
  big_amendment+=$'\n'"Additional amendment context line ${_i} — padding padding padding padding padding padding padding padding."
done
expect 0 "$(gate_rc 'Accepted (gated by DPO — production gate; see §Gate)' 'Original decision.' 'Rewritten decision.' "$big_amendment")" \
  "large multi-line amendment (keyword at top) passes — no SIGPIPE false-fail (the fix)"

printf '\nadr-immutability.sh — fast PreToolUse mirror (plugin hook)\n'

# hook_emits <status> <edit-old-string> — write an ADR under */adrs/ADR-*.md, feed the hook a
# PreToolUse Edit payload, echo 1 if it warns (additionalContext present), else 0.
hook_emits() {
  local d f out
  d="$(mktemp -d "$WORK/hook.XXXXXX")/adrs"; mkdir -p "$d"
  f="$d/ADR-PC-999-x.md"
  mkadr "$1" "Decision line alpha. Decision line beta." > "$f"
  out="$(printf '{"tool_name":"Edit","tool_input":{"file_path":"%s","old_string":"%s"}}' "$f" "$2" | bash "$HOOK")"
  case "$out" in *additionalContext*) echo 1 ;; *) echo 0 ;; esac
}

# F — REGRESSION: editing a gated-Accepted Decision warns.
expect 1 "$(hook_emits 'Accepted (gated by DPO — production gate; see §Gate)' 'Decision line alpha.')" \
  "hook warns on an edit to a gated-Accepted Decision (the fix)"
# G — canonical Accepted still warns.
expect 1 "$(hook_emits 'Accepted' 'Decision line alpha.')" \
  "hook still warns on an edit to a canonical Accepted Decision"
# H — Proposed is silent.
expect 0 "$(hook_emits 'Proposed' 'Decision line alpha.')" \
  "hook is silent on a Proposed Decision edit"
# I — no false positive: an edit OUTSIDE the Decision section is silent.
expect 0 "$(hook_emits 'Accepted (gated by DPO)' 'Some context prose.')" \
  "hook is silent on an edit outside the Decision section"

printf '\n%d test(s), %d failure(s)\n' "$TESTS" "$FAILS"
[ "$FAILS" -eq 0 ]
