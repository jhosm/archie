# IAM runbook — register the MCP server as a Logto API resource (Boundary 9, bd babelstone-zla1.10.4)

Plain English: this is the operator guide for wiring the AI-agent channel into Logto on the staging
box. You do it twice-over: once to register the **MCP server itself** as a protected API resource
(so Logto can mint tokens scoped and audience-bound to it), and once per **agent vendor** you trust,
hand-registering it as a client. Open self-service onboarding (DCR / RFC 7591) is the accepted gap
([ADR-IC-021](../../docs/product-management/integration_concepts/adrs/ADR-IC-021-iam-oauth-authorization-server.md)
commitment C6 — the open-Boundary-9 production gate), so at staging scale every client is curated and
hand-registered here.

The developer-facing companion (the *why*, with the scope table) is the how-to
[`docs/product-docs/how-to/register-an-agent-as-a-logto-client.md`](../../docs/product-docs/how-to/register-an-agent-as-a-logto-client.md).

Scope: the single-node staging box (`overlays/staging`), Logto at `https://auth.babelstone.dev`,
MCP server fronted by Kong. Prerequisite: Logto is deployed and seeded (bd babelstone-zla1.10.2 —
`logto.yaml` + `logto-jobs.yaml`).

> **Secrets discipline.** No client secret, token, or PEM is ever committed. Client secrets live in
> the OpenBao-seeded Kubernetes Secret and are injected at deploy; tokens are minted at runtime and
> never written to the repo (memory: secrets off the bus; ADR-PC-004 §A1).

---

## 0. Reach the Logto Admin Console

The Console is fronted at its own HTTPS host, **`https://auth-admin.babelstone.dev`** (the
`logto-admin` Ingress, bd zla1.10). Open it in a browser and sign in with the Logto admin account.

> Do **not** use a `kubectl port-forward` to `localhost:3002`: Logto OSS v1.41 mints the operator's
> Management-API tokens with `iss = {ADMIN_ENDPOINT}/oidc`, and the default tenant rejects any
> issuer that is not the real `auth-admin` host — so on a port-forward every Console write 401s
> (`JWTClaimValidationFailed`). `ADMIN_ENDPOINT=https://auth-admin.babelstone.dev` in `logto.yaml`
> is what makes the Console usable. (The host is auth-gated by the admin login; edge hardening is
> tracked as bd zla1.10.6.)

## 1. Register the MCP server as an API resource (RFC 8707)

The **canonical staging MCP URI is `https://api.babelstone.dev/mcp`** — Kong fronts the MCP
server at `api.babelstone.dev` and routes `/mcp` with `strip_path: false` (`infra/kong/kong.yml`),
so the path is preserved verbatim. This value MUST equal the server's `BABELSTONE_MCP_SERVER_URI`
(what `mcp_resource_indicator()` in `mcp-server/src/babelstone_mcp/auth.py` returns and the RFC 9728
metadata advertises as `resource`).

> **Automated path (preferred).** Sections 1-2 (the resource + its three scopes — the durable
> C1/C5 substrate) are codified in [`scripts/iam/register-mcp-resource.py`](../../scripts/iam/register-mcp-resource.py),
> an idempotent Management-API script. Logto's app config is not captured in the manifests or the
> `logto db seed` job, so a re-onboard wipes it — re-run this to recreate the substrate. Export a
> Management-API token from the `babelstone-mgmt` M2M app first (see the script's docstring), then
> `python3 scripts/iam/register-mcp-resource.py`. The manual console steps below remain the
> equivalent by-hand procedure.

Manual console steps:

1. Console → **API resources** → **Create API resource**.
2. **API identifier** = the canonical URI above, pasted **verbatim** (the trailing-slash rule is
   slash-sensitive in the MCP-Auth SDK).
3. Verify the server and Logto agree on the identifier:

   ```bash
   # what the server advertises (must equal the Logto API identifier you just set)
   curl -s https://<mcp-host>/.well-known/oauth-protected-resource | jq -r .resource
   ```

## 2. Declare exactly three scopes on the resource (C5)

On the new API resource, add these scopes — narrow, per-tool, no god scope
([ADR-IC-021](../../docs/product-management/integration_concepts/adrs/ADR-IC-021-iam-oauth-authorization-server.md)
C5; enforced one-scope-per-tool by `TOOL_SCOPES` in `auth.py`):

| Scope | Purpose |
|---|---|
| `deposits:read` | read a deposit / poll saga status |
| `deposits:write` | constitute / mature / pay-interest (incl. the saga producer) |
| `transfers:write` | reserved for a future transfer tool; declared so the registered catalogue stays stable |

Declare all three even though `transfers:write` has no tool yet — registering the full catalogue now
keeps Logto and the resource server (`RESOURCE_SCOPES` in `auth.py`) from drifting when the transfer
tool lands.

## 3. Hand-register each curated agent as a client

For each trusted vendor (Claude, ChatGPT, a self-hosted agent):

1. Console → **Applications** → **Create application** → type **Machine-to-machine** or **Native /
   SPA** as appropriate for the agent's flow (authorization-code + PKCE for interactive agents).
2. Grant the application **only** the scopes it needs on the MCP API resource (a read-only analytics
   agent gets `deposits:read` alone — least privilege).
3. Record the client_id in the cohort register; put any client secret in the OpenBao-seeded Secret,
   never in git.

## 4. Confirm the `resource` parameter is always sent (the footgun)

Logto binds `aud` to the resource **only when the client sends the `resource` request parameter**. If
omitted, Logto falls back to a default resource and the token is silently **not** bound to the MCP
server ([ADR-IC-021](../../docs/product-management/integration_concepts/adrs/ADR-IC-021-iam-oauth-authorization-server.md)
§Residual-risks). For each agent:

- Confirm its SDK sends `resource=<canonical MCP-server URI>` on the authorization/token request (the
  MCP-Auth SDK does this automatically; verify any custom client).
- Decode a freshly issued token and assert `aud` equals the MCP server URI:

  ```bash
  # paste a freshly minted agent access token; check the aud claim
  echo "$TOKEN" | cut -d. -f2 | base64 -d 2>/dev/null | jq -r '.aud'
  # → must print the canonical MCP-server URI, not Logto's default resource
  ```

## 5. Verify the cross-resource replay reject (RFC 8707, C1)

The whole point of binding `aud` to the server's own URI is that a token minted for *another*
resource is refused here. Prove it before letting an agent loose:

1. Mint (or reuse) a token whose `aud` is some **other** resource.
2. Call the MCP edge with it:

   ```bash
   curl -s -o /dev/null -w '%{http_code}\n' \
     -H "Authorization: Bearer $WRONG_AUD_TOKEN" \
     -X POST https://<mcp-host>/mcp \
     -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
   # → 401  (body code AUDIENCE_MISMATCH; WWW-Authenticate carries resource_metadata)
   ```

   A `401` confirms the binding. This is the Kong-edge + app-layer leg of catalogue Test ID
   `MCP_WRONG_RESOURCE_TOKEN_REJECTED` / the reserved `IAM_TOKEN_AUD_RESOURCE_BOUND` (C1); the app
   unit leg is `mcp-server/tests/test_auth.py`.

> **AS-side C1/C5 verified on the live staging Logto (2026-07-08, bd zla1.10.5).** With the
> resource + scopes registered and a curated M2M agent, tokens minted via `client_credentials`
> against `https://auth.babelstone.dev/oidc/token` were decoded and asserted:
> - **C1 aud-binding** — a token requested with `resource=https://api.babelstone.dev/mcp` carried
>   `aud=https://api.babelstone.dev/mcp` exactly.
> - **C1 cross-resource isolation** — the same agent requesting `resource=https://default.logto.app/api`
>   got `aud=https://default.logto.app/api` and was **not** granted the `deposits:*` scopes (scopes are
>   resource-bound; they do not leak across resources).
> - **Default-resource footgun (stronger than feared)** — omitting `resource` on the M2M grant returns
>   `invalid_target` (Logto **fails closed**, no silent default-resource fallback). The §4 footgun
>   therefore applies only to the interactive authorization-code flow, which the MCP-Auth SDK covers.
> - **C5** — only the three per-tool scopes exist; a token carries only the requested scope.
>
> This is the **AS-side (token-minting)** proof. The **end-to-end** leg (a real token through Kong to
> the MCP server) still needs the `mcp-server` deploy repointed off its placeholder
> `BABELSTONE_MCP_SERVER_URI` / `BABELSTONE_IAM_URL` env, and the CI test that flips
> `IAM_TOKEN_AUD_RESOURCE_BOUND` to **Live** — both tracked under bd zla1.10.5.

## 6. Verify per-tool scope enforcement (C5)

A `deposits:read` token must not reach a write tool:

1. Mint a token granted `deposits:read` only (and the correct `aud`).
2. Call a write tool (`constitute_deposit`) — the server rejects it with an insufficient-scope
   `McpError` (the `check_tool_scope` gate in `auth.py`), before the engine is touched.

---

## Production gate (do NOT skip when promoting Boundary 9)

This staging wiring is the **curated-cohort** posture. Before open, un-pre-provisioned agent
onboarding, ship **DCR (RFC 7591)** or an RFC 7591 → Logto-Management-API shim
([ADR-IC-021](../../docs/product-management/integration_concepts/adrs/ADR-IC-021-iam-oauth-authorization-server.md)
C6 / §Rejected-not-taken). Until then, every client passes through Section 3 by hand.
