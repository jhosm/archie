# How to structure event payloads

This guide is the rulebook for **what may ride on a family's event record** — the
`record <Entity><PastParticipleVerb> : DomainEvent` you define in your family's
`Events.cs`. An event is the durable, replayable fact the whole system folds and
the bus carries, so getting its payload right is the difference between a family
that replays deterministically and ships no customer identity onto the wire — and
one that does not.

You will learn the three hard rules (no PII, computed facts only, `Money` as
cents), what goes on the envelope instead of the payload, and how to check your
event against them. The worked example throughout is the `term_deposit` family's
[`Events.cs`](../../../families/term-deposit/src/Babelstone.Families.TermDeposit/Events.cs).

**Before you start, know this:** an event is *forever*. It is appended to an
immutable log, replayed on every rebuild, and (for promoted events) published to
downstream consumers. You cannot quietly change its shape later — schema
evolution is forward-only. So the discipline below is not style; it is the cost
of a fact you can never take back.

---

## The three rules, stated once

Every field you put on an event must pass all three:

1. **No PII — structural facts only.** No name, NIF, IBAN, address — cleartext
   *or* ciphertext. Carry an **opaque reference** the engine resolves internally
   instead.
2. **Computed facts only.** The event records what *already happened*, with the
   numbers already worked out. The financial-math kernel runs on the
   command/decider side that *builds* the event — never inside a fold.
3. **All money is `Money` (integer cents).** No `decimal`, no floats, no
   currency-as-string-amount. `Money` is the only monetary type.

The rest of this page is each rule, why it bites, and how to satisfy it.

---

## Rule 1 — No PII on the event

An event is structural. Open any event in
[`Events.cs`](../../../families/term-deposit/src/Babelstone.Families.TermDeposit/Events.cs)
and you will find a deposit id, a principal, a rate, dates, a variant string —
and **never** a depositor's name or tax number. Where the family genuinely needs
to point at a person or an account, it carries an **opaque token**, not the
identity:

```csharp
// FundingAccount is an OPAQUE funding-account TOKEN — a reference the engine
// resolves internally, NEVER an IBAN / cleartext account identifier.
string FundingAccount,
```

The reason is twofold and absolute. First, events land on the durable
integration bus, and **identity must never travel the bus** — cleartext or
ciphertext. A reference is allowed; the thing it refers to is resolved inside the
engine, behind the OpenBao seam. Second, GDPR Article 17 erasure works by
*crypto-shredding* the subject's key
([ADR-PC-004 §P3](../../product-management/product_concepts/adrs/ADR-PC-004-pii-crypto-shredding.md)):
the PII lives encrypted under a per-subject key, and erasure destroys the key.
If PII were copied onto an event in the append-only log, it could never be
erased — the log is immutable. Keeping personal data off the event entirely is
what *makes erasure possible*.

**How to satisfy it:** wherever you reach for a personal or account identifier,
ask "can I carry an opaque reference and resolve it internally?" The answer is
yes, and that reference is what goes on the event. A pseudonym used for routing
(e.g. a subject hash) must be a salted one-way hash, never the raw id. The rule
is [ADR-PC-004 §P2](../../product-management/product_concepts/adrs/ADR-PC-004-pii-crypto-shredding.md).

---

## Rule 2 — Computed facts only; the math ran on the command side

An event carries the *result*, not the inputs to re-derive it. When a deposit
accrues, the `InterestAccrued` event carries the already-computed
`GrossInterest` as a `Money` — it does **not** carry the rate and day-count for
the fold to multiply out. The accrual math ran in the decider that built the
event; the fold only records the figure.

This is forced by the purity rule on folds: a fold that recomputed interest would
need the day-count math, and the engine's
[fold-purity analysers](./write-and-test-event-handlers.md) (`BENG001/002/003`)
forbid the impure machinery anyway. But the deeper reason is **determinism**: if
an event carried inputs and the fold recomputed, a later change to the math
library would silently change historical replay. By carrying the *computed
result* as a flat fact, the event pins what actually happened, immune to later
code changes. The financial-math reviewer and the determinism gate both lean on
this.

**How to satisfy it:** put the *outcome* on the event as plain fields. If you
find yourself wanting to put a formula's *inputs* on an event so the fold can
compute, stop — compute it on the command side and carry the answer. (Conserved
quantities should also be conserved *on the event*: `term_deposit`'s payout-style
events carry `Net = Gross − Withholding` already reconciled to the cent.)

---

## Rule 3 — All money is `Money`, in integer cents

Every monetary field on an event is the `Money` type
([`engine/src/Babelstone.FinancialTypes/Money.cs`](../../../engine/src/Babelstone.FinancialTypes/Money.cs)),
which is integer cents under the hood. No `decimal`, no `double`, no
`amount: 100.00`. The folded state record is the same — a `decimal` money field
fails the `BMNY002` analyser.

```csharp
// from DepositConstituted — principal is Money, the rate is integer basis points
Money Principal,
int  TanBasisPoints,   // a rate/share in basis points (1 bp = 0.01%), never a float
```

Rates and shares follow the same integer discipline: a TAN or a penalty share is
**basis points** (`int`), not a floating-point percentage. The reason is exact
arithmetic: cents and basis points are integers, so there is no representation
error and no mid-step rounding to argue about — sums are exact and replay is
byte-identical. This is [ADR-PC-010 §P1](../../product-management/product_concepts/adrs/ADR-PC-010-dotnet-hand-rolled-engine.md).

**How to satisfy it:** type every amount as `Money` and every rate/share as
`int` basis points. If a value is naturally fractional money, it was rounded to
cents on the command side before it reached the event — the event only ever sees
the integer.

---

## What goes on the envelope, not the payload

A common first mistake is to stamp the governing context — the pack version, the
family-schema version, the family name — onto the event record. **Don't.** Those
**pins ride on the `EventEnvelope`**
([`engine/src/Babelstone.EventStore/EventEnvelope.cs`](../../../engine/src/Babelstone.EventStore/EventEnvelope.cs)),
supplied via the append context, not on your event fields. The reference family
says so in its own header:

> The pack/schema/family pins (`pt.2026.1` / `term_deposit@2026.1`) ride on the
> `EventEnvelope` via `AppendContext`, not on the event records.

Likewise, for events that get **published** to the bus, the CloudEvents envelope
travels in **Kafka headers**, not in the Avro payload — the generated
[events reference](../reference/events/README.md) restates this on every event
("the business payload only; the CloudEvents envelope rides in Kafka headers").
Your event record is the *business payload*: the domain facts, and nothing
infrastructural.

**How to satisfy it:** if a field is "which version / which family / which
correlation id / when-was-it-published," it belongs on the envelope or the
headers, not the record. The record carries only the domain fact.

---

## Naming: `<Entity><PastParticipleVerb>`

The event type itself is a naming contract: a **past fact**, PascalCase, of the
form `<Entity><PastParticipleVerb>` — `DepositConstituted`, `InterestAccrued`,
`DepositMatured`. Not `ConstituteDeposit` (that is a command), not
`DepositConstitution` (that is a noun). An event names something that *already
happened*. The convention is
[08-event-catalog-governance.md](../../product-management/integration_concepts/08-event-catalog-governance.md);
the per-event Avro `.avsc` and EventCatalog registration that go with each name
are the [`new-event` skill](../../../plugins/babelstone-engine/skills/new-event/SKILL.md)'s job.

---

## Check your event against the rules

There is no single "lint my payload" command, but each rule has a real backstop
you can lean on:

- **Money / decimal discipline** is enforced at build time: a `decimal` money
  field trips `BMNY002`, and the build is warnings-as-errors. Just build the
  pure project:
  ```sh
  mise exec -- dotnet build families/term-deposit/src/Babelstone.Families.TermDeposit/Babelstone.Families.TermDeposit.csproj --nologo -v q
  ```
- **No-PII** has no analyser — it is a review rule. The
  [`contract-reviewer`](../../../plugins/babelstone-engine/agents/contract-reviewer.md)
  subagent checks an event's shape for identity-on-the-bus before a PR, and the
  generated [events reference](../reference/events/README.md) is where every
  shipped event's payload is visible for an auditor to confirm.
- **Computed-facts / determinism** is caught downstream by the fold purity
  analysers (a fold that needs to recompute can't, because it can't be impure)
  and the fixture-replay determinism gate.

Read the rendered shape of the events that already exist, field for field, in
the generated [events reference](../reference/events/README.md) — it is rendered
from the governed Avro and gated for drift in CI, so it never goes stale the way
a copy here would.

---

## Honest limits

- **Most events are not published.** The no-PII rule applies to *every* event
  regardless, because every event is in the durable log and replayed — but the
  CloudEvents-in-headers and bus-promotion concerns only apply to the subset of
  events promoted to integration events. Whether an event is promoted is a
  separate decision ([ADR-IC-017](../../product-management/integration_concepts/adrs/ADR-IC-017-integration-event-promotion-criterion.md)),
  not something you set on the record.
- **The Avro `.avsc` is a separate artefact.** This page is about the C# record's
  fields. The governed Avro schema, EventCatalog entry, and BACKWARD
  registry-compatibility check that must accompany each event are the
  [`new-event` skill](../../../plugins/babelstone-engine/skills/new-event/SKILL.md)'s
  procedure — they must stay in lock-step with the record, but they are authored
  there, not here.

## Related

- [Write and test pure event handlers (folds)](./write-and-test-event-handlers.md)
  — the folds that consume the events you shape here, and the analyser gates.
- [The family lifecycle state machine](../explanation/the-family-lifecycle-state-machine.md)
  — how the events that carry a lifecycle label relate to the legality table.
- [Tutorial: author your first family schema](../tutorials/author-your-first-family-schema.md)
  — events in the context of a whole family.
- Normative sources: [ADR-PC-004](../../product-management/product_concepts/adrs/ADR-PC-004-pii-crypto-shredding.md)
  (no PII / crypto-shredding), [ADR-PC-010](../../product-management/product_concepts/adrs/ADR-PC-010-dotnet-hand-rolled-engine.md)
  (`Money` cents), [08-event-catalog-governance.md](../../product-management/integration_concepts/08-event-catalog-governance.md)
  (event naming).
- [Product-docs home](../README.md).
</content>
