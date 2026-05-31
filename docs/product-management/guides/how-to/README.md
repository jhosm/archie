# How-To Guides

**Goal-oriented.** A how-to guide is a procedure for a reader who already has the terrain and wants to accomplish one specific task. Unlike a tutorial it does not teach the ground-up mental model — it assumes it, and links to the [explanation](../README.md) series and the generated [reference](../../reference/README.md) for the load-bearing detail.

How-to guides follow the [guides invariant](../README.md): procedural and thin, link-heavy for everything normative.

| Goal | Persona | Links into |
|---|---|---|
| [Wire the ACL to a legacy core](./wire-the-acl-to-a-legacy-core.md) | Engine-team developer building the boundary service | [02-anti-corruption-layer](../../integration_concepts/02-anti-corruption-layer.md) · [ADR-IC-012](../../integration_concepts/adrs/ADR-IC-012-anti-corruption-layer-implementation.md) · [ADR-PC-016](../../product_concepts/adrs/ADR-PC-016-legacy-current-account-adapter.md)/[017](../../product_concepts/adrs/ADR-PC-017-legacy-batch-ingest-contract.md)/[018](../../product_concepts/adrs/ADR-PC-018-channel-routing-coexistence.md) |
| [Expose a new MCP tool](./expose-a-new-mcp-tool.md) | Developer working in `mcp-server/` | [11-chat-agent-channel-strategy](../../integration_concepts/11-chat-agent-channel-strategy.md) · [ADR-IC-010](../../integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md) · [mcp-tools reference](../../reference/mcp-tools/README.md) |
| [Add a product family](./add-a-product-family.md) | Engine-team developer onboarding a family | [ADR-PC-021](../../product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md) · [ADR-PC-010](../../product_concepts/adrs/ADR-PC-010-dotnet-hand-rolled-engine.md) · [ADR-PC-006](../../product_concepts/adrs/ADR-PC-006-cue-schema-language.md) · [family-schemas reference](../../reference/family-schemas/README.md) |
