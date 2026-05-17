# Financial Mathematics of Banking Products
## Conceptual Model and Examples

---

## 1. Framework

The main retail banking financial products can be grouped into two categories:

**With a predefined financial plan:**
- Term deposit
- Credit (various modalities)

**Without a predefined plan (irregular):**
- Current account (demand deposit)
- Credit card

All of them calculate interest. The fundamental difference is that products with a plan generate future cash flows known upfront, whereas irregular ones only produce cash flows observable after the fact.

---

## 2. The Generic Algorithm: Sequence of Cash Flows

Any financial product is a **sequence of cash flows over time:**

```
t0      t1      t2      t3      ...     tn
 |-------|-------|-------|-------|-------|
CF0     CF1     CF2     CF3            CFn
```

Each `CF` can be positive (inflow) or negative (outflow).

### 2.1 The Unifying Function: Present Value

The central mathematical model is the **present value of discounted cash flows:**

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

**What is fixed:** the installment `P(t)` — it is constant across all periods.

**Formula:**

```
P = C × r / (1 - (1+r)^-n)
```

**Calculation:**

```
P = 10,000 × 0.005 / (1 - 1.005^-12)
  = 50 / (1 - 0.94191)
  = 50 / 0.05809
  = €860.66
```

Interest and amortization derive from the fundamental identity:

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

### 4.2 SAC System (German)

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

**Characteristic:** amortizes capital faster at the beginning, so less interest is paid in total compared to Price.

---

### 4.3 American System (Bullet)

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

**Characteristic:** minimum installment during the life of the credit, but a high final payment ("balloon"). This is the mathematical structure of a bond.

---

### 4.4 Comparison of the Three Systems

| | Price | SAC | American |
|---|---|---|---|
| What is fixed | Installment | Amortization | Balance |
| Installment | Constant | Decreasing | Low + balloon |
| Capital amortized | Increasing | Constant | All at the end |
| Total interest | Intermediate | Lower | Higher |
| Initial burden | Medium | Higher | Lower |
| Typical use | Mortgage, personal credit | Mortgage (less common) | Bonds, corporate credit |

**Cash flows compared:**

```
Price:     [ -860.66 ; -860.66 ; -860.66 ; ... ; -860.66 ]
SAC:       [ -883.33 ; -879.16 ; -875.00 ; ... ; -837.50 ]
American:  [  -50.00 ;  -50.00 ;  -50.00 ; ... ; -10,050.00 ]
```

---

## 5. Term Deposit

In a term deposit the perspective is reversed: the depositor hands money over to the bank, which returns it with interest.

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

When interest is capitalized (automatically reinvested):

```
M = C × (1 + TAN)^n
```

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

**Interest paid in advance** (paid up front):
```
CF(0) = -10,000 + interest received upfront
CF(n) = +10,000
```

### 5.4 Deposit Rates

```
TANB  →  Gross Nominal Annual Rate (before taxes)
TANL  →  Net Nominal Annual Rate (after withholding tax)

TANL = TANB × (1 - 0.28)    (withholding in Portugal: 28%)
```

Effective annual rate with compounding m times per year:

```
TAE = (1 + TAN/m)^m - 1
```

---

## 6. Cross-Cutting Metrics

### 6.1 IRR — Internal Rate of Return

The IRR is the rate `i` that zeroes the present value of all cash flows:

```
0 = Σ [ CF(t) / (1+i)^t ]
    t=0..n
```

**Example — Price credit without charges:**

```
CF(0)     = -10,000
CF(1..12) = +860.66

0 = -10,000 + Σ [ 860.66 / (1+i)^t ]
              t=1..12
```

Solving: `i = 0.005` per period → **IRR = 6% annual = TAN** ✓

The IRR coincides with the TAN when there are no additional charges.

### 6.2 APR (TAEG) — Annual Percentage Rate of Charge

The APR is the metric that allows comparing any financial product regardless of the amortization system, fees, insurance, or periodicity.

**Formal definition:** the APR is the rate `i` that satisfies:

```
Σ [ Ak / (1+i)^tk ] = Σ [ Al / (1+i)^tl ]
```

Where:
- `Ak` = each amount received (capital handed over)
- `Al` = each amount paid (installments, fees, insurance...)
- `tk`, `tl` = moment of each cash flow, expressed in years

**It is mathematically equivalent to the IRR of the full set of cash flows**, including all mandatory charges.

**What enters the calculation:**

| Enters the APR | Does not enter |
|---|---|
| Installments | Taxes (IMT, Stamp Duty) |
| Origination fee | Notary costs |
| Appraisal fee | Default penalties |
| Life insurance premium | Optional insurance |
| Multi-risk insurance premium | |

**There is no closed-form formula** — it is solved numerically by the Newton-Raphson method (or bisection):

```
1. Build the full sequence of CFs (including fees, insurance)
2. Guess an initial value for i
3. Compute PV with that i
4. If PV ≠ 0, adjust i and repeat
5. Converges when |PV| < epsilon
```

**Example — Price credit with origination fee:**

Adding a €200 origination fee to the base example:

```
CF(0)     = -10,000 + 200 = -9,800   (bank hands over 10,000 but charges 200 upfront)
CF(1..12) = +860.66
```

Solving numerically: `i* ≈ 0.00857` per month

```
APR = (1 + 0.00857)^12 - 1 ≈ 10.78%
```

Compare with TAN = 6%. **The €200 fee added ~4.8 pp to the effective cost.**

**Relationship between the rates:**

```
TAN  →  nominal rate, no charges, no compounding
TAE  →  TAN converted to an annual basis (compounding)
APR  →  TAE + all mandatory charges

Always: APR ≥ TAE ≥ TAN
```

---

## 7. Composite Cases

### 7.1 Grace Period (Mix of Systems)

The credit has phases with different behaviors:

```
Phase 1 (grace): interest only  →  American style
Phase 2 (amortization): normal Price or SAC
```

The capital entering Phase 2 is the original capital untouched — because during the grace period nothing is amortized.

**Example:** €10,000, 6 months grace + 12 months Price, TAN 6%:

```
Phase 1 (t=1..6):
    P(t) = C × r = 10,000 × 0.005 = €50.00

Phase 2 (t=7..18), recompute Price on C=10,000:
    P = 10,000 × 0.005 / (1 - 1.005^-12) = €860.66
```

**Cash flows:**

```
CF(0)     = -10,000
CF(1..6)  = +50.00
CF(7..18) = +860.66
```

---

### 7.2 Variable Rate

**Example:** €10,000, Price, 12 months, with a revision at the 6th month:
- Initial TAN: 6% → r₁ = 0.005
- TAN after revision: 7% → r₂ = 0.00583

**Phase 1 (t=1..6) with r₁:**

```
P₁ = 10,000 × 0.005 / (1 - 1.005^-12) = €860.66
```

Balance at the end of Phase 1, using the general formula:

```
S(6) = C × (1+r₁)^6 - P₁ × [(1+r₁)^6 - 1] / r₁
     = 10,000 × 1.03038 - 860.66 × 6.0755
     = 10,303.80 - 5,227.49
     = €5,076.31
```

**Phase 2 (t=7..12) with r₂, on S(6):**

```
P₂ = 5,076.31 × 0.00583 / (1 - 1.00583^-6) = €862.18
```

**Cash flows:**

```
CF(0)     = -10,000
CF(1..6)  = +860.66
CF(7..12) = +862.18
```

---

### 7.3 Balloon Installments

Regular installments with one or more extraordinary payments at defined moments.

**Example:** Price 12 months + balloon of €2,000 at month 6:

The first 5 months are normal Price installments. In month 6, the installment is paid plus an extra €2,000:

```
S(6)_after_balloon = S(6) - 2,000
```

The Price is recomputed on the new balance for the remaining 6 months:

```
P_new = S(6)_after_balloon × r / (1 - (1+r)^-6)
```

**Cash flows:**

```
CF(0)     = -10,000
CF(1..5)  = +860.66
CF(6)     = +860.66 + 2,000 = +2,860.66
CF(7..12) = +P_new          (lower than 860.66 because the balance is smaller)
```

---

### 7.4 Balance After m Installments (General Formula)

For any Price credit, the outstanding balance after `m` installments is:

```
S(m) = C × (1+r)^m - P × [(1+r)^m - 1] / r
```

This formula is used to:
- Compute the balance at a rate revision point
- Compute the balance just before a balloon payment
- Compute the outstanding capital at any moment

---

## 8. Irregular Products

### 8.1 The Change of Nature

In products with a plan, the balance is discrete and fixed in each period:

```
J(t) = S(t-1) × r
```

In irregular products, the balance varies continuously between movements. Interest is an integral:

```
J = ∫ S(τ) × r(τ) dτ
    [t0, t1]
```

Since the balance only changes at discrete moments (movements), the integral collapses into a sum:

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

**Daily balances calculation:**

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

Payment of €50 at the end of the month. Balance at the start of February:

```
S(start feb) = (1,000 - 50) + 16.99 = €966.99
```

Unpaid interest is added to the outstanding capital — **it is compound interest disguised as monthly simple interest.**

---

### 8.5 The Revolving Evolution Equation

```
S(m) = S(m-1) × (1 + r) - P(m)
```

Where:
- `r` = TAN / 12 (equivalent monthly rate)
- `P(m)` = payment in month m

This is a **difference equation** — recursive, with the same mathematical structure as Price.

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

In just over 2 years, €1,000 is paid off at €50/month, with ~€250 of total interest paid.

---

### 8.7 APR for Irregular Products

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
| APR | Ex-ante (contract) | Ex-post (history) |
| Equation | Solvable recursion | Numerical accumulation |

### 9.2 The Mathematical Unification

Both families follow the same fundamental identity:

```
S(t + Δt) = S(t) × (1 + r × Δt) − payments(Δt) + drawdowns(Δt)
```

The only difference is:
- **Products with a plan:** fixed Δt (month), payments known a priori
- **Irregular products:** variable Δt (intervals between movements), payments observed

The function `J = Σ S × r × Δt` is universal to both.

### 9.3 Product Taxonomy

```
Financial Product
├── With a plan (prospective)
│   ├── Term deposit
│   │   ├── Interest at maturity
│   │   ├── Periodic interest
│   │   └── Interest paid in advance
│   └── Credit
│       ├── Price (fixed installment)
│       ├── SAC (fixed amortization)
│       ├── American (fixed balance)
│       └── Composite
│           ├── Grace period + amortization
│           ├── Fixed rate + variable rate
│           └── With balloon installments
└── Irregular (retrospective)
    ├── Current account
    └── Credit card (revolving)
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
| Compound interest (deposit) | `M = C × (1 + TAN)^n` |
| Effective annual rate | `TAE = (1 + TAN/m)^m - 1` |
| Present value | `PV = Σ CF(t) / (1+i)^t` |
| IRR / APR | solve `PV(i) = 0` for `i` |
| Rate relationship | `APR ≥ TAE ≥ TAN` |
