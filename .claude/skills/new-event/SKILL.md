---
name: new-event
description: >-
  Add one new integration event to an existing family — enforce the
  `<Entity><PastParticipleVerb>` naming convention, write the C# event record
  and pure handler, author the governed Avro `.avsc`, register it in the
  AsyncAPI EventCatalog (and the Backstage descriptor), confirm the CloudEvents
  envelope stays in headers (never on the payload), and run the BACKWARD
  registry compatibility check. Use when the user wants to add/create/define a
  new domain or integration event on a family that already exists.
---

# new-event — add a governed event to an existing family

You add **one** new event to a family that already ships (the reference family is
[`term_deposit`](families/term-deposit/src/Babelstone.Families.TermDeposit/)). This is the
event-level companion to `new-family-schema` (which scaffolds a *whole* family). The event
is **structural only** — computed facts and opaque references, never PII, in cleartext or
ciphertext ([ADR-PC-004 §P2](docs/product-management/product_concepts/adrs/ADR-PC-004-pii-crypto-shredding.md)).

> An event has FOUR coupled artefacts that must agree, or a CI gate fails:
> the C# record, its pure handler binding, the governed Avro `.avsc`, and the
> AsyncAPI catalogue entry. This skill writes all four in lock-step.

Throughout, replace `<domain>` (e.g. `deposits`), `<aggregate_type>` / `<family>` (snake_case,
e.g. `term_deposit`), `<family-kebab>` (the project directory, e.g. `term-deposit`), `<Family>`
(the PascalCase namespace segment, e.g. `TermDeposit`), `<State>` (the family's folded-state
record, e.g. `DepositPosition`), and `<EventName>` with the real values. **Edit *your* family's
files.** The `families/term-deposit/…` links below point at the reference family
[`term_deposit`](families/term-deposit/src/Babelstone.Families.TermDeposit/) to show the
*pattern* — for any other family, write to `families/<family-kebab>/…/Babelstone.Families.<Family>/`
and swap `TermDeposit`/`DepositPosition` for your own names. The reference family's domain is
`deposits` and its aggregate type / family name is `term_deposit`.

## Step 1 — Name the event: `<Entity><PastParticipleVerb>`

The naming rule is **absolute** and authoritative in
[08-event-catalog-governance.md §"Naming Convention for Integration Events"](docs/product-management/integration_concepts/08-event-catalog-governance.md):

> Structure: **`<Entity><PastParticipleVerb>`** (or `<Entity><State>`). PascalCase. The name
> describes **a specific, identifiable past fact**. If you hesitate on "what exact moment does
> this describe?", the name is wrong.

| Good (a past fact) | Bad — and why |
|---|---|
| `DepositConstituted`, `InterestPaid`, `DepositMatured` | `ConstituteDeposit` — that is a **command** (Primitive 1 violated, [01-the-six-primitives.md](docs/product-management/integration_concepts/01-the-six-primitives.md)) |
| `DepositRenewed`, `WithholdingApplied` | `NewDeposit` / `DepositEvent` / `DepositStatusChange` — generic or ambiguous |
| `DepositTerminatedEarly` | `dep_constituted` — doesn't match the catalogue casing |

Reject the name and stop if it reads as a command (imperative verb first) or names no
discrete moment. An event **cannot be rejected** — it already happened — so a name implying
a still-pending action is wrong.

## Step 2 — Write the C# event record (the cleartext domain event)

Append to **your family's** `Events.cs` —
`families/<family-kebab>/src/Babelstone.Families.<Family>/Events.cs` (pattern:
[`term_deposit`'s `Events.cs`](families/term-deposit/src/Babelstone.Families.TermDeposit/Events.cs)).
The record is a `sealed record … : DomainEvent`
(base in [`engine/src/Babelstone.Engine/Handlers.cs`](engine/src/Babelstone.Engine/Handlers.cs)),
in namespace `Babelstone.Families.<Family>`. Discipline (mirrors every existing event):

- **Carry already-COMPUTED facts**, never inputs the handler would recompute. The
  financial-math kernel ([`Babelstone.FinancialMath`](engine/src/Babelstone.FinancialMath/))
  runs **command-side** (the decider), never in a fold.
- **All money is [`Money`](engine/src/Babelstone.FinancialTypes/Money.cs)** (integer cents —
  [ADR-PC-010 §P1](docs/product-management/product_concepts/adrs/ADR-PC-010-dotnet-hand-rolled-engine.md), `BMNY002`); `Guid` for ids; `DateOnly` for dates.
- **No PII** — no depositor/heir name, NIF, IBAN. A PII field carries an *opaque reference*
  the engine resolves internally behind the OpenBao seam (the `HeirCaseRef` pattern on
  `DepositTransferredToHeirs`), [ADR-PC-004 §P2](docs/product-management/product_concepts/adrs/ADR-PC-004-pii-crypto-shredding.md).
- **The envelope is NOT on the record.** The pack/schema/family pins, ids, actor, and times
  ride on [`EventEnvelope`](engine/src/Babelstone.EventStore/EventEnvelope.cs) via
  `AppendContext` — see Step 5. The record is the business *payload* only.

```csharp
/// <summary>One-line "what past fact happened". Computed facts as Money; no PII (ADR-PC-004 §P2).</summary>
public sealed record <EventName>(
    Guid DepositId,
    Money <AmountField>,
    DateOnly <WhenField>) : DomainEvent;
```

## Step 3 — Write the pure handler and bind it in the module

Two edits, both in **your family's** pure-fold project (it references **only** `Babelstone.Engine`
+ `Babelstone.FinancialTypes` — a fold structurally cannot reach a DB, per
[term_deposit's `.csproj`](families/term-deposit/src/Babelstone.Families.TermDeposit/Babelstone.Families.TermDeposit.csproj)):

1. **The fold** in `families/<family-kebab>/src/Babelstone.Families.<Family>/Handlers.cs`
   (pattern: [term_deposit's `Handlers.cs`](families/term-deposit/src/Babelstone.Families.TermDeposit/Handlers.cs)) —
   one `IEventHandler<<State>, <EventName>>` whose body is a single `state with { … }`.
   **No clock, no I/O, no randomness** — the `BENG001/002/003` analysers fail the build
   otherwise (warnings are errors via `Directory.Build.props`). Sum into accumulators
   (`state.X + event.Y`) rather than overwrite, so the fold stays correct under replay.

```csharp
public sealed class <EventName>Handler : IEventHandler<<State>, <EventName>>
{
    public HandlerResult<<State>> Apply(<State> state, <EventName> @event)
        => HandlerResult<<State>>.From(state with { /* label / accumulate only */ });
}
```

2. **The binding** in
   `families/<family-kebab>/src/Babelstone.Families.<Family>/<Family>FamilyModule.cs`
   (pattern: [`TermDepositFamilyModule.cs`](families/term-deposit/src/Babelstone.Families.TermDeposit/TermDepositFamilyModule.cs))
   `Handlers` — the `event_type` string is `<family>.<EventName>`:

```csharp
new("<family>.<EventName>", typeof(<EventName>),
    new DispatchableHandler<<State>, <EventName>>(new <EventName>Handler())),
```

If the new event must update one of the family's projections (term_deposit's are the F.6
accrual schedule, maturity calendar, withholding ledger), also add a `<State>`-shaped fold +
binding to the relevant registry in
`families/<family-kebab>/src/Babelstone.Families.<Family>/<Family>ProjectionModule.cs`
(pattern: [`TermDepositProjectionModule.cs`](families/term-deposit/src/Babelstone.Families.TermDeposit/TermDepositProjectionModule.cs)).
A projection runner **skips** any event type it has no binding for, so you only add a fold
where the event genuinely changes that belief.

If the event also opens a new lifecycle transition, add the `Transition` enum value and its
legal-source row to
`families/<family-kebab>/src/Babelstone.Families.<Family>/LifecycleTransitions.cs`
(pattern: [term_deposit's `LifecycleTransitions.cs`](families/term-deposit/src/Babelstone.Families.TermDeposit/LifecycleTransitions.cs))
— that table is the only place a new transition may exist (the decider consults it before
appending). A new *operating* event that does not move the lifecycle needs no row beyond
its `Active`-only source if the decider gates it.

## Step 4 — Author the governed Avro `.avsc`

Create `contracts/avro/<domain>/<aggregate_type>/<EventName>.avsc` — the directory mirrors
the Avro `namespace` exactly, and the file name is the **bare PascalCase event name**
([`contracts/avro/README.md`](contracts/avro/README.md), [ADR-IC-002 §P1](docs/product-management/integration_concepts/adrs/ADR-IC-002-schema-format-and-registry.md) as amended). No `.csproj`
edit is needed — the recursive glob in
[`Babelstone.Engine.Avro.csproj`](engine/src/Babelstone.Engine.Avro/Babelstone.Engine.Avro.csproj)
embeds every `.avsc` automatically.

```json
{
  "type": "record",
  "namespace": "<domain>.<aggregate_type>",
  "name": "<EventName>",
  "doc": "What past fact this is. Business payload only (ADR-IC-002 §P5); CloudEvents envelope rides in Kafka headers, NOT here. No PII (ADR-PC-004 §P2).",
  "fields": [
    { "name": "deposit_id", "type": { "type": "string", "logicalType": "uuid" }, "doc": "DepositId (Guid)." },
    { "name": "<amount>_cents", "type": "long", "doc": "Money as integer EUR cents (ADR-PC-010 §P1)." },
    { "name": "<when>", "type": { "type": "int", "logicalType": "date" }, "doc": "DateOnly as days since epoch." }
  ]
}
```

The C#→Avro type map ([`contracts/avro/README.md`](contracts/avro/README.md)):

| C# | Avro | Field-name convention |
|---|---|---|
| `Money(long Cents)` | `long` | `*_cents` |
| `Guid` | `string` + `{"logicalType":"uuid"}` | `*_id` |
| `DateOnly` | `int` + `{"logicalType":"date"}` | days since epoch |
| `int` / `string` | `int` / `string` | snake_case |

The Avro field order and names must align with the C# record's constructor parameters — the
codec binds parameters to fields by convention. **All v1 fields are required** (no
`["null", T]` union). If you genuinely need an optional/additive field for forward-only
evolution, give it a `default` and put `"null"` **first** in any union (ADR-IC-002 §P2 — the
compatibility gate in Step 7 lints this).

## Step 5 — Confirm the envelope (headers, never payload)

You do **not** add envelope fields to the `.avsc` or the C# record. The CloudEvents 1.0
envelope travels as **Kafka headers** (Binary Content Mode), written by the outbox relay
from outbox columns; the persisted-row contract is
[`EventEnvelope`](engine/src/Babelstone.EventStore/EventEnvelope.cs). The required header set
is declared once, per event, in the AsyncAPI message `headers` block (Step 6) — copy the
canonical required list from
[`DepositConstituted.asyncapi.yaml`](contracts/catalog/events/DepositConstituted.asyncapi.yaml):
`ce_specversion`, `ce_id`, `ce_source`, `ce_type`, `ce_time`, `ce_subject`,
`ce_aggregatetype`, `ce_datacontenttype`. The Kafka record **key** is the `aggregate_id`
(the `DepositId`). Putting a `ce_*` attribute or a pin into the `.avsc` is the mistake to
avoid — the payload is business data only ([ADR-IC-002 §P5](docs/product-management/integration_concepts/adrs/ADR-IC-002-schema-format-and-registry.md)).

## Step 6 — Register in the EventCatalog (AsyncAPI source of truth)

The governed catalogue is the set of **AsyncAPI 3.0** files under
[`contracts/catalog/events/`](contracts/catalog/events/) — the *single source of truth*
([ADR-IC-015](docs/product-management/integration_concepts/adrs/ADR-IC-015-event-catalog-governance-tooling-backstage.md);
the Backstage portal and the generated `infra/eventcatalog/site` only *render* it). The
payload is **referenced, never restated** — the message's `payload.schema.$ref` points at
your `.avsc`, so the catalogue never re-types fields.

1. Create `contracts/catalog/events/<EventName>.asyncapi.yaml`, copying the structure of
   [`DepositConstituted.asyncapi.yaml`](contracts/catalog/events/DepositConstituted.asyncapi.yaml).
   Set on the new event: `info.title`/`x-owner`/`x-status`/`x-gdpr-legal-basis`/
   `x-authorized-consumers`; the `<aggregate_type>` channel (topic name == `aggregate_type`);
   the `headers` required list from Step 5; and the two payload anchors —
   - `x-schema-registry-subject: <domain>.<aggregate_type>.<EventName>-value`
   - `payload.schema.$ref: '../../avro/<domain>/<aggregate_type>/<EventName>.avsc'`
2. Add a `kind: API` entity (and the `targets:` line) for the new event to the Backstage
   descriptor [`contracts/catalog/catalog-info.yaml`](contracts/catalog/catalog-info.yaml),
   matching the existing four entries.

Why this matters mechanically: the gate's **orphan check (§P2)** fails if a governed `.avsc`
under `contracts/avro/` is not `$ref`'d by *any* catalogue file. Skipping Step 6 makes the
build red, not silently undocumented.

## Step 7 — Run the gates (registry BACKWARD compat + catalogue)

Two CI gates, both runnable locally:

```bash
# Avro §P1 path-mirror + §P2 null-first lint + §P3 BACKWARD registry compatibility
# vs origin/main (throwaway Redpanda Schema Registry; needs Docker).
mise exec -- make avro-compat-check
#   Docker-free static-only subset (skips §P3 registry round-trip):
AVRO_COMPAT_STATIC_ONLY=1 ./scripts/avro-compat-check.sh

# AsyncAPI catalogue §P1–§P6 (well-formed, governance fields, orphan check, subject
# reconstructs from the .avsc). Fast + hermetic — needs only Node + jq, no live registry.
mise exec -- make asyncapi-catalog-validate
```

A brand-new event has **no previously-published version**, so the §P3 BACKWARD check reports
`NEW … nothing to break` — that is the expected pass. The compatibility gate bites on a
*later* edit to an existing event's `.avsc`: a non-additive change (dropping/renaming a
field, or adding a required one with no default) fails BACKWARD, and the remedy is the
versioning convention from
[08 §"Versioning Convention"](docs/product-management/integration_concepts/08-event-catalog-governance.md) —
**never rename in place**; ship a parallel `<EventName>V2` with a sunset plan.

## Step 8 — Cover the fold and verify the build

Add a unit test for the new fold alongside the existing ones in **your family's** test project
`families/<family-kebab>/tests/Babelstone.Families.<Family>.Tests/` (pure, no Docker — see
term_deposit's [`TermDepositProjectionTests.cs`](families/term-deposit/tests/Babelstone.Families.TermDeposit.Tests/TermDepositProjectionTests.cs)
for the `Fold(seed, registry, event)` pattern). Then build + test only the projects you touched:

```bash
mise exec -- dotnet build families/<family-kebab>/src/Babelstone.Families.<Family>/Babelstone.Families.<Family>.csproj --nologo -v q
mise exec -- dotnet test families/<family-kebab>/tests/Babelstone.Families.<Family>.Tests/ --nologo -v q
```

## Guardrails

- **The name is a past fact** — `<Entity><PastParticipleVerb>`. A command-shaped or ambiguous
  name is rejected at review (Step 1), not negotiable.
- **Four artefacts in lock-step** — C# record, handler binding, `.avsc`, AsyncAPI entry. A
  missing catalogue entry trips the §P2 orphan check; a missing handler binding throws
  `Duplicate/unknown handler` at registry build.
- **No PII, ever** — references behind the OpenBao seam, never identity on the bus
  (cleartext or ciphertext), [ADR-PC-004 §P2](docs/product-management/product_concepts/adrs/ADR-PC-004-pii-crypto-shredding.md).
- **Envelope in headers, not payload** — `ce_*` and pins never enter the `.avsc`.
- **Never rename or break in place** — BACKWARD-incompatible evolution is a new
  `<EventName>V2` published in parallel ([08 §Versioning](docs/product-management/integration_concepts/08-event-catalog-governance.md)).
- **Folds stay pure** — no clock/I/O/randomness; the `BENG001/002/003` analysers enforce it.
