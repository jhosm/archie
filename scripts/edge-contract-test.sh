#!/usr/bin/env bash
# scripts/edge-contract-test.sh — the LIVE-KONG edge runtime-contract harness.
# Closes bd babelstone-abig (PSD2 SCA + X-Client-Id gateway contract test, a PRODUCTION
# BLOCKER) and bd babelstone-1z0r (SoR resolver split-brain guard) with ONE shared harness.
#
# In plain English: the static gate (scripts/kong-config-check.sh) proves the edge config
# PARSES. It cannot prove the edge BEHAVES. This harness stands up the REAL gateway
# (kong:3.9.1, DB-less, the actual infra/kong/kong.yml byte-for-byte) in front of two test
# doubles — an echo upstream that reports the headers Kong forwarded, and a stub engine read
# surface that drives each SoR branch — and then fires real HTTP requests through Kong to
# prove the load-bearing RUNTIME properties end to end:
#
#   abig (against live Kong + the echo upstream):
#     (a) no `acr` OR stale `auth_time`  -> 403 SCA_REQUIRED, upstream NEVER called.
#     (b) tampered `sub` (broken sig)    -> 401 from jwt, upstream NEVER called (a pre-jwt
#                                            payload read cannot leak a forged claim through).
#     (c) valid token sub=attacker + client header X-Client-Id: victim -> echo upstream
#                                            reports X-Client-Id=attacker (the IDOR fix:
#                                            attested from the JWT sub, client value overwritten).
#     (d) valid, SCA-compliant token     -> passes through (2xx) to the upstream.
#
#   1z0r (against live Kong + the stub engine read surface):
#     (a) read sor==engine  -> proxies to the engine (reaches the upstream).
#     (b) sor==legacy       -> 503 SOR_UNRESOLVED, no proxy.
#     (c) read 404          -> 503 (unknown instance / projection lag).
#     (d) transport error / non-200 / non-table body -> 503 (fail closed on EVERY error path).
#     (e) require('resty.http') resolves at RUNTIME inside the CE image (the static parse
#         cannot prove the module loads) — proven implicitly: if the module were missing the
#         SoR pre-function would 500, never reaching the deterministic 503/200 outcomes above.
#
# WHY THIS IS SAFE ON KONG CE (the property abig exists to lock): on CE there is no dynamic
# plugin `ordering:` (Enterprise-only). The SCA + X-Client-Id pre-functions run BEFORE jwt by
# STATIC priority (pre-function 1000000 > jwt 1450), reading claims pre-signature-validation.
# That is safe ONLY because the no-anonymous jwt plugin 401s a forged/tampered token BEFORE any
# upstream proxy — so a claim a pre-function read off a bad token never takes effect. jwt is now
# attached PER-ROUTE (de-globalized, ADR-IC-010 §P2 Amendment 2026-06-15), one no-anonymous
# instance on each authenticated route; the static priority (and so this property) is independent
# of where jwt is configured. Assertion (b) is the end-to-end lock on exactly that.
#
# CI-friendly: deterministic health waits (no sleep-and-pray), a trap that tears the stack
# down on ANY exit, fixed host ports overridable via env. Toolchain: pure shell + docker +
# curl + jq (no .NET). Run: make edge-contract-test  (or ./scripts/edge-contract-test.sh).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
HARNESS_DIR="$ROOT/scripts/edge-contract-test"
COMPOSE="docker compose -f $HARNESS_DIR/compose.yaml"

# Fixed but overridable host ports (avoid clashing with a running `make up` stack on 8000/8001).
export KONG_PROXY_PORT="${KONG_PROXY_PORT:-8010}"
export KONG_ADMIN_PORT="${KONG_ADMIN_PORT:-8011}"
EDGE="http://localhost:${KONG_PROXY_PORT}"
ADMIN="http://localhost:${KONG_ADMIN_PORT}"

MINT="$ROOT/scripts/mint-edge-token.sh"
KONG_YML="$ROOT/infra/kong/kong.yml"

# ── pretty output ────────────────────────────────────────────────────────────────
say()  { printf '\n\033[1;36m▶ %s\033[0m\n' "$*"; }
ok()   { printf '  \033[32m✓ %s\033[0m\n' "$*"; }
info() { printf '  \033[2m%s\033[0m\n' "$*"; }
die()  { printf '\n\033[1;31m✗ %s\033[0m\n' "$*" >&2; exit 1; }

PASS=0
FAIL=0
declare -a RESULTS=()

# record <label> <PASS|FAIL> <detail>
record() {
  local label="$1" verdict="$2" detail="${3:-}"
  if [ "$verdict" = "PASS" ]; then
    PASS=$((PASS + 1)); ok "$label — $detail"
  else
    FAIL=$((FAIL + 1)); printf '  \033[1;31m✗ %s — %s\033[0m\n' "$label" "$detail" >&2
  fi
  RESULTS+=("$verdict | $label | $detail")
}

# assert_eq <label> <expected> <actual> [extra]
assert_eq() {
  local label="$1" expected="$2" actual="$3" extra="${4:-}"
  if [ "$expected" = "$actual" ]; then
    record "$label" PASS "got $actual${extra:+ ($extra)}"
  else
    record "$label" FAIL "expected $expected, got $actual${extra:+ ($extra)}"
  fi
}

# ── teardown trap (CI cleanliness) ─────────────────────────────────────────────────
cleanup() {
  local code=$?
  say "Tearing down the harness"
  $COMPOSE down -v --remove-orphans >/dev/null 2>&1 || true
  exit "$code"
}
trap cleanup EXIT INT TERM

# ── preflight ──────────────────────────────────────────────────────────────────────
command -v docker >/dev/null 2>&1 || die "docker is required"
command -v jq >/dev/null 2>&1 || die "jq is required"
command -v curl >/dev/null 2>&1 || die "curl is required"
[ -f "$KONG_YML" ] || die "kong.yml not found at $KONG_YML"
[ -x "$MINT" ] || die "mint-edge-token.sh not found/executable at $MINT"

# ── bring the stack up (deterministic; --wait blocks on healthchecks) ───────────────
say "Bringing up live Kong (kong:3.9.1, DB-less) + echo upstream + stub engine read surface"
info "edge proxy → $EDGE   admin → $ADMIN   config → infra/kong/kong.yml (byte-for-byte)"
$COMPOSE down -v --remove-orphans >/dev/null 2>&1 || true
$COMPOSE up -d --wait --build >/dev/null 2>&1 || {
  info "compose --wait failed; dumping kong logs for diagnosis:"
  $COMPOSE logs kong 2>&1 | tail -n 40 || true
  die "stack did not come up healthy"
}

# Belt-and-braces: poll the Kong admin /status until it reports a parsed config (the --wait
# above already gates on `kong health`, but this confirms the proxy is actually serving).
say "Waiting for the Kong proxy to serve the parsed declarative config"
for i in $(seq 1 30); do
  if curl -fsS "$ADMIN/status" >/dev/null 2>&1; then ok "Kong admin /status is up"; break; fi
  [ "$i" -eq 30 ] && die "Kong admin did not become ready"
  sleep 1
done
# Confirm the declarative routes loaded (the real kong.yml, not an empty config).
ROUTE_COUNT="$(curl -fsS "$ADMIN/routes" | jq '.data | length')"
[ "${ROUTE_COUNT:-0}" -ge 5 ] || die "expected the real kong.yml routes to load (>=5), got ${ROUTE_COUNT:-0}"
ok "Kong loaded $ROUTE_COUNT routes from the real kong.yml"

# A constitution body that PASSES the kong.yml structural body validation (so the only thing
# under test on the constitute route is the SCA/identity gate, not the body check).
VALID_BODY='{"product_code":"TD-TRAD-12M","amount":1000000,"source_account_ref":"acct-ref-001"}'

# curl helper: prints "HTTP_STATUS\n<body>" so we can split status from body deterministically.
# Usage: req <METHOD> <path> [extra curl args...]
req() {
  local method="$1" path="$2"; shift 2
  curl -s -o /tmp/edge_body.$$ -w '%{http_code}' -X "$method" "$EDGE$path" "$@"
}
body() { cat /tmp/edge_body.$$ 2>/dev/null || true; }

# ════════════════════════════════════════════════════════════════════════════════════
# babelstone-abig — SCA + X-Client-Id gateway contract (live Kong + echo upstream)
# ════════════════════════════════════════════════════════════════════════════════════
say "babelstone-abig — PSD2 SCA + X-Client-Id attestation (live Kong + echo upstream)"

# (a.1) NO acr (--no-sca) => 403 SCA_REQUIRED, upstream never called.
TOK_NOSCA="$($MINT --no-sca 2>/dev/null)"
ST="$(req POST /api/v1/deposits/constitute \
  -H "Authorization: Bearer $TOK_NOSCA" -H 'Content-Type: application/json' -d "$VALID_BODY")"
CODE="$(body | jq -r '.code // empty' 2>/dev/null || true)"
STUB="$(body | jq -r '.stub // empty' 2>/dev/null || true)"
if [ "$ST" = "403" ] && [ "$CODE" = "SCA_REQUIRED" ] && [ -z "$STUB" ]; then
  record "abig(a) no-acr token" PASS "403 SCA_REQUIRED, upstream not reached"
else
  record "abig(a) no-acr token" FAIL "status=$ST code='$CODE' stub='$STUB' (want 403/SCA_REQUIRED/no-stub)"
fi

# (a.2) STALE auth_time (--auth-age 600 => 600s > SCA_MAX_AGE 300) => 403 SCA_REQUIRED, no upstream.
TOK_STALE="$($MINT --auth-age 600 2>/dev/null)"
ST="$(req POST /api/v1/deposits/constitute \
  -H "Authorization: Bearer $TOK_STALE" -H 'Content-Type: application/json' -d "$VALID_BODY")"
CODE="$(body | jq -r '.code // empty' 2>/dev/null || true)"
STUB="$(body | jq -r '.stub // empty' 2>/dev/null || true)"
if [ "$ST" = "403" ] && [ "$CODE" = "SCA_REQUIRED" ] && [ -z "$STUB" ]; then
  record "abig(a) stale auth_time" PASS "403 SCA_REQUIRED, upstream not reached"
else
  record "abig(a) stale auth_time" FAIL "status=$ST code='$CODE' stub='$STUB' (want 403/SCA_REQUIRED/no-stub)"
fi

# (b) TAMPERED sub => signature broken => jwt 401 BEFORE upstream. Mint a compliant token,
# then flip a byte in the payload segment WITHOUT re-signing: jwt must reject the bad sig.
TOK_GOOD="$($MINT --sub attacker 2>/dev/null)"
IFS='.' read -r H P S <<<"$TOK_GOOD"
# Forge a new payload claiming sub=victim, re-base64url it, keep the OLD signature (now invalid).
FORGED_P="$(python3 - "$P" <<'PY'
import base64, json, sys
seg = sys.argv[1]
seg_pad = seg + "=" * (-len(seg) % 4)
claims = json.loads(base64.urlsafe_b64decode(seg_pad))
claims["sub"] = "victim"
raw = json.dumps(claims, separators=(",", ":")).encode()
print(base64.urlsafe_b64encode(raw).decode().rstrip("="))
PY
)"
TOK_TAMPERED="$H.$FORGED_P.$S"
ST="$(req POST /api/v1/deposits/constitute \
  -H "Authorization: Bearer $TOK_TAMPERED" -H 'Content-Type: application/json' -d "$VALID_BODY")"
STUB="$(body | jq -r '.stub // empty' 2>/dev/null || true)"
if [ "$ST" = "401" ] && [ -z "$STUB" ]; then
  record "abig(b) tampered sub" PASS "401 from jwt, upstream not reached (pre-jwt read cannot leak)"
else
  record "abig(b) tampered sub" FAIL "status=$ST stub='$STUB' (want 401/no-stub)"
fi

# (c) THE IDOR FIX: valid token sub=attacker + a CLIENT-supplied X-Client-Id: victim header.
# The echo upstream must report it received X-Client-Id=attacker (overwritten from the JWT sub).
TOK_ATTACKER="$($MINT --sub attacker 2>/dev/null)"
ST="$(req POST /api/v1/deposits/constitute \
  -H "Authorization: Bearer $TOK_ATTACKER" -H 'X-Client-Id: victim' \
  -H 'Content-Type: application/json' -d "$VALID_BODY")"
# Echo handler lowercases nothing, but HTTP header keys are case-insensitive — read either case.
FWD="$(body | jq -r '.received_headers["X-Client-Id"] // .received_headers["x-client-id"] // empty' 2>/dev/null || true)"
STUB="$(body | jq -r '.stub // empty' 2>/dev/null || true)"
if [ "$STUB" = "echo-upstream" ] && [ "$FWD" = "attacker" ]; then
  record "abig(c) X-Client-Id IDOR" PASS "upstream received X-Client-Id=attacker (client 'victim' overwritten)"
else
  record "abig(c) X-Client-Id IDOR" FAIL "status=$ST stub='$STUB' forwarded X-Client-Id='$FWD' (want attacker)"
fi

# (d) VALID, SCA-compliant token => passes through (2xx) to the echo upstream.
TOK_OK="$($MINT 2>/dev/null)"
ST="$(req POST /api/v1/deposits/constitute \
  -H "Authorization: Bearer $TOK_OK" -H 'Content-Type: application/json' -d "$VALID_BODY")"
STUB="$(body | jq -r '.stub // empty' 2>/dev/null || true)"
if [[ "$ST" =~ ^2[0-9][0-9]$ ]] && [ "$STUB" = "echo-upstream" ]; then
  record "abig(d) compliant token" PASS "HTTP $ST pass-through to upstream"
else
  record "abig(d) compliant token" FAIL "status=$ST stub='$STUB' (want 2xx + echo-upstream)"
fi

# ════════════════════════════════════════════════════════════════════════════════════
# babelstone-1z0r — SoR resolver split-brain guard (live Kong + stub engine read surface)
# ════════════════════════════════════════════════════════════════════════════════════
say "babelstone-1z0r — SoR resolution fail-closed guard (live Kong + stub engine read surface)"
info "all 1z0r requests carry a fresh SCA-compliant token (the op route is a money-mover: SCA-gated first)"

# The SoR-routed op route is /api/v1/sor/instances/{id}/operations and is a money-mover, so it
# enforces SCA FIRST. Use a compliant token throughout so the only thing under test is SoR.
sor_req() { # <instance_id>
  local instance="$1"
  local tok; tok="$($MINT --sub CLI-SOR-001 2>/dev/null)"
  req POST "/api/v1/sor/instances/$instance/operations" \
    -H "Authorization: Bearer $tok" -H 'Content-Type: application/json' -d '{"op":"terminate_early"}'
}

# (a) sor==engine => proxies to the engine upstream (reaches it). The engine stub answers the
# POST .../operations 200 with stub=engine-read — the "reached the engine" signal.
ST="$(sor_req sor-engine)"
STUB="$(body | jq -r '.stub // empty' 2>/dev/null || true)"
RESULT="$(body | jq -r '.result // empty' 2>/dev/null || true)"
if [[ "$ST" =~ ^2[0-9][0-9]$ ]] && [ "$STUB" = "engine-read" ] && [ "$RESULT" = "engine-op-accepted" ]; then
  record "1z0r(a) sor==engine" PASS "HTTP $ST proxied to engine (resty.http resolved + read sor==engine)"
else
  record "1z0r(a) sor==engine" FAIL "status=$ST stub='$STUB' result='$RESULT' (want 2xx + engine-read proxy)"
fi

# (b) sor==legacy => 503 SOR_UNRESOLVED, no proxy.
ST="$(sor_req sor-legacy)"
CODE="$(body | jq -r '.code // empty' 2>/dev/null || true)"
STUB="$(body | jq -r '.stub // empty' 2>/dev/null || true)"
if [ "$ST" = "503" ] && [ "$CODE" = "SOR_UNRESOLVED" ] && [ -z "$STUB" ]; then
  record "1z0r(b) sor==legacy" PASS "503 SOR_UNRESOLVED, no proxy (split-brain refused)"
else
  record "1z0r(b) sor==legacy" FAIL "status=$ST code='$CODE' stub='$STUB' (want 503/SOR_UNRESOLVED/no-proxy)"
fi

# (c) read 404 (unknown instance / projection lag) => 503.
ST="$(sor_req sor-404-unknown)"
CODE="$(body | jq -r '.code // empty' 2>/dev/null || true)"
STUB="$(body | jq -r '.stub // empty' 2>/dev/null || true)"
if [ "$ST" = "503" ] && [ "$CODE" = "SOR_UNRESOLVED" ] && [ -z "$STUB" ]; then
  record "1z0r(c) read 404" PASS "503 SOR_UNRESOLVED on unknown instance / projection lag"
else
  record "1z0r(c) read 404" FAIL "status=$ST code='$CODE' stub='$STUB' (want 503/SOR_UNRESOLVED)"
fi

# (d) read transport error / non-200 / non-table body => 503 on EVERY path. Three sub-cases:
#   sor-500     : engine read returns 500 (non-200).
#   sor-notable : engine read returns 200 with a JSON ARRAY (non-table) body.
#   sor-missing : engine read returns 200 but NO `sor` field (missing column).
for instance in sor-500 sor-notable sor-missing; do
  ST="$(sor_req "$instance")"
  CODE="$(body | jq -r '.code // empty' 2>/dev/null || true)"
  STUB="$(body | jq -r '.stub // empty' 2>/dev/null || true)"
  if [ "$ST" = "503" ] && [ "$CODE" = "SOR_UNRESOLVED" ] && [ -z "$STUB" ]; then
    record "1z0r(d) $instance" PASS "503 SOR_UNRESOLVED (fail closed on this error path)"
  else
    record "1z0r(d) $instance" FAIL "status=$ST code='$CODE' stub='$STUB' (want 503/SOR_UNRESOLVED)"
  fi
done

# (d.transport) TRUE transport error: the engine read surface is UNREACHABLE. The SoR Lua
# addresses a fixed host (https://engine:8080/...), so we can't redirect it via instance_id —
# instead we STOP the engine stub container, fire one SoR request, and assert the
# `httpc:request_uri` returns nil/err → the `if not res` arm → 503 SOR_UNRESOLVED. This is the
# only branch instance-id alone can't reach, and it's the most important fail-closed path
# (the read surface itself being down must NOT route a money-mover by guess, ADR-PC-018 §5).
say "1z0r(d) transport error — engine read surface UNREACHABLE must still fail closed 503"
ENGINE_CONTAINER="$($COMPOSE ps -q engine-stub)"
docker stop "$ENGINE_CONTAINER" >/dev/null 2>&1 || true
# Give Kong's 2s resty.http timeout room; the call returns an error promptly on a refused conn.
ST="$(sor_req sor-engine)"   # would be sor==engine IF reachable; unreachable => transport error
CODE="$(body | jq -r '.code // empty' 2>/dev/null || true)"
STUB="$(body | jq -r '.stub // empty' 2>/dev/null || true)"
if [ "$ST" = "503" ] && [ "$CODE" = "SOR_UNRESOLVED" ] && [ -z "$STUB" ]; then
  record "1z0r(d) transport error" PASS "503 SOR_UNRESOLVED when the read surface is unreachable (fail closed)"
else
  record "1z0r(d) transport error" FAIL "status=$ST code='$CODE' stub='$STUB' (want 503/SOR_UNRESOLVED)"
fi
# Restart the engine stub and wait for health so any later use is clean (and teardown is tidy).
docker start "$ENGINE_CONTAINER" >/dev/null 2>&1 || true
for i in $(seq 1 30); do
  state="$(docker inspect -f '{{.State.Health.Status}}' "$ENGINE_CONTAINER" 2>/dev/null || echo unknown)"
  [ "$state" = "healthy" ] && break
  sleep 1
done

# (e) require('resty.http') resolves at RUNTIME inside kong:3.9.1. The static `kong config
# parse` loads NO Lua module; only an executed request proves the module resolves. Had
# `require('resty.http')` errored, the SoR pre-function would 500 on EVERY sor request — but
# 1z0r(a) returned a clean 2xx engine proxy (the proxy path EXECUTES the http call) and
# (b)-(d) returned deterministic 503 SOR_UNRESOLVED bodies, which the function emits only
# AFTER a successful require + http call. (Note: this is also exactly why the harness sets
# KONG_UNTRUSTED_LUA_SANDBOX_REQUIRES=cjson.safe,resty.http — without it the default CE
# sandbox BLOCKS the require and every edge request 500s; see the compose comment.) Re-assert
# explicitly on a fresh engine-reachable proxy.
say "1z0r(e) — require('resty.http') resolves at runtime inside kong:3.9.1"
ST="$(sor_req sor-engine)"
STUB="$(body | jq -r '.stub // empty' 2>/dev/null || true)"
if [ "$ST" != "500" ] && [ "$STUB" = "engine-read" ]; then
  record "1z0r(e) resty.http runtime" PASS "SoR pre-function executed its http call (no 500; module loaded)"
else
  record "1z0r(e) resty.http runtime" FAIL "status=$ST stub='$STUB' (a 500 would indicate require('resty.http') failed)"
fi

# ── summary ─────────────────────────────────────────────────────────────────────────
say "Edge runtime-contract results"
for r in "${RESULTS[@]}"; do
  IFS='|' read -r verdict label detail <<<"$r"
  verdict="$(printf '%s' "$verdict" | tr -d ' ')"
  if [ "$verdict" = "PASS" ]; then
    printf '  \033[32mPASS\033[0m %s —%s\n' "$label" "$detail"
  else
    printf '  \033[1;31mFAIL\033[0m %s —%s\n' "$label" "$detail"
  fi
done
printf '\n  Total: %d passed, %d failed\n' "$PASS" "$FAIL"

rm -f /tmp/edge_body.$$ 2>/dev/null || true

if [ "$FAIL" -ne 0 ]; then
  die "edge-contract-test: $FAIL assertion(s) FAILED"
fi
say "edge-contract-test: ALL assertions GREEN (abig + 1z0r)"
