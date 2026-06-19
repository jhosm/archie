# How to interpret a validation failure — a message decoder

You ran a validation — `make pack-validate`, `make validate-variant`, a raw
`cue vet`, or a `POST /v1/rate-sheets` — and it came back red. This page is a
**lookup table**: find the message you actually saw, read the root cause, apply
the fix. It is deliberately a fast index, not a tutorial — every entry is a real
diagnostic from a shipped fixture, and each one links to where the deeper *why*
lives.

Two companion pages do different jobs, and this one sits between them:

- If you want to understand **why a rule rejects what it rejects** — closed
  structs, pack-bound references, the validator depths — read
  [reading a CUE schema](../explanation/reading-a-cue-schema.md). This decoder
  *complements* that page; it does not restate the method.
- If you are specifically debugging a **variant** and want the
  depth → reason-code → fix map with the `validate-variant` reason codes,
  [troubleshoot a variant rejection](./troubleshoot-a-variant-rejection.md) is
  the deeper companion. This page is the broader quick index that also covers
  **pack data** and **rate-sheet deploy** rejections in one place.

The fixture catalogue these entries come from is your worked list of "what each
rule rejects", one violation per file:
[`contracts/cue/testdata/term-deposit/invalid/`](../../../contracts/cue/testdata/term-deposit/invalid/).

---

## How to use this page

1. Identify **which validator** rejected you — they print differently:
   - A raw `cue vet` (or `make pack-validate`) prints a CUE diagnostic like
     `field not allowed` or `conflicting values`.
   - `make validate-variant` (the Go `pack-validate` binary) prints a
     **depth + `[reason_code]` + message** line: `✗ depth 2 currency
     [type_mismatch] …`.
   - A `POST /v1/rate-sheets` returns an **HTTP status** and a JSON
     `ProblemDetails` / conflict body.
2. Find the message below. The tables are grouped by validator.
3. Apply the fix. Because each fixture isolates **one** rule, a run names one
   problem at a time — fix it, re-run, repeat.

To *see* any entry yourself, point the validator at the named fixture, e.g.:

```sh
# Go binary, depth-tagged output:
make validate-variant VARIANT=contracts/cue/testdata/term-deposit/invalid/non-eur-currency.yaml PACK=pt.2026.1

# Raw CUE diagnostic for the same file:
cue vet -d '#TermDeposit' contracts/cue/testdata/term-deposit/invalid/non-eur-currency.yaml \
    contracts/cue/common.cue contracts/cue/families/term-deposit.cue
```

---

## A. Rate-sheet deploy rejections

These come back from `POST /v1/rate-sheets` (the loop in
[author and deploy a rate sheet](./author-and-deploy-a-rate-sheet.md)). The body
diagnostics are produced by
[`RateSheetValidator`](../../../engine/src/Babelstone.RateSheets/RateSheetValidator.cs)
and returned in a `400` validation-problem under the `rate_sheet` key; the
idempotency outcomes are status codes from
[`DeployRateSheetEndpoint`](../../../engine/src/Babelstone.RateSheets.Api/DeployRateSheetEndpoint.cs).

| What you see | Root cause | Fix |
|---|---|---|
| `400` · `… band N: tan_basis_points 2500 is outside the pack-declared bounds [0, 2000].` | A TAN is above the pack ceiling (`max_consumer_rate_bps`, 2000 for `pt.2026.1`) or below `0`. The bound is **read from the verified pack**, not a host knob. | Bring every `tan_basis_points` into `[0, max_consumer_rate_bps]`. To price higher, the **pack** ceiling must change — a regulatory pack change, not a sheet edit. |
| `400` · `…/standard: gap between a band ending at 5000000 and a band starting at 6000000; bands must be contiguous and non-overlapping.` | Adjacent bands leave a hole: one band's `to` ≠ the next band's `from`. | Re-knit the boundary so the upper bound meets the next lower bound exactly. See [add a rate band §2](./add-a-rate-band.md#2-insert-the-band-and-re-knit-the-boundaries). |
| `400` · `…/standard: overlap between a band ending at 6000000 and a band starting at 5000000; bands must be contiguous and non-overlapping.` | Two bands cover the same cents. | Move one boundary so the ranges abut instead of overlapping. |
| `400` · `…/standard: the highest band must be open-ended (null upper bound) so the principal range is exhaustive; got upper bound 25000000.` | The top band has a finite `to`, so principals above it are unpriced. | Make the highest band open-ended: `to: null`. Exactly one band per `(product, role)` may be open-ended, and it must be the highest. |
| `400` · `…/standard: an open-ended band (no upper bound) is not the highest band; higher bands are unreachable.` | A `to: null` band sits **below** another band, which can never be reached. | Move the open-ended band to the top, or give it a finite `to`. |
| `400` · `…/standard: no bands.` | A `(product, role)` was declared with an empty band list. | Give the role at least one band, or remove the role. |
| `400` · `pack_version 'pt.2099.1' is not a loaded, verified pack; the rate-sheet bound cannot be resolved …` | The `pack_version` in the request is one the engine has not loaded (a stale or typo'd pin) — so the bound can't be resolved. | Pin a loaded pack version (the host pre-loads e.g. `pt.2026.1`). |
| `400` · `The Idempotency-Key header, when supplied, must equal rate_sheet_version_id.` | You sent an `Idempotency-Key` header that differs from `rate_sheet_version_id`. The version id **is** the idempotency key. | Drop the header, or set it equal to `rate_sheet_version_id`. |
| `401` · `The X-Deploy-Actor header (the gateway-authenticated deploying principal) is required.` | The required `X-Deploy-Actor` header was missing or blank. | Add `-H 'X-Deploy-Actor: <principal>'`. |
| `409` · `rate_sheet_version_id 'pt-deposits-2026.1' already exists with a different definition; corrections ship forward-only as a new version (ADR-PC-008 §P5).` | You re-used an existing version id with a **changed** body/envelope. Published sheets are immutable. | Ship the change as a **new** `rate_sheet_version_id`. |
| `409` · `effective_from is already claimed by a different rate_sheet_version_id.` | Another sheet for this family already has that `effective_from` (the `(product_family, effective_from)` uniqueness constraint). | Choose a distinct `effective_from`. |
| `200` (not an error) | An **identical** body re-POSTed under an existing version id — idempotent replay. | Nothing; it already exists exactly as sent. |

> **The cross-artefact checks pass vacuously locally.** "Every priced
> `product_id` exists in an active config" and "every active config's `rate_ref`
> is covered" are not enforced until the product-config registry lands, so a
> sheet can pass locally and still under-cover a config the validator cannot see
> ([why packs and rate sheets are separate §gaps](../explanation/why-packs-and-rate-sheets-are-separate.md#where-this-honestly-does-not-work-yet)).

---

## B. Pack / variant validation rejections (depths 1–4)

These come from `make pack-validate` (your pack's own data) and
`make validate-variant` (a variant). The depth tells you *what kind* of problem
it is and therefore *where the fix lives*; the four named below are the ones the
issue calls out, plus the neighbours from the same fixture catalogue. The
authoritative depth definitions and budgets are normative in
[ADR-PC-006 §Context and §P3](../../product-management/product_concepts/adrs/ADR-PC-006-cue-schema-language.md)
— the one-line summaries here are orientation, not the contract.

Each row shows the **Go `validate-variant` line** (the depth-tagged form) and,
where it differs usefully, the **raw `cue vet` diagnostic** you would see from
`make pack-validate`. The fixture file is named so you can reproduce it.

### Depth 1 — syntactic / structural shape (fix is in *your* file)

| Reason code · fixture | What `validate-variant` prints | Raw `cue vet` diagnostic | Cause → fix |
|---|---|---|---|
| `unknown_field` · `unknown-field.yaml` | `✗ depth 1 unknown-field.yaml:12:1 [unknown_field] #TermDeposit.promo_flag: field not allowed` | `promo_flag: field not allowed` | An undeclared/misspelled field. The schema is **closed** — there is no escape hatch. **Fix:** remove or correct the field (e.g. a typo'd `interst_variant`). Why closedness helps you: [reading a CUE schema](../explanation/reading-a-cue-schema.md#why-closedness-is-a-feature). |
| `shape_mismatch` · `unbound-day-count.yaml` | `✗ depth 1 …/common.cue:55:22 [shape_mismatch] #TermDeposit.day_count: invalid value "Act/365" (out of bound =~"^[a-z]{2}\.[a-z0-9_]+(\.[a-z0-9_]+)*$")` | `day_count: invalid value "Act/365" (out of bound =~"…")` | A **free string** where a pack-bound *reference* belongs. `day_count: "Act/365"` doesn't *set* Act/365 — it's a bare string, not a dotted reference. **Fix:** use a pack-bound reference like `pt.act_360`. |
| `shape_mismatch` · `malformed-version-key.yaml` | `✗ depth 1 schema [shape_mismatch] schema pin "term_deposit-2026.1" does not resolve to a known family schema: unknown product family "term_deposit-2026.1" (v1 supports: term_deposit)` | `schema: invalid value "term_deposit-2026.1" (out of bound =~"^[a-z_]+@[0-9]{4}\.[0-9]+$")` | The `schema:` pin is missing the `@YYYY.N` form (`term_deposit@2026.1`). The version key travels onto `DepositConstituted` and never moves, so a malformed pin is rejected at the door. **Fix:** write the pin as `<family>@YYYY.N`. |
| `missing_field` · `rateref-missing-role.yaml` | `✗ depth 1 …/common.cue:69:17 [missing_field] #TermDeposit.rate.flat.rate_ref.role_selector: incomplete value =~"^[a-z][a-z0-9_]*$"` | `rate.flat.rate_ref.role_selector: incomplete value =~"…"` | A required field is absent — here `rate_ref` omits `role_selector`. `#RateRef` is closed and both fields are required. **Fix:** supply the missing required field. |

### Depth 2 — type / range + primitive resolution (fix is in *your* file)

| Reason code · fixture | What `validate-variant` prints | Raw `cue vet` diagnostic | Cause → fix |
|---|---|---|---|
| `type_mismatch` · `non-eur-currency.yaml` | `✗ depth 2 …/term-deposit.cue:35:12 [type_mismatch] #TermDeposit.currency: conflicting values "USD" and "EUR"` | `currency: conflicting values "EUR" and "USD"` | A field is the wrong value/type. v1 is **EUR-only** — `currency` is the literal `"EUR"`. **Fix:** set `currency: EUR`. |
| `out_of_range` · `principal-max-below-min.yaml` | `✗ depth 2 …/term-deposit.cue:158:14 [out_of_range] #TermDeposit.principal_bounds.max_cents: invalid value 50000 (out of bound >=100000)` | `principal_bounds.max_cents: invalid value 50000 (out of bound >=100000)` | An **inverted corridor**: `max_cents` is below `min_cents`. The cross-field guard requires `max_cents >= min_cents`. **Fix:** raise `max_cents` above `min_cents` (or lower `min_cents`). |
| `unknown_primitive` · `depth2-unknown-primitive.yaml` | `✗ depth 2 day_count [unknown_primitive] day_count "pt.act_999" resolves to no day-count primitive in pack pt.2026.1` | *(resolved Go-side, not a raw `cue vet` line)* | A **well-formed** reference (depth 1 passed) that names no primitive the pinned pack carries. **Fix:** name a primitive the pack's `primitives/day-count.yaml` lists, or [add it to the pack](./add-a-day-count-primitive.md). |

### Depth 3 — pack compliance (fix is in *your* file)

| Reason code · fixture | What `validate-variant` prints | Cause → fix |
|---|---|---|
| `pack_bound_violation` · `depth3-wrong-pack.yaml` | `✗ depth 3 pack [pack_bound_violation] variant pins pack "pt.2099.1" but is being validated against pack "pt.2026.1"` | The variant's `pack:` and the `PACK=` you validated against disagree. **Fix:** pin the pack you mean, or validate against the pack the variant pins. |

### Depth 4 — regulatory coherence (the pack judging a *well-formed* variant)

Depths 1–3 all passed — the variant is well-formed and its references resolve —
and it is *still* rejected, because the pack's regulatory law forbids it for this
family. A depth-4 rejection almost always means **your variant should comply**,
not that the pack should change; changing what a pack *permits* is a heavier,
slower pack change. The deeper treatment is
[troubleshoot a variant rejection §depth 4](./troubleshoot-a-variant-rejection.md#depth-4--the-pack-forbids-it-regulatory-coherence).

| Reason code · fixture | What `validate-variant` prints | Cause → fix |
|---|---|---|
| `forbidden_day_count` · `depth4-act365-deposit.yaml` | `✗ depth 4 day_count [forbidden_day_count] day-count "act_365" is not regulatorily permitted for a PT term_deposit (pack pt.2026.1 permits: act_360)` | The pack *carries* `act_365` but does not **permit** it for a term deposit (its `permitted_for` set excludes the family). **Fix:** use a permitted day-count (`pt.act_360`). |
| `forbidden_renewal_policy` · `depth4-same-term-same-rate.yaml` | `✗ depth 4 auto_renewal_policy [forbidden_renewal_policy] auto-renewal policy "SAME_TERM_SAME_RATE" is pack-restricted and not permitted for a PT term_deposit (pack pt.2026.1; 02 §2.4.4)` | A pack-restricted renewal policy. **Fix:** choose an unrestricted policy. |
| `non_ascending_steps` · `depth4-descending-steps.yaml` | `✗ depth 4 rate.stepped.steps.1.from_day [non_ascending_steps] from_day 90 is not strictly greater than the preceding step (365)` | Stepped-rate `from_day`s are not strictly ascending. **Fix:** sort the steps so each `from_day` strictly exceeds the previous. |
| `open_tail_not_last` · `depth4-open-tail-not-last.yaml` | `✗ depth 4 early_termination.banded.0.up_to_days [open_tail_not_last] the open (null) up_to_days band must be the single last band` | The open (`null`) early-termination band is not last. **Fix:** move the open band to the end (it must be the single last element). |

---

## Honest limits

- **Depth 5 (sealed-corpus engine simulation) does not run locally** — it is
  engine-generated and still pending, so a clean `pack-validate` /
  `validate-variant` proves depths 1–4 only, not the *accrual output*. See
  [ADR-PC-006 §P4](../../product-management/product_concepts/adrs/ADR-PC-006-cue-schema-language.md).
- **The rate-sheet cross-artefact checks pass vacuously** until the
  product-config registry lands (section A note above).

## See also

- [Reading a CUE schema](../explanation/reading-a-cue-schema.md) — *why* a rule rejects what it rejects (the understanding this decoder assumes).
- [Troubleshoot a variant rejection](./troubleshoot-a-variant-rejection.md) — the deeper, variant-only depth → fix companion.
- [Validate a pack locally](./validate-a-pack-locally.md) — the `make pack-validate` inner loop.
- [Author and deploy a rate sheet](./author-and-deploy-a-rate-sheet.md) — where the section-A deploy rejections come from.
- [Add a day-count primitive](./add-a-day-count-primitive.md) — authoring the `permitted_for` rule a `forbidden_day_count` enforces.
- [Product-docs front door](../README.md).
