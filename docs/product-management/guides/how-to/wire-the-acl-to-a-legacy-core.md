# How-to: Wire the ACL to a legacy Core

**Goal.** Stand up the Deposits [ACL](../../reference/glossary.md#acl-anti-corruption-layer) for a new legacy Core Banking system — the boundary that lets the engine settle money without ever speaking the Core's language.

**Audience.** An engine-team developer who already understands *why* the ACL exists and what it protects. If you don't, read the [Anti-Corruption Layer concept](../../integration_concepts/02-anti-corruption-layer.md) first — this guide assumes it.

> **Honest state of the tree.** `acl/` is a documented **skeleton** today — a `README.md` and a `Dockerfile`, no source ([its README](../../../../acl/README.md) says so: "Status: skeleton — no source yet"). So this is not a "wire up the existing service" guide; it is the **build order** for the responsibilities the [ACL concept doc](../../integration_concepts/02-anti-corruption-layer.md) and [ADR-IC-012](../../integration_concepts/adrs/ADR-IC-012-anti-corruption-layer-implementation.md) already specify. Every load-bearing decision below is *already made* in those documents — this guide sequences them; it does not re-decide them.

---

## Before you start

The shape of the service is fixed, not yours to choose. Confirm you are building within these decisions:

- **It is its own service, not a library.** Deployment topology, outbound adapter pattern, inbound trigger, failure isolation, and the state/outbox relationship are all settled in [ADR-IC-012](../../integration_concepts/adrs/ADR-IC-012-anti-corruption-layer-implementation.md) (D1–D5). The [acl/README](../../../../acl/README.md) records the resulting placement: in-house estate per [ADR-IC-013](../../integration_concepts/adrs/ADR-IC-013-in-house-estate-build-and-repository-placement.md), .NET stack-coherent with the engine, hosting a per-service [outbox](../../reference/glossary.md#outbox) worker per [ADR-IC-004](../../integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md).
- **One ACL per bounded context.** The Deposits ACL is *not* shared with any other consumer — sharing is a named antipattern ([ADR-IC-012 §P1](../../integration_concepts/adrs/ADR-IC-012-anti-corruption-layer-implementation.md), [concept §Antipatterns](../../integration_concepts/02-anti-corruption-layer.md)).
- **The settlement contract is already written.** What the engine asks the Core to do (the command direction) is [ADR-PC-016](../../product_concepts/adrs/ADR-PC-016-legacy-current-account-adapter.md); how the Core's state flows back (the daily batch) is [ADR-PC-017](../../product_concepts/adrs/ADR-PC-017-legacy-batch-ingest-contract.md); which backend a state-changing request is routed to during coexistence is [ADR-PC-018](../../product_concepts/adrs/ADR-PC-018-channel-routing-coexistence.md). Read those three before writing a line of translation.

---

## Steps — the eight responsibilities you implement

The [ACL concept doc](../../integration_concepts/02-anti-corruption-layer.md) enumerates the **eight concrete responsibilities** an ACL absorbs. Building the ACL *is* implementing them, in this order. Do not restate them here — follow the link and build to each.

1. **Scaffold the service and its own database.** Per [ADR-IC-012 D1+D5](../../integration_concepts/adrs/ADR-IC-012-anti-corruption-layer-implementation.md): a dedicated .NET service with its own PostgreSQL instance holding `idempotency_keys`, `id_mappings`, `in_flight_operations`, `inbound_event_dedup`, `outbox`, and `reconciliation_runs` ([§P1](../../integration_concepts/adrs/ADR-IC-012-anti-corruption-layer-implementation.md)). The ACL has its own state — it is not a stateless proxy ([concept §Internal Structure](../../integration_concepts/02-anti-corruption-layer.md)).

2. **Define the domain-vocabulary port.** The interface the engine's decider calls speaks *deposit* language (`debitForConstitution`, `reverseConstitution`), never Core language ([ADR-IC-012 §P2](../../integration_concepts/adrs/ADR-IC-012-anti-corruption-layer-implementation.md), [concept §Internal Structure](../../integration_concepts/02-anti-corruption-layer.md)). On the engine side this is the `ISettlementPort` seam the term-deposit decider fronts ([ADR-PC-021](../../product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md), [ADR-PC-016](../../product_concepts/adrs/ADR-PC-016-legacy-current-account-adapter.md)).

3. **Write the translator and the per-operation client.** Semantic + protocol translation: a hand-rolled client per Core operation, WSDL-to-stub for SOAP only, Core types confined to the protocol-client module ([ADR-IC-012 D2 + §P2](../../integration_concepts/adrs/ADR-IC-012-anti-corruption-layer-implementation.md)). The translator carries zero business logic — "business logic in the ACL" is an antipattern ([concept §Antipatterns](../../integration_concepts/02-anti-corruption-layer.md)).

4. **Make the Core appear idempotent.** Maintain `(idempotency_key → core_reference)` keyed per Core operation, not per saga ([ADR-IC-012 §P4](../../integration_concepts/adrs/ADR-IC-012-anti-corruption-layer-implementation.md), [concept responsibility 3](../../integration_concepts/02-anti-corruption-layer.md)). This is the [idempotency](../../reference/glossary.md#idempotency-key) primitive applied at the most hostile edge.

5. **Persist the ID mapping and the error catalogue.** `deposit_id ↔ core_txn_id` is durable ([concept responsibility 4](../../integration_concepts/02-anti-corruption-layer.md)); the Core-error → domain-category table is hand-maintained code with an "unknown ⇒ non-recoverable, escalate" default ([ADR-IC-012 D2](../../integration_concepts/adrs/ADR-IC-012-anti-corruption-layer-implementation.md), [concept responsibility 5](../../integration_concepts/02-anti-corruption-layer.md)).

6. **Build the pluggable inbound adapter.** Webhook, poller, and MQ adapters all produce one internal `CoreInboundEvent`; the translator and saga never see the wire format ([ADR-IC-012 D3](../../integration_concepts/adrs/ADR-IC-012-anti-corruption-layer-implementation.md), [concept responsibility 6](../../integration_concepts/02-anti-corruption-layer.md)). This is also how the Core's own state reaches the engine as the daily batch contract of [ADR-PC-017](../../product_concepts/adrs/ADR-PC-017-legacy-batch-ingest-contract.md).

7. **Wire the local outbox and per-adapter failure isolation.** State mutation + [outbox](../../reference/glossary.md#outbox) row commit in one local transaction ([ADR-IC-012 D5 + §P6](../../integration_concepts/adrs/ADR-IC-012-anti-corruption-layer-implementation.md), [ADR-IC-004](../../integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md)); a circuit breaker + bulkhead per failure class ([ADR-IC-012 D4 + §P8](../../integration_concepts/adrs/ADR-IC-012-anti-corruption-layer-implementation.md)).

8. **Model `INDETERMINATE` as a first-class state, and make the reconciler mandatory.** Never silently retry an in-flight operation — query the Core for ground truth first ([ADR-IC-012 §P5](../../integration_concepts/adrs/ADR-IC-012-anti-corruption-layer-implementation.md), [concept §The Hard Case](../../integration_concepts/02-anti-corruption-layer.md)). The daily reconciler is not optional and must self-evidence even on a zero-divergence day ([ADR-IC-012 §P7](../../integration_concepts/adrs/ADR-IC-012-anti-corruption-layer-implementation.md), [concept responsibility 7](../../integration_concepts/02-anti-corruption-layer.md)).

8b. **Authenticate to the Core with a bounded, dedicated identity.** A dedicated service account, secrets in a manager/HSM, a separate read-only identity for the reconciler, mTLS from the [saga](../../reference/glossary.md#saga) orchestrator into the port ([concept responsibility 8](../../integration_concepts/02-anti-corruption-layer.md), and the security trust boundary in [10-security-and-threat-model](../../integration_concepts/10-security-and-threat-model.md)).

---

## Verify

The ACL's reading test, straight from the [concept doc](../../integration_concepts/02-anti-corruption-layer.md): *if the Core vendor were replaced tomorrow, how many files would change?* The healthy answer is **only the ACL ones** — and [ADR-IC-012](../../integration_concepts/adrs/ADR-IC-012-anti-corruption-layer-implementation.md) makes that structurally enforceable by lining up the deployment, database, and event-payload boundaries. If a Core type leaks past the protocol-client module, you have a leak, not an ACL.

## When you're done / related tasks

- The reverse direction — ingesting the legacy Core's daily state — is the [batch contract](../../product_concepts/adrs/ADR-PC-017-legacy-batch-ingest-contract.md); each record becomes one `LegacyInstanceObserved` event.
- The coexistence posture this ACL serves (the [strangler fig](../../reference/glossary.md#strangler-fig) migration) is explained in [feature-design-strangler-fig-coexistence](../../product_concepts/feature-design-strangler-fig-coexistence.md).
- Back to the [how-to index](./README.md) · [guides root](../README.md).
