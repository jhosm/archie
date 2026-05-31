# How-to: Expose a new MCP tool

**Goal.** Add a tool to the bank's [MCP](../../reference/glossary.md#mcp-model-context-protocol) server so an LLM agent can invoke a new operation against the engine.

**Audience.** A developer working in `mcp-server/` who already understands the bank-as-MCP-server posture. If you don't, read the [chat-agent channel strategy](../../integration_concepts/11-chat-agent-channel-strategy.md) first — it explains why the agent is an untrusted caller and why every tool maps to a command or an on-demand read.

The server today exposes three tools — [`constitute_deposit`](../../reference/mcp-tools/constitute_deposit.md), [`get_deposit`](../../reference/mcp-tools/get_deposit.md), and [`mature_deposit`](../../reference/mcp-tools/mature_deposit.md) — and you add a fourth by following the exact pattern they already use.

---

## Before you start

- **A tool is a model-invokable operation.** Use a tool for a command (a write) *or* an on-demand read the agent fetches mid-reasoning. Reserve *resources* for host-attached/subscribable context. The split is **control ownership**, not the engine's CQRS command/query boundary — settled in [ADR-IC-010](../../integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md)'s 2026-05-31 amendment and [doc 11's tool/resource table](../../integration_concepts/11-chat-agent-channel-strategy.md). `get_deposit` is a read, and it is a *tool*, precisely for this reason.
- **Align the tool with an aggregate boundary, not a screen.** One tool ≈ one command on one aggregate; no "mega-tools" that fan out across aggregates ([doc 11 §Tool, Resource, and Prompt Design](../../integration_concepts/11-chat-agent-channel-strategy.md)).
- **The tool talks to the engine, not the database.** Each existing tool maps 1:1 onto the engine's HTTP API (`Babelstone.Engine.Api`, [ADR-PC-021 §D5](../../product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md)). New behaviour the engine doesn't yet expose needs an engine endpoint first.

---

## Steps

### 1. Add the engine call to the client

The HTTP boundary is wrapped by `EngineClient` in `mcp-server/src/babelstone_mcp/engine_client.py`. If your tool needs an engine call that isn't there yet, add an `async` method that hits the endpoint and returns parsed JSON, fail-loud (`raise_for_status`) — the same shape `constitute`, `deposit_position`, and `mature` already use.

### 2. Declare the structured return type

Every tool **must** publish an `outputSchema` ([ADR-IC-010 P6](../../integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md), [doc 11 "Always declare `outputSchema`"](../../integration_concepts/11-chat-agent-channel-strategy.md)). In `server.py` that means a Pydantic `BaseModel` with `Field(description=…)` on each field — the SDK derives the schema from it. All [money](../../reference/glossary.md#money-cents) is integer cents, never a float ([ADR-PC-010 §P1](../../product_concepts/adrs/ADR-PC-010-dotnet-hand-rolled-engine.md)). The existing `ConstituteDepositResult` and `DepositPosition` models are your templates.

### 3. Write the `@mcp.tool()` function

The real decorator pattern, from `mcp-server/src/babelstone_mcp/server.py`:

```python
@mcp.tool()
async def get_deposit(deposit_id: str) -> DepositPosition:
    """Read a term deposit's current state — the folded ``deposit_position`` projection.

    ``deposit_id`` is the engine-assigned UUID returned by ``constitute_deposit``. Money is integer
    cents. ... Scoped ``deposits:read`` at the gateway (ADR-IC-010 §P4).
    """
    return DepositPosition(**await engine().deposit_position(deposit_id))
```

The load-bearing parts: the `@mcp.tool()` decorator, a typed signature whose return annotation is your `BaseModel` (that is what publishes the `outputSchema`), and a docstring that becomes the tool description the agent reads. Resolve the engine through the module-level `engine()` accessor so tests can inject a client via `set_engine()`.

### 4. Write the docstring as if an untrusted agent reads it — because one does

The docstring is the agent's only instruction surface. State units (integer cents), say which inputs are model-supplied vs engine-stamped (e.g. the resolved [TAN](../../reference/glossary.md#tan-taxa-anual-nominal) is stamped by the engine, never passed in), and name the OAuth scope the gateway tiers the tool under (`deposits:read` vs `deposits:write`). The agent is steerable and hallucinates parameters — the [trust model](../../integration_concepts/11-chat-agent-channel-strategy.md) is why this matters and what the gateway enforces around it.

### 5. Know what's deferred

This dev server hits the engine directly. OAuth/Kong scoping, RFC 8707 audience binding, and the `elicitation/create` confirmation on irreversible writes are **deferred to Epic J** — the `server.py` module docstring and [ADR-IC-010](../../integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md) record exactly what the secured edge adds. Don't re-invent auth in the tool; leave the seam for the gateway.

---

## Verify

Your tool surfaces in the generated reference once the docs are regenerated: the [`mcp-tools` index](../../reference/mcp-tools/README.md) and a per-tool page are produced from the `@mcp.tool()`-decorated functions in `server.py` by `scripts/docs-gen/generate.py` (the maintainer runs `make docs-gen`). If your tool isn't decorated, typed, and `outputSchema`-bearing, it won't appear — which is the check working.

## When you're done / related tasks

- The conceptual home for *when* a long-running operation needs a polling tool vs an out-of-band callback is [doc 11 §Asynchronous Completion](../../integration_concepts/11-chat-agent-channel-strategy.md).
- Back to the [how-to index](./README.md) · [guides root](../README.md).
