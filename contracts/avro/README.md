# /contracts/avro

Hand-authored **Avro payload schemas** — the wire format of every integration event
published to Redpanda ([ADR-IC-002](../../docs/product-management/integration_concepts/adrs/ADR-IC-002-schema-format-and-registry.md)).

## Location & naming (ADR-IC-002 §P1, amended 2026-05-31)

- One `.avsc` file per event type, nested under `{domain}/{aggregate_type}/`.
- File name: `{EventName}.avsc` (bare PascalCase event name — the directory already encodes
  the domain and aggregate type, so the filename carries no redundancy).
- The Avro `namespace` inside the file is `{domain}.{aggregate_type}` (e.g.
  `deposits.term_deposit`) and the Avro `name` is the PascalCase event name. The
  directory mirrors the namespace exactly:
  `contracts/avro/deposits/term_deposit/DepositConstituted.avsc`.
- The registry **subject** is derived from the fully-qualified name + `-value`:
  `deposits.term_deposit.DepositConstituted-value`. The engine's Avro codec
  (`Babelstone.Engine.Avro`) maps `event_type` (`term_deposit.DepositConstituted`) →
  subject at encode time; the relay (`Babelstone.OutboxPublisher`) never re-resolves a
  subject — it reads the `schema_id` embedded in the outbox row (ADR-IC-004 §P3).
- **Adding a family:** create `contracts/avro/{domain}/{aggregate_type}/` and drop the
  event `.avsc` files there. No engine code changes — the recursive glob in
  `Babelstone.Engine.Avro.csproj` picks them up automatically.

## What these schemas model

The **business data payload only** — the CloudEvents `data` field (ADR-IC-002 §P5). The
CloudEvents envelope (`ce_*` attributes) travels as **Kafka headers**, written by the
relay from outbox columns, and is NOT in any `.avsc`.

Field-type mapping (matches the C# events in
`families/term-deposit/src/Babelstone.Families.TermDeposit/Events.cs`):

| C# type | Avro type | Note |
|---|---|---|
| `Money(long Cents)` | `long` | the integer cent count; field named `*_cents` |
| `Guid` | `string` + `{"logicalType":"uuid"}` | |
| `DateOnly` | `int` + `{"logicalType":"date"}` | days since Unix epoch |
| `int` | `int` | |
| `string` | `string` | |

All v1 fields are **required** — none is a nullable union (ADR-IC-002 §P2 governs the
`["null", T]`-with-null-first convention if/when an optional field is introduced).

## The `Movement` carrier (ADR-PC-032)

A money-moving event carries the money legs it caused — a list of **`Movement`** records
(one recorded change of value against one engine-owned account, [ADR-PC-032](../../docs/product-management/product_concepts/adrs/ADR-PC-032-money-movement-primitive.md)) —
**inside its own payload**, as a required Avro `array` field whose `items` is the nested
`Movement` record. No new `events`-table column, no envelope change: the `Movement`s ride
the event the family already writes and are therefore written **append-first** in the
event's outbox transaction.

The canonical, governed carrier shape is [`_shared/Movement.avsc.json`](./_shared/Movement.avsc.json).
It is deliberately **not** a `.avsc`: a `Movement` is **not an event** — it is never
catalogued, never registered under a Schema-Registry subject, and never published on its
own. The `.avsc.json` extension keeps it out of the `*.avsc` glob the engine embeds, the
catalog discovers, and the compat gate scans. A carrying event's `.avsc` **inlines** this
record verbatim as its array `items`; the engine's family-agnostic codec
(`MovementCarrier` in `Babelstone.Engine.Avro`) binds the carrying event's
`IReadOnlyList<Movement>` parameter to that array. An event with no movements carries an
**empty** array, never null.

`account_ref` is an **opaque reference** the engine resolves internally — never an
IBAN/cleartext or ciphertext account identifier ([ADR-PC-004 §P2](../../docs/product-management/product_concepts/adrs/ADR-PC-004-pii-crypto-shredding.md)).
The first family to emit movements authors the carrying event's `.avsc` (the
`Movement`-array field) and is the **contract-reviewer**'s lane.

## No PII — ever

These events are **structural** ([ADR-PC-004 §P2](../../docs/product-management/product_concepts/adrs/ADR-PC-004-pii-crypto-shredding.md)):
no depositor name, NIF, account-holder, IBAN, or any other PII appears on a schema or on
the bus — cleartext or ciphertext. Adding a PII field is a schema-review stop: the field
must instead carry a reference resolved internally behind the engine's OpenBao boundary.

## Compatibility

Default compatibility is **BACKWARD** (ADR-IC-002 §Consequences). Registration is a CI
gate, not a runtime operation (ADR-IC-002 §P3) — the engine's startup register-if-absent
(`Babelstone.Engine.Avro`) is a **walking-skeleton convenience**; the authoritative
CI-gate compatibility check is Epic G.3.
