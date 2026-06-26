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
#      Schema Registry under the subject's EFFECTIVE compatibility level (see
#      "Per-subject / per-family compatibility override" below), then ask the
#      registry's /compatibility endpoint whether the working-tree schema may
#      follow it. An incompatible evolution introduced in a PR fails here, not
#      in prod.
#
#   4. SHAPE-LOCK — every subject carries a committed day-one golden snapshot of
#      its structural fingerprint (field name + normalised type + logicalType +
#      union shape + optional/default presence). The §P3 registry check is a
#      NO-OP for a brand-new subject (no previously-published version means
#      nothing to break), so a day-one field-type mistake on, say, a new loans.*
#      schema would sail through §P3 and reach prod with only review standing
#      between it and a wrong wire type. The shape-lock closes that gap: a new
#      subject MUST carry a snapshot (authored in the SAME change via
#      --update-shape-lock), and any later structural drift fails the gate until
#      the snapshot is intentionally re-locked. This is the day-one analogue of
#      §P3's evolution check — §P3 proves a CHANGE stays compatible; the
#      shape-lock proves the FIRST shape was reviewed and is now pinned. The
#      snapshot is doc-insensitive (a doc edit never trips it) and Docker-free,
#      so it runs in both the full and the AVRO_COMPAT_STATIC_ONLY=1 path.
#      Snapshots live under contracts/avro/.shape-lock/{subject}.json.
#
# Why a real registry (not an offline resolver): ADR-IC-002 chose the Confluent
# SR API as the authority for compatibility. The Redpanda built-in SR is its POC
# implementation (ADR-IC-002 Decision) and is what the dev stack + Testcontainers
# already use. Reusing it makes this gate agree exactly with what publish-time
# enforcement would do — no second, hand-rolled compatibility implementation to
# drift from the registry's actual semantics.
#
# Default compatibility mode: BACKWARD — the ADR-IC-002 Consequences default
# ("producer evolves first; old consumers can read new data"). Set explicitly so
# the gate never depends on the registry's shipped default.
#
# Per-subject / per-family compatibility override (ADR-IC-002 §Consequences names
# FULL for events with many known consumers; the §Residual-risks "compatibility
# group overrides — per-subject deviation from the global compatibility setting"
# is exactly this seam). Drop a `.avro-compat` sidecar in any directory under
# contracts/avro/. Each non-blank, non-`#` line is `KEY=LEVEL`:
#
#     # contracts/avro/deposits/term_deposit/.avro-compat
#     *=FULL                                          # per-FAMILY default for this subtree
#     deposits.term_deposit.DepositMatured-value=BACKWARD   # per-SUBJECT override
#
# LEVEL is one of BACKWARD / BACKWARD_TRANSITIVE / FORWARD / FORWARD_TRANSITIVE /
# FULL / FULL_TRANSITIVE / NONE (the Confluent SR vocabulary). Resolution for a
# subject: the most specific match wins — a per-subject line in the deepest
# directory beats a `*` wildcard, and a deeper directory beats a shallower one;
# absent any match the global default (AVRO_COMPAT_LEVEL, BACKWARD) applies. The
# override is NOT a relaxation back door: the §P3 registry still enforces the
# chosen level for real, so naming FULL makes the gate STRICTER, not weaker.
#
# Re-lock the shape snapshots after an intentional schema change (the only way a
# committed snapshot ever changes):
#   ./scripts/avro-compat-check.sh --update-shape-lock      # (or AVRO_SHAPE_LOCK_UPDATE=1)
#
# Skip the registry round-trip (steps 1, 2 + 4 only, no Docker) with:
#   AVRO_COMPAT_STATIC_ONLY=1 ./scripts/avro-compat-check.sh
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO_ROOT"

AVRO_DIR="contracts/avro"
SHAPE_LOCK_DIR="$AVRO_DIR/.shape-lock"          # day-one golden snapshots (step 4)
COMPAT_SIDECAR=".avro-compat"                    # per-directory override file
BASELINE_REF="${AVRO_COMPAT_BASELINE_REF:-origin/main}"
COMPAT_LEVEL="${AVRO_COMPAT_LEVEL:-BACKWARD}"   # global default; sidecars refine per subject/family
SR_IMAGE="docker.redpanda.com/redpandadata/redpanda:v24.3.1" # pinned, == infra/compose.yaml
STATIC_ONLY="${AVRO_COMPAT_STATIC_ONLY:-0}"
UPDATE_SHAPE_LOCK="${AVRO_SHAPE_LOCK_UPDATE:-0}" # 1 ⇒ (re)write snapshots instead of verifying

# --update-shape-lock is the ergonomic equivalent of AVRO_SHAPE_LOCK_UPDATE=1.
for arg in "$@"; do
	case "$arg" in
		--update-shape-lock) UPDATE_SHAPE_LOCK=1 ;;
		*) echo "FATAL: unknown argument '$arg' (only --update-shape-lock is accepted)"; exit 2 ;;
	esac
done

# The Confluent SR compatibility vocabulary — an override level must be one of these.
VALID_LEVELS="BACKWARD BACKWARD_TRANSITIVE FORWARD FORWARD_TRANSITIVE FULL FULL_TRANSITIVE NONE"

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

# ---------------------------------------------------------------------------
# is_valid_level <level> — true iff <level> is a Confluent SR compatibility mode.
# ---------------------------------------------------------------------------
is_valid_level() {
	local lvl="$1" v
	for v in $VALID_LEVELS; do [ "$lvl" = "$v" ] && return 0; done
	return 1
}

# ---------------------------------------------------------------------------
# validate_sidecars — lint EVERY `.avro-compat` sidecar under $AVRO_DIR for shape
# (each non-blank/non-# line is KEY=LEVEL with a recognised LEVEL). Runs in the
# MAIN shell (not in a command substitution) so an err() here actually sets the
# `fail` flag — compat_level_for below is invoked via $(...) and so cannot, which
# is why malformed-sidecar reporting lives here, separately from resolution.
# ---------------------------------------------------------------------------
validate_sidecars() {
	local sidecar line key val
	while IFS= read -r -d '' sidecar; do
		while IFS= read -r line || [ -n "$line" ]; do
			line="${line%%#*}"                       # strip trailing comment
			line="$(printf '%s' "$line" | tr -d '[:space:]')"
			[ -z "$line" ] && continue
			key="${line%%=*}"; val="${line#*=}"
			if [ "$key" = "$line" ] || [ -z "$val" ] || [ -z "$key" ]; then
				err "$sidecar: malformed line '$line' (expected KEY=LEVEL, e.g. '*=FULL' or 'deposits.term_deposit.X-value=BACKWARD')"
				continue
			fi
			if ! is_valid_level "$val"; then
				err "$sidecar: '$val' is not a valid compatibility level (one of: $VALID_LEVELS)"
			fi
		done < "$sidecar"
	done < <(find "$AVRO_DIR" -name "$COMPAT_SIDECAR" -print0 | sort -z)
}

# ---------------------------------------------------------------------------
# compat_level_for <file> <subject> — resolve the EFFECTIVE compatibility level
# for a subject from the `.avro-compat` sidecars on the path from the schema's
# own directory UP to $AVRO_DIR. Most-specific wins: in any one directory a
# per-subject line beats the `*` wildcard, and a deeper directory beats a
# shallower one (the first match found while walking UP from the schema dir).
# Falls back to the global $COMPAT_LEVEL. Pure resolver — echoes the level only;
# malformed/bad-level reporting is validate_sidecars' job (see its note on why).
# ---------------------------------------------------------------------------
compat_level_for() {
	local file="$1" subject="$2" dir line key val subj_hit="" star_hit=""
	dir="$(dirname "$file")"
	while :; do
		local sidecar="$dir/$COMPAT_SIDECAR"
		if [ -f "$sidecar" ]; then
			subj_hit=""; star_hit=""
			# Read each KEY=LEVEL line; ignore blanks, # comments, and any line a
			# bad level (already reported by validate_sidecars) would otherwise pick.
			while IFS= read -r line || [ -n "$line" ]; do
				line="${line%%#*}"                       # strip trailing comment
				line="$(printf '%s' "$line" | tr -d '[:space:]')"
				[ -z "$line" ] && continue
				key="${line%%=*}"; val="${line#*=}"
				[ "$key" = "$line" ] && continue          # malformed (no '=')
				is_valid_level "$val" || continue          # ignore an invalid level here
				if [ "$key" = "$subject" ]; then subj_hit="$val"
				elif [ "$key" = "*" ];      then star_hit="$val"
				fi
			done < "$sidecar"
			# Per-subject beats wildcard within this (deepest-so-far) directory.
			if [ -n "$subj_hit" ]; then printf '%s' "$subj_hit"; return 0; fi
			if [ -n "$star_hit" ]; then printf '%s' "$star_hit"; return 0; fi
		fi
		[ "$dir" = "$AVRO_DIR" ] && break
		[ "$dir" = "." ] && break
		dir="$(dirname "$dir")"
	done
	printf '%s' "$COMPAT_LEVEL"
}

# ---------------------------------------------------------------------------
# shape_fingerprint <file> — a canonical, doc-INSENSITIVE structural fingerprint
# of a record schema: namespace + name + the ordered list of fields, each as
# {name, normalised type (scalar / `T@logicalType` / `[null,T]` union / nested
# record / array<items> / map<values>), optional flag, default-presence}. This
# is exactly the day-one structural surface a field-type mistake corrupts; it
# deliberately drops `doc` strings so an explanatory edit never trips the lock.
# ---------------------------------------------------------------------------
shape_fingerprint() {
	jq -S '
		def typefp:
			if type == "string" then .
			elif type == "array" then "[" + ([.[] | typefp] | join(",")) + "]"
			elif type == "object" then
				( .type | typefp )
				+ ( if .logicalType then "@" + .logicalType else "" end)
				+ ( if .type == "array"  then "<items:"  + (.items  | typefp) + ">" else "" end)
				+ ( if .type == "map"    then "<values:" + (.values | typefp) + ">" else "" end)
				+ ( if .type == "record" then "<rec:" + (.name // "") + ":"
					+ ([.fields[] | .name + ":" + (.type | typefp)] | join(",")) + ">" else "" end)
			else tostring end;
		{
			subject: (.namespace + "." + .name + "-value"),
			namespace: .namespace,
			name: .name,
			fields: [ .fields[] | {
				name,
				type: (.type | typefp),
				optional: (.type | (type == "array" and (.[0] == "null"))),
				has_default: (has("default"))
			} ]
		}
	' "$1"
}

# ---------------------------------------------------------------------------
# shape_lock_path <subject> — the on-disk snapshot path for a subject.
# ---------------------------------------------------------------------------
shape_lock_path() { printf '%s/%s.json' "$SHAPE_LOCK_DIR" "$1"; }

# ---------------------------------------------------------------------------
# check_shape_lock <file> <subject> — step 4. In update mode, (re)write the
# snapshot. Otherwise: a missing snapshot is a FAIL (a new subject must be
# day-one locked in the same change); a present-but-divergent snapshot is a FAIL
# (structural drift — re-lock intentionally with --update-shape-lock); a match
# passes silently-ish.
# ---------------------------------------------------------------------------
check_shape_lock() {
	local file="$1" subject="$2" lock current
	lock="$(shape_lock_path "$subject")"
	current="$(shape_fingerprint "$file")"

	if [ "$UPDATE_SHAPE_LOCK" = "1" ]; then
		mkdir -p "$SHAPE_LOCK_DIR"
		printf '%s\n' "$current" > "$lock"
		note "  shape-lock   $subject — snapshot written ($lock)"
		return 0
	fi

	if [ ! -f "$lock" ]; then
		err "$file: NO day-one shape-lock for $subject. A new subject must pin its shape in the SAME change — run: ./scripts/avro-compat-check.sh --update-shape-lock (expected $lock)"
		return 1
	fi

	if [ "$current" != "$(cat "$lock")" ]; then
		err "$file: shape-lock DRIFT for $subject vs $lock. If this structural change is intentional, re-lock it: ./scripts/avro-compat-check.sh --update-shape-lock (and confirm §P3 BACKWARD/effective-level compatibility)."
		return 1
	fi
	note "  shape-lock ok $subject"
}

note "== Avro schema checks (ADR-IC-002 §P1/§P2/§P3 + shape-lock) =="
note "baseline ref: $BASELINE_REF   compatibility: $COMPAT_LEVEL"
note ""

# --- Static checks (§P1 path mirror + §P2 null-first + step-4 shape-lock). ----
# All Docker-free, so they run in every mode (full, static-only, update).
declare -a subjects=()
for f in "${schemas[@]}"; do
	subj="$(subject_of "$f")" || { subjects+=("__SKIP__"); continue; }
	subjects+=("$subj")
	lint_null_first "$f"
	note "  static ok   $f  ->  $subj"
	check_shape_lock "$f" "$subj"
done

# Lint the per-subject/per-family compatibility sidecars (in the main shell, so a
# bad level actually fails the gate — see validate_sidecars' note).
validate_sidecars

# In update mode the run's whole purpose is to (re)write snapshots — do that and
# stop. The registry round-trip is irrelevant when re-locking.
if [ "$UPDATE_SHAPE_LOCK" = "1" ]; then
	note ""
	[ "$fail" -eq 0 ] && { note "SHAPE-LOCK SNAPSHOTS UPDATED under $SHAPE_LOCK_DIR"; exit 0; }
	note "SHAPE-LOCK UPDATE FAILED (a schema is structurally unsound — fix it first)"; exit 1
fi

if [ "$STATIC_ONLY" = "1" ]; then
	note ""
	[ "$fail" -eq 0 ] && { note "STATIC OK (§P1/§P2 + shape-lock; registry compatibility skipped)"; exit 0; }
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

# Set the GLOBAL compatibility level (ADR-IC-002: BACKWARD default). Subjects
# with a `.avro-compat` override get their effective level PER-SUBJECT below,
# before their own compatibility check (the SR honours a subject-level config
# over the global one — the §Residual-risks "compatibility group override").
curl -fsS -X PUT "$SR_URL/config" \
	-H 'Content-Type: application/vnd.schemaregistry.v1+json' \
	-d "{\"compatibility\":\"$COMPAT_LEVEL\"}" >/dev/null
note "  global compatibility set to $COMPAT_LEVEL (per-subject overrides applied where a .avro-compat sidecar names one)"
note ""

# post_schema_json <file> -> the SR request body { "schemaType":"AVRO", "schema":"<escaped>" }
post_body() { jq -Rs '{schemaType:"AVRO", schema:.}' "$1"; }

for i in "${!schemas[@]}"; do
	f="${schemas[$i]}"
	subject="${subjects[$i]}"
	[ "$subject" = "__SKIP__" ] && continue

	# Resolve THIS subject's effective compatibility level (sidecar override or
	# the global default) and pin it as the subject-level SR config, so the
	# /compatibility check below runs at exactly that level.
	level="$(compat_level_for "$f" "$subject")"
	if [ "$level" != "$COMPAT_LEVEL" ]; then
		cfg_http="$(curl -sS -o /tmp/avro-cfg-resp -w '%{http_code}' \
			-X PUT "$SR_URL/config/$subject" \
			-H 'Content-Type: application/vnd.schemaregistry.v1+json' \
			-d "{\"compatibility\":\"$level\"}")"
		if [ "$cfg_http" != "200" ]; then
			err "$f: could not set per-subject compatibility $level for $subject (HTTP $cfg_http): $(cat /tmp/avro-cfg-resp)"
			continue
		fi
	fi

	# Is there a baseline (previously-published) version of THIS file on the baseline ref?
	baseline_json=""
	if [ -n "$BASELINE_REF" ] && git cat-file -e "$BASELINE_REF:$f" 2>/dev/null; then
		baseline_json="$(git show "$BASELINE_REF:$f")"
	fi

	if [ -z "$baseline_json" ]; then
		note "  NEW          $subject ($f) — no $BASELINE_REF version; nothing to break, skip (shape-lock pinned its day-one shape; effective level $level)"
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
		note "  COMPATIBLE   $subject ($f) — $level with $BASELINE_REF"
	else
		msgs="$(jq -r '(.messages // []) | join("; ")' /tmp/avro-chk-resp)"
		err "$f: $level-INCOMPATIBLE evolution vs $BASELINE_REF — ${msgs:-no detail}"
	fi
done

note ""
if [ "$fail" -ne 0 ]; then
	note "FAILED — an Avro schema breaks ADR-IC-002 (§P1/§P2/§P3) or its day-one shape-lock."
	exit 1
fi
note "OK — all Avro schemas pass ADR-IC-002 §P1/§P2/§P3 + the day-one shape-lock."
