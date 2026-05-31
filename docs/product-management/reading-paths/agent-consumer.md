# Reading path — Agent-channel consumer

**You drive the bank-as-[MCP](../reference/glossary.md#mcp-model-context-protocol)-server tool surface from an LLM agent** — calling tools to constitute, read, and mature deposits instead of speaking the engine's events directly. Follow this sequence and you'll know why the channel is MCP, what each tool does, and the exact arguments and shapes your agent passes and receives. It links and sequences only — every claim lives once, in the spine ([ADR-PC-022 §P3](../product_concepts/adrs/ADR-PC-022-product-documentation-architecture.md)).

1. [Integration 11 — Chat Agent Channel Strategy, Bank as MCP Server](../integration_concepts/11-chat-agent-channel-strategy.md) — why the agent channel is an MCP server and what that buys you; start here.
2. [ADR-IC-010 — MCP Server Runtime and SDK](../integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md) — the runtime and SDK decision behind the tool surface you call.
3. [reference/mcp-tools/](../reference/mcp-tools/README.md) — the generated index of the whole tool surface; your map of what's callable.
4. [reference/mcp-tools/constitute_deposit](../reference/mcp-tools/constitute_deposit.md) — the constitute tool's exact arguments (money in integer [cents](../reference/glossary.md#money-cents)).
5. [reference/mcp-tools/get_deposit](../reference/mcp-tools/get_deposit.md) — read a deposit's current folded [projection](../reference/glossary.md#projection).
6. [reference/mcp-tools/mature_deposit](../reference/mcp-tools/mature_deposit.md) — settle a deposit and read back the matured position.

**When you're ready to DO something:** drive the loop yourself with [Tutorial 01 — constitute a term deposit end-to-end](../guides/tutorials/01-constitute-a-term-deposit-end-to-end.md), then add your own tool with [How-to — expose a new MCP tool](../guides/how-to/expose-a-new-mcp-tool.md).
