# Product catalogue

**For the product owner deciding *what to offer*.** Before you author a pack
or a variant, you have a prior question: *what can this product family even
be?* What can you vary, and what is fixed for you? This quadrant answers that —
one readable page per product family, written as a menu of business decisions,
not as a schema.

It is for the same reader as the rest of this set — the product manager,
treasury/ALM, or compliance owner at an adopting bank (see
[the set overview](../README.md)) — but at an earlier moment: **choosing the
shape of an offering**, before you sit down to write the YAML that realises it.

## Why this is a separate quadrant

The four [Diátaxis](https://diataxis.fr) quadrants answer *learn* (tutorials),
*do* (how-to), *understand* (explanation), and *look up* (reference). A
"what can I offer?" catalogue is none of those cleanly:

- It is **not the reference.** The [generated family schema](../reference/family-schemas/term-deposit.md)
  is the drift-proof, field-by-field contract — authoritative, but written for
  the machine and the engineer, organised by field. This catalogue is written
  for the product owner, organised by *decision*, and reads in plain business
  language.
- It is **not explanation.** Explanation gives you the *why* behind the model.
  The catalogue gives you the *what you can choose* — the menu itself.

So it earns its own slot. It is **hand-written and readable-first**: where a
precise contract exists, the catalogue links to it rather than restating it
(the same [link-don't-restate](../README.md) discipline the whole set follows,
and that [ADR-PC-022](../../product-management/product_concepts/adrs/ADR-PC-022-product-documentation-architecture.md)
requires). A catalogue page may simplify for readability and lags the schema by
a small, deliberate margin; when you need the exact, current contract, follow
the link to the generated reference.

## What's here

- [Term deposit — the product menu](./term-deposit.md) — what a *depósito a
  prazo* can be: the decisions you make, the options for each, and the rules
  that are fixed for you.

More families get a page here as the engine grows; term deposit is the first.
