#!/usr/bin/env bash
# infra/stub-as/mint-stepup-token.sh — POC-ONLY stub authorization server: step-up SCA token issuer.
#
# In plain English: this stands in for the bank's real authorization server (AS) in the reference
# system. When an AI agent completes a step-up strong-customer-authentication (SCA) challenge at the
# bank-controlled URL, the real AS would sign a FRESH access token proving it — a non-empty `acr` and an
# `auth_time` of now. There is no real bank AS here, so this script mints that step-up token, signing
# with the THROWAWAY POC key committed in infra/kong/kong.yml (the exact key Kong's jwt plugin verifies
# against). The agent presents the result on its retry; Kong attests the acr/auth_time to the engine as
# X-SCA-Acr / X-SCA-Auth-Time, and the engine's ScaPrecondition settles the money-mover (Q-BE Q2,
# bd babelstone-ziu3.5).
#
# ⚠️  NEVER use this against a real or shared deployment. It is a LOCAL-dev stand-in only, and it works
#     solely because kong.yml currently carries a throwaway key. Replacing that key at deploy time makes
#     every token this mints invalid — by design. Real key material must NEVER be committed (see
#     infra/stub-as/.gitignore).
#
# This is a thin, named front door over scripts/mint-edge-token.sh (the single token-minting
# implementation) so the step-up issuer has a documented home without duplicating the JWT-signing logic.
# It defaults to a FRESH step-up token bound to the MCP server audience with the deposit write scope; the
# flags below pass straight through to mint-edge-token.sh.
#
# Usage:
#   infra/stub-as/mint-stepup-token.sh                 # fresh step-up token (acr present, auth_time=now)
#   infra/stub-as/mint-stepup-token.sh --no-sca        # NO step-up SCA  -> exercises the engine 422
#   infra/stub-as/mint-stepup-token.sh --auth-age 600  # SCA 10 min ago  -> exercises the stale-SCA 422
#   infra/stub-as/mint-stepup-token.sh --aud <uri> --scope "deposits:write" --acr <level>
#
# Any flag scripts/mint-edge-token.sh accepts (--sub, --aud, --scope, --acr, --auth-age, --ttl,
# --no-sca, --curl, …) is forwarded verbatim; run that script's --help for the full list.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
MINTER="$REPO_ROOT/scripts/mint-edge-token.sh"
[ -x "$MINTER" ] || { echo "step-up issuer backend not found/executable: $MINTER" >&2; exit 1; }

# Step-up defaults for the MCP money-mover channel: bound to the MCP server audience, the deposit write
# scope (the money-movers need deposits:write), and a strong PSD2 acr. A caller can override any of these
# by passing the same flag again — the LAST value wins in mint-edge-token.sh's left-to-right parse, so
# our defaults come FIRST and a user override that follows takes precedence.
exec "$MINTER" \
  --aud "http://localhost:8000/mcp" \
  --scope "deposits:write" \
  --acr "urn:bank:sca:psd2" \
  "$@"
