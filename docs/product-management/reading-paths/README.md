# Reading Paths — start here, by role

The **front door** to the documentation overlay ([ADR-PC-022](../product_concepts/adrs/ADR-PC-022-product-documentation-architecture.md)). The three concern-axis series are self-contained but answer *concerns*, not *people* — a newcomer arrives knowing their **role** before they know which concern they need. A reading path is a curated, shallow→deep **link-only** sequence for one role: it threads existing concept docs, the new [guides](../guides/README.md), and the generated [reference](../reference/README.md) into an order you can follow start to finish.

> Reading paths **link and sequence**; they restate nothing ([ADR-PC-022 §P3](../product_concepts/adrs/ADR-PC-022-product-documentation-architecture.md)). Every claim still lives once, in the spine.

## Who are you?

| Persona | You want to… | Path |
|---|---|---|
| **Integrator / solution architect** | Wire the engine into the bank's estate — ACL, saga, edge API, event catalogue | _(Epic R · R.5)_ |
| **Family / engine developer** | Add a product family — a decider plus pure folds — over the family-agnostic engine | _(Epic R · R.5)_ |
| **Pack author / compliance** | Author and audit a `pt.YYYY.N` regulatory pack and its rate sheets | _(Epic R · R.5)_ |
| **Agent-channel consumer** | Drive the bank-as-MCP-server tool surface from an LLM agent | _(Epic R · R.5)_ |
| **Operator** | Run the stack, observe it, and recover it | _(Epic R · R.5)_ |

These five are the canonical persona vocabulary ([ADR-PC-022 §P4](../product_concepts/adrs/ADR-PC-022-product-documentation-architecture.md)); paths and tutorial front-matter reference these tags and do not redefine them. Adding a persona is one row here plus one path file — no structural change.

## If you don't fit a role

Read the corpus by concern instead — the [top-level map](../../../README.md) and the three series:
[financial_concepts](../financial_concepts/banking_products_financial_mathematics.md) (the math) ·
[product_concepts](../product_concepts/README.md) (the configurable product) ·
[integration_concepts](../integration_concepts/00-introduction-and-decisions.md) (how it integrates).
