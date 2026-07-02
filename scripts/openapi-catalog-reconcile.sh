#!/usr/bin/env bash
# scripts/openapi-catalog-reconcile.sh — the ADR-IC-020 (main-lane) OpenAPI catalogue
# reconciliation (bd babelstone-ax0b.2). The REST mirror of scripts/asyncapi-catalog-reconcile.sh.
# Run by the `contracts` CI job ON PUSH TO MAIN ONLY (never on PRs — the PR lane stays hermetic,
# the same CI-fragility discipline ADR-IC-015 §4 keeps for AsyncAPI).
#
# In plain English: the hermetic PR gate (openapi-catalog-validate.sh) reconciles the specs against
# the STATIC kong.yml file. This main-lane check adds the LIVE leg: it loads that same kong.yml into
# a throwaway Kong (DB-less mode, the pinned kong:3.9.1 image kong-config-check.sh already uses) and
# confirms Kong actually MATERIALISES the public routes — so a config that parses on disk but fails
# to load its routes into a running gateway can never pass silently. It then delegates the full
# spec<->route reconciliation to the hermetic validate script, keeping a single source of the
# reconcile logic (exactly as the AsyncAPI reconcile delegates to its validate --reconcile path).
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO_ROOT"

KONG_CONFIG="${OPENAPI_KONG_CONFIG:-infra/kong/kong.yml}"
KONG_IMAGE="${KONG_IMAGE:-kong:3.9.1}"   # pinned, == kong-config-check.sh

note() { printf '%s\n' "$*"; }

command -v docker >/dev/null 2>&1 || { echo "FATAL: docker is required for the live-Kong reconciliation"; exit 2; }
command -v curl   >/dev/null 2>&1 || { echo "FATAL: curl is required"; exit 2; }

if [ ! -f "$KONG_CONFIG" ]; then
	note "no $KONG_CONFIG — nothing to load; reconciliation is a no-op"
	exit 0
fi

note "== OpenAPI catalogue live reconciliation (ADR-IC-020 main lane) =="
note "loading $KONG_CONFIG into a throwaway $KONG_IMAGE (DB-less) ..."

CID=""
cleanup() { [ -n "$CID" ] && docker rm -f "$CID" >/dev/null 2>&1 || true; }
trap cleanup EXIT

# DB-less Kong: mount the declarative config read-only and point KONG_DECLARATIVE_CONFIG at it.
# Expose the admin API on a random host port so we can read the materialised routes.
CID="$(docker run -d -P \
	-e KONG_DATABASE=off \
	-e KONG_DECLARATIVE_CONFIG=/kong/kong.yml \
	-e KONG_ADMIN_LISTEN='0.0.0.0:8001' \
	-v "$REPO_ROOT/$KONG_CONFIG":/kong/kong.yml:ro \
	"$KONG_IMAGE")"
admin_port="$(docker port "$CID" 8001/tcp | head -1 | sed 's/.*://')"
ADMIN_URL="http://127.0.0.1:${admin_port}"

ready=0
for _ in $(seq 1 60); do
	if curl -fsS "$ADMIN_URL/routes" >/dev/null 2>&1; then ready=1; break; fi
	sleep 1
done
if [ "$ready" -ne 1 ]; then
	echo "FATAL: throwaway Kong did not become ready at $ADMIN_URL (the declarative config may have failed to load)"
	docker logs "$CID" 2>&1 | tail -30
	exit 2
fi

route_count="$(curl -fsS "$ADMIN_URL/routes" | (command -v jq >/dev/null 2>&1 && jq '.data | length' || grep -c '"id"'))"
if [ "${route_count:-0}" -lt 1 ]; then
	echo "FATAL: Kong loaded but materialised NO routes from $KONG_CONFIG — the public route surface is empty"
	exit 2
fi
note "  ok  Kong materialised $route_count live route(s) from $KONG_CONFIG"
note ""

# Delegate the full spec<->route reconciliation to the hermetic validate script (single source of
# the reconcile logic). It re-runs the whole gate; the gate is cheap and keeps one entry point.
exec "$REPO_ROOT/scripts/openapi-catalog-validate.sh"
