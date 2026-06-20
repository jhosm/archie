# Brief 01 — What is a Credit Card? (jurisdiction-agnostic)

> Part 1 of 3. Establishes the universal vocabulary, taxonomy, and lifecycle reused by
> [02 — Portugal context](./02-portugal-context.md) and
> [03 — competitive landscape](./03-competitive-landscape-pt.md).
> Conceptual brief — few perishable figures; any global statistic is tagged `[REFRESH]`.

## In plain English

A credit card is a plastic-or-digital **key to a pool of money the bank lends you**, up to a
limit, that you can borrow and repay over and over. Each month the bank tallies what you
spent and sends a bill. If you pay it **in full**, the borrowing was free — the bank still
made money, just from the *shop* (a fee called interchange), not from you. If you pay only
**part** of it, you start paying **interest** on the rest, and that interest is where most
credit-card profit comes from.

That single fork — pay-in-full ("transactor") vs. carry-a-balance ("revolver") — is the
hinge the whole product turns on. Everything below (mechanics, who-pays-whom, the variants,
the lifecycle) is detail hung on that hinge.

---

## 1. Definition & boundaries — the "card spectrum"

A credit card is an instrument that gives the holder access to a **revolving, usually
unsecured line of credit** extended by an issuer, usable for purchases and cash advances,
billed periodically, repayable in flexible amounts (subject to a minimum), with interest
charged on any unpaid balance.

It is easiest to define by contrast with its neighbours:

| Instrument | Whose money | When you pay | Credit line? | Interest? |
|---|---|---|---|---|
| **Debit card** | Yours (deposit account) | Instantly | No | No |
| **Prepaid card** | Yours (pre-loaded) | Before use | No | No |
| **Deferred-debit card** | Bank fronts, yours settles | End of cycle, **in full** | Soft / short | Normally none |
| **Charge card** | Issuer credit | At statement, **in full** | Yes, no revolving | Penalty only |
| **Credit card** | Issuer credit | Minimum → full, **your choice** | **Yes, revolving** | **Yes, on carried balance** |
| **BNPL / installment** | Lender/merchant credit | Fixed installments | Per-purchase | Sometimes 0% |

The defining attributes of the *credit card* specifically: **(a) a revolving line** (repaid
credit becomes available again), **(b) payment optionality** (you choose how much to repay
above a minimum), and **(c) interest on the carried balance**. Charge cards lack (b)/(c);
deferred-debit lacks the revolving interest mechanic; debit/prepaid lack the credit line.

---

## 2. Core mechanics

- **Credit limit / available credit ("open-to-buy").** The ceiling on outstanding balance.
  Authorizations place **holds** that reduce available credit before the charge even posts.
- **Billing cycle → statement → due date.** Spend accumulates over a ~monthly cycle; a
  statement closes it; payment is due ~2–3 weeks later.
- **Grace period.** An interest-free window on *purchases* — but typically **only if the
  previous balance was paid in full**. Carry a balance and you usually lose the grace period
  until you clear it. Cash advances normally get **no** grace period (interest from day one).
- **Minimum payment.** A small fraction of the balance (plus fees + interest). Paying only
  the minimum keeps the account current while **maximising the interest the issuer earns** —
  the core of revolver economics.
- **Transactor vs. revolver.** *Transactor* pays in full → no interest; issuer earns
  interchange + any annual fee. *Revolver* carries a balance → issuer earns interest. This
  split drives product design and profitability.
- **Interest types (APRs).** Purchase APR; **cash-advance APR** (higher, no grace);
  **balance-transfer APR** (often a promotional teaser); **penalty/default APR** (triggered
  by missed payments).
- **Balance-computation method.** How interest is calculated — commonly **average daily
  balance** (with or without new purchases); some methods (e.g. two-cycle billing) are now
  restricted in many jurisdictions.

---

## 3. The payments value chain — the four-party scheme model

Most credit cards run on an **open-loop, four-party** model:

```
  Cardholder ──spends──▶ Merchant
      ▲                     │
      │ bills               │ deposits via
      │                     ▼
   ISSUER ◀──scheme rails──▶ ACQUIRER
   (cardholder's bank)   (merchant's bank)
            │
        SCHEME / NETWORK (Visa, Mastercard…)
```

- **Roles:** cardholder, merchant, **issuer** (extends the credit, bills the cardholder),
  **acquirer** (banks the merchant), **scheme/network** (the rails + rules).
- **Three-party (closed-loop) variant:** Amex/Discover historically act as issuer +
  acquirer + scheme at once.
- **Money flow on a €100 sale:** acquirer pays the merchant €100 **minus the merchant
  discount (MDR/MSC)**; the issuer pays the acquirer €100 **minus interchange**; the
  cardholder eventually pays the issuer €100. The **scheme** takes scheme fees from both
  sides. **Interchange** (acquirer → issuer) is the central transfer that funds issuer
  economics and rewards.
- **Float:** the issuer funds the gap between the cardholder's purchase and repayment.
- **Tech layer:** PAN + BIN/IIN, EMV chip, contactless (NFC), **tokenization** (network &
  device tokens behind Apple/Google Pay), and **3-D Secure / strong authentication**.
- **Settlement stages:** authorization (real-time) → clearing (merchant submits) →
  settlement (interbank funds movement) → posting (to the cardholder's account).

---

## 4. Issuer economics — how a credit card makes money

| Revenue | Cost |
|---|---|
| **Net interest income** (from revolvers) | Cost of funds |
| **Interchange** (from all spend) | **Credit losses** (charge-offs) |
| **Fees:** annual, late, over-limit, cash-advance, **FX / foreign-transaction**, balance-transfer | Fraud losses |
| Merchant-funded contributions (co-brand) | Rewards / redemption cost |
| | Servicing, operations, acquisition, capital |

The tension: a **transactor** with rich rewards and no annual fee can be *unprofitable*
(interchange minus rewards is thin); a **revolver** is highly profitable via interest. Issuers
therefore design rewards and minimum payments to nudge behaviour and segment risk.

---

## 5. Product taxonomy / variants

The axes below become the **comparison dimensions** in Brief 03.

- **By credit mechanics:** charge · revolving · deferred-debit · installment.
- **By customer segment:** consumer · student · **secured** (deposit-collateralised,
  credit-building) · commercial (small-business · corporate · purchasing/P-card · fleet).
- **By value proposition:** general-purpose **rewards** (cashback / points / miles) ·
  **co-branded** (airline, hotel, retailer) · affinity · **premium/travel** (lounge,
  insurance, concierge) · low-interest / balance-transfer · store / private-label.
- **By tier:** classic/standard → gold → platinum → signature/world → infinite/world-elite
  (the Visa/Mastercard benefit ladders).
- **By scheme:** Visa · Mastercard · Amex · Discover/Diners · **domestic** schemes.

---

## 6. Lifecycle — three nested clocks

**A. Product lifecycle.** Market/segment definition → product design (rewards, pricing,
T&Cs) → underwriting policy & risk appetite → pricing (APRs, fees) → launch → portfolio
management (monitoring, repricing, line management) → renewal/refresh → retirement/migration.

**B. Account / customer lifecycle.** Marketing / pre-screen → **application** → identity
verification (KYC/AML) → **credit assessment** (bureau data, scoring, affordability) →
**decision** (approve / decline / refer) + limit & APR assignment → card production /
issuance → activation → **usage** → statementing → **repayment** → servicing (credit-line
increase/decrease, replacement, reissue at expiry, lost/stolen) → **delinquency →
collections** (days-past-due buckets, roll rates) → **charge-off** (~180 dpd) → recovery /
debt sale → account closure (voluntary or involuntary).

**C. Transaction lifecycle.** Authorization (real-time approve/decline, fraud screen,
available-credit check, hold placed) → capture / clearing → settlement → posting (hold
released) → reconciliation → **dispute / chargeback** (cardholder dispute → chargeback →
representment → arbitration).

---

## 7. Risk & control backdrop (generic)

- **Risks:** credit (default), **fraud** (card-not-present, counterfeit, account takeover,
  application fraud, first-party/"friendly" fraud), operational, conduct/compliance.
- **Controls:** PCI-DSS, EMV liability shift, 3-D Secure / strong authentication,
  velocity & ML fraud models, expected-loss provisioning (e.g. IFRS 9).
- **Disclosure norms:** standardised APR disclosure (e.g. the US "Schumer box"; the EU's
  SECCI / standardised pre-contractual information — picked up concretely in Brief 02).

---

## Figures to verify `[REFRESH]`

- Any global market statistic cited (none asserted here by design).
- Typical grace-period length and minimum-payment percentages vary by issuer/jurisdiction —
  treat the *mechanism* as durable, specific numbers as illustrative.

---

*Next:* [02 — Credit cards in the Portuguese context →](./02-portugal-context.md)
