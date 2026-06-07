# /contracts/catalog

The **event-catalogue source** — the governed, machine-readable business contract of every
integration event the engine publishes ([ADR-IC-015](../../docs/product-management/integration_concepts/adrs/ADR-IC-015-event-catalog-governance-tooling-backstage.md)).
One [AsyncAPI 3.0](https://www.asyncapi.com/) file per event under [`events/`](./events/);
the [`catalog-info.yaml`](./catalog-info.yaml) is the only portal-specific artefact — a
[Backstage](https://backstage.io/) descriptor that imports the same AsyncAPI files.

> The AsyncAPI files are the **single source of truth** (ADR-IC-015 Decision §1): the schema
> registry is their structural validation layer, the CI gate is their enforcement
> mechanism, the portal is *only* the rendering layer. No governance record lives
> outside these files and the registry.

## Layout

```
contracts/catalog/
  catalog-info.yaml               # Backstage descriptor (the only portal-specific file)
  events/
    DepositConstituted.asyncapi.yaml
    InterestAccrued.asyncapi.yaml
    WithholdingApplied.asyncapi.yaml
    DepositMatured.asyncapi.yaml
```

Each file documents one event on the `term_deposit` channel (topic name == `aggregate_type`,
the relay's documented convention). The events are **Option A** (doc 08): one Avro schema per
event type, each its own message on the channel — not a discriminated single schema.

## The payload is referenced, never restated (Decision §1–§2)

Each message's `payload.schema.$ref` points at the **governed Avro `.avsc`** in
[`../avro/deposits/term_deposit/`](../avro/deposits/term_deposit/) — the real source of truth.
The catalogue never re-types the fields (a restatement would drift from `contracts/avro/`). The
registry **subject** each message reconciles against is recorded as
`x-schema-registry-subject` and is asserted to reconstruct from the `.avsc`
namespace + name (`{namespace}.{name}-value`, ADR-IC-002 §P1).

## No PII — ever

The events are structural ([ADR-PC-004 §P2](../../docs/product-management/product_concepts/adrs/ADR-PC-004-pii-crypto-shredding.md)).
The CloudEvents headers and the Avro payload carry no depositor name, NIF, IBAN, or any other
PII — cleartext or ciphertext. The catalogue's `examples:` blocks (when added) must use synthetic
data only; a real `client_id` in a version-controlled example is a GDPR incident (ADR-IC-015
Residual risks).

## The CI gate

[`scripts/asyncapi-catalog-validate.sh`](../../scripts/asyncapi-catalog-validate.sh) is the gate. Run it
locally with `make asyncapi-catalog-validate`. It is **fast and hermetic** — it needs only Node
(for the Apache-2.0 [`@asyncapi/cli`](https://github.com/asyncapi/cli)) and `jq`, **no live
Schema Registry and no running portal** (ADR-IC-015 Decision §4 + the CI-fragility
residual risk). It enforces §P1 (validity + required governance fields), §P2 (orphan check —
every governed `.avsc` under `contracts/avro/` is `$ref`'d by some catalogue file, so no
integration-event schema lacks an entry), §P3 (tombstone contract on compacted topics), §P4
(breaking-change diff vs `origin/main`), §P5 (deprecation notice period), §P6 (subject
reconstructs from the `.avsc`), and §9 (the Backstage [`catalog-info.yaml`](./catalog-info.yaml)
descriptor is well-formed YAML). The §8 *live* registry reconciliation
([`scripts/asyncapi-catalog-reconcile.sh`](../../scripts/asyncapi-catalog-reconcile.sh)) runs only
on the main lane.

## Backstage is the portal — descriptors now, host later

[`catalog-info.yaml`](./catalog-info.yaml) is a Backstage descriptor: one `kind: API` entity per
event, each `spec.type: asyncapi` with `spec.definition.$text` pointing at the relative
`events/*.asyncapi.yaml` (Backstage resolves `$text` relative to the descriptor's location). The
descriptors **ship now**; the **Backstage host deployment is deferred to platform work**
(ADR-IC-015 Decision §9, bd `babelstone-s4ol.1`).

To register these events in a Backstage instance later, point the Backstage catalogue at this
descriptor — e.g. in the instance's `app-config.yaml`:

```yaml
catalog:
  locations:
    - type: url
      target: https://github.com/jhosm/babelstone/blob/main/contracts/catalog/catalog-info.yaml
```

No catalogue-specific glue is needed — the AsyncAPI files are the import source, exactly as for
any AsyncAPI portal.

### Git-native fallback

Until the Backstage host exists, the estate operates **Git-native** (ADR-IC-015 Decision §9 /
the rejected-but-retained option): the AsyncAPI files + the CI gate + GitHub's file renderer
already satisfy the governance *existence* bar (every event is documented and machine-validated).
Backstage adds the *discoverability* bar (a rendered, searchable portal) at no licence cost. The
descriptor sitting here inert costs nothing while the host is undeployed.

## License-drift history (why Backstage, not EventCatalog)

This catalogue's portal tool changed once, on the record. [ADR-IC-008](../../docs/product-management/integration_concepts/adrs/retired/ADR-IC-008-event-catalog-governance-tooling.md)
(now superseded, retired) chose **EventCatalog** with a *conditional* licence pass — its required
features had to be re-verified to sit in the free open-source tier at implementation time. At the
G.4 implementation re-check (2026-06-07) that conditional was **realised**: EventCatalog's AsyncAPI
**generator plugin** (`@eventcatalog/generator-asyncapi`, the component that ingests these files
into the portal) had moved to a **dual-licensed AGPL-3.0 / commercial, license-keyed** model — no
longer in the free permissive tier.

ADR-IC-008 had pre-committed two exit paths for exactly this. [ADR-IC-015](../../docs/product-management/integration_concepts/adrs/ADR-IC-015-event-catalog-governance-tooling-backstage.md)
takes the **Backstage** one (Apache-2.0, CNCF-graduated): the estate keeps a rendered portal with
no AGPL/commercial exposure, the AsyncAPI files are untouched (the portal swap is a portal change,
not a specification change), and the governance gate — always on the Apache-2.0 AsyncAPI CLI, never
on EventCatalog — never regressed. The full audit trail is the
[ADR-IC-015 Context](../../docs/product-management/integration_concepts/adrs/ADR-IC-015-event-catalog-governance-tooling-backstage.md#context).
