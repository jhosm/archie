# /contracts/avro

Hand-authored **Avro payload schemas** — the wire format of every integration event
published to Redpanda ([ADR-IC-002](../../docs/product-management/integration_concepts/adrs/ADR-IC-002-schema-format-and-registry.md)).

## Location & naming (ADR-IC-002 §P1)

- One `.avsc` file per event type.
- File name: `{aggregate_type}.{EventName}.avsc` with the Avro `namespace` =
  `{domain}.{aggregate_type}` (here `deposits.term_deposit`) and the Avro `name` = the
  PascalCase event name. So the files are named after the **fully-qualified name**:
  `deposits.term_deposit.DepositConstituted.avsc`.
- The registry **subject** is derived from the fully-qualified name + `-value`:
  `deposits.term_deposit.DepositConstituted-value`. The engine's Avro codec
  (`Babelstone.Engine.Avro`) maps `event_type` (`term_deposit.DepositConstituted`) →
  subject at encode time; the relay (`Babelstone.OutboxPublisher`) never re-resolves a
  subject — it reads the `schema_id` embedded in the outbox row (ADR-IC-004 §P3).

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
