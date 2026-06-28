# Conta à Ordem — Market Research Plan

> **Status:** Produced to support a real decision — the **conta à ordem family ADR**
> (bd `babelstone-xvcx`; [ADR-PC-030 §Open Action 4](../../adrs/ADR-PC-030-product-scope-and-boundary.md)).
> Unlike its two siblings ([credit cards](../credit-cards/00-research-plan.md),
> [crédito pessoal](../personal-loan/00-research-plan.md)), which began as scratch market
> research and were *promoted* once they started feeding [ADR-PC-030](../../adrs/ADR-PC-030-product-scope-and-boundary.md),
> the conta à ordem is **already inside scope**: ADR-PC-030 fixes it as the **4th product
> shape** (the *transactional balance account*) and the
> [ADR-PC-016](../../adrs/ADR-PC-016-legacy-current-account-adapter.md) **v4 destination**.
> So the babelstone bridge is the explicit motivation here, not a deferred maybe. The briefs
> below remain **descriptive market research** — they describe the *product and the market*;
> they perform no engine/ADR/contract design. That design lives in the family ADR these briefs
> feed.

## In plain English

We're going to figure out, from the ground up, **what a _conta à ordem_ (a Portuguese current
/ demand account) actually is, how it works in Portugal specifically, and what the banks
operating in Portugal are selling today.** A conta à ordem is the everyday account most people
think of as "my bank account": money is available **on demand** (no maturity, no lock-up), you
pay in and take out as often as you like, a debit card and direct debits hang off it, and — the
part that makes it special — the bank tracks **two** balances, not one: the **accounting
balance** (what has actually posted) and the **available balance** (what you can spend right
now, after subtracting money that's been earmarked but not yet settled).

We do it in three steps that build on each other: first the universal "what is a demand /
transactional account" picture (true anywhere), then the same picture re-drawn with Portugal's
rules, taxes, and habits, and finally a survey of who offers current accounts in the Portuguese
market and how the offers compare.

Two threads make the conta à ordem different from its three sibling products. **First, it is the
hub:** salaries land in it, bills leave it, and the term deposit, the loan, and the credit card
all settle *against* it — it is the account the others plug into. **Second, the most
Portugal-specific twist is consumer protection on the account itself:** the law guarantees a
**basic bank account** at a capped cost (*serviços mínimos bancários*), forces banks to publish
comparable fee documents and to run a **free account-switching service**, and treats an
overdraft (*descoberto*) as a regulated form of consumer credit.

This first pass is written from existing knowledge so we can lock down the *structure* and the
*right questions* quickly. Every concrete number that can drift — maintenance fees, overdraft
TAN/TAEG and the cap that bounds it, the *imposto do selo* rates, the deposit-guarantee ceiling,
dormancy/escheat periods, market-share figures — gets a visible **`[REFRESH]`** tag and is
collected in a "Figures to verify" appendix at the end of each brief, so we know exactly what to
confirm against live sources before anyone relies on it.

## Scope & ground rules (locked with the user)

| Decision | Choice |
|---|---|
| **What "conta à ordem" means here** | A **demand / current deposit account**: funds available on demand (no maturity), an authoritative balance with the **accounting-vs-available split** (holds / *cativos*), the payment instruments that ride it (debit card, *débito direto*, *transferências*), an optional **authorized overdraft** (*descoberto autorizado*), and its role as the **settlement hub**. **Not** a term deposit (*depósito a prazo* — the [term_deposit](../../02-v1-scope-term-deposits.md) family), **not** a savings account, **not** a credit-card revolving account, **not** the payment rails/scheme/clearing. |
| **Output format** | Separate markdown briefs in `research/current-account/` (this folder) |
| **Provider coverage (Q3)** | Universal banks **+** their digital arms **+** neobanks/fintech — each confirmed to actually offer a Portuguese current account / IBAN |
| **Method / data currency** | Domain knowledge first; every volatile figure tagged `[REFRESH]` |
| **Relationship to the sibling research** | **Reference, don't duplicate** — the shared PT regime (BdP supervision, DL 133/2009 caps as they touch *descoberto*, *imposto do selo*, FIN, PARI/PERSI, CRC, FGD) is cross-linked to [`../credit-cards/`](../credit-cards/02-portugal-context.md) and [`../personal-loan/`](../personal-loan/02-portugal-context.md) rather than re-derived |
| **babelstone coupling** | **Known and named, but not designed here.** The account is the [ADR-PC-030](../../adrs/ADR-PC-030-product-scope-and-boundary.md) 4th shape and the [ADR-PC-016](../../adrs/ADR-PC-016-legacy-current-account-adapter.md) v4 destination; the engine/event/pack design is the family ADR's job (bd `babelstone-xvcx`), **not** these descriptive briefs. |

## The three briefs (sequenced — each constrains the next)

The work funnels from the **abstract product** → the **jurisdiction-specific instance** → the
**live competitive instances**. Brief 01 gives us the vocabulary and the dimension list; Brief 02
specialises those dimensions to Portugal; Brief 03 populates them with real banks.

---

### Brief 01 — `01-fundamentals.md` — What is a current / demand account? (jurisdiction-agnostic)

The universal **demand-deposit / transactional-account** mechanics, taxonomy, and lifecycle —
the conceptual backbone reused by 02/03. (The term is Portuguese; the *instrument* is universal.
PT law/tax/habits are deferred to 02.)

**1. Definition & boundaries — the "deposit-account spectrum"**
- Current account defined: a **demand deposit** — funds available on demand, no maturity, an
  authoritative running balance, unlimited credits/debits, payment instruments attached.
- Defined by contrast with its neighbours: **savings account** (interest-bearing, sometimes
  notice/withdrawal-limited), **term deposit / *depósito a prazo*** (locked to maturity → the
  [term_deposit](../../02-v1-scope-term-deposits.md) family; the conta à ordem is its liquid
  opposite), **money-market account**, the **credit-card account** (a revolving *line*, not a
  deposit → reference [`../credit-cards/`](../credit-cards/01-fundamentals.md)), **e-money /
  payment accounts** (PSD2/EMI — holds balances, takes no deposits).

**2. The two balances — the conceptual spine**
- **Accounting / ledger balance** (what has posted) vs **available balance** (what is spendable
  now). Their gap = **holds / earmarks** (approved-but-unsettled authorizations) + uncleared
  items, ± **overdraft headroom**.
- `available balance` is a **derived quantity**, not a second stored number:
  `available = accounting − Σ(active holds) (+ authorized-overdraft limit)`.
- Why two balances exist: the **authorization → capture → settlement → posting** gap; the
  canonical case is the hotel / car-rental / fuel **pre-authorization**.

**3. Core mechanics**
- **Credits & debits** as a running ledger; **value date vs booking/posting date** (when
  interest counts vs when it shows).
- **Holds / authorizations / earmarks:** placed at authorization, **captured** on settlement, or
  **expire** on timeout; partial captures and reversals.
- **Overdraft:** **authorized / arranged** (a pre-agreed credit line on the account) vs
  **unauthorized / unarranged** (going below zero without agreement); overdraft limit, interest,
  and fees. (Overdraft is *credit*, which is why the consumer-credit regime reaches it in 02.)
- **Statement cycle:** the periodic statement as an immutable record of the cycle.
- **Payment instruments riding the account:** debit card, **direct debits** (pull), **standing
  orders / standing instructions** (scheduled push), **credit transfers** (SEPA), historically
  cheques.
- **Interest & fees:** demand balances typically earn little/no interest; the economics come from
  **account fees** + the float + cross-sell, not a lending margin.

**4. The account as the hub (why this shape is foundational)**
- The conta à ordem is the **settlement account** the other products plug into: salary/credits
  in; bills/direct debits out; **loan disbursement in, installments out**; **deposit interest and
  maturity in, constitution debit out**; **card statement settles** from it. This is precisely the
  "*the hub the others settle against*" role [ADR-PC-030](../../adrs/ADR-PC-030-product-scope-and-boundary.md)
  gives the 4th shape — described here at the product level only.

**5. The authorization pipeline (generic)**
- A debit attempt runs a pipeline: **instrument valid? → customer authenticated → funds
  available? → within product rules/limits/overdraft? → earmark (place hold) → fraud screen →
  effect on the rails.** Described generically; *where a ledger engine's responsibility starts
  and stops within that pipeline is a babelstone boundary question, decided in
  [ADR-PC-030](../../adrs/ADR-PC-030-product-scope-and-boundary.md) — not asserted here.*

**6. Variant taxonomy** (becomes the comparison axes in 03)
- **By holder:** personal (sole / joint — *contas solidárias / conjuntas*) vs business.
- **By packaging:** standalone account vs **bundled "package" account** (a monthly fee bundling
  card + insurance + transfers) vs **basic / regulated** account.
- **By channel/provider:** incumbent-bank account vs **digital-arm** account vs **neobank /
  e-money** account.
- **By currency:** single-currency vs **multi-currency** (the neobank angle).

**7. Lifecycle**
- **Product clock:** design → pricing (fee schedule, overdraft terms) → launch → portfolio
  management → repricing → retirement.
- **Account / customer clock:** application → **KYC/AML** → open + IBAN issued → active (the long
  steady state) → servicing (limit/overdraft changes, joint-holder changes, instrument
  reissue) → **dormancy / inactivity** → **escheat / unclaimed-balance** → closure (voluntary,
  or on death → succession, handled upstream). Default on an overdraft forks into
  arrears → collections.

**8. Risk & control backdrop (generic)**
- **Risks:** overdraft credit risk; **fraud** (unauthorized transactions, account takeover, APP
  scams); operational; AML / transaction monitoring; **dormancy / unclaimed property**.
- **Controls:** strong customer authentication, transaction monitoring, **deposit insurance**,
  garnishment / court-ordered holds (a second meaning of "hold" distinct from an authorization
  earmark), standardised fee disclosure.

**Figures to verify:** mostly none — conceptual; any global statistic flagged `[REFRESH]`.

---

### Brief 02 — `02-portugal-context.md` — Conta à ordem in Portugal

Re-draw Brief 01 with Portuguese terminology, regulation, taxes, and market habits.

**1. Portuguese vocabulary** — *conta à ordem* / *conta de depósito à ordem (DO)*; *depósito à
ordem* vs *depósito a prazo*; ***saldo contabilístico*** (accounting balance) vs **_saldo
disponível_** (available balance) vs **_saldo cativo_ / _cativos_ / _cativação_** (holds);
**_descoberto_** — *descoberto autorizado* (arranged) vs *ultrapassagem de crédito* /
*descoberto não autorizado* (unarranged); *facilidade de descoberto*; *comissão de manutenção de
conta*; *autorização de débito direto (ADC/SEPA)* / *domiciliação*; *transferência* (SEPA /
*MB WAY*); *domiciliação de ordenado*; *IBAN (PT50…)*; *NIB*.

**2. Regulatory framework** (reference the sibling card/loan brief 02 for the shared spine; focus
on conta-à-ordem specifics)
- **Banco de Portugal** retail-conduct supervision; deposit-taking under the **RGICSF**.
- **★ Serviços Mínimos Bancários (SMB) — the defining PT/EU consumer feature.** A regulated
  **basic bank account** (current account + debit card + transfers + direct debits) at a **capped
  annual cost**, available as of right. PT regime: **DL 27-C/2000** as amended (notably DL
  107/2017 / DL 7/2020); the cost cap is pegged to a fraction of the **IAS** (*Indexante dos
  Apoios Sociais*). `[REFRESH]` the current cap and the IAS peg. This is a market-shaping floor
  with no analogue in the card/loan briefs.
- **★ Payment Accounts Directive (PAD, 2014/92/EU), transposed by DL 107/2017** — the other
  defining layer, all account-specific:
  - **Fee comparability:** the **FID** (*Documento de Informação sobre Comissões*) pre-contract
    and the annual **statement of fees** (*Extrato de Comissões*), using EU-standardised
    terminology; BdP's fee-comparison portal.
  - **Account-switching service** (*serviço de mudança de conta*) — a bank-to-bank assisted
    switch within a legally fixed window. `[REFRESH]` the window.
  - **Right to a basic payment account** (dovetails with SMB).
- **Comissões regime.** BdP regulates and publishes the *preçário*; the **comissão de manutenção
  de conta** (account-maintenance fee) and *contas-pacote* are a contentious consumer topic;
  certain fees are prohibited/capped. `[REFRESH]` current rules.
- **Descoberto as consumer credit — DL 133/2009.** A *facilidade de descoberto* (arranged
  overdraft) and *ultrapassagem* (unarranged) are regulated credit: solvency assessment, **FIN**,
  and — critically — the **maximum-TAEG cap**. The BdP quarterly cap bucket
  *"cartões de crédito, linhas de crédito, contas correntes bancárias e **facilidades de
  descoberto**"* is **shared with the credit-card bucket** → reference
  [`../credit-cards/02-portugal-context.md` §2](../credit-cards/02-portugal-context.md); the
  ×1.25 / ×1.5 mechanism is the same. `[REFRESH]` current-quarter cap.
- **Imposto do selo** on the *descoberto* — **Verba 17.x utilization duty** on the credit used
  (the monthly-balance basis, *contrasted* with the one-off *crédito pessoal* duty), plus
  **Verba 17.3 (~4%)** on interest/commissions and the consumer surcharge — reference the card
  brief. `[REFRESH]`.
- **Fundo de Garantia de Depósitos (FGD).** Demand deposits are covered up to **€100,000 per
  depositor per institution**. `[REFRESH]` ceiling + scope.
- **Dormancy / contas inativas / *saldos prescritos*.** Long-inactive balances eventually revert
  to the **State** under the prescription/escheat regime. `[REFRESH]` the inactivity period and
  mechanism (a notably PT-specific lifecycle endpoint).
- **PSD2 (DL 91/2018):** SCA on payments; **open banking** (AISP/PISP) access to the account.
- **Penhora de saldos** (garnishment): a court/AT-ordered freeze on a balance — the *legal*
  meaning of *cativo*, distinct from an authorization earmark. Worth disambiguating.
- **CRC**, **PARI / PERSI** (DL 227/2012) — apply to *descoberto* default; reference the sibling
  briefs.

**3. Domestic infrastructure**
- **SIBS / Multibanco / MB WAY** — the conta à ordem is the account *behind* the debit card and
  MB WAY; the domestic rail is table stakes (reference the card brief).
- **SEPA / IBAN (PT50)** — credit transfers, **débitos diretos SEPA**, *domiciliações*; **MB WAY**
  for P2P + instant.

**4. Market characteristics — "why Portugal is different"**
- Near-universal account ownership; the conta à ordem is the **relationship anchor** for
  cross-sell. `[REFRESH]` penetration.
- **Maintenance fees** rose and are politically charged → **SMB** as the regulated floor and the
  switching service as the pressure valve; *contas-pacote* bundling. `[REFRESH]` average fees +
  SMB uptake.
- **Domiciliação de ordenado** (salary domiciliation) as the relationship lever and the usual
  **fee-waiver** condition.
- Strong **débito direto / domiciliação** culture for utilities; high MB WAY adoption.
- **Neobanks (Revolut, N26) widely held as secondary accounts** — FX/UX driven. `[REFRESH]`.

**5. PT-adapted lifecycle (delta vs Brief 01 §7)** — opening under PT **KYC/AML** (NIF,
comprovativo de morada) → IBAN issued → SMB option offered → active, with *débitos diretos* /
*domiciliações* → *descoberto* (if any) governed by DL 133/2009 + **PARI/PERSI** on default →
**dormancy → prescription/escheat to the State**; on death, succession handled **upstream**
(reference [ADR-PC-030 §Decision "Succession is upstream-decided"](../../adrs/ADR-PC-030-product-scope-and-boundary.md)).

**Figures to verify:** SMB cost cap + IAS peg; PAD switching window; maintenance-fee rules &
averages; *descoberto* max-TAEG cap (current quarter); *imposto do selo* on overdraft; FGD
ceiling; dormancy/escheat period; market penetration & neobank-secondary-account share.

---

### Brief 03 — `03-competitive-landscape-pt.md` — Who offers current accounts in Portugal

Survey + comparison matrix across the agreed provider set. **All pricing `[REFRESH]`.** Each
provider confirmed to offer a Portuguese current account / IBAN.

**1. Market structure & sizing** — account penetration, *contas-pacote* vs standalone, **SMB
uptake**, average maintenance fee, switching-service volumes. `[REFRESH]` from BdP.

**2. Player taxonomy**
- **Universal banks:** CGD, Millennium BCP, Santander Totta, Novo Banco, BPI, Bankinter, Crédito
  Agrícola, ABANCA (incl. former EuroBic), Montepio.
- **Digital arms:** ActivoBank (BCP), Moey (Crédito Agrícola), Openbank (Santander).
- **Low-fee / postal:** **Banco CTT**.
- **Neobanks / e-money:** **Revolut**, **N26**, **Wise** (multi-currency; confirm whether a true
  PT IBAN current account vs e-money). `[REFRESH]` IBAN country + deposit-guarantee scheme per
  provider.
- **Basic account:** **Serviços Mínimos Bancários** offered across the incumbent set (the
  regulated floor, not a provider).

**3. Comparison matrix** (one row per flagship account/package; numeric cells `[REFRESH]`) —
**maintenance fee** (*comissão de manutenção*) · **fee-waiver conditions** (salary domiciliation /
minimum balance / age) · debit card included · **MB WAY / Apple·Google Pay** · **overdraft
(*descoberto autorizado*)** availability + TAN/TAEG · interest on balance (usually 0) ·
multi-currency · **SMB offered** · **switching-service** · digital/app · positioning.

**4. Cross-cutting themes** — maintenance-fee competition & **waiver-via-salary**; *contas-pacote*
bundling; **SMB as the regulated floor**; **neobank disruption** (free accounts, FX, UX) and the
**deposit-guarantee / IBAN-country** caveat that distinguishes a true bank account from e-money;
the account as the **cross-sell hub**; switching friction *despite* the mandated service;
*descoberto* as a fee/credit lever; **open-banking** aggregation.

**Figures to verify:** every fee/rate/waiver-threshold and market-share number; per-provider IBAN
country + deposit-guarantee scheme; current package line-ups (marketing names change).

---

## Methodology & conventions

- **One concept, one brief; briefs build in order** (01 → 02 → 03). No re-defining terms — 02 and
  03 reference 01's taxonomy.
- **`[REFRESH]` marker** on every figure whose currency can't be guaranteed from memory.
- **"Figures to verify" appendix** at the foot of each brief consolidates the `[REFRESH]` items.
- **Authoritative-source shortlist** (for the eventual refresh): **Banco de Portugal** (the
  *preçários* / *comparador de comissões*; the *serviços mínimos bancários* pages; the quarterly
  *taxas máximas no crédito aos consumidores* tables that bound the *descoberto*; *Todos Contam*;
  retail-banking conduct reports), the **FGD** (deposit-guarantee scope), provider *preçários* +
  **FID** + *FIN*, **DECO / Deco Proteste** comparisons, and **EUR-Lex / DRE** for the directive
  and decree-law text. Exact pointers go in a `sources.md` during the refresh pass — not invented
  now.
- **Plain-English first** in each brief, then the formal detail (house style).
- **Reference, don't duplicate, the sibling research** wherever the PT regime is shared.

## Sequencing & dependencies

1. **Brief 01** (no dependencies) → vocabulary + dimension list, centred on the two-balance split.
2. **Brief 02** (depends on 01) → PT regulation/tax/habits, centred on SMB + PAD + *descoberto*.
3. **Brief 03** (depends on 01 + 02) → populates the matrix with live-ish provider data.
4. *(Optional, later)* **Refresh pass** → resolve all `[REFRESH]` tags; add `sources.md`.
5. **babelstone family ADR** (bd `babelstone-xvcx`) → the *consumer* of this research: turns the
   product description into the engine/event/pack design (the *cativo* model, *saldo disponível*
   as a fold, *descoberto* as pack rules). **Out of scope for these briefs** — they describe the
   product; the ADR designs the engine.

## Open questions / risks

- **The two meanings of *cativo*.** An **authorization earmark** (product mechanic) and a
  **court-ordered *penhora*** (legal freeze) are both "*saldo cativo*". Keep them distinct — the
  family ADR will model them differently.
- **True bank account vs e-money.** Revolut/N26/Wise blur the line; the **IBAN country** and the
  **deposit-guarantee scheme** (FGD vs a foreign DGS vs e-money safeguarding) are the
  decision-relevant distinctions and the easiest to assert wrongly → hard `[REFRESH]`.
- **The *descoberto* cap moves quarterly** → any overdraft-TAEG figure is stale on arrival; treat
  the *mechanism* (shared cap bucket) as durable, the *number* as perishable.
- **SMB cost + dormancy/escheat period** are pegged to indices/statute that change → `[REFRESH]`
  against the current text, never quote from memory.
- **Maintenance-fee data is noisy** and politically charged; averages vary by source → hard
  `[REFRESH]`.

## Out of scope (for now)

- Any babelstone engine / family / ADR / contract design (the *cativo* event model, the
  available-balance fold, *descoberto* as pack rules) — that is the family ADR (bd
  `babelstone-xvcx`), the consumer of this research.
- Non-Portuguese competitive analysis.
- Term deposit, *crédito pessoal*, and credit-card deep-dives (referenced only).
- The **payment rails / scheme / clearing / settlement** and SCA/fraud — external by
  [ADR-PC-030](../../adrs/ADR-PC-030-product-scope-and-boundary.md) construction.
- Live figure verification (deferred refresh pass).
- Legal/compliance opinion (we describe the regime; we don't advise on it).
