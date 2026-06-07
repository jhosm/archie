#!/usr/bin/env bash
# scripts/avro-compat-check.sh — the residual ADR-IC-002 §P3 schema-compatibility
# gate (G.3 / bd babelstone-c3bq). Run by the `contracts` path-scoped CI job
# (ADR-PC-019 §P1) and locally via `make avro-compat-check`.
#
# What it proves, for every contracts/avro/**/*.avsc in the working tree:
#
#   1. §P1 — the on-disk path mirrors the Avro namespace + record name, so the
#      registry subject ({namespace}.{name}-value) reconstructs from the path.
#   2. §P2 — every nullable union lists "null" first (so the field can default
#      to null and BACKWARD checks behave). A static lint, fast and Docker-free.
#   3. §P3 — the schema is registry-COMPATIBLE with its previously-published
#      version. "Previously published" is the schema as of the git merge-base
#      with origin/main: we register THAT version into a throwaway Confluent
#      Schema Registry under BACKWARD compatibility, then ask the registry's
#      /compatibility endpoint whether the working-tree schema may follow it.
#      An incompatible evolution introduced inside a PR fails here, not in prod.
#
# Why a real registry (not an offline resolver): ADR-IC-002 chose the Confluent
# SR API as the authority for compatibility. The Redpanda built-in SR is its POC
# implementation (ADR-IC-002 Decision) and is what the dev stack + Testcontainers
# already use. Reusing it makes this gate agree exactly with what publish-time
# enforcement would do — no second, hand-rolled compatibility implementation to
# drift from the registry's actual semantics.
#
# Compatibility mode: BACKWARD — the ADR-IC-002 Consequences default ("producer
# evolves first; old consumers can read new data"). Set explicitly so the gate
# never depends on the registry's shipped default.
#
# Skip the registry round-trip (steps 1–2 only, no Docker) with:
#   AVRO_COMPAT_STATIC_ONLY=1 ./scripts/avro-compat-check.sh
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO_ROOT"

AVRO_DIR="contracts/avro"
BASELINE_REF="${AVRO_COMPAT_BASELINE_REF:-origin/main}"
COMPAT_LEVEL="${AVRO_COMPAT_LEVEL:-BACKWARD}"
SR_IMAGE="docker.redpanda.com/redpandadata/redpanda:v24.3.1" # pinned, == infra/compose.yaml
STATIC_ONLY="${AVRO_COMPAT_STATIC_ONLY:-0}"

fail=0
note()  { printf '%s\n' "$*"; }
err()   { printf '  ERROR: %s\n' "$*" >&2; fail=1; }

command -v jq >/dev/null 2>&1 || { echo "FATAL: jq is required (brew install jq)"; exit 2; }

if [ ! -d "$AVRO_DIR" ]; then
	note "no $AVRO_DIR directory — nothing to check"
	exit 0
fi

# Collect the working-tree schemas (recursive glob; ADR-IC-002 A1 directory layout).
schemas=()
while IFS= read -r -d '' f; do schemas+=("$f"); done \
	< <(find "$AVRO_DIR" -name '*.avsc' -print0 | sort -z)

if [ "${#schemas[@]}" -eq 0 ]; then
	note "no .avsc files under $AVRO_DIR — nothing to check"
	exit 0
fi

# ---------------------------------------------------------------------------
# subject_of <file> — derive the registry subject from the schema's own
# namespace + name (the authoritative source, ADR-IC-002 §P1/A1), and assert
# the on-disk path mirrors it. Echoes the subject on success.
# ---------------------------------------------------------------------------
subject_of() {
	local file="$1" ns name expected_path subject
	ns="$(jq -r '.namespace // empty' "$file")"
	name="$(jq -r '.name // empty' "$file")"
	if [ -z "$ns" ] || [ -z "$name" ]; then
		err "$file: schema is missing a namespace or name"
		return 1
	fi
	# §P1/A1: contracts/avro/{ns-as-path}/{name}.avsc
	expected_path="$AVRO_DIR/${ns//.//}/${name}.avsc"
	if [ "$file" != "$expected_path" ]; then
		err "$file: path does not mirror namespace+name (ADR-IC-002 §P1/A1; expected $expected_path)"
		return 1
	fi
	subject="${ns}.${name}-value"
	printf '%s' "$subject"
}

# ---------------------------------------------------------------------------
# lint_null_first <file> — ADR-IC-002 §P2: in every union that includes "null",
# "null" must be the first branch. Catches the verbose-union authoring error the
# ADR calls out as a common source of mistakes (Residual risks).
# ---------------------------------------------------------------------------
lint_null_first() {
	local file="$1" bad
	# A union in Avro is a JSON array of type-branches. Find EVERY array anywhere
	# in the document (record/nested-field types, array `items`, map `values`,
	# union-of-union) that contains the "null" branch but does not list it first.
	# `..` recurses through all values, so a null-second union nested inside an
	# array's items or a map's values is caught too (ADR-IC-002 §P2).
	bad="$(jq -r '
		[ .. | select(type == "array")
		      | select(any(.[]; . == "null") and .[0] != "null") ]
		| length
	' "$file")"
	if [ "$bad" != "0" ]; then
		err "$file: a nullable union does not list \"null\" first (ADR-IC-002 §P2)"
	fi
}

note "== Avro schema checks (ADR-IC-002 §P1/§P2/§P3) =="
note "baseline ref: $BASELINE_REF   compatibility: $COMPAT_LEVEL"
note ""

# --- Static checks (§P1 path mirror + §P2 null-first), always run. -----------
declare -a subjects=()
for f in "${schemas[@]}"; do
	subj="$(subject_of "$f")" || { subjects+=("__SKIP__"); continue; }
	subjects+=("$subj")
	lint_null_first "$f"
	note "  static ok   $f  ->  $subj"
done

if [ "$STATIC_ONLY" = "1" ]; then
	note ""
	[ "$fail" -eq 0 ] && { note "STATIC OK (registry compatibility skipped)"; exit 0; }
	note "STATIC FAILED"; exit 1
fi

# A static failure means a subject/path is unsound — don't bother the registry.
if [ "$fail" -ne 0 ]; then
	note ""
	note "FAILED (static checks) — registry compatibility skipped"
	exit 1
fi

# --- §P3 registry compatibility -------------------------------------------------
command -v docker >/dev/null 2>&1 || { echo "FATAL: docker is required for the §P3 registry gate (or set AVRO_COMPAT_STATIC_ONLY=1)"; exit 2; }

note ""
note "== §P3 registry compatibility vs $BASELINE_REF =="

# Make sure the baseline ref is present (CI checks out a shallow PR merge; fetch it).
if ! git rev-parse --verify --quiet "$BASELINE_REF" >/dev/null; then
	remote="${BASELINE_REF%%/*}"
	branch="${BASELINE_REF#*/}"
	note "  baseline $BASELINE_REF not in this clone — fetching $branch from $remote"
	git fetch --quiet --depth=1 "$remote" "$branch" || true
fi
if ! git rev-parse --verify --quiet "$BASELINE_REF" >/dev/null; then
	note "  WARNING: baseline ref $BASELINE_REF is unavailable — treating every schema as new."
	note "           (No previously-published version means nothing to break; this is a no-op)."
	BASELINE_REF=""
fi

CID=""
SR_URL=""
cleanup() { [ -n "$CID" ] && docker rm -f "$CID" >/dev/null 2>&1 || true; }
trap cleanup EXIT

note "  starting throwaway Redpanda SR ($SR_IMAGE) ..."
CID="$(docker run -d -P "$SR_IMAGE" \
	redpanda start --mode dev-container --smp 1 --default-log-level=warn \
	--schema-registry-addr 0.0.0.0:8081)"

# Resolve the host-mapped SR port.
host_port="$(docker port "$CID" 8081/tcp | head -1 | sed 's/.*://')"
SR_URL="http://127.0.0.1:${host_port}"

# Wait for the SR REST endpoint to answer.
ready=0
for _ in $(seq 1 60); do
	if curl -fsS "$SR_URL/subjects" >/dev/null 2>&1; then ready=1; break; fi
	sleep 1
done
[ "$ready" -eq 1 ] || { echo "FATAL: Redpanda Schema Registry did not become ready at $SR_URL"; docker logs "$CID" 2>&1 | tail -20; exit 2; }
note "  SR ready at $SR_URL"

# Set the global compatibility level (ADR-IC-002: BACKWARD default).
curl -fsS -X PUT "$SR_URL/config" \
	-H 'Content-Type: application/vnd.schemaregistry.v1+json' \
	-d "{\"compatibility\":\"$COMPAT_LEVEL\"}" >/dev/null
note "  global compatibility set to $COMPAT_LEVEL"
note ""

# post_schema_json <file> -> the SR request body { "schemaType":"AVRO", "schema":"<escaped>" }
post_body() { jq -Rs '{schemaType:"AVRO", schema:.}' "$1"; }

for i in "${!schemas[@]}"; do
	f="${schemas[$i]}"
	subject="${subjects[$i]}"
	[ "$subject" = "__SKIP__" ] && continue

	# Is there a baseline (previously-published) version of THIS file on the baseline ref?
	baseline_json=""
	if [ -n "$BASELINE_REF" ] && git cat-file -e "$BASELINE_REF:$f" 2>/dev/null; then
		baseline_json="$(git show "$BASELINE_REF:$f")"
	fi

	if [ -z "$baseline_json" ]; then
		note "  NEW          $subject ($f) — no $BASELINE_REF version; nothing to break, skip"
		continue
	fi

	# Register the baseline version under the subject. (No-op if identical.)
	reg_body="$(printf '%s' "$baseline_json" | jq -Rs '{schemaType:"AVRO", schema:.}')"
	reg_http="$(curl -sS -o /tmp/avro-reg-resp -w '%{http_code}' \
		-X POST "$SR_URL/subjects/$subject/versions" \
		-H 'Content-Type: application/vnd.schemaregistry.v1+json' \
		-d "$reg_body")"
	if [ "$reg_http" != "200" ]; then
		err "$f: could not register the baseline version (HTTP $reg_http): $(cat /tmp/avro-reg-resp)"
		continue
	fi

	# Ask the registry whether the WORKING-TREE schema may follow the baseline.
	chk_body="$(post_body "$f")"
	chk_http="$(curl -sS -o /tmp/avro-chk-resp -w '%{http_code}' \
		-X POST "$SR_URL/compatibility/subjects/$subject/versions/latest?verbose=true" \
		-H 'Content-Type: application/vnd.schemaregistry.v1+json' \
		-d "$chk_body")"
	if [ "$chk_http" != "200" ]; then
		err "$f: compatibility check call failed (HTTP $chk_http): $(cat /tmp/avro-chk-resp)"
		continue
	fi

	is_compat="$(jq -r '.is_compatible' /tmp/avro-chk-resp)"
	if [ "$is_compat" = "true" ]; then
		note "  COMPATIBLE   $subject ($f) — $COMPAT_LEVEL with $BASELINE_REF"
	else
		msgs="$(jq -r '(.messages // []) | join("; ")' /tmp/avro-chk-resp)"
		err "$f: $COMPAT_LEVEL-INCOMPATIBLE evolution vs $BASELINE_REF — ${msgs:-no detail}"
	fi
done

note ""
if [ "$fail" -ne 0 ]; then
	note "FAILED — an Avro schema breaks ADR-IC-002 (§P1/§P2/§P3)."
	exit 1
fi
note "OK — all Avro schemas pass ADR-IC-002 §P1/§P2/§P3."
