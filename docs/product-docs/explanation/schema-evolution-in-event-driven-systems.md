# Schema evolution in event-driven systems

A REST API can be changed by coordinating with the handful of clients you can see
in your server logs. Events are different: an event published today might be read
years from now by a consumer that did not exist when you wrote the schema. That
makes "change the shape of an event" a multi-year commitment, not a sprint task.
This page explains the discipline that keeps the engine's events readable forever —
**add, never rename; default everything new; treat removal as a last resort** — and
why that discipline is what lets you, the integrator, build a consumer that does not
break when the bank evolves its events.

This is an **understanding** page (the Diátaxis explanation quadrant) for the
**integrator / solution-architect** consuming the engine's events. It is a *recipe
companion* to the deep normative treatment in
[Document 09](../../product-management/integration_concepts/09-long-term-schema-evolution.md) —
that document is the rationale and the full taxonomy; this page distills the rules
you act on and links back for the depth.

> ## ⚠ Provisional page — built vs pending
>
> The **rules** below are normative and apply from the first event. What is **built
> today** is a single-family event surface (`term_deposit`) plus a second family
> landing (`personal_loan`); the **schema-registry compatibility gate, Pact broker,
> and per-version consumption metrics** that Document 09 names as prerequisites for
> *deprecation* are design commitments, not all wired in this repo yet. So you can
> rely on the additive discipline now; the tooling that lets the bank *retire* an
> old field safely is partly pending. Build your consumer defensively regardless.

---

## Why this is hard in events specifically

Three facts make event-schema change harder than REST-API change
([Document 09 §Why](../../product-management/integration_concepts/09-long-term-schema-evolution.md)):

1. **Consumers may be unknown.** A public event is consumed by *any* subscriber. You
   cannot enumerate who depends on a field.
2. **Events persist.** An event written today may be re-projected in two years to
   build a new read model. Today's schema must stay readable for the whole
   retention/archive window.
3. **Consumers are asynchronous and on different versions.** Dozens of consumers may
   run in parallel, all valid, all on different schema versions.

The practical consequence: think of a schema as **archaeological data**. Five years
on, someone will interpret this event without the context you have now. Every "small"
field decision is recorded permanently.

---

## The rules, in priority order

These are the load-bearing rules. Follow them and ~90% of evolution pain never
materialises (Document 09 §Principles).

### 1. Add; do not rename or retype

Adding an **optional field with a default** is free: old consumers ignore it, new
consumers use it, the registry validates it automatically. Renaming a field,
changing its type, or changing its *meaning* (e.g. a `withholding_tax` field whose
formula changed) is **incompatible** — it cannot be done in place. When you must, you
add the new field alongside the old and deprecate the old over a 6–12 month window;
you never edit the old one
([Document 09 §Strategy 1](../../product-management/integration_concepts/09-long-term-schema-evolution.md)).

### 2. Events are immutable; schemas evolve

A published event is a recorded fact. It is never "corrected" — it is *complemented*
by a new fact. The schema may grow; an individual event never changes after it is
written. This is the same forward-only property the engine relies on internally (an
illegal state is never patched; a correcting event is appended).

### 3. Self-describing wins

An event that depends on external context — a cutoff date, a config value, implicit
knowledge — becomes unintelligible over time. When a regulation changes the meaning
of a field, do **not** ask consumers to branch on `event.timestamp`; add a
`tax_regime` field so each event carries its own interpretation
([Document 09 §Real Scenario 1](../../product-management/integration_concepts/09-long-term-schema-evolution.md)).
This is exactly why the engine's events carry computed facts and explicit context,
not raw inputs a consumer must re-derive.

### 4. Deprecation is harder than addition

Adding a field is trivial; *removing* one — even a deprecated one — needs evidence of
non-use, time, and coordination. *Think twice before adding; think ten times before
removing.* Removing an enum value is **never** compatible if any historical event
used it.

### 5. Even compatible changes are documented

A backward-compatible field addition still goes in the catalogue's change log. Three
years from now someone needs to know *when* a field appeared and why earlier events
lack it ([Document 08](../../product-management/integration_concepts/08-event-catalog-governance.md)).

---

## The enum trap (the one that bites quietly)

Adding a value to an enum **looks** compatible and **is not**, in practice. A consumer
that does a `switch`/pattern-match on `interest_variant` and has no default case will
crash, silently skip, or write garbage the moment a new value (`SEMIANNUAL`, say)
arrives ([Document 09 §Enums](../../product-management/integration_concepts/09-long-term-schema-evolution.md)).

The defence is **on the consumer**, and it is the single most important habit for an
integrator on this bus:

```
match variant:
  AT_MATURITY -> ...
  PERIODIC    -> ...
  ADVANCE     -> ...
  _           -> log.warn("unknown interest_variant"); metric("unknown_enum"); park()
```

An explicit default turns "consumer crashes in production" into "consumer alerts and
keeps going" — and the metric tells the bank a new value appeared. (For reference,
`interest_variant` today is exactly `AT_MATURITY` / `PERIODIC` / `ADVANCE` — see
[`constitute_deposit`](../reference/mcp-tools/constitute_deposit.md) — but you should
code as if a fourth value will appear, because one day it will.)

---

## What the registry guarantees (and does not)

The schema registry enforces **BACKWARD** compatibility by default
([Document 09 §Compatibility Modes](../../product-management/integration_concepts/09-long-term-schema-evolution.md),
[ADR-IC-002](../../product-management/integration_concepts/adrs/ADR-IC-002-schema-format-and-registry.md)):
a new schema can read data produced by the old one. That mechanically allows adding
optional-with-default fields and removing optional fields; it mechanically blocks
adding a mandatory field with no default, or changing a field's type. The producer
build fails on an incompatible change — it is a CI gate, not a courtesy check.

What the registry does **not** do: catch a *semantic* change (a field whose type is
unchanged but whose meaning shifted), or tell you who still consumes an old version.
Those need self-describing events (rule 3) and consumption metrics — which is why
deprecation is a coordination problem, not a tooling toggle.

---

## What this means for your consumer

Concretely, to build a consumer that ages well on this bus:

- **Default every enum branch.** (The enum trap, above.) This is non-negotiable.
- **Ignore unknown fields.** Do not fail on a field you do not recognise — it is a
  newer producer being additive.
- **Dedupe on the event's idempotency key.** The relay is at-least-once
  ([ADR-IC-004](../../product-management/integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md)) —
  you will see some facts twice.
- **Do not embed cutoff dates or regime logic.** If interpretation depends on
  context, that context should be *in the event* (rule 3). If it is not, raise it via
  the catalogue RFC rather than hard-coding a date.
- **Watch the catalogue change log**, not just the current schema, so you see a field
  arriving before it surprises you in production.

The five outbound signals an integrator consumes all ride this same evolution
discipline — see
[the five boundary signal contracts](./the-five-boundary-signal-contracts.md) for the
shapes those events take.

---

## Related

- The full rationale, taxonomy, strategies, and real-world scenarios:
  [Document 09 — Long-term schema evolution](../../product-management/integration_concepts/09-long-term-schema-evolution.md).
- The wire format and registry that enforce compatibility:
  [ADR-IC-002](../../product-management/integration_concepts/adrs/ADR-IC-002-schema-format-and-registry.md).
- Why even compatible changes are governed:
  [Document 08 — Event catalogue governance](../../product-management/integration_concepts/08-event-catalog-governance.md).
- The one-way signals you are consuming:
  [the five boundary signal contracts](./the-five-boundary-signal-contracts.md).
- The at-least-once delivery you must dedupe against:
  [ADR-IC-004](../../product-management/integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md).
- Back to the [product-docs front door](../README.md).
