# Reading path — Operator

**You run the stack, observe it, and recover it** — bring the services up, trace a request across them, and meet the recovery targets when something breaks. Follow this sequence and you'll know what the local stack is made of, how to see inside it, and what the disaster-recovery objectives commit you to. It links and sequences only — every claim lives once, in the spine ([ADR-PC-022 §P3](../product_concepts/adrs/ADR-PC-022-product-documentation-architecture.md)).

1. [infra/](../../../infra/README.md) — the dev stack's services and how they're wired; your starting inventory.
2. [Integration 06 — Observability and Distributed Tracing](../integration_concepts/06-observability-and-tracing.md) — how a single request is traced across every service so you can see inside a running system.
3. [ADR-IC-007 — Observability Stack](../integration_concepts/adrs/ADR-IC-007-observability-stack.md) — the decision behind the metrics, logs, and traces tooling you operate.
4. [ADR-PC-005 — DR, RTO, RPO](../product_concepts/adrs/ADR-PC-005-dr-rto-rpo.md) — the recovery-time and recovery-point objectives your operations must meet.
5. [Integration 04 — Plumbing Patterns](../integration_concepts/04-plumbing-patterns.md) — the [outbox](../reference/glossary.md#outbox), retry, and idempotency mechanics that make recovery safe to retry.

**When you're ready to DO something:** bring the whole stack up and verify it with [Tutorial 00 — bring up the dev stack](../guides/tutorials/00-bring-up-the-dev-stack.md).
