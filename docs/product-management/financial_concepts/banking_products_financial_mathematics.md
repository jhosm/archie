# Financial Mathematics of Banking Products
## Conceptual Model and Examples

---

> This document is a conceptual reference, not a regulatory or accounting source.
> Real-world implementations must respect Banco de Portugal conventions and IFRS 9 for accounting recognition.

**Reader's map:** §1–3 set up the unifying framework (cash flows, present value, the fundamental identity); §4 develops the three loan amortization systems; §5 covers *depósitos a prazo*; §6 introduces the cross-cutting metrics (IRR, TAEG); §7 handles composite cases including *carência*, variable rate, *prestações extraordinárias*, *amortização antecipada*, and payment moratoria; §8 treats irregular products (*conta à ordem*, *cartão de crédito*); §9 synthesizes both families. A glossary is provided at the end.

---

## 1. Framework

The main retail banking financial products can be grouped into two categories:

**With a predefined financial plan:**
- Term deposit
- Credit (various modalities)

**Without a predefined plan (irregular):**
- Current account (demand deposit)
- Credit card

All accrue interest. The fundamental difference is that products with a plan have cash flows known *ex ante*; irregular products have cash flows known only *ex post*.

---

## 2. The Generic Model: Sequence of Cash Flows

Any financial product is a **sequence of cash flows over time:**

```
t0      t1      t2      t3      ...     tn
 |-------|-------|-------|-------|-------|
CF0     CF1     CF2     CF3            CFn
```

**Sign convention.** Cash flows are written from the perspective of the **holder of the product** — the depositor for a *depósito a prazo*, the borrower for a credit, the cardholder for a credit card. Money received by the holder is positive; money paid out is negative. This convention is applied uniformly throughout the document.

- Term deposit: `CF(0) < 0` (handover to the bank), `CF(n) > 0` (return of capital and interest).
- Credit: `CF(0) > 0` (disbursement received), `CF(t) < 0` (installments paid).
- Current account / card: each movement carries its natural sign from the holder's side.

### 2.1 The Unifying Function: Present Value

The central mathematical model is the **present value of future cash flows:**

```
PV = Σ [ CF(t) / (1 + i)^t ]
     t=0..n
```

Where:
- `PV` = present value
- `CF(t)` = cash flow in period t
- `i` = interest rate for the period
- `t` = period (day, month, year...)

**All financial mathematics of banking products is derived from this equation.**

### 2.2 The Three Variable Dimensions

The base algorithm is always the same. What differs between products are only three dimensions:

1. **Shape of the cash flows** — fixed, variable, irregular
2. **Day-count convention** — Act/360, Act/365, 30/360...
3. **Compounding frequency** — daily, monthly, annual

**Rate notation used throughout:**

- `TAN` — nominal annual rate
- `r = TAN / m` — periodic rate for products with a plan (m = periods per year)
- `r = TAN / base` — daily rate for irregular products (base = 360 or 365 days)

The relation `r = TAN / m` is the **proportional rate** (*taxa proporcional*) — the convention used in Portuguese retail credit for installment computation. An alternative is the **equivalent rate** (*taxa equivalente*), `r = (1 + TAE)^(1/m) − 1`, which preserves the effective annual return across compounding frequencies. The two coincide only when `m = 1`; otherwise the proportional rate produces a slightly higher TAE for the same TAN. Unless stated otherwise, this document uses the proportional convention.

---

## 3. Fundamental Identity

For all products with a plan, the following identity holds in each period:

```
P(t) = J(t) + A(t)
J(t) = S(t-1) × r
S(t) = S(t-1) - A(t)
S(0) = C
```

Where:
- `C` = initial capital (principal)
- `r` = period interest rate (TAN / m, where m = periods per year)
- `n` = number of periods
- `S(t)` = outstanding balance in period t
- `J(t)` = interest of period t
- `A(t)` = capital amortization in period t
- `P(t)` = installment for period t

**Everything else — Price, SAC, American, deposit, balloon, grace period, variable rate — is a choice about what is fixed in each period.**

---

## 4. Credit Amortization Systems

The base example for all systems is:

> **Capital = €10,000 | TAN = 6% | Term = 12 months**
>
> Monthly rate: r = 6% / 12 = 0.005

### 4.1 Price System (French)

**What is fixed:** the installment `P(t)` — it is constant across all periods. This is the *sistema francês*, the default for Portuguese mortgages (*crédito à habitação*) and most personal credit.

**Derivation.** Imposing `P(t) = P` in §3's identity and summing forward, the present value of `n` equal installments at rate `r` must equal `C`:

```
C = P × Σ (1+r)^-t   for t = 1..n
  = P × [1 − (1+r)^-n] / r
```

Inverting for `P`:

```
P = C × r / (1 - (1+r)^-n)
```

**Calculation:**

```
P = 10,000 × 0.005 / (1 - 1.005^-12)
  = 50 / (1 - 0.941905)
  = 50 / 0.058095
  = €860.66
```

Interest and amortization derive from the fundamental identity (§3):

```
J(t) = S(t-1) × 0.005
A(t) = 860.66 - J(t)
```

**Amortization schedule:**

| Month | Opening balance | Interest | Capital amortized | Installment |
|-------|-----------------|----------|-------------------|-------------|
| 1 | 10,000.00 | 50.00 | 810.66 | 860.66 |
| 2 | 9,189.34 | 45.95 | 814.71 | 860.66 |
| 3 | 8,374.63 | 41.87 | 818.79 | 860.66 |
| ... | ... | ... | ... | ... |
| 12 | 856.38 | 4.28 | 856.38 | 860.66 |

**Characteristic:** interest decreases and amortization increases over time, keeping the installment constant.

---

### 4.2 SAC System (German, *Sistema de Amortização Constante*)

**What is fixed:** the capital amortization `A(t)` — it is constant across all periods.

**Formulas:**

```
A(t) = C / n                    (always equal)
J(t) = S(t-1) × r
P(t) = A(t) + J(t)              (decreases over time)
```

**Calculation:**

```
A = 10,000 / 12 = €833.33

P(1) = 833.33 + 10,000.00 × 0.005 = €883.33
P(2) = 833.33 +  9,166.67 × 0.005 = €879.16
P(3) = 833.33 +  8,333.34 × 0.005 = €875.00
...
P(12)= 833.33 +    833.33 × 0.005 = €837.50
```

**Amortization schedule:**

| Month | Opening balance | Interest | Capital amortized | Installment |
|-------|-----------------|----------|-------------------|-------------|
| 1 | 10,000.00 | 50.00 | 833.33 | 883.33 |
| 2 | 9,166.67 | 45.83 | 833.33 | 879.16 |
| 3 | 8,333.34 | 41.67 | 833.33 | 875.00 |
| ... | ... | ... | ... | ... |
| 12 | 833.33 | 4.17 | 833.33 | 837.50 |

**Characteristic:** amortizes capital faster at the beginning. The outstanding balance falls more quickly than under Price, so the interest base is smaller every month and the total interest paid over the life of the loan is lower.

---

### 4.3 American System (bullet)

**What is fixed:** the outstanding balance `S(t)` — it remains constant until the last period.

**Formulas:**

```
S(t) = C          for t < n
A(t) = 0          for t < n
A(n) = C
P(t) = C × r      for t < n
P(n) = C × r + C
```

**Calculation:**

```
P(1..11) = 10,000 × 0.005 = €50.00
P(12)    = 50.00 + 10,000 = €10,050.00
```

**Amortization schedule:**

| Month | Opening balance | Interest | Capital amortized | Installment |
|-------|-----------------|----------|-------------------|-------------|
| 1 | 10,000.00 | 50.00 | 0.00 | 50.00 |
| 2 | 10,000.00 | 50.00 | 0.00 | 50.00 |
| ... | ... | ... | ... | ... |
| 11 | 10,000.00 | 50.00 | 0.00 | 50.00 |
| 12 | 10,000.00 | 50.00 | 10,000.00 | 10,050.00 |

**Characteristic:** minimum installment during the life of the credit, but a high final payment ("balloon"). Rare in Portuguese retail; common in corporate financing and the structural form of a bond.

---

### 4.4 Comparison of the Three Systems

| | Price | SAC | American |
|---|---|---|---|
| What is fixed | Installment | Amortization | Balance |
| Installment | Constant | Decreasing | Low + balloon |
| Capital amortized | Increasing | Constant | All at the end |
| Total interest | Intermediate | Lower | Higher |
| Initial burden (borrower) | Medium | Higher | Lower |
| Typical use | Mortgage, personal credit | Mortgage (less common) | Bonds, corporate credit |

**Cash flows compared** (borrower's perspective; the initial disbursement of `+€10,000` at t = 0 is omitted from the vector below):

```
Price:     [ -860.66 ; -860.66 ; -860.66 ; ... ; -860.66 ]
SAC:       [ -883.33 ; -879.16 ; -875.00 ; ... ; -837.50 ]
American:  [  -50.00 ;  -50.00 ;  -50.00 ; ... ; -10,050.00 ]
```

---

## 5. Term Deposit

In a term deposit the perspective is reversed: the depositor hands money over to the bank, which returns it with interest.

**Sign convention used throughout:** cash flows are written from the holder's point of view — money paid is negative, money received is positive.

**Cash flows (from the depositor's point of view):**

```
CF(0) = -C       (handover to the bank)
CF(n) = +C + J   (bank returns capital and interest)
```

### 5.1 Simple Interest

Used in most term deposits in Portugal. Usual convention: **Act/360** (actual days, 360-day year).

```
M = C × (1 + TAN × days/360)
```

**Example:** C = €10,000, TAN = 6%, 365 days:

```
M = 10,000 × (1 + 0.06 × 365/360) = €10,608.33
```

### 5.2 Compound Interest

When interest is capitalized (automatically reinvested), with compounding `m` times per year and `n` in years:

```
M = C × (1 + TAN/m)^(m·n)
```

The special case `m = 1` (annual compounding) reduces to:

```
M = C × (1 + TAN)^n
```

Beware of misreading the second form: `TAN` in `(1 + TAN)^n` is the nominal *annual* rate, not the periodic rate. For monthly compounding always use `(1 + TAN/12)^(12·n)`.

### 5.3 Variants of Interest Payment

**Interest at maturity** (most common in Portugal):
```
CF(0)     = -10,000
CF(final) = +10,608.33
```

**Periodic interest** (monthly, quarterly...):
```
CF(0)        = -10,000
CF(1..n-1)   = +interest of the period
CF(n)        = +10,000 + interest of the last period
```

**Interest paid in advance** (paid up front, *juros antecipados*):
```
CF(0) = -10,000 + interest received upfront
CF(n) = +10,000
```

For the same nominal rate, the depositor's IRR is higher than in the "interest at maturity" case — the interest is received at t = 0 rather than at t = n. Banks offer this variant as a cash-management tool; for the depositor it is a presentation difference unless the upfront cash is actually reinvested.

### 5.4 Deposit Rates

```
TANB  →  Gross Nominal Annual Rate (Taxa Anual Nominal Bruta, before taxes)
TANL  →  Net Nominal Annual Rate  (Taxa Anual Nominal Líquida, after withholding tax)

TANL = TANB × (1 - 0.28)    (withholding in Portugal: 28%)
```

The rate-level conversion above is exact for a single-period deposit with interest paid at maturity. For multi-period compound deposits, withholding tax is applied to each interest payment as it accrues, so the realized net return must be computed flow-by-flow rather than by scaling the rate.

Effective annual rate with compounding `m` times per year:

```
TAE = (1 + TAN/m)^m - 1
```

**Worked example.** A *depósito a prazo* with TAN 6% and monthly capitalization:

```
TAE = (1 + 0.06/12)^12 - 1 = (1.005)^12 - 1 ≈ 6.17%
```

The 17 basis-point gap is the compounding effect — it grows with `m`. For deposits with interest at maturity (no intra-period capitalization) `TAE = TAN`; the formula matters once compounding kicks in.

---

## 6. Cross-Cutting Metrics

### 6.1 IRR — Internal Rate of Return

The IRR is the rate `i` that zeroes the present value of all cash flows:

```
0 = Σ [ CF(t) / (1+i)^t ]
    t=0..n
```

**Example — Price credit without charges** (using the €860.66 installment derived in §4.1, borrower's perspective):

```
CF(0)     = +10,000           (loan received)
CF(1..12) = -860.66            (installments paid)

0 = 10,000 - Σ [ 860.66 / (1+i)^t ]
             t=1..12
```

Solving: `i = 0.005` per month → **TAN = 6% (nominal annual); equivalent TAE = (1.005)^12 − 1 ≈ 6.17%** ✓

The per-period IRR coincides with the nominal periodic rate (TAN / m) when there are no additional charges.

### 6.2 TAEG (APR) — *Taxa Anual Efetiva Global*

The TAEG is the metric that allows comparing any financial product regardless of the amortization system, fees, insurance, or periodicity.

**Formal definition** (Decreto-Lei n.º 133/2009, Anexo II): the TAEG is the rate `i` that satisfies:

```
Σ_k [ Ak / (1+i)^tk ] = Σ_l [ Al / (1+i)^tl ]
```

The two sums use independent indices because credits and debits occur on different timetables:

- `Ak` = amounts received by the borrower (capital disbursed); `tk` = moment of receipt (years)
- `Al` = amounts paid by the borrower (installments, fees, insurance…); `tl` = moment of payment (years)

Equivalently, with net flows from the borrower's perspective, `0 = Σ CF(t) / (1+i)^t` — i.e. **the TAEG is the IRR of the full borrower-side cash flow vector**, including all mandatory charges.

**What enters the calculation** (illustrative; the authoritative list is in Decreto-Lei n.º 133/2009, art. 24.º):

| Enters the TAEG | Does not enter |
|---|---|
| Installments | Taxes (IMT, *Imposto do Selo*) |
| *Comissão de abertura* (origination fee) | Notary and registry costs |
| *Comissão de processamento de prestação* (per-installment fee) | Default penalties |
| Appraisal fee | Optional insurance |
| Mandatory life insurance premium | |
| Mandatory multi-risk insurance premium | |

Taxes are excluded because the TAEG measures the cost of the *credit relationship*, not the total cost of the transaction. *Imposto do Selo* on the loan principal is mandatory but legally a tax on the contract, not a charge by the lender.

**For `n ≥ 5` there is no general closed-form solution** in `i` (by Abel–Ruffini, after substituting `x = 1/(1+i)` to obtain a polynomial of degree `n`) — solve numerically by Newton-Raphson or bisection:

```
1. Build the full sequence of CFs (including fees, insurance)
2. Guess an initial value for i
3. Compute PV with that i
4. If PV ≠ 0, adjust i and repeat
5. Converges when |PV| < epsilon
```

**Example — Price credit with *comissão de abertura*:**

Adding a €200 origination fee to the base example. The borrower nominally contracts €10,000 but the fee is netted at disbursement, so net cash received is €9,800. Installments are unchanged at €860.66.

```
CF(0)     = +10,000 - 200 = +9,800   (net disbursement to borrower)
CF(1..12) = -860.66
```

Solving numerically: `i* ≈ 0.008166` per month

```
TAEG = (1 + 0.008166)^12 - 1 ≈ 10.25%
```

Compare with TAN = 6% (TAE ≈ 6.17%). **The €200 fee added ~4.1 pp to the effective cost** — a striking reminder that a small upfront fee on a short-term credit can dwarf the nominal rate. (Annualize the *unrounded* `i*`: rounding it to 0.00818 before compounding `(1 + i*)^12` inflates the result to ≈10.27% — the same round-once discipline that governs the cents boundary applies to the rate, so round only at the end.)

**Mandatory insurance — sketch.** For a mortgage with a life-insurance premium pegged to the outstanding balance, each monthly `CF(t)` becomes `−(installment + premium(t))`, where `premium(t)` falls over time as `S(t)` is amortized. Because the premium is a periodic charge tied to the balance, any additional mandatory cost is just one more term in the CF vector — no new mathematics, only a longer sum.

(For consumer credit in Portugal, the rules for what enters the TAEG are set by Decreto-Lei n.º 133/2009; for mortgages, by Decreto-Lei n.º 74-A/2017; at EU level, see the Consumer Credit Directive 2008/48/EC.)

**Relationship between the rates:**

```
TAN   →  nominal rate, no charges, no compounding
TAE   →  TAN converted to an annual basis (compounding)
TAEG  →  TAE + all mandatory charges

In normal conditions (positive rates, non-negative charges, m ≥ 1):  TAEG ≥ TAE ≥ TAN
```

---

## 7. Composite Cases

### 7.1 Grace Period (Mix of Systems)

In Portuguese practice the grace period (*carência*) comes in two forms:

- ***Carência parcial*** (partial): only interest is paid during the grace phase; principal is untouched. American-style for Phase 1.
- ***Carência total*** (total): neither interest nor principal is paid; accrued interest is *capitalized* into the principal, so the capital entering Phase 2 is **larger** than `C`.

The example below uses *carência parcial*, the more common form for personal credit. *Carência total* is typical of construction-phase mortgages (*crédito à habitação em fase de obra*).

```
Phase 1 (grace): interest only  →  American style (carência parcial)
Phase 2 (amortization): normal Price or SAC
```

With *carência parcial* the capital entering Phase 2 is the original capital untouched. Under *carência total*, replace `C` by `C × (1+r)^g` (where `g` is the number of grace periods) before computing the Phase 2 installment.

**Example (carência parcial):** €10,000, 6 months grace + 12 months Price, TAN 6%:

```
Phase 1 (t=1..6):
    P(t) = C × r = 10,000 × 0.005 = €50.00

Phase 2 (t=7..18), recompute Price on C=10,000:
    P = 10,000 × 0.005 / (1 - 1.005^-12) = €860.66
```

**Cash flows** (borrower's perspective):

```
CF(0)     = +10,000
CF(1..6)  = -50.00
CF(7..18) = -860.66
```

---

### 7.2 Variable Rate

**Example:** €10,000, Price, 12 months, with a revision at the 6th month:
- Initial TAN: 6% → r₁ = 0.005
- TAN after revision: 7% → r₂ ≈ 0.005833

**Phase 1 (t=1..6) with r₁:**

```
P₁ = 10,000 × 0.005 / (1 - 1.005^-12) = €860.66
```

Balance at the end of Phase 1, using the general formula:

```
S(6) = C × (1+r₁)^6 - P₁ × [(1+r₁)^6 - 1] / r₁
     = 10,000 × 1.030378 - 860.66 × 6.0755
     = 10,303.78 - 5,228.94
     = €5,074.84
```

**Phase 2 (t=7..12) with r₂, on S(6):**

```
P₂ = 5,074.84 × 0.0058333 / (1 - 1.0058333^-6) = €863.24
```

(In Portuguese variable-rate mortgages this recomputation happens at each rate revision — usually every 6 or 12 months, indexed to Euribor — keeping the residual term constant.)

**Cash flows** (borrower's perspective):

```
CF(0)     = +10,000
CF(1..6)  = -860.66
CF(7..12) = -863.24
```

---

### 7.3 Balloon Installments (*Prestações Extraordinárias*)

Regular installments with one or more extraordinary payments at defined moments. In Portuguese retail terminology these are *prestações extraordinárias*; "balloon" is the corporate/bond term for the same structure.

**Example:** Price 12 months + *prestação extraordinária* of €2,000 at month 6:

The first 5 months are normal Price installments. In month 6, the installment is paid plus an extra €2,000:

```
S(6)_after_balloon = S(6) - 2,000
```

The Price is recomputed on the new balance for the remaining 6 months:

```
P_new = S(6)_after_balloon × r / (1 - (1+r)^-6)
```

**Cash flows** (borrower's perspective):

```
CF(0)     = +10,000
CF(1..5)  = -860.66
CF(6)     = -(860.66 + 2,000) = -2,860.66
CF(7..12) = -P_new           (smaller in magnitude than 860.66 because the balance is smaller)
```

---

### 7.4 Balance After m Installments (General Formula)

For any Price credit, the outstanding balance after `m` installments is:

```
S(m) = C × (1+r)^m - P × [(1+r)^m - 1] / r
```

**Derivation.** Iterating the fundamental identity `S(t) = S(t-1)(1+r) - P` gives `S(m) = C(1+r)^m - P × Σ (1+r)^k` for `k = 0..m-1`. The geometric sum collapses to `[(1+r)^m - 1] / r`.

This formula is used to:
- Compute the balance at a rate revision point (§7.2)
- Compute the balance just before a *prestação extraordinária* (§7.3)
- Compute the outstanding capital for *amortização antecipada* (§7.5)
- Compute the outstanding capital at any moment

---

### 7.5 Early Repayment (*Amortização Antecipada*)

Portuguese law gives borrowers the right to repay early. The lender may charge a *comissão de amortização antecipada* capped by statute:

- **Mortgages, variable rate:** 0.5% of the capital repaid (Decreto-Lei n.º 74-A/2017)
- **Mortgages, fixed rate:** 2.0% of the capital repaid
- **Consumer credit (*crédito pessoal*):** the statutory cap on the early-repayment commission depends on the remaining term (Decreto-Lei n.º 133/2009, art. 19º). The **authoritative values** live as the kernel constants `PersonalLoanDecider.StatutoryCapBpsOverOneYear` (when more than one year of the term remains) and `PersonalLoanDecider.StatutoryCapBpsUnderOneYear` (when one year or less remains) — gated Live by the `CREDITO_PESSOAL_AMORTIZATION_MATH` commitment test and recorded in [ADR-PC-031 §D5](../product_concepts/adrs/ADR-PC-031-personal-loan-family.md). At v1 those are **0.5%** (>1 year remaining) and **0.25%** (≤1 year remaining); this doc cites the constants rather than restating them as the source of truth, so a future change to the statute updates one place.

**Mechanics.** At month `m` the borrower pays the regular installment plus `S(m) + fee`, where `S(m)` is the formula from §7.4 and the fee is the capped percentage of `S(m)`. The contract terminates and the CF vector is truncated:

```
CF(0)         = +C
CF(1..m-1)    = -P
CF(m)         = -(P + S(m) + fee)
CF(m+1..n)    = 0
```

**Effect on realized cost.** The TAEG of the executed contract (computed on the truncated CF vector) generally exceeds the contractual TAEG, because fixed upfront charges (*comissão de abertura*, appraisal fee) are amortized over a shorter horizon. This is especially pronounced for Price under early repayment in the first years — the borrower has paid mostly interest, with little principal retired.

**Effect on amortization-system choice.** Under early repayment, SAC and Price diverge more than the comparison in §4.4 suggests:

- Price front-loads interest → larger residual `S(m)` for given `m` → higher early-repayment cost.
- SAC pays principal faster → smaller residual `S(m)` → lower early-repayment cost.

A borrower who anticipates *amortização antecipada* should prefer SAC; one who will hold to term and values predictable budgeting should prefer Price.

---

### 7.6 Payment Moratorium (*Moratória*)

A **payment moratorium** (Portuguese *moratória*; plural moratoria) is a temporary, legally-permitted suspension of payment obligations on an active credit instance — typically triggered by a government decree in response to a disaster (the canonical recent Portuguese example is *Decreto-Lei* 10-J/2020 during COVID), or by a bank-initiated forbearance arrangement under EBA *forborne exposures* rules. The mathematical content is a special case of §7.1 (*carência*) inserted mid-contract on the balance computed via §7.4 — no new framework, only re-use.

**Three flavours**, distinguished by what is suspended:

- **Full moratorium.** Capital amortization *and* interest accrual suspended. Term typically extends by the moratorium duration.
- **Interest-only moratorium.** Capital amortization suspended; interest continues to accrue. Three sub-flavours on the interest treatment: *capitalised* into principal at moratorium-end (PT DL 10-J/2020 default), *deferred* to a lump-sum payment at moratorium-end (or spread over the post-moratorium schedule), or *paid as scheduled* throughout the moratorium window.
- **Capital-only moratorium.** Interest paid as scheduled; capital amortization suspended.

The combinations map onto §7.1 mechanics:

- *Capitalised interest* during the moratorium uses the *carência total* formula: `S(end) = S(start) × (1 + r)^g`.
- *Suspended interest* (no accrual): `S(end) = S(start)`.
- *Paid-as-scheduled interest* with capital suspended is *carência parcial* inserted at the moratorium window: balance unchanged, `J(t) = S(start) × r` paid each period.
- *Deferred interest* accrues notionally during the window (`J_def = S(start) × r × g`) and is paid as a separate flow at moratorium-end or distributed over the remaining schedule; principal is unchanged at end.

**Worked example — interest-only with capitalisation (DL 10-J/2020 shape).** A 10-year (120-month) mortgage at TAN 4%, principal €100,000:

```
r = 0.04 / 12 ≈ 0.003333
P = 100,000 × 0.003333 / (1 − 1.003333^−120) ≈ €1,012.30
```

After 24 installments, the outstanding balance from §7.4:

```
S(24) = 100,000 × 1.003333^24 − 1,012.30 × (1.003333^24 − 1) / 0.003333
      ≈ €83,062
```

A 6-month interest-only moratorium with capitalisation is granted starting month 24. Capital amortization pauses; interest accrues at `r` and capitalises into principal at month 30 (the *carência total* mechanism, applied mid-contract):

```
S(30) = S(24) × (1 + r)^6 ≈ 83,062 × 1.003333^6 ≈ €84,740
```

The term extends by 6 months (so the original 120-month schedule now ends at month 126; 96 monthly installments remain), recomputed on the new balance:

```
P_new = 84,740 × 0.003333 / (1 − 1.003333^−96) ≈ €1,032.51
```

**Cash flows** (borrower's perspective):

```
CF(0)       = +100,000
CF(1..24)   = −1,012.30
CF(25..30)  = 0                  (moratorium window — no payment)
CF(31..126) = −1,032.51
```

The customer's new installment is about 2% larger than the original, the principal-side cost of the 6-month capitalised window.

**Term-extension alternatives.** PT DL 10-J/2020 extends the term by the moratorium duration (the example above). Two other policies appear in practice:

- *No extension* — the remaining schedule is compressed into the original maturity, producing a larger `P_new` over fewer periods.
- *Compress remaining* with a re-amortization that targets a fixed final-installment moment.

The choice is a policy parameter of the moratorium, not a property of the math; the formulas are identical with different `(n − m)` substituted for the remaining-term denominator.

**TAEG impact.** Applying a moratorium mutates the realized cash-flow vector, so the TAEG (per §6.2) of the executed contract differs from the contractual TAEG — the same kind of re-solve as §7.5 *amortização antecipada*, with the IRR computed numerically over the new vector. PT/EU consumer-credit rules typically require re-disclosure of the new TAEG via an updated SECCI or FINE; this is a regulatory consequence of the math.

**Retroactivity.** Government moratoria are frequently declared retroactively — the legal text is published days or weeks after the operative date. The math is unchanged: the cash-flow vector is rewritten as if the moratorium had taken effect at its operative date, and any payments collected in the interim are treated as reversal candidates against the corrected vector. Recording both the original and the corrected history is a system concern; the financial math operates on the corrected vector as the source of truth for TAEG, balances, and projections.

**Sub-flavour quick reference (g periods, starting at month m):**

| Flavour | Interest treatment | `S(m + g)` | Cash flow during window |
|---|---|---|---|
| Full, suspended interest | None accrues | `S(m)` | `0` |
| Full, capitalised interest | Accrues, capitalises | `S(m) × (1 + r)^g` | `0` |
| Interest-only, capitalised | Accrues, capitalises | `S(m) × (1 + r)^g` | `0` |
| Interest-only, deferred | Accrues, deferred | `S(m)` plus `J_def = S(m) × r × g` at end | `0` during window; lump at `m + g` |
| Interest-only, paid as scheduled | Accrues, paid each period | `S(m)` | `−S(m) × r` per period |
| Capital-only | Paid as scheduled | `S(m)` | `−S(m) × r` per period (*carência parcial*) |

All entries are compositions of the §7.1 and §7.4 formulas; no new mathematics is introduced.

---

## 8. Irregular Products

### 8.1 The Change of Nature

In products with a plan, the balance is discrete and fixed in each period:

```
J(t) = S(t-1) × r
```

In irregular products, the balance varies continuously between movements. Interest is an integral:

```
J = ∫ S(τ) × r dτ
    [t0, t1]
```

(In the general case `r` may also vary in time; for clarity we assume `r` is constant within the accrual period, which matches Portuguese current-account practice between rate-revision dates.)

Since the balance only changes at discrete moments (movements), `S(τ)` is a step function and the integral collapses into a sum:

```
J = Σ S(d) × r × Δt(d)
    d
```

Where:
- `S(d)` = balance during interval d
- `r` = daily rate = TAN / base (360 or 365)
- `Δt(d)` = duration in days with that balance

### 8.2 Operational Formula: Interest on Daily Balance

```
J(period) = (TAN / base) × Σ S(d)
                            d=1..N
```

The sum `Σ S(d)` is called the **number of capitals** — it is the sum of the daily balances over the period.

The **weighted average balance** is defined as:

```
S_avg = Σ [ S(d) × Δt(d) ] / Σ Δt(d)
```

And then:

```
J = S_avg × TAN × (N / base)
```

It is the same formula as a term deposit, but with an average balance instead of a fixed capital.

---

### 8.3 Example — Current Account (Demand Deposit)

Credit TAN = 0.5%, Act/365 basis, period = January (31 days).

**Movements:**

```
Jan 01: deposit of €1,000   → balance €1,000
Jan 10: deposit of €500     → balance €1,500
Jan 20: withdrawal of €1,300 → balance €200
Jan 31: end of period
```

**Daily balances calculation.** Convention: the new balance applies from the day of the movement up to (but not including) the day of the next movement. So a deposit on Jan 01 gives balance €1,000 on Jan 01 through Jan 09 (9 days); a deposit on Jan 10 changes it to €1,500 from Jan 10 through Jan 19 (10 days); and so on.

| Interval | Balance | Δt (days) | S × Δt |
|----------|---------|-----------|--------|
| Jan 01–09 | 1,000 | 9 | 9,000 |
| Jan 10–19 | 1,500 | 10 | 15,000 |
| Jan 20–31 | 200 | 12 | 2,400 |
| **Total** | | **31** | **26,400** |

```
J = (0.005 / 365) × 26,400 = €0.36
```

At the end of the month, 36 cents of interest are credited.

**Verification via average balance:**

```
S_avg = 26,400 / 31 = €851.61
J = 851.61 × 0.005 × (31/365) = €0.36  ✓
```

---

### 8.4 Example — Credit Card (Revolving)

TAN = 20%, base 365.

**January interest** (balance €1,000 during 31 days):

```
J(jan) = 1,000 × 0.20 × (31/365) = €16.99
```

Payment of €50 at the end of the month. Balance at the start of February (starting balance + interest accrued − payment received):

```
S(start feb) = 1,000 + 16.99 - 50 = €966.99
```

Unpaid interest is added to the outstanding capital — **it is compound interest disguised as monthly simple interest.**

---

### 8.5 The Revolving Evolution Equation

```
S(m) = S(m-1) × (1 + r) - P(m)
```

Where:
- `r` = TAN / 12 (monthly *taxa proporcional*)
- `P(m)` = payment in month m

This is a **difference equation** — recursive, with the same mathematical structure as Price.

Note that §8.4 used daily accrual (`TAN × days / base`) while this section uses monthly compounding at `TAN / 12`. The two differ by a few basis points and reflect two real conventions: card statements typically accrue interest daily but capitalize at month-end, which is well-approximated by the monthly-compounding model used here. The simplified form is the one used for projection and pay-off calculations.

**Analytical resolution with constant payment P:**

```
S(m) = S(0) × (1+r)^m - P × [(1+r)^m - 1] / r
```

This is exactly the balance formula of a Price credit — **paying a fixed installment on a revolving balance is mathematically equivalent to a Price credit.**

---

### 8.6 Example — Revolving Duration

Balance €1,000, TAN 20% (r = 0.01667/month), fixed payment of €50/month. How many months to pay it off?

Solve `S(n) = 0`:

```
0 = 1,000 × (1+r)^n - 50 × [(1+r)^n - 1] / r

(1+r)^n = (50/r) / (50/r - 1,000)
        = 2,999.4 / 1,999.4
        = 1.5002

n = log(1.5002) / log(1.01667) ≈ 24.5 months
```

In just over 2 years, €1,000 is paid off at €50/month. Total paid ≈ 24.5 × 50 = €1,225, of which ~€225 is interest — roughly a quarter of the principal, paid in interest alone, for a relatively small balance.

---

### 8.7 TAEG for Irregular Products

Conceptually it is the same as for products with a plan — solve:

```
0 = Σ CF(t) / (1+i)^t
```

**But the CFs have to be observed, not forecast.**

For a current account with 1 year of history:

```
CF(0)        = -initial_balance
CF(d₁..dₖ)  = ±observed movements
CF(365)      = +final_balance + credited_interest
```

The IRR of these CFs is the **actual realized return** — only computable after the fact.

---

## 9. Comparative Synthesis

### 9.1 Prospective vs Retrospective

| | Products with a plan | Irregular products |
|---|---|---|
| Balance | `S(t)` closed-form formula | `S(d)` observed |
| Interest | `J(t) = S(t-1) × r` | `J = Σ S(d) × r × Δt` |
| Cash flows | Forecast | Observed |
| TAEG | Ex-ante (contract) | Ex-post (history) |
| Equation | Solvable recursion | Numerical accumulation |

### 9.2 The Mathematical Unification

Both families follow the same fundamental identity:

```
S(t + Δt) = S(t) × (1 + r × Δt) − payments(Δt) + drawdowns(Δt)
```

Read `r` and `Δt` in matched units: for a Price installment, `r` is the period rate and `Δt = 1` period (so `r × Δt = r`, matching §3); for a current account, `r` is the daily rate and `Δt` is the number of days between movements. The form `(1 + r × Δt)` is simple-interest accrual within the interval — exact for products that capitalize once per period and a tight approximation otherwise.

The only difference is:
- **Products with a plan:** fixed Δt (month), payments known a priori, drawdowns only at t = 0
- **Irregular products:** variable Δt (intervals between movements), payments and drawdowns observed

The function `J = Σ S × r × Δt` is universal to both.

### 9.3 Product Taxonomy

```
Financial Product
├── With a plan (prospective)
│   ├── Depósito a prazo
│   │   ├── Juros no vencimento (interest at maturity)
│   │   ├── Juros periódicos (periodic interest)
│   │   └── Juros antecipados (interest paid in advance)
│   └── Credit
│       ├── Price / sistema francês (fixed installment)
│       ├── SAC (fixed amortization)
│       ├── American / bullet (fixed balance)
│       └── Composite
│           ├── Carência (parcial / total) + amortização
│           ├── Fixed rate + variable rate (revisão Euribor)
│           ├── With prestações extraordinárias
│           └── With amortização antecipada
└── Irregular (retrospective)
    ├── Conta à ordem (current account)
    └── Cartão de crédito (revolving)
```

### 9.4 Formula Map

| Concept | Expression |
|---|---|
| Base identity | `P(t) = J(t) + A(t)` |
| Balance | `S(t) = S(t-1) - A(t)` |
| Interest (product with a plan) | `J(t) = S(t-1) × r` |
| Interest (irregular product) | `J = Σ S(d) × r × Δt` |
| Price installment | `P = C × r / (1 - (1+r)^-n)` |
| SAC amortization | `A = C / n` |
| Balance after m installments | `S(m) = C×(1+r)^m - P×[(1+r)^m - 1]/r` |
| Simple interest (deposit) | `M = C × (1 + TAN × days/base)` |
| Compound interest (deposit) | `M = C × (1 + TAN/m)^(m·n)` |
| Effective annual rate | `TAE = (1 + TAN/m)^m - 1` |
| Present value | `PV = Σ CF(t) / (1+i)^t` |
| IRR / TAEG | solve `PV(i) = 0` for `i` |
| Rate relationship | `TAEG ≥ TAE ≥ TAN` |

---

## Glossary

| Term / Abbreviation | Meaning |
|---|---|
| TAN | *Taxa Anual Nominal* — nominal annual rate, no compounding |
| TAE | *Taxa Anual Efetiva* — effective annual rate, with compounding |
| TAEG / APR | *Taxa Anual Efetiva Global* — TAEG, including all mandatory charges |
| TANB / TANL | *Bruta / Líquida* — gross / net of withholding tax |
| *Taxa proporcional* | Periodic rate computed as `TAN / m`; Portuguese retail-credit convention |
| *Taxa equivalente* | Periodic rate computed as `(1 + TAE)^(1/m) − 1`; preserves effective return |
| SAC | *Sistema de Amortização Constante* — constant-amortization system |
| *Sistema francês* | French (Price) system — constant installment |
| *Carência parcial / total* | Grace period: interest-only / fully-capitalized |
| *Prestação extraordinária* | Extraordinary (balloon) payment within a Price/SAC schedule |
| *Amortização antecipada* | Early repayment of part or all of the outstanding capital |
| *Moratória* / Payment moratorium | Temporary legally-permitted suspension of credit payment obligations (§7.6); flavours by what is suspended and by interest treatment |
| *Comissão de abertura* | Origination fee |
| *Comissão de processamento* | Per-installment processing fee |
| *Imposto do Selo* | Portuguese Stamp Duty (tax on the contract, excluded from TAEG) |
| *Depósito a prazo* | Term deposit |
| *Conta à ordem* | Current account (demand deposit) |
| *Cartão de crédito* | Credit card |
| *Número de capitais* | Sum of daily balances over a period |
| Act/360, Act/365 | Day-count conventions (actual days over a 360- or 365-day year) |
| 30/360 | Day-count convention treating each month as 30 days, year as 360 |
| IRR | Internal Rate of Return |
| PV | Present Value |
| `C` | Initial capital (principal) |
| `r` | Periodic interest rate |
| `n` | Number of periods |
| `m` | Compounding periods per year |
| `S(t)` | Outstanding balance at period t |
| `J(t)` | Interest in period t |
| `A(t)` | Capital amortized in period t |
| `P(t)` | Installment in period t |
