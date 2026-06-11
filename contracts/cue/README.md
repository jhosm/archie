# `/contracts/cue` — the family-schema constraint language

The CUE half of the [governed contract surface](../README.md). These `.cue`
files are the **typed contract that variant YAML populates**
([feature-design-configuration-authoring §2.2](../../docs/product-management/product_concepts/feature-design-configuration-authoring.md)),
and the language is **CUE 0.16.1** validated by a purpose-built Go binary
([ADR-PC-006](../../docs/product-management/product_concepts/adrs/ADR-PC-006-cue-schema-language.md)).

## Layout

```
common.cue                       cross-family vocabulary: version-key shapes,
                                 bounded scalars, pack-binding + rate-sheet refs
families/term-deposit.cue        the v1 family schema (02 §2)
testdata/term-deposit/valid/     variants the schema must accept
testdata/term-deposit/invalid/   variants the schema must reject (one rule each)
check.sh                         fmt + compile + accept/reject gate
```

## What this is — and is not

This is the **schema language** (Epic C.1): variant structure, type/range
bounds, and *pack-binding declarations*. It is verifiable today with the
pinned `cue` CLI alone — no engine, no PostgreSQL.

It is **not** the `pack-validate` binary (C.2). The validator's five depths
([authoring §5](../../docs/product-management/product_concepts/feature-design-configuration-authoring.md))
resolve pack-bound primitives against the pinned pack's data and run the
depth-5 simulation; that needs pack data (C.4) and the Go binary. Here, a
pack-bound field (e.g. `day_count: pt.act_360`) is constrained only to its
*binding shape* — a dotted, jurisdiction-namespaced reference — and the
namespace-to-primitive resolution is left to depths 2–3.

## Source of truth vs. the pack

These files are the **source of truth**, owned by engineering on a quarterly
cadence ([authoring §2.2](../../docs/product-management/product_concepts/feature-design-configuration-authoring.md),
[ADR-PC-019 §P1](../../docs/product-management/product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md)
places CUE constraint schemas under `/contracts`). A signed pack bundles a
**digest-pinned copy** under its own `schemas/` directory
([ADR-PC-007 §P1](../../docs/product-management/product_concepts/adrs/ADR-PC-007-signed-yaml-oci-pack.md))
so the engine validates against exactly the schema the pack was signed with —
that copy-into-the-pack step is C.4, not this task.

## No DSL escape hatch

[ADR-PC-006](../../docs/product-management/product_concepts/adrs/ADR-PC-006-cue-schema-language.md)
Decision and [authoring §9.5](../../docs/product-management/product_concepts/feature-design-configuration-authoring.md)
forbid a runtime-evaluated escape hatch. Enforcement is structural, not by
convention: every type is a CUE **definition** (`#Name`), which is **closed** —
a field the schema does not declare is rejected at depth 1. The
`invalid/unknown-field.yaml` fixture proves it (`promo_flag: field not
allowed`). There is no `extra: {...}` passthrough anywhere.

## Running the gate

```sh
make contracts-check        # or: ./contracts/cue/check.sh
```

The same script runs in the `contracts` path-scoped CI job
([ADR-PC-019 §P1](../../docs/product-management/product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md)).
To validate one variant by hand:

```sh
cue vet -d '#TermDeposit' <variant>.yaml common.cue families/term-deposit.cue
```

## Adding a family or a fixture

- **New family:** add `families/<family-kebab>.cue` (a closed `#RootDefinition`
  composing `common.cue`) and a `testdata/<family-kebab>/{valid,invalid}/` tree —
  both kebab-cased to match the family's project directory. `check.sh`
  auto-discovers them from `families/*.cue`; no script edit needed.
- **New rule:** add a `valid/` fixture exercising it and an `invalid/` fixture
  that isolates its violation — keep invalid fixtures one-rule-per-file so a
  failure names the rule.
