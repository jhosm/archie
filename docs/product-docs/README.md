# babelstone product documentation

This is the documentation for **people using babelstone** — starting with the **config author**: the product manager, treasury/ALM, or compliance owner at an adopting bank who writes regulatory **packs** (`packs/pt.YYYY.N/`) and deploys **rate sheets**. You write YAML data and run validation; you do not work on the engine's source.

It is deliberately separate from the internal design corpus under [`../product-management/`](https://github.com/jhosm/babelstone/tree/main/docs/product-management), which is the engine team's concern-axis series and decision records (ADRs). That corpus answers *"what did we decide and why"*; this set answers *"how do I get my pack right."*

It is organised by the [Diátaxis](https://diataxis.fr) framework.

## The three quadrants here

| Quadrant | For | When you reach for it |
|---|---|---|
| [tutorials/](./tutorials/author-your-first-pack.md) | learning | Zero to a first validated pack. Hold-your-hand, single happy path. |
| [how-to/](./how-to/validate-a-pack-locally.md) | doing | You already know the shape; you need the recipe for one task. |
| [explanation/](./explanation/why-packs-and-rate-sheets-are-separate.md) | understanding | The *why* behind the model — why packs and rate sheets live apart, how to read a CUE schema. |

## Where is the reference quadrant?

There is no `reference/` quadrant **in this set, on purpose.** babelstone's reference is **generated from the source of truth and diffed in CI**, so it cannot drift. It lives at the canonical home:

➡️ **[The generated reference](../product-management/reference/README.md)** — including the [pack manifest format](../product-management/reference/pack-format/README.md), [family schemas](../product-management/reference/family-schemas/README.md), [event payloads](../product-management/reference/events/README.md), and the [glossary](../product-management/reference/glossary.md).

These pages follow the **link-don't-restate** discipline: the authoritative field-by-field truth (manifest fields, CUE field lists, event payloads, the pack-format schema) is **never retyped here** — we link to the generated source instead. Restating it would create a stale-able duplicate, the exact failure [ADR-PC-022](../product-management/product_concepts/adrs/ADR-PC-022-product-documentation-architecture.md) forbids. A page here may show a short YAML snippet *as an illustration*, but for the contract it points you out.

## One persona today, more pages later

The config author is the **first** reader, not the only one. Family-schema authors and variant authors arrive later — and when they do, they get **more pages in these same three quadrants**, never a parallel persona tree. The structure is doc-type-shaped, not role-shaped, so it grows by adding pages.

## The pages that exist now

This is a **proof-of-concept slice**: just the pack + rate-sheet authoring workflow for the worked example (`pt.2026.1`), not the full product documentation.

**Tutorials**
- [Author your first pack](./tutorials/author-your-first-pack.md)

**How-to**
- [Add a rate band](./how-to/add-a-rate-band.md)
- [Add a day-count primitive](./how-to/add-a-day-count-primitive.md)
- [Validate a pack locally](./how-to/validate-a-pack-locally.md)

**Explanation**
- [Why packs and rate sheets are separate](./explanation/why-packs-and-rate-sheets-are-separate.md)
- [Reading a CUE schema](./explanation/reading-a-cue-schema.md)
