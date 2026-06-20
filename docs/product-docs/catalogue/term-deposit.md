# Term deposit — the product menu

**What you can offer with a *depósito a prazo*, in business terms.**

A term deposit is the simplest banking product: a customer locks an amount of
money for a fixed period, and the bank pays interest for the use of it. That
much is fixed. Everything else on this page is a **decision you make** when you
design an offering — and this is the menu of those decisions.

This page is for choosing the *shape* of a product. It is written for reading,
not for validation: each section tells you what you are choosing and what it
means for the customer, then points you to the exact, machine-checked contract
when you need it. The authoritative field-by-field truth lives in the
[generated family schema](../reference/family-schemas/term-deposit.md); the
financial mathematics behind each choice is in
[financial concepts §5](../../product-management/financial_concepts/banking_products_financial_mathematics.md);
the v1 product scope and its reasoning is
[02 — v1 Scope](../../product-management/product_concepts/02-v1-scope-term-deposits.md).

Everything here is **what the engine can do today**. Roadmap items (payment
moratoria, more exotic penalties) are deliberately not listed — when they
arrive, they arrive here.

---

## The seven decisions

| # | You decide… | And the customer experiences… |
|---|---|---|
| 1 | [When interest is paid](#1-when-interest-is-paid) | A lump sum at the end, a regular income, or cash up front |
| 2 | [How the rate is shaped](#2-how-the-rate-is-shaped) | One rate throughout, or a rate that climbs the longer they stay |
| 3 | [What happens at maturity](#3-what-happens-at-maturity-auto-renewal) | The money returns, or the deposit rolls over automatically |
| 4 | [The penalty for breaking early](#4-the-penalty-for-breaking-early) | What they give up if they need the money before the end |
| 5 | [Whether they can take some out](#5-whether-they-can-take-some-out-partial-withdrawals) | A locked deposit, or one they can dip into under rules |
| 6 | [Who is eligible](#6-who-is-eligible-commercial-gates) | An open product, or one reserved for a target segment |
| 7 | [Amount and timing limits](#7-amount-and-timing-limits) | The minimum/maximum they can place, the term, the launch date |

And then there are [the rules you don't set](#the-rules-you-dont-set) — fixed
for you by Portuguese regulation.

---

## 1. When interest is paid

The single biggest choice. The same money and the same headline rate feel like
three different products depending on *when* the interest lands:

- **At maturity** (*juros no vencimento*) — interest is paid once, at the end,
  together with the principal. The most common Portuguese variant. Simplest for
  the customer to understand: "put in €10,000, get €10,608 back in a year."
- **Periodic** (*juros periódicos*) — interest is paid out at regular
  intervals (monthly or quarterly) into the customer's current account, with
  the principal returned at the end. This is an *income* product: the customer
  feels a steady payment while their capital stays locked.
- **In advance** (*juros antecipados*) — the whole term's interest is paid up
  front at the moment the deposit is opened; only the principal comes back at
  the end. A cash-management framing — the customer gets money in hand on day
  one.

Periodic interest is paid **monthly or quarterly** in v1. The variant changes
only the payment schedule — the product is otherwise the same.

*Sources: [02 §2.1](../../product-management/product_concepts/02-v1-scope-term-deposits.md);
[financial concepts §5.3](../../product-management/financial_concepts/banking_products_financial_mathematics.md);
schema field `interest_variant` / `payment_period_months` in the
[family schema](../reference/family-schemas/term-deposit.md).*

---

## 2. How the rate is shaped

You choose between two rate shapes:

- **Flat** — one rate for the whole term. The customer is quoted a single TAN
  and that is what they earn.
- **Stepped** — the rate rises at defined points in the term (e.g. a lower rate
  for the first 90 days, a higher rate after). A *loyalty* shape: it rewards the
  customer for leaving the money in longer.

In both cases you do **not** type a number here. The actual rate lives on a
separate, faster-moving **rate sheet** — so the pricing team can re-price
without touching the product's structure. This page is about the *shape*; the
[rate-sheet docs](../how-to/author-and-deploy-a-rate-sheet.md) cover the number.

*Sources: schema `rate` (flat XOR stepped) in the
[family schema](../reference/family-schemas/term-deposit.md);
[financial concepts §5.4](../../product-management/financial_concepts/banking_products_financial_mathematics.md);
rate vs structure separation — [ADR-PC-008](../../product-management/product_concepts/adrs/ADR-PC-008-rate-sheet-storage-and-deploy-api.md).*

---

## 3. What happens at maturity (auto-renewal)

When the term ends, the deposit either closes or rolls over. You choose which,
and on what terms:

- **None** — the deposit matures and the money (principal plus final interest)
  settles back into the customer's current account. They decide what to do next.
- **Same term, current rate** — the deposit automatically renews for the same
  length, at the bank's *then-current* standard rate for the product. The
  customer keeps saving without lifting a finger; the rate moves with the market.
- **Same term, original rate** — renews for the same length at the *original*
  rate. Rarer, and pack-restricted, because it locks a possibly-stale rate.

Whatever you choose, the customer always keeps a regulated **opt-out window**
before maturity (typically the final 14 days) in which they can stop a renewal
and take their money with no penalty.

*Sources: [02 §2.4.4](../../product-management/product_concepts/02-v1-scope-term-deposits.md);
schema `auto_renewal_policy` in the
[family schema](../reference/family-schemas/term-deposit.md).*

---

## 4. The penalty for breaking early

If a customer breaks the deposit before the end, they give something up. You
define what:

- **Flat penalty** — one rule for any early exit (e.g. "lose all accrued
  interest", or a fixed haircut).
- **Banded penalty** — the penalty depends on *how early* they break. A typical
  schedule charges more for breaking in the first weeks and less the closer they
  are to maturity — for example: 100% of accrued interest if broken within 30
  days, 50% within 90 days, 25% after that.

For either shape you also choose the **basis** — whether the penalty bites on
the accrued interest, on the principal, or on both — and you can set a **floor**,
a minimum payout the customer's net is never allowed to fall below.

> Which penalty bases are *legally permissible* is restricted by the Portuguese
> regulatory pack, not by you. You pick from what the pack allows.

*Sources: [02 §2.5](../../product-management/product_concepts/02-v1-scope-term-deposits.md);
schema `early_termination` (flat XOR banded), `basis`, `floor_cents` in the
[family schema](../reference/family-schemas/term-deposit.md).*

---

## 5. Whether they can take some out (partial withdrawals)

By default a term deposit is **locked**: the customer commits the whole amount
for the whole term. You can optionally allow **partial withdrawals**, and gate
them with three controls:

- a **minimum withdrawal** amount (no trivial dips),
- a **minimum remaining balance** (the deposit must stay meaningful), and
- a **lock-up window** (*carência*) after opening, during which no withdrawal is
  allowed at all.

If you don't enable this, no partial withdrawals are permitted — the customer's
only early exit is to break the deposit entirely ([decision 4](#4-the-penalty-for-breaking-early)).

> One hard rule: partial withdrawals **cannot** be combined with *interest paid
> in advance* ([decision 1](#1-when-interest-is-paid)). That product pays the
> full term's interest up front on the full principal, so there is no later
> accrual to re-base a reduced balance against.

*Sources: schema `partial_withdrawal` block (and the ADVANCE exclusion) in the
[family schema](../reference/family-schemas/term-deposit.md);
[02 §2.4.1](../../product-management/product_concepts/02-v1-scope-term-deposits.md).*

---

## 6. Who is eligible (commercial gates)

By default a term deposit is open to any eligible resident individual. You can
optionally **reserve** a product for a target segment by requiring one or more
eligibility conditions, checked upstream before the deposit can be opened:

- **New client** — only customers new to the bank.
- **New money** — only funds new to the bank (not shuffled from an existing
  account).
- **Salary domiciled** — only customers who receive their salary into the bank.
- **Mortgage linked** — only customers with a mortgage at the bank.

These are the levers behind "exclusive rate for new customers" or "preferential
rate if you bring your salary." The bank's other systems (CRM, core banking,
the credit system) actually *evaluate* whether a customer qualifies; the product
just declares which conditions it requires. Most launch products require none.

*Sources: schema `required_preconditions` in the
[family schema](../reference/family-schemas/term-deposit.md);
[ADR-PC-024](../../product-management/product_concepts/adrs/ADR-PC-024-constitution-precondition-contract.md).*

---

## 7. Amount and timing limits

The plain corners of the product:

- **Term length** — how long the money is locked, in days.
- **Minimum / maximum principal** — the smallest and (optionally) largest amount
  a customer may place. The minimum keeps the product sensible; the maximum is a
  risk-corridor lever.
- **Activation date** — an optional "effective from" date, so a product can be
  authored now and go live later (e.g. a campaign that opens on the 1st).

*Sources: schema `term_days`, `principal_bounds`, `effective_from` in the
[family schema](../reference/family-schemas/term-deposit.md).*

---

## The rules you don't set

Just as important as the knobs is knowing what is **fixed for you** by Portuguese
regulation and the engine — these are *not* negotiable per product:

- **28% withholding tax on interest.** Every interest payment is taxed at source.
  You quote the gross rate (TANB); the customer receives the net (TANL). The tax
  is applied to each payment as it happens — not by quietly shrinking the rate.
- **Act/360 day-count.** How a day of interest is computed. The PT standard for
  retail deposits.
- **Euro only.** v1 term deposits are EUR-denominated.
- **Exact money.** All amounts are whole cents — no fractional-cent rounding
  games.

The customer-facing information sheet (*Ficha de Informação Normalizada*) and the
effective annual rate (TAE, which shows the compounding effect) are produced for
you from these rules.

*Sources: [02 §2.2](../../product-management/product_concepts/02-v1-scope-term-deposits.md);
[financial concepts §5.4](../../product-management/financial_concepts/banking_products_financial_mathematics.md).*

---

## When you've decided

Once you know the shape you want, the next step is to author it as a **variant**:

- [Write your first product variant](../tutorials/write-your-first-variant.md) —
  hold-your-hand walkthrough.
- [The family schema](../reference/family-schemas/term-deposit.md) — the exact,
  machine-checked contract your variant must satisfy.
