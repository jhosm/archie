# How to add a rate band to a rate sheet

This guide adds a new principal band to an existing rate sheet and ships it. It
assumes you already know which `(product, role)` you are pricing and the rate you
want to offer. If you need the *why* behind rate sheets being a separate artefact
from packs, read [Why packs and rate sheets are separate](../explanation/why-packs-and-rate-sheets-are-separate.md)
first.

A rate sheet is **not** a committed pack file. The pack carries only a *ref*
(`rate-sheet-refs/<name>.yaml`); the actual numbers live as an immutable row in the
`rate_sheets` table and ship through a separate, treasury-gated deploy endpoint. So
"add a band" means **author a new rate-sheet version body and deploy it** — you never
edit a published sheet in place.

## Before you start

- Know the `product_id` (e.g. `dpz_pt_12m_juros_venc`) and `role` (e.g. `standard`,
  `new_money`) you are changing.
- Have the new band's principal range in **cents** and its rate in **basis points**.
- Confirm the new rate is within the pack-declared bound. For `pt.2026.1` that is
  `0 ≤ tan_basis_points ≤ max_consumer_rate_bps`. Locally this bound is an interim
  static `max = 2000` (see [Local gaps](#local-gaps-read-before-you-rely-on-this)),
  not yet read from the pack.

## The body shape, in one paragraph

A rate-sheet body is `products → role → ordered principal bands`. Each band is
`{ principal_cents: [from, to], tan_basis_points: N }`. The bands for one
`(product, role)` must be **contiguous, non-overlapping, and exhaustive**: each band's
upper bound (`to`) meets the next band's lower bound (`from`), and only the **highest**
band is open-ended, written with `to: null`. Bands are half-open intervals
`[from, to)` — the lower bound is **inclusive**, the upper bound **exclusive** — so a
principal exactly equal to a band's `to` falls in the *next* band up
(`RateSheetBody.Covers`: `principal ≥ from && principal < to`). The authoritative shape lives in
[`RateSheetBody.cs`](../../../engine/src/Babelstone.RateSheets/RateSheetBody.cs) and is
recorded in [ADR-PC-008 §P1](../../product-management/product_concepts/adrs/ADR-PC-008-rate-sheet-storage-and-deploy-api.md);
the worked example shape is in [config surface §2.2](../../product-management/product_concepts/feature-design-configuration-surface.md).
Don't restate those tables — link to them.

## Steps

### 1. Start from the current body

Take the body of the rate sheet you are revising. Note there is no rate-sheet body
committed on disk to copy — the numbers live as a row in the `rate_sheets` table,
not in the pack (which carries only a *ref*). So we work from an **illustrative**
`standard` ladder for `dpz_pt_12m_juros_venc` (the canonical shape is the link
above):

```yaml
products:
  dpz_pt_12m_juros_venc:
    standard:
      bands:
        - { principal_cents: [50000,    5000000],   tan_basis_points: 300 }
        - { principal_cents: [5000000,  25000000],  tan_basis_points: 325 }
        - { principal_cents: [25000000, null],      tan_basis_points: 350 }   # open-ended top band
```

### 2. Insert the band and re-knit the boundaries

Adding a band is not just an insert — you must keep the ladder contiguous. The new
band's `from` must equal the previous band's `to`, and its `to` must equal the next
band's `from`. **If you split an existing range**, change the neighbour you split so the
two new boundaries meet exactly.

Say you want a new tier from `25 000 000` to `50 000 000` cents at `340` bps, sitting
*below* the open-ended top band. You must move the old top band's lower bound up to
`50000000` so it starts where the new band ends:

```yaml
products:
  dpz_pt_12m_juros_venc:
    standard:
      bands:
        - { principal_cents: [50000,    5000000],   tan_basis_points: 300 }
        - { principal_cents: [5000000,  25000000],  tan_basis_points: 325 }
        - { principal_cents: [25000000, 50000000],  tan_basis_points: 340 }   # new band
        - { principal_cents: [50000000, null],      tan_basis_points: 350 }   # top band: from moved up to meet it
```

**If the new band is the open-ended top band** (you are extending the ladder upward),
give the *old* top band a finite `to` and make the new band the only one with
`to: null`:

```yaml
        - { principal_cents: [25000000, 100000000], tan_basis_points: 350 }   # was open-ended; now closed
        - { principal_cents: [100000000, null],     tan_basis_points: 375 }   # new open-ended top band
```

Exactly one band per `(product, role)` may be open-ended, and it must be the highest.
The deploy validator rejects a sheet where an open-ended band is not the highest, where
adjacent bands leave a gap or overlap, where the highest band is *not* open-ended, or
where any `tan_basis_points` is outside the pack bound. These checks are in
[`RateSheetValidator.cs`](../../../engine/src/Babelstone.RateSheets/RateSheetValidator.cs).

### 3. Mint a new version id

The body change rides on a **new** `rate_sheet_version_id` (and a new `effective_from`).
You never edit a published sheet — corrections and additions ship forward-only as a new
version ([ADR-PC-008 §P2/§P5](../../product-management/product_concepts/adrs/ADR-PC-008-rate-sheet-storage-and-deploy-api.md)).
The `pt.2026.1` pack's ref points at a `rate_sheet_version_id` (currently
`pt-deposits-2026.1` in
[`rate-sheet-refs/deposits-pt.yaml`](../../../packs/pt.2026.1/rate-sheet-refs/deposits-pt.yaml)) —
the new version becomes the active sheet for that family from its `effective_from`.

### 4. Deploy via `POST /v1/rate-sheets`

Deploy the new body to the treasury-gated endpoint:

```bash
curl -sS -X POST http://localhost:8080/v1/rate-sheets \
  -H 'Content-Type: application/json' \
  -d @new-sheet.json
```

The deploy contract — request shape, idempotency, status codes — is defined in
[ADR-PC-008 §P2](../../product-management/product_concepts/adrs/ADR-PC-008-rate-sheet-storage-and-deploy-api.md).
Authorization (the treasury / ALM approver scope) is enforced by the edge gateway, not
by this host; the host records the gateway-supplied approver on the immutable row
([ADR-PC-008 §P4 / Amendment §A3](../../product-management/product_concepts/adrs/ADR-PC-008-rate-sheet-storage-and-deploy-api.md)).
Do not expect to restate the `rate_sheets` DDL here — it is in §P1 of that ADR.

Idempotency is keyed on `rate_sheet_version_id`:

- **Re-POSTing an identical body** under an existing version id returns `200` with the
  stored resource (safe to retry).
- **A different body** under an existing version id returns `409 Conflict` — published
  sheets are immutable.
- **A correction** ships as a *new* `rate_sheet_version_id`, never an edit to an
  existing one.

The body comparison is canonical (key order doesn't matter; band order does) — see
`RateSheetJson.Canonical` in
[`RateSheetBody.cs`](../../../engine/src/Babelstone.RateSheets/RateSheetBody.cs).

### 5. Confirm it took

A `200` (or `201` on first deploy) with the stored body means the band is live from its
`effective_from`. The engine resolves the active sheet at constitution and stamps both
`rate_sheet_version_id` and the resolved `tan_basis_points` onto `DepositConstituted`,
so the rate a deposit pays is decidable from the event stream alone
([ADR-PC-008 §P3](../../product-management/product_concepts/adrs/ADR-PC-008-rate-sheet-storage-and-deploy-api.md)).

## Local gaps (read before you rely on this)

These are honest limitations of the current local setup, not steady-state behaviour:

- **The deploy endpoint needs the running engine + a live Postgres.** Bring the stack
  up with `make up` before `POST /v1/rate-sheets`. The endpoint is hosted by
  `Babelstone.RateSheets.Api`
  ([ADR-PC-008 Amendment §A1](../../product-management/product_concepts/adrs/ADR-PC-008-rate-sheet-storage-and-deploy-api.md)).
- **The pack bound is an interim static `max = 2000` bps**, not yet derived from the
  pack's `max_consumer_rate_bps`. A rate over `2000` is rejected locally even if the
  pack would allow more.
- **Cross-artefact coverage checks are not enforced yet.** The deploy validator checks
  the *self-contained* invariants (band shape, contiguity, exhaustiveness, the bound).
  It does **not** yet check that every `product_id` exists in an active config or that
  every active config's `rate_ref` is covered — those need the product-config registry
  (a later epic). A sheet can pass locally and still under-cover a config the validator
  cannot see.

## See also

- [Why packs and rate sheets are separate](../explanation/why-packs-and-rate-sheets-are-separate.md) — the cadence/approval rationale.
- [ADR-PC-008](../../product-management/product_concepts/adrs/ADR-PC-008-rate-sheet-storage-and-deploy-api.md) — storage, deploy API, idempotency, resolution.
- [Generated reference home](../../product-management/reference/README.md) — the drift-proof field-level truth.
- [Product-docs front door](../README.md).
