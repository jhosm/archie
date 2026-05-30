# /mcp-server

The bank's **MCP server** — custom code on the official **Python** MCP SDK,
exposing tools (commands), resources (CQRS read models), and vetted prompts to
authenticated LLM agents.

- **Build provenance:** in-house estate — [ADR-IC-013](../docs/product-management/integration_concepts/adrs/ADR-IC-013-in-house-estate-build-and-repository-placement.md)
- **Runtime / stack:** Python SDK (`modelcontextprotocol/python-sdk`) — [ADR-IC-010](../docs/product-management/integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md)
- **CODEOWNERS:** engine team
- **Path-scoped CI:** build + contract tests (Python)

## E.5 — minimal dev server

The walking-skeleton slice (auth deferred): a `constitute_deposit` **tool** and a
`deposit_position` **resource** (`bank://deposits/{deposit_id}`), over Streamable HTTP, that hit
the engine command/query host (`Babelstone.Engine.Api`, [ADR-PC-021](../docs/product-management/product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md) §D5) directly. The
secured edge — OAuth 2.1 + Kong per [ADR-IC-010](../docs/product-management/integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md) — is Epic J (`babelstone-e50n`).

```sh
# Reaches the engine at BABELSTONE_ENGINE_URL (default http://localhost:8080).
pip install -e ".[dev]" && pytest -q   # contract tests
python -m babelstone_mcp               # run the server (Streamable HTTP)
```

| Surface | Kind | Maps to |
|---|---|---|
| `constitute_deposit` | tool (declares `outputSchema`, P6) | `POST /v1/deposits` |
| `deposit_position` | resource | `GET /v1/deposits/{deposit_id}` |

Extraction-ready subtree per [ADR-PC-019 §P2](../docs/product-management/product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md); placement per [ADR-IC-013](../docs/product-management/integration_concepts/adrs/ADR-IC-013-in-house-estate-build-and-repository-placement.md).
