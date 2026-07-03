# Pact consumer-driven contracts (ADR-IC-009)

In plain English: each JSON file here is a **consumer's written-down expectation** of a message
another service produces, in the standard [Pact](https://docs.pact.io) format. The consumer's test
suite *generates* the file and fails if the committed copy drifts; the producer's CI *verifies* it
can actually produce a matching message. That closed loop — consumer writes, producer proves — is
the behavioural gate the structural schema checks (SR compatibility, shape-lock, the AsyncAPI
catalogue) cannot give on their own.

## Contracts

| File | Consumer (writes it) | Provider (verified against it) | What it pins |
|---|---|---|---|
| `notification-delivery-engine.json` | `NotificationDueMessagePactTests` (`notification/tests/Babelstone.Notification.Delivery.Tests`) | `NotificationDuePactProviderTests` (`engine/tests/Babelstone.OutboxPublisher.Tests`, Test ID `NOTIFY_EMIT_PACT`) | The EVENT_DRIVEN `NotificationDue` message (ADR-PC-025): identity fields present and non-null, `customer_id` an opaque uuid reference, money as integer-cent **strings**, the governed `trigger_kind` symbol, ISO `due_at`. |

The GL-posting Pact consumer is **GL-team-owned and out-of-repo** (ADR-PC-012): babelstone owns GL
producer-verification only if/when a reference GL consumer exists.

## Conventions

- **The pact speaks the decoded view of the Avro payload** — uuids as canonical strings, enums as
  their SCREAMING_SNAKE_CASE symbols, `data` as a string→string map, dates as `yyyy-MM-dd`. The
  producer side builds that view by round-tripping through the real Avro codec against the governed
  `contracts/avro/**` schema, so the pact gates the wire, not a hand-maintained mirror.
- **CI is hermetic**: the PR-lane producer verification reads these committed files
  (`WithFileSource`), never a live broker — the same no-network stance as the other contracts gates.
- **The dev-stack Pact Broker** (ADR-IC-009 §S1) is the human/estate surface:
  `make pact-broker-up` then `make pact-publish` (profile `pact` in `infra/compose.yaml`,
  UI at `http://localhost:9292`). A broker-sourced CI verification is the named follow-up once a
  second repository consumes a babelstone contract.
- **Regenerating after a deliberate contract change**: run the consumer test with
  `BABELSTONE_PACT_UPDATE=1`, commit the diff, and expect the producer verification to answer for it.
- **PactNet is pinned to 4.5** (`Directory.Packages.props`): the 5.0.x line fails message
  producer-verification with "builder error for url (message://…)" (pact-foundation/pact-net#530).
