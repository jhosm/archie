#!/usr/bin/env bash
# scripts/asyncapi-catalog-reconcile.sh — the ADR-IC-015 §8 (merge-time) main-lane
# reconciliation (G.4 / bd babelstone-ymav). Run by the `contracts` CI job ON PUSH TO
# MAIN ONLY (never on PRs — the PR lane stays hermetic, ADR-IC-015 Decision §4 + the
# CI-fragility residual risk). ADR-IC-015 supersedes ADR-IC-008 (EventCatalog →
# Backstage); the AsyncAPI governance format and these checks carry forward unchanged.
#
# What it proves: every x-schema-registry-subject the catalog references actually
# EXISTS in a Schema Registry at pipeline time — so the catalog can never document an
# event whose registry subject is missing or was deleted.
#
# How: it registers the working-tree contracts/avro/**/*.avsc set into a throwaway
# Redpanda built-in Schema Registry (the same POC registry ADR-IC-002 chose and that
# scripts/avro-compat-check.sh already uses), then delegates the assertion to
# `asyncapi-catalog-validate.sh --reconcile`, which curls /subjects/<subject> for each
# subject the catalog declares. Reusing the validate script's reconcile path keeps a
# single source of the reconciliation logic — this script only stands up + seeds the registry.
#
# Why a real registry round-trip and not just a static check: this is specifically a
# registry-existence guarantee. Seeding the registry from the .avsc set mirrors what
# the engine's startup register-if-absent does, so the gate agrees with runtime.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO_ROOT"

AVRO_DIR="contracts/avro"
SR_IMAGE="${SR_IMAGE:-docker.redpanda.com/redpandadata/redpanda:v24.3.1}" # pinned, == avro-compat-check.sh

note() { printf '%s\n' "$*"; }

command -v jq     >/dev/null 2>&1 || { echo "FATAL: jq is required";     exit 2; }
command -v curl   >/dev/null 2>&1 || { echo "FATAL: curl is required";   exit 2; }
command -v docker >/dev/null 2>&1 || { echo "FATAL: docker is required for the §P6 registry round-trip"; exit 2; }

if [ ! -d "$AVRO_DIR" ]; then
	note "no $AVRO_DIR — nothing to register; reconciliation is a no-op"
	exit 0
fi

# Collect the working-tree .avsc set (ADR-IC-002 A1 layout).
schemas=()
while IFS= read -r -d '' f; do schemas+=("$f"); done \
	< <(find "$AVRO_DIR" -name '*.avsc' -print0 | sort -z)
[ "${#schemas[@]}" -gt 0 ] || { note "no .avsc under $AVRO_DIR — nothing to register"; exit 0; }

note "== AsyncAPI catalogue registry reconciliation (ADR-IC-015 §8) =="
note "starting throwaway Redpanda SR ($SR_IMAGE) ..."

CID=""
cleanup() { [ -n "$CID" ] && docker rm -f "$CID" >/dev/null 2>&1 || true; }
trap cleanup EXIT

CID="$(docker run -d -P "$SR_IMAGE" \
	redpanda start --mode dev-container --smp 1 --default-log-level=warn \
	--schema-registry-addr 0.0.0.0:8081)"
host_port="$(docker port "$CID" 8081/tcp | head -1 | sed 's/.*://')"
SR_URL="http://127.0.0.1:${host_port}"

ready=0
for _ in $(seq 1 60); do
	if curl -fsS "$SR_URL/subjects" >/dev/null 2>&1; then ready=1; break; fi
	sleep 1
done
[ "$ready" -eq 1 ] || { echo "FATAL: Redpanda SR did not become ready at $SR_URL"; docker logs "$CID" 2>&1 | tail -20; exit 2; }
note "SR ready at $SR_URL"

# Register each .avsc under its derived subject ({namespace}.{name}-value, ADR-IC-002 §P1).
for f in "${schemas[@]}"; do
	ns="$(jq -r '.namespace // empty' "$f")"
	name="$(jq -r '.name // empty' "$f")"
	[ -n "$ns" ] && [ -n "$name" ] || { echo "FATAL: $f missing namespace/name"; exit 2; }
	subject="${ns}.${name}-value"
	body="$(jq -Rs '{schemaType:"AVRO", schema:.}' "$f")"
	http="$(curl -sS -o /tmp/ec-reg-resp -w '%{http_code}' \
		-X POST "$SR_URL/subjects/$subject/versions" \
		-H 'Content-Type: application/vnd.schemaregistry.v1+json' \
		-d "$body")"
	[ "$http" = "200" ] || { echo "FATAL: could not seed $subject (HTTP $http): $(cat /tmp/ec-reg-resp)"; exit 2; }
	note "  registered  $subject"
done
note ""

# Delegate the existence assertion to the validate script's --reconcile path,
# pointed at the seeded registry. (It re-runs the full gate AND the live check; the
# full gate is cheap and keeps a single entry point for the reconciliation logic.)
SCHEMA_REGISTRY_URL="$SR_URL" exec "$REPO_ROOT/scripts/asyncapi-catalog-validate.sh" --reconcile
