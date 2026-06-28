# How to register an agent as a Logto client for the MCP edge

This guide is for an **operator** onboarding a curated AI-agent vendor (Claude,
ChatGPT, a self-hosted agent) onto the bank's MCP server. In plain English: the MCP
server is a *protected resource*, and Logto is the authority that hands out the
tokens for it. Before an agent can call a deposit tool you do two things in Logto —
register the **MCP server itself** as an API resource with a fixed set of scopes, and
register **each agent** as a client that may request those scopes. This page is the
recipe; it links each rule to its ADR rather than restating it.

> ## ⚠ Provisional page — curated cohort, DCR is the accepted gap
>
> Open, self-service agent onboarding ([Dynamic Client Registration, RFC 7591](../../product-management/integration_concepts/11-chat-agent-channel-strategy.md))
> is **not** available: Logto does not implement DCR, and that is the deliberate,
> tracked hole ([ADR-IC-021](../../product-management/integration_concepts/adrs/ADR-IC-021-iam-oauth-authorization-server.md)
> commitment C6 — the open-Boundary-9 production gate). At staging scale every agent
> client is **hand-registered** in the Logto Console, which is exactly what this guide
> covers. The operational side (the Console click-path, the verification commands) is
> the companion runbook [`infra/runbooks/iam-mcp-resource-registration.md`](../../../infra/runbooks/iam-mcp-resource-registration.md).

---

## The shape, in one breath

1. **Register the MCP server as a Logto API resource** whose *identifier is its
   canonical URI*. That identifier is the [RFC 8707](../../product-management/integration_concepts/adrs/ADR-IC-021-iam-oauth-authorization-server.md)
   resource indicator — the value Logto binds an access token's `aud` to, and the
   value this server re-checks before any tool runs.
2. **Declare the resource's scopes** — `deposits:read`, `deposits:write`,
   `transfers:write` — narrow and per-tool, no god scope.
3. **Register each agent as a client** and grant it the subset of scopes it needs.
4. **Confirm the agent SDK always sends the `resource` parameter**, so Logto binds
   `aud` and the default-resource fallback never silently un-binds it.

Steps 1–3 are Logto Console / Management-API actions (see the runbook); step 4 and
the *why* are below.

## Step 1 — the resource identifier IS the canonical MCP-server URI

When you create the API resource in Logto, its **API identifier** must be the MCP
server's canonical URI exactly as the server advertises it — the trailing slash is
significant per the MCP-Auth SDK, so register the value verbatim. The server reads the
same value from `BABELSTONE_MCP_SERVER_URI` and:

- advertises it as `resource` in its [RFC 9728](../../product-management/integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md)
  Protected Resource Metadata at `/.well-known/oauth-protected-resource`, and
- re-checks every token's `aud` against it at the app layer (defence-in-depth behind
  Kong's edge check).

Both come from one source — [`mcp_resource_indicator()`](../../../mcp-server/src/babelstone_mcp/auth.py)
— so what an agent discovers and what the server enforces can never drift. Set the
Logto resource identifier to that same string and the audience binding closes
end-to-end.

## Step 2 — declare exactly three scopes

On the resource, declare these and only these scopes ([ADR-IC-021](../../product-management/integration_concepts/adrs/ADR-IC-021-iam-oauth-authorization-server.md)
commitment C5):

| Scope | Grants | Tool(s) today |
|---|---|---|
| `deposits:read` | read a deposit / poll saga status | `get_deposit`, `get_process_status` |
| `deposits:write` | constitute / mature / pay-interest (incl. the saga producer) | `constitute_deposit`, `constitute_deposit_saga`, `mature_deposit`, `pay_interest` |
| `transfers:write` | *reserved* — money transfer | none yet (declared so the catalogue is stable) |

The server enforces **one scope per tool** ([ADR-IC-010](../../product-management/integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md)
§P4); the enforced map is the `TOOL_SCOPES` table in
[`auth.py`](../../../mcp-server/src/babelstone_mcp/auth.py). `transfers:write` is in the
registered catalogue but maps to no tool yet — it exists so the day a transfer tool
ships, the registered scope set and the enforced set already agree.

## Step 3 — register each agent, grant the minimum scopes

Create one Logto **application** (client) per curated agent vendor and grant it only
the scopes it needs for its job (a read-only analytics agent gets `deposits:read`
alone). The clients are authorization-code + PKCE; no client gets a wildcard scope.

## Step 4 — the `resource` parameter is mandatory (the footgun)

This is the one that bites silently. Logto binds a token's `aud` to a resource **only
if the client sends the `resource` request parameter** on the authorization/token
request. If a client omits it, Logto falls back to a *default* resource and the token
is **not** bound to the MCP server — so the anti-replay guarantee evaporates while
everything still appears to work ([ADR-IC-021](../../product-management/integration_concepts/adrs/ADR-IC-021-iam-oauth-authorization-server.md)
§Residual-risks). So:

- Confirm the agent SDK sends `resource=<the canonical MCP-server URI>` on every token
  request (the MCP-Auth SDK does this automatically; verify it for any custom client).
- The server's defence-in-depth makes the failure *safe*: a token whose `aud` is not
  this server's URI is rejected `401 AUDIENCE_MISMATCH` before any tool runs (the
  `audience_binds_resource` check in [`auth.py`](../../../mcp-server/src/babelstone_mcp/auth.py)),
  with a `WWW-Authenticate` pointer back to the metadata document.

## Verify it end-to-end

Before you let a newly registered agent loose, run the replay-reject check from the
runbook: mint a token for *another* resource and confirm the MCP edge rejects it. That
is the [RFC 8707](../../product-management/integration_concepts/10-security-and-threat-model.md)
cross-resource replay defence — the whole reason the resource identifier is the
server's own URI.

## Related

- [Discover tools and authenticate to the MCP server](./discover-and-authenticate-to-the-mcp-server.md) — the agent-side handshake this registration enables.
- [`infra/runbooks/iam-mcp-resource-registration.md`](../../../infra/runbooks/iam-mcp-resource-registration.md) — the operator runbook (Console click-path + verification commands).
- [ADR-IC-021](../../product-management/integration_concepts/adrs/ADR-IC-021-iam-oauth-authorization-server.md) — the IAM decision (Logto), the curated-cohort posture, and the DCR gap (C6).
