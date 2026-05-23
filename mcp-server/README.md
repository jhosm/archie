# /mcp-server

The bank's **MCP server** — custom code on the official **Python** MCP SDK,
exposing tools (commands), resources (CQRS read models), and vetted prompts to
authenticated LLM agents.

- **Build provenance:** in-house estate — [ADR-IC-013](../docs/product-management/integration_concepts/adrs/ADR-IC-013-in-house-estate-build-and-repository-placement.md)
- **Runtime / stack:** Python SDK (`modelcontextprotocol/python-sdk`) — [ADR-IC-010](../docs/product-management/integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md)
- **CODEOWNERS:** engine team
- **Path-scoped CI:** build + contract tests (Python)

> Status: skeleton — no source yet. Extraction-ready subtree per [ADR-PC-019 §P2](../docs/product-management/product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md); placement per [ADR-IC-013](../docs/product-management/integration_concepts/adrs/ADR-IC-013-in-house-estate-build-and-repository-placement.md).
