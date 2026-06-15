#!/usr/bin/env bash
# scripts/mcp-contract-test.sh — the LIVE-KONG MCP-edge runtime-contract harness.
# Closes bd babelstone-5ot0 (end-to-end MCP gateway runtime contract test) and
# babelstone-29ic (upstream mTLS fail-closed lock) with ONE shared harness.
#
# In plain English: the static gate (scripts/kong-config-check.sh) proves the edge config
# PARSES. The abig harness proves the orchestrator routes BEHAVE. Neither exercises the MCP
# agent channel through the REAL gateway. This harness stands up the REAL Kong (kong:3.9.1,
# DB-less, the actual infra/kong/kong.yml byte-for-byte) in front of the REAL Python MCP server
# (built from mcp-server/) and a stub engine, then fires real HTTP through Kong to prove the
# load-bearing MCP runtime properties end to end:
#
#   A1 wrong-aud      : a signed token with aud != MCP_SERVER_URI -> 401 AUDIENCE_MISMATCH WITH a
#                       WWW-Authenticate header carrying resource_metadata; upstream NOT reached.
#   A2 valid-aud+no-sub: aud == MCP_SERVER_URI but NO sub -> 401 AUDIENCE_MISMATCH via deny_id()
#                       ("does not carry a usable subject") and NO WWW-Authenticate; upstream NOT
#                       reached. (The bd issue text loosely says 403; the ACTUAL CE behaviour is a
#                       401 via deny_id — see the assertion note below.)
#   A3 happy path     : a valid token (aud+sub) -> a REAL MCP Streamable-HTTP `initialize`
#                       handshake SUCCEEDS through Kong (200; result.protocolVersion + serverInfo).
#                       THIS is the bug fix: Kong dials https://mcp-server:8080 — a plain-HTTP
#                       uvicorn 502s the TLS handshake; the env-driven uvicorn TLS makes it pass.
#   A4 X-Client-Id IDOR: valid token sub=attacker scope=deposits:read aud=MCP_SERVER_URI + a CLIENT
#                       header X-Client-Id: victim, calling tools/call get_deposit -> the stub
#                       engine reports it received X-Client-Id=attacker (Kong overwrote the client
#                       value, the MCP server trusted the attested header, the tool forwarded it).
#   A5 well-known/transport: GET /.well-known/oauth-protected-resource WITHOUT a token -> 200
#                       (REAL server, jwt is now de-globalized: it is attached per-route on the
#                       authenticated routes only, NONE on well-known); POST /mcp WITHOUT a token
#                       -> 401 (the per-route jwt on the mcp-streamable-http route).
#                       This is the bd babelstone-ziu3.2 fix and a HARD assertion: it FAILED before
#                       de-globalization (on kong:3.9.1 the well-known route's `jwt enabled:false`
#                       did NOT suppress a same-named GLOBAL jwt, so the public RFC 9728 metadata
#                       401'd — the defect recorded as Q-BD in 04-open-questions.md). The CE-correct
#                       fix removes the global jwt and attaches it explicitly to each authenticated
#                       route (ADR-IC-010 §P2 Amendment 2026-06-15), with NO anonymous consumer.
#   A6 pre-jwt-read-cannot-leak: a token tampered AFTER signing (broken sig) -> 401 from jwt BEFORE
#                       any upstream proxy; upstream NOT reached (mirrors abig(b)).
#   A7 mTLS fail-closed: a client connecting DIRECTLY to uvicorn (bypassing Kong) WITHOUT a client
#                       cert is REJECTED at the TLS layer (ssl_cert_reqs=CERT_REQUIRED); the
#                       WITH-client-cert path (Kong) completes (the 29ic lock).
#
# CI-friendly: deterministic health waits, a trap that tears the stack down on ANY exit, fixed
# host ports overridable via env. Toolchain: pure shell + docker + curl + jq + python3 (no .NET).
# Run: make mcp-contract-test  (or ./scripts/mcp-contract-test.sh).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
HARNESS_DIR="$ROOT/scripts/mcp-contract-test"
COMPOSE="docker compose -f $HARNESS_DIR/compose.yaml"

# Fixed but overridable host ports (distinct from the abig harness's 8010/8011 so both can run).
export MCP_KONG_PROXY_PORT="${MCP_KONG_PROXY_PORT:-8012}"
export MCP_KONG_ADMIN_PORT="${MCP_KONG_ADMIN_PORT:-8013}"
export MCP_ENGINE_STUB_PORT="${MCP_ENGINE_STUB_PORT:-8014}"
export MCP_SERVER_DIRECT_PORT="${MCP_SERVER_DIRECT_PORT:-8015}"
EDGE="http://localhost:${MCP_KONG_PROXY_PORT}"
ADMIN="http://localhost:${MCP_KONG_ADMIN_PORT}"
ENGINE_ECHO="http://localhost:${MCP_ENGINE_STUB_PORT}"

MINT="$ROOT/scripts/mint-edge-token.sh"
KONG_YML="$ROOT/infra/kong/kong.yml"
POC_CA="$ROOT/infra/kong/mcp-poc-ca.crt"

# The MCP server's canonical URI — MUST equal kong.yml's MCP_SERVER_URI literal and the
# mcp-server container's BABELSTONE_MCP_SERVER_URI. Tokens carry it as `aud`.
MCP_AUD="http://localhost:8000/mcp"

# ── pretty output ────────────────────────────────────────────────────────────────
say()  { printf '\n\033[1;36m▶ %s\033[0m\n' "$*"; }
ok()   { printf '  \033[32m✓ %s\033[0m\n' "$*"; }
info() { printf '  \033[2m%s\033[0m\n' "$*"; }
die()  { printf '\n\033[1;31m✗ %s\033[0m\n' "$*" >&2; exit 1; }

PASS=0
FAIL=0
declare -a RESULTS=()

record() {
  local label="$1" verdict="$2" detail="${3:-}"
  if [ "$verdict" = "PASS" ]; then
    PASS=$((PASS + 1)); ok "$label — $detail"
  else
    FAIL=$((FAIL + 1)); printf '  \033[1;31m✗ %s — %s\033[0m\n' "$label" "$detail" >&2
  fi
  RESULTS+=("$verdict | $label | $detail")
}

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
  rm -f /tmp/mcp_body.$$ /tmp/mcp_hdr.$$ 2>/dev/null || true
  exit "$code"
}
trap cleanup EXIT INT TERM

# ── preflight ──────────────────────────────────────────────────────────────────────
command -v docker >/dev/null 2>&1 || die "docker is required"
command -v jq >/dev/null 2>&1 || die "jq is required"
command -v curl >/dev/null 2>&1 || die "curl is required"
command -v python3 >/dev/null 2>&1 || die "python3 is required"
[ -f "$KONG_YML" ] || die "kong.yml not found at $KONG_YML"
[ -f "$POC_CA" ] || die "POC CA cert not found at $POC_CA"
[ -x "$MINT" ] || die "mint-edge-token.sh not found/executable at $MINT"

# ── bring the stack up (deterministic; --wait blocks on healthchecks) ───────────────
say "Bringing up live Kong (kong:3.9.1, DB-less) + the REAL MCP server (mTLS) + stub engine"
info "edge proxy → $EDGE   admin → $ADMIN   config → infra/kong/kong.yml (byte-for-byte)"
$COMPOSE down -v --remove-orphans >/dev/null 2>&1 || true
$COMPOSE up -d --wait --build >/dev/null 2>&1 || {
  info "compose --wait failed; dumping logs for diagnosis:"
  $COMPOSE logs mcp-server 2>&1 | tail -n 30 || true
  $COMPOSE logs kong 2>&1 | tail -n 30 || true
  die "stack did not come up healthy"
}

say "Waiting for the Kong proxy to serve the parsed declarative config"
for i in $(seq 1 30); do
  if curl -fsS "$ADMIN/status" >/dev/null 2>&1; then ok "Kong admin /status is up"; break; fi
  [ "$i" -eq 30 ] && die "Kong admin did not become ready"
  sleep 1
done
ROUTE_COUNT="$(curl -fsS "$ADMIN/routes" | jq '.data | length')"
[ "${ROUTE_COUNT:-0}" -ge 5 ] || die "expected the real kong.yml routes to load (>=5), got ${ROUTE_COUNT:-0}"
ok "Kong loaded $ROUTE_COUNT routes from the real kong.yml"

# ── helpers ──────────────────────────────────────────────────────────────────────
# mcp_post <token> <json-rpc body> [extra curl -H args...] — POST /mcp through Kong, capturing
# HTTP status (stdout), the body (/tmp/mcp_body.$$), and the response headers (/tmp/mcp_hdr.$$).
mcp_post() {
  local token="$1" data="$2"; shift 2
  curl -s -o /tmp/mcp_body.$$ -D /tmp/mcp_hdr.$$ -w '%{http_code}' \
    -X POST "$EDGE/mcp" \
    -H "Authorization: Bearer $token" \
    -H "Content-Type: application/json" \
    -H "Accept: application/json, text/event-stream" \
    "$@" \
    -d "$data"
}
mbody() { cat /tmp/mcp_body.$$ 2>/dev/null || true; }

# mcp_post_ok <token> <json-rpc body> [extra curl -H args...] — like mcp_post, but TOLERATES the
# per-consumer rate limit on the happy-path assertions. The mcp-streamable-http route caps the
# agent channel at 5 req/min limit_by:consumer (kong.yml:812-816), and EVERY POC token maps to the
# SAME `iam-issuer` consumer (the shared `iss` key), so the harness's own valid-token requests
# (A3 init, A4 init+notif+tools/call, A7-via-Kong) share ONE 5/min budget — clean runs sit AT it,
# one request from a 429 (an added probe, a retry, or a minute-window boundary trips it). Rather
# than weaken the byte-for-byte real kong.yml, this helper retries ONCE on a 429, sleeping the
# `Retry-After`/`RateLimit-Reset` seconds Kong returns (fixed-window local policy → ≤60s) so the
# contract test cannot flake on its OWN green path. Negative-path assertions keep using mcp_post
# (they are rejected by jwt/pre-function BEFORE rate-limiting counts them, so they never 429).
mcp_post_ok() {
  local st
  st="$(mcp_post "$@")"
  if [ "$st" = "429" ]; then
    local reset
    reset="$(grep -i '^retry-after:' /tmp/mcp_hdr.$$ | tr -d '\r' | awk '{print $2}' || true)"
    [ -z "$reset" ] && reset="$(grep -i '^ratelimit-reset:' /tmp/mcp_hdr.$$ | tr -d '\r' | awk '{print $2}' || true)"
    # Default to a full fixed-window if Kong did not surface a reset hint; +1s for clock skew.
    case "$reset" in (''|*[!0-9]*) reset=60 ;; esac
    info "rate-limited (429) on a happy-path request; waiting ${reset}s for the per-consumer window to reset, then retrying once"
    sleep $((reset + 1))
    st="$(mcp_post "$@")"
  fi
  printf '%s' "$st"
}

# jsonrpc_field <jq-path> — read a field off the /mcp response whether it is a plain JSON body or
# an SSE `data:` frame (FastMCP Streamable HTTP may return either).
jsonrpc_field() {
  local path="$1" v
  v="$(mbody | jq -r "$path // empty" 2>/dev/null || true)"
  if [ -z "$v" ]; then
    v="$(mbody | grep '^data:' | head -1 | sed 's/^data: //' | jq -r "$path // empty" 2>/dev/null || true)"
  fi
  printf '%s' "$v"
}

INIT_BODY='{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"mcp-contract-harness","version":"0.1"}}}'

# ════════════════════════════════════════════════════════════════════════════════════
# A1 — wrong audience: 401 AUDIENCE_MISMATCH WITH WWW-Authenticate(resource_metadata), no upstream.
# ════════════════════════════════════════════════════════════════════════════════════
say "A1 — a signed token with the WRONG aud is 401 AUDIENCE_MISMATCH (token-replay defence, ADR-IC-010 §P3)"
TOK_WRONGAUD="$($MINT --aud https://some-other-resource.example/ --scope 'deposits:read' --sub attacker 2>/dev/null)"
ST="$(mcp_post "$TOK_WRONGAUD" "$INIT_BODY")"
CODE="$(mbody | jq -r '.code // empty' 2>/dev/null || true)"
WWW="$(grep -i '^www-authenticate:' /tmp/mcp_hdr.$$ | tr -d '\r' || true)"
if [ "$ST" = "401" ] && [ "$CODE" = "AUDIENCE_MISMATCH" ] && echo "$WWW" | grep -q 'resource_metadata='; then
  record "A1 wrong-aud" PASS "401 AUDIENCE_MISMATCH + WWW-Authenticate(resource_metadata), upstream not reached"
else
  record "A1 wrong-aud" FAIL "status=$ST code='$CODE' www='$WWW' (want 401/AUDIENCE_MISMATCH/resource_metadata)"
fi

# ════════════════════════════════════════════════════════════════════════════════════
# A2 — valid aud, NO sub: 401 AUDIENCE_MISMATCH via deny_id ("usable subject"), NO WWW-Authenticate.
# NOTE: bd babelstone-5ot0's text loosely says 403; the ACTUAL Kong CE behaviour is a 401 via the
# deny_id() branch (a 401 with the AUDIENCE_MISMATCH code but a DIFFERENT "usable subject" message
# and NO WWW-Authenticate header). We assert the ACTUAL behaviour and flag the discrepancy.
# ════════════════════════════════════════════════════════════════════════════════════
say "A2 — a valid-aud token with NO sub is 401 via deny_id (no usable subject to attest as X-Client-Id)"
TOK_NOSUB="$($MINT --no-sub --aud "$MCP_AUD" --scope 'deposits:read' 2>/dev/null)"
ST="$(mcp_post "$TOK_NOSUB" "$INIT_BODY")"
CODE="$(mbody | jq -r '.code // empty' 2>/dev/null || true)"
MSG="$(mbody | jq -r '.message // empty' 2>/dev/null || true)"
WWW="$(grep -i '^www-authenticate:' /tmp/mcp_hdr.$$ | tr -d '\r' || true)"
if [ "$ST" = "401" ] && [ "$CODE" = "AUDIENCE_MISMATCH" ] \
   && echo "$MSG" | grep -q 'usable subject' && [ -z "$WWW" ]; then
  record "A2 valid-aud no-sub" PASS "401 deny_id ('usable subject'), no WWW-Authenticate, upstream not reached"
else
  record "A2 valid-aud no-sub" FAIL "status=$ST code='$CODE' msg='$MSG' www='$WWW' (want 401/usable-subject/no-www)"
fi

# ════════════════════════════════════════════════════════════════════════════════════
# A3 — happy path: a REAL MCP `initialize` handshake completes through Kong (THE bug fix).
# ════════════════════════════════════════════════════════════════════════════════════
say "A3 — a valid token completes a REAL MCP Streamable-HTTP initialize through Kong (the 502→200 fix)"
TOK_OK="$($MINT --aud "$MCP_AUD" --scope 'deposits:read deposits:write' --sub CLI-MCP-001 2>/dev/null)"
ST="$(mcp_post_ok "$TOK_OK" "$INIT_BODY")"
PROTO="$(jsonrpc_field '.result.protocolVersion')"
SRVNAME="$(jsonrpc_field '.result.serverInfo.name')"
SESSION_ID="$(grep -i '^mcp-session-id:' /tmp/mcp_hdr.$$ | tr -d '\r' | awk '{print $2}' || true)"
assert_eq "A3 initialize HTTP status"     "200"                 "$ST"
if [ -n "$PROTO" ]; then
  record "A3 initialize protocolVersion" PASS "result.protocolVersion=$PROTO"
else
  record "A3 initialize protocolVersion" FAIL "no result.protocolVersion in the JSON-RPC response (status=$ST)"
fi
assert_eq "A3 initialize serverInfo.name" "babelstone-deposits" "$SRVNAME"
info "Mcp-Session-Id: ${SESSION_ID:-<none>}"

# ════════════════════════════════════════════════════════════════════════════════════
# A4 — X-Client-Id IDOR overwrite, end-to-end: sub=attacker + a client header X-Client-Id: victim
# -> the stub engine's recorded headers show X-Client-Id=attacker on the get_deposit engine call.
# This also proves the scope passthrough (get_deposit needs deposits:read).
# ════════════════════════════════════════════════════════════════════════════════════
say "A4 — tools/call get_deposit: a client-supplied X-Client-Id:victim is OVERWRITTEN to the sub (attacker) all the way to the engine"
TOK_ATTACKER="$($MINT --aud "$MCP_AUD" --scope 'deposits:read' --sub attacker 2>/dev/null)"
# A fresh MCP session: initialize, capture Mcp-Session-Id, send the initialized notification, then tools/call.
ST="$(mcp_post_ok "$TOK_ATTACKER" "$INIT_BODY" -H 'X-Client-Id: victim')"
A4_SESSION="$(grep -i '^mcp-session-id:' /tmp/mcp_hdr.$$ | tr -d '\r' | awk '{print $2}' || true)"
if [ -z "$A4_SESSION" ]; then
  record "A4 X-Client-Id IDOR" FAIL "no Mcp-Session-Id from initialize (status=$ST) — cannot drive tools/call"
else
  # The MCP spec requires the `notifications/initialized` notification before normal requests.
  NOTIF_BODY='{"jsonrpc":"2.0","method":"notifications/initialized"}'
  mcp_post_ok "$TOK_ATTACKER" "$NOTIF_BODY" -H 'X-Client-Id: victim' -H "Mcp-Session-Id: $A4_SESSION" >/dev/null 2>&1 || true
  CALL_BODY='{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"get_deposit","arguments":{"deposit_id":"d-echo-test"}}}'
  ST="$(mcp_post_ok "$TOK_ATTACKER" "$CALL_BODY" -H 'X-Client-Id: victim' -H "Mcp-Session-Id: $A4_SESSION")"
  # Read what the engine stub recorded on its GET /v1/deposits/{id} (via the host-mapped echo port).
  FWD="$(curl -fsS "$ENGINE_ECHO/echo/headers" 2>/dev/null \
        | jq -r '.received_headers["X-Client-Id"] // .received_headers["x-client-id"] // empty' 2>/dev/null || true)"
  if [ "$FWD" = "attacker" ]; then
    record "A4 X-Client-Id IDOR" PASS "engine received X-Client-Id=attacker (client 'victim' overwritten; scope passthrough OK; tools/call HTTP $ST)"
  else
    record "A4 X-Client-Id IDOR" FAIL "engine received X-Client-Id='$FWD' (want attacker; tools/call HTTP $ST)"
  fi
fi

# ════════════════════════════════════════════════════════════════════════════════════
# A5 — public well-known (no token -> 200) vs guarded transport (no token -> 401 per-route jwt).
# ════════════════════════════════════════════════════════════════════════════════════
say "A5 — well-known is public (200 no token); POST /mcp is jwt-gated (401 no token)"
# THE bd babelstone-ziu3.2 FIX, asserted HARD (this assertion FAILED before de-globalization).
# Before: the mcp-well-known route disabled jwt via `plugins: [{name: jwt, enabled: false}]` while
# jwt was ALSO a GLOBAL plugin (top-level `plugins:`). On kong:3.9.1 a route-level `enabled: false`
# does NOT suppress a same-named GLOBAL plugin — Kong falls back to the global jwt, so the public
# RFC 9728 metadata 401'd without a token (the defect recorded as Q-BD in 04-open-questions.md,
# committed in 3cfaf01 / bd babelstone-e50n).
# Now: the jwt plugin is DE-GLOBALIZED (removed from the top-level `plugins:` block and attached
# EXPLICITLY to each of the six authenticated routes — deposits-constitute, processes-stream,
# deposits-maturities, deposits-read, sor-engine-ops, mcp-streamable-http), with NO `anonymous:`
# consumer anywhere. The mcp-well-known route carries no jwt at all, so the public discovery
# document is reachable WITHOUT a token (200), while POST /mcp stays gated by the per-route jwt on
# mcp-streamable-http (401 with no token). This is ADR-IC-010 §P2 Amendment 2026-06-15 (A5/A6) and
# the ordering-safety property (pre-function 1000000 > jwt 1450, no anonymous fallback) is unchanged
# — the proxy gate is now the per-route jwt rather than the global jwt. A6 below re-proves it.
WK_ST="$(curl -s -o /dev/null -w '%{http_code}' "$EDGE/.well-known/oauth-protected-resource")"
assert_eq "A5 well-known no-token (public RFC 9728 discovery, ADR-IC-010 §P2)" "200" "$WK_ST"
NOTOK_ST="$(curl -s -o /dev/null -w '%{http_code}' -X POST "$EDGE/mcp" \
  -H 'Content-Type: application/json' -H 'Accept: application/json, text/event-stream' -d "$INIT_BODY")"
assert_eq "A5 POST /mcp no-token" "401" "$NOTOK_ST"

# ════════════════════════════════════════════════════════════════════════════════════
# A6 — pre-jwt-read cannot leak: a token tampered AFTER signing -> 401 from jwt, no upstream.
# (Mirrors abig(b): the /mcp pre-functions read claims pre-signature, but the PER-ROUTE no-anonymous
# jwt plugin on mcp-streamable-http 401s a bad signature before any upstream proxy — so a pre-jwt
# read never takes effect. After de-globalization the gate is the per-route jwt, not a global one;
# the ordering-safety property is unchanged because pre-function priority 1000000 > jwt 1450 is
# static regardless of where the jwt plugin is attached.)
# ════════════════════════════════════════════════════════════════════════════════════
say "A6 — a token tampered after signing (broken sig) is 401 from jwt BEFORE any upstream (CE ordering safety)"
TOK_GOOD="$($MINT --aud "$MCP_AUD" --scope 'deposits:read' --sub attacker 2>/dev/null)"
IFS='.' read -r H P S <<<"$TOK_GOOD"
FORGED_P="$(python3 - "$P" <<'PY'
import base64, json, sys
seg = sys.argv[1]
seg_pad = seg + "=" * (-len(seg) % 4)
claims = json.loads(base64.urlsafe_b64decode(seg_pad))
claims["sub"] = "victim"        # forge a different identity, keep the OLD (now invalid) signature
raw = json.dumps(claims, separators=(",", ":")).encode()
print(base64.urlsafe_b64encode(raw).decode().rstrip("="))
PY
)"
TOK_TAMPERED="$H.$FORGED_P.$S"
ST="$(mcp_post "$TOK_TAMPERED" "$INIT_BODY")"
# A leaked upstream would be a 200 initialize result; a jwt 401 carries no protocolVersion.
LEAK_PROTO="$(jsonrpc_field '.result.protocolVersion')"
if [ "$ST" = "401" ] && [ -z "$LEAK_PROTO" ]; then
  record "A6 tampered-sig" PASS "401 from jwt, upstream not reached (pre-jwt read cannot leak)"
else
  record "A6 tampered-sig" FAIL "status=$ST leaked-protocolVersion='$LEAK_PROTO' (want 401/no-upstream)"
fi

# ════════════════════════════════════════════════════════════════════════════════════
# A7 — mTLS fail-closed (the 29ic lock): a DIRECT connection to uvicorn with NO client cert is
# rejected at the TLS layer; the WITH-client-cert path (via Kong) completes.
# ════════════════════════════════════════════════════════════════════════════════════
say "A7 — direct to uvicorn with NO client cert is rejected at the TLS handshake (ssl_cert_reqs=CERT_REQUIRED)"
# (a) No client cert: present the POC CA so the SERVER cert verifies, but offer NO client cert.
#     A CERT_REQUIRED server aborts the handshake -> python ssl raises -> we print TLS_REJECTED.
DIRECT="https://localhost:${MCP_SERVER_DIRECT_PORT}/.well-known/oauth-protected-resource"
NOCLIENT_RESULT="$(python3 - "$DIRECT" "$POC_CA" <<'PY'
import http.client, socket, ssl, sys, urllib.request
url, cafile = sys.argv[1], sys.argv[2]
ctx = ssl.create_default_context(cafile=cafile)
ctx.check_hostname = False  # cert CN is mcp-server, we dial localhost
# Deliberately present NO client cert (no load_cert_chain): a CERT_REQUIRED server must reject us.
# We classify by FAILURE MODE so the assertion cannot PASS for the wrong reason. A CERT_REQUIRED
# server that has the client cert it requires missing aborts the connection AFTER we reach it — it
# surfaces as a clean ssl.SSLError, or (commonly) as a ConnectionResetError / RemoteDisconnected
# (the server closes the socket without a response). Those mean TLS_REJECTED. By contrast a port
# that is not bound / not routable (server never came up) raises ConnectionRefusedError / timeout /
# gaierror — that is NOT a TLS rejection, so we report it distinctly and the assertion FAILS loudly
# instead of green-passing for the wrong reason. (RemoteDisconnected/ConnectionReset/Refused all
# carry errno=None at the HTTP layer, so we discriminate on TYPE, not errno.)
try:
    urllib.request.urlopen(url, context=ctx, timeout=5)
    print("REACHED")  # should never happen — the server demands a client cert
except ssl.SSLError:
    print("TLS_REJECTED")  # the server aborted the TLS handshake (no acceptable client cert)
except (ConnectionResetError, http.client.RemoteDisconnected, BrokenPipeError):
    print("TLS_REJECTED")  # reached the server; it closed the connection (no client cert) — the lock
except (ConnectionRefusedError, socket.timeout, socket.gaierror) as e:
    print("CONN_FAILED:%s" % (type(e).__name__,))  # never reached the server — NOT a TLS rejection
except OSError as e:
    print("CONN_FAILED:%s" % (type(e).__name__,))   # any other transport failure — fail loudly
PY
)"
assert_eq "A7 no-client-cert rejected" "TLS_REJECTED" "$NOCLIENT_RESULT"

# (b) WITH a client cert via Kong: a real /mcp initialize through Kong completing (HTTP 200) proves
#     the Kong→mcp-server mutual-TLS hop succeeds — Kong presents its committed client cert AND
#     verifies the server cert against the POC CA, or the upstream TLS handshake would 502. We
#     reuse the /mcp initialize path (not the well-known route) so the probe exercises the SAME
#     mTLS hop and is independent of the well-known-route jwt-disable defect (see A5's note).
say "A7 — the WITH-client-cert path (via Kong) completes: a real /mcp initialize over mTLS returns 200"
VIAKONG_ST="$(mcp_post_ok "$TOK_OK" "$INIT_BODY")"
assert_eq "A7 via-Kong mTLS completes" "200" "$VIAKONG_ST"

# ── summary ─────────────────────────────────────────────────────────────────────────
say "MCP-edge runtime-contract results"
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

if [ "$FAIL" -ne 0 ]; then
  die "mcp-contract-test: $FAIL assertion(s) FAILED"
fi
say "mcp-contract-test: all assertions GREEN (5ot0 + 29ic + the ziu3.2 well-known fix)"
