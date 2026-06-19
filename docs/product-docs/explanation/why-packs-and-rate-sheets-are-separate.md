# Why packs and rate sheets are separate artifacts

This page explains *why* babelstone splits its configuration into separate artifacts
with separate deploy paths, rather than putting everything in one place. It is
background reading, not a procedure — if you came here to actually change a number,
the how-tos are linked at the end. The aim is that after reading this you understand
why a weekly rate move and a once-a-year regulatory change live in different files,
owned by different people, on different cadences, and why that is a feature and not an
accident.

## The configuration surface is three families, not one

Everything you configure in babelstone falls into one of **three artifact families**.
They are deliberately kept apart because they change for different reasons, at
different speeds, and need sign-off from different people. The authoritative table
(with the exact ownership and approver shapes) lives in the engine's design notes:
see [the configuration-surface design][surface] §1. In short:

| Family | What it specifies | Who owns it | How often it changes | Who signs off |
|---|---|---|---|---|
| **Product config** | The *structure* of a product — cash-flow shape, day-count choice, compounding, charges, which rate-sheet role to read | Product team | Days–weeks (per product variant) | Product + Compliance |
| **Rate sheet** | The *numbers* — TANs indexed by product, role, and principal band | Treasury / ALM | Daily–weekly | Treasury sign-off |
| **Pack** | The *jurisdiction vocabulary* — primitives, parameters, and bounds (e.g. Act/360, the 28% withholding rate, the rate ceiling) | Engine team + regulatory counsel | Per regulatory change (months–years) | Engine release + the operating bank pinning the version |

As a config author you will mostly touch two of these. You author **packs** (the
`pt.2026.1` worked example is the one in the repo) and you publish **rate sheets**
(the numbers that move every week). The product config sits between them, composing a
structure that says, in effect, *"take the rate from the live sheet for the
`new_money` role, accrue under the `act_360` primitive from the `pt` pack, and apply
the pack's `irs_juros` withholding at credit time."* The rate sheet supplies the
value; the pack supplies the primitive and its parameters; the product config wires
them together.

## The load-bearing principle: the cheapest change moves through the cheapest approval

The single rule that explains the whole split is this:

> The cheapest change must move through the cheapest approval.

A weekly price tweak and a structural product redesign are not the same *kind* of
change. One is a Tuesday-afternoon treasury decision; the other is a product launch
that compliance reviews. If they share an approval gate, the cheap change inherits the
expensive change's ceremony. The promise that *"a new product is just a configuration
change"* only holds if the configuration surface is layered so that small, frequent
changes do not pay the cost of large, rare ones.

This is why the families are split by *cadence and approver*, not by some tidy
technical taxonomy. The boundary is drawn where the approval cost changes.

## The alternative, and why it fails

The obvious simplification is to put the rate numbers directly in the product config.
One artifact, one deploy, one mental model. It is tempting precisely because it looks
simpler.

It fails on the first promotional campaign. If the TANs live inside the product
config, then **every weekly rate move becomes a product-config deploy** — and a
product-config deploy is gated by Product *and* Compliance, because that gate exists to
review structural and regulatory changes. So a treasury analyst who wants to bump the
12-month new-money rate by 25 basis points on Monday now has to route a "product
change" through a redesign-grade approval. Do that every week, during a campaign maybe
every day, and the approval queue becomes the bottleneck. The agility the engine was
supposed to deliver dies, and it dies on exactly the high-tempo activity — promotional
pricing — that was supposed to showcase it. The design notes put it bluntly: the
agility wedge *"dies on the first promotional campaign."* See [surface][surface] §2.1.

So the split is not premature abstraction. It is the direct consequence of refusing to
let a weekly change cost a quarterly approval.

## How the pack and the rate sheet actually relate

Here is the part that surprises people: **the rate numbers are not in the pack.** The
pack carries only a *reference* to a rate sheet and a *bound* on what the numbers may
be. The actual numbers live somewhere else entirely, on their own cadence.

In the `pt.2026.1` pack, the rate-sheet ref is a tiny file — it names a sheet, nothing
more (see [`packs/pt.2026.1/rate-sheet-refs/deposits-pt.yaml`][rateref]):

```yaml
# illustrative — the ref names a sheet; it carries no TANs
refs:
  - product_family: term_deposit
    rate_sheet_version_id: pt-deposits-2026.1
```

The pack also carries a *bound* — a ceiling the numbers must respect. In `pt.2026.1`
that is `max_consumer_rate_bps` in [`parameters/constants.yaml`][constants] (an
illustrative 2000 bps = 20%). The pack is saying "rates exist, here is what they're
allowed to be, here is the sheet to look in" — it never says what they *are*.

The rate-sheet **body** — the products → role → principal-band → `tan_basis_points`
map — is not a committed pack file at all. It is an immutable row deployed to a
Postgres `rate_sheets` table through a separate, treasury-gated API
(`POST /v1/rate-sheets`). A short illustrative shape (the authoritative shape lives in
the [config-surface design][surface] §2.2 and the engine's `RateSheetBody`):

```yaml
# illustrative rate-sheet body — deployed via the API, not committed to the pack
products:
  dpz_pt_12m_juros_venc:
    new_money:
      bands:
        - { principal_cents: [50000, null], tan_basis_points: 400 }
```

The decision to store sheets as versioned, immutable Postgres rows behind their own
deploy endpoint — distinct table, distinct URL, distinct approver scope — is recorded
in [ADR-PC-008][adr008]. Read its Context and Decision for the cadence-and-ownership
reasoning; the **DDL and field-by-field shape are normative and live there and in the
[generated reference][ref]** — this page deliberately does not restate them.

This is the whole point of the separation made concrete:

- The pack changes per regulatory event (months to years). It is signed by the engine
  team and counsel, pinned by the operating bank, and frozen onto every instance for
  that instance's life ([ADR-PC-007][adr007] covers the signing and pinning model).
- The rate sheet changes weekly. It rides a faster deploy path with a treasury
  approver, and never touches the pack's slow, heavily-reviewed gate.

A rate sheet carries **no PII** — only prices, bands, and roles — and is forward-only:
once published it is never edited; a correction is a new version with a new
`effective_from`. That immutability is what lets a published rate stand as evidence of
"what we offered, and when."

## Either order, never disagreement: the symmetric validator

If the pack (with its ref) and the rate-sheet body deploy independently, what stops
them from contradicting each other — a config that asks for a `new_money` rate the
sheet doesn't price, or a sheet that quotes a product no active config uses?

The answer is a **symmetric validator invariant**. The two artifacts may deploy in
*either order*, but the engine never accepts a *state* where they disagree:

- When a **rate sheet** deploys, it is checked against the active configs: every
  product it prices must exist, every `(product, role, principal)` an active config
  asks for must be covered with non-overlapping, exhaustive bands, and every TAN must
  sit within the pack's declared bound.
- When a **product config** deploys, if it references a `rate_ref`, the active sheet
  must already cover it — a config asking for a role the sheet doesn't have is rejected
  at deploy time, not discovered later at the first deposit.

Either side, deployed second, is rejected if it would create a gap. The
contradiction is caught at deploy, never in production. The full invariant set is in
[surface][surface] §2.5 and the rejection semantics in [ADR-PC-008][adr008] §P2/§P3.

Crucially the rate is resolved and *pinned* at constitution: when a deposit is
created, the engine reads the sheet active on that date, resolves the
`(product, role, principal_band)` to a concrete `tan_basis_points`, and stamps both
the resolved value and the `rate_sheet_version_id` onto the event. From then on the
deposit pays that rate for its whole life — a later sheet does not re-price a live
deposit, because re-pricing a customer is a commercial act, not an operational one.
That is the same per-instance pinning logic the pack uses, applied to prices.

## Where this honestly does not work yet

This page describes the design as decided; some of the local enforcement is still
being built, and it would be misleading to imply otherwise:

- The **cross-artefact rate-sheet invariants** above ("every product exists in an
  active config", "every active config's `rate_ref` is covered") need a product-config
  registry that is a later epic. They are *designed*, not yet enforced end-to-end.
- The pack-declared rate bound is, for now, an interim static `max=2000` rather than a
  value the validator pulls from the loaded pack.
- The treasury-scope authorization on `POST /v1/rate-sheets` is the edge gateway's job,
  not something the engine host enforces itself ([ADR-PC-008][adr008] §P4 and the
  2026-05-30 amendment are explicit about this).
- The engine-side pack loader/verifier is still pending, so the "engine rejects an
  unsigned or wrong-bound pack at load" guarantee is a design commitment, not yet a
  thing you can observe locally.

None of these gaps change the *shape* of the separation — they are work remaining
under it, not reasons to doubt it.

## Why this matters to you as a config author

The split is the reason your day-to-day rate work is fast and your regulatory work is
careful, and the two never block each other:

- A weekly TAN change is a rate-sheet deploy with treasury sign-off. It does not
  reopen the pack and does not summon Compliance.
- A regulatory change — a new withholding rate, a new day-count rule — is a pack
  change, reviewed and signed once, then pinned. It does not force you to redeploy
  prices.
- Because the pack carries only refs and bounds, you can reason about "what is allowed"
  (the pack) separately from "what we are charging this week" (the sheet).

If you keep the principle *"the cheapest change moves through the cheapest approval"*
in mind, every boundary in the configuration surface will make sense.

## Where to go next

- To actually change a number in a rate sheet: [How to add a rate band][howto-band].
- To add a day-count or other primitive to a pack: [How to add a day-count
  primitive][howto-primitive].
- For the reasoning behind storing sheets as immutable Postgres rows on a separate
  deploy path: [ADR-PC-008][adr008].
- For pack format, signing, and per-instance pinning: [ADR-PC-007][adr007].
- For the design narrative behind the three-family split and the worked YAML:
  [the configuration-surface design][surface].
- For the front door to this documentation set: [product-docs README][home].

[surface]: ../../product-management/product_concepts/feature-design-configuration-surface.md
[adr007]: ../../product-management/product_concepts/adrs/ADR-PC-007-signed-yaml-oci-pack.md
[adr008]: ../../product-management/product_concepts/adrs/ADR-PC-008-rate-sheet-storage-and-deploy-api.md
[ref]: ../reference/README.md
[rateref]: ../../../packs/pt.2026.1/rate-sheet-refs/deposits-pt.yaml
[constants]: ../../../packs/pt.2026.1/parameters/constants.yaml
[howto-band]: ../how-to/add-a-rate-band.md
[howto-primitive]: ../how-to/add-a-day-count-primitive.md
[home]: ../README.md
