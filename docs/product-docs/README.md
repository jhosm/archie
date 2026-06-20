# babelstone product documentation

This is the documentation for **people using babelstone**. It serves two readers so far:

- the **config author** — the product manager, treasury/ALM, or compliance owner at an adopting bank who writes regulatory **packs** (`packs/pt.YYYY.N/`), configures **product variants**, and deploys **rate sheets**. You write YAML data and run validation; you do not work on the engine's source.
- the **family-schema author** — a developer defining a new product family in the engine: its event records, pure fold handlers, the `IFamilyModule` binding, the lifecycle legality table, and projections. You write engine-side C# and the family's CUE contract, anchored on the real `term_deposit` reference family.

Both readers share the **same** three hand-authored quadrants (tutorials, how-to, explanation) — the set is doc-type-shaped, not role-shaped, so it grows by adding pages, never a parallel per-persona tree.

It is deliberately separate from the internal design corpus under [`../product-management/`](../product-management/), which is the engine team's concern-axis series and decision records (ADRs). That corpus answers *"what did we decide and why"*; this set answers *"how do I get my pack and variant right."*

It is organised by the [Diátaxis](https://diataxis.fr) framework.

## The four Diátaxis quadrants here

Three are hand-authored (tutorials, how-to, explanation); the fourth — **reference** — is generated from the source of truth and diffed in CI, so it cannot drift.

| Quadrant | For | When you reach for it |
|---|---|---|
| [tutorials/](./tutorials/author-your-first-pack.md) | learning | Zero to a first validated pack. Hold-your-hand, single happy path. |
| [how-to/](./how-to/validate-a-pack-locally.md) | doing | You already know the shape; you need the recipe for one task. |
| [explanation/](./explanation/why-packs-and-rate-sheets-are-separate.md) | understanding | The *why* behind the model — why packs and rate sheets live apart, how to read a CUE schema. |
| [reference/](./reference/README.md) | looking up | The drift-proof, field-level truth — **generated, never hand-written**. |

Alongside the four quadrants sits one companion slot:

| Slot | For | When you reach for it |
|---|---|---|
| [catalogue/](./catalogue/README.md) | choosing | *What can I offer?* The menu of business decisions a product family lets you make — before you author a pack or variant. |

The catalogue is not a Diátaxis quadrant (it is none of learn/do/understand/look-up cleanly — it is a product menu); it is hand-written, readable-first, and links to the generated reference for the exact contract. See [its README](./catalogue/README.md) for why it earns a slot.

## The reference quadrant is generated, not hand-written

The `reference/` quadrant here is **generated from the source of truth and diffed in CI**, so it cannot drift. Do not hand-edit it: `make docs-gen` regenerates it and `make docs-verify` gates it in CI (ADR-PC-022). It lives alongside the other three quadrants:

➡️ **[The generated reference](./reference/README.md)** — including the [pack manifest format](./reference/pack-format/README.md), [family schemas](./reference/family-schemas/README.md), [event payloads](./reference/events/README.md), and the [glossary](./reference/glossary.md).

These pages follow the **link-don't-restate** discipline: the authoritative field-by-field truth (manifest fields, CUE field lists, event payloads, the pack-format schema) is **never retyped here** — we link to the generated source instead. Restating it would create a stale-able duplicate, the exact failure [ADR-PC-022](../product-management/product_concepts/adrs/ADR-PC-022-product-documentation-architecture.md) forbids. A page here may show a short YAML snippet *as an illustration*, but for the contract it points you out.

## Two personas, one structure

The config author was the **first** reader; the **family-schema author** is the second, opened by adding pages to the same three hand-authored quadrants (tutorials, how-to, explanation; reference stays generated), never a parallel persona tree. Further readers (the integrator, the agent channel) arrive the same way. The structure is doc-type-shaped, not role-shaped, so it grows by adding pages.

The family-schema author's pages link heavily into the engine-team corpus and the real `term_deposit` reference family rather than re-authoring the engine's design rationale — so there is no second copy to drift. The authoritative procedure they pair with is the [`new-family-schema`](../../plugins/babelstone-engine/skills/new-family-schema/SKILL.md) and [`new-event`](../../plugins/babelstone-engine/skills/new-event/SKILL.md) skills.

## The pages that exist now

This is an **early slice**: the config author's pack and **variant** authoring
workflows for the worked example (`pt.2026.1`), plus the family-schema author's
core inner loop anchored on the `term_deposit` reference family — not yet the full
product documentation. Pages are grouped by reader within each quadrant.

**Tutorials**

*Config author*
- [Author your first pack](./tutorials/author-your-first-pack.md)
- [Write your first product variant](./tutorials/write-your-first-variant.md)

*Family-schema author*
- [Author your first family schema](./tutorials/author-your-first-family-schema.md)

**How-to**

*Config author*
- [Author and deploy a complete rate-sheet version](./how-to/author-and-deploy-a-rate-sheet.md)
- [Add a rate band](./how-to/add-a-rate-band.md)
- [Add a day-count primitive](./how-to/add-a-day-count-primitive.md)
- [Add a withholding rule](./how-to/add-a-withholding-rule.md)
- [Add a renewal-policy restriction](./how-to/add-a-renewal-policy-restriction.md)
- [Manage FGD deposit-guarantee coverage](./how-to/manage-fgd-coverage.md)
- [Add a regulatory reporting hook](./how-to/add-a-reporting-hook.md)
- [Validate a pack locally](./how-to/validate-a-pack-locally.md)
- [Interpret a validation failure (message decoder)](./how-to/interpret-a-validation-failure.md)
- [Troubleshoot a variant rejection](./how-to/troubleshoot-a-variant-rejection.md)
- [Version and release a pack (the pt.YYYY.N lifecycle)](./how-to/version-and-release-a-pack.md)
- [Sign and publish a pack with cosign and ORAS](./how-to/sign-and-publish-a-pack.md) — **provisional** (publish path partly unbuilt)
- [Write a sealed test corpus for a pack](./how-to/write-a-sealed-test-corpus.md)

*Family-schema author*
- [Structure event payloads](./how-to/structure-event-payloads.md)
- [Write and test pure event handlers (folds)](./how-to/write-and-test-event-handlers.md)
- [Author the family CUE schema](./how-to/author-the-family-cue-schema.md)

**Explanation**

*Config author*
- [Why packs and rate sheets are separate](./explanation/why-packs-and-rate-sheets-are-separate.md)
- [Rate-sheet versioning and point-in-time resolution](./explanation/rate-sheet-versioning-and-resolution.md)
- [Pack effective-date and per-instance pinning](./explanation/pack-effective-date-and-per-instance-pinning.md)
- [Reading a CUE schema](./explanation/reading-a-cue-schema.md)
- [How packs and variants relate](./explanation/how-packs-and-variants-relate.md)

*Family-schema author*
- [The family lifecycle state machine](./explanation/the-family-lifecycle-state-machine.md)

**Catalogue**
- [Term deposit — the product menu](./catalogue/term-deposit.md)
