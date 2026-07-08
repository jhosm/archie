# IAM runbook — repoint the MCP edge at the real staging resource (end-to-end C1, bd zla1.10.5 slice 4)

Plain English: until now the MCP server's deployment and Kong's `/mcp` audience check still pointed at
placeholder URLs (`http://localhost:8000/mcp`), so a **real** Logto token — which Logto binds to
`https://api.babelstone.dev/mcp` (RFC 8707) — would be rejected at the edge as an audience mismatch. This
change repoints both to the real staging URI so a genuine token actually flows through Kong into the MCP
server, closing ADR-IC-021 §C1 end-to-end. It keeps the committed `kong.yml` on the POC value (so CI and
the offline `mcp-contract-test` harness keep passing) and does the swap at **deploy time**, exactly like
the `iss`/JWKS placeholder swaps.

See [ADR-IC-021](../../docs/product-management/integration_concepts/adrs/ADR-IC-021-iam-oauth-authorization-server.md)
§C1 and [ADR-IC-006 §P7](../../docs/product-management/integration_concepts/adrs/ADR-IC-006-edge-api-gateway.md)
(the deploy-time placeholder-swap discipline).

## What changed (this PR)

- `infra/k8s/apps/mcp-server.yaml`: `BABELSTONE_MCP_SERVER_URI` → `https://api.babelstone.dev/mcp` (the
  aud the app re-checks, §P3); `BABELSTONE_IAM_URL` → `https://auth.babelstone.dev/oidc` (the AS it
  advertises in the RFC 9728 metadata, §P2 — Logto's real issuer).
- `scripts/deck-sync.sh`: a new `rewrite_mcp_server_uri` (mirrors `rewrite_iam_issuer`) rewrites the
  deployed kong.yml's `/mcp` pre-function `MCP_SERVER_URI` **and** the two RFC 9728 resource-metadata
  pointers (`RESOURCE_METADATA` + `CNF_RESOURCE_METADATA`) from the POC placeholder to
  `https://api.babelstone.dev/{mcp,.well-known/oauth-protected-resource}`. Count-checked (1 aud + 2
  metadata); a botched edit fails `deck file validate`, not Kong.
- `infra/kong/kong.yml`: **unchanged** — the committed file stays POC so CI (`kong-config-check.sh`) and
  the offline `mcp-contract-test.sh` (which mint POC-aud tokens) keep passing.

Validated: `scripts/deck-sync.sh --dry-run` renders + `deck file validate` passes with the rewrite applied.

## Redeploy (maintainer — cluster mutation)

```bash
# 1. Kong: sync the edge config with the real MCP aud rewritten in (needs OpenBao access).
scripts/deck-sync.sh                     # renders kong.yml with iss/JWKS + MCP_SERVER_URI swapped, deck syncs

# 2. MCP server: apply the repointed env + roll.
kubectl -n babelstone-staging apply -k infra/k8s/overlays/staging
kubectl -n babelstone-staging rollout status deploy/mcp-server
```

## Live proof — a real token through Kong reaches the MCP server

Mint a real Logto token bound to the MCP resource and drive it through the public edge; assert the aud
check now ACCEPTS it (and a wrong-resource token is still `401 AUDIENCE_MISMATCH` — RFC 8707 intact).

```bash
# A real MCP-resource token (mcp-agent M2M client + resource=the MCP URI). curl sets a real UA
# (Cloudflare 1010-bans Python-urllib in front of auth.babelstone.dev).
TOKEN=$(curl -s -A babelstone-iam/1.0 -u "$MCP_AGENT_ID:$MCP_AGENT_SECRET" \
  -d grant_type=client_credentials \
  --data-urlencode resource=https://api.babelstone.dev/mcp \
  -d scope=deposits:read https://auth.babelstone.dev/oidc/token | jq -r .access_token)

# POST an MCP initialize through the PUBLIC edge. Expect NOT 401 AUDIENCE_MISMATCH (before this change
# the edge checked aud==http://localhost:8000/mcp and 401'd a real token).
curl -s -o /dev/null -w '%{http_code}\n' -A babelstone-iam/1.0 \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -H 'Accept: application/json, text/event-stream' \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"c1-proof","version":"1"}}}' \
  https://api.babelstone.dev/mcp
# → 200 (MCP initialize handshake) — the real token passed the edge aud check and reached the server.

# Negative control: a token for a DIFFERENT resource must still be rejected at the edge.
BAD=$(curl -s -A babelstone-iam/1.0 -u "$MCP_AGENT_ID:$MCP_AGENT_SECRET" -d grant_type=client_credentials \
  --data-urlencode resource=https://default.logto.app/api -d scope=all https://auth.babelstone.dev/oidc/token | jq -r .access_token)
curl -s -A babelstone-iam/1.0 -H "Authorization: Bearer $BAD" -H 'Content-Type: application/json' \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize"}' https://api.babelstone.dev/mcp
# → 401 with code AUDIENCE_MISMATCH + a WWW-Authenticate resource_metadata pointer (RFC 8707 still enforced).
```

> The `mcp-agent` client credentials live in OpenBao / were captured at registration
> (`infra/runbooks/iam-mcp-resource-registration.md` §3); never commit or echo the secret. This live
> proof is the staging analogue of the local `scripts/mcp-contract-test.sh` A1/A3 assertions (bd
> babelstone-5ot0) — it cannot run in CI (no live staging Logto/edge in CI).
