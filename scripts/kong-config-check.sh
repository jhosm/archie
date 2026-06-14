#!/usr/bin/env bash
# scripts/kong-config-check.sh — the ADR-IC-006 §P1 declarative-config gate for the
# edge API gateway (I.3 / bd babelstone-a079). Run by the `infra` path-scoped CI job
# (ADR-PC-019 §P1) and locally via `make kong-config-check`.
#
# In plain English: Kong is the single edge gateway, and its ENTIRE configuration is
# one git-tracked file (infra/kong/kong.yml, DB-less declarative mode). This script is
# the gate that stops a malformed or drifted kong.yml from ever reaching main — a bad
# route, a mistyped plugin config, or a silently-dropped edge policy fails CI here.
#
# What it proves, against infra/kong/kong.yml as it stands in the working tree:
#
#   1. deck file validate (ADR-IC-006 §P1: "deck is the gate in CI") — the
#      Kong-authored CLI validates the declarative state file's STRUCTURE and entity
#      schemas offline: unknown fields, missing required keys, malformed entities all
#      fail here. deck is pinned in mise.toml (aqua:Kong/deck), the same single source
#      of truth as every dev machine and CI runner.
#
#   2. kong config parse (KONG_DATABASE=off) on the pinned kong:3.9.1 image — the
#      gateway's OWN parser, run DB-less, validates the FULL plugin-config schema
#      (deck's offline validate does not deep-type-check every plugin field; the
#      gateway does). A rate-limiting.minute that is a string, an opentelemetry config
#      missing its endpoint, etc. fail here. The image is the exact pin the Compose
#      stack and the K8s base run (infra/compose.yaml, infra/k8s/base/kong.yaml), so
#      this gate agrees byte-for-byte with what the running gateway would accept.
#
#   3. Edge-contract assertions (ADR-IC-006 §Decision/§P1–§P6 + Document 05) — mechanical
#      fitness checks that the config still EXPRESSES the load-bearing edge guarantees,
#      so a future edit can't silently drop one (ADR-PC-020 §D3: no silent divergence):
#        - DB-less mode is asserted at the image level by the infra job's existing
#          KONG_DATABASE=off check; here we assert the config CONTENT.
#        - the client-facing routes exist: POST /api/v1/deposits/constitute and the SSE
#          GET /api/v1/processes/{id}/stream (the I.1 edge-over-saga front door), plus
#          the engine query reads GET /v1/deposits/{id} and /v1/deposits/maturities.
#        - the SSE route carries its SSE-aware settings (long read_timeout, buffering off).
#        - the edge policies are attached (jwt, rate-limiting, payload validation
#          (request-validator on Enterprise, or a CE pre-function body check — the edition
#          actually selected), PSD2 SCA enforcement on the money-mover routes — constitute
#          AND the SoR-routed existing-instance op (acr + auth_time freshness, ADR-IC-006
#          §P2), gateway-attested caller identity (X-Client-Id from the validated jwt sub
#          on the orchestrator routes and the SoR-routed op, §P4),
#          opentelemetry, and upstream mTLS to the orchestrator/engine). No fixed count —
#          the inventory grows as routes/policies do.
#        - the engine COMMAND surface (POST /v1/deposits) is NOT a public Kong route —
#          it is the orchestrator's INTERNAL saga target (ADR-IC-006 §P5 mTLS boundary),
#          never the public client write path.
#
#   3b. SoR-routing scaffold (ADR-PC-018 §1/§2/§5 + coexistence §3.1/§6.2; ADR-IC-006 §P2/§P4)
#       — the coexistence channel-routing contract, gated so a future edit cannot silently
#       drop it (ADR-PC-020 §D3). Distinct provenance from block 3 above (which is bounded to
#       ADR-IC-006 §Decision/§P1–§P6 + Document 05): these assertions are governed by
#       ADR-PC-018 (the gateway resolves instance_id -> sor -> backend) and reuse the ADR-IC-006
#       §P2/§P4 SCA + identity posture on the new money-mover route:
#        - the new-constitution route documents engine-SoR by product_family + cutover rule;
#        - the existing-instance op route names the instance_id -> sor -> backend resolution
#          AND resolves `sor` itself off the engine read surface (no client-supplied `sor`
#          path segment picks the backend — the split-brain-proofing, ADR-PC-018 §2);
#        - the SoR-routed money-mover route carries the SAME SCA gate as constitute
#          (SCA_REQUIRED freshness, ADR-IC-006 §P2) — no less-guarded new money path;
#        - the fail-closed default (SOR_UNRESOLVED, 503) refuses a legacy/unresolved op
#          rather than guessing a backend (ADR-PC-018 §5).
#
# Static-only (steps 1 + 3, no Docker; skips the kong-image parse) with:
#   KONG_CHECK_STATIC_ONLY=1 ./scripts/kong-config-check.sh
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO_ROOT"

CONFIG="infra/kong/kong.yml"
KONG_IMAGE="${KONG_IMAGE:-kong:3.9.1}" # pinned, == infra/compose.yaml + infra/k8s/base/kong.yaml
STATIC_ONLY="${KONG_CHECK_STATIC_ONLY:-0}"

note() { printf '%s\n' "$*"; }
fail() { printf '::error::%s\n' "$*" >&2; exit 1; }

[ -f "$CONFIG" ] || fail "$CONFIG not found"

# ---------------------------------------------------------------------------
# 1. deck file validate — the ADR-IC-006 §P1 deck gate (structural + entity schema).
# ---------------------------------------------------------------------------
note "== deck file validate $CONFIG =="
if command -v deck >/dev/null 2>&1; then
  deck file validate "$CONFIG"
elif command -v mise >/dev/null 2>&1; then
  # deck is pinned in mise.toml; mise exec resolves it without a global install.
  mise exec -- deck file validate "$CONFIG"
else
  fail "deck not found and mise unavailable — cannot run the ADR-IC-006 §P1 deck gate"
fi
note "deck file validate: OK"

# ---------------------------------------------------------------------------
# 2. kong config parse (DB-less) — the gateway's own full plugin-config schema check.
# ---------------------------------------------------------------------------
if [ "$STATIC_ONLY" = "1" ]; then
  note "KONG_CHECK_STATIC_ONLY=1 — skipping the kong:3.9.1 config-parse step"
elif command -v docker >/dev/null 2>&1; then
  note "== kong config parse ($KONG_IMAGE, KONG_DATABASE=off) =="
  # Mount the kong dir read-only; KONG_DATABASE=off makes `kong config parse` validate
  # the declarative file instead of trying to reach a gateway database.
  docker run --rm -e KONG_DATABASE=off \
    -v "$REPO_ROOT/infra/kong:/kong:ro" \
    "$KONG_IMAGE" kong config parse /kong/kong.yml
  note "kong config parse: OK"
else
  fail "docker not found — set KONG_CHECK_STATIC_ONLY=1 to skip the kong-image parse"
fi

# ---------------------------------------------------------------------------
# 3. Edge-contract assertions (ADR-IC-006 §Decision/§P1–§P6 + Document 05).
# ---------------------------------------------------------------------------
note "== edge-contract assertions (ADR-IC-006 + Document 05) =="
have() { grep -Eq "$1" "$CONFIG" || fail "$2"; }
hasnot() { grep -Eq "$1" "$CONFIG" && fail "$2"; return 0; }

# DB-less declarative format header (ADR-IC-006 §P1).
have '^_format_version:' "kong.yml missing _format_version (not a declarative config)"

# The I.1 edge-over-saga front door (ADR-IC-006 §P4 / Document 05 §Step 0).
have '/api/v1/deposits/constitute' "missing the constitute route POST /api/v1/deposits/constitute"
have '/api/v1/processes/[^ ]*/stream' "missing the SSE stream route /api/v1/processes/{id}/stream"

# The CQRS query reads on the engine upstream (ADR-IC-005 / ADR-PC-027).
have '/v1/deposits/maturities' "missing the maturities query read GET /v1/deposits/maturities"
have '/v1/deposits/\[\^/\]' "missing the deposit point-read route GET /v1/deposits/{id}"

# SSE-aware settings on the stream route (ADR-IC-006 §P4): a long read_timeout and
# response buffering OFF. 1800000 ms (30 min) covers an approval-gated saga.
have 'read_timeout: *1800000' "SSE stream route missing the 1800000ms (30-min) read_timeout (ADR-IC-006 §P4)"
have 'request_buffering: *false' "SSE stream route missing request_buffering: false (ADR-IC-006 §P4)"
have 'response_buffering: *false' "SSE stream route missing response_buffering: false (ADR-IC-006 §P4)"

# The edge policies (ADR-IC-006 §P2/§P3/§P4/§P5/§P6 + §Decision) — no fixed count; the
# inventory grows as routes/policies are added, so assertions are listed, not enumerated.
have 'name: *jwt' "missing the jwt plugin (token signature validation, ADR-IC-006 §1/§P7)"
# jwt must have NO anonymous fallback. The SCA + X-Client-Id pre-functions read claims from the token
# payload, and on Kong CE they run BEFORE jwt (static priority: pre-function 1000000 > jwt 1450; CE has
# no dynamic `ordering`). That is safe ONLY because the GLOBAL jwt plugin 401s an unauthenticated /
# invalid token BEFORE the upstream proxy — so a forged/tampered token never reaches the orchestrator
# and the claims the pre-functions read never take effect. An `anonymous` consumer on jwt would let an
# unauthenticated request fall through to the pre-functions' set_header and the orchestrator — breaking
# both SCA and the X-Client-Id attestation. Lock it. (End-to-end lock: the §P2 contract test, bd abig.)
# NOTE (deliberate simplicity): this greps the WHOLE config, not the jwt block specifically — today
# jwt is the only plugin where `anonymous` is the dangerous fallback, and no `anonymous:` appears
# anywhere. A future plugin that legitimately needs `anonymous` would require scoping this to the jwt
# block. Likewise, the OTHER half of the precondition — jwt staying GLOBAL (top-level plugins, not a
# route-scoped override) — is not yet gated here; `have 'name: *jwt'` only checks jwt exists. Both are
# tracked hardening (bd babelstone-abig covers the runtime end-to-end lock).
hasnot 'anonymous:' "the jwt plugin must have NO anonymous fallback — the SCA + X-Client-Id pre-functions read claims before jwt and rely on jwt 401'ing an unauthenticated request before upstream (ADR-IC-006 §1/§P7)"
have 'name: *rate-limiting' "missing the rate-limiting plugin (ADR-IC-006 §P3)"
# Payload validation (ADR-IC-006 §4): the constitute route must validate the request
# body. The Enterprise `request-validator` plugin is NOT in Kong CE (the selected
# edition), so the CE-native mechanism is a `pre-function` Lua body check — the same
# bundled mechanism the ADR mandates for SCA (§P2). Accept EITHER, so a future move to
# Enterprise/APISIX can swap to the declarative validator without tripping this gate.
grep -Eq 'name: *(request-validator|pre-function)' "$CONFIG" \
  || fail "missing payload validation on the edge (ADR-IC-006 §4: request-validator on Enterprise, or a pre-function body check on Kong CE)"
# PSD2 SCA enforcement on the constitute money-mover (ADR-IC-006 §P2, bd babelstone-6imx /
# I.4). The constitute route's access-phase pre-function MUST reject a request whose bearer
# token lacks a valid, fresh SCA-completion claim with `403 { code = "SCA_REQUIRED" }` — the
# orchestrator never starts. Kong CE has no native SCA plugin (§F2), so §P2 mandates exactly
# this CE-bundled pre-function access check. Asserting the rejection code AND the 403 status
# are BOTH present in the config means a future edit cannot silently drop SCA enforcement
# (ADR-PC-020 §D3: no silent divergence). The claim contract (acr SCA-completion + auth_time
# freshness, Document 10) is documented inline in kong.yml.
have 'SCA_REQUIRED' "missing PSD2 SCA enforcement: the constitute route must reject a token without a valid SCA-completion claim with code SCA_REQUIRED (ADR-IC-006 §P2 / bd babelstone-6imx)"
have 'kong\.response\.exit\(403' "missing the SCA 403 rejection: the constitute SCA pre-function must kong.response.exit(403, ...) on an absent/expired SCA claim (ADR-IC-006 §P2)"
# Gateway-attested caller identity (ADR-IC-006 §P4, bd babelstone-bkqo). The orchestrator's
# per-process OWNERSHIP check (EdgeAuth) trusts the X-Client-Id request header as the authenticated
# caller and never re-reads the token (Boundary 2, Document 10). So the edge MUST derive X-Client-Id
# from the VALIDATED jwt `sub` and OVERWRITE any client-supplied value (kong.service.request.set_header)
# on EVERY orchestrator-bound route — the constitute POST and the SSE stream. A missing or
# client-sourced value is an IDOR hole (a caller asserting another customer's identity). Asserting the
# exact set_header("X-Client-Id", claims.sub) form, on BOTH routes (>=2), means a future edit cannot
# silently drop the attestation or source it from anything but the validated token (ADR-PC-020 §D3).
have 'set_header\("X-Client-Id", *claims\.sub\)' "missing gateway-attested caller identity: the edge must set X-Client-Id from the VALIDATED jwt sub, not a client value (IDOR; ADR-IC-006 §P4 / bd babelstone-bkqo)"
xclient_count="$(grep -cF 'set_header("X-Client-Id", claims.sub)' "$CONFIG")"
[ "${xclient_count:-0}" -ge 2 ] \
  || fail "X-Client-Id attestation must cover BOTH orchestrator-bound routes (constitute + SSE stream); found $xclient_count of 2 (ADR-IC-006 §P4 / bd babelstone-bkqo)"
have 'name: *opentelemetry' "missing the opentelemetry plugin (W3C traceparent, ADR-IC-006 §P6)"
# Upstream mTLS to internal services (ADR-IC-006 §P5): the service presents a client
# cert to the orchestrator/engine. Expressed via a client_certificate on the service.
have 'client_certificate:' "missing upstream mTLS client_certificate (ADR-IC-006 §P5 Boundary 2)"

# ADR-IC-006 §P5 / Document 05: the engine COMMAND surface (POST /v1/deposits, the
# bare command path with NO sub-path) must NOT be a PUBLIC Kong route — the client
# starts the SAGA at the edge; the engine command is the orchestrator's internal,
# mTLS-only target. We assert no route path is exactly "/v1/deposits" (the command),
# while the query reads (/v1/deposits/maturities, /v1/deposits/{id}) ARE allowed.
if grep -Eq 'paths:.*"/v1/deposits"' "$CONFIG" \
   || grep -Eq -- '- *"?/v1/deposits"?$' "$CONFIG"; then
  fail "ADR-IC-006 §P5 violation: the engine COMMAND surface POST /v1/deposits is exposed as a public Kong route — it must be the orchestrator's INTERNAL saga target, not the public client write path"
fi

# ---------------------------------------------------------------------------
# 3b. SoR-routing scaffold (ADR-PC-018 — I.5 / bd babelstone-r3rt).
# ---------------------------------------------------------------------------
# During coexistence a state-changing operation must land on the system that holds the
# system-of-record (SoR) for that instance — the new engine (term deposits, v1) or the
# legacy core (current accounts). Routing to the wrong backend is the split-brain
# failure the whole design prevents. ADR-PC-018 places this routing on the EDGE GATEWAY
# (option b, §Decision), not in the engine and not duplicated per-channel; the binding
# placement is the bank's channel tier (§Decision binding authority / §6 slot 6). These
# assertions lock in that the GATEWAY resolves `sor` (it is never picked by a client path
# segment), that the SoR-routed money-mover is SCA-guarded to parity with constitute, and
# that the fail-closed default is present — so a future edit cannot silently re-open the
# split-brain mis-route (ADR-PC-020 §D3 / ADR-PC-018 §1/§2/§5).

# (i) New-constitution routing (ADR-PC-018 §1, the v1 load-bearing case): a NEW term
# deposit is engine-SoR by the product_family + coexistence §3.1 cutover-date rule —
# every term deposit constituted on/after cutover is engine-SoR, so it routes to the
# engine/orchestrator WITHOUT an instance lookup. The constitute route already takes
# this path; assert the routing intent is documented on it so the decision is auditable.
have 'SoR: *engine \(new-constitution rule' "missing the documented new-constitution SoR-routing intent on the constitute route (engine-SoR by product_family + cutover rule, ADR-PC-018 §1 / coexistence §3.1)"

# (ii) Existing-instance SoR routing keyed on `sor` (ADR-PC-018 §1/§2). For an existing
# instance the GATEWAY resolves instance_id -> sor -> backend. Assert the scaffold names
# this resolution explicitly so the intent is auditable and cannot be dropped.
have 'instance_id *-> *sor *-> *backend' "missing the documented instance_id -> sor -> backend SoR-resolution scaffold (ADR-PC-018 §1)"

# (ii-a) THE SPLIT-BRAIN-PROOFING (ADR-PC-018 §2: the router READS sor, never DECIDES it;
# and the client path must not decide it either). The gateway must RESOLVE `sor` itself —
# off the engine read surface (GET /v1/deposits/{id}) — not trust a client-supplied `sor`
# path segment. Assert the resolution lookup is present...
# Match the read-surface resolution GET itself (the engine deposit point-read URL the gateway
# interpolates the captured instance segment into), not a specific Lua variable name — so a safe
# refactor (e.g. URL-encoding the segment) cannot trip the gate while the resolution stays present.
have 'https://engine:8080/v1/deposits/" *\.\. *instance_' "missing the gateway-side SoR resolution: the SoR-routed op route must READ \`sor\` off the engine read surface (GET /v1/deposits/{id}), not trust a client-supplied path segment (ADR-PC-018 §1/§2 — the cardinal split-brain guard)"
have 'body\.sor *~= *"engine"' "missing the explicit engine-SoR gate: the op route must proxy to the engine ONLY on \`sor == engine\` read off the read surface, failing closed otherwise (ADR-PC-018 §2/§5)"
# ...AND assert the OLD client-supplied path-per-SoR scheme is GONE (the inversion the
# review flagged). No ROUTE PATH may key the backend on a /sor/engine or /sor/legacy path
# segment — that would let the CALLER pick the backend (split-brain). The single op route
# is /api/v1/sor/instances/{id}/operations with NO `sor` class word. Scope the check to
# quoted path entries (a `paths:` array string or a `- "..."` list item) so it cannot be
# tripped by an explanatory comment that merely names the rejected scheme.
if grep -Eq '"[^"]*/sor/(engine|legacy|unresolved)/' "$CONFIG"; then
  fail "ADR-PC-018 §2 violation (split-brain mis-route): a /sor/{engine|legacy|unresolved}/... route path lets the CLIENT pick the backend. The gateway must resolve instance_id -> sor -> backend itself; the op route must carry NO \`sor\` class word in the path (single route /api/v1/sor/instances/{id}/operations)"
fi

# (ii-b) SCA PARITY ON THE NEW MONEY-MOVER (ADR-IC-006 §P2). An existing-instance op
# (terminate early, withdraw partially) is a PIS-equivalent money-mover, the same class as
# constitute — so it must carry the same SCA freshness gate. Assert the SoR-routed op route
# enforces SCA (acr + auth_time freshness, SCA_REQUIRED) like constitute, and attests the
# caller identity (X-Client-Id from the jwt sub, §P4 — the per-instance ownership/IDOR
# guard). The presence of the SCA window + the freshness check on this file is asserted via
# the SCA_MAX_AGE constant and the SCA_REQUIRED code; that they appear on TWO money-mover
# routes (constitute + sor-ops) is asserted by requiring at least two occurrences.
if [ "$(grep -c 'SCA_MAX_AGE *= *300' "$CONFIG")" -lt 2 ]; then
  fail "ADR-IC-006 §P2 regression: the SoR-routed existing-instance op is a money-mover but is NOT SCA-guarded to parity with constitute (expected the SCA freshness window on BOTH the constitute route and the sor-ops route)"
fi
if [ "$(grep -c 'auth_time' "$CONFIG")" -lt 2 ]; then
  fail "ADR-IC-006 §P2 regression: the SoR-routed money-mover route is missing the auth_time SCA-freshness check that constitute carries"
fi

# (iii) FAIL-CLOSED default for an unresolvable / legacy `sor` (ADR-PC-018 §5). A legacy-SoR
# or unresolvable-`sor` state-changing op must be REFUSED ("instance state unavailable") —
# the router NEVER guesses a backend, because guessing routes a command to a system that
# does not hold the SoR (split-brain). The op route's resolution pre-function fails closed
# with a stable code SOR_UNRESOLVED + a 503 on every non-engine / unresolvable outcome.
# Asserting the code AND a 503 refusal are BOTH present means a future edit cannot silently
# turn the refusal into a guess (ADR-PC-020 §D3 / ADR-PC-018 §5). NOTE OF SCOPE: these
# assert the refusal EXISTS, not that it cannot be bypassed — the bypass is closed by (ii-a)
# above (the gateway resolves `sor`; the client path cannot pick the engine backend).
have 'SOR_UNRESOLVED' "missing the fail-closed SoR refusal: an unresolvable/legacy-SoR state-changing op must be refused with code SOR_UNRESOLVED, never routed by guess (ADR-PC-018 §5)"
have 'kong\.response\.exit\(503' "missing the fail-closed 503 refusal on the SoR-routed op route (ADR-PC-018 §5: refuse 'instance state unavailable', never guess a backend)"

# (iv) Honour the engine's NEGATIVE commitment (ADR-PC-018 §6 / §Decision): the routing
# logic lives in Kong, NOT in the engine. The engine COMMAND surface (POST /v1/deposits)
# must STILL be absent as a public route — already asserted in 3a above; the SoR scaffold
# must not have re-introduced a command endpoint on the engine read surface. (No new
# assertion needed: 3a's exact-path check already covers it; this comment records the
# negative commitment is consciously preserved by the scaffold.)

note "edge-contract assertions: OK"
note "kong-config-check: all checks passed"
