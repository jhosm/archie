# /contracts/catalog/reconciliation

The **per-consumer reconciliation contracts** (event-store
[§7.3](../../../docs/product-management/product_concepts/feature-design-event-store-projections.md#73-what-consumer-means-here)).

> "The event log is the source of truth" is aspirational unless consumers can **prove** they
> consumed it correctly (event-store §7). Every downstream system that derives state from engine
> events is a *consumer* subject to reconciliation, and **each consumer agrees with the engine on a
> reconciliation contract** — which checksums it publishes, which event-count it reports, and how
> full rebuilds are coordinated. Those contracts are part of the event catalogue's governance.

This subtree is the catalogued, machine-readable form of that agreement: one
`*.reconciliation.yaml` per consumer, registered in the catalogue's
[`catalog-info.yaml`](../catalog-info.yaml) alongside the AsyncAPI events.

## The two sides of one contract

A reconciliation contract has a **catalogued side** (these YAML descriptors — the portal/auditor
view) and an **executable side** (`ReconciliationContract` in
[`engine/src/Babelstone.Engine/ProjectionReconciler.cs`](../../../engine/src/Babelstone.Engine/ProjectionReconciler.cs)).
Both carry the same four fields and must stay in step:

| Descriptor field            | `ReconciliationContract` field | Meaning |
|-----------------------------|--------------------------------|---------|
| `spec.consumer`             | `Consumer`                     | The consumer's stable name (== the AsyncAPI `x-authorized-consumers` identity). |
| `spec.projectionKinds[].kind` | `ProjectionKind`             | The family-prefixed projection discriminator it reconciles. |
| `spec.patterns.*`           | `Patterns` (`ReconciliationPatterns` flags) | Which of the three §7.1 patterns it runs. |
| (the descriptor's own path) | `ContractRef`                  | The back-reference from the executable contract to this descriptor. |

`ProjectionReconciler.ReconcileAsync(contract, streamId, …)` drives a contract: it runs exactly the
§7.1 patterns the contract declares and folds them into a `ConsumerReconciliationReport` whose
`IsClean` verdict is what the reconciliation-alerting layer (M.5, bd `babelstone-irfl`) keys off.

## Why per-consumer, not one-size-fits-all

The §7.1 patterns a consumer participates in depend on what it derives:

| Consumer | Checksum (a) | Event-count (b) | Full rebuild (c) | Why |
|---|:---:|:---:|:---:|---|
| [`engine-projection-runtime`](./engine-projection-runtime.reconciliation.yaml) | yes | yes | yes | Folds the log it emits — all three patterns over every projection kind. |
| [`acl`](./acl.reconciliation.yaml) | yes | yes | yes | Out-of-process GL / IFRS 9 / tax projection (event-store §9); self-reports its progress. |
| [`notification`](./notification.reconciliation.yaml) | no | yes | no | Event-triggered confirmations — no rebuildable derived state, so checksum + rebuild are N/A. |

## No PII — ever

A reconciliation contract carries **references only** — a consumer name, a projection-kind
discriminator, a stream id, a sequence number, a state hash
([ADR-PC-004 §P2](../../../docs/product-management/product_concepts/adrs/ADR-PC-004-pii-crypto-shredding.md)
/ the no-PII-on-the-durable-bus rule). No depositor name, NIF, or IBAN — cleartext or ciphertext —
appears in a descriptor, in the executable contract, or in any reconciliation message a consumer
publishes. PII is resolved inside each consumer from a reference, never reconciled.

## The CI gate

The catalogue's [`catalog-info.yaml`](../catalog-info.yaml) registers these descriptors as a
Backstage `Location`, and the §9 well-formedness check in
[`scripts/asyncapi-catalog-validate.sh`](../../../scripts/asyncapi-catalog-validate.sh) proves the
descriptor parses. The Backstage **host** that renders the registered descriptors is deferred to
platform work (ADR-IC-015 Decision §9, bd `babelstone-s4ol.1`) — until then the files + the gate +
GitHub's renderer are the Git-native governance surface, exactly as for the AsyncAPI events.
