#!/usr/bin/env bash
# scripts/openapi-catalog-validate.sh — the ADR-IC-020 OpenAPI catalogue PR gate
# (bd babelstone-ax0b.2). The REST mirror of scripts/asyncapi-catalog-validate.sh: it keeps the
# hand-written OpenAPI specs well-formed, free of breaking changes, and in lockstep with the real
# public route surface Kong exposes. Run by the `contracts` path-scoped CI job (ADR-PC-019 §P1)
# and locally via `make openapi-catalog-validate`.
#
# In plain English: for every OpenAPI spec under contracts/openapi/specs/*.openapi.yaml this proves
# three things, with NO live gateway and NO running server (hermetic — the same posture as the
# AsyncAPI gate):
#
#   (1) SPECTRAL LINT — `spectral lint` with the built-in `oas` ruleset PLUS the FROZEN project
#       .spectral.yaml, which requires the ADR-IC-020 §P1 governance fields (info.x-owner,
#       x-owner-contact, x-status [active|deprecated|sunset], x-gdpr-legal-basis,
#       x-authorized-consumers) — the mirror of the AsyncAPI §P1 field set. Only `error`-severity
#       findings block the build (`--fail-severity=error`); style `warn`s are advisory.
#
#   (2) OASDIFF BREAKING-CHANGE DIFF — each modified spec is diffed against its origin/main version
#       with `oasdiff breaking`; a breaking change fails the build UNLESS the spec carries
#       info.x-breaking-change-approved: true (the mirror of the AsyncAPI §P4 approval gate).
#
#   (3) KONG-ROUTE <-> SPEC RECONCILIATION (the no-drift anchor). Reads infra/kong/kong.yml (never
#       writes it) and, over the PUBLIC route surface:
#         FORWARD  — every public Kong route (path+method) has a matching spec operation;
#         REVERSE  — every spec operation is an exposed public Kong route;
#         NEGATIVE — the internal engine COMMAND surface (POST /v1/deposits) and mTLS-only /
#                    service-principal command channels must NEVER appear as a public spec (agrees
#                    with kong-config-check.sh's POST /v1/deposits absence assertion).
#       Regex route paths are normalised to a canonical shape (a single-segment param, whether a
#       Kong `[^/]+` / `(?<id>[^/]+)` regex or an OpenAPI `{id}` template, canonicalises to `{}`),
#       so /v1/deposits/[^/]+$ (Kong) and /v1/deposits/{id} (spec) compare equal.
#
#       PUBLIC scope: the client-facing edge (constitute + saga stream) and the engine query reads —
#       the orchestrator-edge / orchestrator-sse / engine-query Kong services. EXCLUDED as
#       non-public command/agent channels (OPENAPI_EXCLUDE_SERVICES):
#         * mcp-server            — the LLM agent channel, catalogued via AsyncAPI/MCP, not REST
#                                   (ADR-IC-010; explicitly excluded by ADR-IC-020 / bd ax0b.2).
#         * engine-sor-ops        — the SoR-routed existing-instance money-mover (a gateway-resolved
#                                   internal command surface, ADR-PC-018).
#         * engine-lifecycle-movers — the ADR-PC-036 clock-driven service-principal money-movers
#                                   (a scoped, audited command channel, NOT the public client API).
#       These are the same negative-invariant class as the deliberately-absent POST /v1/deposits:
#       command surfaces, not the public read/constitute API the tier-1 specs (bd ax0b.3) document.
#
#   SSE EXEMPTION: an operation marked x-sse-stream: true streams text/event-stream, not a JSON body,
#   so the response-body check is WAIVED for it (the REST mirror of the AsyncAPI x-compacted case).
#
# TOOLING IS PINNED (no @latest drift — the AsyncAPI gate's discipline): the Spectral CLI and the
# oasdiff image versions are fixed below. Spectral runs via npx (Node ships on the runner); oasdiff
# runs via a pinned Docker image (the ubuntu-latest runner ships Docker, as avro-compat-check relies on).
#
# SELF-TEST (--self-test): runs the gate against the negative fixtures under
# contracts/openapi/_selftest/<case>/ and asserts each FAILS — the executable proof that a missing
# governance field, a spec path that is not a public route, a public route with no spec, and a spec
# describing POST /v1/deposits are all caught (ADR-IC-020 / bd ax0b.2 acceptance criteria).
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO_ROOT"

SPECS_DIR="${OPENAPI_SPECS_DIR:-contracts/openapi/specs}"
KONG_CONFIG="${OPENAPI_KONG_CONFIG:-infra/kong/kong.yml}"
SPECTRAL_RULESET="${OPENAPI_SPECTRAL_RULESET:-.spectral.yaml}"
BASELINE_REF="${OPENAPI_BASELINE_REF:-origin/main}"
SELFTEST_DIR="${OPENAPI_SELFTEST_DIR:-contracts/openapi/_selftest}"

# Pin the tooling so the gate is reproducible across runners (no "@latest" drift).
SPECTRAL_CLI="${OPENAPI_SPECTRAL_CLI:-@stoplight/spectral-cli@6.14.2}"
OASDIFF_IMAGE="${OPENAPI_OASDIFF_IMAGE:-tufin/oasdiff:v1.21.0}"
JS_YAML="${JS_YAML:-js-yaml@4.1.0}"

# Kong services whose routes are NOT the public client-facing REST surface (see header).
OPENAPI_EXCLUDE_SERVICES="${OPENAPI_EXCLUDE_SERVICES:-mcp-server engine-sor-ops engine-lifecycle-movers}"

# Skip the git-baseline breaking-change diff (used by --self-test, whose fixture specs are not
# tracked in git, and available as an escape hatch when origin/main is unresolvable).
SKIP_BREAKING="${OPENAPI_SKIP_BREAKING:-0}"

fail=0
note() { printf '%s\n' "$*"; }
err()  { printf '  ERROR: %s\n' "$*" >&2; fail=1; }

command -v jq  >/dev/null 2>&1 || { echo "FATAL: jq is required (brew install jq)"; exit 2; }
command -v npx >/dev/null 2>&1 || { echo "FATAL: npx (Node.js) is required for Spectral + js-yaml"; exit 2; }

# Read a YAML file as JSON via js-yaml's CLI (pinned) — Node is already required for Spectral, so
# no yq/PyYAML dependency on the runner. y2j <file> echoes the parsed doc as JSON on stdout.
export SUPPRESS_NO_CONFIG_WARNING=1
y2j() { npx --yes "$JS_YAML" "$1"; }

# canon <path> — normalise a Kong OR OpenAPI path to a comparable canonical form: strip a leading
# `~` regex marker and a trailing `$`, then collapse EVERY single-segment parameter (a Kong
# `(?<name>[^/]+)` named capture, a bare `[^/]+`, or an OpenAPI `{name}` template) to the literal
# token `{}`. So `~/v1/deposits/[^/]+$` and `/v1/deposits/{id}` both canonicalise to `/v1/deposits/{}`.
canon() {
	printf '%s' "$1" \
		| sed -E \
			-e 's/^~//' \
			-e 's/\$$//' \
			-e 's/\(\?<[^>]+>\[\^\/\]\+\)/{}/g' \
			-e 's/\[\^\/\]\+/{}/g' \
			-e 's/\{[^}]+\}/{}/g'
}

# ---------------------------------------------------------------------------
# --self-test: run the gate against the negative fixtures and assert each FAILS.
# ---------------------------------------------------------------------------
if [ "${1:-}" = "--self-test" ]; then
	note "== OpenAPI catalogue gate SELF-TEST (ADR-IC-020; each fixture MUST fail) =="
	if [ ! -d "$SELFTEST_DIR" ]; then
		note "no $SELFTEST_DIR — no negative fixtures to self-test"; exit 0
	fi
	st_fail=0
	for case_dir in "$SELFTEST_DIR"/*/; do
		[ -d "$case_dir" ] || continue
		case_name="$(basename "$case_dir")"
		# Build a throwaway spec set = the good baseline + this case's overlay (files that OVERWRITE
		# same-named baseline files) minus any basenames listed in the case's `.remove` file.
		tmp="$(mktemp -d)"
		cp -f "$SPECS_DIR"/*.openapi.yaml "$tmp"/ 2>/dev/null || true
		for ov in "$case_dir"*.openapi.yaml; do
			[ -f "$ov" ] && cp -f "$ov" "$tmp"/
		done
		if [ -f "${case_dir}.remove" ]; then
			while IFS= read -r rm_name; do
				[ -n "$rm_name" ] || continue
				rm -f "$tmp/$rm_name"
			done < "${case_dir}.remove"
		fi
		# Run the gate against the throwaway set (skip the git-baseline breaking diff — the tmp
		# specs are untracked). It MUST exit non-zero.
		if OPENAPI_SPECS_DIR="$tmp" OPENAPI_SKIP_BREAKING=1 "$0" >/tmp/openapi-selftest.out 2>&1; then
			note "  FAIL  case '$case_name' PASSED the gate but was expected to FAIL"
			sed 's/^/      | /' /tmp/openapi-selftest.out >&2 || true
			st_fail=1
		else
			note "  ok    case '$case_name' correctly FAILED the gate"
		fi
		rm -rf "$tmp"
	done
	note ""
	if [ "$st_fail" -eq 0 ]; then
		note "OPENAPI CATALOGUE GATE SELF-TEST OK — every negative fixture failed as expected"
		exit 0
	fi
	note "OPENAPI CATALOGUE GATE SELF-TEST FAILED — a fixture did not fail the gate"
	exit 1
fi

if [ ! -d "$SPECS_DIR" ]; then
	note "no $SPECS_DIR directory — no OpenAPI catalogue source to validate"
	exit 0
fi

files=()
while IFS= read -r -d '' f; do files+=("$f"); done \
	< <(find "$SPECS_DIR" -name '*.openapi.yaml' -print0 | sort -z)

if [ "${#files[@]}" -eq 0 ]; then
	note "no *.openapi.yaml under $SPECS_DIR — nothing to validate"
	exit 0
fi

note "== OpenAPI catalogue gate (ADR-IC-020; REST mirror of the AsyncAPI §P1/§P4 gate) =="
note "specs dir: $SPECS_DIR   kong: $KONG_CONFIG   baseline: $BASELINE_REF"
note ""

# ---------------------------------------------------------------------------
# (1) Spectral lint — oas ruleset + the FROZEN governance ruleset. One invocation over the whole
# set; only error-severity findings fail the build.
# ---------------------------------------------------------------------------
note "-- (1) spectral lint (oas + $SPECTRAL_RULESET governance fields; ADR-IC-020 §P1) --"
if [ ! -f "$SPECTRAL_RULESET" ]; then
	err "the frozen Spectral ruleset $SPECTRAL_RULESET is missing (ADR-IC-020)"
else
	if npx --yes "$SPECTRAL_CLI" lint --ruleset "$SPECTRAL_RULESET" --fail-severity=error \
		"${files[@]}" >/tmp/openapi-spectral.out 2>&1; then
		note "  ok    spectral lint clean (no error-severity findings)"
	else
		sed 's/^/    /' /tmp/openapi-spectral.out >&2 || true
		err "spectral lint reported error-severity findings (see above) — a malformed spec or a missing governance x-* field (ADR-IC-020 §P1)"
	fi
fi
note ""

# ---------------------------------------------------------------------------
# (2) oasdiff breaking-change diff vs origin/main. Per file, diff the baseline version against the
# working tree; a BREAKING change fails unless info.x-breaking-change-approved: true (ADR-IC-020 §P4).
# ---------------------------------------------------------------------------
note "-- (2) oasdiff breaking-change diff vs $BASELINE_REF (ADR-IC-020 §P4) --"
if [ "$SKIP_BREAKING" = "1" ]; then
	note "  skipped (OPENAPI_SKIP_BREAKING=1)"
elif ! command -v docker >/dev/null 2>&1; then
	note "  warn  docker not available — skipping the oasdiff breaking-change diff (CI provides Docker)"
elif ! git rev-parse --verify "$BASELINE_REF" >/dev/null 2>&1; then
	note "  baseline ref $BASELINE_REF not resolvable — skipping the diff (run \`git fetch origin main\`)"
else
	for f in "${files[@]}"; do
		if ! git cat-file -e "$BASELINE_REF:$f" 2>/dev/null; then
			note "  new   $f (no baseline — not a breaking change)"
			continue
		fi
		diffdir="$(mktemp -d)"
		git show "$BASELINE_REF:$f" > "$diffdir/base.yaml"
		cp -f "$f" "$diffdir/revision.yaml"
		set +e
		docker run --rm -v "$diffdir":/specs:ro "$OASDIFF_IMAGE" \
			breaking --fail-on ERR /specs/base.yaml /specs/revision.yaml >/tmp/openapi-oasdiff.out 2>&1
		rc=$?
		set -e
		rm -rf "$diffdir"
		if [ "$rc" -eq 0 ]; then
			note "  ok    $f: no breaking changes vs $BASELINE_REF"
		else
			approved="$(y2j "$f" | jq -r '.info["x-breaking-change-approved"] // empty')"
			if [ "$approved" = "true" ]; then
				note "  ok    $f: breaking change(s) — APPROVED (x-breaking-change-approved:true)"
			else
				sed 's/^/    /' /tmp/openapi-oasdiff.out >&2 || true
				err "$f: breaking change(s) vs $BASELINE_REF without x-breaking-change-approved:true (ADR-IC-020 §P4)"
			fi
		fi
	done
fi
note ""

# ---------------------------------------------------------------------------
# Collect the SPEC (method, canonical-path) set + per-op the raw path/method for messages, and check
# the SSE-exempt response-body obligation.
# ---------------------------------------------------------------------------
note "-- structural: response-body present (SSE-exempt) + collect spec operations --"
declare -a spec_ops=()          # "METHOD <canon-path>"
declare -a spec_ops_raw=()      # "METHOD <raw-path> (<file>)" for messages, lockstep with spec_ops
for f in "${files[@]}"; do
	doc="$(y2j "$f")" || { err "$f: could not parse YAML"; continue; }
	while IFS= read -r line; do
		[ -n "$line" ] || continue
		# line = "<METHOD>\t<path>\t<has_sse>\t<has_2xx_json>"
		method="$(printf '%s' "$line" | cut -f1)"
		path="$(printf '%s' "$line" | cut -f2)"
		has_sse="$(printf '%s' "$line" | cut -f3)"
		has_body="$(printf '%s' "$line" | cut -f4)"
		cp="$(canon "$path")"
		spec_ops+=("$method $cp")
		spec_ops_raw+=("$method $path ($f)")
		# NEGATIVE INVARIANT: POST /v1/deposits (the bare engine command, no sub-path) must never be a
		# public spec (ADR-IC-006 §P5 / ADR-IC-020). Checked on the canonical path so a templated form
		# cannot dodge it.
		if [ "$method" = "POST" ] && [ "$cp" = "/v1/deposits" ]; then
			err "$f: declares POST /v1/deposits — the engine COMMAND surface must NOT be a public spec (ADR-IC-006 §P5 / ADR-IC-020 negative invariant)"
		fi
		# SSE exemption: an operation without x-sse-stream:true MUST define a 2xx response with a body
		# schema; an x-sse-stream:true op is WAIVED (streams text/event-stream).
		if [ "$has_sse" = "true" ]; then
			note "  ok    $method $path :: SSE-exempt (x-sse-stream:true) — response-body check waived"
		elif [ "$has_body" = "true" ]; then
			note "  ok    $method $path :: has a 2xx response body"
		else
			err "$f: operation $method $path has no 2xx response body and is not x-sse-stream:true (ADR-IC-020 response-body check)"
		fi
	done < <(printf '%s' "$doc" | jq -r '
		(.paths // {}) | to_entries[] | .key as $p | .value | to_entries[]
		| select(.key | test("^(get|put|post|delete|patch|head|options|trace)$"))
		| (.key | ascii_upcase) as $m
		| ((.value["x-sse-stream"] // false) | tostring) as $sse
		| ([ (.value.responses // {}) | to_entries[]
			| select(.key | test("^2..$"))
			| ((.value.content // {}) | length) ] | (add // 0) > 0 | tostring) as $body
		| "\($m)\t\($p)\t\($sse)\t\($body)"
	')
done
note ""

# ---------------------------------------------------------------------------
# (3) Kong-route <-> spec reconciliation (the no-drift anchor).
# ---------------------------------------------------------------------------
note "-- (3) Kong-route <-> spec reconciliation ($KONG_CONFIG; excluding: $OPENAPI_EXCLUDE_SERVICES) --"
if [ ! -f "$KONG_CONFIG" ]; then
	err "$KONG_CONFIG not found — cannot reconcile specs against the public route surface (ADR-IC-020)"
else
	kong_json="$(y2j "$KONG_CONFIG")" || { err "$KONG_CONFIG: could not parse YAML"; kong_json=""; }
	if [ -n "$kong_json" ]; then
		# Collect the PUBLIC Kong route (method, canonical-path) set — every route of every non-excluded
		# service, expanded over its paths × methods.
		excl_json="$(printf '%s' "$OPENAPI_EXCLUDE_SERVICES" | jq -R 'split(" ")')"
		declare -a kong_ops=()
		declare -a kong_ops_raw=()
		while IFS= read -r line; do
			[ -n "$line" ] || continue
			method="$(printf '%s' "$line" | cut -f1)"
			path="$(printf '%s' "$line" | cut -f2)"
			cp="$(canon "$path")"
			kong_ops+=("$method $cp")
			kong_ops_raw+=("$method $path")
		done < <(printf '%s' "$kong_json" | jq -r --argjson excl "$excl_json" '
			(.services // [])[]
			| select((.name as $n | $excl | index($n)) | not)
			| (.routes // [])[]
			| . as $r
			| (($r.methods // []))[] as $m
			| (($r.paths // []))[] as $p
			| "\($m)\t\($p)"
		')

		# FORWARD — every public Kong route (method, canon-path) has a matching spec operation.
		i=0
		while [ "$i" -lt "${#kong_ops[@]}" ]; do
			ko="${kong_ops[$i]}"; kraw="${kong_ops_raw[$i]}"
			found=0
			for so in ${spec_ops[@]+"${spec_ops[@]}"}; do
				[ "$so" = "$ko" ] && { found=1; break; }
			done
			if [ "$found" = "1" ]; then
				note "  ok    FORWARD  public Kong route [$kraw] has a spec"
			else
				err "public Kong route [$kraw] (canonical: $ko) has NO matching OpenAPI spec operation (ADR-IC-020 FORWARD reconcile — a public route must be catalogued)"
			fi
			i=$((i + 1))
		done

		# REVERSE — every spec operation is an exposed public Kong route.
		i=0
		while [ "$i" -lt "${#spec_ops[@]}" ]; do
			so="${spec_ops[$i]}"; sraw="${spec_ops_raw[$i]}"
			found=0
			for ko in ${kong_ops[@]+"${kong_ops[@]}"}; do
				[ "$ko" = "$so" ] && { found=1; break; }
			done
			if [ "$found" = "1" ]; then
				note "  ok    REVERSE  spec op [$sraw] is a public Kong route"
			else
				err "spec operation [$sraw] (canonical: $so) is NOT an exposed public Kong route (ADR-IC-020 REVERSE reconcile — a spec must document a real public route, never an internal/command surface)"
			fi
			i=$((i + 1))
		done
	fi
fi
note ""

if [ "$fail" -eq 0 ]; then
	note "OPENAPI CATALOGUE GATE OK"
	exit 0
fi
note "OPENAPI CATALOGUE GATE FAILED"
exit 1
