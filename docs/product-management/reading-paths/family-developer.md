# Reading path — Family / engine developer

**You add a product [family](../reference/glossary.md#family)** — a [decider](../reference/glossary.md#decider) plus pure [folds](../reference/glossary.md#fold) — on top of the family-agnostic engine, without touching the kernel. Follow this sequence and you'll know where the engine ends and your family begins, which contract your [variant](../reference/glossary.md#variant) must satisfy, and which financial rules your folds encode. It links and sequences only — every claim lives once, in the spine ([ADR-PC-022 §P3](../product_concepts/adrs/ADR-PC-022-product-documentation-architecture.md)).

1. [Product 01 — Product Architecture](../product_concepts/01-product-architecture.md) — the family-agnostic kernel and the seam a family plugs into; orient here first.
2. [ADR-PC-021 — Application-Layer Family-Owned Deciders](../product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md) — the decision that puts the decider in your family, not the engine.
3. [ADR-PC-010 — .NET Hand-Rolled Engine](../product_concepts/adrs/ADR-PC-010-dotnet-hand-rolled-engine.md) — why the kernel is hand-rolled, so you know what it does and doesn't give you.
4. [Product 02 — v1 Scope, Portuguese Term Deposits](../product_concepts/02-v1-scope-term-deposits.md) — the one shipped family, read as the worked template for your own.
5. [families/](../../../families/README.md) — the code home where the term-deposit family lives and yours will go.
6. [reference/family-schemas/](../reference/family-schemas/README.md) — the generated CUE contract your variant config must satisfy ([ADR-PC-006](../product_concepts/adrs/ADR-PC-006-cue-schema-language.md)).
7. [Banking products — financial mathematics](../financial_concepts/banking_products_financial_mathematics.md) — the [accrual](../reference/glossary.md#accrual) and [day-count](../reference/glossary.md#day-count) math your folds must get right.

**When you're ready to DO something:** run a family through its full lifecycle first with [Tutorial 01 — constitute a term deposit end-to-end](../guides/tutorials/01-constitute-a-term-deposit-end-to-end.md), then scaffold your own with [How-to — add a product family](../guides/how-to/add-a-product-family.md).
