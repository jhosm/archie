#!/usr/bin/env bash
# packs/pack.sh — pack format build / verify tooling (ADR-PC-007).
#
# A pack ships as auditor-readable YAML data + bundled `.cue` family schemas,
# packed into a single OCI tar layer (media type
# application/vnd.babelstone.pack.v1+yaml), pushed/pulled BY DIGEST, never by
# tag (§P2), and cosign-signed. The version key is pt.YYYY.N (= pack_id.version),
# immutable once published (§P1).
#
# Subcommands:
#   validate <packdir>                    stage schemas + cue-vet manifest & data
#   build    <packdir> [--layout DIR]     validate, then oras push (prints digest)
#   verify   <packdir> --digest SHA [--layout DIR]
#                                         pull by digest + re-validate
#   sign     <registry-ref>@<digest>      cosign sign  (keyless in CI — Q.5)
#   verify-signature <registry-ref>@<digest>
#                                         cosign verify (the validated-in-CI attestation)
#
# `validate` + `build` + `verify` are fully offline (oras OCI layout, no
# registry, no docker) and run in the `packs` CI job. `sign`/`verify-signature`
# need a registry + OIDC/key; keyless OIDC wiring into CI is story Q.5.
set -euo pipefail

readonly MEDIA_TYPE="application/vnd.babelstone.pack.v1+yaml"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
readonly ROOT
readonly CONTRACTS_CUE="$ROOT/contracts/cue"
readonly PACK_SCHEMA="$CONTRACTS_CUE/pack/pack.cue"

# Relative-path → root-definition map for every data file in a pack. Same for
# all packs (the format is fixed). Plain string list for bash 3.2 portability.
readonly DATA_FILES="
pack.yaml|#Manifest
primitives/day-count.yaml|#DayCounts
primitives/withholding.yaml|#Withholding
primitives/fgd.yaml|#Fgd
primitives/reporting.yaml|#Reporting
parameters/constants.yaml|#Parameters
rate-sheet-refs/deposits-pt.yaml|#RateSheetRefs
test-corpus/canonical-instances.yaml|#CanonicalInstances
"

die() {
	echo "pack.sh: $*" >&2
	exit 1
}

# Stage a build dir: the pack source + the digest-pinned family schemas copied
# in from /contracts/cue (the single source of truth — C.1). The committed pack
# source does NOT carry schemas/; the artefact does, so it is self-contained.
stage() {
	local packdir="$1" staging="$2"
	cp -R "$packdir"/. "$staging"/
	rm -rf "$staging/schemas"
	mkdir -p "$staging/schemas"
	cp -f "$CONTRACTS_CUE/common.cue" "$staging/schemas/common.cue"
	cp -f "$CONTRACTS_CUE/families/term-deposit.cue" "$staging/schemas/term-deposit.cue"
}

validate_staging() {
	local staging="$1" fail=0 entry rel def

	for entry in $DATA_FILES; do
		rel="${entry%%|*}"
		def="${entry##*|}"
		[ -f "$staging/$rel" ] || die "missing required pack file: $rel"
		if cue vet -d "$def" "$staging/$rel" "$PACK_SCHEMA" 2>/tmp/pack-cue-err; then
			echo "  ok ($def)  $rel"
		else
			echo "  FAIL ($def)  $rel:"
			sed 's/^/    /' /tmp/pack-cue-err
			fail=1
		fi
	done

	# The bundled family schemas must compile (depth-1 soundness).
	if cue vet "$staging/schemas/common.cue" "$staging/schemas/term-deposit.cue" 2>/tmp/pack-cue-err; then
		echo "  ok            schemas/ compile"
	else
		echo "  FAIL          schemas/ do not compile:"
		sed 's/^/    /' /tmp/pack-cue-err
		fail=1
	fi

	# The pack version key must match the directory name (pt.YYYY.N invariant).
	local pack_id pack_version key
	pack_id="$(cue export "$staging/pack.yaml" -e pack_id --out text)"
	pack_version="$(cue export "$staging/pack.yaml" -e pack_version --out text)"
	key="${pack_id}.${pack_version}"
	if [ "$key" != "$EXPECTED_KEY" ]; then
		echo "  FAIL          version key $key != directory name $EXPECTED_KEY"
		fail=1
	else
		echo "  ok            version key $key matches directory"
	fi

	# Sealed corpus: expected-events.yaml is engine-GENERATED (§P5). Empty ⇒
	# generation pending (C.3) — a logged skip, never a silent pass.
	if [ -f "$staging/test-corpus/expected-events.yaml" ]; then
		local n
		n="$(cue export "$staging/test-corpus/expected-events.yaml" -e 'len(expected)' --out text 2>/dev/null || echo 0)"
		if [ "$n" = "0" ]; then
			echo "  skip          depth-5 corpus: expected-events.yaml empty (generation pending, C.3)"
		else
			echo "  note          depth-5 corpus present ($n) — depth-5 sim is C.3, not run here"
		fi
	fi

	[ "$fail" -eq 0 ] || die "validation failed"
}

# Deterministic tar of the staging dir. On GNU tar (CI/Linux — where ADR-PC-007
# publication actually runs) the layer is byte-reproducible; bsd tar (macOS dev)
# is sorted/owner-normalised but mtime is not pinned (local convenience only).
make_tar() {
	local staging="$1" out="$2"
	if tar --version 2>/dev/null | grep -q GNU; then
		tar --sort=name --mtime='UTC 2020-01-01' --owner=0 --group=0 \
			--numeric-owner --format=ustar -cf "$out" -C "$staging" .
	else
		(cd "$staging" && find . -type f | LC_ALL=C sort | tar --uid 0 --gid 0 -cf "$out" -T -)
	fi
}

cmd_validate() {
	local packdir="${1:?usage: pack.sh validate <packdir>}"
	[ -d "$packdir" ] || die "no such pack dir: $packdir"
	EXPECTED_KEY="$(basename "$packdir")"
	local staging
	staging="$(mktemp -d)"
	trap 'rm -rf "$staging"' RETURN
	stage "$packdir" "$staging"
	echo "== validate $EXPECTED_KEY =="
	validate_staging "$staging"
	echo "OK"
}

cmd_build() {
	local packdir="" layout=""
	while [ $# -gt 0 ]; do
		case "$1" in
		--layout) layout="$2"; shift 2 ;;
		*) packdir="$1"; shift ;;
		esac
	done
	[ -n "$packdir" ] || die "usage: pack.sh build <packdir> [--layout DIR]"
	[ -d "$packdir" ] || die "no such pack dir: $packdir"
	EXPECTED_KEY="$(basename "$packdir")"
	[ -n "$layout" ] || layout="$ROOT/.pack-build/$EXPECTED_KEY"

	local staging tar
	staging="$(mktemp -d)"
	tar="$(mktemp -d)/pack.tar"
	trap 'rm -rf "$staging" "$(dirname "$tar")"' RETURN
	stage "$packdir" "$staging"
	# Progress to stderr; stdout carries only the digest, for clean capture.
	echo "== build $EXPECTED_KEY ==" >&2
	validate_staging "$staging" >&2

	make_tar "$staging" "$tar"
	rm -rf "$layout"
	mkdir -p "$layout"
	# oras rejects absolute file paths (so artefact filenames can't leak host
	# paths); push from the tar's dir with a relative name. $layout is absolute,
	# which is fine for the --oci-layout target.
	local digest
	digest="$(cd "$(dirname "$tar")" && oras push --oci-layout "$layout:$EXPECTED_KEY" \
		--artifact-type "$MEDIA_TYPE" "$(basename "$tar"):$MEDIA_TYPE" 2>&1 |
		sed -n 's/^Digest: //p' | tail -1)"
	[ -n "$digest" ] || die "oras push produced no digest"
	echo "  pushed $EXPECTED_KEY @ $digest" >&2
	echo "  layout: $layout" >&2
	echo "$digest"
}

cmd_verify() {
	local packdir="" layout="" digest=""
	while [ $# -gt 0 ]; do
		case "$1" in
		--layout) layout="$2"; shift 2 ;;
		--digest) digest="$2"; shift 2 ;;
		*) packdir="$1"; shift ;;
		esac
	done
	[ -n "$packdir" ] && [ -n "$digest" ] || die "usage: pack.sh verify <packdir> --digest SHA [--layout DIR]"
	EXPECTED_KEY="$(basename "$packdir")"
	[ -n "$layout" ] || layout="$ROOT/.pack-build/$EXPECTED_KEY"

	local out
	out="$(mktemp -d)"
	trap 'rm -rf "$out"' RETURN
	echo "== verify $EXPECTED_KEY @ $digest =="
	oras pull --oci-layout "$layout@$digest" -o "$out" >/dev/null
	tar -xf "$out/pack.tar" -C "$out"
	rm -f "$out/pack.tar"
	validate_staging "$out"
	echo "OK (pulled by digest, re-validated)"
}

cmd_sign() {
	local ref="${1:?usage: pack.sh sign <registry-ref>@<digest>}"
	echo "cosign sign $ref"
	echo "  (production: keyless OIDC — story Q.5. Local: set COSIGN_KEY.)"
	cosign sign ${COSIGN_KEY:+--key "$COSIGN_KEY"} "$ref"
}

cmd_verify_signature() {
	local ref="${1:?usage: pack.sh verify-signature <registry-ref>@<digest>}"
	# A verified signature is the attestation that CUE depths 1–4 passed in CI
	# (ADR-PC-006 §P3): verified-signature ⇒ already-validated.
	cosign verify ${COSIGN_KEY:+--key "${COSIGN_KEY%.key}.pub"} "$ref"
}

EXPECTED_KEY=""
case "${1:-}" in
validate) shift; cmd_validate "$@" ;;
build) shift; cmd_build "$@" ;;
verify) shift; cmd_verify "$@" ;;
sign) shift; cmd_sign "$@" ;;
verify-signature) shift; cmd_verify_signature "$@" ;;
*)
	sed -n '2,30p' "$0" | sed 's/^# \{0,1\}//'
	exit 1
	;;
esac
