# ADR-PC-023: Temporal Signals Are Projection-Derived — The Engine Emits No Clock-Driven Events

| Field | Value |
|---|---|
| Status | Accepted |
| Date | 2026-06-03 |
| Deciders | jhosm |
| Shape | Contract-shape |
| Counterparty | Every downstream consumer of an "about-to-happen" signal — the customer-communications system ([ADR-PC-014](./retired/ADR-PC-014-customer-notification-emit-contract.md)), operational planning (the maturity calendar drives liquidity / renewal campaigns, [02 §2.3](../02-v1-scope-term-deposits.md)), and any future alerting tool — each of which **reads an engine projection** rather than receiving a clock-driven engine event |
| Depends on | [ADR-PC-002](./ADR-PC-002-application-level-bitemporality.md) (the bitemporal projections that *are* the temporal signal), [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) §P5 (handler purity — no clock in the fold), [ADR-IC-005](../../integration_concepts/adrs/ADR-IC-005-cqrs-read-model-storage.md) (the read surface a downstream scheduler queries) |
| Amends | [ADR-PC-014](./retired/ADR-PC-014-customer-notification-emit-contract.md) — removes engine-side emission of `SCHEDULED` `NotificationDue` and resolves its open "scheduled-trigger machinery" residual *downstream*. The PC-014 amendment lands in this same change (explicit-drift gate, [ADR-PC-020 §D3](./ADR-PC-020-llm-toolchain-and-conformance-governance.md)) |
| Resolves | the temporal-trigger half of [Q-AV](../04-open-questions.md); §B alerts gap (term-deposit scope review, 2026-06-03) |
| Related | [event-store §5.1–§5.2](../feature-design-event-store-projections.md) (pure handlers; side-effects-as-scheduled-events), [ADR-PC-012](./ADR-PC-012-gl-posting-signal-contract.md) / [ADR-PC-015](./ADR-PC-015-ifrs9-signal-contract.md) (the "engine emits clean facts, downstream interprets" siblings) |

---

## Context

A recurring question spans the whole engine: when something is *about to happen* — a deposit matures in 7 days, the 14-day pre-maturity opt-out window opens, the annual IRS-withholding statement is due — does the engine **emit a clock-driven event** announcing it, or does it **expose state** and let a downstream consumer derive the timing?

Two corpus commitments pull toward "expose state":

- **Handlers are pure; no clock.** [event-store §5.1](../feature-design-event-store-projections.md) / [ADR-PC-010 §P5](./ADR-PC-010-dotnet-hand-rolled-engine.md): "No clock reads. All timestamps come from the event envelope." The CI determinism gate (`DETERMINISM_GATE`) **fails the build** for a handler that reads the clock.
- **Events are facts about the aggregate.** A clock advance is **not a fact about the deposit** — nothing happened *to it* at T-7days; the calendar moved. A `DepositMaturityApproaching` event would be a non-fact in the log, and a rebuild could not reproduce it deterministically (it depends on *when the rebuild runs*, not on the stream).

But one Accepted contract pulls the other way: **[ADR-PC-014](./retired/ADR-PC-014-customer-notification-emit-contract.md)** defined `trigger_kind ∈ {EVENT_DRIVEN, SCHEDULED, PRE_CONTRACTUAL}` and has the engine **emit a `SCHEDULED` `NotificationDue` from an internal scheduler** at temporal points — its examples are exactly the 14-day opt-out window and the annual statement. It left *"how the engine's scheduler materialises temporal triggers"* as an explicit, unresolved residual. That residual is this decision, and resolving it the purity-preserving way means **amending PC-014**.

The distinction that makes the answer clean: a signal is either **fact-driven** or **clock-driven**.

- **Fact-driven** — caused by a domain event. A handler reacting to `DepositMatured` schedules a maturity notice through the side-effects-as-scheduled-events mechanism ([event-store §5.2](../feature-design-event-store-projections.md)). This is pure, replayable, and **stays in the engine** — it is PC-014's `EVENT_DRIVEN` kind.
- **Clock-driven** — caused only by a date arriving, with *no* domain event behind it. "Maturity is approaching" has no causing fact. This is the one that needs an engine clock — and the one this contract removes from the engine.

## Decision

**The engine emits no clock-driven signal.** Every engine-emitted signal is caused by a **domain event** (fact-driven); a signal whose only cause is "a date arrived" is **not emitted by the engine** — it is a **downstream read over an engine projection**.

1. **Payload shape.** No new event. This contract is primarily a **negative** one: it removes a class of emission. Concretely — **no clock-driven family domain event type** may exist (no `DepositMaturityApproaching`, no `PaymentDue` family event), and the engine runs **no internal scheduler** that emits a temporal signal. Temporal consumers instead query a projection ([ADR-PC-002](./ADR-PC-002-application-level-bitemporality.md)): the **maturity calendar** ("deposits maturing in date band B") and the **accrual schedule** ([02 §2.3](../02-v1-scope-term-deposits.md)) carry every date a temporal trigger could need.

2. **Semantics.** A projection *is* the temporal signal. "Which deposits mature in the next 7 days" is answered by reading the maturity-calendar projection as of today — the engine **guarantees the projection's data is correct and timely**; the downstream consumer **owns the question and the clock**. The engine asserts facts ("this deposit's `maturity_date` is D"); it never asserts "D is now close."

3. **Ordering and delivery guarantees.** Fact-driven signals keep their existing guarantees (outbox, at-least-once, per-instance order — [ADR-PC-014 slot 3](./retired/ADR-PC-014-customer-notification-emit-contract.md), [ADR-IC-004](../../integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md)). Clock-driven timing has **no engine delivery guarantee** because the engine emits nothing — the downstream scheduler's read cadence and its own delivery contract own it.

4. **Idempotency.** Trivially preserved: the engine emits no clock-driven event, so there is nothing to deduplicate across replay or wall-clock. A projection read is a query, not an emission — running it twice changes nothing. This is *why* the contract is purity-preserving: removing the clock from the emit path removes the only source of replay non-determinism a temporal event would introduce.

5. **Error model.** Not applicable in the engine — there is no clock-driven flow to fail or gate. A downstream scheduler that is late, double-reads, or crashes affects *that consumer's* notifications, never an engine domain operation. Fact-driven notifications remain **post-flag, never gated** ([ADR-PC-014 slot 5](./retired/ADR-PC-014-customer-notification-emit-contract.md)).

6. **Ownership and versioning.** The **engine owns** the projections (the temporal signal) and the rule that no clock-driven event type exists. The **downstream scheduler** (part of the customer-communications system, [ADR-PC-014](./retired/ADR-PC-014-customer-notification-emit-contract.md); deferred to **DEF-2** post-v1) owns timing, read cadence, and notification emission. A new temporal notification is a **downstream change with zero engine diff** — it reads an existing projection.

### What this amends in ADR-PC-014

Adopting this contract revises [ADR-PC-014](./retired/ADR-PC-014-customer-notification-emit-contract.md) (dated amendment, [ADR-PC-000 §D5](./ADR-PC-000-namespace-and-contract-shape-framework.md)):

- The engine **no longer emits `SCHEDULED` `NotificationDue`** and runs **no internal scheduler**. PC-014's open "scheduled-trigger machinery" residual is resolved **downstream**: a scheduler in the communications system reads the maturity-calendar / accrual-schedule / withholding-ledger projections and drives temporal notification timing itself.
- The engine's `NotificationDue` emission is now **`EVENT_DRIVEN` only** (a handler reacting to a domain fact via [event-store §5.2](../feature-design-event-store-projections.md)), plus the **`PRE_CONTRACTUAL`** FIN record-copy (the legally load-bearing FIN gate remains the synchronous saga step, unchanged).
- `EVENT_DRIVEN` and `PRE_CONTRACTUAL` and the PII-by-reference rule are **unchanged**; the `NotificationDue` schema retains its `trigger_kind` field (a downstream-produced temporal notification may still carry `SCHEDULED` — but the engine is no longer its producer).

### What stays in the engine (the fact-driven path is untouched)

A handler reacting to a real domain event still schedules its side-effect notification ([event-store §5.2](../feature-design-event-store-projections.md)) — `DepositMatured` → maturity notice — because that is *caused by a fact* and is pure and replayable. The **14-day auto-renewal opt-out is still enforced in the engine** ([02 §2.4.4](../02-v1-scope-term-deposits.md)): a customer termination inside the window is a *command* the decider acts on (a fact), not a clock-driven emission. This contract removes only the **clock-driven** emission, never the fact-driven one.

## Consequences

**Easier.** Handler purity and replay-determinism are preserved **everywhere** — there is no engine clock anywhere on the emit path, so the determinism gate holds without exception. Adding a temporal notification (a new "renewal approaching" reminder) is downstream work over an existing projection; the engine ships nothing. The maturity-calendar projection, already in scope ([F.6](../v1-build-backlog.md)), does double duty as the temporal signal.

**Harder / locked-in.** A **downstream scheduler must exist** to drive temporal notifications — it is a real component (in the communications system, DEF-2) that needs read access to the engine's projections via the query surface ([ADR-IC-005](../../integration_concepts/adrs/ADR-IC-005-cqrs-read-model-storage.md)). The maturity-calendar projection's freshness now sits on the temporal-notification path (a stale calendar delays a reminder) — acceptable for latency-tolerant notifications, but a real dependency the projection's update mechanism ([ADR-PC-002](./ADR-PC-002-application-level-bitemporality.md) sync/async) must honour.

**Impossible by construction.** A clock read cannot enter the engine's emit path (no scheduler exists to host it). A rebuild cannot produce a different set of events depending on when it runs (no event depends on wall-clock). A family-schema author cannot introduce a `DepositMaturityApproaching`-style non-fact event type — the commitment below fails the build.

## Residual risks

- **The downstream scheduler is deferred (DEF-2), so v1 ships the signal without the consumer.** v1 emits the maturity-calendar projection; the scheduler that reads it and notifies is post-v1. This is the same accept-now/build-downstream posture as [ADR-PC-014](./retired/ADR-PC-014-customer-notification-emit-contract.md)'s delivery side — named here so the gap is a tracked decision, not a surprise.
- **Polling vs push at the read surface.** Whether the downstream scheduler polls the maturity-calendar projection or subscribes to a change feed is a downstream/read-model concern ([ADR-IC-005](../../integration_concepts/adrs/ADR-IC-005-cqrs-read-model-storage.md)), out of this boundary. The engine commits only that the projection is correct and queryable as-of a date.
- **What this contract does not commit to.** The scheduler's cadence, its notification policy (which date bands warrant a reminder), and its delivery are **downstream / operating-bank deliverables**. The contract supplies the projection and the prohibition on clock-driven engine emission; it does not supply the timing logic.

## Verifiable commitments

This contract's load-bearing commitment is a fitness function in the [commitment catalogue](./commitment-catalogue.md) — the single source of truth for its exact claim, gate, and `Live`/`Planned`/`Gap` status ([ADR-PC-020 §P5–§P7](./ADR-PC-020-llm-toolchain-and-conformance-governance.md)):

- `NO_CLOCK_DRIVEN_ENGINE_SIGNAL` — no engine-emitted event is produced by a clock/scheduler (every emitted signal traces to a causing domain event); no family schema declares a clock-driven "about-to-happen" event type. Extends the `DETERMINISM_GATE` purity stance from the *fold* to the *emit path* (slot 1 · Payload shape).
