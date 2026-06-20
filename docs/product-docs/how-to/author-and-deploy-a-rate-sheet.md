# How to author and deploy a complete rate-sheet version

This guide walks the **whole** rate-sheet loop, end to end: start from the YAML
that prices a family today, edit it into a new version, deploy that version, and
confirm a deposit will actually be priced by it. It is the companion to
[how to add a rate band](./add-a-rate-band.md) — that page is "I already have a
sheet and want to change one band"; this page is "I am starting a fresh product
and want the whole author → deploy → confirm loop."

The thing to get right first is **where a rate sheet lives**. A rate sheet is a
**YAML file you commit**, exactly like a product variant. It lives under
[`/rate-sheets/`](../../../rate-sheets/) — the treasury / ALM-owned sibling of
[`/product-configs/`](../../../product-configs/) in the three-owner
configuration surface. That YAML is the **source of truth**: deploying it copies
it, serialised, into an immutable row in the `rate_sheets` Postgres table, so the
stored row is *1:1 with the YAML you committed* ([ADR-PC-008 §P1](../../product-management/product_concepts/adrs/ADR-PC-008-rate-sheet-storage-and-deploy-api.md)).

So you **author in YAML and you never hand-edit JSON or poke the database**. To
"start from the rate sheet that is live now," you copy its **YAML file** — not a
`SELECT` against a table. The database row is the *deployed result* of a YAML
file, downstream of it, never the thing you edit. If you want the *why* behind
the pack/sheet split first, read
[Why packs and rate sheets are separate](../explanation/why-packs-and-rate-sheets-are-separate.md);
for *why* the rate a deposit pays never moves once it is created, read
[Rate-sheet versioning and point-in-time resolution](../explanation/rate-sheet-versioning-and-resolution.md).

## Before you start

- You need the repo checked out — that is where the rate-sheet YAML lives. You do
  **not** need the stack up to *author* a new version; you need it up only to
  *deploy* (step 4) and *confirm* (step 5). `make up` brings Postgres up; the demo
  scripts (`make demo-mcp`, `make demo-saga`, `make demo`) also apply the
  migrations that include `0004_rate_sheets.sql` (the table a deploy writes to) and
  deploy the starter sheet.
- Know the `product_id` (e.g. `dpz_pt_12m_juros_venc`) and `role` (e.g.
  `standard`, `new_money`) you are pricing, the principal ranges in **cents**,
  and the rates in **basis points**.
- Know the **pack version** the sheet validates against (e.g. `pt.2026.1`). The
  deploy reads that pack's `max_consumer_rate_bps` as the rate ceiling — see
  [step 3](#step-3--stay-inside-the-pack-declared-bound).

The authoritative body shape is [`RateSheetBody.cs`](../../../engine/src/Babelstone.RateSheets/RateSheetBody.cs);
the table DDL and deploy contract are [ADR-PC-008 §P1/§P2](../../product-management/product_concepts/adrs/ADR-PC-008-rate-sheet-storage-and-deploy-api.md).
This page does not restate either — it links to them and shows the loop.

## Step 1 — Start from the existing sheet's YAML (copy the file, not the database)

The rate sheet that prices term deposits today is a committed file:
[`rate-sheets/term_deposit/pt-deposits-2026.1.yaml`](../../../rate-sheets/term_deposit/pt-deposits-2026.1.yaml).
Open it and you see the **whole sheet** — the version metadata and the
`products → role → bands` body in one auditor-readable artefact:

```yaml
rate_sheet_version_id: pt-deposits-2026.1
product_family: term_deposit
pack_version: pt.2026.1
effective_from: "2026-01-01T00:00:00+00:00"
approved_by: treasury.alm@bank.internal
approval_ref: ALM-2026-019
products:
  dpz_pt_12m_juros_venc:
    standard:
      bands:
        - { principal_cents: [0, null], tan_basis_points: 300 }
  dpz_pt_12m_juros_mensal:
    standard:
      bands:
        - { principal_cents: [0, null], tan_basis_points: 325 }
  dpz_pt_12m_juros_antecip:
    standard:
      bands:
        - { principal_cents: [0, null], tan_basis_points: 300 }
```

**Copy it to a new file** for the version you are about to author — you never edit
the existing file in place (a published sheet is immutable; see
[step 4](#step-4--deploy-the-new-version)):

```sh
cp -f rate-sheets/term_deposit/pt-deposits-2026.1.yaml \
      rate-sheets/term_deposit/pt-deposits-2026.2.yaml
```

The envelope at the top maps 1:1 to the columns on the stored row; everything
under `products:` is the priceable body. That is the file you edit next.

> **Why not just read the database?** The numbers do live as a row in the
> `rate_sheets` table once deployed — but that row is the *output* of this YAML,
> not the source. Reading it (step 5) is a way to *confirm what is deployed*, never
> the way to *author*. If you edited JSON pulled out of the table you would be
> hand-maintaining a derived artefact and the committed source would drift from
> production.

## Step 2 — Edit the bands and mint the new version

Two edits go together in the copied file: the **version metadata** (so it is a new
version, not a re-publish) and the **band structure**.

**1. Mint a new version.** Change `rate_sheet_version_id` to a *new* id and
`effective_from` to a *later* instant. You never edit a published sheet; a
correction or addition ships forward-only as a new version
([ADR-PC-008 §P5](../../product-management/product_concepts/adrs/ADR-PC-008-rate-sheet-storage-and-deploy-api.md)).
Update the approver fields to the sign-off that authorised this change:

```yaml
rate_sheet_version_id: pt-deposits-2026.2          # NEW id — never reuse a published one
effective_from: "2026-02-01T00:00:00+00:00"        # later, and unique within the family
approved_by: treasury.alm@bank.internal
approval_ref: ALM-2026-031
```

**2. Edit the bands.** The band rules for one `(product, role)` are: contiguous,
non-overlapping, exhaustive; bands are half-open `[from, to)` (lower inclusive,
upper exclusive); exactly one band — the highest — is open-ended, written
`to: null`. The full mechanics of inserting a band and re-knitting the boundaries
are in [how to add a rate band](./add-a-rate-band.md#2-insert-the-band-and-re-knit-the-boundaries);
this page does not repeat them.

Say you want to add a tier to `dpz_pt_12m_juros_venc`'s `standard` ladder so
larger deposits earn more. Turn the single open band into a two-band ladder:

```yaml
products:
  dpz_pt_12m_juros_venc:
    standard:
      bands:
        - { principal_cents: [0,       5000000], tan_basis_points: 300 }   # was the only band; now closed at 5 000 000
        - { principal_cents: [5000000, null],    tan_basis_points: 325 }   # new open-ended top band
```

The boundary meets exactly (`5000000` = `5000000`), there is no gap or overlap,
and only the highest band is open-ended. That is what the deploy validator checks
— the next two steps cover the exact rejections.

Two rules that bite at deploy and are easy to miss:

- **`rate_sheet_version_id` must be NEW.** Re-using an existing id with a different
  body is a `409` (step 4).
- **`effective_from` must be unique within the family.** No two sheets for
  `term_deposit` may share an `effective_from` — the point-in-time resolve would be
  ambiguous otherwise (the `rate_sheets_family_effective_uq` constraint). A
  collision also surfaces as a `409`.

The `approved_by` / `approval_ref` fields carry the treasury sign-off the row must
record. The **deploying principal** is *not* in the file — it arrives as the
gateway-authenticated `X-Deploy-Actor` header (step 4) and is stored as
`published_by`.

## Step 3 — Stay inside the pack-declared bound

Every `tan_basis_points` must sit within `[0, max_consumer_rate_bps]`. The ceiling
is **read from the verified pack** named in `pack_version` — for `pt.2026.1` that
is `max_consumer_rate_bps: 2000` in
[`parameters/constants.yaml`](../../../packs/pt.2026.1/parameters/constants.yaml)
(20.00%, an illustrative ceiling). The floor is `0`: a negative TAN is rejected at
deploy, not by the band type (the type permits negatives by design, for
negative-rate environments), so the pack bound is the gate. See
[`PackRateBoundsSource`](../../../engine/src/Babelstone.RateSheets.Api/RateSheetContracts.cs)
and [ADR-PC-008 §P2](../../product-management/product_concepts/adrs/ADR-PC-008-rate-sheet-storage-and-deploy-api.md).

If you exceed it, the deploy fails validation with the exact diagnostic decoded in
[how to interpret a validation failure](./interpret-a-validation-failure.md#a-rate-sheet-deploy-rejections).

## Step 4 — Deploy the new version

Deploying takes the YAML file you authored and POSTs it to the treasury-gated
endpoint hosted by `Babelstone.RateSheets.Api`. Locally the demo scripts bring it
up as a transient host; if you are running it yourself it listens on the
`ASPNETCORE_URLS` you gave it.

**The deploy wire format is JSON**, so the one interim step is to serialise your
YAML source to JSON on the way in. `yq` does it in one line — install it with
`brew install yq` (it is an external tool, deliberately **not** in the pinned
`mise` toolchain; the repo's CI scripts avoid it and use `cue export` / `js-yaml`
to stay pinned). Pipe the result straight to `curl` with the **required**
`X-Deploy-Actor` header (the gateway-authenticated identity; omitting it is a
`401`):

```sh
yq -o=json rate-sheets/term_deposit/pt-deposits-2026.2.yaml \
  | curl -sS -o response.json -w '%{http_code}\n' \
      -X POST http://localhost:8080/v1/rate-sheets \
      -H 'Content-Type: application/json' \
      -H 'X-Deploy-Actor: treasury.analyst@bank.internal' \
      --data-binary @-
```

> The YAML → JSON step is a *bridge*, not the design intent: the YAML is the
> source, JSON is only the wire shape the endpoint accepts. A YAML-native deploy
> tool — and wiring the demo scripts to deploy straight from the committed file —
> is the follow-up (bd `babelstone-alfy`), tracked in
> [Honest local gaps](#honest-local-gaps).

What the status code tells you ([ADR-PC-008 §P2](../../product-management/product_concepts/adrs/ADR-PC-008-rate-sheet-storage-and-deploy-api.md)):

| Status | Meaning | What to do |
|---|---|---|
| **`201 Created`** | New version stored. The response carries the row plus its `published_at`. | Done — go to step 5. |
| **`200 OK`** | You re-deployed an **identical** body under an **existing** version id. Idempotent replay; safe to retry. | Nothing — it already exists exactly as you sent it. |
| **`409 Conflict`** | The version id exists with a **different** definition, or another sheet already claims this `effective_from`. | Ship a **new** `rate_sheet_version_id` (and/or a unique `effective_from`). Never edit a published sheet. |
| **`400 Bad Request`** | The body failed validation (band gap/overlap, non-open top band, TAN out of bound), or `pack_version` is not a loaded pack, or `Idempotency-Key` ≠ `rate_sheet_version_id`. | Decode it with [interpret a validation failure](./interpret-a-validation-failure.md#a-rate-sheet-deploy-rejections), fix the YAML, re-deploy. |
| **`401 Unauthorized`** | The `X-Deploy-Actor` header was missing or blank. | Add the header. |

Idempotency is keyed on `rate_sheet_version_id`, and the body comparison is
**canonical** (object-key order doesn't matter; band order does) — so a re-deploy
that differs only in key order is still a `200`, while a real change under the same
id is a `409`.

## Step 5 — Confirm resolution at constitution

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
   `DepositConstituted` event — documented in the generated
   [event reference](../reference/events/deposits.term_deposit.DepositConstituted.md).
   The stamped `rate_sheet_version_id` should be your new version; the stamped TAN
   should be the band the principal falls into.

2. **Inspect the deployed row (read-only verification).** If you want to eyeball
   what landed, the stored row is queryable — but treat this as *confirming the
   deploy*, never as the place you author. There is **no `GET /v1/rate-sheets`**
   endpoint, so a direct read goes through `psql`; the body it shows is the
   serialised form of the YAML you deployed:

   ```sh
   docker exec babelstone-postgres psql -U babelstone -d babelstone -c \
     "SELECT rate_sheet_version_id, effective_from, pack_version, approved_by
        FROM rate_sheets
       WHERE product_family = 'term_deposit'
       ORDER BY effective_from DESC;"
   ```

   Your new `rate_sheet_version_id` should appear with the latest `effective_from`
   for the family — which is exactly what the point-in-time resolve picks for a
   constitution at or after that instant.

If a constitution after your `effective_from` still stamps the *old* version id,
the usual cause is that your `effective_from` is in the future relative to the
constitution instant — the resolve picks the highest `effective_from` **not
after** the constitution time, so a sheet dated tomorrow does not price a deposit
created today.

## Honest local gaps

These are real limitations of the current local setup, not steady-state design:

- **The deploy wire format is JSON; the YAML → JSON bridge is manual.** You author
  YAML (the source of truth) but serialise it with `yq` to POST it (step 4). A
  YAML-native deploy tool, and wiring `make demo-*` to deploy from
  [`rate-sheets/term_deposit/pt-deposits-2026.1.yaml`](../../../rate-sheets/term_deposit/pt-deposits-2026.1.yaml)
  instead of an inline JSON heredoc, is the follow-up (bd `babelstone-alfy`). Until
  then the committed YAML and the demo's inline JSON are kept in sync by hand.
- **No GET endpoint.** Reading the deployed body means querying the table (step 5,
  a verification aid). A read API is not part of this slice.
- **The bound is read from the pack now** (`max_consumer_rate_bps`), but the
  cross-artefact coverage checks ("every priced `product_id` exists in an active
  config", "every active config's `rate_ref` is covered") are **not** enforced yet
  — they need the product-config registry (a later epic), so the deploy host runs
  them vacuously. A sheet can pass locally and still under-cover a config the
  validator cannot see. See
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
- [`/rate-sheets/`](../../../rate-sheets/) — where the committed rate-sheet YAML lives (treasury / ALM-owned).
- [ADR-PC-008](../../product-management/product_concepts/adrs/ADR-PC-008-rate-sheet-storage-and-deploy-api.md) — storage, deploy API, idempotency, resolution.
- [Generated reference home](../reference/README.md) — the drift-proof field-level truth.
- [Product-docs front door](../README.md).
