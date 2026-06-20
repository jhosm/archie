# How to author the family CUE schema (without learning CUE)

This guide walks you through writing the **CUE family schema** for a new product
family — the closed contract every variant YAML of that family must satisfy. As a
family author you *write* this schema (config authors only ever read it), but you
do not need to learn CUE as a language: the reference family gives you every
shape you need, and your job is to compose them and swap the names.

You will add `contracts/cue/families/<family>.cue`, make it a closed definition,
declare the variant's fields, add accept/reject fixtures, and verify. The worked
example throughout is the real
[`contracts/cue/families/term-deposit.cue`](../../../contracts/cue/families/term-deposit.cue),
rendered in the generated
[family-schema reference](../reference/family-schemas/term-deposit.md).

**Before you start, know this:** this schema is the **depth-1 gate** on every
variant of your family — it decides which configured products are even
well-formed. It is *closed*: a field it does not declare is a hard error, not a
silent pass. So the schema is also the family's promise that "no config can
smuggle behaviour the engine doesn't model." Author it as a contract, not a
suggestion.

> **You are the author here — that is the difference from the config author.**
> The config-author set has a page on
> [reading a CUE schema](../explanation/reading-a-cue-schema.md); that reader
> *consumes* the schema you write. This page is the authoring side. The `.cue`
> files are owned by engineering on a quarterly cadence — and as a family author,
> you are that owner.

---

## Where the file goes, and how it is named

The family schema lives at `contracts/cue/families/<family-kebab>.cue` —
**kebab-case**, matching the .NET project directory, *not* the snake_case bus
name. So the `term_deposit` family's schema is `families/term-deposit.cue`.

It shares one package with the cross-family vocabulary in
[`contracts/cue/common.cue`](../../../contracts/cue/common.cue): the version-key
shapes, the bounded scalar types (`#Cents`, `#BasisPoints`), the pack-binding
declaration shape, and the rate-sheet reference shape. You **compose** those into
one closed family contract — you do not redefine them.

---

## Step 1 — Copy the reference family and rename

Open [`families/term-deposit.cue`](../../../contracts/cue/families/term-deposit.cue)
and copy its shape. Every construct you need is already there. The skeleton is a
single closed definition named for your family:

```cue
package family

// #<Family> is a CLOSED definition: a variant carrying a field this schema does
// not declare fails depth 1 (no DSL escape hatch, ADR-PC-006 Decision).
#<Family>: {
    // --- version envelope: every variant pins its schema + pack -----------
    variant_id: #VariantId
    schema:     #SchemaRef   // e.g. <family>@2026.1 — must equal the module's SchemaVersion
    pack:       #PackId      // e.g. pt.2026.1

    // … your family's fields …
}
```

Two things are not optional:

- The definition is a `#Name` (a CUE *definition*), which is **closed** — that is
  what rejects an undeclared field. Never model a family as a plain struct.
- The `schema` pin value (`<family>@YYYY.N`) **must equal** the `SchemaVersion`
  your family module exports in C#
  ([`TermDepositFamilyModule.cs`](../../../families/term-deposit/src/Babelstone.Families.TermDeposit/TermDepositFamilyModule.cs):
  `SchemaVersion => "term_deposit@2026.1";`). The schema and the code must agree
  on the family-schema version.

---

## Step 2 — Declare fields by composing, not by writing rules

You almost never write a raw constraint. You declare a field as one of the
vocabulary shapes from `common.cue` or a small closed sub-definition. The whole
table of useful shapes, copied from the reference family:

| You want a field that is… | Declare it as | Reference-family example |
|---|---|---|
| a money amount (cents) | `#Cents` | `min_cents: #Cents` |
| a rate / share in basis points | `#BasisPoints` | `penalty_basis_points: #BasisPoints` |
| a **pack-supplied primitive** (day-count, …) | `#PackBoundPrimitive` | `day_count: #PackBoundPrimitive` |
| a **rate-sheet reference** (never an inline number) | `#RateRef` | `rate: { flat: rate_ref: #RateRef }` |
| a closed set of allowed strings | a disjunction | `interest_variant: "AT_MATURITY" \| "PERIODIC" \| "ADVANCE"` |
| a positive integer | `int & >0` | `term_days: int & >0` |
| an optional field | `field?: T` | `effective_from?: …` |

The single most important habit: **a variant carries references, not values.**
A day-count is a `#PackBoundPrimitive` (the variant names `pt.act_360`; the pack
supplies the meaning), and a rate is a `#RateRef` (the variant names a sheet
role; the rate sheet supplies the number). The schema *forbids* an inline formula
or an inline rate — that keeps the variant thin and the pack/rate-sheet the
source of truth. (The reader's-eye view of these shapes is in
[reading a CUE schema](../explanation/reading-a-cue-schema.md); how packs supply
what a reference names is [how packs and variants
relate](../explanation/how-packs-and-variants-relate.md).)

---

## Step 3 — Comment the *why* above each constraint, then the constraint

The reference family comments the **why** above every field, in plain prose, and
the constraint below it is the enforcement. This is not decoration — config
authors read these comments to understand a rejection, and the generated
reference renders them. Follow the same discipline:

```cue
// Pack-bound: PT retail deposits use Act/360 (02 §2.2). The schema only declares
// the binding; depth-4 regulatory coherence (in the pack) is what rejects e.g.
// Act/365 for a PT deposit.
day_count: #PackBoundPrimitive
```

Write the comment so a reader who is *not* you, months from now, knows what the
field means and which validation depth actually enforces the deeper rule. A
constraint with no comment is a constraint nobody downstream can safely read.

---

## Step 4 — Express cross-field invariants declaratively where you can

Some rules relate two fields ("`payment_period_months` is required for `PERIODIC`
and forbidden otherwise"). CUE expresses these declaratively with `if`-guards,
and you should prefer that over leaving the rule to the engine:

```cue
if interest_variant == "PERIODIC" {
    payment_period_months: 1 | 3        // required, and monthly-or-quarterly only
}
if interest_variant != "PERIODIC" {
    payment_period_months?: _|_         // _|_ = "an error if present" — forbidden otherwise
}
```

This is a *presence-given-enum* rule, and the schema catches it at depth 1 — no
engine round-trip needed. Reach for an `if`-guard whenever a field's legality
depends on another field's value.

**Know the line you cannot cross, though.** Some invariants are *not* expressible
element-wise in CUE — for example "the steps must ascend by `from_day`" or "a
numeric bound must be below another that lives in the pack." The reference family
marks these explicitly as **depth-4 obligations** deferred to the Go
`pack-validate` binary, with a comment saying so:

```cue
// Ascending up_to_days order … are a depth-4 obligation — not expressible
// element-wise in CUE, so not enforced here.
banded: [#Band, ...#Band]
```

When you hit a rule CUE can't express, do what the reference does: enforce the
*shape* you can (a non-empty list, a bounded scalar) and leave an explicit
comment that the ordering/coherence rule is a depth-4 check. Do not pretend CUE
enforces something it doesn't.

---

## Step 5 — Add accept/reject fixtures (one broken rule per file)

A schema without fixtures is untested. The reference family ships paired
fixtures under
[`contracts/cue/testdata/term-deposit/`](../../../contracts/cue/testdata/term-deposit/):

- `valid/` — variants the schema **must accept** (e.g. `flat-at-maturity.yaml`).
- `invalid/` — variants the schema **must reject**, each isolating **one broken
  rule**, with a top comment naming the rule it breaks (e.g.
  `period-without-periodic.yaml`, `unknown-field.yaml`,
  `partial-withdrawal-on-advance.yaml`).

Mirror this for your family under
`contracts/cue/testdata/<family-kebab>/{valid,invalid}/`. The **one-broken-rule
-per-file** discipline is what makes a failed validation name exactly one rule —
it is the method working as intended, not an accident. Write one invalid fixture
per constraint you care about, and a comment at the top of each saying which rule
it violates.

---

## Step 6 — Verify

Run the contracts check, which runs `cue fmt` and validates every accept/reject
fixture against the schema:

```sh
mise exec -- make contracts-check
```

A green run means: the schema is well-formed CUE, every `valid/` fixture is
accepted, and every `invalid/` fixture is rejected for its named rule. If an
`invalid/` fixture is *accepted*, your constraint is too loose; if a `valid/` one
is *rejected*, it is too tight. Both are real bugs the fixtures catch before any
config author meets the schema.

The rendered, field-by-field view of your schema then lands in the **generated**
[family-schema reference](../reference/family-schemas/README.md) — do not
hand-write that page; `make docs-gen` renders it from your `.cue` and `make
docs-verify` gates it for drift in CI. The schema source is the truth; the
reference page is its rendering.

---

## Honest limits

- **CUE only owns depths 1–3.** This schema gives you structural validity, type
  ranges, the binding *shape* of a pack reference, and declarative cross-field
  rules — depth 1, and the shape side of 2–3. Whether a named primitive *resolves*
  in the pinned pack (depth 2–3) and whether the pack's *regulatory law permits*
  it (depth 4) need the pack data and the Go `pack-validate` binary. Author the
  CUE for what CUE can hold, and mark the rest as deferred depth-4 obligations
  (Step 4).
- **The schema is not the only artefact for a new family.** The C# events, folds,
  module, lifecycle table, and projections are the
  [`new-family-schema` skill](../../../plugins/babelstone-engine/skills/new-family-schema/SKILL.md)'s
  job (the CUE schema is its Step 8); a new family's *regulatory pack* (primitives,
  parameters, sealed corpus) is the
  [`pack-author` skill](../../../plugins/babelstone-engine/skills/pack-author/SKILL.md)'s.
  This page covers the family-schema `.cue` alone.

## Related

- [Reading a CUE schema](../explanation/reading-a-cue-schema.md) — the
  consumer's-eye view of the schema you author here (the config-author reader).
- [How packs and variants relate](../explanation/how-packs-and-variants-relate.md)
  — why a variant carries references and the pack supplies the meaning.
- [Tutorial: author your first family schema](../tutorials/author-your-first-family-schema.md)
  — the CUE schema in the context of a whole family.
- The full scaffolding procedure (the CUE schema is its Step 8): the
  [`new-family-schema` skill](../../../plugins/babelstone-engine/skills/new-family-schema/SKILL.md).
- Normative source: [ADR-PC-006](../../product-management/product_concepts/adrs/ADR-PC-006-cue-schema-language.md)
  (CUE as the schema language; the closed-definition / no-escape-hatch decision).
- [Product-docs home](../README.md).
</content>
