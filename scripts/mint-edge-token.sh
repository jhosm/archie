#!/usr/bin/env bash
# scripts/mint-edge-token.sh — POC-ONLY local-dev helper.
#
# In plain English: the Kong edge (infra/kong/kong.yml) now refuses a constitution request unless the
# caller presents an OAuth bearer JWT that (a) is signed by the IDP key registered on the `iam-issuer`
# consumer and (b) proves recent PSD2 Strong Customer Authentication via an `acr` claim and a FRESH
# `auth_time` (bd babelstone-6imx / ADR-IC-006 §P2). Until the real bank IDP is wired in, there is no
# token source — so this script mints one locally, signing with the THROWAWAY POC keypair that is
# committed in kong.yml (the exact key the `jwt` plugin verifies against). That lets you exercise the
# edge — the 403 SCA_REQUIRED path AND the happy path — without a real IDP.
#
# ⚠️  NEVER use this against a real or shared deployment. In production the bank IDP issues these tokens
#     only after the customer completes SCA; this is a stand-in for LOCAL dev only, and it works solely
#     because kong.yml currently carries a throwaway key. Replacing that key at deploy time makes every
#     token this script mints invalid — by design.
#
# Usage:
#   scripts/mint-edge-token.sh                      # mint a COMPLIANT token, print the JWT to stdout
#   scripts/mint-edge-token.sh --curl               # also print ready-to-run curls (edge + direct)
#   scripts/mint-edge-token.sh --no-sca             # omit acr/auth_time  -> exercises 403 SCA_REQUIRED
#   scripts/mint-edge-token.sh --auth-age 600       # SCA 10 min ago      -> exercises 403 (stale, >300s)
#   scripts/mint-edge-token.sh --ttl -60            # already-expired exp  -> exercises 401 (jwt plugin)
#
# Flags:
#   --sub <id>         JWT `sub` = caller client_id              (default: CLI-2026-007842)
#   --acr <value>      `acr` claim (the SCA level)               (default: urn:bank:sca:2fa)
#   --auth-age <secs>  seconds since SCA completed               (default: 0 = now; >300 => stale 403)
#   --ttl <secs>       token lifetime; `exp` = now + ttl         (default: 3600; <=0 => expired 401)
#   --no-sca           omit `acr` + `auth_time`                  (=> 403 SCA_REQUIRED)
#   --kong-yml <path>  kong.yml to read the POC signing key from (default: infra/kong/kong.yml)
#   --curl             also print ready curls (edge + direct-to-orchestrator)
#   --edge <url>       Kong proxy base URL for --curl            (default: http://localhost:8000)
#   --orch <url>       orchestrator base URL for --curl          (default: http://localhost:8080)
#   -h | --help        this help
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"

SUB="CLI-2026-007842"
ACR="urn:bank:sca:2fa"
AUTH_AGE=0
TTL=3600
NO_SCA=0
KONG_YML="$REPO_ROOT/infra/kong/kong.yml"
PRINT_CURL=0
EDGE="http://localhost:8000"
ORCH="http://localhost:8080"
# The `iss` MUST equal the jwt_secret `key` on the iam-issuer consumer (Kong's jwt plugin matches on
# key_claim_name=iss). Keep this in sync with infra/kong/kong.yml if that key ever changes.
ISS="https://iam.babelstone.example/"

usage() { sed -n '2,40p' "$0"; exit "${1:-0}"; }

while [ $# -gt 0 ]; do
  case "$1" in
    --sub)        SUB="$2"; shift 2 ;;
    --acr)        ACR="$2"; shift 2 ;;
    --auth-age)   AUTH_AGE="$2"; shift 2 ;;
    --ttl)        TTL="$2"; shift 2 ;;
    --no-sca)     NO_SCA=1; shift ;;
    --kong-yml)   KONG_YML="$2"; shift 2 ;;
    --curl)       PRINT_CURL=1; shift ;;
    --edge)       EDGE="$2"; shift 2 ;;
    --orch)       ORCH="$2"; shift 2 ;;
    -h|--help)    usage 0 ;;
    *) echo "unknown flag: $1" >&2; usage 1 ;;
  esac
done

command -v openssl >/dev/null 2>&1 || { echo "openssl is required" >&2; exit 1; }
[ -f "$KONG_YML" ] || { echo "kong.yml not found: $KONG_YML" >&2; exit 1; }

# base64url with no padding (the JWT segment encoding).
b64url() { openssl base64 -A | tr '+/' '-_' | tr -d '='; }

# Extract the POC RSA private key from kong.yml's certificates[].key block, stripping the YAML
# block-scalar indentation. This is the key paired with the iam-issuer consumer's rsa_public_key, so a
# token signed here verifies against the `jwt` plugin (asserted by the repo's key-pairing check).
KEYFILE="$(mktemp)"
trap 'rm -f "$KEYFILE"' EXIT
awk '/-----BEGIN PRIVATE KEY-----/{f=1} f{sub(/^[[:space:]]+/,""); print} /-----END PRIVATE KEY-----/{f=0}' \
  "$KONG_YML" > "$KEYFILE"
[ -s "$KEYFILE" ] || { echo "no PRIVATE KEY block found in $KONG_YML" >&2; exit 1; }

NOW="$(date +%s)"
EXP=$((NOW + TTL))

# Build the claims. acr + auth_time are present unless --no-sca; auth_time is `now - auth-age` so a
# large --auth-age produces a STALE (expired-SCA) token the pre-function rejects with 403.
CLAIMS="\"iss\":\"$ISS\",\"sub\":\"$SUB\",\"iat\":$NOW,\"exp\":$EXP"
if [ "$NO_SCA" -eq 0 ]; then
  AUTH_TIME=$((NOW - AUTH_AGE))
  CLAIMS="$CLAIMS,\"acr\":\"$ACR\",\"auth_time\":$AUTH_TIME"
fi

HEADER='{"alg":"RS256","typ":"JWT"}'
PAYLOAD="{$CLAIMS}"

H="$(printf '%s' "$HEADER"  | b64url)"
P="$(printf '%s' "$PAYLOAD" | b64url)"
SIGNING_INPUT="$H.$P"
SIG="$(printf '%s' "$SIGNING_INPUT" | openssl dgst -sha256 -sign "$KEYFILE" -binary | b64url)"
JWT="$SIGNING_INPUT.$SIG"

printf '%s\n' "$JWT"

if [ "$PRINT_CURL" -eq 1 ]; then
  BODY='{"product_code":"TD-TRAD-12M","amount":1000000,"source_account_ref":"acct-ref-001"}'
  {
    echo
    echo "# --- Through the Kong edge (exercises jwt + PSD2-SCA enforcement) ---"
    echo "curl -i -X POST '$EDGE/api/v1/deposits/constitute' \\"
    echo "  -H 'Authorization: Bearer $JWT' \\"
    echo "  -H 'Content-Type: application/json' \\"
    echo "  -d '$BODY'"
    echo "# Expect: a no-SCA/stale/expired token -> 401 or 403 at the edge; a compliant token passes the"
    echo "# gate. NOTE: kong.yml does not yet inject X-Client-Id from the JWT sub, so end-to-end through"
    echo "# Kong also needs that header wired (the I.1 edge binding) before the orchestrator accepts it."
    echo
    echo "# --- Direct to the orchestrator (dev bypass: no Kong, no token; supply X-Client-Id yourself) ---"
    echo "curl -i -X POST '$ORCH/api/v1/deposits/constitute' \\"
    echo "  -H 'X-Client-Id: $SUB' \\"
    echo "  -H 'Content-Type: application/json' \\"
    echo "  -d '$BODY'"
    echo "# The orchestrator trusts the gateway's assertion and does NOT re-validate the token"
    echo "# (Document 10) — so the bypass needs only the gateway-attested X-Client-Id header."
  } >&2
fi
