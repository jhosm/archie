# Brief 01 — What is Crédito Pessoal? (concept-level)

> Part 1 of 3. Establishes the universal vocabulary, taxonomy, and lifecycle of the
> **amortizing unsecured personal loan**, reused by
> [02 — Portugal context](./02-portugal-context.md) and
> [03 — competitive landscape](./03-competitive-landscape-pt.md).
> The term *crédito pessoal* is Portuguese, but the *instrument* is universal — Portugal's
> law, taxes, and rate caps are deferred to Brief 02.
> Conceptual brief — few perishable figures; any global statistic is tagged `[REFRESH]`.

## In plain English

A *crédito pessoal* is the plainest kind of loan: the lender gives you **one fixed sum, once**,
and you pay it back in **equal monthly installments** (*prestações*) over a **fixed number of
years**. There is no card, no spending limit you dip in and out of, and nothing pledged as
security — it rests on your ability to repay. When the last installment clears, the loan is gone.

That makes it the mirror image of a credit card. A card is a **revolving** line you borrow and
re-borrow against indefinitely (covered in [`../credit-cards/`](../credit-cards/01-fundamentals.md));
a crédito pessoal is **closed-end** — borrow once, repay to zero, done. Each installment you pay
is part interest, part repayment of the borrowed sum, and the mix shifts over time: early on
you're mostly paying interest, near the end you're mostly paying down the loan. That repayment
table — the *quadro de amortização* — is the heart of the product, and everything below (the
mechanics, the variants, the lifecycle) hangs off it.

---

## 1. Definition & boundaries — the "consumer-credit spectrum"

A crédito pessoal is a loan that gives the borrower a **fixed principal, disbursed as a lump sum
up front**, repaid over a **fixed term** in **periodic (usually monthly) installments** that
**fully amortize** the debt (principal + interest) to zero by maturity, **without security**
(unsecured — no asset pledged) and **without revolving** (repaid amounts cannot be re-borrowed).

It is easiest to define by contrast with its neighbours:

| Instrument | Disbursement | Repayment | Revolving? | Secured? | Purpose-tied? |
|---|---|---|---|---|---|
| **Crédito pessoal** | **Lump sum, once** | **Level installments, fixed term** | **No (closed-end)** | **No** | Optional (general or stated) |
| **Credit card / revolving** | As you spend, repeatedly | Flexible (min → full), open-ended | **Yes** | No | No |
| **Overdraft / conta corrente** | Draw down as needed | Flexible, open-ended | Yes | No | No |
| **Crédito automóvel** | Lump sum to buy a vehicle | Level installments | No | Often (vehicle / *reserva de propriedade*) | **Yes (a vehicle)** |
| **Mortgage / crédito habitação** | Lump sum to buy property | Level installments, long term | No | **Yes (the property)** | **Yes (housing)** |
| **Leasing / locação financeira** | Lender buys the asset, rents to you | Rentals + purchase option | No | The asset itself | **Yes (an asset)** |
| **BNPL / installment-at-POS** | Per-purchase | A few fixed installments | Per-purchase | No | The purchase |

The defining attributes of *crédito pessoal* specifically: **(a) closed-end** (no re-draw — once
repaid the line is gone), **(b) lump-sum disbursement** (the full amount up front, not drawn as
needed), and **(c) full amortization over a set term** (the schedule retires the debt to zero).
*Crédito automóvel* and mortgages share (a)–(c) but are **purpose-tied and usually secured**;
the card and overdraft are **revolving**; leasing transfers *use* of an asset rather than cash.

> **Boundary note — the discriminator is *security*, not *purpose*.** The line between crédito
> pessoal and *crédito automóvel* is **collateral** (the *reserva de propriedade* / pledge over
> the vehicle), not the existence of a vehicle purpose. An **unsecured** loan whose declared
> *finalidade* happens to be a car — a *crédito pessoal com finalidade automóvel* — is still
> **crédito pessoal**, in scope here; a loan **secured on the vehicle** is *crédito automóvel*,
> out of scope. This is exactly where the two most often blur in Portugal.

---

## 2. Core mechanics

- **Capital (principal) & prazo (term).** The borrowed amount and the number of periods over
  which it's repaid — the two headline parameters the borrower chooses (within lender bands).
- **TAN (nominal annual rate).** The interest rate applied to the outstanding balance. It is
  *nominal* — it does not, on its own, include fees.
- **Amortization method.** Almost universally the **French method / constant installment**
  (*prestação constante*): every installment is the **same amount**, but its internal split
  shifts — **interest-heavy at the start** (interest is charged on a large outstanding balance),
  **principal-heavy at the end**. The alternative, **constant-capital** (each installment repays
  the same slice of principal, so the total installment *falls* over time), is rarer in consumer
  lending.
- **The amortization schedule (*quadro de amortização*).** The period-by-period table showing,
  for each installment: interest portion, principal portion, and remaining balance. It is the
  loan's ground truth — early repayment, total cost, and the effect of a rate change all read off
  it.
- **Fixed vs variable rate.** A **fixed** rate locks the installment for the whole term (common
  for short/medium personal loans — predictability sells). A **variable** rate is an **index +
  spread** (in the euro area, **Euribor + spread**); the installment is recomputed when the index
  resets. Personal loans skew fixed; longer/larger ones may be variable.
- **TAEG (all-in effective annual rate) & MTIC (total cost to the consumer).** The **TAEG** rolls
  interest **plus** mandatory fees and charges into one comparable annual figure — the number to
  compare offers on. The **MTIC** is the total euros the borrower ends up paying (principal +
  interest + fees + taxes). Headline TAN always understates the real cost; TAEG is the honest one.
- **The level-installment formula.** For principal `P`, periodic rate `i` (= TAN/12 for monthly),
  and `n` periods, the constant installment is
  `A = P · i / (1 − (1 + i)^(−n))`.
  Total interest over the life = `A·n − P` — which **grows with the term**: a longer loan means a
  smaller installment but **more interest paid overall**, the central trade-off a borrower faces.

---

## 3. Variant taxonomy

The axes below become the **comparison dimensions** in Brief 03.

- **By purpose (*finalidade*) — the axis that matters most in Portugal:**
  - *Sem finalidade específica* — **general-purpose**: no declared use, maximum flexibility.
  - *Com finalidade específica* — **purpose-stated**: e.g. **educação**, **saúde**, **energias
    renováveis**, **locação financeira de equipamentos**, plus home improvement (*obras/lar*),
    travel, life events, etc. Declaring a purpose can unlock better pricing (in Portugal it
    changes the *legal* price ceiling — see Brief 02 §2). *Note:* "locação financeira de
    equipamentos" here is a **BdP cap-category label for a purpose-stated cash loan** earmarked
    for equipment — **not** true *locação financeira* (leasing), which §1 excludes as a separate
    instrument. The shared wording is BdP's category name, not a sign that leasing is in scope.
- **Debt consolidation (*crédito consolidado*) — an adjacent form.** Not a fresh-money loan in
  spirit but a **refinancing**: several existing debts (cards, other loans) are rolled into **one
  new amortizing loan** with a single, usually lower, installment — achieved by stretching the
  term (which can raise total interest even as the monthly payment falls). Described here as a
  neighbour of crédito pessoal, not its core case.
- **By rate type:** **fixed** vs **variable** (Euribor + spread).
- **By guarantee:** **unsecured** (the default) vs **with a guarantor** (*fiador* / *avalista* —
  a third party liable if the borrower defaults), which can improve approval odds or pricing
  without making the loan "secured" in the collateral sense.
- **By channel:** **branch** (relationship-led), **online / app** (self-service, instant or
  near-instant decisioning), and **point-of-sale** (*crédito no ponto de venda* — arranged where
  the purchase happens, the specialists' classic turf).

---

## 4. Lender economics — how an amortizing personal loan makes money

| Revenue | Cost |
|---|---|
| **Net interest margin** — interest earned over the term minus cost of funds (the core engine) | **Cost of funds** (deposits / wholesale funding) |
| **Origination fees** — *comissão de abertura / dossier* (one-off, at disbursement) | **Credit losses** — defaults / write-offs (the dominant risk) |
| **Insurance cross-sell** — *seguro de proteção ao crédito* (payment-protection / life), often commission-rich | Servicing, collections, acquisition |
| Account/servicing fees (where permitted) | Capital held against the loan; operations |

The contrast with **card economics** (Brief [`../credit-cards/`](../credit-cards/01-fundamentals.md) §4)
is sharp: a card earns **interchange** on every swipe plus interest from *revolvers*; a personal
loan earns **no interchange at all** — its profit is **pure lending margin over the term**, topped
up by the origination fee and (often materially) by **insurance attach**. Because the cash goes
out on day one and comes back slowly, the lender carries **funding and default risk for the whole
term**, which is why **affordability assessment and pricing-for-risk** dominate the product.

---

## 5. Lifecycle — the amortizing-loan clocks

**A. Product lifecycle.** Segment/purpose definition → product design (term bands, rate type,
fees, insurance bundle) → underwriting policy & risk appetite → pricing (TAN/TAEG by risk and
purpose) → launch → portfolio management (vintage/loss monitoring, repricing of *new* business) →
refresh → retirement.

**B. Loan / customer lifecycle** *(the spine of the product)*. Marketing / simulation
(*simulador*) → **application** → identity verification (KYC/AML) → **solvency & affordability
assessment** (income, existing debts, credit-history/scoring) → **decision** (approve / decline /
counter-offer) + **pricing & limit** → contract signing + **pre-contractual disclosure** + a
**withdrawal window** (cooling-off) → **lump-sum disbursement** (cash to the borrower's account)
→ **amortization** (level installments collected, typically by **direct debit**) → servicing
(statements, queries, rate resets if variable) → **early repayment** (partial or full) → **either
maturity & closure** *or* **default → restructuring → collections → recovery / write-off**.

For a **purpose-stated (*com finalidade*)** loan, the lender may require **proof of the declared
purpose** (*comprovativo da finalidade*) at or before disbursement — the touchpoint that
mechanically connects the §3 purpose taxonomy to the **per-*finalidade* legal price ceiling** in
Brief 02 §2: the cheaper cap is earned by *evidencing* the purpose, not merely naming it.

**C. Installment/payment cycle** (the recurring inner loop, replacing the card's transaction
lifecycle). Each period: installment falls due → **direct-debit collection** → on success, the
schedule advances (interest + principal posted per the *quadro*); on failure → arrears handling
(reminders → late interest → the default path in §5B).

The structural difference from a card: there is **no per-transaction authorization/clearing/
chargeback machinery**. The money moves **twice** — out once (disbursement), back many times
(installments) — and the lender's whole risk lives in whether those installments arrive.

---

## 6. Early-repayment mechanics (generic)

A borrower can repay **part** (*reembolso antecipado parcial* — shortens the term or lowers the
installment) or **all** (*reembolso antecipado total* — closes the loan) ahead of schedule.
Motives: cash windfall, refinancing to a cheaper rate, or rolling into a consolidation loan.
Because the lender loses expected future interest, regulators allow a **capped compensation** to
the lender — small, and tighter the closer the loan is to maturity. The **exact Portuguese caps**
(and the threshold between "more than a year left" and "a year or less") are set out in
Brief 02 §2.

---

## 7. Risk & control backdrop (generic)

- **Risks:** **credit / default risk dominates** (the cash is out and unsecured); plus fraud
  (application/identity), operational, and conduct risk.
- **Controls:** affordability-based underwriting, credit-history/scoring, **responsible-lending**
  duties, expected-loss provisioning (e.g. IFRS 9), and over-indebtedness safeguards.
- **Disclosure norms:** standardised pre-contractual information so offers are comparable — the
  EU **SECCI**, realised in Portugal as the **FIN** (*Ficha de Informação Normalizada*), picked
  up concretely in Brief 02.

---

## Figures to verify `[REFRESH]`

- Any global market statistic cited (none asserted here by design).
- Typical term bands, fee levels, and fixed-vs-variable prevalence vary by lender/jurisdiction —
  treat the *mechanism* as durable, specific numbers as illustrative until Brief 02/03 + refresh.

---

*Next:* [02 — Crédito pessoal in the Portuguese context →](./02-portugal-context.md)
