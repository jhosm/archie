# Credit Cards — Market Research Plan

> **Status:** Promoted into the doc corpus (2026-06-20). Originally pure market research
> produced on scratch terms; it now **supports a real decision** —
> [ADR-PC-030](../../adrs/ADR-PC-030-product-scope-and-boundary.md) cites it as the
> motivating research for babelstone's product scope (the credit-card *account/revolving
> slice* is in scope; the four-party scheme is not). The briefs remain **descriptive
> market research** — they perform no engine/ADR/contract design themselves; that lives in
> ADR-PC-030.

## In plain English

We're going to figure out, from the ground up, **what a credit card actually is, how it
works in Portugal specifically, and what the Portuguese banks and card specialists are
selling today.** We do it in three steps that build on each other: first the universal
"what is a credit card" picture (true anywhere in the world), then the same picture
re-drawn with Portugal's rules, taxes, and habits, and finally a survey of who offers what
in the Portuguese market and how their products compare.

This first pass is written from existing knowledge so we can lock down the *structure* and
the *right questions* quickly. Every concrete number that can drift — annual fees, interest
rates (TAN/TAEG), rewards, FX markups — gets a visible **`[REFRESH]`** tag and is collected
in a "Figures to verify" appendix at the end of each brief, so we know exactly what to
confirm against live sources before anyone relies on it.

## Scope & ground rules (locked with the user)

| Decision | Choice |
|---|---|
| **Output format** | Separate markdown briefs in `research/credit-cards/` (this folder) |
| **Issuer coverage (Q3)** | Universal banks **+** card specialists/monoliners **+** digital/fintech |
| **Method / data currency** | Domain knowledge first; every volatile figure tagged `[REFRESH]` |
| **babelstone coupling** | None yet — deliberately deferred |

## The three briefs (sequenced — each constrains the next)

The work funnels from the **abstract product** → the **jurisdiction-specific instance** →
the **live competitive instances**. Brief 01 gives us the vocabulary and the dimension list;
Brief 02 specialises those dimensions to Portugal; Brief 03 populates them with real issuers.

---

### Brief 01 — `01-fundamentals.md` — What is a credit card? (jurisdiction-agnostic)

The universal mechanics, taxonomy, and lifecycle — the conceptual backbone reused by 02/03.

**1. Definition & boundaries**
- Credit card defined; how it differs from **debit**, **charge**, **deferred-debit**,
  **prepaid**, and **installment/BNPL** instruments (the "card spectrum").
- The card as *access device* to a **revolving unsecured credit line** vs. card-as-product.

**2. Core mechanics**
- Credit limit, available credit, authorizations & holds.
- Billing cycle, statement, **grace period**, due date.
- **Minimum payment** vs. full payment vs. revolving balance.
- **Transactor vs. revolver** behaviour (the central economic split).
- Interest: purchase APR, cash-advance APR, penalty/default APR; how the grace period is
  lost; balance-computation methods (e.g. average daily balance).

**3. The payments value chain (four-party scheme model)**
- Roles: cardholder, merchant, **issuer**, **acquirer**, **scheme/network** (Visa,
  Mastercard, Amex three-party variant).
- Money flows: **interchange**, merchant discount rate (MDR/MSC), scheme fees, **float**.
- Card-not-present vs. card-present; tokenization, EMV, contactless, 3-D Secure.

**4. Issuer economics — how a credit card makes money**
- Net interest income (revolvers), interchange (transactors), fee income
  (annual, late, cash-advance, FX), and the cost side (cost of funds, fraud, rewards,
  capital, operations).

**5. Product taxonomy / variants** (the dimensions that become the comparison axes in 03)
- By **credit mechanics:** charge, revolving, deferred-debit, installment.
- By **segment:** consumer, student, **secured**, commercial (business / corporate /
  purchasing P-cards).
- By **value proposition:** rewards (cashback / points / miles), **co-branded**
  (airline, retail), affinity, premium/travel, low-APR / balance-transfer, store cards.
- By **tier:** classic → gold → platinum → signature/world → infinite/world-elite.
- By **scheme:** Visa / Mastercard / Amex / domestic.

**6. Lifecycle** (three nested clocks)
- **Product lifecycle:** design → pricing/underwriting policy → launch → portfolio mgmt →
  repricing → retirement.
- **Customer/account lifecycle:** application → KYC + credit assessment/scoring → decision
  & limit assignment → issuance/activation → usage → billing → repayment → servicing
  (limit changes, renewals, reissue on expiry/loss) → **delinquency → collections →
  charge-off/recovery** → closure.
- **Transaction lifecycle:** authorization → clearing → settlement → posting →
  dispute/chargeback.

**7. Risk & control backdrop (generic)**
- Credit risk, fraud risk, operational risk; PCI-DSS; consumer-protection/disclosure norms.

**Figures to verify:** (mostly none — this brief is conceptual; flag any global stats cited.)

---

### Brief 02 — `02-portugal-context.md` — Credit cards in Portugal

Re-draw Brief 01 with Portuguese terminology, regulation, taxes, and market habits.

**1. Portuguese vocabulary**
- *Cartão de crédito* vs. *cartão de débito* vs. *cartão de crédito de pagamento diferido*
  (deferred-debit) vs. *pagamento em prestações* (installments).
- **TAN** (taxa anual nominal) vs. **TAEG** (taxa anual de encargos efetiva global);
  *comissões*, *MTIC* (montante total imputado ao consumidor).

**2. Regulatory framework**
- **Banco de Portugal** supervision of retail conduct.
- **Decreto-Lei n.º 133/2009** — consumer credit regime (transposes the EU Consumer Credit
  Directive); mandatory *avaliação de solvabilidade*, **FIN** (Ficha de Informação
  Normalizada), 14-day **right of free withdrawal**, early repayment.
  *Note:* CCD2 (Directive (EU) 2023/2225) transposition is on the horizon — flag timing.
- **Maximum-rate (usury) regime:** Banco de Portugal publishes **quarterly maximum TAEG
  ceilings** by credit type, including the *cartões de crédito / linhas de crédito / contas
  correntes / facilidades de descoberto* bucket and *crédito revolving*. This rate cap is a
  defining feature of the PT market and pushes pricing toward the ceiling. `[REFRESH]` the
  current quarter's caps.
- **PARI / PERSI** (Decreto-Lei n.º 227/2012) — pre-default (PARI) and out-of-court
  default-resolution (PERSI) regimes; reshape the delinquency/collections lifecycle vs. 01.
- **Imposto do Selo** (stamp duty) on credit utilization and on interest/commissions — a
  PT-specific cost layer that materially affects the true cost of revolving. `[REFRESH]`
  current rates.
- **EU Interchange Fee Regulation** ((EU) 2015/751): 0.3% credit / 0.2% debit caps apply.
- **Central de Responsabilidades de Crédito (CRC)** at Banco de Portugal — credit register
  feeding solvency assessment (the closest PT analogue to a credit bureau).
- **PSD2 / SCA**, **RGPD/GDPR**, fee-transparency rules on *comissões*.

**3. Domestic infrastructure**
- **SIBS** (processor/owner of **Multibanco** and **MB WAY**); domestic acquiring/processing.
- **Unicre / UNIBANCO** — historical card issuer + acquirer.
- Most PT credit cards ride Visa/Mastercard rails but are processed domestically via SIBS.

**4. Market characteristics (the "why PT is different" section)**
- Lower revolving-credit usage than US/UK; **debit and deferred-debit dominate**.
- "Cartão de crédito" frequently used in *full-payment / deferred-debit* mode, not revolving.
- Strong **installment ("prestações")** and retail-loyalty culture. `[REFRESH]` penetration
  and revolving-share statistics.

**5. PT-adapted lifecycle**
- Application/KYC under PT AML; mandatory solvency assessment + CRC consultation; FIN
  delivery; withdrawal right; PERSI on default; reissue/expiry norms.

**Figures to verify:** maximum TAEG caps (current quarter), imposto do selo rates, market
penetration & revolving share, any cited BdP statistics.

---

### Brief 03 — `03-competitive-landscape-pt.md` — Who offers what in Portugal

Survey + comparison matrix across the agreed wide issuer set. **All pricing `[REFRESH]`.**

**1. Market structure & sizing** — penetration, scheme split, key BdP/SIBS stats `[REFRESH]`.

**2. Player taxonomy**
- **Universal / incumbent banks:** Caixa Geral de Depósitos (CGD), Millennium BCP,
  Santander Totta, Novo Banco, BPI (CaixaBank group), Bankinter, Crédito Agrícola, Abanca,
  Montepio, EuroBic.
- **Digital arms:** ActivoBank (BCP), Moey (Crédito Agrícola), Openbank (Santander).
- **Card specialists / monoliners (consumer finance):** **WiZink Bank**, **Unicre/UNIBANCO**
  (incl. the **Universo** card), **Cofidis**, **Cetelem** (BNP Paribas Personal Finance),
  Oney, Younited.
- **Retail co-brand:** **Universo** (Sonae MC + Unicre, Continente ecosystem) — outsized in
  PT; Cartão Continente loyalty gravity.
- **Digital / neobank / fintech & BNPL:** **Revolut** (very high PT adoption), N26;
  BNPL via Klarna, Cofidis, Oney.

**3. Per-issuer comparison dimensions** (one row per card; populated, every figure `[REFRESH]`)
- Card portfolio & tiers (classic/gold/platinum/premium; co-brands).
- **Pricing:** annual fee (*anuidade*), TAN, TAEG, default interest, cash-advance,
  **FX/MTF markup**, imposto-do-selo treatment.
- **Repayment options:** full / minimum / installments (*modalidade de pagamento*).
- **Rewards:** cashback, points, Continente/retail discounts, miles, partnerships.
- **Value-adds:** travel insurance, purchase protection, lounge/concierge (premium).
- **Digital:** app, MB WAY, Apple/Google Pay, virtual cards, on-demand installments.
- **Positioning / distinctive angle.**

**4. Cross-cutting themes**
- Pricing convergence toward the TAEG cap; dominance of deferred-debit; **Continente/Universo
  loyalty gravity**; **Revolut** disruption; **BNPL** encroachment on revolving.

**Deliverable shape:** a **comparison matrix** (issuers × dimensions) + short per-issuer
profiles + the themes. Everything quantitative carries `[REFRESH]`.

**Figures to verify:** every fee, rate, reward rate, and market-share number in the brief.

---

## Methodology & conventions

- **One concept, one brief; briefs build in order** (01 → 02 → 03). I won't duplicate
  definitions across briefs — 02 and 03 reference 01's taxonomy.
- **`[REFRESH]` marker** on every figure/claim whose currency I can't guarantee from memory.
- **"Figures to verify" appendix** at the foot of each brief consolidates the `[REFRESH]`
  items into a checklist for a later targeted live-verification pass.
- **Authoritative-source shortlist** (for the eventual refresh): Banco de Portugal
  (*todoscontam* / maximum-rate tables / retail-banking reports), SIBS, issuer pre-contractual
  *FIN* documents and price lists (*preçários*), DECO/Deco Proteste comparisons. I'll record
  exact source pointers as a `sources.md` during the refresh pass, not invent them now.
- **Plain-English first** in each brief, then the formal detail (house style).

## Sequencing & dependencies

1. **Brief 01** (no dependencies) → establishes vocabulary + dimension list.
2. **Brief 02** (depends on 01) → specialises to PT regulation/tax/habits.
3. **Brief 03** (depends on 01 + 02) → populates the matrix with live-ish issuer data.
4. *(Optional, later)* **Refresh pass** → resolve all `[REFRESH]` tags against live sources;
   add `sources.md`.
5. *(Optional, much later)* **babelstone bridge note** → only if/when we decide to model a
   `credit_card` family; explicitly out of scope now.

## Open questions / risks

- **Penetration data is noisy.** PT revolving-vs-deferred split is the single most
  decision-relevant statistic and the easiest to get wrong from memory → hard `[REFRESH]`.
- **Maximum-rate caps move quarterly** → any TAEG figure is stale on arrival; treat the
  *mechanism* as the durable insight, the *number* as perishable.
- **Co-brand churn:** Universo/WiZink/Sonae and bank co-brand partnerships change; verify
  current issuing relationships before asserting them.
- **Commit/branch decision:** if you want this folder version-controlled in babelstone,
  I'll move to a dedicated branch + worktree per repo policy. As scratch, it stays local.

## Out of scope (for now)

- Any babelstone engine / family / ADR / contract design.
- Non-Portuguese competitive analysis.
- Quantitative profitability modelling of a hypothetical card product.
- Legal/compliance opinion (we describe the regime; we don't advise on it).
