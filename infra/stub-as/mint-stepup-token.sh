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
# It defaults to a FRESH step-up token bound to the MCP server audience with the deposit write scope, AND
# SENDER-CONSTRAINED (RFC 8705 mTLS-bound) so a stolen token cannot be replayed from a different sender
# (ADR-IC-010 §A8); the flags below pass straight through to mint-edge-token.sh.
#
# Sender-constraining (mTLS-bound, ADR-IC-010 §A8). The refreshed step-up token is no longer a plain
# Bearer: it carries a `cnf` claim with the `x5t#S256` thumbprint of the client certificate the holder
# presents on the mutually-authenticated connection — the SAME Kong→MCP client cert (CN=babelstone-mcp-
# client-poc) the §P5 mTLS posture (CERT_REQUIRED) already presents. The Kong /mcp route checks the
# token's cnf.x5t#S256 against the presented client cert and 401s a token replayed from a different
# sender. We compute that thumbprint LIVE from kong.yml so it never drifts from the committed cert; pass
# --no-cnf to mint a plain (POC-legacy) Bearer instead.
#
# Usage:
#   infra/stub-as/mint-stepup-token.sh                 # fresh, mTLS-bound step-up token (acr=now, cnf set)
#   infra/stub-as/mint-stepup-token.sh --no-cnf        # plain Bearer (NOT sender-constrained) -> POC-legacy
#   infra/stub-as/mint-stepup-token.sh --no-sca        # NO step-up SCA  -> exercises the engine 422
#   infra/stub-as/mint-stepup-token.sh --auth-age 600  # SCA 10 min ago  -> exercises the stale-SCA 422
#   infra/stub-as/mint-stepup-token.sh --aud <uri> --scope "deposits:write" --acr <level>
#
# Any flag scripts/mint-edge-token.sh accepts (--sub, --aud, --scope, --acr, --auth-age, --ttl,
# --no-sca, --cnf-x5t, --curl, …) is forwarded verbatim; run that script's --help for the full list.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
MINTER="$REPO_ROOT/scripts/mint-edge-token.sh"
[ -x "$MINTER" ] || { echo "step-up issuer backend not found/executable: $MINTER" >&2; exit 1; }
KONG_YML="$REPO_ROOT/infra/kong/kong.yml"

# --no-cnf opts out of the sender constraint (POC-legacy plain Bearer); any other flag is forwarded.
# We pull --no-cnf out of the argv so it does not reach mint-edge-token.sh (which has no such flag).
WANT_CNF=1
FORWARD=()
for arg in "$@"; do
  if [ "$arg" = "--no-cnf" ]; then WANT_CNF=0; else FORWARD+=("$arg"); fi
done

# Compute the RFC 8705 x5t#S256 thumbprint of the MCP client cert (CN=babelstone-mcp-client-poc) that
# Kong presents to the mcp-server upstream — the cert the §P5 CERT_REQUIRED posture mutually authenticates
# with. Reading it LIVE from kong.yml keeps the binding in lock-step with the committed cert: swap the
# cert (deploy-time deck sync) and the thumbprint this mints tracks it. The mcp-server cert is the one
# whose `id` is the a1b2c3d4-… UUID (the FIRST cert after that id marker in kong.yml's certificates list).
CNF_ARGS=()
if [ "$WANT_CNF" -eq 1 ]; then
  X5T="$(python3 - "$KONG_YML" <<'PY'
import base64, hashlib, re, sys
text = open(sys.argv[1]).read()
# The mcp-server client cert is the CERTIFICATE block following the a1b2c3d4-… id in certificates[].
idx = text.index("a1b2c3d4-e5f6-7890-abcd-ef1234567890")
m = re.search(r"-----BEGIN CERTIFICATE-----(.*?)-----END CERTIFICATE-----", text[idx:], re.S)
der = base64.b64decode("".join(l.strip() for l in m.group(1).strip().splitlines()))
print(base64.urlsafe_b64encode(hashlib.sha256(der).digest()).decode().rstrip("="))
PY
)" || { echo "failed to compute the MCP client-cert thumbprint from $KONG_YML" >&2; exit 1; }
  [ -n "$X5T" ] || { echo "empty MCP client-cert thumbprint from $KONG_YML" >&2; exit 1; }
  CNF_ARGS=(--cnf-x5t "$X5T")
fi

# Step-up defaults for the MCP money-mover channel: bound to the MCP server audience, the deposit write
# scope (the money-movers need deposits:write), a strong PSD2 acr, AND sender-constrained to the MCP
# client cert (unless --no-cnf). A caller can override any of these by passing the same flag again — the
# LAST value wins in mint-edge-token.sh's left-to-right parse, so our defaults come FIRST and a user
# override that follows takes precedence.
# NOTE: ${arr[@]+"${arr[@]}"} is the bash-3.2-safe expansion for a possibly-EMPTY array under
# `set -u` (macOS ships bash 3.2, where a bare "${arr[@]}" on an empty array is an "unbound variable").
exec "$MINTER" \
  --aud "http://localhost:8000/mcp" \
  --scope "deposits:write" \
  --acr "urn:bank:sca:psd2" \
  ${CNF_ARGS[@]+"${CNF_ARGS[@]}"} \
  ${FORWARD[@]+"${FORWARD[@]}"}
