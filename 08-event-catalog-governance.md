# Term Deposit System — Integration Architecture
## Document 08: Event Catalog Governance

Governance sounds like corporate slides and committees. In event-driven systems, it is the opposite: it is the mechanical discipline without which the architecture collapses in 18 months. We start by showing exactly how it collapses, because the urgency of the problem is only visible once you have seen it happen.

---

## What Happens Without Governance — the Predictable Scenario

Month 6 of the system in production. You have 12 integration events published, 6 consumer teams. Everything running well. The Deposits team adds a field to `DepositConstituted`. The Reporting team needs a new event and publishes `DepositCreated` (alternative name, individual choice). The Notifications team creates `DepositActivated` because "constituted" didn't sound right in literal Portuguese.

Month 12. You have 47 events. Three of them refer to the same thing with different names. Five are partial duplications ("same as X but with more fields"). Nobody knows which is canonical. New consumers don't know which one to subscribe to. Documentation lives in scattered wikis, some updated, others not. Changes require cross-team meetings to discover who owns what.

Month 18. The "event-driven" architecture has become a swamp of partially overlapping messages. Every new feature is archaeology before design. **You've lost the flexibility that was the initial point**, precisely because of the missing discipline that governance imposes.

This scenario isn't hypothetical. It is what happens **by default** in any event-driven ecosystem without explicit governance.

---

## The Mental Inversion: Events Are Public API, More Durable Than REST

The first thing to internalize is that **integration events are public API**. They are not "messages"; they are contracts with multiple external consumers.

And they are contracts with a peculiar property: **they are more durable than REST APIs**. Concrete reasons:

- REST APIs are called in real time. If you change the endpoint, the consumer fails immediately, you discover the problem, you fix it.
- Events persist in retention (Kafka, event stores). Events published 6 months ago can be **re-projected** when you build a new read model. The schema you used **6 months ago** still has to work.
- REST APIs are consumed by consumers you know. Events can be consumed by **consumers that will exist in the future**. The contract commits to people not yet in the conversation.

This inverts a common intuition. In REST APIs, mistakes are expensive but reversible. In events, mistakes can be **irreversible** within the retention window — the old events with the wrong schema are already in the log, you can't go back.

**Conclusion**: the care applied to designing an integration event should be **greater** than that applied to designing a REST API, not less. In most organizations it's the opposite, and that's why event-driven systems degrade.

---

## The Four Pillars of Event Governance

Robust governance rests on four pillars. Without any one of them, the others lose effect.

1. **Clear ownership** — each event has an identified, responsible owner
2. **Conventions and standards** — naming, structure, semantics, all disciplined
3. **Review process** — new events and changes go through an explicit gate
4. **Living catalogue** — central documentation, always up to date, discoverable

We cover each.

---

## Pillar 1: Ownership — Who Owns What

In event-driven systems, the question *"who owns this event?"* must have a unique answer in <30 seconds. If it takes longer, governance is broken.

**The rule: each integration event belongs to the bounded context that produces it.** Always. Without exception.

- `DepositConstituted`, `DepositCancelled`, `DepositMobilized` → Deposits team
- `ClientOnboarded`, `ClientKycUpdated` → Compliance team
- `AccountDebited`, `AccountCredited` → Core Banking team
- `NotificationSent`, `NotificationFailed` → Notifications team

The owner has three concrete responsibilities:

1. **Defines the schema** and its business meaning
2. **Guarantees backward compatibility** over time
3. **Coordinates deprecations** when needed

Notice what is **not** on the list: the owner **does not decide who consumes the event**. Public events are public — any context can subscribe. The owner has no veto over consumers, only obligations toward them.

### What Does NOT Work in Ownership

*"The event belongs to the main consumer."* No. Creates inverted dependency: the producer becomes hostage to the consumer to evolve its own model.

*"The event belongs to a central platform team."* No. Creates a bottleneck: every change passes through third parties who don't know the domain. Platform provides infrastructure (Kafka, registry, tooling), not events.

*"The event belongs to the architecture committee."* No. Committees don't have enough operational context to decide quickly on event semantics.

### Stewardship — One Layer Above Ownership

To avoid total fragmentation, a light structure on top helps: **event stewards** transverse to teams (1-3 people) who **are not owners** of events, but ensure global coherence. They review naming, validate conventions, maintain the catalogue. They don't block decisions; they guide.

In ecosystems with 6-10 bounded contexts, this is enough. In larger ones, formalize as a guild or architecture-on-call.

---

## Pillar 2: Conventions — Naming and Structure

Conventions look like aesthetic detail until you see what happens without them: 47 events with 47 naming styles, and time wasted just figuring out how to search.

### Naming Convention for Integration Events

Recommended structure: `<Entity><PastParticipleVerb>` or `<Entity><State>`.

Good examples:
- `DepositConstituted` ✓ (entity + past participle, factual)
- `DepositCancelled` ✓
- `InterestPaid` ✓
- `MaturityReached` ✓

Bad examples:
- `ConstituteDeposit` ✗ (looks like a command, not an event — Primitive 1 violated)
- `DepositEvent` ✗ (generic, doesn't say what happened)
- `NewDeposit` ✗ (ambiguous — created? draft? active?)
- `DepositStatusChange` ✗ (which change?)
- `dep_constituted` ✗ (doesn't match the rest of the catalogue)

**The rule is absolute**: the event name describes **a specific, identifiable past fact**. If you hesitate on "what exact moment does this describe?", the name is wrong.

### Versioning Convention

- Backward-compatible schema evolution: **same name, updated schema**, managed by the registry. No "v2" in the name.
- Incompatible schema evolution: **new event with explicit version** (`DepositConstitutedV2`), published in parallel with the previous one, with sunset plan.
- Never rename in-place. Never.

### Payload Structure Convention

All events share a **common envelope**:

```yaml
envelope:
  message_id: uuid
  event_type: DepositConstituted
  event_version: 1
  aggregate_id: DEP-2026-00012345
  aggregate_type: Deposit
  correlation_id: corr-aB7xK2pQ9
  causation_id: msg-008-h6i7j
  timestamp: 2026-05-15T14:32:17.687Z
  producer:
    bounded_context: deposits
    service_version: 1.4.2
payload:
  # event-specific fields here
```

Without a consistent envelope, you lose tracing, correlation, debugging, and the ability to mechanically filter events by type. **The envelope is non-negotiable; it is infrastructure.**

### Field Convention in the Payload

- `snake_case` for field names (or camelCase, **but pick one and enforce globally**)
- IDs are strings (even if they look numeric) — avoids problems with parsers that truncate large ints
- Dates in ISO-8601 UTC (`2026-05-15T14:32:17Z`), never Unix timestamps nor local formats
- Monetary values as **integers in cents**, never floats — `1000000` for €10,000, not `10000.00`
- Currencies in ISO-4217 (`EUR`, `USD`) — always explicit, even in a Portuguese bank with 99% in euros (because the remaining 1% exists and breaks assumptions)
- Boolean fields with positive names (`automatic_renewal: true`, not `no_automatic_renewal: false`)

Each of these choices looks micro. Together, they make the difference between a navigable catalogue and a swamp.

### Semantic Convention — Perhaps the Most Important

Events must be **factually true and complete about the moment they happened**.

- ✅ "X happened with these parameters on this date"
- ❌ "Please do Y" (that's a command)
- ❌ "I think maybe X" (events are not attempts)
- ❌ Partial events that require a subsequent call to "get the details"

A consumer processing an event must be able to do its job **with the event alone**, without calling back the producer for "more info". If it needs to, the event is incomplete.

This has implications for size: integration events tend to be **fatter** than you would intuitively expect. Accepting that is correct. Minimalist events that force consumers to re-call APIs are a governance antipattern.

---

## Pillar 3: Review Process — Explicit Gates

Adding a new event to the catalogue is a permanent architectural decision. Changing an existing one is potentially irreversible. These decisions cannot happen as reflexes in solitary PRs.

### The Proposal: a Lightweight RFC Process for Events

Before creating a new integration event, the proposer writes a short document (1-2 pages):

```
EVENT RFC: DepositMatured

Producer: deposits team
Proposed by: <name>, <date>

Business meaning:
  Emitted when a term deposit reaches its contractually defined maturity
  date, regardless of whether it auto-renews. Always emitted at the start
  of the maturity processing batch on the maturity date.

When emitted:
  - At deposit maturity, after capital settlement processing completes
  - Always, even if deposit auto-renews (renewal generates separate event)
  - Never retroactively

Payload schema:
  [link to schema definition]

Expected consumers (known at design time):
  - Notifications (notify client of maturity)
  - Reporting (BdP statistics on matured deposits)
  - CRM (relationship update)

Alternatives considered:
  - Adding "matured" flag to DepositStateChanged: rejected because state
    change is too generic; consumers care about maturity specifically.
  - Splitting into MaturityReached + CapitalSettled: rejected because the
    two always happen together within the batch.

Open questions:
  - Should we include accrued_interest_at_maturity in the payload? 
    Pro: avoids re-computation by consumers. Con: data duplication.

Review:
  - Event stewards: <names>
  - Affected teams: notifications, reporting, crm
```

The document circulates. Reviewers comment. There is discussion. Eventually approval (or rejection with reasoning).

**It's not a heavy process.** It's written, it's fast (1-2 days), but it's **explicit**. The difference from implicit decisions in PRs: the why is documented, and who decided is documented.

### Who Approves

- Event stewards (global coherence perspective)
- At least one representative of each bounded context that will consume
- Lead of the producing context (definitive responsibility)

### What the Process Validates

- The event is necessary (vs. an extension of an existing one)
- The name follows conventions
- The payload is complete and useful
- The semantics are clear
- The relationship with existing events is coherent
- Known consumers were consulted

### For Changes to Existing Events

- **Backward-compatible (adding optional field)**: normal PR review by the owner. No RFC. Catalogue notification.
- **Backward-incompatible (changing type, removing field)**: mandatory RFC. Approval from all known consumers.
- **Deprecation of the whole event**: mandatory RFC. Explicit migration plan.

**The principle**: the process effort is **proportional to the impact** of the change. Adding an optional field is trivial. Breaking contracts is nuclear.

---

## Deprecation Policy — Because Events Don't Die Easily

Deprecating an event is particularly hard in event-driven systems, because consumers may be unknown at the moment of the decision.

### Recommended Policy

1. **Explicit announcement**: event marked as `deprecated` in the catalogue, with a target removal date (minimum 6 months).
2. **Parallel events** during the window: you keep publishing the old **and** the new, in parallel. Consumers migrate at their own pace.
3. **Consumer monitoring**: metrics show who still consumes the deprecated event. Before removing, you ensure that nobody consumes.
4. **Active coordination**: stewards actively contact teams with still-active consumers. It's not "you posted on the wiki and wished good luck".
5. **Removal only when consumption == 0** for a sustained period (weeks).

**The cost of maintaining dual-publishing for 6+ months is real**, but it's the right cost. The alternative (quick removal) breaks consumers in production, and the cost of an incident in banking is orders of magnitude larger.

**Common antipattern**: "rip-the-bandaid" deprecation — publish announcement, give 1 month, remove. In systems with diverse consumers, there will always be someone who missed the announcement. Result: avoidable incident.

---

## Pillar 4: The Living Catalogue — Documentation as a Product

Without a catalogue, governance lives in the heads of a few people, and disappears when those people leave. With a catalogue, it is discoverable, navigable, maintainable.

### What the Catalogue Contains for Each Event

```
DepositConstituted

Owner: deposits team
Status: active (v1)
Published since: 2026-01-15

Business meaning:
  A term deposit has been successfully constituted and is now active.
  The capital has been debited from the source account and the
  contractual terms are now binding.

When emitted:
  - After complete saga of constitution succeeds
  - After Core debit is confirmed
  - After Compliance registration is confirmed
  - Never if the constitution saga fails or is cancelled

Payload schema:
  [link to current schema, with all versions]

Compatibility mode: BACKWARD

Known consumers (informational):
  - notifications (since 2026-01-15)
  - reporting (since 2026-01-20)
  - crm (since 2026-02-10)
  - documentation (since 2026-03-01)

Related events:
  - DepositRequested (precedes, internal)
  - DepositCancelled (alternative outcome)
  - DepositMobilized (later state)

Historical changes:
  - 2026-03-15: added field interest_modality (backward compatible)
  - 2026-05-01: added field metadata.core_txn_id (backward compatible)

Examples:
  [link to sample payloads]

SLA:
  - Emitted within 1 second of saga completion (p99)
  - Retention: 90 days in Kafka, indefinite in event archive

Contact:
  - deposits-team@bank.pt
  - #deposits-platform on Slack
```

### Concrete Tooling

Several options, ordered by sophistication:

- **Simplest**: Git repository with markdown, one file per event. Works. Search by grep. No cost.
- **Middle ground**: tools like **EventCatalog** (open source), **AsyncAPI** with a static generator. Good navigation, schemas integrated, versioning.
- **Sophisticated**: commercial platforms (Backstage with event plugin, Confluent Stream Governance, Solace Event Portal). Beautiful, complete, expensive.

In greenfield, start with the middle option: AsyncAPI + a static generator. Low cost, high capability. Migrate when justified.

**The catalogue is integrated with the schema registry**, not alternative to it. Schemas are what the system mechanically validates. The catalogue is what humans consult to understand meaning.

**The hard rule**: **no event can be published in production without an entry in the catalogue**. CI/CD gate verifies. No entry → build fails. It's not convention; it's mechanical.

---

## Common Antipatterns

In order of prevalence:

**1. "We'll document it later."** No. Events without documentation at the time stay without documentation forever. RFC and catalogue entry are a PR prerequisite, not post.

**2. Event as a bag of optional fields.** "Just in case, we include everything, and consumers use what they need." Result: payloads of 50+ fields where nobody knows what's mandatory, what can be null, and the meaning of each field in this particular context. **Focused events win; generic events lose.**

**3. Events as reflections of UI.** "Screen X needs these data, so we emit `ScreenXDataReady`." No. Events reflect **business facts**, not UI states. UI is a consumer, not a producer of semantics.

**4. CRUD in events.** `DepositCreated`, `DepositUpdated`, `DepositDeleted`. Technically valid, semantically empty. *Updated* — which change? *Deleted* — means cancelled, mobilized, or matured? Each state deserves its specific event.

**5. Event microscopy.** Emitting 47 events for what is a single business operation. Every internal transition becomes a public event. Result: consumers don't know which to subscribe to, and the concept of "deposit constituted" fragments into 47 signals that need to be reassembled. **Integration events are fat, not thin.** Primitive 2 forgotten.

**6. Out-of-date catalogue.** The catalogue says one thing, the code emits another. Result: consumers build on the catalogue and break in production. **CI gate that validates that the code's schema matches the catalogue's** is the minimum defence.

**7. Events with no real owner.** "Belongs to the platform." Result: nobody is responsible, nobody maintains, nobody evolves. Orphan events are latent bugs.

---

## Organizational Considerations — Conway's Law Made Explicit

Conway's law states that systems reflect the communication structure of the organizations that build them. In event-driven, this is literal: **events reflect (and fix) organizational boundaries**.

Two practical implications:

**1. Bounded context boundaries should align with team boundaries.** If two teams share a bounded context, they will produce conflicting events. If a bounded context is fragmented across an organizational boundary, the context will disintegrate.

**2. Events crossing multiple teams need explicit stewardship.** Where the producer is one and consumers are many (the common case), stewards ensure the producer doesn't evolve the event egocentrically.

**The reverse is also true**: if you want to change the system toward an architectural direction, you may need to change the organizational structure first. Trying to impose an event-driven architecture with teams structured by layers (frontend, backend, DB) rarely works — layer-organized teams produce CRUD APIs, not business events.

---

## The Relationship With Everything Before

Governance closes the cycle:

- **Primitives 1 and 2** (Command vs Event, Domain vs Integration): governance is what makes these distinctions **mechanically enforced** over time, not dependent on individual goodwill.
- **ACL (Document 02)**: governance dictates how events leave the ACL for the backbone. It defines who can publish `DepositConstituted` (only Deposits, even if the ACL is involved).
- **CQRS (Document 03)**: governance defines which events can feed read models and how changes to events affect projections.
- **Plumbing (Document 04)**: the schema registry is the technical infrastructure; governance is the human discipline that decides what to register.
- **Saga (Document 05)**: the saga publishes precise integration events. Governance validates that those events follow conventions and are in the catalogue.
- **Observability (Document 06)**: governance defines that each event carries correlation_id, causation_id, and that is mechanically validated.
- **Testing (Document 07)**: contract tests **are the technical manifestation** of governance. Without governance, contract tests don't know what to validate.

Governance isn't decorative overlay; it's the connective tissue of everything else.

---

## Where to Start — Pragmatic Recommendation for Greenfield

In greenfield, with a small team, **don't start heavy**. But establish the foundations from the beginning:

1. **Decide naming and envelope conventions before the first event.** 30 minutes of discussion, recorded in a document. Apply from the start.
2. **Define ownership by bounded context** from the beginning. Even if each context is a single team today, formal ownership protects when it grows.
3. **Simple catalogue (markdown in Git) from the first event.** Don't wait for the second.
4. **CI gate**: code schema matches catalogue, or build fails. Implement early, before accumulating debt.
5. **Light RFC process from the second bounded context onwards** (until then, it's internal discussion). Requiring it earlier is premature; not requiring it later is negligence.
6. **Formalized event stewards at 5-6 active bounded contexts.** Earlier is unnecessary; later is too late.

The scale is incremental. The principle is constant: **decisions about events are architectural, not tactical**. Treating them as tactical is the fast track to the month-18 swamp.
