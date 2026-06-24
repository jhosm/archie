# How to discover tools and authenticate to the MCP server

This guide is for an **agent-channel consumer** — the team building or operating an
LLM agent (Claude, Claude Code, a self-hosted equivalent) that needs to act on the
bank's MCP server. It walks the two things you do *before* you can call any deposit
tool: **discover** the server's authorisation requirements (the standards-based
handshake, no bank-specific shortcut), and **authenticate** with an OAuth 2.1 token
bound to this server. We link the rule for each step to its ADR rather than restating
it.

> ## ⚠ Provisional page — demo-only / partly unbuilt
>
> The bank's MCP server is a **walking skeleton**. The honest split:
>
> | Step | Status today |
> |---|---|
> | The MCP server runs and exposes deposit **tools** over Streamable HTTP | **Built (dev server).** The minimal slice (`constitute_deposit`, `get_deposit`, `mature_deposit`, `pay_interest`, the saga tools) runs against the engine — see [`mcp-server/README.md`](../../../mcp-server/README.md). |
> | **Discovery** (`/.well-known/oauth-protected-resource`, `initialize`, `tools/list`) | **Spec-mandated shape; partly wired.** The secured OAuth edge is Epic J; the *intended* RFC 9728 / RFC 8707 handshake described here is the design ([ADR-IC-010](../../product-management/integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md)), not all observable end-to-end. |
> | **OAuth 2.1 + PKCE token issuance** by the bank IAM, audience-bound per RFC 8707 | **Not operated here.** No production IAM is stood up; the demo uses a server-side demo token. Treat the OAuth flow below as the production *shape*, not a path you run against a live IAM today. |
>
> So: read this as the standards-conformant handshake an agent vendor implements
> against the bank, with each step's runnable-vs-pending status flagged. Do not treat
> it as a finished, production OAuth integration.

---

## The shape, in one breath

The bank's MCP surface is a **protected resource**. To use it an agent must, in
order:

1. **Discover** which authorisation server protects it — by fetching the server's
   RFC 9728 *Protected Resource Metadata*, not by guessing.
2. **Get a token** from that authorisation server using OAuth 2.1 with PKCE (S256),
   including the bank MCP server's canonical URI as the RFC 8707 `resource` so the
   token is *audience-bound* to this server.
3. **Connect and list tools** — `initialize` the MCP session, then `tools/list` to
   see the typed tool surface.

Every one of these is a spec requirement of the MCP **2025-11-25** spec, materialised
by [ADR-IC-010](../../product-management/integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md).
The bank does **not** offer a non-standard shortcut — interop with agents the bank
cannot control depends on the standard path
([ADR-IC-010 §P2](../../product-management/integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md)).

---

## Step 1 — Discover the authorisation server (RFC 9728)

The MCP server publishes *Protected Resource Metadata* at a well-known path. Fetch it
to learn which authorisation server issues tokens for this resource — never hard-code
the IAM URL.

```sh
# PENDING for production — the secured edge is Epic J; shape per ADR-IC-010 §P2.
curl -s https://<mcp-server-host>/.well-known/oauth-protected-resource
```

```jsonc
{
  "resource": "https://<mcp-server-host>/",          // the canonical URI — you will need it in step 2
  "authorization_servers": ["https://<bank-iam-host>/"]
}
```

The `authorization_servers` entry is where you go next; the `resource` value is the
exact string you must pass as the RFC 8707 `resource` parameter when you request a
token. Using the value the server advertises (rather than one you assume) is what
keeps the audience binding correct
([ADR-IC-010 §P2/§P3](../../product-management/integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md)).

Then fetch the authorisation server's own metadata (RFC 8414 / OIDC Discovery) to
find its authorisation and token endpoints and confirm it advertises
`code_challenge_methods_supported: ["S256"]`:

```sh
curl -s https://<bank-iam-host>/.well-known/oauth-authorization-server
```

---

## Step 2 — Get an audience-bound token (OAuth 2.1 + PKCE + RFC 8707)

The bank **reuses its existing IAM** as the OAuth 2.1 authorisation server
([ADR-IC-010 Area 4](../../product-management/integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md)) —
the same identity store that authenticates customers for the REST edge. From the
agent's side that means a standard OAuth 2.1 authorization-code flow with PKCE, plus
two MCP-specific requirements:

- **PKCE with S256 is mandatory.** The agent generates a `code_verifier`, sends its
  S256 `code_challenge` on the authorisation request, and proves possession with the
  verifier on the token request. (PKCE is enforced by the authorisation server, not
  the MCP server.)
- **The RFC 8707 `resource` parameter is mandatory** on *both* the authorisation and
  the token request, set to the canonical URI from step 1. This binds the issued
  token's `aud` claim to the bank's MCP server. A token issued for any other resource
  **will be rejected** at this server with `401`
  ([ADR-IC-010 §P3](../../product-management/integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md),
  the `MCP_WRONG_RESOURCE_TOKEN_REJECTED` invariant) — this is the structural defence
  against replaying a token across MCP servers
  ([Document 11 §Threat: token replay](../../product-management/integration_concepts/11-chat-agent-channel-strategy.md)).

Request the **narrowest scope** the task needs — scopes are per tool family, with no
"god scope" ([ADR-IC-010 §P4](../../product-management/integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md)):

| Scope | Lets you call | Notes |
|---|---|---|
| `deposits:read` | `get_deposit`, `get_process_status` | A read token **cannot** reach the write tools. |
| `deposits:write` | `constitute_deposit`, `mature_deposit`, `pay_interest`, `constitute_deposit_saga` | Irreversible money-movers additionally require step-up SCA at call time (see below). |

The actor's identity is the gateway-attested `X-Client-Id` (the OAuth `sub`), derived
from your token — **never** a tool argument
([Document 11 — the agent is untrusted](../../product-management/integration_concepts/11-chat-agent-channel-strategy.md)).
Do not try to pass a `client_id` as a parameter; it is ignored, and the binding comes
from the token.

**Client registration.** An agent with no prior relationship registers via a *Client
ID Metadata Document* (the preferred path — host a metadata document at an HTTPS URL
and use that URL as your `client_id`), with Dynamic Client Registration as a fallback
([ADR-IC-010 §P7](../../product-management/integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md)).

---

## Step 3 — Connect, then list the tools

With a token, open the MCP session over Streamable HTTP and negotiate the protocol
version, then enumerate the tools:

```sh
# Every request carries the bearer token and the pinned protocol version.
#   Authorization: Bearer <token>
#   MCP-Protocol-Version: 2025-11-25
# 1) initialize the session, then 2) tools/list (JSON-RPC over Streamable HTTP).
```

`tools/list` returns each tool with its **`inputSchema`** and **`outputSchema`**.
Every tool declares an `outputSchema` — that is mandatory at this server
([ADR-IC-010 §P6](../../product-management/integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md)) —
so your agent can reason over typed fields rather than free text. The human-readable
catalogue of what each tool does is the
[generated MCP-tools reference](../reference/mcp-tools/README.md); use it to choose a
tool, and the live `tools/list` schema as the authoritative call shape.

---

## A note on step-up SCA for money-movers

Discovery and authentication get you a *token*; they do **not** by themselves let you
move money irreversibly. `mature_deposit` and `pay_interest` settle irreversibly, so
the engine refuses them without **fresh** gateway-attested step-up SCA and returns
`422 SCA_REQUIRED` ([ADR-IC-010 §P8](../../product-management/integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md),
the `MCP_SCA_GATE_CANNOT_BYPASS` invariant). The settlement transitions on the bank's
own signal (an authorisation-server-signed `acr`/`auth_time` the agent cannot forge),
never on anything the agent reports. The
[call-a-deposit-tool how-to](./call-a-deposit-tool-and-parse-the-result.md) covers
what that looks like from the agent's side.

---

## Related

- The runtime, transport, hosting, and OAuth decision (the rule for every step here):
  [ADR-IC-010](../../product-management/integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md).
- The pattern this materialises (bank as MCP server, untrusted agent):
  [Document 11 — Chat agent channel strategy](../../product-management/integration_concepts/11-chat-agent-channel-strategy.md).
- The dev server you can run locally:
  [`mcp-server/README.md`](../../../mcp-server/README.md).
- The next step — call a tool and parse its result:
  [Call a deposit tool and parse the structured result](./call-a-deposit-tool-and-parse-the-result.md).
- The tool catalogue:
  [MCP-tools reference](../reference/mcp-tools/README.md).
- Back to the [product-docs front door](../README.md).
