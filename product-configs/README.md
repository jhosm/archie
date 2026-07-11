# /product-configs

Product-team-authored **variant configurations** — the *structure* artefact of the
three-owner configuration surface ([01 §3](../docs/product-management/product_concepts/01-product-architecture.md)).
Declarative variants layered on a family schema (`/families`) and a chosen pack
(`/packs`); numerical rates come from `/rate-sheets`.

- **Build provenance:** in-house (config data, not engine code)
- **CODEOWNERS:** **Product team** (one of the three config-surface owners)
- **Cadence:** days–weeks
- **Path-scoped CI:** structural validation via `/pack-validate` against the family schema + active pack

This path exists so the three-owner split is enforceable by `CODEOWNERS`: the
cheapest, most frequent change (a variant) does not inherit the most expensive
approval (the pack). See [ADR-PC-019 F2](../docs/product-management/product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md).

## Variants

| File | Family | Pack | Shape |
|---|---|---|---|
| `dpz_pt_12m_juros_venc.yaml` | `term_deposit@2026.1` | `pt.2026.1` | 12-month, AT_MATURITY simple interest, Act/360 |
| `dpz_pt_12m_juros_mensal.yaml` | `term_deposit@2026.1` | `pt.2026.1` | 12-month, PERIODIC monthly coupons (`m=1`), Act/360 |
| `dpz_pt_24m_juros_trimestral.yaml` | `term_deposit@2026.1` | `pt.2026.1` | 24-month, PERIODIC quarterly coupons (`m=3`), Act/360 |
| `dpz_pt_6m_juros_antecipados.yaml` | `term_deposit@2026.1` | `pt.2026.1` | 6-month, ADVANCE interest (juros antecipados), Act/360 |
| `dpz_pt_18m_resgate_escalonado.yaml` | `term_deposit@2026.1` | `pt.2026.1` | 18-month, AT_MATURITY, banded early-termination schedule, Act/360 |
| `dpz_pt_12m_resgate_parcial.yaml` | `term_deposit@2026.1` | `pt.2026.1` | 12-month, AT_MATURITY, partial-withdrawal policy (*resgate parcial*, F.12), Act/360 |
| `dpz_pt_12m_mensal_resgate_parcial.yaml` | `term_deposit@2026.1` | `pt.2026.1` | 12-month, PERIODIC monthly coupons (`m=1`) **+** partial-withdrawal policy (F.12 orthogonality), Act/360 |
| `current-account/ca_pt_standard.yaml` | `current_account@2026.1` | `pt.2026.1` | Standard demand account, arranged overdraft + velocity caps, overdraft-interest `rate_ref` |
| `current-account/ca_pt_basic.yaml` | `current_account@2026.1` | `pt.2026.1` | Basic demand account, no overdraft, no `rate` block |
| `personal-loan/cp_pt_general_36m.yaml` | `personal_loan@2026.1` | `pt.2026.1` | 36-month general-purpose loan, French amortization, fixed-rate `rate_ref`, 50 bps early-repayment cap |
| `personal-loan/cp_pt_education_24m_gated.yaml` | `personal_loan@2026.1` | `pt.2026.1` | 24-month education-purpose (lower-cap) loan, fixed-rate `rate_ref`, 25 bps early-repayment cap, origination preconditions declared |

The `current-account/` and `personal-loan/` variants live in per-family
subdirectories, not at the root. The term-deposit product-config store reads
`product-configs/*.yaml` **non-recursively** and requires term-deposit shape fields
(`term_days`, `interest_variant`), so a current-account or loan config placed at the
root would break its load; the subdirectory keeps the families' config surfaces
decoupled. The `personal-loan/cp_pt_*` configs carry a fixed-rate `rate_ref`
(`#FixedRate`) that resolves against the pack's `loans-pt` rate-sheet ref — the pack
pricing that lets a personal loan run LIVE·engine rather than DEMO-only.

`dpz_pt_12m_juros_venc` is the walking-skeleton variant (E.2). The four siblings
land in F.7, completing the v1 input surface: every interest shape the family
schema carries (the three `interest_variant`s × the flat/banded early-termination
split) is represented by one variant and one sealed-corpus canonical instance
([packs/pt.2026.1/test-corpus](../packs/pt.2026.1/test-corpus/canonical-instances.yaml)).
`dpz_pt_12m_resgate_parcial` is the F.12 follow-on: the first variant to declare a
`partial_withdrawal` policy, so the partial-withdrawal decider is exercised by a
real product (its own canonical instance lands with the depth-5 leg, F.12 wiring 4/4).
`dpz_pt_12m_mensal_resgate_parcial` then demonstrates that the policy is **orthogonal
to the interest cadence** the schema places it beside: the same `partial_withdrawal`
block riding a PERIODIC monthly-coupon deposit instead of an AT_MATURITY one — the
coupon schedule is untouched, only the base of later coupons shrinks after a
withdrawal (config-surface coverage; depths 1–4, no canonical instance of its own).
The variants reference the rate sheet by ref only — no inline rates — and reuse
the pack's existing primitives (`act_360`, `irs_juros`); TANB/TANL/TAE stay
engine-computed (financial_concepts §5.4), never variant fields.

Validate any variant through depths 1–4 against the pack it pins:

```sh
make validate-variant VARIANT=product-configs/dpz_pt_12m_juros_venc.yaml
```

The `product-configs` CI job runs this over every committed variant on each PR.
