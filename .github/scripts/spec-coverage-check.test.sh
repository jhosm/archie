#!/usr/bin/env bash
# Unit tests for spec-coverage-check.sh — focused on the scripts/*.sh Live-resolution
# branch and its CI-wiring tightening (ADR-PC-020).
#
# REGRESSION GUARD (the reason this file exists): a Live catalogue row with no
# compiled-code home may resolve against a CI shell-script gate under scripts/. The
# original branch accepted ANY scripts/*.sh that merely NAMED the Test ID — binding
# the ADR's "and runs in CI" by convention, not mechanically. An orphaned script no ci.yml
# step ever executes could therefore satisfy a Live row. The tightened branch accepts a
# script only when .github/workflows/ci.yml invokes it — directly, or via a Makefile
# target ci.yml runs. Case B below fails against the pre-fix behaviour and passes
# against the tightened one.
#
# Hermetic: each case builds a throwaway git repo (the checker anchors on
# `git rev-parse --show-toplevel` and reads `git ls-tree HEAD`), so no fixtures on disk
# and no dependence on the real catalogue. Pure bash + git + awk/grep (all on the
# ubuntu-latest runner).
#
# Run: bash .github/scripts/spec-coverage-check.test.sh   (exit 0 = all green)
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT="$HERE/spec-coverage-check.sh"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

TID="FAKE_SCRIPT_GATE"

TESTS=0
FAILS=0
expect() { # <expected> <actual> <label>
  TESTS=$((TESTS + 1))
  if [ "$1" = "$2" ]; then printf '  ok   %s\n' "$3"
  else FAILS=$((FAILS + 1)); printf '  FAIL %s (expected [%s], got [%s])\n' "$3" "$1" "$2"; fi
}

# mkrepo <dir> — a minimal-but-valid fixture repo: one catalogue with a single Live row
# whose ONLY candidate evidence is scripts/fake-gate.sh (no code dirs), plus the
# governing ADR referencing the row back. Cases then vary just the CI wiring around it.
mkrepo() {
  local wc="$1" adrs="$1/docs/product-management/product_concepts/adrs"
  mkdir -p "$adrs" "$wc/scripts" "$wc/.github/workflows"

  cat > "$adrs/commitment-catalogue.md" <<EOF
# Commitment catalogue (fixture)

## The seed

| # | Commitment | Governing source | Gate | Test ID | Status |
|---|---|---|---|---|---|
| 1 | the fixture script-gate claim | [ADR-PC-900](./ADR-PC-900-fixture.md) | CI script | \`$TID\` | Live |
EOF

  cat > "$adrs/ADR-PC-900-fixture.md" <<EOF
# ADR-PC-900: fixture

| Field | Value |
|---|---|
| Status | Accepted |
| Shape | Tool-selection |

## Decision

Fixture decision.

## Verifiable commitments

- \`$TID\` — the fixture gate.
EOF

  printf '#!/usr/bin/env bash\n# gate for %s\nexit 0\n' "$TID" > "$wc/scripts/fake-gate.sh"

  # A ci.yml exists in every case; cases append the wiring (or leave it absent).
  printf 'name: ci\njobs:\n  x:\n    steps:\n' > "$wc/.github/workflows/ci.yml"

  git -C "$wc" init -q
  git -C "$wc" config user.email t@example.com
  git -C "$wc" config user.name tester
  git -C "$wc" config commit.gpgsign false
}

# run_check <dir> — commit whatever the case staged, run the checker, echo its exit code.
run_check() {
  git -C "$1" add -A
  git -C "$1" commit -qm fixture
  ( cd "$1" && bash "$SCRIPT" ) >/dev/null 2>&1
  echo $?
}

printf 'spec-coverage-check.sh — scripts/*.sh Live-resolution branch (ADR-PC-020 §P6)\n'

# A — a script gate INVOKED DIRECTLY by ci.yml resolves the Live row (the
# OBS_PLANE_RBAC / grafana-rbac-check.sh shape; must keep passing after the tightening).
wc="$WORK/a"; mkrepo "$wc"
printf '      - run: ./scripts/fake-gate.sh\n' >> "$wc/.github/workflows/ci.yml"
expect 0 "$(run_check "$wc")" "ci.yml-invoked script gate resolves the Live row"

# B — REGRESSION: an ORPHANED script (names the Test ID; no ci.yml step runs it) must
# NOT resolve the row. Pre-tightening this exited 0.
wc="$WORK/b"; mkrepo "$wc"
expect 1 "$(run_check "$wc")" "orphaned script gate does NOT resolve the row (the fix)"

# C — make-target indirection: ci.yml runs \`make fake-gate\`, whose recipe invokes the
# script — the "or a make target CI invokes" half of the wiring contract.
wc="$WORK/c"; mkrepo "$wc"
printf 'fake-gate:\n\t./scripts/fake-gate.sh\n' > "$wc/Makefile"
printf '      - run: make fake-gate\n' >> "$wc/.github/workflows/ci.yml"
expect 0 "$(run_check "$wc")" "script gate invoked via a ci.yml-run make target resolves"

# D — a make target invoking the script exists, but ci.yml never runs it: still orphaned.
wc="$WORK/d"; mkrepo "$wc"
printf 'fake-gate:\n\t./scripts/fake-gate.sh\n' > "$wc/Makefile"
printf '      - run: make something-else\n' >> "$wc/.github/workflows/ci.yml"
expect 1 "$(run_check "$wc")" "make target not run by ci.yml does not resolve"

# E — control: a Live row resolved by a CODE-DIR test is untouched by the tightening
# (the scripts branch is only consulted when no code dir names the Test ID).
wc="$WORK/e"; mkrepo "$wc"
rm -f "$wc/scripts/fake-gate.sh"
mkdir -p "$wc/engine"
printf '// %s exercised here.\n' "$TID" > "$wc/engine/FixtureGateTests.cs"
expect 0 "$(run_check "$wc")" "code-dir-resolved Live row is unaffected (control)"

printf '\n%d test(s), %d failure(s)\n' "$TESTS" "$FAILS"
[ "$FAILS" -eq 0 ]
