# How to author and deploy a complete rate-sheet version

This guide walks the **whole** rate-sheet loop, end to end: find the rates that
are live now, edit them into a new version, deploy that version, and confirm a
deposit will actually be priced by it. It is the companion to
[how to add a rate band](./add-a-rate-band.md) — that page is "I already have a
body and want to change one band"; this page is "I am starting a fresh product
and don't even know where the current numbers live."

The thing that strands most newcomers is this: **a rate sheet is not a file in
your pack.** The pack carries only a *ref* (a name) and a *bound* (a ceiling);
the actual numbers — the products → role → principal-band → TAN map — live as an
immutable row in the `rate_sheets` table in Postgres, deployed through a
separate, treasury-gated endpoint. So "where do I get the current body?" has a
real answer, and it is **not** "look in the pack." It is "query the table." This
guide shows you how. If you want the *why* behind that split first, read
[Why packs and rate sheets are separate](../explanation/why-packs-and-rate-sheets-are-separate.md);
for the *why* the rate a deposit pays never moves once it is created, read
[Rate-sheet versioning and point-in-time resolution](../explanation/rate-sheet-versioning-and-resolution.md).

## Before you start

- The stack must be **up** with the rate-sheet table migrated. `make up` brings
  Postgres up; the demo scripts (`make demo-mcp`, `make demo-saga`, `make demo`)
  also apply migrations `0001..NNNN` — which include `0004_rate_sheets.sql`, the
  table this guide reads and writes — and deploy a starter sheet. If you have run
  one of those, the loop below works against a populated table.
- Know the `product_id` (e.g. `dpz_pt_12m_juros_venc`) and `role` (e.g.
  `standard`, `new_money`) you are pricing, the principal ranges in **cents**,
  and the rates in **basis points**.
- Know the **pack version** the sheet validates against (e.g. `pt.2026.1`). The
  deploy reads that pack's `max_consumer_rate_bps` as the rate ceiling — see
  [step 4](#step-4--stay-inside-the-pack-declared-bound).

The authoritative body shape is [`RateSheetBody.cs`](../../../engine/src/Babelstone.RateSheets/RateSheetBody.cs);
the table DDL and deploy contract are [ADR-PC-008 §P1/§P2](../../product-management/product_concepts/adrs/ADR-PC-008-rate-sheet-storage-and-deploy-api.md).
This page does not restate either — it links to them and shows the loop.

## Step 1 — Read the body that is live now (it is in the table, not the pack)

There is **no `GET /v1/rate-sheets` endpoint** — the API surface is deploy-only
(`POST`). So to see the current numbers you read the `rate_sheets` table
directly. With the dev stack up, the Postgres container is `babelstone-postgres`
and the database/user is `babelstone`:

```sh
# List the deployed sheets for the term-deposit family, newest effective_from first.
docker exec babelstone-postgres psql -U babelstone -d babelstone -c \
  "SELECT rate_sheet_version_id, effective_from, pack_version, approved_by
     FROM rate_sheets
    WHERE product_family = 'term_deposit'
    ORDER BY effective_from DESC;"
```

To pull the **body** of the one you want to revise (replace the version id with
the one you found above):

```sh
docker exec babelstone-postgres psql -U babelstone -d babelstone -tAc \
  "SELECT jsonb_pretty(body)
     FROM rate_sheets
    WHERE rate_sheet_version_id = 'pt-deposits-2026.1';"
```

That `body` JSONB is **1:1 with the YAML that was deployed** — it is the exact
`products → role → bands` map. The starter sheet the demo scripts deploy looks
like this (one open-ended band per product, all at the `standard` role):

```json
{
  "dpz_pt_12m_juros_venc":   { "standard": { "bands": [ { "principal_cents": [0, null], "tan_basis_points": 300 } ] } },
  "dpz_pt_12m_juros_mensal": { "standard": { "bands": [ { "principal_cents": [0, null], "tan_basis_points": 325 } ] } },
  "dpz_pt_12m_juros_antecip":{ "standard": { "bands": [ { "principal_cents": [0, null], "tan_basis_points": 300 } ] } }
}
```

This is the body you start from. Copy it out and edit a local file — you never
edit the stored row (it is immutable; see [step 5](#step-5--deploy-the-new-version-via-post-v1rate-sheets)).

> **No stack up yet, or an empty table?** You have no current body to read —
> start from the shape in [`RateSheetBody.cs`](../../../engine/src/Babelstone.RateSheets/RateSheetBody.cs)
> or the illustrative ladder in [how to add a rate band](./add-a-rate-band.md#1-start-from-the-current-body),
> and skip to step 3.

## Step 2 — Edit the band structure

Edit the body you pulled. The band rules for one `(product, role)` are:
contiguous, non-overlapping, exhaustive; bands are half-open `[from, to)` (lower
inclusive, upper exclusive); exactly one band — the highest — is open-ended,
written `to: null`. The full mechanics of inserting a band and re-knitting the
boundaries are in [how to add a rate band](./add-a-rate-band.md#2-insert-the-band-and-re-knit-the-boundaries);
this page does not repeat them.

Say you want to add a tier to `dpz_pt_12m_juros_venc`'s `standard` ladder so
larger deposits earn more. Turn the single open band into a two-band ladder:

```yaml
products:
  dpz_pt_12m_juros_venc:
    standard:
      bands:
        - { principal_cents: [0,        5000000], tan_basis_points: 300 }   # was the only band; now closed at 5 000 000
        - { principal_cents: [5000000,  null],    tan_basis_points: 325 }   # new open-ended top band
```

The boundary meets exactly (`5000000` = `5000000`), there is no gap or overlap,
and only the highest band is open-ended. That is what the deploy validator
checks — see the next two steps for the exact rejections.

## Step 3 — Assemble the deploy request

The deploy request is the body wrapped in an **envelope** of version metadata.
The envelope fields are columns on the stored row; `products` is the JSONB body.
Build a `new-sheet.json` (the shape matches
[`RateSheetDeployRequest`](../../../engine/src/Babelstone.RateSheets.Api/RateSheetContracts.cs)):

```json
{
  "rate_sheet_version_id": "pt-deposits-2026.2",
  "product_family": "term_deposit",
  "pack_version": "pt.2026.1",
  "effective_from": "2026-02-01T00:00:00+00:00",
  "approved_by": "treasury.alm@bank.internal",
  "approval_ref": "ALM-2026-019",
  "products": {
    "dpz_pt_12m_juros_venc": {
      "standard": {
        "bands": [
          { "principal_cents": [0, 5000000], "tan_basis_points": 300 },
          { "principal_cents": [5000000, null], "tan_basis_points": 325 }
        ]
      }
    }
  }
}
```

Two rules that bite at deploy and are easy to miss:

- **`rate_sheet_version_id` must be NEW.** You never edit a published sheet; a
  correction or addition ships forward-only as a *new* version with a *later*
  `effective_from` ([ADR-PC-008 §P5](../../product-management/product_concepts/adrs/ADR-PC-008-rate-sheet-storage-and-deploy-api.md)).
  Re-using an existing id with a different body is a `409` (step 5).
- **`effective_from` must be unique within the family.** No two sheets for
  `term_deposit` may share an `effective_from` — the point-in-time resolve would
  be ambiguous otherwise (the `rate_sheets_family_effective_uq` constraint).
  A collision also surfaces as a `409`.

The `approved_by` / `approval_ref` fields carry the treasury sign-off the row
must record. The **deploying principal** is *not* in the body — it arrives as the
gateway-authenticated `X-Deploy-Actor` header (step 5) and is stored as
`published_by`.

## Step 4 — Stay inside the pack-declared bound

Every `tan_basis_points` must sit within `[0, max_consumer_rate_bps]`. The
ceiling is **read from the verified pack** named in `pack_version` — for
`pt.2026.1` that is `max_consumer_rate_bps: 2000` in
[`parameters/constants.yaml`](../../../packs/pt.2026.1/parameters/constants.yaml)
(20.00%, an illustrative ceiling). The floor is `0`: a negative TAN is rejected
at deploy, not by the band type (the type permits negatives by design, for
negative-rate environments), so the pack bound is the gate. See
[`PackRateBoundsSource`](../../../engine/src/Babelstone.RateSheets.Api/RateSheetContracts.cs)
and [ADR-PC-008 §P2](../../product-management/product_concepts/adrs/ADR-PC-008-rate-sheet-storage-and-deploy-api.md).

If you exceed it, the deploy fails validation with the exact diagnostic decoded
in [how to interpret a validation failure](./interpret-a-validation-failure.md#a-rate-sheet-deploy-rejections).

## Step 5 — Deploy the new version via `POST /v1/rate-sheets`

The endpoint is hosted by `Babelstone.RateSheets.Api`. Locally the demo scripts
bring it up as a transient host; if you are running it yourself it listens on the
`ASPNETCORE_URLS` you gave it. POST your file with the **required**
`X-Deploy-Actor` header (the gateway-authenticated identity; omitting it is a
`401`):

```sh
curl -sS -o response.json -w '%{http_code}\n' \
  -X POST http://localhost:8080/v1/rate-sheets \
  -H 'Content-Type: application/json' \
  -H 'X-Deploy-Actor: treasury.analyst@bank.internal' \
  --data-binary @new-sheet.json
```

What the status code tells you ([ADR-PC-008 §P2](../../product-management/product_concepts/adrs/ADR-PC-008-rate-sheet-storage-and-deploy-api.md)):

| Status | Meaning | What to do |
|---|---|---|
| **`201 Created`** | New version stored. The response carries the row plus its `published_at`. | Done — go to step 6. |
| **`200 OK`** | You re-POSTed an **identical** body under an **existing** version id. Idempotent replay; safe to retry. | Nothing — it already exists exactly as you sent it. |
| **`409 Conflict`** | The version id exists with a **different** definition, or another sheet already claims this `effective_from`. | Ship a **new** `rate_sheet_version_id` (and/or a unique `effective_from`). Never edit a published sheet. |
| **`400 Bad Request`** | The body failed validation (band gap/overlap, non-open top band, TAN out of bound), or `pack_version` is not a loaded pack, or `Idempotency-Key` ≠ `rate_sheet_version_id`. | Decode it with [interpret a validation failure](./interpret-a-validation-failure.md#a-rate-sheet-deploy-rejections), fix, re-POST. |
| **`401 Unauthorized`** | The `X-Deploy-Actor` header was missing or blank. | Add the header. |

Idempotency is keyed on `rate_sheet_version_id`, and the body comparison is
**canonical** (object-key order doesn't matter; band order does) — so a re-POST
that differs only in key order is still a `200`, while a real change under the
same id is a `409`.

## Step 6 — Confirm resolution at constitution

A `201` means the row is stored, but the loop isn't closed until you confirm a
deposit will actually be **priced** by it. The engine resolves the active sheet
*at constitution* — the sheet with the highest `effective_from` not after the
constitution instant — resolves `(product, role, principal)` to a concrete
`tan_basis_points`, and **stamps both** the `rate_sheet_version_id` and the
resolved TAN onto `DepositConstituted`. From then on the deposit pays that rate
for its whole life ([ADR-PC-008 §P3](../../product-management/product_concepts/adrs/ADR-PC-008-rate-sheet-storage-and-deploy-api.md);
the *why it never moves* is [rate-sheet versioning and resolution](../explanation/rate-sheet-versioning-and-resolution.md)).

Two ways to confirm:

1. **Constitute a deposit and read the event.** Drive a constitution (the
   `make demo-mcp` / `make demo-saga` flows do this) for a `(product, role,
   principal)` your new bands cover, then read the stamped fields on the
   `DepositConstituted` event — they are documented in the generated
   [event reference](../reference/events/deposits.term_deposit.DepositConstituted.md).
   The stamped `rate_sheet_version_id` should be your new version; the stamped
   TAN should be the band the principal falls into.

2. **Check the row landed and is the newest.** Re-run the list query from step 1;
   your new `rate_sheet_version_id` should appear with the latest
   `effective_from` for the family — which is exactly what the point-in-time
   resolve picks for a constitution at or after that instant.

If a constitution after your `effective_from` still stamps the *old* version id,
the usual cause is that your `effective_from` is in the future relative to the
constitution instant — the resolve picks the highest `effective_from` **not
after** the constitution time, so a sheet dated tomorrow does not price a deposit
created today.

## Honest local gaps

These are real limitations of the current local setup, not steady-state design:

- **No GET endpoint.** Reading the current body means querying the table
  (step 1). A read API is not part of this slice.
- **The bound is read from the pack now** (`max_consumer_rate_bps`), but the
  cross-artefact coverage checks ("every priced `product_id` exists in an active
  config", "every active config's `rate_ref` is covered") are **not** enforced
  yet — they need the product-config registry (a later epic), so the deploy host
  runs them vacuously. A sheet can pass locally and still under-cover a config
  the validator cannot see. See
  [`EmptyProductConfigSource`](../../../engine/src/Babelstone.RateSheets.Api/RateSheetContracts.cs)
  and [why packs and rate sheets are separate](../explanation/why-packs-and-rate-sheets-are-separate.md#where-this-honestly-does-not-work-yet).
- **Treasury-scope authorization is the edge gateway's job**, not the deploy
  host's; the host records the gateway-supplied `X-Deploy-Actor` on the immutable
  row ([ADR-PC-008 §P4 / Amendment §A3](../../product-management/product_concepts/adrs/ADR-PC-008-rate-sheet-storage-and-deploy-api.md)).

## See also

- [How to add a rate band](./add-a-rate-band.md) — the band-editing mechanics this page builds on.
- [How to interpret a validation failure](./interpret-a-validation-failure.md) — decode a `400`/`409` or a `cue vet` rejection.
- [Why packs and rate sheets are separate](../explanation/why-packs-and-rate-sheets-are-separate.md) — the cadence/ownership rationale.
- [Rate-sheet versioning and point-in-time resolution](../explanation/rate-sheet-versioning-and-resolution.md) — why the stamped TAN never moves.
- [ADR-PC-008](../../product-management/product_concepts/adrs/ADR-PC-008-rate-sheet-storage-and-deploy-api.md) — storage, deploy API, idempotency, resolution.
- [Generated reference home](../reference/README.md) — the drift-proof field-level truth.
- [Product-docs front door](../README.md).
