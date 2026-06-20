# Crédito Pessoal — Market Research Plan

> **Status:** Plan. Pure market research — **not** babelstone product work. No
> engine/ADR/contract concerns in scope. This folder (`research/credito-pessoal/`) is a
> scratch deliverable; nothing here is committed to a babelstone doc series unless we later
> decide to promote it. Sibling of the credit-card research in
> [`../credit-cards/`](../credit-cards/00-research-plan.md), produced on identical terms.

## In plain English

We're going to figure out, from the ground up, **what a _crédito pessoal_ (a Portuguese
personal loan) actually is, how it works in Portugal specifically, and what the lenders
operating in Portugal are selling today.** A crédito pessoal is the plain-vanilla loan most
people picture: the bank hands you a **fixed sum once**, and you pay it back in **equal monthly
installments** over a **fixed number of years** — no card, no re-borrowing, no revolving line.

We do it in three steps that build on each other: first the universal "what is an amortizing
personal loan" picture (true anywhere), then the same picture re-drawn with Portugal's rules,
taxes, and habits, and finally a survey of who lends what in the Portuguese market and how the
offers compare.

The single most Portugal-specific twist — and the spine of brief 02 — is that the **legal price
ceiling depends on what the loan is _for_**: Banco de Portugal sets a **lower** maximum cost
for loans earmarked for **education, health, renewable energy, or equipment leasing** than for
a general-purpose loan with no stated purpose.

This first pass is written from existing knowledge so we can lock down the *structure* and the
*right questions* quickly. Every concrete number that can drift — interest rates (TAN/TAEG), the
maximum-TAEG caps per purpose category, fees, loan amounts, terms, market share — gets a visible
**`[REFRESH]`** tag and is collected in a "Figures to verify" appendix at the end of each brief,
so we know exactly what to confirm against live sources before anyone relies on it.

## Scope & ground rules (locked with the user)

| Decision | Choice |
|---|---|
| **What "crédito pessoal" means here** | A **fixed-amount, fixed-term, fully amortizing, unsecured** personal loan (lump-sum disbursement, level installments). **Not** the broad consumer-credit umbrella, **not** cards, **not** *crédito automóvel*, **not** revolving. |
| **Output format** | Separate markdown briefs in `research/credito-pessoal/` (this folder) |
| **Lender coverage (Q3)** | Universal banks **+** consumer-finance specialists **+** digital/fintech — each confirmed to actually offer crédito pessoal |
| **Method / data currency** | Domain knowledge first; every volatile figure tagged `[REFRESH]` |
| **Relationship to the card research** | **Reference, don't duplicate** — shared PT regime (DL 133/2009, caps, imposto do selo, FIN, PARI/PERSI, CRC) is cross-linked to [`../credit-cards/`](../credit-cards/02-portugal-context.md) |
| **babelstone coupling** | None — deliberately decoupled (ADRs touched = NONE) |

## The three briefs (sequenced — each constrains the next)

The work funnels from the **abstract product** → the **jurisdiction-specific instance** → the
**live competitive instances**. Brief 01 gives us the vocabulary and the dimension list; Brief
02 specialises those dimensions to Portugal; Brief 03 populates them with real lenders.

---

### Brief 01 — `01-fundamentals.md` — What is crédito pessoal? (concept-level)

The universal **amortizing, unsecured installment-loan** mechanics, taxonomy, and lifecycle —
the conceptual backbone reused by 02/03. (The term is Portuguese; the *instrument* is universal.
PT law/tax/caps are deferred to 02.)

**1. Definition & boundaries — the "consumer-credit spectrum"**
- Crédito pessoal defined: fixed principal, fixed term, **fully amortizing**, **unsecured**,
  lump-sum up front, repaid in level installments.
- Defined by contrast with its neighbours: revolving / **credit card** (→ reference
  `../credit-cards/`), **overdraft / conta corrente**, **crédito automóvel** (purpose-tied,
  often secured by the vehicle), **mortgage** (secured by property), **leasing / locação
  financeira**, **BNPL / installment-at-POS**.

**2. Core mechanics**
- Capital, prazo, **TAN** (nominal rate); amortization methods (**French / constant-installment
  = _prestação constante_** vs constant-capital); the **amortization schedule (_quadro de
  amortização_)** — juros-heavy early, capital-heavy late.
- Fixed vs variable rate (Euribor + spread); **TAEG / MTIC** as the all-in cost; the
  level-installment formula.

**3. Variant taxonomy** (becomes the comparison axes in 03)
- **By purpose:** *sem finalidade específica* (general-purpose) vs *com finalidade específica*
  (educação, saúde, energias renováveis, locação financeira de equipamentos, lar/obras…).
- **Debt consolidation (_crédito consolidado_)** as an **adjacent form** (described, not core).
- **By rate** (fixed/variable); **by guarantee** (unsecured / with *fiador*); **by channel**
  (branch / online / point-of-sale).

**4. Lender economics** — net interest margin over the term, *comissão de abertura*, insurance
cross-sell, vs cost of funds / credit losses / servicing. Contrast with card economics (no
interchange — pure lending margin).

**5. Lifecycle** — product clock; loan/customer clock: application → KYC → solvency/affordability
+ scoring → decision + pricing → contract + disclosures + withdrawal window → **lump-sum
disbursement** → **amortization (level installments)** → servicing → **early repayment** →
maturity/closure **or** default → restructuring → collections → recovery.

**6. Early-repayment mechanics** (generic) — partial vs full; lender compensation principle.

**7. Risk & control backdrop (generic)** — credit risk dominant; affordability / responsible
lending; over-indebtedness; standardised disclosure (EU SECCI → PT FIN).

**Figures to verify:** mostly none — conceptual; any global stat flagged `[REFRESH]`.

---

### Brief 02 — `02-portugal-context.md` — Crédito pessoal in Portugal

Re-draw Brief 01 with Portuguese terminology, regulation, taxes, and market habits.

**1. Portuguese vocabulary** — crédito pessoal, crédito aos consumidores, finalidade, montante,
prazo, prestação, TAN/TAEG/MTIC, comissão de abertura, reembolso antecipado, crédito
consolidado, fiador.

**2. Regulatory framework** (reference card brief 02 for the shared spine; focus on
personal-loan specifics)
- **DL 133/2009** — *avaliação de solvabilidade*, **FIN**, 14-day withdrawal, and the
  **early-repayment compensation cap** specific to amortizing loans (≈0.5% of capital repaid if
  >1 yr remaining / ≈0.25% if ≤1 yr). `[REFRESH]`. CCD2 horizon `[REFRESH]`.
- **★ The defining PT feature — purpose-segmented maximum-TAEG caps.** BdP publishes quarterly
  max TAEGs per category; crédito pessoal splits into **"Educação, Saúde, Energias renováveis e
  Locação financeira de equipamentos" (LOWER cap)** vs **"Outros créditos pessoais (sem
  finalidade específica)" (HIGHER cap)**. The ×1.25 / ×1.5 mechanism; pricing clusters under
  the applicable cap. `[REFRESH]` current-quarter caps per category.
- **Imposto do selo** for crédito pessoal — **Verba 17.2.1 by maturity band** (one-off duty on
  the credit granted) — *contrasted* with the revolving monthly utilization duty in the card
  brief — plus **Verba 17.3 (~4%)** on interest/commissions and the **50% consumer surcharge**.
  `[REFRESH]`.
- **CRC**, **PARI / PERSI** (DL 227/2012), responsible-lending — reference card brief 02.

**3. Market characteristics — "why PT is different"** — crédito pessoal's weight in PT consumer
credit; **ASFAC** specialists vs bank lenders; typical amount/term bands; online/instant
origination; consolidation demand. `[REFRESH]` stats.

**4. PT-adapted lifecycle** — solvency + **CRC**, **FIN**, withdrawal, disbursement, **prestações
via débito direto**, **reembolso antecipado with capped compensation**, **PARI → PERSI**.

**Figures to verify:** caps per category (current quarter); imposto-do-selo bands + surcharge;
early-repayment cap; CCD2 status; market-size stats.

---

### Brief 03 — `03-competitive-landscape-pt.md` — Who lends in Portugal

Survey + comparison matrix across the agreed wide lender set. **All pricing `[REFRESH]`.** Each
lender confirmed to offer crédito pessoal (vs only cards/auto).

**1. Market structure & sizing** — `[REFRESH]` from BdP / ASFAC.

**2. Player taxonomy**
- **Universal banks:** CGD, Millennium BCP, Santander Totta, Novo Banco, BPI, Bankinter,
  Crédito Agrícola, ABANCA (incl. former EuroBic), Montepio.
- **Digital arms:** ActivoBank, Moey, Openbank.
- **Consumer-finance specialists:** Cofidis, Cetelem (BNP Paribas PF), **Younited**, Oney
  (confirm), **Banco Credibom**, Bankinter Consumer Finance. (Auto-only players e.g. 321 Crédito
  noted as out-of-scope.)
- **Digital / fintech:** **Banco CTT**, Revolut (`[REFRESH]` whether a true PT personal loan).

**3. Comparison matrix** (one row per flagship product; numeric cells `[REFRESH]`) — amount
range · term range · TAN · **TAEG by finalidade** · comissão de abertura · early-repayment
terms · fixed/variable · channel · insurance cross-sell · positioning.

**4. Cross-cutting themes** — pricing converges under the **purpose-segmented cap**; *finalidade*
as a pricing lever; **consolidação** as a growth product; **online/instant approval** as the new
battleground; specialists' insurance attach; the stamp-duty wedge; ASFAC specialists vs bank
balance-sheet lending; BNPL adjacency.

**Figures to verify:** every fee/rate/term/amount and market-share number.

---

## Methodology & conventions

- **One concept, one brief; briefs build in order** (01 → 02 → 03). No re-defining terms — 02
  and 03 reference 01's taxonomy.
- **`[REFRESH]` marker** on every figure whose currency can't be guaranteed from memory.
- **"Figures to verify" appendix** at the foot of each brief consolidates the `[REFRESH]` items.
- **Authoritative-source shortlist** (for the eventual refresh): **Banco de Portugal** (the
  quarterly *taxas máximas no crédito aos consumidores* tables — which literally enumerate the
  crédito-pessoal sub-categories; *Todos Contam*; retail-banking conduct reports; the *Custo do
  crédito aos consumidores* statistics), **ASFAC** (the consumer-finance specialists'
  association), lender *preçários* + *FIN* + online *simuladores*, DECO / Deco Proteste. Exact
  source pointers go in a `sources.md` during the refresh pass — not invented now.
- **Plain-English first** in each brief, then the formal detail (house style).
- **Reference, don't duplicate, the card research** wherever the PT regime is shared.

## Sequencing & dependencies

1. **Brief 01** (no dependencies) → vocabulary + dimension list.
2. **Brief 02** (depends on 01) → PT regulation/tax/habits, centred on the per-purpose caps.
3. **Brief 03** (depends on 01 + 02) → populates the matrix with live-ish lender data.
4. *(Optional, later)* **Refresh pass** → resolve all `[REFRESH]` tags; add `sources.md`.
5. *(Optional, much later)* **babelstone bridge note** → only if we ever model a `personal_loan`
   family; explicitly out of scope now.

## Open questions / risks

- **The per-purpose caps move quarterly** → any TAEG figure is stale on arrival; treat the
  *mechanism* (purpose-segmented ceiling) as the durable insight, the *number* as perishable.
- **Lender line-ups churn** — confirm each named lender currently offers crédito pessoal (vs
  only cards/auto/BNPL) before asserting it; Oney/Revolut especially.
- **Early-repayment compensation figures** — verify the exact percentages and the >1yr / ≤1yr
  threshold against the current DL 133/2009 text.
- **Market-share / volume data is noisy** — banks vs ASFAC specialists split is the most
  decision-relevant sizing figure and the easiest to get wrong from memory → hard `[REFRESH]`.

## Out of scope (for now)

- Any babelstone engine / family / ADR / contract design.
- Non-Portuguese competitive analysis.
- *Crédito automóvel*, mortgage, and credit-card deep-dives (referenced only).
- Live figure verification (deferred refresh pass).
- Legal/compliance opinion (we describe the regime; we don't advise on it).
