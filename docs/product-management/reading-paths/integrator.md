# Reading path — Integrator / solution architect

**You wire the engine into the bank's estate** — the [ACL](../reference/glossary.md#acl-anti-corruption-layer) at the legacy boundary, the [saga](../reference/glossary.md#saga) that coordinates a constitution, the edge API, and the [event catalogue](../reference/glossary.md#event-envelope) other systems subscribe to. Follow this sequence and you'll know which seam each integration concern lives at, which contract carries it, and where to put your own adapter. It links and sequences only — every claim lives once, in the spine ([ADR-PC-022 §P3](../product_concepts/adrs/ADR-PC-022-product-documentation-architecture.md)).

1. [Integration 00 — Introduction and Foundational Decisions](../integration_concepts/00-introduction-and-decisions.md) — the estate's shape and the decisions that fix it; start here.
2. [Integration 01 — The Six Primitives](../integration_concepts/01-the-six-primitives.md) — the vocabulary of seams every later doc composes from.
3. [Integration 02 — Anti-Corruption Layer](../integration_concepts/02-anti-corruption-layer.md) — how the engine stays clean of the legacy core's model.
4. [Integration 05 — Constitution Saga Walkthrough](../integration_concepts/05-constitution-saga-walkthrough.md) — one request traced end-to-end across every seam at once.
5. [Integration 04 — Plumbing Patterns](../integration_concepts/04-plumbing-patterns.md) — the [outbox](../reference/glossary.md#outbox), idempotency, and retry mechanics that make the seams reliable.
6. [Integration 08 — Event Catalog Governance](../integration_concepts/08-event-catalog-governance.md) — how the events you subscribe to are versioned and kept from drifting.
7. [reference/events/](../reference/events/README.md) — the generated, exhaustive payload schemas your subscribers parse against.

Two ADRs anchor the boundary you'll build against: [ADR-IC-012 — Anti-Corruption Layer Implementation](../integration_concepts/adrs/ADR-IC-012-anti-corruption-layer-implementation.md) and [ADR-IC-003 — Saga Orchestrator](../integration_concepts/adrs/ADR-IC-003-saga-orchestrator.md).

**When you're ready to DO something:** stand the estate up with [Tutorial 00 — bring up the dev stack](../guides/tutorials/00-bring-up-the-dev-stack.md), watch a request flow through it in [Tutorial 01 — constitute a term deposit end-to-end](../guides/tutorials/01-constitute-a-term-deposit-end-to-end.md), then connect your legacy core with [How-to — wire the ACL to a legacy core](../guides/how-to/wire-the-acl-to-a-legacy-core.md).
