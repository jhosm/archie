# Tutorials

**Learning-oriented, run-it-yourself.** A tutorial takes a newcomer by the hand through a complete, working sequence — every command shown, every result visible — so they finish with the thing actually running and a mental model to build on. Tutorials are numbered to suggest a reading order; they are the first contact for most readers and are reached from the [reading paths](../../reading-paths/README.md).

Tutorials follow the [guides invariant](../README.md): they link to the normative spine for the *why*, and keep the hand-authored part to the *do*.

| # | Tutorial | Persona entry point | Grounded in |
|---|---|---|---|
| 00 | [Bring up the dev stack](./00-bring-up-the-dev-stack.md) | Operator | `make bootstrap` / `make up` / `make verify` · [`INSTALL.md`](../../../../INSTALL.md) |
| 01 | [Constitute a term deposit end-to-end](./01-constitute-a-term-deposit-end-to-end.md) | Agent-channel consumer | `make demo-mcp` (`scripts/demo-mcp.sh`) · [MCP tools](../../reference/mcp-tools/README.md) · [constitution saga](../../integration_concepts/05-constitution-saga-walkthrough.md) |
| 02 | [Author and load a PT pack](./02-author-and-load-a-pt-pack.md) | Pack author / compliance | `make pack-validate` / `pack-build` / `pack-verify` · [pack-author skill](../../../../.claude/skills/pack-author/SKILL.md) · [ADR-PC-007](../../product_concepts/adrs/ADR-PC-007-signed-yaml-oci-pack.md) |
