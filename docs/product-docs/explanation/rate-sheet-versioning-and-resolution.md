# Rate-sheet versioning and point-in-time resolution

This page explains *why* the rate a deposit pays is fixed the moment the deposit
is created and **never moves afterwards** — even when treasury publishes a new
rate sheet next week. It is background reading, not a procedure: if you came here
to deploy a sheet, [author and deploy a rate sheet](../how-to/author-and-deploy-a-rate-sheet.md)
is the task. The aim is that after reading this you understand two things that
surprise newcomers: that a rate sheet is *versioned and immutable* (you never
edit one), and that resolution is *point-in-time* (a later sheet does not
re-price a live deposit). Both fall out of one idea — a published price is
evidence — and once you hold that, the whole versioning model reads as obvious.

This complements [why packs and rate sheets are separate](./why-packs-and-rate-sheets-are-separate.md):
that page explains *why the numbers live apart from the pack and move on a faster
cadence*; this page explains *what happens to a number once it has priced a
deposit*.

## A published rate is evidence, not a setting

Start from the load-bearing fact. When you publish a rate sheet, you are not
flipping a configuration toggle that the engine reads afresh on every operation.
You are recording **what the bank offered, and from when**. That record is
regulatory and commercial evidence: a customer was told "your 12-month deposit
earns 3.00%", and that statement has to stay true and auditable for the life of
the deposit, regardless of what rates do later.

Two design choices follow directly, and the rest of the page is just their
consequences:

1. A published sheet is **immutable** — never edited, never deleted. You cannot
   rewrite history you have already shown a customer.
2. The rate a deposit pays is **pinned at constitution** — resolved once, stamped
   onto the deposit's creation event, and read from there forever. A new sheet
   does not reach back and re-price an existing deposit.

## Versioning: corrections ship forward, they never overwrite

Because a published sheet is immutable, there is no "edit" operation. Every
change — a new tier, a weekly rate bump, even fixing a typo — ships as a **new
version**: a new `rate_sheet_version_id` with a new `effective_from`, deployed
through the same `POST /v1/rate-sheets` endpoint. The old version stays exactly
as it was published. This is *forward-only correction*
([ADR-PC-008 §P5](../../product-management/product_concepts/adrs/ADR-PC-008-rate-sheet-storage-and-deploy-api.md)).

The deploy endpoint enforces this at the boundary, not just by convention:

- Re-POSTing an **identical** body under an existing version id is an idempotent
  `200` (safe to retry).
- A **different** body under an existing version id is a `409 Conflict` — the
  immutability guarantee refusing to let you overwrite a published price.

So "I made a mistake in last week's sheet" is never "go edit it." It is "publish
a corrected new version with a later `effective_from`." The wrong sheet remains
the truthful record of what was offered during its window — which is the whole
point, because some deposits were constituted under it and they are entitled to
the rate they were told.

### Why a typo isn't a silent rollback

Suppose treasury types `350` bps where they meant `35`. The fix is a new,
corrected version (forward-only). But what about the deposits already constituted
under the bad sheet? They each carry the bad `rate_sheet_version_id` on their
`DepositConstituted` event — so *"which deposits priced off the bad sheet"* is a
single decidable query over the event stream, not a forensic reconstruction. The
re-pricing itself is **out-of-band and commercial** (it lands as a separate
correction event per affected deposit), because there is no correct *silent*
rollback of a price a customer was already told. The engine's job is to make the
affected set computable and the correction auditable
([ADR-PC-008 §P5](../../product-management/product_concepts/adrs/ADR-PC-008-rate-sheet-storage-and-deploy-api.md)).
This is the same per-instance pinning the pack uses, applied to prices.

## Point-in-time resolution: "the sheet active at this instant"

When a deposit is constituted, the engine asks one question: *what sheet was
active for this family at the constitution instant?* The answer is the sheet with
the **highest `effective_from` that is not after** the constitution time. In SQL
that is a single indexed query:

```sql
SELECT … FROM rate_sheets
 WHERE product_family = $1 AND effective_from <= $constituted_at
 ORDER BY effective_from DESC
 LIMIT 1;
```

Two design details make this unambiguous and fast:

- **No two sheets in a family may share an `effective_from`** (the
  `rate_sheets_family_effective_uq` constraint). So "the active sheet at T" is
  always exactly one row — never a tie.
- The `(product_family, effective_from DESC)` index serves the query directly, so
  resolution is cheap even with many published versions.

A consequence worth internalising: a sheet with an `effective_from` in the
**future** does not price deposits constituted **now**. If you deploy a sheet
dated next month and constitute a deposit today, today's deposit resolves to the
sheet active *today*, not your future one. (This is the most common "why did it
stamp the old version?" surprise — see the troubleshooting note in
[author and deploy a rate sheet §6](../how-to/author-and-deploy-a-rate-sheet.md#step-6--confirm-resolution-at-constitution).)

## Stamping: the rate is decidable from the event alone

Resolution does not just *find* the active sheet — it **stamps the result onto
the deposit's creation event**. `DepositConstituted` carries both:

- `rate_sheet_version_id` — *which* sheet version priced this deposit (anchors
  audit and replay), and
- `tan_basis_points` — the *resolved* rate the deposit actually pays.

Both are on the event (their field definitions are in the generated
[`DepositConstituted` reference](../reference/events/deposits.term_deposit.DepositConstituted.md)).
Storing **both** is deliberate: the version id answers "trace this back to a
published sheet"; the resolved value answers "what rate is this deposit paying?"
*without re-resolving* — so the engine never has to consult the rate sheet again
for that deposit. The rate a deposit pays is therefore decidable from the event
stream alone, which is exactly what lets a published rate stand as evidence
([ADR-PC-008 §P3](../../product-management/product_concepts/adrs/ADR-PC-008-rate-sheet-storage-and-deploy-api.md)).

This is why a *later* sheet cannot re-price a *live* deposit: the deposit's rate
isn't a lookup against "the current sheet", it is a value frozen onto an
immutable event at birth. Re-pricing a customer would be a commercial act (a new
correction event), never a quiet side effect of someone publishing next week's
prices.

## Why this matters to you as a config author

- **You never edit a sheet.** Every change — even a fix — is a new version with a
  later `effective_from`. The endpoint will `409` you if you try to overwrite.
- **Timing is `effective_from`, not deploy time.** A sheet prices deposits
  constituted at or after its `effective_from`; it cannot retro-price earlier
  ones, and a future-dated sheet doesn't price today's deposits.
- **Once a deposit exists, its rate is settled.** Your new sheet changes what
  *new* deposits get, never what existing ones pay. Re-pricing live deposits is a
  separate, deliberate, commercial action.

## Where to go next

- To actually deploy a version: [author and deploy a rate sheet](../how-to/author-and-deploy-a-rate-sheet.md).
- To change one band in an existing body: [add a rate band](../how-to/add-a-rate-band.md).
- For *why the numbers live apart from the pack* on a faster cadence: [why packs and rate sheets are separate](./why-packs-and-rate-sheets-are-separate.md).
- For the full storage / deploy / resolution decision: [ADR-PC-008](../../product-management/product_concepts/adrs/ADR-PC-008-rate-sheet-storage-and-deploy-api.md).
- Back to the [product-docs front door](../README.md).
