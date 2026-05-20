# Feature Design — Two Operating Modes Asymmetry

> A design-notes companion to the brief, not a numbered member of the series. Deepens [§01-product-architecture §3](./01-product-architecture.md) ("Two Families Inside One Engine"), specifically the operational-asymmetry warning at the end of that section. The brief says: "the engine architecture has to be built with the irregular profile as the upper-bound design point, even if the irregular mode lands later in the roadmap. Sizing for with-a-plan only and retrofitting irregular is one of the ways 'one engine, two modes' turns into two engines under the same name." That warning is decorative until it is operationalised. This document operationalises it: it commits to **Approach C — interfaces for v4, implementations for v1**, and specifies the six v1 architectural commitments that distinguish Approach C from a v1 design that would have to be substantially rewritten to absorb v4.
>
> This document is unusual in the series because it forces v4 thinking into v1. Every other design notes companion (configuration surface, configuration authoring, event store + projections, strangler-fig coexistence) treats v4 as future scope; this one treats v4 as a v1 design constraint. That framing is necessary because the architecture cannot absorb v4 retroactively without breaking v1 contracts that have already been written down.
>
> Reading order: §1 frames why v4 has to be a v1 concern. §2 quantifies the asymmetry. §3 names the three credible architectural answers. §4 commits to Approach C. §5 specifies the six v1 commitments that follow from Approach C. §6 specifies the event-store selection criteria that refine [Q-AC](./04-open-questions.md). §7 names how this document interacts with the other design notes companions. §8 collects consequences for the brief. §9 is status.

---

## 1. Frame: v4 as a v1 Design Constraint

The roadmap in [03-roadmap §v4](./03-roadmap.md) treats current accounts and cards as the fourth product family, several phases after v1 deposits. The same document is careful to call v4 "a firm long-term goal, optional in practice" — meaning the bank can stop at v1–v3 and still extract the full agility wedge, but the architecture must remain v4-capable regardless. The unification claim in [§01-product-architecture §1](./01-product-architecture.md) — "one engine, one runtime, one balance evolution function" — is what the v4-capability commitment is *about*. Drop v4-capability, and the unification claim collapses to "one engine for with-a-plan families, plus a separate runtime later for irregular families." That is not what the brief promises.

The architectural cost of staying v4-capable is concentrated in v1. Every v1 design decision either preserves v4 optionality or forecloses it. A v1 event store that hits a throughput wall at 10k events/day is fine for v1's term-deposit workload (~12M events/year, ~30k/day) but forecloses v4 (~100M–600M events/year, sustained 100s TPS, bursts to 1000s). A v1 handler interface that assumes batch invocation is fine for v1's daily accrual cadence but forecloses v4's real-time card-transaction ingest. A v1 event envelope without a `partition_key` field is fine for v1's unsharded operation but turns sharding into a breaking schema change when v4 needs it.

The asymmetry is not just numerical scale. The two modes differ on six independent operational dimensions (§2): instance count, event volume per instance, peak rate, per-event latency budget, event source direction (engine-generated vs externally-ingested), and lifecycle bounded­ness. Each dimension is its own architectural constraint, and each compounds the others.

The discipline of this document is to specify what "v1 stays v4-capable" means concretely — six non-negotiable v1 commitments (§5). Without that concretion, the brief's warning is decorative and the failure mode is the one the warning names: years of v1–v3 work followed by a v4 effort that finds itself rewriting the engine.

---

## 2. The Asymmetry, Quantified

The two operating modes from [§01-product-architecture §3](./01-product-architecture.md) — *with-a-plan* (v1 deposits, v2 credit, v3 mortgage) and *irregular* (v4 current accounts and cards) — differ across seven dimensions. The runtime is the same (event handlers, projections per [feature-design-event-store-projections](./feature-design-event-store-projections.md)); the operational profile is materially different.

| Property | With-a-plan (v1–v3) | Irregular (v4) | Asymmetry |
|---|---|---|---|
| **Active instances** | ~500k | ~1.5–3M | 3–6× |
| **Events per instance per year** | ~24 | ~50–200 | 2–8× |
| **Annual event volume** | ~12M | ~100M–600M | 10–50× |
| **Peak rate** | ~100k events on month-end | Sustained 100s TPS, bursts to 1000s | 100×+ at peaks |
| **Per-event latency budget** | Hours (accrual is a batch) | Seconds (balance display, fraud screening, overdraft check) | 1000×+ |
| **Event source direction** | Engine generates internally (accrual, maturity, scheduled installments) | External transactions arrive (card swipes, direct debits, salary credits) | Opposite direction |
| **Lifecycle** | Bounded (constitute → mature) | Unbounded (open → close, possibly never) | Different state machine |
| **Failure-mode stakes** | Missing accrual = recoverable (replay catches it) | Missing transaction = customer incident (balance is wrong, overdraft fires falsely) | Different operational class |

The volumetric numbers (~500k vs ~1.5–3M instances; ~12M vs ~100M–600M events) are order-of-magnitude estimates calibrated to a mid-size Portuguese retail bank; the asymmetry holds across a wide range of operating-bank sizes. The point is the ratios, not the absolute numbers.

The most architecturally consequential dimensions are the bottom four. The volumetric asymmetry can be partially addressed by infrastructure scaling (more shards, more replicas, more compute). The peak-rate, latency-budget, source-direction, and lifecycle differences cannot — they are structural properties of the workload that the engine's *interfaces* have to absorb, not just its capacity.

That structural property is the load-bearing claim of this document: **the v4 workload is not the v1 workload at higher volume. It is a different workload that happens to share the engine's runtime.**

---

## 3. Three Architectural Answers

Three credible answers to "how does the architecture stay v4-capable through v1–v3?"

| Approach | What v1 builds | What v4 changes | Tradeoff |
|---|---|---|---|
| **A. Full v4-readiness from v1** | Engine sized for v4 throughput from day one: distributed event store, sharded projection runtime, sync projections, full observability for irregular workloads | Nothing structural; v4 just turns on the irregular mode | Over-engineers v1 by years; team builds infrastructure they will not exercise until v4; high opportunity cost |
| **B. Refactor before v4** | Engine sized for v1–v3 workload: single-node event store, batch projections, eventually-consistent reads | Substantial refactor of the engine before v4 ships: replace event store, introduce sharding, rewrite projections, change envelope schema | Treats v4 as a re-architecture, which contradicts the unification claim — the engine becomes "two engines" the moment the refactor lands |
| **C. Interfaces for v4, implementations for v1** | Engine sized for v1–v3 workload, but the interfaces, envelope shapes, handler signatures, and infrastructure choices are picked to absorb v4 without breaking changes | Implementations scale up: event store sharded, projection runtime parallelised, sync-projection paths exercised. No interface changes; no breaking schema migrations | Asks v1 to absorb specific design constraints that have no v1 payoff, in exchange for keeping the v4 door open |

Approach A is the conservative shape but pays for v4 throughout v1–v3. The opportunity cost is years of team effort spent on infrastructure that does not contribute to the product. Approach A would also make v1 itself slower to ship, which compounds the risk: if v1 ships late or at all because the v1 team had to build out v4-scale infrastructure first, the engine never reaches the v4 phase to justify the investment.

Approach B is the path of least resistance during v1–v3 and the path of maximum pain at the v4 boundary. The refactor that Approach B implies is not a routine engineering exercise — it changes the event envelope (instances pinned to old schemas have to migrate), changes the handler dispatch runtime (every family schema has to be rebuilt), and changes the projection runtime (every read-model consumer has to be re-pointed). At v4 scale, with v1–v3 already in production, the refactor is the most expensive possible time for a fundamental architectural change. Approach B is what the brief's warning calls "two engines under the same name."

Approach C is the architectural commitment of this document. The reasoning: **interfaces are cheap to get right and expensive to retrofit; implementations are the opposite.** Reserving a `partition_key` field in the event envelope costs nothing in v1 (the field is set to `instance_id` and ignored by the unsharded event store) but is impossible to add at v4 scale without a breaking schema migration. Designing handler interfaces to be invocation-pattern-agnostic costs marginally in v1 (an extra layer of indirection) but is impossible to add at v4 scale without rewriting every handler. The economics of "reserve now, implement later" favour Approach C.

The cost of Approach C is concentrated in six non-negotiable v1 commitments (§5) and one acceptance criterion (the synthetic v4-scale load test, §5.6). None of the six commitments has a v1 payoff; each one is paid for by v4 viability.

---

## 4. The Commitment: Approach C

**The engine's v1 architecture commits to Approach C — interfaces for v4, implementations for v1.**

The commitment is non-negotiable for v1 acceptance. A v1 implementation that ships with any of the six commitments (§5) unmet is a v1 implementation that has foreclosed v4, and the unification claim in [§01-product-architecture §1](./01-product-architecture.md) is no longer credible.

Approach C is what the brief's [§01-product-architecture §3](./01-product-architecture.md) warning actually requires. The warning is "size for the irregular profile as the upper-bound design point"; Approach C operationalises that as "v1 implementations may be sized for v1, but v1 interfaces are sized for the irregular profile."

The discipline that follows: every v1 architectural decision is reviewed against the v4 implications. A v1 PR that introduces a batch-only assumption in a core code path is rejected even if it ships v1 functionality cleanly. A v1 PR that adds a field to the event envelope without considering its v4 routing implications is rejected. A v1 PR that picks an event store without naming its v4 scale path is rejected. The review gate is part of the v1 engineering process, not an after-the-fact check.

---

## 5. The Six v1 Architectural Commitments

Six commitments. Each one is what makes Approach C operationally true rather than aspirational. Each is a v1 deliverable.

### 5.1 Event store technology with a clear scale path

The v1 event store must be a technology that has a credible path to v4-scale throughput. Not necessarily a v4-throughput event store running at v1 traffic — that would be Approach A — but a technology where the v4 scale path is named and the migration from v1 traffic to v4 traffic is not a re-architecture.

Three candidates are credible (§6 specifies the criteria). What is *not* credible is an in-house-built event store, or an event store the team has no operational experience with. [feature-design-event-store-projections §10.4](./feature-design-event-store-projections.md) is already explicit on this: building the event store in-house is rejected, because the team's moderate event-sourcing experience cannot absorb both event-sourcing-pattern discipline AND event-store-infrastructure correctness simultaneously.

The choice between the three credible candidates is deferred (Q-AC, opened by the event-store-projections companion). This document refines the deferral with the scale-path criteria of §6.

### 5.2 No batch-only assumptions in core code paths

Handler interfaces do not know about batch vs real-time invocation. A handler has the signature `(state, event) → new_state` (per [feature-design-event-store-projections §5.1](./feature-design-event-store-projections.md)); whether the engine invokes the handler in a nightly batch over today's accruals (v1 mode) or inline on an incoming card-transaction event (v4 mode) is the *engine's* concern, not the handler's.

The discipline: the handler does not check the current time, does not check whether it is running in a batch context, does not assume the event order it is being called with reflects the order events arrived. The handler is pure (per [feature-design-event-store-projections §5.1](./feature-design-event-store-projections.md)) and works correctly under any invocation pattern.

This commitment is enforced by CI — the determinism tests from [feature-design-event-store-projections §5.3](./feature-design-event-store-projections.md) reject handlers that have batch-only assumptions baked in. The test runs the handler in both batch and inline modes against the same event sequence and compares projections; divergence is a test failure.

The v1 engine may invoke handlers exclusively in batch (daily accrual jobs, end-of-day settlement). v4 invokes the same handlers inline on irregular events. The handler does not change; only the engine's dispatch pattern does.

### 5.3 `partition_key` on every event envelope

The event envelope from [feature-design-event-store-projections §4.3](./feature-design-event-store-projections.md) is extended with a reserved field:

```
partition_key: <typically instance_id, but reserved as a separate field>
```

In v1, `partition_key` is set to `instance_id` on every event, and the unsharded event store ignores it. In v4, `partition_key` is the routing key used to spread events across shards.

The reason the field is reserved separately from `instance_id`, even though v1 sets them to the same value: some v4 workloads benefit from partitioning by something other than the instance. Cross-instance reconciliation across a customer's accounts may benefit from partitioning by `customer_id`; high-volume fraud-screening pipelines may benefit from partitioning by a hash of the merchant. The reserved field lets the family schema declare its preferred partition key per event type without changing the envelope structure.

Reserving the field in v1 costs nothing. Adding it in v4 is a breaking schema change — every consumer of every event must be updated to parse the new envelope, every instance pinned to old schemas must be migrated, every replay must handle the version skew. The economics are stark: the field is reserved.

### 5.4 Projection update mechanism designed for sync OR async — per projection

The projection runtime from [feature-design-event-store-projections §6](./feature-design-event-store-projections.md) is extended with a per-projection latency-budget declaration. Each projection's family schema declares whether the projection is **sync** (updated transactionally with the event store write; reads of the projection are guaranteed to reflect the latest event) or **async** (updated by a separate projector consuming the event stream; reads are eventually consistent within a stated lag bound).

In v1, every projection is async — the v1 workload tolerates batch-window eventual consistency, and async projections are operationally simpler. In v4, some projections must be sync: the current-account balance projection (used for overdraft checks), the available-credit projection (used for card-authorisation), the fraud-screening projection (used for transaction approval). Each of these has a per-event latency budget that async projections cannot meet.

The commitment in v1: the projection runtime supports both modes, even if no v1 projection exercises the sync mode in production. The mode is declared per projection in the family schema, not hardcoded into the engine. The engine's projection dispatch reads the mode and invokes the right path.

The synchronous-projection path needs particular care because it sits on the event store's write commit. The implementation is the harder of the two — it must avoid blocking the write path beyond a stated budget, must handle projection failures without rolling back the event commit (the event is true regardless of whether a projection consumed it), and must surface lag when the projection can't keep up. v1 builds this path even though it is unexercised; v4 turns it on.

### 5.5 Snapshot infrastructure built in v1

Snapshots are the hardest part of event sourcing to get right (per [feature-design-event-store-projections §8.3](./feature-design-event-store-projections.md): "a buggy snapshot is the worst failure mode in event sourcing because subsequent reads trust the snapshot blindly"). v1 builds the snapshot infrastructure even though v1's modest replay needs (lifecycle boundaries, calendar boundaries) only minimally exercise it.

The reason: v4's replay needs are not minimal. A current account with 5 years of transactions (~250–1000 events) needs cold replay in under 30 seconds (per [feature-design-event-store-projections §8.2](./feature-design-event-store-projections.md) and [Q-Z](./04-open-questions.md)). Without snapshots, cold replay against a deep event history takes long enough to break the read-model rebuild SLA. With snapshots, cold replay starts from the most recent snapshot and applies only the tail of events since.

The v1 snapshot infrastructure exercises:

- The triggers (per N events, at lifecycle boundaries, at calendar boundaries, per [feature-design-event-store-projections §8.1](./feature-design-event-store-projections.md)).
- The hash-and-verify mechanism (snapshot hash includes the last event_id covered; replay verification checks the rebuilt state matches the snapshot hash, per [feature-design-event-store-projections §8.3](./feature-design-event-store-projections.md)).
- The discard-and-rebuild path (monthly projection-rebuild drills routinely discard all snapshots and rebuild from cold, per [feature-design-event-store-projections §7.2](./feature-design-event-store-projections.md)).

v4 turns up the trigger frequency (more aggressive snapshot cadence, finer-grained snapshot scopes) without changing the architecture. The infrastructure is exercised continuously throughout v1–v3, which is what keeps it operationally correct by the time v4 demands it.

### 5.6 Synthetic v4-scale load tests as v1 acceptance

The v1 engine is load-tested against synthetic v4-scale traffic as part of v1 acceptance. The test is not "if needed"; it is on the v1 acceptance checklist. [feature-design-event-store-projections §10.6](./feature-design-event-store-projections.md) is already explicit about this; this document specifies the workflow falsifiable claim.

The test workload simulates the v4 profile:

- ~100 sustained TPS, bursting to ~1000 TPS for short windows (minutes).
- Mixed event sources: 80% externally-ingested (simulated card transactions, direct debits, salary credits), 20% engine-generated (simulated daily accrual, statement-cycle close, fee assessment).
- Per-event latency: synchronous projection updates (current balance, available credit) must complete within 200ms p99; asynchronous projection updates may lag up to the v1 batch window.
- Replay performance: cold replay of a synthetic 5-year-old account with 1000 events completes in under 30 seconds (the [Q-Z](./04-open-questions.md) budget).

The pass/fail criteria are workflow falsifiable claims:

- The v1 engine sustains the workload for 24 hours without OOM, without event-store write failures, without projection-rebuild divergence.
- The sustained-TPS p99 latency meets the budget.
- The burst-TPS p99 latency degrades gracefully (no event loss, no projection corruption) even when the latency budget is exceeded.
- The replay-performance budget is met.

If any pass/fail criterion is missed, v1 does not ship until the cause is identified and fixed. The fix is either a v1 implementation change (resize, retune) or a v1 architecture change (the rare case where one of §5.1–§5.5 turns out to need refinement). The fix is not "ship v1 and revisit at v4" — that is Approach B disguised as Approach C.

The test infrastructure is owned by the engine team and run on every v1 release candidate. Q-AK (below) names the open questions: exact workload patterns, exact pass/fail thresholds, exact test infrastructure shape.

---

## 6. Event Store Selection Criteria

[feature-design-event-store-projections §10.4](./feature-design-event-store-projections.md) named three candidates and deferred the choice as [Q-AC](./04-open-questions.md). This section refines the deferral with the criteria that Q-AC's resolution must satisfy.

### 6.1 The four criteria

| Criterion | What it tests | Why it matters for the asymmetry |
|---|---|---|
| **Throughput at v4 scale** | Sustained 100s TPS, bursts to 1000s, no event loss | The v1 implementation does not need this; the v1 commitment requires the technology to be capable of it without re-architecture |
| **Replay performance** | Cold replay of a 5-year account in under 30 seconds; full-projection rebuild for the bank's complete v4 book within a published window (probably 24 hours) | The hardest workload for an event-sourced system at scale; failure here is the failure mode the brief warns against |
| **Schema evolution support** | Forward-only schema evolution (per [feature-design-event-store-projections §5.4](./feature-design-event-store-projections.md)); old events readable forever; new event types added without disturbing existing pins | Event sourcing is a multi-decade commitment; the event store has to outlive the engine's first version and any reasonable refactor |
| **Operational maturity** | Production references at v4-comparable scale; documented operational runbooks; the team can answer a 3am page without inventing a procedure | Approach C's whole point is to defer v4 operational complexity; that defer only works if v4-readiness is a known operational shape |

The criteria are ordered by how decisive they are for the v1-to-v4 transition. A candidate that fails on throughput is disqualified. A candidate that fails on replay performance is disqualified. A candidate that fails on schema evolution support is disqualified. Operational maturity is a tiebreaker among candidates that pass the first three.

### 6.2 The three candidates, against the criteria

| Candidate | Throughput | Replay | Schema evolution | Operational maturity | Notes |
|---|---|---|---|---|---|
| **Kurrent / EventStoreDB** | Native; designed for event sourcing | Native; designed for it | Native; event-sourcing-first product | New dependency for the team; documented runbooks; mature product; smaller ecosystem | Best on the technical criteria; weakest on team familiarity |
| **Postgres-based** | ~1k TPS without sharding; needs careful sharding strategy for v4 | Excellent at v1 scale; replay characteristics at v4 scale depend on schema design | Application-level (the engine code maintains schema evolution discipline) | Familiar; the team operates Postgres for other systems; mature ecosystem | Strongest on team familiarity; most engineering work to sustain v4 scale |
| **Kafka-as-event-store (Redpanda per [ADR-001](../integration_concepts/adrs/ADR-001-event-backbone-message-broker.md))** | Native very high throughput | Replay = stream consumption from beginning; characteristics depend on retention and tiered storage | Streaming-first semantics; schema-registry support via [ADR-002](../integration_concepts/adrs/ADR-002-schema-format-and-registry.md) | Used by some modern fintechs; the team already operates Redpanda as the event backbone | Single technology with the existing backbone; trades SQL query ergonomics for streaming semantics |

The choice is genuinely open. Each candidate has a credible v1-to-v4 path; each has known operational tradeoffs at v4 scale; each has a different cost profile for the operating bank's specific team and existing infrastructure.

The decision is unblocked by:

- A small spike per candidate against synthetic v1-scale workload to prove implementation viability.
- The §5.6 synthetic v4-scale load test run against each candidate to surface bottlenecks.
- An operational-readiness assessment per candidate against the team's existing skills (Postgres yes; Redpanda yes; Kurrent no).
- A cost-of-operation projection per candidate over a 5-year horizon, including the v4 scale-up cost.

The choice is committed before v1 implementation begins, not deferred to v4. Picking the event store at v4 is Approach B by another name.

### 6.3 What is *not* deferred

Even though the technology choice is deferred, the *commitments* are not. Whichever event store is picked must:

- Support the envelope shape from [feature-design-event-store-projections §4.3](./feature-design-event-store-projections.md) plus the `partition_key` field from §5.3 above.
- Co-locate with the outbox per [feature-design-event-store-projections §1](./feature-design-event-store-projections.md) and [ADR-004](../integration_concepts/adrs/ADR-004-outbox-pattern-mechanism.md) so the event-write and bus-emit commit atomically.
- Honour the forward-only schema-evolution discipline from [feature-design-event-store-projections §5.4](./feature-design-event-store-projections.md).
- Support the snapshot mechanism from §5.5 above and [feature-design-event-store-projections §8](./feature-design-event-store-projections.md).

A candidate that cannot meet one of these is disqualified regardless of how well it scores on §6.1.

---

## 7. Interactions With Other Design Notes

The two-modes asymmetry is the design constraint that cuts across all the other companion documents. This section names the specific interactions.

### 7.1 With [feature-design-event-store-projections](./feature-design-event-store-projections.md)

This document **extends** the event-store-projections document with operational-profile considerations:

- **Event envelope** (event-store §4.3) gains the reserved `partition_key` field (§5.3 above).
- **Handler discipline** (event-store §5) is reinforced as a v1 commitment for v4 reasons, not just for replay correctness (§5.2 above).
- **Projection mechanism** (event-store §6) gains the per-projection sync/async declaration (§5.4 above).
- **Snapshot infrastructure** (event-store §8) is committed as a v1 deliverable rather than a v4 future (§5.5 above).
- **Event store technology** (event-store §10.4, [Q-AC](./04-open-questions.md)) is refined with the four selection criteria of §6 above.
- **Synthetic v4-scale load test** (event-store §10.6) is operationalised with workload patterns and pass/fail criteria (§5.6 above and [Q-AK](#85-new-open-questions-to-fold-into-04-open-questions) below).

The two documents are read together: event-store-projections specifies what the event-sourced engine *is*; this document specifies what its v1 implementation must commit to so v4 remains viable.

### 7.2 With [feature-design-configuration-authoring](./feature-design-configuration-authoring.md)

Family schemas for irregular families (v4) declare different event types and handler patterns than for with-a-plan families. The configuration model from configuration-authoring already accommodates this — family schemas are the variable part, and the engine does not know what a family is (per [feature-design-event-store-projections §3](./feature-design-event-store-projections.md)). The asymmetry is therefore already absorbed at the configuration layer.

The v4 family schemas will declare:

- Event types for irregular operations (`CardTransactionAuthorised`, `DirectDebitProcessed`, `SalaryCredited`, `StatementCycleClosed`, …).
- Handlers that are pure (per [feature-design-event-store-projections §5.1](./feature-design-event-store-projections.md)) and work under inline invocation (per §5.2 above).
- Projections with sync/async declarations per the projection's latency budget (per §5.4 above).
- Partition-key declarations per event type (per §5.3 above).

None of this requires a configuration-model change. The v1 configuration model — primitives, family schemas, variants — absorbs v4 by being the variable part the brief promised.

### 7.3 With [feature-design-strangler-fig-coexistence](./feature-design-strangler-fig-coexistence.md)

Legacy emission shape (the daily batch file from coexistence §5) is one async ingest source. v4 card transactions are another (real-time) ingest source. The engine must absorb both — the unified read model from coexistence §6 and the projection runtime from event-store §6 must handle the two ingestion patterns coherently.

The interaction surfaces in the projection sync/async declaration (§5.4 above): legacy-sourced projections are inherently async (24-hour staleness profile per coexistence §5.1); v4 sync projections coexist with them on the same read model. The unified-read-surface staleness asymmetry from coexistence §6.2 is the same architectural pattern as the v4 sync/async asymmetry — the read model surfaces per-row staleness regardless of source.

A specific interaction worth flagging: **a deposit maturing into a current account is a cross-mode flow.** v1's `DepositMatured` (with-a-plan) settles into the legacy current account via the ACL today (v1) and into the engine's current-account projection at v4. At v4, the cross-mode flow runs end-to-end inside the engine: `DepositMatured` from the with-a-plan side fires a settlement that creates a balance event on the irregular side. The engine has to model this without breaking either family schema's autonomy. [Q-AN](#85-new-open-questions-to-fold-into-04-open-questions) names the open question.

### 7.4 With the integration architecture

The event backbone choice ([ADR-001](../integration_concepts/adrs/ADR-001-event-backbone-message-broker.md)) is Redpanda. The ACL ([integration_concepts/02](../integration_concepts/02-anti-corruption-layer.md)) and the outbox ([integration_concepts/04](../integration_concepts/04-plumbing-patterns.md)) are inherited as-is. The v4 implications for the integration layer are:

- **Backbone throughput at v4 scale.** Redpanda is throughput-capable per its native semantics; the operating bank's specific Redpanda topology has to be sized for v4 ingest. This is an operations question, not a product-engine question, but the engine team should surface the v4 throughput projection to the integration team early.
- **ACL latency under v4 load.** v4 introduces real-time settlement paths (engine → legacy DDA during coexistence) at much higher event rates than v1. The ACL's idempotency guarantees and the indeterminate-state handling have to survive the rate increase. This is documented in [integration_concepts/02](../integration_concepts/02-anti-corruption-layer.md) at the pattern level; the operating bank's specific ACL implementation must absorb the rate increase.

These are integration-layer concerns, not engine-layer concerns. The engine commits to playing well with the integration layer; the integration layer commits to scaling to v4. The boundary is the same as for v1.

---

## 8. Consequences for the Brief

### 8.1 Sections that change

- **[§01-product-architecture §3](./01-product-architecture.md) ("Two Families Inside One Engine").** Keep the operational-asymmetry warning paragraph; add a sentence committing to Approach C and pointing at this document for the six v1 commitments. The §3 narrative stays compact; the operational detail lives here.
- **[§03-roadmap §v4](./03-roadmap.md) ("v4 stance: firm long-term goal, optional in practice").** Cross-reference this document for the v1 commitments that keep v4 viable. The "optional in practice" framing is unchanged; the architectural commitments that *make* it optional in practice (rather than infeasible in practice) are specified here.

### 8.2 Decisions committed in this document

1. **Approach C is the architectural commitment.** Interfaces for v4, implementations for v1. §3, §4.
2. **Six v1 architectural commitments are non-negotiable for v1 acceptance.** §5.
3. **The event-store selection criteria are the four of §6.1.** §6. Refines [Q-AC](./04-open-questions.md) without re-opening it.
4. **The synthetic v4-scale load test is part of v1 acceptance, with workflow falsifiable pass/fail criteria.** §5.6.

### 8.3 Envelope addition

The event envelope from [feature-design-event-store-projections §4.3](./feature-design-event-store-projections.md) gains:

- `partition_key` (reserved) — typically `instance_id` in v1; routing key in v4.

This is a payload addition under the additive-only schema-evolution discipline. No existing event type changes.

### 8.4 Open questions resolved

None directly. This document **refines** Q-AC (event-store technology selection, opened by event-store-projections) and Q-Z (replay performance targets, opened by event-store-projections); neither closes here.

### 8.5 New open questions to fold into [04-open-questions](./04-open-questions.md)

Continuing the lettered sequence from [feature-design-strangler-fig-coexistence](./feature-design-strangler-fig-coexistence.md) (which opened Q-AD through Q-AJ):

- **Q-AK. Synthetic v4-scale load test specification.** §5.6 commits to the test as v1 acceptance and names the workflow falsifiable claims. The exact workload patterns (event mix, instance-distribution shape, burst patterns), the exact pass/fail thresholds (TPS, p99 latency budgets per projection type, replay-time budget per snapshot-coverage scenario), and the exact test-infrastructure shape (where it runs, how synthetic instances are generated, how results are reported) are deferred to v1 engineering. The infrastructure is owned by the engine team.
- **Q-AL. Sharding strategy for v4.** The `partition_key` field (§5.3) reserves the routing-key shape, but the v4 sharding strategy itself — how shards are sized, how rebalancing is handled, how cross-shard transactions are avoided or coordinated, how shard-level replay is parallelised — is deferred. This is a v4-time decision, not a v1-time decision, but the v1 event store choice must not foreclose any credible v4 sharding shape.
- **Q-AM. Real-time projection backpressure.** §5.4 commits to sync/async projection support but does not specify what happens when an async projection's projector falls behind the event stream. Acceptable lag bounds per projection class; alerting thresholds; recovery procedures (catch up by replay, by parallel projector spawning, by load shedding). v4 makes this question urgent; v1 names the question.
- **Q-AN. Cross-mode reconciliation.** §7.3 flags the cross-mode flow (a deposit on the with-a-plan side maturing into a current account on the irregular side). At v4, this runs end-to-end inside the engine. The reconciliation contract between the two family schemas, the event chain across the mode boundary, the responsibility for the cross-mode invariant ("the deposit principal landing in the current account exactly once") — all deferred to v4 design.
- **Q-AO. Operational tooling asymmetry.** v1's operational tools (manual deposit lookup, batch reports, accrual investigation) are different in shape from v4's operational tools (real-time transaction search, fraud-screening review, overdraft incident triage). The engine's MCP server ([ADR-010](../integration_concepts/adrs/ADR-010-mcp-server-runtime-and-sdk.md)) and admin APIs must accommodate both. v1 builds the with-a-plan tools; v4 adds the irregular tools. The MCP surface must be additive across the mode boundary, not bifurcated into two tools.

---

## 9. Status

This document captures a design exploration, not an adopted spec. To move from exploration to spec:

1. Fold §8.1 changes into the numbered brief. The §01 §3 edit is small (one sentence and a cross-reference). The §03 §v4 edit is also small.
2. Fold §8.5 questions into [04-open-questions](./04-open-questions.md). Q-AK through Q-AO. Cross-reference Q-AC and Q-Z as refined-not-resolved.
3. Run the three event-store spikes (Kurrent, Postgres-based, Redpanda-as-event-store) under §6's four criteria during the v1 architectural-decision phase. The synthetic v4-scale load test from §5.6 is the spike's most decisive evaluation.
4. Convert Q-AK (synthetic load test specification) from open question into v1 engineering work as soon as the event-store choice is made. The test infrastructure has to exist before v1 release-candidate phase.
5. Treat each of the six commitments in §5 as v1 acceptance gates. A v1 implementation that misses any one of them is not a v1 implementation under the architectural commitment of this document.

The natural next design thread is the **v4 design notes themselves** — a future document that picks up the implementation specifics of the irregular mode (the current-account family schema, the card family schema, the cross-mode reconciliation contract, the v4 sharding strategy, the real-time projection runtime). That document is not v1 work; it is v3-or-v4 work. But the contract this document writes down — Approach C plus the six commitments — is what makes that future document possible without rewriting v1.
