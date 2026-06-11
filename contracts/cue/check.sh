#!/usr/bin/env bash
# contracts/cue/check.sh — soundness gate for the CUE family-schema language
# (ADR-PC-006 C.1). Run by the `contracts` path-scoped CI job (ADR-PC-019 §P1)
# and locally via `make contracts-check`.
#
# This is *not* the depths-1–4 pack-validate binary (C.2): it has no pack data
# and resolves no pack-bound primitive. It proves three things about the schema
# itself, using only the pinned `cue` CLI (mise.toml, CUE 0.16.1):
#   1. the .cue files are canonically formatted (cue fmt --check)
#   2. the schema compiles (cue vet of the schema alone)
#   3. every valid fixture is accepted and every invalid fixture is rejected
#      — the schema's behavioural contract, including the closed-struct
#      "no DSL escape hatch" guarantee (ADR-PC-006 Decision).
set -euo pipefail

cd "$(dirname "$0")"

COMMON="common.cue"

# Families are AUTO-DISCOVERED from families/*.cue — there is no hand-maintained list, so a
# new family's schema + fixtures are gated the moment its .cue lands (matching the
# auto-discovering Avro/AsyncAPI gates; a hand-kept list silently skips any family it omits).
# The testdata dir is the file's basename; the root definition is its PascalCase form
# (#<Family>, the ADR-PC-006 / new-family-schema convention). A name that does not resolve to a
# real definition fails `cue vet` loudly below — never a silent skip.
# bash 3.2-safe (macOS): no associative arrays, no mapfile.
kebab_to_def() { # term-deposit | savings_account -> #TermDeposit | #SavingsAccount
	local name="$1" out="" part
	local IFS='-_'
	for part in $name; do
		out+="$(printf '%s' "${part:0:1}" | tr '[:lower:]' '[:upper:]')${part:1}"
	done
	printf '#%s' "$out"
}

fail=0

echo "== cue fmt --check =="
if ! cue fmt --check ./... 2>/tmp/cue-fmt-err; then
	echo "  NOT canonically formatted — run 'cue fmt ./...':"
	sed 's/^/    /' /tmp/cue-fmt-err
	fail=1
fi

for schema in families/*.cue; do
	[ -e "$schema" ] || continue
	family="$(basename "$schema" .cue)"
	def="$(kebab_to_def "$family")"
	echo "== ${family} (${def}) =="

	if ! cue vet "$COMMON" "$schema" 2>/tmp/cue-err; then
		echo "  schema does not compile:"
		sed 's/^/    /' /tmp/cue-err
		fail=1
		continue
	fi

	for f in "testdata/${family}/valid/"*.yaml; do
		[ -e "$f" ] || continue
		if cue vet -d "$def" "$f" "$COMMON" "$schema" 2>/tmp/cue-err; then
			echo "  ok (accepted)  $(basename "$f")"
		else
			echo "  FAIL (should accept)  $(basename "$f"):"
			sed 's/^/    /' /tmp/cue-err
			fail=1
		fi
	done

	for f in "testdata/${family}/invalid/"*.yaml; do
		[ -e "$f" ] || continue
		if cue vet -d "$def" "$f" "$COMMON" "$schema" 2>/dev/null; then
			echo "  LEAK (should reject)  $(basename "$f")"
			fail=1
		else
			echo "  ok (rejected)  $(basename "$f")"
		fi
	done
done

# Pack-manifest reject fixtures. The ACCEPT case is the real packs/pt.2026.1
# pack.yaml, validated end-to-end by packs/pack.sh; here we pin that #Manifest
# (pack/pack.cue) rejects malformed manifests — one rule per file.
if [ -d testdata/pack/invalid ]; then
	echo "== pack manifest (#Manifest) =="
	for f in testdata/pack/invalid/*.yaml; do
		[ -e "$f" ] || continue
		if cue vet -d '#Manifest' "$f" pack/pack.cue 2>/dev/null; then
			echo "  LEAK (should reject)  $(basename "$f")"
			fail=1
		else
			echo "  ok (rejected)  $(basename "$f")"
		fi
	done
fi

if [ "$fail" -ne 0 ]; then
	echo "FAILED"
	exit 1
fi
echo "OK"
