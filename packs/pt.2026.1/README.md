# pt.2026.1 — PT term-deposit pack

The first regulatory pack in the PT line (ADR-PC-007). Jurisdiction-scoped
vocabulary the engine resolves against at constitution and pins per instance
for life (surface §3.5). **Declarative data, not code.**

## Contents

| Path | What |
|---|---|
| `pack.yaml` | Manifest — identity, metadata, deps, pins (§P1) |
| `primitives/day-count.yaml` | Day-count conventions (default `act_360`) |
| `primitives/withholding.yaml` | 28% IRS on deposit interest (`irs_juros`) |
| `primitives/fgd.yaml` | Deposit-guarantee-fund coverage (€100k) |
| `primitives/reporting.yaml` | Regulatory reporting hooks (BdP retail-rate stats + FGD coverage active) |
| `primitives/renewal-policies.yaml` | Auto-renewal-policy restrictions (SAME_TERM_SAME_RATE is pack-restricted) |
| `families.yaml` | Family-set roster the deployment may run (ADR-PC-007 §P1) |
| `parameters/constants.yaml` | Pack-level scalar constants |
| `rate-sheet-refs/deposits-pt.yaml` | Version-pinned term-deposit rate-sheet ref (ADR-PC-008) |
| `rate-sheet-refs/current-account-pt.yaml` | Version-pinned current-account overdraft-interest rate-sheet ref (ADR-PC-008; ADR-PC-037) |
| `rate-sheet-refs/loans-pt.yaml` | Version-pinned personal-loan fixed-rate rate-sheet ref (ADR-PC-008; ADR-PC-030) |
| `test-corpus/` | Sealed regression evidence (§3.9) |
| `schemas/` | **Not committed** — staged at build from `/contracts/cue` |

## Version key

`pt.2026.1` = `<pack_id>.<pack_version>`. **Immutable once published.** The
engine pins it per instance via the event envelope (ADR-PC-009); a correction
ships as a new version, never an in-place edit.

## Changelog

- **2026.1** — Initial pack. Act/360 day-count, 28% IRS withholding, FGD
  coverage, BdP retail-rate statistics hook. No prior pack.
  - **F.7** — full v1 *input* surface: five `/product-configs` variants covering
    every interest shape the family schema carries (AT_MATURITY, periodic-monthly,
    periodic-quarterly, advance, banded early-termination) and one sealed-corpus
    canonical instance per shape. No new primitives; rate by ref only; withholding
    unchanged (2800 bps `irs_juros`, gross interest); day-count `act_360` only.
  - **Personal-loan pricing** (bd `babelstone-u79p.7`) — added
    `rate-sheet-refs/loans-pt.yaml`, the version-pinned `personal_loan` rate-sheet
    ref (→ `pt-loans-2026.1`), so the pack can price the loan family
    (ADR-PC-030 / ADR-PC-008). Two priced loan variants land under
    `product-configs/personal-loan/` (`cp_pt_general_36m`,
    `cp_pt_education_24m_gated`), each carrying a `rate_ref` `#FixedRate` block; the
    ref is what makes their pack-validate depth-3 pass (a variant with a `rate:`
    block needs the pack to price its family, `KindUnresolvedRateRef` otherwise).
    This is what lets a personal loan run LIVE·engine, not DEMO-only. Loan variants
    live in their own subdirectory so the term-deposit product-config store (which
    reads `product-configs/*.yaml` non-recursively and requires `term_days` /
    `interest_variant`) never sees them.

## v1 surface & deferrals (F.7)

This pack carries **what the engine WILL compute, never the computed results.**
What it declares vs. what is deliberately deferred:

**Delivered (inputs only):**

| Shape | Variant (`/product-configs`) | Corpus instance |
|---|---|---|
| AT_MATURITY, single coupon | `dpz_pt_12m_juros_venc` | `pt_dpz_12m_simple_with_irs` |
| PERIODIC monthly (`m=1`) | `dpz_pt_12m_juros_mensal` | `pt_dpz_12m_periodic_monthly_with_irs` |
| PERIODIC quarterly (`m=3`) | `dpz_pt_24m_juros_trimestral` | `pt_dpz_24m_periodic_quarterly_with_irs` |
| ADVANCE (juros antecipados) | `dpz_pt_6m_juros_antecipados` | `pt_dpz_6m_advance_with_irs` |
| Banded early-termination | `dpz_pt_18m_resgate_escalonado` | `pt_dpz_18m_banded_termination_with_irs` |

Each variant references the rate sheet by `rate_ref` only and reuses the existing
primitives (`act_360`, `irs_juros` at 2800 bps on gross interest). The corpus
instances carry the rate the engine **resolves** from the sheet at constitution
(`rate_basis_points`, ADR-PC-008), pinned per instance for deterministic
regression — the per-instance pinning discipline is ADR-PC-009 §P1/§P5, to which
ADR-PC-008 §P3 defers. These are sealed-corpus inputs, not pack-authored
rate-sheet data.

**Resolved upstream since first draft (Epic 0.3 / 0.4 — no further F.7 pack work):**

- **Pack-effective-date semantics** (0.3, bd `babelstone-oa3i`). v1 **pins
  everything at constitution and floats nothing** — `pack_effective_from` is
  informational metadata only (ADR-PC-009 §P5, *Revised 2026-06-10*). Per-primitive
  pin-or-float is the **confirmed v2+ direction**, not a v1 deliverable, so
  `pt.2026.1` needs **no new effective-date machinery**; the mid-life
  statutory-rate escape hatch stays the explicit `PackVersionMigrated` event
  (ADR-PC-009 §P3).
- **BdP signal inventory** (0.4, bd `babelstone-gjyl`; 04-open-questions §Q-AX).
  The v1 named-report set is **three returns**, all declared in this pack: BdP
  retail-rate statistics (`reporting.yaml: bdp_estatisticas_taxas_juro`, monthly),
  IRS Modelo 39 (`withholding.yaml: reporting.modelo_39`, annual), and FGD
  coverage (`reporting.yaml: fgd_cobertura_depositos`, annual + on-demand). The
  engine emits **subject references only — never PII** on the durable bus; a
  downstream application assembles each return. The per-report engine-signal
  field contract F.7 consumes is recorded in 04-open-questions §Q-AX.

**Still deferred (documented here, never stubbed with fictional content):**

- **TANB / TANL / TAE / TAEG** — engine-computed derived figures
  (financial_concepts §5.4 for TANB/TANL/TAE, §6.2 for TAEG), never pack
  primitives or inline rate fields. The
  pack carries the rate-sheet **ref** plus the withholding rate only.
- **`expected-events.yaml`** — the **GENERATED** sealed corpus
  (ADR-PC-007 §P5), now populated. It is produced by replaying the engine's
  hand-rolled substrate over the canonical instances and committed; the depth-5
  simulation gate (`PackSimulationDepth5Tests`) asserts the replayed event
  sequences field-for-field against it and fails CI on any drift. Regenerate
  with `BABELSTONE_DEPTH5_GENERATE=1 dotnet test` on `PackSimulationDepth5Tests`.
- **FIN data-field reconciliation** — aligning 02 §2.4.1's `WithholdingApplied` /
  `InterestAccrued` / `InterestPaid` payload sketches with the canonical (minimal)
  `.avsc` shapes is an engine/contract concern tracked separately in bd
  `babelstone-50vy`, not pack data.
- **Rate-sheet bodies** — the numeric TANs live in the `rate_sheets` table on
  their own cadence (ADR-PC-008; C.6). A variant's `rate_ref` resolving requires
  the referenced sheet (`pt-deposits-2026.1`) to be published — a **C.6 deploy
  prerequisite**, not pack content.

## Building & verifying

```sh
make pack-validate PACK=pt.2026.1  # stage schemas + cue-vet manifest & data
make pack-build    PACK=pt.2026.1  # validate + oras push, prints the digest
make pack-verify   PACK=pt.2026.1  # build, then pull by digest + re-validate
```

The build copies the digest-pinned family schemas from `/contracts/cue` into a
staging `schemas/` directory so the artefact is self-contained, then packages a
deterministic OCI artefact (media type `application/vnd.babelstone.pack.v1+yaml`)
pulled by digest, never by tag (ADR-PC-007 §P2). Keyless cosign signing in CI
(OIDC) lands with **Q.5**; `pack.sh` carries the `sign`/`verify-signature`
commands for it.
