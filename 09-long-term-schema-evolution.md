# Banking Ecosystem — Integration Architecture
## Document 09: Long-term Schema Evolution

We already touched on schema versioning in [Document 04 (Plumbing)](./04-plumbing-patterns.md) and [Document 08 (Governance)](./08-event-catalog-governance.md). Here we go deep into the specific discipline that makes the difference between an event-driven system that ages well over 5+ years and one that becomes paralysed by changes impossible to make.

We start with why this is especially hard in events, then move to concrete techniques, antipatterns, and real-world scenarios of changes you will inevitably face.

---

## Why Schema Evolution in Events Is Especially Hard

In REST APIs, evolution is manageable because you have visibility of who calls: server logs show you the active consumers, and you can coordinate changes.

In events, three factors worsen the equation:

**1. Consumers may be unknown.** Public events are consumed by any subscriber. In large organizations, consumers may appear that weren't in the original design.

**2. Events persist.** An event published today can be re-projected 2 years from now when someone builds a new read model from history. Today's schema **must be readable forever**, or at least throughout the retention/archive window.

**3. Consumers are asynchronous.** In a REST API, a change is coordinated client-by-client. In events, you may have dozens of consumers on different versions, all running in parallel, all valid.

The combination of these three means that **schema decisions have a multi-year time horizon**, not months. And rolling back is hard or impossible.

---

## The Mental Model: Schemas Have Three Distinct Audiences

To think clearly about evolution, it's useful to separate three audiences of a schema:

| Audience | When they read | Tolerance to changes |
|---|---|---|
| **Current producers** | Today, when publishing | None — you control |
| **Current consumers** | Today, when consuming | Low — you coordinate |
| **Future consumers** | Months/years from now, or on replay | Zero — you don't know who they are |

The third audience is the most underestimated. Every "small" schema decision today gets recorded in the persisted events, and all future consumers will have to know how to interpret it.

**Practical implication**: schemas should be thought of as **archaeological data**. Five years from now, someone will need to interpret this event without the context you have today. What clues do you leave so that it remains possible?

---

## The Taxonomy of Changes

Not all changes are equal. Categorizing allows you to respond proportionally.

### Category 1: Additive Compatible (90% of real changes)

- Adding an optional field with default
- Adding a value to an enum (with care — see below)
- Adding a new event

These are **free**. Old consumers ignore the new field; new consumers use it. Schema registry validates automatically. No coordination needed.

### Category 2: Subtractive Compatible (some)

- Removing an optional field that no consumer uses
- Marking a field as deprecated while keeping it in the payload

Technically compatible, but require **verification of consumers**. Before removing, ensure nobody depends on it. Tools: search in consumer code, usage metrics, contract tests (Pact shows you who uses what).

### Category 3: Modifying Incompatible (rare but inevitable)

- Changing field type (string → integer, integer → decimal)
- Renaming a field
- Changing semantics of an existing field (e.g., a field `amount` that goes from cents to currency units)
- Making an optional field mandatory, or vice versa
- Removing or renaming an enum value

**These are where the pain lives.** They can't be done in-place. They require specific strategies that we'll see next.

### Category 4: Structural (devastating if mishandled)

- Splitting an event into several (`DepositChanged` → `DepositActivated` + `DepositSuspended` + `DepositReactivated`)
- Merging multiple events into one
- Changing granularity (events per movement vs events per balance)

These are essentially **domain model redesign**. Treatment ahead.

---

## Strategies for Incompatible Changes

Four techniques, in increasing order of complexity. You choose the lightest one that solves your case.

### Strategy 1: Add New Field, Deprecate Old, Keep Both

For subtle changes (changing type, renaming, changing semantics).

```
v1 of the schema:
  amount: integer (in cents)

v1.1 (transition):
  amount: integer (DEPRECATED, in cents)  
  amount_decimal: decimal (NEW, in currency units)
  # producer fills both
  # consumers can read either

v2 (after sunset of the old one, 6+ months later):
  amount_decimal: decimal
  # old field removed
```

Characteristics:
- **Cost during transition**: producer writes both, payloads are larger
- **Window**: typically 6-12 months
- **When to conclude**: when usage metrics show nobody reads `amount` (the old field)

This is the standard strategy for small incompatible changes. It resolves 80% of cases where "Category 3" appears.

### Strategy 2: New Event in Parallel

For changes where the event changes significantly.

```
2026: 
  - publish DepositConstituted (v1)

2027 (introduction of the new):
  - publish DepositConstituted (v1) — continues, deprecated
  - publish DepositConstitutedV2 — new, with different schema
  - producer emits BOTH for every constitution
  - new consumers subscribe to V2
  - old consumers continue on v1

2028 (sunset):
  - producer stops emitting v1
  - v1 topic stays during retention for replay
  - old event eventually disappears
```

Costs:
- **Duplication of published events** during transition window
- **Consumers that still process both** may need to deduplicate logically

Key characteristic: **the major version change in the event name is explicit**. There is no ambiguity — different schemas, literally different event.

**When to use this strategy instead of 1**: when the change involves multiple related fields, or a partial redesign. Adding 5 new fields + deprecating 3 old ones is too much to manage in a single schema; better a clean new event.

### Strategy 3: Upcasting / Transformation on Read

For cases where you **cannot** alter the schema of old events (already published, in retention/archive), but you need to consume them with the new model.

Concept: an **upcasting layer** transforms old events into new events at read time.

```
Event store contains:
  - 2024 events with schema v1
  - 2025 events with schema v1.5
  - 2026 events with schema v2

When a projector reads the stream:
  v1 event → upcast(v1 → v2) → process(v2)
  v1.5 event → upcast(v1.5 → v2) → process(v2)
  v2 event → process(v2) directly

The projector code only handles v2.
```

Especially useful for:
- **Replay of read models** years later (you need to re-project old events with new logic)
- **Strict event sourcing** (where old events are an immutable source of truth)

Costs:
- Upcasters accumulate: after 5 years you may have chains `v1 → v2 → v3 → v4`
- Each upcaster is code that needs tests
- Refactoring forces re-thinking all upcasters

**For your system**: you probably won't use pure event sourcing, but the technique is useful for the specific case of re-projecting old read models with new logic.

### Strategy 4: Big-Bang With Freeze + Replay

For structural changes (Category 4) where none of the previous strategies cover.

```
Phase 1 — preparation:
  - new schema/model designed and validated
  - infrastructure ready to emit the new model
  - consumers prepared to subscribe to the new model

Phase 2 — freeze:
  - producer stops emitting the old model
  - short window (hours, ideally overnight)

Phase 3 — replay:
  - tooling re-processes historical events from the old model
  - transforms to the new model
  - publishes to the new topic

Phase 4 — switch:
  - producer starts emitting the new model
  - consumers fully migrate to the new
  - old is archived
```

**Rarely justified.** Has high operational risk, requires intensive coordination, and there is usually an incremental path. But in extreme cases (regulatory change, fundamental redesign of the business model), it's the only way out.

---

## The Special Case of Enums

Enums deserve their own section because they are the most common source of changes that look innocent but break consumers.

**Common scenario**: you have a field `interest_modality` with values `AT_MATURITY`, `MONTHLY`, `QUARTERLY`, `ANNUAL`. The product adds "semi-annual" and you want `SEMIANNUAL`.

Looks compatible, right? **It isn't.**

Reason: consumers that do `switch` or `pattern matching` on the enum **will fail** when receiving an unknown value. Typical behaviour:

```
when modality:
  AT_MATURITY → calculate_at_maturity()
  MONTHLY → calculate_monthly()
  QUARTERLY → calculate_quarterly()
  ANNUAL → calculate_annual()
  // SEMIANNUAL arrives → throws UnknownValueException
```

The result depends on the consumer: it may crash, may silently ignore, may write garbage to the read model. All behaviours are bad.

### Strategies for Healthy Enum Management

**1. Consumers should be defensive by design.**

Whenever you process an enum coming from an event, you should have an explicit default case:

```
when modality:
  AT_MATURITY → calculate_at_maturity()
  MONTHLY → calculate_monthly()
  QUARTERLY → calculate_quarterly()
  ANNUAL → calculate_annual()
  else → 
    log.warn("Unknown modality: {modality}, skipping")
    metrics.increment("unknown_enum_value", tags={field: "modality"})
    skip_or_park_event()
```

This turns the problem from "consumer crashes" into "consumer alerts and continues". Metrics tell you when new values appear; you can adapt the consumer.

**2. Adding enum values is a change that requires communication.**

Even if "compatible" in the schema, it should be announced to consumers via the catalogue. Window of at least one sprint for consumers to update their `switch`.

**3. Removing enum values is never compatible.**

If any historical event uses the value, removing it from the schema breaks the reading of those events. Always deprecate, never remove.

**4. Consider "open enums" with free-form field.**

In cases of volatile enums (product categories, cancellation reasons), it can make sense to model as an open string instead of a strict enum. Trade-off: less type safety, more flexibility.

---

## Compatibility Isn't Binary — the Four Modes of the Schema Registry

The schema registry (Confluent, Apicurio, etc.) offers four compatibility modes. Choosing the right one per event is an architectural decision.

### BACKWARD Compatibility (default and recommended for most cases)

New schema can read data produced with the old schema. Type: producer evolves first, consumers after.

Allows:
- Adding optional fields with default
- Removing optional fields
- Removing mandatory fields as long as the producer stops writing them

Does not allow:
- Adding mandatory fields without default
- Changing field type

### FORWARD Compatibility

Old schema can read data produced with the new schema. Type: consumer evolves first, producer after.

Rare use cases — when you need to ensure old consumers continue to work even if the producer already emits a new version.

### FULL Compatibility (BACKWARD + FORWARD)

Only allows changes that both support — essentially, optional fields. Very restrictive, but maximum safety.

**Recommendation**: start in **BACKWARD**. Migrate to FULL for critical events where you want maximum guarantee. Forward is rarely useful in isolation.

### NONE

Anything allowed. Translation: "no governance, even if cosmetically registered". Used only in development environments, never in production.

---

## Real Scenario 1: The Regulatory Change

Banco de Portugal announces a change in the withholding tax calculation formula on interest, with an application date 6 months out.

Affected events: `InterestPaid` contains fields `gross_interest`, `withholding_tax`, `net_interest`. The semantics of `withholding_tax` changes — new formula, different values.

**Analysis**: technical schema does not change. Field types are the same. **But semantically the event is different** from a specific date onwards.

### Possible Strategies

**Option A**: Keep the same event, depend on the date.

```
Consumers interpret withholding_tax based on event.timestamp:
  if timestamp < 2026-09-01 → old formula
  else → new formula
```

Works, but spreads regulatory logic across all consumers. Every consumer has to know the cutoff date. Fragile.

**Option B**: Add a tax-regime field.

```
v1.1 of the schema:
  gross_interest: integer
  withholding_tax: integer
  net_interest: integer
  tax_regime: string  // "PRE_2026_REFORM" | "POST_2026_REFORM"
```

Every event is self-describing about which regime applies. Consumers can evolve independently. History stays interpretable.

**Recommended**: option B. **The broader rule is "events should be self-describing"** — they should not depend on external context (dates, configurations) to be correctly interpreted.

---

## Real Scenario 2: The Granularity Migration

After 18 months in production, you discover that the `DepositConstituted` event is too coarse — consumers need to know separately when the capital was debited and when the compliance was registered. Currently both happen inside the saga and only one event emerges.

**Analysis**: structural change (Category 4). Cannot be resolved with an additional field.

### Recommended Strategy: New Family of Fine Events, Keeping the Coarse

```
From the next version:
  - DepositConstituted continues to be emitted (compatibility)
  - NEW: CapitalDebitedForDeposit + ComplianceRegisteredForDeposit
  - The three events reference the same deposit_id
  - Consumers choose granularity

After 12 months, with consumer migration:
  - Evaluate whether DepositConstituted can be deprecated
  - Probably stays indefinitely (high-level consumers still need it)
```

**Important principle**: different granularities can coexist. Integration events are not exclusive — you can have "deposit constituted" as an aggregated fact **and** "capital debited" as a fine fact, both public, both valid.

---

## Real Scenario 3: The Bounded Context Split

After two years, the "Deposits" context grew too large and the team splits it into "Term Deposits" and "Savings Accounts". Current events like `DepositConstituted` refer to both concepts.

**Analysis**: organizational redesign. Events as they stand become ambiguous.

### Recommended Strategy: Introduce New Specific Events, Deprecate the Generic

```
Phase 1 (months 0-3):
  - new events: TermDepositConstituted, SavingsAccountOpened
  - producer emits the new events IN ADDITION to the old
  - old DepositConstituted continues with field "type" to distinguish

Phase 2 (months 3-12):
  - consumers migrate to specific events
  - metrics show who still consumes the old

Phase 3 (month 12+):
  - if nobody consumes the old, deprecate
  - if some consumers still use it, keep but with warning
```

Realistic total time: **12-18 months**. Structural changes aren't quick, even with good governance.

---

## Antipatterns in Evolution

**1. "Compatibility mode = NONE in production, we'll coordinate."**

No. Manual coordination in distributed ecosystems is like trying to manage Java libraries without semver. Works until it fails catastrophically.

**2. "Consumers should adapt."**

This attitude shifts the cost onto all consumers, often at unplanned moments, often in production. In banking, this is toxic — regulatory consumers can't "adapt" without formal change cycles.

**3. "Let's remove the field and see who complains."**

Learning by incident. In event-driven systems with unknown consumers, this can silently break regulatory reporting.

**4. "We add a version in the name of every change."**

`DepositConstitutedV2`, `DepositConstitutedV3`, `DepositConstitutedV4`... Result: proliferation of nearly identical events, consumers confused about which to subscribe to. Major versioning in the event name should be **rare** (truly incompatible), not default.

**5. "We don't document the change because it's compatible."**

Even compatible changes should enter the catalogue log. Three years from now, someone will need to know when `interest_modality` was added, and why pre-March-2026 events don't have the field.

**6. "Tracking who consumes what" as a manual exercise.**

Doesn't scale. It must be mechanical: pact broker, consumption dashboards from the schema registry, automated dependency tracking. Without this, deprecation becomes impossible.

---

## Essential Supporting Tooling

For evolution to work, certain tools are prerequisites:

**1. Schema registry with enforcement.** It's not "check if you want to" — it's a CI/CD gate. Incompatible schemas fail the producer build.

**2. Pact broker (or equivalent).** Tells you who consumes what, with what expectations, in what version. Essential for deprecation.

**3. Consumption metrics per event and per version.** How many consumers still process `DepositConstituted v1.0`? Without this data, you cannot make informed sunset decisions.

**4. Catalogue with change history.** Each event shows its temporal evolution, not just the current state. Audit, debugging, and onboarding of new consumers depend on this.

**5. Schema registry health dashboards.** Attempted compatibility violations (that had to be converted into workarounds), schemas registered per team, age of schemas (one not touched in 3 years may be a deprecation candidate).

---

## [GDPR](https://eur-lex.europa.eu/legal-content/EN/TXT/?uri=CELEX:32016R0679) and the Immutability Tension

[Document 10](./10-security-and-threat-model.md) names this as boundary 8 — the GDPR data boundary. It deserves treatment here because it surfaces as a schema evolution question: what do you do when a client exercises their right to erasure, and their personal data is embedded in events you cannot change?

### The Tension

[Principle 3 of Document 10](./10-security-and-threat-model.md): events are immutable, published facts. [GDPR Article 17](https://gdpr-info.eu/art-17-gdpr/): clients have the right to erasure of their personal data. If `DepositConstituted` carries the client's IBAN in its payload, and that event persists in Kafka for 90 days and in the event archive indefinitely, erasure is structurally impossible without tampering with the event store.

### The Structural Resolution — Design Before the First Event

The resolution is not a clever technique applied retroactively. It is a schema design decision that must be made before the first event is published:

**Personal data does not belong in events. Events carry only the pseudonymous `client_id`.**

Name, NIF, contact details, and account numbers that identify the client live in a separate **Customer Data Store** — a service (or dedicated tables) keyed by `client_id`. When a downstream consumer needs the client's name to send a notification, it fetches it from the Customer Data Store using the `client_id` from the event. When a client exercises their right to erasure, the Customer Data Store record is deleted; the event log retains only the `client_id`, which without the corresponding record is no longer personal data under GDPR.

### The Hard Case: Account Numbers in Financial Events

The IBAN in `DepositConstituted` is a partial exception. It is financial account data rather than purely personal data, but it is typically considered personal data under GDPR because it identifies the account holder. The safe design: remove the IBAN from the integration event payload; consumers that need it (the Core ACL, for example) obtain it from the Customer Data Store.

If a consumer has a genuinely compelling need for the account number in the event payload — a timing or availability argument that cannot be resolved by lookup — that is a design decision to document explicitly in the RFC (see [Document 08](./08-event-catalog-governance.md)), with the [GDPR legal basis (Article 6)](https://gdpr-info.eu/art-6-gdpr/) stated, not assumed.

### Data Subject Access Requests

A client submitting a DSAR asks: "what data do you hold about me?" The answer spans multiple stores:

- Customer Data Store: name, NIF, contact details, account relationships
- Read model tables: deposit history, interest payments, saga outcomes — all keyed by `client_id`
- Outbox / inbox tables: short-retention, operational; typically not in scope for DSAR
- Traces and logs: if pseudonymized correctly, the `client_id` reference does not constitute personal data requiring DSAR disclosure; if account numbers appear in traces, it does

The pseudonymization design simplifies DSAR substantially. Without it, DSAR reconstruction requires querying Kafka archives, read models, ACL state stores, inbox tables, and the tracing backend.

### Retention Policies Require a Legal Basis

Kafka's 90-day retention and the indefinite event archive must have documented legal bases. In banking, AML obligations and BdP supervisory requirements typically provide that basis for financial operation records (`DepositConstituted`, `DepositMobilized`, `InterestPaid`). Marketing or operational events (`NotificationSent`) may not share the same basis and should have shorter, separately documented retention policies.

Retention is not a schema decision, but it is documented in the event catalogue alongside the schema — and it must be consistent with the [GDPR legal basis (Article 6)](https://gdpr-info.eu/art-6-gdpr/) for each event type.

---

## Principles That Tie Everything Together

Five principles that, if consistently followed, resolve 90% of evolution problems. The most important of the five is the first — it reframes schema discipline from a technical concern into an organizational and economic one:

**1. Compatibility is economic, not technical.** Compatibility exists to reduce coordination cost. Every incompatible change is a cross-team meeting, a migration plan, a risk window. **Optimizing for compatibility is optimizing for organizational velocity**.

**2. Optimize for the unknown future consumer.** The hardest consumer to serve is the one that doesn't exist yet. Every schema decision should pass through the filter: *"if someone reads this event 3 years from now without context, will they correctly interpret it?"*

**3. Events are immutable. Schemas evolve.** A published event is a recorded fact. It's not "corrected"; it's complemented with new facts. The schema may grow; the individual event never changes.

**4. Deprecation is harder than addition.** Adding fields is trivial. Removing (even deprecated) requires evidence of non-use, time, and coordination. So, avoid adding what you'll probably want to remove. *Think twice before adding; think ten times before removing.*

**5. Self-describing wins.** Events that depend on external context (cutoff dates, configurations, implicit knowledge) become unintelligible over time. Events that carry their own interpretive context (tax regime, product version, etc.) age well.

---

## Where to Start — Pragmatic Recommendation

In greenfield, before the first event:

1. **Compatibility mode = BACKWARD** as default in the schema registry.
2. **Conventions on nullability**: prefer optional fields by default, mandatory requires justification.
3. **Versioning in the envelope**: `event_version: 1` in every event from the beginning.
4. **Date/time in ISO-8601 UTC**, currencies in ISO-4217, monetary values in integer cents — defined and enforced from day 1.
5. **CI compatibility gate** active from the first event.
6. **Catalogue with history** from the first event — "Historical changes" field even if empty.

Initial investment: ~1 day of configuration. Return: capacity to evolve the system for years without chronic pain.

---

## Closing the Series

This is the final document of the series. The architecture is coherent across all dimensions: conceptual foundation, boundaries, read model, plumbing, materialization in concrete sagas, operational concerns (observability, testing), and organizational concerns (governance, evolution).

Each piece serves an identified purpose, each trade-off is explicit, and the whole rests on the foundational principles established at the outset: maximum flexibility, performance honouring the sub-500ms requirement, and compensation mechanisms instead of distributed transactionality. The term deposit walkthrough demonstrates these principles concretely — but the architecture is the underlying pattern, applicable across the full range of applications that share the same banking ecosystem.
