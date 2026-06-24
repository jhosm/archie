#!/usr/bin/env bash
# scripts/asyncapi-catalog-validate.sh — the ADR-IC-015 PR gate (G.4 / bd
# babelstone-ymav). Run by the `contracts` path-scoped CI job (ADR-PC-019 §P1) and
# locally via `make asyncapi-catalog-validate`. ADR-IC-015 supersedes ADR-IC-008
# (EventCatalog → Backstage); the AsyncAPI governance format and every check below
# carry forward unchanged — only the portal tool changed, and the portal was never
# on this gate's path. The §Pn labels below are ADR-IC-008's P1–P6 governance
# principles, restated verbatim in present tense as ADR-IC-015's Decision clauses;
# the log lines keep the familiar §Pn names.
#
# THE FAST, HERMETIC PR LANE (default). For every AsyncAPI file under
# contracts/catalog/events/**.asyncapi.yaml it proves, with NO live Schema
# Registry and NO running portal (ADR-IC-015 + the CI-fragility residual risk —
# the gate must validate the file set, not build a portal):
#
#   §P1  — `asyncapi validate <file>` passes (the file is a well-formed AsyncAPI
#          3.0 document) AND the governance source-of-truth fields are present:
#          info.x-owner, info.x-owner-contact, info.x-status, info.x-gdpr-legal-basis.
#          The payload schema $ref resolves to the on-disk Avro .avsc (hermetic) —
#          so the catalog never restates fields that can drift from contracts/avro/.
#   §P2  — orphan check: every governed .avsc under contracts/avro/ is $ref'd by some
#          catalog file (no integration-event schema without an entry).
#   §P3  — any channel with `x-compacted: true` MUST carry `x-tombstone-contract`.
#          A compacted topic without the tombstone contract fails the gate.
#   §P4  — every modified AsyncAPI file is diffed against its origin/main version
#          with `asyncapi diff --type breaking`; a breaking change fails the build
#          unless the file carries info.x-breaking-change-approved: true.
#   §P5  — a `x-status: deprecated` event must set x-sunset-date >= 180 days after
#          x-deprecated-date.
#   §P6  — every message records its registry subject (x-schema-registry-subject)
#          and the subject reconstructs from the referenced .avsc namespace+name.
#          The PR lane checks the subject is WELL-FORMED; the main lane (--reconcile)
#          checks it EXISTS in the live registry.
#   BS   — the Backstage descriptor contracts/catalog/catalog-info.yaml (if present)
#          is well-formed YAML (ADR-IC-015 Decision §9 — the portal ingest source
#          ships now; the Backstage host is deferred to platform work).
#
# Why the AsyncAPI CLI for the gate: ADR-IC-015 prescribes `@asyncapi/cli` (Apache
# 2.0) for validation precisely so the gate stays hermetic and free. This independence
# is also why the EventCatalog → Backstage supersession did not touch the gate: at the
# G.4 re-check (2026-06-07) EventCatalog's AsyncAPI generator plugin had moved OUT of
# the free tier into an AGPL-3.0/commercial license-keyed plugin (the License-drift
# residual risk ADR-IC-008 anticipated, now realised), so ADR-IC-015 swapped the portal
# to Backstage; the AsyncAPI files stayed the source of truth and this gate stayed on
# the AsyncAPI CLI. The portal changed; the governance gate did not.
#
# THE MAIN LANE (--reconcile). Adds the live check (ADR-IC-015 §8): every
# x-schema-registry-subject must exist in the Schema Registry. Needs a reachable
# registry (SCHEMA_REGISTRY_URL, default the dev stack). NEVER run on the PR lane.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO_ROOT"

CATALOG_DIR="contracts/catalog/events"
RECON_DIR="contracts/catalog/reconciliation"          # per-consumer reconciliation contracts (event-store §7.3)
CATALOG_INFO="contracts/catalog/catalog-info.yaml"   # the Backstage descriptor (ADR-IC-015 §9)
AVRO_DIR="${ASYNCAPI_CATALOG_AVRO_DIR:-contracts/avro}"
BASELINE_REF="${ASYNCAPI_CATALOG_BASELINE_REF:-origin/main}"
SCHEMA_REGISTRY_URL="${SCHEMA_REGISTRY_URL:-http://localhost:18081}"
# Pin the AsyncAPI CLI so the gate is reproducible across runners (no "@latest" drift).
ASYNCAPI_CLI="${ASYNCAPI_CLI:-@asyncapi/cli@6.0.2}"

RECONCILE=0
[ "${1:-}" = "--reconcile" ] && RECONCILE=1

fail=0
note() { printf '%s\n' "$*"; }
err()  { printf '  ERROR: %s\n' "$*" >&2; fail=1; }

command -v jq  >/dev/null 2>&1 || { echo "FATAL: jq is required (brew install jq)"; exit 2; }
command -v npx >/dev/null 2>&1 || { echo "FATAL: npx (Node.js) is required for @asyncapi/cli"; exit 2; }

# Read YAML as JSON via js-yaml's CLI (pinned) — the gate needs only Node, already
# required for @asyncapi/cli, so no yq/PyYAML dependency on the runner. y2j <file>
# echoes the parsed doc as JSON on stdout.
JS_YAML="${JS_YAML:-js-yaml@4.1.0}"
y2j() { npx --yes "$JS_YAML" "$1"; }

if [ ! -d "$CATALOG_DIR" ]; then
	note "no $CATALOG_DIR directory — no AsyncAPI catalogue source to validate"
	exit 0
fi

files=()
while IFS= read -r -d '' f; do files+=("$f"); done \
	< <(find "$CATALOG_DIR" -name '*.asyncapi.yaml' -print0 | sort -z)

if [ "${#files[@]}" -eq 0 ]; then
	note "no *.asyncapi.yaml under $CATALOG_DIR — nothing to validate"
	exit 0
fi

note "== AsyncAPI catalogue gate (ADR-IC-015; §P1–§P6 carried from ADR-IC-008) =="
note "catalog dir: $CATALOG_DIR   baseline: $BASELINE_REF   reconcile: $RECONCILE"
note ""

# ---------------------------------------------------------------------------
# §P1 — AsyncAPI validity. One CLI invocation over the whole set is fastest, but
# we need per-file pass/fail for a clear log, and the CLI exits non-zero if ANY
# file is invalid. Validate the set in one shot (the .avsc $refs resolve on disk).
# ---------------------------------------------------------------------------
note "-- §P1 asyncapi validate (well-formed AsyncAPI 3.0; .avsc \$refs resolve on disk) --"
export SUPPRESS_NO_CONFIG_WARNING=1
# `asyncapi validate` takes one file per invocation — loop. A latest-version 'info'
# diagnostic is not a failure (the ADR §P1 prescribes 3.0.0); a real error exits non-zero.
for f in "${files[@]}"; do
	if npx --yes "$ASYNCAPI_CLI" validate "$f" >/tmp/ec-validate.out 2>&1; then
		note "  valid  $f"
	else
		grep -E '  error  |is not valid|FetchError|Unexpected' /tmp/ec-validate.out >&2 || true
		err "$f: asyncapi validate reported errors (see above)"
	fi
done
note ""

# ---------------------------------------------------------------------------
# Per-file structural assertions (§P1 fields, §P3 tombstone, §P5 deprecation,
# §P6 subject). All hermetic — pure YAML/JSON inspection, no network.
# ---------------------------------------------------------------------------
note "-- §P1/§P3/§P5/§P6 structural assertions --"
declare -a subjects=()
declare -a referenced_avscs=()   # §P2: the .avsc set the catalog actually $refs (orphan check below)
for f in "${files[@]}"; do
	doc="$(y2j "$f")" || { err "$f: could not parse YAML"; continue; }

	# §P1 — required governance fields on info.
	for field in x-owner x-owner-contact x-status x-gdpr-legal-basis; do
		val="$(printf '%s' "$doc" | jq -r --arg k "$field" '.info[$k] // empty')"
		[ -n "$val" ] || err "$f: info.$field is required (ADR-IC-008 §P1)"
	done

	# §P1 — x-status is a closed enum.
	status="$(printf '%s' "$doc" | jq -r '.info["x-status"] // empty')"
	case "$status" in
		active|deprecated|sunset|'') ;;
		*) err "$f: info.x-status '$status' is not one of active|deprecated|sunset (ADR-IC-008 §P1)";;
	esac

	# §P3 — every compacted channel must carry the tombstone contract.
	while IFS= read -r ch; do
		[ -n "$ch" ] || continue
		compacted="$(printf '%s' "$doc" | jq -r --arg c "$ch" '.channels[$c]["x-compacted"]')"
		if [ "$compacted" = "true" ]; then
			tomb="$(printf '%s' "$doc" | jq -r --arg c "$ch" '.channels[$c]["x-tombstone-contract"] // empty')"
			[ -n "$tomb" ] || err "$f: channel '$ch' is x-compacted:true but has no x-tombstone-contract (ADR-IC-008 §P3)"
		elif [ "$compacted" != "false" ]; then
			err "$f: channel '$ch' must declare x-compacted: true|false (ADR-IC-008 §P1/§P3)"
		fi
	done < <(printf '%s' "$doc" | jq -r '.channels | keys[]')

	# §P5 — a deprecated event must give >=180 days notice.
	if [ "$status" = "deprecated" ]; then
		dep="$(printf '%s' "$doc" | jq -r '.info["x-deprecated-date"] // empty')"
		sun="$(printf '%s' "$doc" | jq -r '.info["x-sunset-date"] // empty')"
		if [ -z "$dep" ] || [ -z "$sun" ]; then
			err "$f: x-status:deprecated requires x-deprecated-date and x-sunset-date (ADR-IC-008 §P5)"
		else
			dep_s="$(date -j -f %Y-%m-%d "$dep" +%s 2>/dev/null || date -d "$dep" +%s 2>/dev/null || echo '')"
			sun_s="$(date -j -f %Y-%m-%d "$sun" +%s 2>/dev/null || date -d "$sun" +%s 2>/dev/null || echo '')"
			if [ -n "$dep_s" ] && [ -n "$sun_s" ]; then
				days=$(( (sun_s - dep_s) / 86400 ))
				[ "$days" -ge 180 ] || err "$f: x-sunset-date is only $days days after x-deprecated-date (<180, ADR-IC-008 §P5)"
			fi
		fi
	fi

	# §P6 — every message records a subject that reconstructs from the .avsc it $refs.
	while IFS= read -r msg; do
		[ -n "$msg" ] || continue
		subject="$(printf '%s' "$doc" | jq -r --arg m "$msg" '.components.messages[$m]["x-schema-registry-subject"] // empty')"
		ref="$(printf '%s' "$doc" | jq -r --arg m "$msg" '.components.messages[$m].payload.schema["$ref"] // empty')"
		if [ -z "$subject" ]; then
			err "$f: message '$msg' has no x-schema-registry-subject (ADR-IC-008 §P6)"
			continue
		fi
		if [ -z "$ref" ]; then
			err "$f: message '$msg' payload has no Avro \$ref (ADR-IC-008 §P1/§P6)"
			continue
		fi
		# Resolve the .avsc relative to the AsyncAPI file, read its namespace+name,
		# and require subject == {namespace}.{name}-value (ADR-IC-002 §P1 subject rule).
		avsc="$(cd "$(dirname "$f")" && cd "$(dirname "$ref")" 2>/dev/null && pwd)/$(basename "$ref")"
		if [ ! -f "$avsc" ]; then
			err "$f: message '$msg' payload \$ref '$ref' does not resolve to a file on disk (ADR-IC-008 §P6)"
			continue
		fi
		ns="$(jq -r '.namespace // empty' "$avsc")"
		name="$(jq -r '.name // empty' "$avsc")"
		expected="${ns}.${name}-value"
		if [ "$subject" != "$expected" ]; then
			err "$f: message '$msg' subject '$subject' != '${expected}' derived from its .avsc (ADR-IC-008 §P6 / ADR-IC-002 §P1)"
			continue
		fi
		subjects+=("$subject")
		referenced_avscs+=("$avsc")   # §P2: this catalog file claims this .avsc
		note "  ok  $f :: $msg  ->  $subject"
	done < <(printf '%s' "$doc" | jq -r '.components.messages | keys[]')
done
note ""

# ---------------------------------------------------------------------------
# §P2 step 2 — no integration-event schema without a catalog entry (the orphan
# half of §P2). The §P6 loop above proves catalog -> .avsc resolves; this proves
# the REVERSE: every governed .avsc under contracts/avro/ is $ref'd by some
# catalog file. Without it, a future fifth event with a registered .avsc and no
# catalog file would pass the gate silently — exactly the §P2 failure mode.
#
# Scope == the reconcile script's: it registers the whole contracts/avro/**/*.avsc
# set into the throwaway registry, so every .avsc there is an integration-event
# schema that MUST have a catalog entry. Hermetic — pure on-disk set membership,
# no registry. Override the tree with ASYNCAPI_CATALOG_AVRO_DIR for a future split.
# ---------------------------------------------------------------------------
note "-- §P2 every governed .avsc has a catalog entry (orphan check) --"
if [ -d "$AVRO_DIR" ]; then
	# Normalise the referenced set to absolute paths for comparison (the §P6 loop
	# already resolved each to an absolute path; this is belt-and-braces).
	while IFS= read -r -d '' avsc; do
		abs="$(cd "$(dirname "$avsc")" && pwd)/$(basename "$avsc")"
		found_ref=0
		for r in ${referenced_avscs[@]+"${referenced_avscs[@]}"}; do
			[ "$r" = "$abs" ] && { found_ref=1; break; }
		done
		if [ "$found_ref" = "1" ]; then
			note "  ok  $avsc has a catalog entry"
		else
			err "$avsc has no catalog/events/*.asyncapi.yaml that \$refs it (ADR-IC-008 §P2 step 2)"
		fi
	done < <(find "$AVRO_DIR" -name '*.avsc' -print0 | sort -z)
else
	note "  no $AVRO_DIR — no integration-event schemas to reconcile against the catalog"
fi
note ""

# ---------------------------------------------------------------------------
# §P3 (ADR-IC-017) — the REVERSE orphan check: NO_UNCATALOGUED_EVENT_ON_BUS.
#
# §P2 above proves catalog -> .avsc and .avsc -> catalog. ADR-IC-017 adds the
# RUNTIME-anchored leg: with the catalog-gated relay, an event is on the bus IFF it is
# catalogued, so the build-time mirror must anchor on the engine's actual event set — the
# family DomainEvent records (the things the relay COULD publish) — not on the .avsc set.
# This catches the drift §P2 cannot see: a catalogued .avsc whose record name is NOT a real
# DomainEvent (a schema promoting an event the engine cannot even append — a phantom
# promotion). The complementary "schemaless event is correctly store-only" direction is the
# DESIRED state (ADR-IC-017 §P4) and is reported informationally, never failed.
#
# Hermetic: it scans families/**/Events.cs AND the engine spine's cross-cutting events
# (engine/src/Babelstone.Engine/CrossCuttingEvents.cs) for `record <Name>(...) : ... DomainEvent`
# declarations — the SAME on-disk regex idiom the .NET fitness test
# (CatalogGatedRelayReverseOrphanTests) uses — so the gate needs no .NET build. The spine source
# matters because an engine-declared cross-cutting event can be PROMOTED to the bus (e.g.
# operations.PersonalDataErasureRequested, ADR-PC-004 A4): it is a real, relay-capable DomainEvent
# (folded per family via CrossCuttingEventRegistrations.For) even though it lives in the spine, not a
# family. The .NET test is the authoritative biconditional proof (it has the real catalog + handler
# registry, which already includes the spliced cross-cutting registrations); this is the contracts-job
# mirror that fails the PR at the schema layer.
# ---------------------------------------------------------------------------
note "-- §P3 (ADR-IC-017) reverse orphan: every catalogued .avsc is a real DomainEvent (NO_UNCATALOGUED_EVENT_ON_BUS) --"
FAMILIES_DIR="${ASYNCAPI_CATALOG_FAMILIES_DIR:-families}"
# The engine spine's cross-cutting event declarations (engine-owned, family-agnostic — event-store
# §4.1/§4.3). A promoted cross-cutting event (a catalogued .avsc) resolves to a DomainEvent HERE, not
# under families/. Overridable for a future spine split.
SPINE_CROSSCUTTING="${ASYNCAPI_CATALOG_SPINE_CROSSCUTTING:-engine/src/Babelstone.Engine/CrossCuttingEvents.cs}"
if [ -d "$AVRO_DIR" ] && [ -d "$FAMILIES_DIR" ]; then
	command -v perl >/dev/null 2>&1 || { echo "FATAL: perl is required for the §P3 reverse-orphan scan"; exit 2; }
	# The DomainEvent record names, off disk (skip bin/obj build output). A single multiline-aware pass
	# (perl -0777) over each Events.cs AND the spine cross-cutting source: `record [class] <Name> ... : ...
	# DomainEvent`, with [^{;] stopping the base scan at the first { or ; so it never bleeds past
	# the declaration into the next record. Mirrors the .NET fitness test's regex
	# (CatalogGatedRelayReverseOrphanTests / EmitContractFitnessTests) — perl handles the
	# multi-line positional-ctor records grep -z could not match portably on macOS/Linux.
	domain_event_names="$(
		{
			find "$FAMILIES_DIR" -name 'Events.cs' \
				-not -path '*/bin/*' -not -path '*/obj/*' -print0
			[ -f "$SPINE_CROSSCUTTING" ] && printf '%s\0' "$SPINE_CROSSCUTTING"
		} \
		| xargs -0 perl -0777 -ne \
			'while (/record\s+(?:class\s+)?([A-Z]\w*)\b[^{;]*?:\s*[^{;]*?\bDomainEvent\b/sg) { print "$1\n"; }' \
		| sort -u
	)"

	if [ -z "${domain_event_names//[$'\n']/}" ]; then
		err "no DomainEvent records found under $FAMILIES_DIR/**/Events.cs or $SPINE_CROSSCUTTING — the reverse-orphan scan is vacuous (ADR-IC-017 §P3)"
	fi

	# Every catalogued .avsc record name MUST be a real DomainEvent (family OR engine-declared
	# cross-cutting). A catalogued schema with no CLR event is a phantom promotion — drift the forward
	# .avsc->catalog check cannot see.
	while IFS= read -r -d '' avsc; do
		record_name="$(jq -r '.name // empty' "$avsc")"
		[ -n "$record_name" ] || { err "$avsc has no Avro record .name (ADR-IC-002 §P1)"; continue; }
		if printf '%s\n' "$domain_event_names" | grep -qxF "$record_name"; then
			note "  ok  $record_name is a DomainEvent (catalogued ⇔ relay-capable)"
		else
			err "catalogued schema '$avsc' (record '$record_name') has no family or spine DomainEvent record — a catalog entry must promote a real, relay-capable event (ADR-IC-017 §P3, NO_UNCATALOGUED_EVENT_ON_BUS)"
		fi
	done < <(find "$AVRO_DIR" -name '*.avsc' -print0 | sort -z)

	# Informational: the schemaless DomainEvents that are correctly store-only by construction
	# (the DESIRED ADR-IC-017 §P4 state — e.g. DepositConstitutionFailed, whose coarse fact rides
	# the saga's DepositCancelled). Listed for visibility; NEVER a failure.
	catalogued_record_names="$(find "$AVRO_DIR" -name '*.avsc' -exec jq -r '.name // empty' {} \; | sort -u)"
	while IFS= read -r name; do
		[ -n "$name" ] || continue
		printf '%s\n' "$catalogued_record_names" | grep -qxF "$name" \
			|| note "  info  $name is store-only (uncatalogued, never on the bus — ADR-IC-017 §P4)"
	done <<< "$domain_event_names"
else
	note "  no $AVRO_DIR or $FAMILIES_DIR — reverse-orphan scan skipped"
fi
note ""

# ---------------------------------------------------------------------------
# §P4 — breaking-change diff vs origin/main. Per file, diff the baseline version
# against the working tree. A BREAKING change fails the build unless the file is
# annotated info.x-breaking-change-approved: true.
#
# Division of labour (ADR-IC-008 §P4 names BOTH halves): `asyncapi diff` classifies
# breaking changes at the AsyncAPI-structural level — removing/renaming a channel,
# operation, or message; flipping a status. It does NOT descend into the embedded
# Avro payload to field-level. That half — "any modification that would fail the
# BACKWARD compatibility check in the schema registry (removing a field, changing a
# field type...)" — is the COMPLEMENTARY gate scripts/avro-compat-check.sh (G.3),
# which runs in the same `contracts` job. The two compose: this gate guards the
# catalog's contract shape, that one guards the wire schema. Neither alone is §P4.
# ---------------------------------------------------------------------------
note "-- §P4 breaking-change diff vs $BASELINE_REF --"
if git rev-parse --verify "$BASELINE_REF" >/dev/null 2>&1; then
	tmpdir="$(mktemp -d)"
	trap 'rm -rf "$tmpdir"' EXIT
	for f in "${files[@]}"; do
		# The baseline copy of this file (if it existed on the baseline ref).
		if ! git cat-file -e "$BASELINE_REF:$f" 2>/dev/null; then
			note "  new   $f (no baseline — not a breaking change)"
			continue
		fi
		old="$tmpdir/old.asyncapi.yaml"
		git show "$BASELINE_REF:$f" > "$old"
		# diff resolves $refs in BOTH docs; the baseline must sit next to the working
		# tree so its relative .avsc $refs resolve identically (the .avsc set rarely
		# moves). Name it `.baseline.<n>.yaml-tmp` — NOT *.asyncapi.yaml — so a leaked
		# copy from an interrupted run is never itself picked up by the file glob.
		base_copy="$(dirname "$f")/.baseline.$(basename "${f%.asyncapi.yaml}").yaml-tmp"
		cp -f "$old" "$base_copy"
		set +e
		diff_out="$(npx --yes "$ASYNCAPI_CLI" diff "$base_copy" "$f" --type breaking --format json 2>/tmp/ec-diff.err)"
		rc=$?
		set -e
		rm -f "$base_copy"
		if [ $rc -ne 0 ]; then
			# A non-zero exit that is NOT "breaking changes found" is a tooling error.
			if ! printf '%s' "$diff_out" | jq -e . >/dev/null 2>&1; then
				note "  warn  $f: asyncapi diff could not run cleanly:"; sed 's/^/    /' /tmp/ec-diff.err >&2
				continue
			fi
		fi
		breaking_count="$(printf '%s' "$diff_out" | jq 'if type=="array" then length else (.breaking // [] | length) end' 2>/dev/null || echo 0)"
		if [ "${breaking_count:-0}" -gt 0 ]; then
			approved="$(y2j "$f" | jq -r '.info["x-breaking-change-approved"] // empty')"
			if [ "$approved" = "true" ]; then
				note "  ok    $f: $breaking_count breaking change(s) — APPROVED (x-breaking-change-approved:true)"
			else
				err "$f: $breaking_count breaking change(s) vs $BASELINE_REF without x-breaking-change-approved:true (ADR-IC-008 §P4)"
			fi
		else
			note "  ok    $f: no breaking changes vs $BASELINE_REF"
		fi
	done
else
	note "  baseline ref $BASELINE_REF not resolvable — skipping §P4 diff (run \`git fetch origin main\`)"
fi
note ""

# ---------------------------------------------------------------------------
# §P6 reconciliation (main lane only). Every recorded subject must exist in the
# live Schema Registry. NEVER on the PR lane — keeps that gate hermetic.
# ---------------------------------------------------------------------------
if [ "$RECONCILE" = "1" ]; then
	note "-- §P6 registry reconciliation @ $SCHEMA_REGISTRY_URL --"
	command -v curl >/dev/null 2>&1 || { echo "FATAL: curl is required for --reconcile"; exit 2; }
	# Dedupe subjects (portable — no mapfile, so bash 3.2 on macOS works too).
	while IFS= read -r subj; do
		[ -n "$subj" ] || continue
		code="$(curl -fsS -o /dev/null -w '%{http_code}' \
			"$SCHEMA_REGISTRY_URL/subjects/$subj/versions/latest" 2>/dev/null || echo 000)"
		if [ "$code" = "200" ]; then
			note "  ok    $subj is registered"
		else
			err "$subj is NOT in the registry (HTTP $code) (ADR-IC-008 §P6)"
		fi
	done < <(printf '%s\n' "${subjects[@]+"${subjects[@]}"}" | sort -u)
	note ""
fi

# ---------------------------------------------------------------------------
# §9 (BS) — the Backstage descriptor is well-formed YAML. It ships now; the
# Backstage host is deferred (ADR-IC-015 Decision §9 / bd babelstone-s4ol.1), so the
# gate only proves the descriptor parses — it does not stand up a portal. Hermetic:
# js-yaml's loadAll over the multi-document stream (same Node dep already used above).
# ---------------------------------------------------------------------------
note "-- §9 Backstage descriptor well-formedness (catalog-info.yaml) --"
if [ -f "$CATALOG_INFO" ]; then
	if npx --yes "$JS_YAML" "$CATALOG_INFO" >/dev/null 2>/tmp/ec-bs.err; then
		note "  ok  $CATALOG_INFO is well-formed YAML"
	else
		sed 's/^/    /' /tmp/ec-bs.err >&2 || true
		err "$CATALOG_INFO is not well-formed YAML (ADR-IC-015 §9)"
	fi
else
	note "  no $CATALOG_INFO — Backstage descriptor not present (Git-native fallback posture)"
fi
note ""

# ---------------------------------------------------------------------------
# §7.3 — per-consumer reconciliation contracts (bd babelstone-y1t7). The catalogued
# side of each consumer's reconciliation agreement (which §7.1 patterns it runs); the
# executable side is ReconciliationContract in ProjectionReconciler.cs. Hermetic —
# pure YAML/JSON inspection, no network. Each descriptor must:
#   * parse as YAML;
#   * name its consumer (spec.consumer — a reference, never PII);
#   * declare the three §7.1 pattern flags (checksum/eventCount/fullRebuild) and have at
#     least one true (a contract that reconciles nothing is a misconfiguration — mirrors
#     ReconciliationContract.EnsureValid()'s Patterns != None check);
#   * carry the governance fields metadata.x-owner / x-owner-contact / x-status.
# ---------------------------------------------------------------------------
note "-- §7.3 per-consumer reconciliation contracts ($RECON_DIR) --"
if [ -d "$RECON_DIR" ]; then
	recon_files=()
	while IFS= read -r -d '' f; do recon_files+=("$f"); done \
		< <(find "$RECON_DIR" -name '*.reconciliation.yaml' -print0 | sort -z)
	if [ "${#recon_files[@]}" -eq 0 ]; then
		note "  no *.reconciliation.yaml under $RECON_DIR — none to validate"
	fi
	for f in ${recon_files[@]+"${recon_files[@]}"}; do
		before_fail="$fail"
		rdoc="$(y2j "$f")" || { err "$f: could not parse YAML"; continue; }
		consumer="$(printf '%s' "$rdoc" | jq -r '.spec.consumer // empty')"
		[ -n "$consumer" ] || err "$f: spec.consumer is required (event-store §7.3)"
		for field in x-owner x-owner-contact x-status; do
			val="$(printf '%s' "$rdoc" | jq -r --arg k "$field" '.metadata[$k] // empty')"
			[ -n "$val" ] || err "$f: metadata.$field is required (event-store §7.3 governance)"
		done
		# At least one §7.1 pattern must be true — mirrors ReconciliationContract.EnsureValid().
		any_pattern="$(printf '%s' "$rdoc" | jq -r '
			[(.spec.patterns.checksum // false), (.spec.patterns.eventCount // false),
			 (.spec.patterns.fullRebuild // false)] | any')"
		[ "$any_pattern" = "true" ] \
			|| err "$f: spec.patterns declares no §7.1 pattern true — a contract that reconciles nothing is a misconfiguration (event-store §7.3)"
		# Every declared projection kind is family-prefixed (the ProjectionRunner.Kind shape).
		while IFS= read -r k; do
			[ -n "$k" ] || continue
			case "$k" in
				*.*) ;;
				*) err "$f: projectionKind '$k' is not family-prefixed (e.g. term_deposit.deposit_position)";;
			esac
		done < <(printf '%s' "$rdoc" | jq -r '.spec.projectionKinds[]?.kind // empty')
		[ "$fail" = "$before_fail" ] && note "  ok  $f :: consumer '$consumer'"
	done
else
	note "  no $RECON_DIR — no reconciliation contracts present"
fi
note ""

# ---------------------------------------------------------------------------
# §7.3 (DECISION) — consumer → reconciliation coverage gate: RECORDED, NOT
# enforced (bd babelstone-5r9n.13).
#
# In plain English: every event lists which downstream systems are ALLOWED to
# consume it (its x-authorized-consumers). A coverage gate would additionally
# require that each such consumer, for each family it is authorized on, actually
# declares reconciliation coverage for THAT family's projection kinds — so a
# family cannot quietly grow a consumer with no way to prove it consumed the
# stream correctly (the gap 5r9n.13 found: the loan events listed acl/notification
# as consumers while the reconciliation contracts covered only term_deposit).
#
# DECISION: keep the symmetry an AUTHORED invariant, NOT a mechanical gate, for now.
#   * WHY NOT YET a check: a faithful gate must map an x-authorized-consumer to the
#     SET of projection kinds that consumer is expected to reconcile for that family
#     — but "expected coverage" is a judgement (the notification consumer reconciles
#     event-count against TRIGGER projections, not every family projection; a future
#     read-only consumer may legitimately reconcile NONE). Encoding that mapping here
#     would bake a policy the reconciliation contracts themselves already express more
#     precisely, and risks false RED on a deliberate no-coverage consumer.
#   * WHAT HOLDS THE INVARIANT MEANWHILE: this lane added the personal_loan projection
#     kinds (personal_loan.loan_position, personal_loan.amortization_schedule) to BOTH
#     the acl and notification reconciliation contracts, restoring family symmetry by
#     hand; the contract-reviewer agent (plugins/babelstone-engine/agents) reviews new
#     events for it; and the family-prefixed-kind check above already fails a malformed
#     kind. A consumer that genuinely reconciles nothing records that as a deliberate
#     note in its contract (mirrors notification's checksum:false / fullRebuild:false
#     opt-outs), which a future gate would have to whitelist anyway.
#   * REVISIT TRIGGER (osv6): promote to a real check when a THIRD family or a third
#     reconciling consumer lands — at three the by-hand symmetry stops being reliably
#     auditable and the per-consumer expected-coverage mapping is worth encoding (the
#     x-authorized-consumers ⨯ family → projectionKinds cross-product, with an explicit
#     `x-no-reconciliation-coverage: <reason>` opt-out for deliberate read-only consumers).
# ---------------------------------------------------------------------------
note "-- §7.3 consumer→reconciliation coverage gate: RECORDED as an authored invariant, not enforced (bd babelstone-5r9n.13; revisit at a 3rd family/consumer) --"
note ""

if [ "$fail" -eq 0 ]; then
	note "ASYNCAPI CATALOGUE GATE OK"
	exit 0
fi
note "ASYNCAPI CATALOGUE GATE FAILED"
exit 1
