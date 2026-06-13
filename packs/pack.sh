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

# Relative-path → root-definition map for the FORMAT-FIXED data files every pack
# carries at a known name (ADR-PC-007 §P1). The rate-sheet-ref files are NOT
# listed here: they are a per-pack set the manifest enumerates, so we DERIVE them
# from pack.yaml at validate time (see manifest_data_files) — a pack that declares
# an extra rate-sheet-ref then gets covered automatically, never silently skipped.
# Plain string list for bash 3.2 portability.
readonly FIXED_DATA_FILES="
pack.yaml|#Manifest
primitives/day-count.yaml|#DayCounts
primitives/withholding.yaml|#Withholding
primitives/fgd.yaml|#Fgd
primitives/reporting.yaml|#Reporting
parameters/constants.yaml|#Parameters
test-corpus/canonical-instances.yaml|#CanonicalInstances
"

# The engine-GENERATED corpus file (ADR-PC-007 §P5): not hand-authored, so it has
# no input-side root def and is handled separately (see validate_staging). Excluded
# from the on-disk coverage sweep so its presence is not flagged as uncovered.
readonly GENERATED_FILES="
test-corpus/expected-events.yaml
"

# Build the full relative-path → root-def map for a staged pack: the format-fixed
# files PLUS one rate-sheet-ref file per name the MANIFEST declares in its
# `rate_sheet_refs` array. Emitted on stdout as `rel|#Def` lines so every declared
# file is enumerated from the pack's own manifest, not a hardcoded list.
manifest_data_files() {
	local staging="$1" name refs
	printf '%s' "$FIXED_DATA_FILES"
	# Derive the per-pack rate-sheet-ref files from the manifest. `cue export`
	# (not yq) keeps us on the pinned toolchain; --out text + strings.Join emits
	# one bare name per line. An empty list yields no extra lines.
	if ! refs="$(cue export "$staging/pack.yaml" \
		-e 'strings.Join(rate_sheet_refs, "\n")' --out text 2>/tmp/pack-cue-err)"; then
		echo "pack.sh: cannot read rate_sheet_refs from manifest:" >&2
		sed 's/^/    /' /tmp/pack-cue-err >&2
		exit 1
	fi
	while IFS= read -r name; do
		[ -n "$name" ] || continue
		echo "rate-sheet-refs/$name.yaml|#RateSheetRefs"
	done <<-EOF
		$refs
	EOF
}

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
	local staging="$1" fail=0 entry rel def data_files covered

	# Enumerate every file-to-validate from the manifest (format-fixed set +
	# manifest-declared rate-sheet-refs) so no declared file is silently skipped.
	data_files="$(manifest_data_files "$staging")"

	# Track what we cover so the on-disk sweep below can flag any data .yaml that
	# is present but matched no validation entry (an undeclared / orphan file).
	covered=""
	for entry in $data_files; do
		rel="${entry%%|*}"
		def="${entry##*|}"
		covered="$covered $rel"
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
	# generation pending (C.3) — a logged skip. But an *unparseable* corpus is a
	# FAILURE, never a skip: masking a parse error to "empty" would let a
	# corrupted BdP/DORA evidence file validate green (ADR-PC-007 fail-loud).
	if [ -f "$staging/test-corpus/expected-events.yaml" ]; then
		# Default JSON output (an int renders as `0`); --out text would itself
		# error on an int, masking real parse errors.
		local n
		if ! n="$(cue export "$staging/test-corpus/expected-events.yaml" -e 'len(expected)' 2>/tmp/pack-cue-err)"; then
			echo "  FAIL          test-corpus/expected-events.yaml does not parse:"
			sed 's/^/    /' /tmp/pack-cue-err
			fail=1
		elif [ "$n" = "0" ]; then
			# C.3 landed the depth-5 SIMULATION (engine PackSimulationDepth5Tests gates the
			# corpus event sequence + < 30 s budget); generating the byte-level expected-events
			# corpus from it is the deferred half (bd babelstone-fnqa), so this stays a logged skip.
			echo "  skip          depth-5 corpus: expected-events.yaml empty (byte-corpus generation pending; sim is PackSimulationDepth5Tests)"
		else
			echo "  note          depth-5 corpus present ($n) — byte-corpus comparison is bd babelstone-fnqa; the sim runs as PackSimulationDepth5Tests"
		fi
	fi

	# No-silent-gap sweep: every data .yaml present on disk must have been in the
	# validation set above (covered) or be an engine-generated file handled
	# specially. A primitive/parameter/rate-sheet/corpus file that is present but
	# matched nothing means the pack carries data NO schema vetted — fail loud
	# (ADR-PC-007), rather than letting it ship unvalidated. `schemas/` is the
	# staged family schema (compiled, not vetted as pack data) and is excluded.
	local found
	while IFS= read -r found; do
		found="${found#"$staging"/}"
		case " $covered " in *" $found "*) continue ;; esac
		case "$GENERATED_FILES" in *"$found"*) continue ;; esac
		echo "  FAIL          uncovered pack data file (declared by no manifest/format entry): $found"
		fail=1
	done <<-EOF
		$(find "$staging" -type f -name '*.yaml' ! -path "$staging/schemas/*" | LC_ALL=C sort)
	EOF
	[ "$fail" -eq 0 ] && echo "  ok            no-silent-gap sweep: all data .yaml covered"

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
	# which is fine for the --oci-layout target. Check oras's OWN exit status —
	# not just a non-empty digest — so a push that errors after printing a
	# Digest line cannot be mistaken for success (ADR-PC-007 fail-loud).
	local push_log digest
	push_log="$(mktemp)"
	if ! (cd "$(dirname "$tar")" && oras push --oci-layout "$layout:$EXPECTED_KEY" \
		--artifact-type "$MEDIA_TYPE" "$(basename "$tar"):$MEDIA_TYPE") >"$push_log" 2>&1; then
		sed 's/^/    /' "$push_log" >&2
		rm -f "$push_log"
		die "oras push failed"
	fi
	digest="$(sed -n 's/^Digest: //p' "$push_log" | tail -1)"
	rm -f "$push_log"
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
