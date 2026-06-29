# /mcp-server

The bank's **MCP server** — custom code on the official **Python** MCP SDK,
exposing model-invokable tools (both writes and on-demand reads), host-attached
resources, and vetted prompts to authenticated LLM agents (the tool/resource axis
is control-ownership, not CQRS — [ADR-IC-010](../docs/product-management/integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md) 2026-05-31 amendment).

- **Build provenance:** in-house estate — [ADR-IC-013](../docs/product-management/integration_concepts/adrs/ADR-IC-013-in-house-estate-build-and-repository-placement.md)
- **Runtime / stack:** Python SDK (`modelcontextprotocol/python-sdk`) — [ADR-IC-010](../docs/product-management/integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md)
- **CODEOWNERS:** engine team
- **Path-scoped CI:** build + contract tests (Python)

## E.5 — minimal dev server

The walking-skeleton slice (auth deferred): `constitute_deposit`, `mature_deposit`, and
`pay_interest` **tools** (writes) plus a `get_deposit` **tool** (on-demand read), over Streamable
HTTP, that hit the engine
command/query host (`Babelstone.Engine.Api`, [ADR-PC-021](../docs/product-management/product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md) §D5) directly. The
secured edge — OAuth 2.1 + Kong per [ADR-IC-010](../docs/product-management/integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md) — is Epic J (`babelstone-e50n`).

```sh
# Reaches the engine at BABELSTONE_ENGINE_URL (default http://localhost:8080).
pip install -e ".[dev]" && pytest -q   # contract tests
python -m babelstone_mcp               # run the server (Streamable HTTP)
```

| Surface | Kind | Scope | Maps to |
|---|---|---|---|
| `constitute_deposit` | tool (declares `outputSchema`, P6) | `deposits:write` | `POST /v1/deposits` (mints a UUID `Idempotency-Key`, ADR-PC-029 slot 1) |
| `get_deposit` | tool (declares `outputSchema`, P6) | `deposits:read` | `GET /v1/deposits/{deposit_id}` |
| `mature_deposit` | tool (declares `outputSchema`, P6) | `deposits:write` | `POST /v1/deposits/{deposit_id}/maturity` |
| `pay_interest` | tool (declares `outputSchema`, P6) | `deposits:write` | `POST /v1/deposits/{deposit_id}/interest` |
| `pay_installment` | tool (declares `outputSchema`, P6) | `deposits:write` | `POST /v1/loans/{loan_id}/installment` (NO caller key — the engine derives a number-pinned, SERVER-DERIVED key, ADR-PC-036 §Decision 1+3) |
| `constitute_deposit_saga` | tool (declares `outputSchema`, P6) | `deposits:write` | orchestrator `POST /api/v1/deposits/constitute` (saga PRODUCER, Document 11 Pattern 2; returns a `PROC-…` `process_id`) |
| `get_process_status` | tool (declares `outputSchema`, P6) | `deposits:read` | orchestrator `GET /api/v1/processes/{process_id}/status` (async-completion poll, Document 11 Pattern 2) |

## Observability (OTel)

The server emits OpenTelemetry traces against the SAME contract the .NET estate uses
([ADR-IC-007](../docs/product-management/integration_concepts/adrs/ADR-IC-007-observability-stack.md) Layer 1,
bd `babelstone-scd2.1`): a `service.namespace=babelstone` resource with `service.name=babelstone-mcp-server`
and a fail-fast `deployment.environment`, exported OTLP/HTTP to the **OTel Collector** (never a backend
directly, §P1). The ASGI middleware makes each MCP request a SERVER span; httpx instrumentation makes each
engine/orchestrator call a CLIENT span that propagates the W3C `traceparent` — so an MCP-driven deposit is
one connected trace (MCP → engine `deposit.*` → Npgsql query spans). Wiring lives in
`src/babelstone_mcp/telemetry.py`. Env: `OTEL_EXPORTER_OTLP_ENDPOINT` (default `http://localhost:4318`, the
dev Collector); `DEPLOYMENT_ENVIRONMENT` (or `DOTNET_ENVIRONMENT` / `ASPNETCORE_ENVIRONMENT`).

Extraction-ready subtree per [ADR-PC-019 §P2](../docs/product-management/product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md); placement per [ADR-IC-013](../docs/product-management/integration_concepts/adrs/ADR-IC-013-in-house-estate-build-and-repository-placement.md).
