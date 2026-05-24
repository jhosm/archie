# /conformance — spec-conformance governance

The design-time drift guard from [ADR-PC-020](../docs/product-management/product_concepts/adrs/ADR-PC-020-llm-toolchain-and-conformance-governance.md):
the catalogue that binds each load-bearing ADR commitment to the test that
proves it ("architecture fitness functions"), plus — as they land — the §P6
coverage checker and the §P3 spec-coverage auditor.

This is **dev-time governance tooling**, not a shipped runtime artefact. It is
its own extraction-ready top-level subtree per [ADR-PC-019 §P2](../docs/product-management/product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md),
and it spans **both** ADR namespaces (ADR-PC and the in-house ADR-IC estate),
per [ADR-PC-020 §P11](../docs/product-management/product_concepts/adrs/ADR-PC-020-llm-toolchain-and-conformance-governance.md).

## Contents

| Path | What it is |
|---|---|
| [`commitments.yaml`](./commitments.yaml) | The commitment catalogue — machine-readable source of truth. Seeded with the ~8 load-bearing invariants of [ADR-PC-020 §P7](../docs/product-management/product_concepts/adrs/ADR-PC-020-llm-toolchain-and-conformance-governance.md) ([Open Action #4](../docs/product-management/product_concepts/adrs/ADR-PC-020-llm-toolchain-and-conformance-governance.md)). |

Still to land (separate tasks): the §P6 **coverage checker** (a hook + CI step
that resolves every `Live` commitment to a running test and every code anchor
to a live ADR section) and the §P3 **spec-coverage auditor** (a periodic sweep
for ADRs with no commitment and governed code with no commitment test).

## `commitments.yaml` schema

```yaml
commitments:
  - id:           <stable Test ID — the §P6 coverage checker resolves it to a test>
    title:        <short label>
    commitment:   <the falsifiable claim the implementation must satisfy>
    sources:      [<ADR/doc §-anchor the claim derives from>, ...]
    source_paths: [<repo-relative path to each source>, ...]
    gate:         unit | integration | contract | saga | e2e | analyser | benchmark
    level:        <human-readable pyramid level / gate label>
    status:       Live | Planned | Gap
    per_push:     <bool — false for nightly/timing-sensitive gates>
    code_anchors: [<repo path : symbol of an implementing site, filled per §P6>, ...]
```

- **Test ID** — stable, UPPER_SNAKE_CASE; the contract between this catalogue, a
  test, and the per-ADR `Verifiable commitments` tables that cite it.
- **Status** ([ADR-PC-000 §A1](../docs/product-management/product_concepts/adrs/ADR-PC-000-namespace-and-contract-shape-framework.md)) — `Live` (gate exists and passes), `Planned`
  (gate to be built before the decision is implemented), `Gap` (no gate yet — a
  known hole, listed deliberately). At seeding every entry is `Planned`: the
  catalogue lands before broad engine work begins.
- **Gate** — a level of the [07-testing-strategy](../docs/product-management/integration_concepts/07-testing-strategy.md)
  pyramid (unit < integration < contract < saga < e2e), or `analyser`
  (build-time) / `benchmark` (timing, nightly).

## How an entry becomes `Live` (the §P10 spec-first loop)

> ADR (or amendment) → add/confirm the commitment here as a **failing** fitness
> function → implement until green → the §P6 coverage checker confirms the code
> anchor.

So `Planned` → `Live` happens when the gate is written and the implementing
site is annotated (`// ADR-PC-001 §P2`) and listed under `code_anchors`.

## Relationship to the per-ADR sections

Each ADR that owns a commitment carries a `## Verifiable commitments` table
([ADR-PC-000 §A1](../docs/product-management/product_concepts/adrs/ADR-PC-000-namespace-and-contract-shape-framework.md)) citing the same Test IDs. Those per-ADR tables are the
human-facing view; **this file is the aggregate registry the tooling reads**.
The two non-ADR commitments (`REPLAY_BUDGET_5S_30S`, `ZERO_ENGINE_DIFF_PER_VARIANT`)
derive from concept/feature-design docs, which carry no `Verifiable commitments`
section ([ADR-PC-000 §A2](../docs/product-management/product_concepts/adrs/ADR-PC-000-namespace-and-contract-shape-framework.md)), so they live only here.

Backfilling the section into the remaining ADRs is incremental
([ADR-PC-020 Open Action #7](../docs/product-management/product_concepts/adrs/ADR-PC-020-llm-toolchain-and-conformance-governance.md)), not part of this seed.
