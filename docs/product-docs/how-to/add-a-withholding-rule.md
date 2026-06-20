# How to add a withholding rule to a pack

This guide walks you through adding a **withholding rule** to a pack's
`primitives/withholding.yaml` — the file that declares which tax-withholding
rules exist in your jurisdiction's vocabulary, at what rate, on what basis, and
when the withholding bites.

You will: add the entry, point it at an engine formula, set the rate and basis,
declare any exemptions, and validate locally. The worked example throughout is
the PT term-deposit pack, [`pt.2026.1`](../../../packs/pt.2026.1/), whose
standard case is the 28% IRS withholding on deposit interest.

**Before you start, know this:** withholding in Portugal is applied
**flow-by-flow** — withheld from each interest payment as it accrues, **never**
by scaling the headline rate. Getting this wrong (scaling the rate once instead
of withholding each flow) silently mis-states the net return on a multi-period
compound deposit. The *why* — and the exact arithmetic — lives in the
[financial mathematics §5.4 (deposit rates / TANL)](../../product-management/financial_concepts/banking_products_financial_mathematics.md);
this page is the *how* of declaring the rule in the pack.

## What a withholding entry looks like

In `primitives/withholding.yaml` each rule is one map entry, keyed by the
**rule id**. Here is the shape, as a short illustration — not the authoritative
field list:

```yaml
# packs/pt.2026.1/primitives/withholding.yaml
irs_juros:
  formula_ref: engine.withholding.percentage   # bridge to the engine primitive
  rate_basis_points: 2800                       # 28.00%
  basis: gross_interest                         # what the rate applies to
  timing: at_credit                             # withheld when net interest settles
  exemptions:
    - { id: pme_leader, evidence: declaration_pme }
  reporting:
    modelo_39: { required: true, frequency: annual }
```

The authoritative field-by-field shape is the
[`pack.cue`](../../../contracts/cue/pack/pack.cue) schema, rendered in the
generated [pack-format reference](../reference/pack-format/README.md).
Do not copy a field table from elsewhere — link to those and you will never go
stale.

The fields you set:

- **`formula_ref`** — a dotted reference (`engine.withholding.<…>`) naming an
  engine-implemented withholding primitive. This is a *bridge*: the pack names
  the formula, the engine supplies the math (and applies it flow-by-flow).
- **`rate_basis_points`** — the withholding rate in **basis points** (28% =
  `2800`). Basis points keep it an exact integer, no floating-point drift.
- **`basis`** — what the rate applies to (e.g. `gross_interest`).
- **`timing`** — when the withholding bites (e.g. `at_credit` = withheld when
  the net interest settles to the account).
- **`exemptions`** — the rule's full catalogue of exemption cases, each an
  `{ id, evidence }` pair. This is the **canonical** exemption manifest, not the
  set your v1 product onboards: a case (e.g. `non_resident_treaty`) may be
  declared here while the onboarding it needs is itself out of v1 scope.

## Which primitives does the engine implement?

`formula_ref` is the one field the pack tooling **cannot fully check for you**.
`make pack-validate` confirms the string is well-formed; it does **not** confirm
the engine actually implements the formula. That binding is resolved only when
the engine loads the pack — an unimplemented `formula_ref` throws there, never
at validate time. Confirm a `formula_ref` against the **engine**, not the pack
(the same trap the [day-count page](./add-a-day-count-primitive.md#which-primitives-does-the-engine-implement)
documents in full).

> **The flow-by-flow trap, stated plainly.** It is tempting to model withholding
> as a one-shot rate haircut (`TANL = TANB × (1 − 0.28)`). That is exact only
> for a single-period deposit paid at maturity. For a multi-period compound
> deposit the engine **must** withhold on each interest flow as it accrues —
> which is exactly why the rule is a `formula_ref` the engine applies per flow,
> not a number you bake into the rate sheet. See
> [financial mathematics §5.4](../../product-management/financial_concepts/banking_products_financial_mathematics.md).

## Steps

### 1. Add the entry

Open `primitives/withholding.yaml` in the pack you are editing and add a new
top-level key for the rule id. Use lowercase `snake_case`:

```yaml
irs_juros:
  formula_ref: ...      # set in step 2
  rate_basis_points: ...
  basis: ...
  timing: ...
```

Remember a pack is **immutable once published** — if `pt.2026.1` is already
released, your edit belongs in a new pack version (`pt.2026.2` / `pt.2027.1`),
never an in-place change to a published one.

### 2. Set `formula_ref`, `rate_basis_points`, `basis`, and `timing`

Point `formula_ref` at the engine primitive (form `engine.withholding.<name>`),
set the rate in basis points, and declare the basis and timing:

```yaml
irs_juros:
  formula_ref: engine.withholding.percentage
  rate_basis_points: 2800          # 28.00% IRS on deposit interest
  basis: gross_interest
  timing: at_credit
```

`2800` basis points is 28.00%. Use basis points (not a decimal percentage) so
the rate is an exact integer.

### 3. Declare exemptions and reporting

List the rule's exemption cases — each an `{ id, evidence }` pair naming the
exemption and the evidence that substantiates it — and any linked regulatory
report:

```yaml
  exemptions:
    - { id: pme_leader, evidence: declaration_pme }
    - { id: non_resident_treaty, evidence: rfi_form }
    - { id: jovens_poupanca, evidence: scheme_enrolment }
  reporting:
    modelo_39: { required: true, frequency: annual }
```

> **Branch — declare the canonical set, not just what v1 onboards.** The
> `exemptions` list is the rule's **full** catalogue, matching the canonical
> withholding manifest. A case may be declared here even when the onboarding it
> depends on (e.g. non-resident onboarding) is out of your v1 slice — declaring
> it keeps the pack a complete, auditable statement of the rule.

### 4. Validate locally

Run the offline depth-1–4 validation over the pack's data:

```bash
make pack-validate PACK=pt.2026.1     # use your pack's dirname
```

This `cue vet`s `withholding.yaml` against its schema — catching a malformed
`formula_ref`, a non-integer `rate_basis_points`, a bad `basis`/`timing`, or a
mistyped exemption. It is fully offline (no registry, no Docker).

## Honest local limits

A clean `make pack-validate` covers depths 1–4 only. Be aware:

- **Depth-5 (sealed-corpus engine simulation) does not run locally yet**, so a
  local validate does **not** exercise your rule's actual flow-by-flow
  withholding output. The example pack's depth-5 corpus is wired in CI; locally
  it is a logged skip.
- **`formula_ref` is an engine promise.** The pack tooling cannot confirm the
  engine implements `engine.withholding.percentage`; only the engine can, at
  pack load.
- **Signing and a production registry are not wired for you** — do not treat a
  locally built pack as published.

## Related

- [Add a day-count primitive](./add-a-day-count-primitive.md) — the same
  `formula_ref` + validate-locally pattern, for accrual conventions
- [Add a regulatory reporting hook](./add-a-reporting-hook.md) — Modelo 39 is a
  withholding report; the reporting hooks that activate it live in
  `primitives/reporting.yaml`
- [Financial mathematics §5.4](../../product-management/financial_concepts/banking_products_financial_mathematics.md)
  — TANB/TANL and why withholding is flow-by-flow
- [The pack-format reference](../reference/pack-format/README.md) — the
  authoritative, generated field list
- [Product-docs home](../README.md)
