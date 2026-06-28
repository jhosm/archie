# Brief 01 — What is a Current / Demand Account? (jurisdiction-agnostic)

> Part 1 of 3. Establishes the universal vocabulary, taxonomy, and lifecycle of the
> **demand / transactional deposit account**, reused by
> [02 — Portugal context](./02-portugal-context.md) and
> [03 — competitive landscape](./03-competitive-landscape-pt.md).
> The term *conta à ordem* is Portuguese, but the *instrument* is universal — Portugal's
> law, taxes, fee caps, and market habits are deferred to Brief 02.
> Conceptual brief — few perishable figures; any global statistic is tagged `[REFRESH]`.

## In plain English

A current account (a *conta à ordem* in Portugal) is the everyday account most people think of
as "my bank account": money is there **on demand** — no maturity, no lock-up — and you pay in
and take out as often as you like. A debit card, your direct debits, and your salary all hang
off it. It is the plain **liquid opposite** of a term deposit, where the money is locked away
until a maturity date.

The one idea that makes a current account special is that the bank tracks **two balances, not
one**. The **accounting balance** is what has actually *posted* — the cold, settled truth of the
ledger. The **available balance** is what you can actually *spend right now*, after the bank
subtracts money that has been earmarked but not yet settled (a hotel pre-authorization, a
pending card purchase) and adds any overdraft you are allowed to dip into. That gap between the
two balances — created by the lag between *approving* a payment and *settling* it — is the spine
this whole brief hangs on. Everything below (the mechanics, the instruments that ride the
account, its role as the hub the other products plug into, the lifecycle) is detail hung on that
two-balance idea.

---

## 1. Definition & boundaries — the "deposit-account spectrum"

A current account is a **demand deposit**: customer funds held by a deposit-taking institution,
**available on demand** (no maturity, no notice), tracked as an **authoritative running balance**,
with **unlimited credits and debits** and a set of **payment instruments attached** (debit card,
direct debits, transfers). It typically earns little or no interest; the institution's economics
come from fees, float, and cross-sell rather than a lending margin.

It is easiest to define by contrast with its neighbours on the deposit-account spectrum:

| Instrument | Funds available | Maturity / lock | Interest | Payment instruments attached | Is it a deposit? |
|---|---|---|---|---|---|
| **Current / demand account** | **On demand, any time** | **None** | Little / none | **Yes — card, direct debits, transfers** | **Yes** |
| **Savings account** | On demand, sometimes notice/withdrawal-limited | None (or notice) | Yes (the point of it) | Usually none / limited | Yes |
| **Term deposit (*depósito a prazo*)** | **Locked to maturity** | **Fixed term** | Yes (higher, for the lock-up) | None | Yes — the [term_deposit](../../02-v1-scope-term-deposits.md) family; the current account is its **liquid opposite** |
| **Money-market account** | On demand, often with limits | None | Yes (market-linked) | Limited | Yes |
| **Credit-card account** | A revolving credit *line*, not your money | n/a (a line) | Charged *to* you on carried balance | The card | **No — it is a liability/line**, see [`../credit-cards/`](../credit-cards/01-fundamentals.md) |
| **E-money / payment account** | On demand | None | None | Card, transfers | **No — holds balances, takes no deposits**; safeguarded, not deposit-insured (e-money under the E-Money Directive; payment accounts under PSD2) |

The defining attributes of the *current account* specifically: **(a) demand liquidity** (no
maturity — money out whenever you want), **(b) a transactional role** (unlimited movements with
instruments riding the account), and **(c) the two-balance split** (an accounting balance vs a
derived available balance — the subject of §2, and the feature that separates it from a plain
savings book). The term deposit shares (a-less): it is the *locked* counterpart. The credit-card
account is a **line of credit**, not a deposit. The e-money / payment account *looks* like a
current account to a user but takes **no deposits** — its balances are **safeguarded, not
deposit-insured**. Two distinct regimes sit behind that label: **e-money** is a creature of the
**E-Money Directive** (the EMD), where customer funds are *safeguarded*; **payment accounts /
payment institutions** are the **PSD2** domain. The concrete Portuguese consequences — the **IBAN
country** and whether a balance is covered by the **FGD** deposit guarantee versus mere
safeguarding — are a decision-relevant distinction deferred to Brief 03.

---

## 2. The two balances — the conceptual spine

A current account carries two balances, and confusing them is the classic source of customer
disputes and ledger bugs:

- **Accounting / ledger balance** — what has actually **posted**: the sum of settled credits and
  debits. The bank's authoritative books.
- **Available balance** — what is **spendable right now**: the accounting balance, *minus* funds
  earmarked by approved-but-unsettled authorizations (**holds**) and uncleared items, *plus* any
  authorized-overdraft headroom.

The relationship, stated as the durable identity:

```
available balance = accounting balance − Σ(active holds) [+ authorized-overdraft limit]
```

The key conceptual point: **the available balance is a *derived* quantity, not a second stored
number.** It is recomputed from the accounting balance and the set of active holds. (*Whether a
ledger engine models it as a stored field or a recomputed fold is a babelstone boundary question,
decided in [ADR-PC-030](../../adrs/ADR-PC-030-product-scope-and-boundary.md) — whose
Verifiable-commitments framing treats the available balance as a **rebuildable fold**, not a
stored mutable number — and not asserted here.*)

**Why two balances exist at all** is the **authorization → capture → settlement → posting** gap.
When you tap your card, the merchant gets an *authorization* (a promise the funds are good)
**before** the money actually moves. The funds are **earmarked** so you cannot spend them twice,
but they have not yet *posted*. The canonical case is the **hotel / car-rental / fuel
pre-authorization**: the merchant places a hold for an *estimated* amount (a fuel pump might hold
€100 against a €40 fill), the available balance drops by the held amount immediately, and the
final settled (captured) amount — often smaller — posts days later, at which point the hold is
released. Until then, accounting balance and available balance diverge by exactly the held sum.

---

## 3. Core mechanics

- **Credits & debits as a running ledger.** Money in (salary, transfers received, deposits) and
  money out (card spend, direct debits, transfers sent, withdrawals) accumulate as an ordered
  series of postings. The balance is the running sum; the history is the proof.
- **Value date vs booking/posting date.** The **booking (posting) date** is when an item *shows*
  on the account; the **value date** is when it counts for **interest and availability**. They
  often differ — a Friday deposit may book Friday but value Monday — which is exactly how a small
  available-vs-accounting gap arises even without a card hold.
- **Holds / authorizations / earmarks.** A hold is **placed** when an authorization is approved,
  **captured** when the transaction settles (the hold becomes a posted debit), or **expires** on
  timeout if no capture arrives within the scheme/issuer window. Real life adds wrinkles:
  **partial captures** (the fuel example — capture €40 of a €100 hold, release the rest),
  **reversals** (an authorization voided before capture), and **multiple holds** against one
  account at once. (The hold's place/capture/expire lifecycle is described here at product level;
  its modelling is the conta à ordem family ADR's job, not this brief's.)
- **Overdraft (*descoberto*).** Going below zero. Two flavours, and the distinction is legal as
  much as mechanical: **authorized / arranged** — a pre-agreed credit line on the account, with a
  limit, an interest rate, and fees, that *extends* the available balance below zero; and
  **unauthorized / unarranged** — going below zero (or past the arranged limit) **without
  agreement**, usually penalised harder. Overdraft is **credit**, which is why the consumer-credit
  regime reaches it in Brief 02. The arranged limit is the `[+ authorized-overdraft]` term in §2's
  identity.
- **Statement cycle.** A periodic (usually monthly) **statement** closes a cycle and stands as an
  immutable record of every posting in it, plus opening/closing balances and any fees charged.
- **Payment instruments riding the account.** A current account is the *thing payment instruments
  point at*: the **debit card** (drawing on the available balance in real time); **direct debits**
  (a *pull* — the biller initiates, e.g. utilities, under a mandate); **standing orders / standing
  instructions** (a scheduled *push* the holder sets up, e.g. rent); **credit transfers** (SEPA in
  the euro area — one-off pushes between accounts); and, historically, **cheques** (now largely
  displaced by cards and instant transfers).
- **Interest & fees economics.** Demand balances typically earn **little or no interest** — the
  point is liquidity, not yield. The institution's money comes from **account fees** (maintenance,
  per-transaction, instrument fees), the **float** on balances, **overdraft interest/fees**, and
  **cross-sell** — *not* a lending margin on the deposit. This is the structural opposite of the
  loan and card economics in the sibling briefs.

---

## 4. The account as the hub (why this shape is foundational)

The current account is not just *a* product — it is the **settlement account the other products
plug into**. Trace the money and everything routes through it:

- **Salary / credits in** — the *domiciliação de ordenado* that anchors the relationship.
- **Bills / direct debits out** — utilities, subscriptions, taxes.
- **Loan disbursement in, installments out** — a *crédito pessoal* lands in the current account
  and is repaid from it by direct debit (see [`../personal-loan/`](../personal-loan/01-fundamentals.md)).
- **Deposit interest and maturity in, constitution debit out** — a term deposit is funded from the
  current account and pays interest/maturity back into it.
- **Card statement settles from it** — a credit-card bill is paid out of the current account (see
  [`../credit-cards/`](../credit-cards/01-fundamentals.md)).

This is precisely the *"the hub the other three settle against"* role that
[ADR-PC-030](../../adrs/ADR-PC-030-product-scope-and-boundary.md) gives the **4th product shape**
(the transactional balance account) — described here at the **product level only**. It is *why*
the current account is the most fundamental retail shape: the liability (term deposit), the
closed-end asset (personal loan), and the revolving asset (credit-card account) all **settle
against** the demand account, making it the connective tissue of a retail relationship.

---

## 5. The authorization pipeline (generic)

When a debit is *attempted* — a card tap, an ATM withdrawal — it runs through a pipeline before
money is committed:

| # | Stage | What it asks | Nature |
|---|---|---|---|
| 1 | **Instrument valid?** | Card not expired / blocked? | Instrument check |
| 2 | **Customer authenticated?** | PIN / 3DS / strong customer authentication | Authentication (regulated) |
| 3 | **Funds available?** | Is the **available balance** sufficient? | Ledger read |
| 4 | **Within product rules / limits / overdraft?** | Within pack limits and *descoberto autorizado*? | Rules check |
| 5 | **Earmark the funds** | Place the **hold** | Ledger append |
| 6 | **Fraud screen** | Does this look fraudulent? | Risk model |
| 7 | **Effect on the rails** | Network authorization / settlement | Payment rails |

Stages 3-5 — *funds, rules, hold* — are the **ledger-and-rules core** of the decision: read the
balance, apply the account's own rules, earmark. Stages 1-2 and 6-7 need an instrument, an
authentication factor, a fraud model, or the payment network. **Where a ledger engine's
responsibility starts and stops within this pipeline is a babelstone boundary question, decided
in [ADR-PC-030 §P3](../../adrs/ADR-PC-030-product-scope-and-boundary.md) (the engine owns stages
3–5 in real time) — not asserted here.** This brief only observes, descriptively, that the
pipeline *has* these stages and that the "decide and record" stages are conceptually distinct
from the "authenticate / screen / physically move" ones.

---

## 6. Variant taxonomy

The axes below become the **comparison dimensions** in Brief 03.

- **By holder.** **Personal** — *sole* (one holder) vs *joint*; and a joint account splits further
  into **solidária** (any holder acts alone) vs **conjunta** (all holders must act together) —
  versus **business / corporate** accounts.
- **By packaging.** **Standalone** account (pay per service) vs a **bundled "package" account**
  (*conta-pacote* — a flat monthly fee bundling the account + card + insurance + a transfer
  allowance) vs a **basic / regulated** account (a legally mandated minimum-service account at a
  capped cost — the *serviços mínimos bancários* in Portugal, detailed in Brief 02).
- **By channel / provider.** **Incumbent-bank** account vs a bank's **digital-arm** account
  (same banking licence, app-first, lower fees) vs a **neobank / e-money** account (the IBAN-country
  and deposit-guarantee caveat from §1 applies here).
- **By currency.** **Single-currency** vs **multi-currency** (hold and spend in several currencies
  with in-app FX — the neobank angle).

---

## 7. Lifecycle

**A. Product clock.** Design → pricing (fee schedule, overdraft terms) → launch → portfolio
management → repricing → retirement / migration of the product line.

**B. Account / customer clock** *(the spine of the product)*. Application → **KYC / AML**
identity and screening → **open + IBAN issued** → **active** (the long steady state, with credits
in and debits out) → **servicing** (overdraft-limit changes, joint-holder add/remove, instrument
reissue, address changes) → **dormancy / inactivity** (no customer-initiated movement for a long
period) → **escheat / unclaimed-balance** (long-dormant balances are reported and, depending on
the jurisdiction's prescription / escheat regime, eventually transferred to the State or a
designated fund — a notably statutory lifecycle *endpoint*; the exact Portuguese mechanism is
deferred to Brief 02) → **closure** (voluntary, or **on death → succession**, which is adjudicated
**upstream** by a court/notary — the engine *records* a transfer to heirs but never decides who
inherits or pays an heir; see [ADR-PC-030 §Decision "Succession is upstream-decided"](../../adrs/ADR-PC-030-product-scope-and-boundary.md)).

**C. The overdraft default fork.** When an **authorized overdraft** is not repaid, the account
forks off the steady state into **arrears → collections** — because the overdraft is *credit*, this
path is governed by the consumer-credit and pre-default regimes covered in Brief 02.

---

## 8. Risk & control backdrop (generic)

- **Risks.** **Overdraft credit risk** (an unrepaid arranged or unarranged overdraft is a loss);
  **fraud** (unauthorized transactions, account takeover, authorised-push-payment / APP scams);
  **operational** risk; **AML / transaction monitoring** obligations; and **dormancy / unclaimed
  property** (long-inactive balances that must be identified, reported, and eventually escheated
  per the jurisdiction's regime).
- **Controls.** Strong customer authentication, transaction monitoring, **deposit insurance** (a
  scheme guaranteeing balances up to a ceiling if the institution fails), standardised **fee
  disclosure** (so accounts are comparable), and **garnishment / court-ordered holds** — a freeze
  on a balance ordered by a court or tax authority. Note this is a **second, distinct meaning of
  "hold"**: it is a *legal freeze* on funds, **not** the authorization earmark of §2-3. Keeping the
  two senses apart (the product earmark vs the legal *penhora*) matters because they behave
  differently and the family ADR will model them differently.

---

## Figures to verify `[REFRESH]`

- Any **global market statistic** cited (none asserted here by design — this brief is conceptual).
- Typical **hold-expiry windows, statement-cycle lengths, overdraft terms, and dormancy/escheat
  periods** vary by scheme / institution / jurisdiction — treat the *mechanism* as durable and any
  specific number as illustrative until Brief 02/03 + the refresh pass.
- The **e-money vs payment-account regime labels** (E-Money Directive for e-money; PSD2 for payment
  accounts) and the **deposit-guarantee vs safeguarding** distinction — stated conceptually here;
  confirm the concrete Portuguese application (IBAN country, FGD scope) in Brief 03.

---

*Next:* [02 — Conta à ordem in the Portuguese context →](./02-portugal-context.md)
