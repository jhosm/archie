# How to manage FGD deposit-guarantee coverage in a pack

This guide walks you through setting the **deposit-guarantee-fund (FGD) coverage**
in a pack's `primitives/fgd.yaml` — the file that declares the per-depositor
coverage ceiling and the guarantee scheme for your jurisdiction. FGD is the
*Fundo de Garantia de Depósitos*, Portugal's deposit-guarantee scheme.

You will: set the coverage ceiling and the scheme, and validate locally. The
worked example is the PT term-deposit pack,
[`pt.2026.1`](../../../packs/pt.2026.1/).

**Before you start, know this:** in v1 the engine emits the
**eligible-balances-per-customer signal**; assembly of the actual FGD return is
**downstream**, not in the engine. This pack file declares the *coverage
parameters* (the ceiling and the scheme) that the eligibility signal is computed
against — it does not compute the return itself. The wider coexistence picture
(why the report is assembled downstream) is in
[the strangler-fig coexistence note](../../product-management/product_concepts/feature-design-strangler-fig-coexistence.md).

## What an FGD entry looks like

In `primitives/fgd.yaml` the coverage is one map entry, keyed by
`deposit_guarantee`. Here is the shape, as a short illustration — not the
authoritative field list:

```yaml
# packs/pt.2026.1/primitives/fgd.yaml
deposit_guarantee:
  coverage_ceiling_cents: 10000000   # €100,000.00 per depositor
  scheme: fgd_pt
```

The authoritative field-by-field shape is the
[`pack.cue`](../../../contracts/cue/pack/pack.cue) schema, rendered in the
generated [pack-format reference](../reference/pack-format/README.md).
Do not copy a field table from elsewhere — link to those and you will never go
stale.

The fields you set:

- **`coverage_ceiling_cents`** — the per-depositor coverage ceiling in **integer
  cents** (`10000000` = €100,000.00). Money is always integer cents in a pack —
  never a float — so there is no rounding drift. The €100,000 figure is the EU
  Deposit Guarantee Schemes Directive ceiling: stable, jurisdiction-wide, not a
  product-by-product parameter.
- **`scheme`** — the scheme identifier (`fgd_pt` for Portugal).

## Steps

### 1. Set the coverage ceiling and scheme

Open `primitives/fgd.yaml` and set the ceiling (in cents) and the scheme id:

```yaml
deposit_guarantee:
  coverage_ceiling_cents: 10000000   # €100,000.00
  scheme: fgd_pt
```

Use **integer cents**: €100,000.00 is `10000000`, not `100000` and not
`100000.00`. The ceiling is the EU-directive figure; you change it only if the
directive ceiling changes — which, like any pack edit, lands in a **new pack
version**, never an in-place change to a published pack.

### 2. Validate locally

Run the offline depth-1–4 validation over the pack's data:

```bash
make pack-validate PACK=pt.2026.1     # use your pack's dirname
```

This `cue vet`s `fgd.yaml` against its schema — catching a non-integer ceiling,
a missing scheme, or a mistyped key. It is fully offline (no registry, no
Docker).

## How FGD coverage connects to the rest of the pack

The FGD coverage parameters here are consumed by the FGD **reporting hook** —
the `fgd_cobertura_depositos` return declared in
[`primitives/reporting.yaml`](../../../packs/pt.2026.1/primitives/reporting.yaml).
That hook is what schedules the coverage return; the eligible-balances figures it
reports are computed against the ceiling you set here. To wire the report itself,
see [add a regulatory reporting hook](./add-a-reporting-hook.md).

## Honest local limits

A clean `make pack-validate` covers depths 1–4 only. Be aware:

- **The engine emits the signal; it does not assemble the return.** v1 produces
  the eligible-balances-per-customer signal only — a downstream reporting
  application builds the FGD return. A local validate checks the *declaration*,
  not the downstream assembly.
- **Depth-5 (sealed-corpus engine simulation) does not run locally yet** — it
  does not exercise eligibility computation.
- **Signing and a production registry are not wired for you** — do not treat a
  locally built pack as published.

## Related

- [Add a regulatory reporting hook](./add-a-reporting-hook.md) — the
  `fgd_cobertura_depositos` coverage return that consumes these parameters
- [Strangler-fig coexistence](../../product-management/product_concepts/feature-design-strangler-fig-coexistence.md)
  — why FGD return assembly is downstream of the engine
- [The pack-format reference](../reference/pack-format/README.md) — the
  authoritative, generated field list
- [Product-docs home](../README.md)
