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
# One entry per family: "<dir>|<schema.cue>|<#RootDefinition>".
# Plain array (not associative) so macOS's bash 3.2 runs it unchanged.
FAMILIES="term-deposit|families/term-deposit.cue|#TermDeposit"

fail=0

echo "== cue fmt --check =="
if ! cue fmt --check ./... 2>/tmp/cue-fmt-err; then
	echo "  NOT canonically formatted — run 'cue fmt ./...':"
	sed 's/^/    /' /tmp/cue-fmt-err
	fail=1
fi

for entry in $FAMILIES; do
	IFS='|' read -r family schema def <<<"$entry"
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
