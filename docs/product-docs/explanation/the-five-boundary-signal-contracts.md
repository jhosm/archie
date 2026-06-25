# The five boundary signal contracts

When the engine opens a deposit, pays a coupon, or matures a position, other
systems need to know — the general ledger has to book it, an IFRS 9 engine has to
account for it, a comms system has to tell the customer. The engine does **not**
reach into any of those systems and tell them what to do. It states a plain fact
("a deposit was constituted") and lets each downstream system decide what that
fact means for *it*. This page explains that one-way shape, why it is the same
shape five times over, and where the line of responsibility falls.

This is an **understanding** page (the Diátaxis explanation quadrant), written for
the **integrator / solution-architect** who is wiring a downstream consumer onto
the engine's boundary. It explains the *why*; the authoritative rule for each
contract lives in its ADR, which we link rather than restate.

> ## ⚠ Provisional page — built vs pending
>
> This page describes a **design boundary**, not five running integrations. The
> honest split:
>
> | Contract | What is built today |
> |---|---|
> | The engine **emits** its business-event catalogue over the outbox | **Built** — the engine appends events and relays them; the `term_deposit` family events (`DepositConstituted`, `InterestPaid`, `DepositMatured`, …) flow onto the bus (see the [generated event reference](../reference/events/README.md)). |
> | The five **downstream consumers** (GL adapter, IFRS 9 engine, comms system, temporal-trigger reader, upstream precondition resolver) | **Not built here.** They are counterparty-owned systems the engine integrates *with*; in this repo they are design contracts (ADRs), not running services. The notification-side comms system is a **skeleton** (`notification/` has source but no delivery), and several of these contracts are **v1.x / v2** (nothing is emitted for them yet). |
>
> So: read this as the *shape every consumer plugs into*, agreed up front, not as a
> set of live integrations you can observe end-to-end today.

---

## The one shape, stated once

Every one of these five boundaries obeys the same rule, the **signal-contract
principle** from
[ADR-PC-000](../../product-management/product_concepts/adrs/ADR-PC-000-namespace-and-contract-shape-framework.md):

> **The engine records or consumes a *fact*; the counterparty owns the *verdict*.**

Unpacked:

- **The engine emits one-way.** It appends an immutable domain event and relays it
  over the outbox ([ADR-IC-004](../../product-management/integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md)).
  It does not call the downstream system, wait for it, or care what it does next.
- **Downstream interprets.** Each consumer reads the same fact and applies its own
  rules: the GL books a double-entry posting, the IFRS 9 engine stages an exposure,
  the comms system renders and sends a message. The interpretation — the *verdict*
  — is never the engine's.
- **The post-flag is never gated.** A downstream rejection (the GL refuses a
  posting, a notification bounces) **never unwinds** the engine's fact. The deposit
  was constituted; that is true regardless of whether the GL booked it. This is the
  fail-forward property that keeps the engine's lifecycle clean and replay-determinable.

That single shape, applied at five boundaries, is what this page is about. The
ADRs call this collection the **signal-contract family**.

---

## The five contracts

Four are **outbound** (the engine emits; downstream consumes) and one is
**inbound** (an upstream system supplies a verdict the engine *records and acts on
at the decider*). They all share the principle above; what differs is *who owns
which verdict*.

| # | Contract | Direction | The fact the engine states | The verdict the counterparty owns | ADR |
|---|---|---|---|---|---|
| 1 | **GL posting** | outbound | the raw business events (`DepositConstituted`, `InterestPaid`, …) | the double-entry posting + chart-of-accounts mapping | [ADR-PC-012](../../product-management/product_concepts/adrs/ADR-PC-012-gl-posting-signal-contract.md) |
| 2 | **IFRS 9** | outbound | raw operational facts (`ExposureArrearsUpdated`, `LoanRestructured`, `LoanWrittenOff`) | SICR, the default definition, ECL staging | [ADR-PC-015](../../product-management/product_concepts/adrs/ADR-PC-015-ifrs9-signal-contract.md) |
| 3 | **Customer notification** | outbound | a `NotificationDue` event (template-ref + structural data, **no PII on the bus**) | rendering, delivery, and sent/acked/bounced state | [ADR-PC-025](../../product-management/product_concepts/adrs/ADR-PC-025-customer-notification-emit-contract.md) |
| 4 | **Temporal signals** | (none — by design) | *nothing clock-driven*; the engine declares no `DepositMaturityApproaching` | "about to happen" triggers, read downstream from projections | [ADR-PC-023](../../product-management/product_concepts/adrs/ADR-PC-023-temporal-signals-projection-derived.md) |
| 5 | **Constitution precondition** | inbound | the engine *declares* which preconditions a product requires, and *refuses* without them | the upstream system **evaluates** each precondition (new-money, new-client, …) | [ADR-PC-024](../../product-management/product_concepts/adrs/ADR-PC-024-constitution-precondition-contract.md) |

A few of these deserve a sentence of their own, because they show the principle
holding even at its edges:

- **Contract 4 is the principle taken to its logical end.** A signal is either
  *fact-driven* (caused by a domain event — stays in the engine) or *clock-driven*
  (caused only by a date arriving). The engine refuses to manufacture the latter:
  there is **no internal scheduler** and no clock-driven family event. "Maturity is
  approaching" is a **read over a projection** (a maturity calendar), owned
  downstream — because the alternative would make a fold impure and break
  replay-determinism. The cleanest one-way signal is the one the engine declines to
  invent.
- **Contract 5 is inbound, but still verdict-owned-elsewhere.** The engine declares
  the rule-set (`required_preconditions` in the product config) and the family
  decider *refuses* a constitution whose required verdict is absent or false — a
  pure function of the command. But the engine never **evaluates** the precondition
  (it cannot see the bank's transaction history to decide "new money"). The
  constitution saga resolves each verdict from its owning upstream system and passes
  it in. Same line: the engine records and acts on a fact; it does not own the
  judgement that produced it.

---

## Why the same shape five times is the point

It would be easy to model each of these as a bespoke integration. The
signal-contract family deliberately does not, and the payoff is concrete:

- **The engine stays small and in-scope.** Booking double-entry, staging exposures,
  rendering messages, deciding eligibility — every one of those is a domain the
  [product vision](../../product-management/product_concepts/00-product-vision.md)
  explicitly puts *out of scope*. The signal-contract shape is what keeps them out:
  the engine emits a fact and stops.
- **One mental model for every consumer.** As an integrator, once you understand one
  of these boundaries you understand all five. The wire mechanics (Avro on the bus,
  outbox at-least-once, idempotency on a stable id) are shared; only the *meaning*
  of the fact changes.
- **No PII on the durable bus, uniformly.** Every outbound signal carries structural
  data and references, never cleartext personal data — the comms contract resolves
  PII by reference against an engine-internal surface, and a crypto-shredded subject
  resolves to null
  ([ADR-PC-004](../../product-management/product_concepts/adrs/ADR-PC-004-pii-crypto-shredding.md)).
  That is one rule enforced once, not five times.

---

## How an integrator consumes one of these

The wire-level mechanics are the engine's general event-emission machinery, not a
per-contract invention:

1. **Subscribe to the family topic** for the events you care about. The governed
   event catalogue is the public API ([Document 08](../../product-management/integration_concepts/08-event-catalog-governance.md));
   the per-event payloads are in the
   [generated event reference](../reference/events/README.md).
2. **Dedupe on the event's idempotency key** — the relay is at-least-once
   ([ADR-IC-004](../../product-management/integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md)),
   so you will occasionally see a fact twice. Each contract names its stable key
   (e.g. `event_id` for GL, `notification_id` for notifications).
3. **Apply your own verdict** and **never expect the engine to react to it.** If your
   booking fails, that is your problem to retry or reconcile — it does not, and must
   not, unwind the engine's fact.
4. **Be defensive about enums and new fields.** Schemas evolve additively; an
   unknown enum value or a new optional field will arrive eventually. The companion
   explanation,
   [schema evolution in event-driven systems](./schema-evolution-in-event-driven-systems.md),
   is the discipline that keeps your consumer working across those changes.

---

## Related

- The shared principle and contract-shape template:
  [ADR-PC-000](../../product-management/product_concepts/adrs/ADR-PC-000-namespace-and-contract-shape-framework.md).
- The five contracts themselves:
  [GL posting](../../product-management/product_concepts/adrs/ADR-PC-012-gl-posting-signal-contract.md),
  [IFRS 9](../../product-management/product_concepts/adrs/ADR-PC-015-ifrs9-signal-contract.md),
  [customer notification](../../product-management/product_concepts/adrs/ADR-PC-025-customer-notification-emit-contract.md),
  [temporal signals](../../product-management/product_concepts/adrs/ADR-PC-023-temporal-signals-projection-derived.md),
  [constitution precondition](../../product-management/product_concepts/adrs/ADR-PC-024-constitution-precondition-contract.md).
- The emission substrate (outbox, at-least-once):
  [ADR-IC-004](../../product-management/integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md)
  and the wire contract
  [ADR-IC-002](../../product-management/integration_concepts/adrs/ADR-IC-002-schema-format-and-registry.md).
- Keeping a consumer working as the schema grows:
  [schema evolution in event-driven systems](./schema-evolution-in-event-driven-systems.md).
- The generated, field-level truth for the events:
  [event reference](../reference/events/README.md).
- Back to the [product-docs front door](../README.md).
