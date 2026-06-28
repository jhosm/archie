#!/usr/bin/env bash
# scripts/grafana-rbac-check.sh — the ADR-IC-016 §7 (plane iii) / ADR-IC-007 §P6
# observability-plane RBAC enforcement gate (M.3 / bd babelstone-njt2.7,
# catalogue row SEC-2, Test ID OBS_PLANE_RBAC). Run by the `infra` path-scoped CI job
# and locally via `make grafana-rbac-check`. Modelled on scripts/kong-config-check.sh.
#
# In plain English: the trace/log store is a searchable database of every financial
# operation, so it must NOT be open to all engineers, and every access to a
# financially-attributed trace must itself be logged (doc 10 Boundary 7 / Principle 4).
# This script proves that end-to-end against a REAL Grafana, not just on paper: it
# stands up the pinned `grafana/otel-lgtm:0.28.0` appliance with the provisioned-as-code
# overlay from infra/grafana/rbac/, then asserts that a NOC-class token is REFUSED a
# Tempo trace query while engineer/admin tokens SUCCEED, and that an authorised trace
# read is RECORDED in Grafana's dataproxy access log with the acting user. A future edit
# that silently re-opens the trace plane (or turns off the access log) fails CI here.
#
# What it proves:
#
#   1. Static assertions on the provisioned-as-code config (infra/grafana/rbac/) — the
#      observability-plane access controls are still EXPRESSED, so a future edit cannot
#      silently drop one (ADR-PC-020 §D3: no silent divergence):
#        - grafana.ini turns RBAC on, dataproxy access logging on, and anonymous OFF
#          (the regulated-store posture, ADR-IC-007 §P4 / doc 10 Principle 4).
#        - roles.yaml carries the §P6 four roles, with noc-viewer scoped to Prometheus
#          (no Tempo) and engineer granted all datasources.
#        - datasource-permissions.yaml locks the Tempo (trace) datasource to engineer +
#          admin, with noc-viewer + compliance-viewer absent from the Tempo grant.
#
#   2. LIVE enforcement on the pinned appliance (the end-to-end leg that flips SEC-2
#      Planned->Live): bring up grafana/otel-lgtm:0.28.0 with the grafana.ini overlay
#      mounted, provision two service-account tokens, and assert:
#        - an unauthenticated (anonymous) Tempo query is REFUSED (401) — the plane is not
#          world-readable once anonymous is disabled;
#        - a NOC-class token WITHOUT the `datasources:query` privilege is REFUSED the
#          Tempo trace query (403 "Permissions needed: datasources:query");
#        - an engineer/admin token WITH the privilege SUCCEEDS (200);
#        - the authorised engineer/admin trace read is RECORDED in the Grafana dataproxy
#          access log (`logger=data-proxy-log`, `datasource=tempo`, acting `username`),
#          while the REFUSED NOC read is NOT (it is denied before the proxy) — exactly
#          "who read a financially-attributed trace, and when".
#
# OSS SCOPE (honest engineering judgement, the same the README + ADR-IC-007 §P6 name):
#   OSS Grafana cannot per-datasource-restrict a Viewer — its basic Viewer role grants
#   `datasources:query` on EVERY datasource, and custom RBAC roles + managed
#   datasource permissions (what roles.yaml / datasource-permissions.yaml express) are
#   an ENTERPRISE feature (the OSS API returns 404 for /api/access-control/roles). So
#   the FAITHFUL "noc-viewer may query Prometheus but not Tempo" split is NOT
#   OSS-enforceable; the per-datasource Tempo lock is the Enterprise / upstream-gateway
#   hardening the ADR names. What IS OSS-enforceable — and what this gate asserts live —
#   is the `datasources:query` ACTION-level gate: a token without that privilege cannot
#   read traces, an authorised read is logged, and the plane refuses anonymous access.
#   That is the OSS-enforceable realisation of doc 10 Boundary 7; the folder-granular
#   role split is documented out-of-OSS-scope in infra/grafana/rbac/README.md.
#
# Static-only (block 1, no Docker; skips the live appliance) with:
#   GRAFANA_RBAC_CHECK_STATIC_ONLY=1 ./scripts/grafana-rbac-check.sh
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO_ROOT"

RBAC_DIR="infra/grafana/rbac"
INI="$RBAC_DIR/grafana.ini"
ROLES="$RBAC_DIR/provisioning/roles.yaml"
DSPERM="$RBAC_DIR/provisioning/datasource-permissions.yaml"
GRAFANA_IMAGE="${GRAFANA_IMAGE:-grafana/otel-lgtm:0.28.0}" # pinned, == infra/compose.yaml grafana-lgtm
STATIC_ONLY="${GRAFANA_RBAC_CHECK_STATIC_ONLY:-0}"
CONTAINER="${GRAFANA_RBAC_CONTAINER:-babelstone-grafana-rbac-check}"

note() { printf '%s\n' "$*"; }
fail() { printf '::error::%s\n' "$*" >&2; exit 1; }

for f in "$INI" "$ROLES" "$DSPERM"; do
  [ -f "$f" ] || fail "$f not found"
done

# ---------------------------------------------------------------------------
# 1. Static assertions on the provisioned-as-code config (ADR-IC-007 §P6 / ADR-IC-016 §7).
# ---------------------------------------------------------------------------
note "== static assertions on $RBAC_DIR (provisioned-as-code) =="
have() { grep -Eq "$2" "$1" || fail "$3"; }

# grafana.ini — RBAC on, dataproxy access logging on, anonymous OFF.
have "$INI" '^\[rbac\]'              "grafana.ini missing the [rbac] section (RBAC must be enabled, ADR-IC-007 §P6)"
have "$INI" '^\[dataproxy\]'         "grafana.ini missing the [dataproxy] section (access logging, doc 10 Boundary 7)"
have "$INI" 'logging *= *true'       "grafana.ini missing [dataproxy] logging = true — trace reads must be logged (doc 10 Boundary 7)"
have "$INI" '^\[auth\.anonymous\]'   "grafana.ini missing the [auth.anonymous] section"
have "$INI" 'enabled *= *false'      "grafana.ini must disable anonymous access — the plane is a regulated data store (ADR-IC-007 §P4)"

# roles.yaml — the §P6 four roles; noc-viewer scoped to Prometheus (no Tempo); engineer all signals.
for role in noc-viewer engineer compliance-viewer admin; do
  have "$ROLES" "name: 'babelstone:$role'" "roles.yaml missing the §P6 '$role' role (ADR-IC-007 §P6 four-role table)"
done
# The noc-viewer role block (up to the next role) must scope datasources:query to Prometheus,
# NOT to all datasources — it has no trace query access (doc 10 Boundary 7).
noc_block="$(awk "/- name: 'babelstone:noc-viewer'/{f=1} /- name: 'babelstone:engineer'/{f=0} f" "$ROLES")"
echo "$noc_block" | grep -q 'datasources:uid:prometheus' \
  || fail "roles.yaml: noc-viewer must scope datasources:query to Prometheus (datasources:uid:prometheus), not traces (ADR-IC-007 §P6)"
echo "$noc_block" | grep -q "scope: 'datasources:\*'" \
  && fail "roles.yaml: noc-viewer must NOT grant datasources:query on ALL datasources (datasources:*) — it has no Tempo/trace access (doc 10 Boundary 7)"
# The engineer role gets all signals (Tempo + Loki + Prometheus).
eng_block="$(awk "/- name: 'babelstone:engineer'/{f=1} /- name: 'babelstone:compliance-viewer'/{f=0} f" "$ROLES")"
echo "$eng_block" | grep -q "scope: 'datasources:\*'" \
  || fail "roles.yaml: engineer must grant datasources:query on all datasources (datasources:*) — full trace/log/metric query (ADR-IC-007 §P6)"

# datasource-permissions.yaml — the Tempo lock: engineer + admin only; NOC + compliance absent.
tempo_block="$(awk "/- name: 'Tempo'/{f=1;next} /- name: '/{f=0} f" "$DSPERM")"
[ -n "$tempo_block" ] || fail "datasource-permissions.yaml missing the Tempo datasource permission block (the load-bearing §P6 lock)"
echo "$tempo_block" | grep -q "role: 'babelstone:engineer'" \
  || fail "datasource-permissions.yaml: Tempo must grant engineer Query (ADR-IC-007 §P6)"
echo "$tempo_block" | grep -q "role: 'babelstone:admin'" \
  || fail "datasource-permissions.yaml: Tempo must grant admin Query (ADR-IC-007 §P6)"
for denied in noc-viewer compliance-viewer; do
  echo "$tempo_block" | grep -q "role: 'babelstone:$denied'" \
    && fail "datasource-permissions.yaml: $denied must NOT have Tempo (trace) access — the financial-restricted tier is engineer+admin only (ADR-IC-007 §P6 / doc 10 Boundary 7)"
done
note "static assertions: OK"

# ---------------------------------------------------------------------------
# 2. Live enforcement on the pinned grafana/otel-lgtm appliance.
# ---------------------------------------------------------------------------
if [ "$STATIC_ONLY" = "1" ]; then
  note "GRAFANA_RBAC_CHECK_STATIC_ONLY=1 — skipping the live $GRAFANA_IMAGE enforcement test"
  note "grafana-rbac-check: static checks passed"
  exit 0
fi
if ! command -v docker >/dev/null 2>&1; then
  fail "docker not found — set GRAFANA_RBAC_CHECK_STATIC_ONLY=1 to skip the live appliance test"
fi

note "== live enforcement ($GRAFANA_IMAGE, overlay from $RBAC_DIR) =="
cleanup() { docker rm -f "$CONTAINER" >/dev/null 2>&1 || true; }
trap cleanup EXIT
cleanup

# Bring up the appliance with our grafana.ini as the config overlay. NB on the appliance:
#   - the otel-lgtm Grafana resolves its override file at conf/custom.ini (homepath == the
#     run dir /otel-lgtm/grafana), so the grafana.ini rides there — that IS provisioning
#     from infra/grafana/rbac/grafana.ini (it sets dataproxy.logging=true + rbac.enabled).
#   - the appliance force-defaults GF_AUTH_ANONYMOUS_ENABLED=true (an ENV var, which wins
#     over the .ini) for zero-config local use; we pass =false to honour the .ini's
#     [auth.anonymous] enabled=false, the regulated-store posture.
#   - ENABLE_LOGS_GRAFANA=true surfaces Grafana's own logs (incl. the dataproxy access log)
#     on stdout, which the appliance otherwise suppresses.
docker run -d --name "$CONTAINER" \
  -e GF_AUTH_ANONYMOUS_ENABLED=false \
  -e ENABLE_LOGS_GRAFANA=true \
  -v "$REPO_ROOT/$INI:/otel-lgtm/grafana/conf/custom.ini:ro" \
  "$GRAFANA_IMAGE" >/dev/null \
  || fail "could not start $GRAFANA_IMAGE (is the image pullable / Docker healthy?)"

# in-container curl (the image ships curl; this avoids any host port-mapping collision).
gcurl() { docker exec "$CONTAINER" curl -s "$@"; }

note "waiting for Grafana to become healthy…"
healthy=0
for _ in $(seq 1 60); do
  if docker exec "$CONTAINER" curl -sf -o /dev/null http://localhost:3000/api/health 2>/dev/null; then healthy=1; break; fi
  sleep 3
done
[ "$healthy" = "1" ] || { docker logs "$CONTAINER" 2>&1 | tail -30 >&2; fail "Grafana did not become healthy in time"; }

# Confirm our grafana.ini overlay actually took effect (no silent fall-through to defaults).
settings="$(gcurl -u admin:admin http://localhost:3000/api/admin/settings)"
echo "$settings" | grep -Eq '"dataproxy"[^}]*"logging" *: *"true"' \
  || fail "the grafana.ini overlay did not apply: dataproxy.logging is not true (access logging off)"

# Provision two service-account tokens via the admin API:
#   - a NOC-class token with role None (no datasources:query) — the OSS-enforceable model
#     of "no trace query access" (a basic Viewer would, on OSS, query every datasource);
#   - an engineer/admin token with role Admin (has datasources:query).
mktoken() { # name role -> prints token
  local sa id
  sa="$(gcurl -u admin:admin -X POST http://localhost:3000/api/serviceaccounts \
    -H 'Content-Type: application/json' -d "{\"name\":\"$1\",\"role\":\"$2\"}")"
  id="$(printf '%s' "$sa" | sed -n 's/.*"id":\([0-9]*\).*/\1/p')"
  [ -n "$id" ] || { printf '%s\n' "$sa" >&2; fail "could not create service account '$1'"; }
  gcurl -u admin:admin -X POST "http://localhost:3000/api/serviceaccounts/$id/tokens" \
    -H 'Content-Type: application/json' -d "{\"name\":\"$1-tok\"}" \
    | sed -n 's/.*"key":"\([^"]*\)".*/\1/p'
}
NOC_NAME="bsrbac-noc"
ENG_NAME="bsrbac-engineer"
ADM_NAME="bsrbac-admin"
NOC_TOKEN="$(mktoken "$NOC_NAME" None)"
ENG_TOKEN="$(mktoken "$ENG_NAME" Admin)"
ADM_TOKEN="$(mktoken "$ADM_NAME" Admin)"
for t in "$NOC_TOKEN" "$ENG_TOKEN" "$ADM_TOKEN"; do
  [ -n "$t" ] || fail "failed to mint a service-account token"
done

# The Tempo (trace) query path through Grafana's datasource proxy.
TEMPO_Q='http://localhost:3000/api/datasources/proxy/uid/tempo/api/search?limit=1'
code() { docker exec "$CONTAINER" curl -s -o /dev/null -w '%{http_code}' "$@"; }

note "asserting the trace-plane access gate…"
anon_code="$(code "$TEMPO_Q")"
[ "$anon_code" = "401" ] \
  || fail "anonymous Tempo query returned $anon_code, expected 401 — the observability plane must refuse anonymous access (ADR-IC-007 §P4 / doc 10 Principle 4)"

noc_code="$(code -H "Authorization: Bearer $NOC_TOKEN" "$TEMPO_Q")"
[ "$noc_code" = "403" ] \
  || fail "NOC-class token Tempo query returned $noc_code, expected 403 — a token without datasources:query must be REFUSED trace reads (ADR-IC-016 §7 / doc 10 Boundary 7)"

eng_code="$(code -H "Authorization: Bearer $ENG_TOKEN" "$TEMPO_Q")"
[ "$eng_code" = "200" ] \
  || fail "engineer token Tempo query returned $eng_code, expected 200 — engineer must have full trace query (ADR-IC-007 §P6)"

adm_code="$(code -H "Authorization: Bearer $ADM_TOKEN" "$TEMPO_Q")"
[ "$adm_code" = "200" ] \
  || fail "admin token Tempo query returned $adm_code, expected 200 — admin must have full trace query (ADR-IC-007 §P6)"
note "trace-plane access gate: anon=401, noc=403, engineer=200, admin=200 — OK"

# The authorised trace read must be RECORDED in the dataproxy access log with its user;
# the REFUSED NOC read must NOT be (it is denied before the proxy). This is the
# "access to financially-attributed traces is itself logged" half of doc 10 Boundary 7.
note "asserting the dataproxy access log records the authorised trace read…"
sleep 2
logs="$(docker logs "$CONTAINER" 2>&1 | grep 'data-proxy-log' || true)"
echo "$logs" | grep -q '"datasource":"tempo"' \
  || { printf '%s\n' "$logs" | tail -10 >&2; fail "no Tempo dataproxy access-log line found — financial-trace access is not being logged (doc 10 Boundary 7 / ADR-IC-007 §P6)"; }
echo "$logs" | grep '"datasource":"tempo"' | grep -q "sa-1-$ENG_NAME" \
  || { printf '%s\n' "$logs" | tail -10 >&2; fail "the authorised engineer trace read is not attributed to its user in the dataproxy access log (the who-queried-what trail)"; }
# The refused NOC read must not have been proxied (defence that the refusal is real, not theatre).
if echo "$logs" | grep '"datasource":"tempo"' | grep -q "sa-1-$NOC_NAME"; then
  fail "the REFUSED NOC token reached the Tempo dataproxy (a logged proxy line) — the trace-query refusal is not enforced before the proxy"
fi
note "dataproxy access log: authorised Tempo read recorded (user-attributed), refused NOC read not proxied — OK"

note "grafana-rbac-check: all checks passed"
