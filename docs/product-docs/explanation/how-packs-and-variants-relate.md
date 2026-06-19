# How packs and variants relate

You have authored a pack and watched a variant validate against it. This page
explains the relationship between the two: what a variant *is*, how it composes
with a pack, and why the pack gets the final say when validation reaches depth
4. It is background reading, not a procedure — the how-tos quietly assume this
relationship, and this is where it is made explicit.

It sits alongside two neighbours and does not repeat them:
[why packs and rate sheets are separate](./why-packs-and-rate-sheets-are-separate.md)
explains the pack-vs-rate-sheet split; [reading a CUE schema](./reading-a-cue-schema.md)
explains how the schema mechanics work. This page is about the third edge — pack
and **variant**.

## The variant is the product; the pack is the law

The configuration surface is three artifact families (the full table, with
ownership and cadence, is in [why packs and rate sheets are
separate](./why-packs-and-rate-sheets-are-separate.md#the-configuration-surface-is-three-families-not-one)
and normatively in [the configuration-surface design][surface]). Two of them
are the subject here:

- A **pack** (`pt.2026.1`) is the *jurisdiction's regulatory vocabulary*:
  primitives (day-counts, withholding), parameters, bounds, and the regulatory
  **permitted-sets** that say which families may use what. It changes per
  regulatory event — months to years — signed by the engine team and counsel.
- A **variant** is *one configured product* — a specific term deposit a bank
  sells (a 12-month, quarterly-interest, Act/360 deposit). It changes on the
  product team's cadence — days to weeks.

The pack is the envelope of what is *allowed*; the variant is one concrete thing
*offered* inside that envelope. Many variants can live under one pack.

## A variant names; the pack supplies

The thing that makes the relationship work — and the thing newcomers miss — is
that **a variant carries references, not values.** Look at the `day_count` field
of any variant:

```yaml
# illustrative — the field names a primitive; it does not define one
day_count: pt.act_360
```

That is not "the Act/360 formula." It is a *pack-bound reference*: the variant
names a primitive (`act_360`) in a pack namespace (`pt`), and the pinned pack
supplies what it means. The same pattern holds for the rate — a variant carries
a `rate_ref` naming a sheet role, never a number (numbers live on the rate
sheet's fast cadence; see [why packs and rate sheets are
separate](./why-packs-and-rate-sheets-are-separate.md)).

This keeps the variant **thin**: it is a composition of pack primitives and
rate-sheet references, plus its own structural choices (term, interest variant,
early-termination schedule). The *meaning* of each reference is supplied once,
by the pack, and shared by every variant that names it — never copied into each
product. (How the schema declares a field as pack-bound — the
`#PackBoundPrimitive` shape — is in [reading a CUE
schema](./reading-a-cue-schema.md); the rendered field-by-field shape is the
generated [term-deposit family-schema reference][familyref]. This page does not
restate either.)

## One variant pins exactly one pack

A variant does not float above "whatever pack is current." It **pins** the pack
and family-schema version it was authored against, in two fields:

```yaml
# illustrative — the pins that bind a variant to its governing law
schema: term_deposit@2026.1   # the family-schema version
pack:   pt.2026.1             # the regulatory pack version
```

This pin is what makes the question *"which rules govern this variant?"*
answerable with a single, unambiguous answer. It is also why a depth-3 rejection
exists at all: validate a variant that pins `pt.2099.1` against `pt.2026.1` and
the validator refuses, because you have asked two different laws to judge one
product. The pinning model — and why an instance keeps its pinned versions for
life even after newer ones ship — is normative in
[ADR-PC-009][adr009].

## The pack judges the variant: that is what depth 4 *is*

Validation runs in depths, and the relationship between pack and variant is
exactly the boundary between the early depths and the last one:

- **Depths 1–3** ask: is this variant *well-formed*, and do its references
  *resolve* against the pinned pack? (Shape, types, primitive resolution, pack
  compliance.)
- **Depth 4** asks: does the pack's *regulatory law* permit this well-formed
  variant for this family?

A variant can pass 1–3 completely — perfect shape, every reference resolving —
and still be refused at depth 4, because the pack carries a convention but does
not *permit* it here. PT retail deposits must use Act/360, so a variant naming
the (carried, resolvable) `pt.act_365` is rejected:

```
  ✗ depth 4  day_count  [forbidden_day_count]  day-count "act_365" is not regulatorily permitted for a PT term_deposit (pack pt.2026.1 permits: act_360)
```

That rejection is the pack judging the variant. The rule lives in the pack's
`permitted_for` declaration — auditor-visible YAML — not in the validator's
code, and not in the variant. This is the single most load-bearing consequence
of the pack/variant split, and it is why a forbidden-day-count fix is *usually*
a variant change (comply with the law), while changing what the pack *permits*
is a regulatory change on the pack's slow gate. (The diagnosis side is [how to
troubleshoot a variant rejection](../how-to/troubleshoot-a-variant-rejection.md);
the authoring side is [how to add a day-count
primitive](../how-to/add-a-day-count-primitive.md); the depth definitions are
normative in [ADR-PC-006][adr006].)

## Pinning travels onto the instance

The relationship does not end at validation. When a deposit is *constituted*,
the engine freezes the governing context onto the instance and never moves it:
the **variant's structure**, the **pinned pack and schema versions**, and the
**resolved rate** (the concrete TAN plus its `rate_sheet_version_id`) are all
stamped onto the constitution event. From then on the instance runs under that
frozen context for its whole life — a later pack, a fine-drift schema split, or
a new rate sheet does not retroactively re-govern a live deposit.

This is the same per-instance pinning that [why packs and rate sheets are
separate](./why-packs-and-rate-sheets-are-separate.md#how-the-pack-and-the-rate-sheet-actually-relate)
describes for *rates*, now stated for the *pack and schema*: pricing is pinned
because re-pricing a customer is a commercial act; the governing law is pinned
because changing the rules under a running contract would be worse. An
instance's full governing context is answerable from its own events alone — the
design rationale is in [ADR-PC-009][adr009].

## Why it is built this way

Separating the fast-moving product (variant) from the slow-moving law (pack)
buys two things at once:

- **A product change does not reopen the regulatory envelope.** Launching a new
  variant is a product-team change validated against the existing pack; it does
  not summon counsel or re-sign the pack.
- **The law is single-sourced.** The pack's regulatory rules apply uniformly to
  every variant without being copied into each one, so there is no per-product
  drift of "what is allowed" — and an auditor reads the rule in exactly one
  place.

That is the promise *"a new product is just a configuration change"* made
concrete: the variant is the configuration, the pack is the law it is checked
against, and the two never have to move together.

## Where this honestly does not work yet

- **Variants have no committed home in the repo.** Packs live under `packs/`,
  but product variants (product configs) get a registry in a later epic, so
  today a variant is authored and validated as a standalone file. The
  cross-artifact checks that need that registry (every product a rate sheet
  prices exists in an active config, and the reverse) are *designed*, not yet
  enforced — see [why packs and rate sheets are
  separate](./why-packs-and-rate-sheets-are-separate.md#either-order-never-disagreement-the-symmetric-validator).
- **The engine-side pack loader/verifier is pending.** The "instance freezes its
  pinned pack at constitution" guarantee is a design commitment
  ([ADR-PC-009][adr009], [ADR-PC-007][adr007]) you cannot yet observe end-to-end
  locally; what you *can* run today is the offline depths-1–4 variant validation.

Neither gap changes the *shape* of the relationship — they are work remaining
under it.

## Where to go next

- To build one: [Tutorial: write your first variant](../tutorials/write-your-first-variant.md).
- When validation rejects one: [How to troubleshoot a variant rejection](../how-to/troubleshoot-a-variant-rejection.md).
- The pack side of a `day_count` reference: [How to add a day-count primitive](../how-to/add-a-day-count-primitive.md).
- The neighbouring splits: [why packs and rate sheets are separate](./why-packs-and-rate-sheets-are-separate.md)
  and [reading a CUE schema](./reading-a-cue-schema.md).
- Normative sources: the [configuration-surface design][surface] and
  [configuration-authoring design][authoring]; [ADR-PC-006][adr006] (validation
  depths), [ADR-PC-009][adr009] (per-instance pinning), [ADR-PC-007][adr007]
  (pack signing and pinning).
- Back to the [product-docs front door](../README.md).

[surface]: ../../product-management/product_concepts/feature-design-configuration-surface.md
[authoring]: ../../product-management/product_concepts/feature-design-configuration-authoring.md
[familyref]: ../reference/family-schemas/term-deposit.md
[adr006]: ../../product-management/product_concepts/adrs/ADR-PC-006-cue-schema-language.md
[adr007]: ../../product-management/product_concepts/adrs/ADR-PC-007-signed-yaml-oci-pack.md
[adr009]: ../../product-management/product_concepts/adrs/ADR-PC-009-per-instance-version-pinning.md
