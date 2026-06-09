# How to add a day-count primitive to a pack

This guide walks you through adding a **day-count convention** to a pack's
`primitives/day-count.yaml` — the file that declares which interest-accrual
conventions exist in your jurisdiction's vocabulary and, crucially, *which
product families are regulatorily allowed to use each one*.

You will: add the entry, point it at an engine formula, declare its
permitted-set, and validate locally. The worked example throughout is the
PT term-deposit pack, [`pt.2026.1`](../../../packs/pt.2026.1/).

**Before you start, know this:** `permitted_for` is **pack-declared regulatory
law**, not a validator default. It is the lever that makes the engine reject a
day-count for a product family. Get it wrong and you either lock a convention
out (empty set) or wave through one your regulator forbids. The *why* behind
this design lives in [reading a CUE schema](../explanation/reading-a-cue-schema.md)
and [ADR-PC-006 §4](../../product-management/product_concepts/adrs/ADR-PC-006-cue-schema-language.md);
this page is the *how*.

## What a day-count entry looks like

In `primitives/day-count.yaml` each convention is one map entry, keyed by the
**reference id** a variant names as `day_count: <namespace>.<id>` (e.g.
`pt.act_360`). Here is the shape, as a short illustration — not the
authoritative field list:

```yaml
# packs/pt.2026.1/primitives/day-count.yaml
act_360:
  formula_ref: engine.day_count.actual_360   # bridge to the engine primitive
  permitted_for: [term_deposit]              # families allowed to use it
```

The authoritative field-by-field shape (`#DayCounts`) is the
[`pack.cue`](../../../contracts/cue/pack/pack.cue) schema, rendered in the
generated [pack-format reference](../../product-management/reference/pack-format/README.md).
Do not copy a field table from elsewhere — link to those and you will never go
stale.

The two fields you set:

- **`formula_ref`** — a dotted reference (`engine.<…>`) naming an
  engine-implemented accrual primitive. This is a *bridge*: the pack names the
  formula, the engine supplies the math.
- **`permitted_for`** — the list of product-family ids
  (e.g. `term_deposit`) that may regulatorily use this convention. An empty
  list `[]` means "carried, but no family may use it."

## Which primitives does the engine implement?

`formula_ref` is the one field the pack tooling **cannot check for you**.
`make pack-validate` confirms the string is well-formed and (for a variant) that
the *pack* carries the day-count; it does **not** confirm the engine actually
implements the formula. That binding is resolved only when the engine loads the
pack — an unimplemented `formula_ref` throws there, never at validate time.

So the authoritative list of implemented day-count primitives lives in **engine
source**, in two places:

- [`Babelstone.Packs/VerifiedPack.cs`](../../../engine/src/Babelstone.Packs/VerifiedPack.cs)
  — `PackDayCount.ToConvention()` is the `formula_ref → convention` bridge: a
  `switch` whose cases are exactly the `formula_ref` strings the engine accepts.
  Anything not in it throws (*"…has no engine convention; refusing to default
  silently"*).
- [`Babelstone.FinancialMath/DayCount.cs`](../../../engine/src/Babelstone.FinancialMath/DayCount.cs)
  — the `DayCountConvention` enum and the math behind each.

Today that switch accepts exactly three:

| `formula_ref` | Convention |
|---|---|
| `engine.day_count.actual_360` | Actual/360 |
| `engine.day_count.actual_365` | Actual/365 |
| `engine.day_count.thirty_360_european` | 30E/360 (European) |

> **The trap, stated as a hypothetical.** Suppose you declared
> `act_act_isda → engine.day_count.actual_actual_isda` in a pack. The string is
> well-formed, so it passes `make pack-validate` — but that `formula_ref` is
> **not** a case in `ToConvention()`, so the entry would throw at engine load
> ("…has no engine convention; refusing to default silently"). `make
> pack-validate` cannot catch this; only the engine can. This is precisely why
> you confirm a `formula_ref` against the *engine*, not against the pack. (No
> shipped pack carries such a dead entry — a guard test, `PackDeclarationsResolveTests`,
> asserts every declared `formula_ref` resolves, so a future one fails CI.)

> **Gap, stated honestly.** There is no *generated* catalogue of engine
> primitives yet — unlike events, family schemas, and the pack-format, the
> implemented-primitive set is not rendered into the
> [generated reference](../../product-management/reference/README.md). Until a
> renderer emits it, the `ToConvention()` switch above is the source of truth.
> Other `formula_ref` primitives (e.g. withholding) follow the same pattern and
> the same gap.

## Steps

### 1. Add the entry

Open `primitives/day-count.yaml` in the pack you are editing and add a new
top-level key for the reference id. Use lowercase `snake_case`; quote a key
that starts with a digit (note `"30_360_european"` in the example pack):

```yaml
act_act_icma:
  formula_ref: ...      # set in step 2
  permitted_for: ...    # set in step 3
```

Remember a pack is **immutable once published** — if `pt.2026.1` is already
released, your edit belongs in a new pack version (`pt.2026.2` / `pt.2027.1`),
never an in-place change to a published one.

### 2. Set `formula_ref`

Point `formula_ref` at the engine primitive that implements this convention,
in the form `engine.day_count.<name>`:

```yaml
act_act_icma:
  formula_ref: engine.day_count.actual_actual_icma
```

The example pack declares three day-count keys, and the engine today implements
exactly those three — `engine.day_count.actual_360`, `actual_365`, and
`thirty_360_european`. If you were to declare a fourth keyed at a `formula_ref`
the engine does **not** implement (say `engine.day_count.actual_actual_isda`),
the pack would still *validate* locally but be **rejected at engine load** — the
trap in the branch below. Always confirm a `formula_ref` against the engine —
see [Which primitives does the engine implement?](#which-primitives-does-the-engine-implement)
above.

> **Branch — the engine must actually implement it.** `formula_ref` is a
> promise the *engine* keeps, not the pack. If you name a formula the engine
> in the pack's `dependencies.engine_compatible_versions` range does not
> implement, the pack still *validates* locally (the string is well-formed),
> but it is **rejected at deploy/load**. Use a `formula_ref` only after you
> have confirmed the engine ships that primitive.

### 3. Set `permitted_for` — the regulatory lever

List every product family regulatorily allowed to use this convention. This is
where the regulatory rule is written, in plain YAML, where an auditor can
`cat` and `diff` it.

In `pt.2026.1` this is exactly why only one convention is open to deposits:

```yaml
act_360:
  formula_ref: engine.day_count.actual_360
  permitted_for: [term_deposit]      # PT retail deposits require Act/360
act_365:
  formula_ref: engine.day_count.actual_365
  permitted_for: []                  # carried, but forbidden for deposits
```

PT retail term deposits require Act/360, so only `act_360` lists
`term_deposit`. The other two conventions (`act_365` and `30_360_european`)
carry `permitted_for: []`. A variant *can still name* one of
them — it parses, and depths 1–2 pass — but **depth-4 (regulatory coherence)
rejects it for a term-deposit variant**, emitting a diagnostic like:

```
day-count "act_365" is not regulatorily permitted for a PT term_deposit
(pack pt.2026.1 permits: act_360)
```

> **Branch — leave `permitted_for` empty and no variant can use it.** An empty
> list is a deliberate "carried but forbidden" state, not a default to fill in
> later. If you intend a family to use this convention, you **must** add that
> family's id to the list, or every variant naming it fails depth-4.

The rationale for putting this rule *in the pack* (rather than hardcoding it in
the validator, as an earlier design did) — and the meaning of the five
validation depths — is in
[reading a CUE schema](../explanation/reading-a-cue-schema.md) and
[ADR-PC-006](../../product-management/product_concepts/adrs/ADR-PC-006-cue-schema-language.md).

### 4. Validate locally

Run the offline depth-1–4 validation over the pack's data:

```bash
make pack-validate PACK=pt.2026.1     # use your pack's dirname
```

This runs `packs/pack.sh validate`, which `cue vet`s `day-count.yaml` against
the `#DayCounts` schema — catching a malformed `formula_ref`, a bad key, or a
mistyped `permitted_for`. It is fully offline (no registry, no Docker).

To confirm the **permitted-set actually bites**, validate a *variant* that
names your day-count against the pack — this is the path that exercises
depth-4:

```bash
make validate-variant VARIANT=path/to/variant.yaml PACK=pt.2026.1
```

A variant naming a `permitted_for: []` convention will fail here with the
`forbidden day-count` diagnostic above; one naming a permitted convention
passes. Two fixture sets show the method:

- The CUE schema fixtures under
  [`contracts/cue/testdata/term-deposit/`](../../../contracts/cue/testdata/term-deposit/)
  — e.g. `invalid/unbound-day-count.yaml`, a free-string `day_count: "Act/365"`
  rejected at **depth 1** (it is not a pack-bound reference at all — a
  *binding-shape* failure).
- The validator fixtures under
  [`pack-validate/testdata/term-deposit/`](../../../pack-validate/testdata/term-deposit/)
  — e.g. `invalid/depth4-act365-deposit.yaml`, a *bound* `pt.act_365` the pack
  carries (depth 2 passes) but PT regulation forbids for a deposit, so **depth
  4** rejects it. That is the permitted-set rejection this page is about, and it
  is exercised by `make validate-variant`.

## Honest local limits

A clean `make pack-validate` covers depths 1–4 only. Be aware:

- **Depth-5 (sealed-corpus engine simulation) does not run locally yet.** The
  example pack's `expected-events.yaml` is an intentional empty placeholder, so
  depth-5 is reported as a *logged skip*, not a pass. It does not exercise your
  new convention's accrual output.
- **Signing and a production registry are not wired for you.** Keyless OIDC
  cosign signing in CI is pending; local signing needs your own `COSIGN_KEY`,
  and only an unauthenticated local OCI registry (`localhost:5001`) is
  documented. Do not treat a locally built pack as published.
- **The engine-side pack loader/verifier is pending** — the "rejected at
  deploy" branch in step 2 is the *designed* behaviour
  ([ADR-PC-007](../../product-management/product_concepts/adrs/ADR-PC-007-signed-yaml-oci-pack.md)),
  not yet a thing you can observe end-to-end locally.

## Related

- [Add a rate band](../how-to/add-a-rate-band.md) — the rate-sheet side, which
  is *not* a pack file (see
  [ADR-PC-008](../../product-management/product_concepts/adrs/ADR-PC-008-rate-sheet-storage-and-deploy-api.md))
- [Reading a CUE schema](../explanation/reading-a-cue-schema.md) — what the
  five depths mean and why `permitted_for` lives in the pack
- [The three-artefact configuration surface](../../product-management/product_concepts/feature-design-configuration-surface.md)
  — packs vs rate sheets vs variants, and where day-counts sit
- [Product-docs home](../README.md)
