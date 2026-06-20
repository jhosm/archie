# Brief 02 — Credit Cards in the Portuguese Context

> Part 2 of 3. Re-draws the universal picture from [01 — fundamentals](./01-fundamentals.md)
> with Portugal's terminology, regulation, taxes, and market habits. Feeds the issuer
> comparison in [03 — competitive landscape](./03-competitive-landscape-pt.md).
> **Every perishable figure is tagged `[REFRESH]`** — verify before relying on a number.

## In plain English

A Portuguese credit card works like the generic one in Brief 01, but three local forces bend
it noticeably. **First, the law caps how expensive it can get:** Banco de Portugal publishes a
*maximum* TAEG (the all-in annual cost) every quarter, so card pricing clusters just under
that ceiling. **Second, the state taxes the borrowing itself** via *imposto do selo* (stamp
duty) on the credit used and on the interest — a cost layer that doesn't exist in many
countries and makes carrying a balance dearer than the headline rate suggests. **Third,
Portuguese habits differ:** people lean on debit and on cards used in *pay-in-full* mode, so a
lot of "credit cards" here behave like charge cards, with revolving borrowing comparatively
less common than in the US or UK.

On top of that sits a distinctly Portuguese rail (**SIBS / Multibanco / MB WAY**) that every
issuer must plug into, and a consumer-protection regime (**PARI/PERSI**) that reshapes what
happens when someone falls behind.

---

## 1. Portuguese vocabulary

| Term | Meaning |
|---|---|
| *Cartão de débito* | Debit card (own funds, instant) |
| *Cartão de crédito* | Credit card (revolving line) |
| *Cartão de crédito de pagamento diferido* | Deferred-debit card — pay in full at cycle end, normally interest-free (a charge-card behaviour) |
| *Pagamento em prestações* | Installment plan run on the card |
| **TAN** (*taxa anual nominal*) | Nominal annual interest rate |
| **TAEG** (*taxa anual de encargos efetiva global*) | All-in effective annual cost (the PT/EU APR) — includes interest + fees |
| **MTIC** (*montante total imputado ao consumidor*) | Total cost of the credit to the consumer |
| *Anuidade* | Annual card fee |
| *Comissões* | Fees |

A subtlety that matters downstream: a single physical *cartão de crédito* is often **used in
deferred-debit mode** (pay the full *extrato*/statement each month, no interest) — so the
*product* is a credit card but the *behaviour* is charge-card-like.

---

## 2. Regulatory framework

**Supervisor.** *Banco de Portugal* (BdP) supervises retail-banking conduct and prudential
matters, including consumer credit.

**Consumer-credit regime — Decreto-Lei n.º 133/2009** (transposing the EU Consumer Credit
Directive 2008/48/EC). For credit cards with a revolving facility it requires:
- a mandatory **avaliação de solvabilidade** (creditworthiness assessment);
- delivery of the **FIN** (*Ficha de Informação Normalizada* — the standardised
  pre-contractual information, the PT form of the EU SECCI);
- a **14-day right of free withdrawal** (*livre revogação*);
- the right of **early repayment** (*reembolso antecipado*).
- *Note:* pure pay-in-full deferred-debit cards may fall partly outside this regime depending
  on terms. `[REFRESH]` the exact scope boundary.

**Upcoming — CCD2 (Directive (EU) 2023/2225)** repeals 2008/48 and widens scope (small loans,
BNPL) with tighter creditworthiness rules; national transposition/application is landing
around **2025–2026**. `[REFRESH]` the exact PT transposition status and dates — this is a live
moving target.

**Maximum-rate (usury) regime — the defining PT feature.** Under DL 133/2009 (art. 28), BdP
publishes **quarterly maximum TAEGs by credit category**, including the bucket *"cartões de
crédito, linhas de crédito, contas correntes bancárias e facilidades de descoberto"* and
*"crédito revolving."* The mechanism (verify the exact multipliers): a category's maximum
TAEG may not exceed the **previous quarter's average TAEG for that category by more than ~25%
(×1.25)**, and may not exceed the **overall consumer-credit average by more than ~50%
(×1.5)** — the binding published cap is the lower result. Consequence: **card pricing
converges toward the cap**, and credit-card/revolving buckets sit among the highest-TAEG
categories. `[REFRESH]` the current quarter's published maximum TAEGs and confirm the
multipliers.

**Imposto do Selo (stamp duty) — a cost layer absent in many markets.** Applies both to the
**credit used** and to **interest/fees**, under the *Tabela Geral do Imposto do Selo*:
- **Verba 17.2 — credit utilization:** for revolving/*conta corrente* credit of undetermined
  term, a monthly rate (historically ~**0.128%/month** on the average monthly debit balance).
  `[REFRESH]` current rate.
- **Consumer-credit surcharge (*agravamento*):** a **50% increase** on consumer-credit stamp
  duty has applied in recent years — pushing the effective utilization rate higher.
  `[REFRESH]` whether still in force and the resulting effective rate.
- **Verba 17.3 — interest & fees:** **~4%** stamp duty on interest and on bank commissions.
  `[REFRESH]`.
- Net effect: **the true cost of revolving exceeds the headline TAN/TAEG once stamp duty is
  layered in** — an important analysis point.

**EU Interchange Fee Regulation ((EU) 2015/751).** Caps consumer-card interchange at
**0.3% (credit) / 0.2% (debit)**; applies in Portugal, compressing issuer interchange income.

**Central de Responsabilidades de Crédito (CRC).** BdP's **central credit register** — banks
must report exposures and **consult it during the solvency assessment**. It is the closest PT
analogue to a credit bureau (public/central, not private).

**Default-handling regimes (very PT-specific) — Decreto-Lei n.º 227/2012:**
- **PARI** (*Plano de Ação para o Risco de Incumprimento*) — a pre-default action plan banks
  must operate when a customer shows risk of missing payments.
- **PERSI** (*Procedimento Extrajudicial de Regularização de Situações de Incumprimento*) —
  a mandatory **out-of-court resolution procedure** the bank must run before pursuing legal
  action. This **inserts extra steps into the delinquency lifecycle** of Brief 01 §6B.

**Other:** PSD2 / **strong customer authentication** (SCA), **RGPD/GDPR**, BdP rules on
**comissões** transparency (published *preçário*; certain fees prohibited), and the *serviços
mínimos bancários* basic-account regime (debit-only, tangential to cards).

---

## 3. Domestic infrastructure

- **SIBS** (*Sociedade Interbancária de Serviços*) — operates **Multibanco** (an unusually
  integrated national ATM + POS network), **MB WAY** (mobile P2P + payments + virtual cards),
  and **MB NET** (virtual card numbers); SIBS also provides **card processing/issuing
  services** to many banks.
- **Multibanco** functions as a **domestic scheme**: PT credit cards are typically co-badged
  Visa/Mastercard for international use while domestic transactions route via Multibanco/SIBS.
- **Unicre** — an *Instituição Financeira de Crédito*; historically the **UNIBANCO** card
  issuer, today a major **acquirer (REDUNIQ)** and co-brand **issuer** (notably the *Universo*
  card with Sonae). Detailed in Brief 03.

---

## 4. Market characteristics — "why Portugal is different"

- **Debit-first, deferred-debit-heavy.** Multibanco ubiquity and a pay-in-full culture mean
  many credit cards behave like charge cards; **revolving share is comparatively low**.
  `[REFRESH]` revolving-vs-deferred split — *the single most decision-relevant statistic.*
- **Strong installment & retail-loyalty culture.** *Pagamento em prestações* and retail
  co-brands (Continente/**Universo**) are central to how credit is consumed.
- **Very high contactless and MB WAY adoption.**
- **BNPL rising** (Cofidis, Oney, Klarna) — encroaching on the installment use case.
- `[REFRESH]` card counts, penetration, revolving balances, average TAEG (BdP / SIBS data).

---

## 5. PT-adapted lifecycle (delta vs. Brief 01 §6B)

Application → **KYC/AML** (Lei 83/2017) → **mandatory *avaliação de solvabilidade*** with
**CRC consultation** + income evidence → **FIN** delivered pre-contract → decision + limit →
issuance (often via SIBS processing) → activation → usage (Multibanco / Visa·MC / **MB WAY**)
→ monthly *extrato* → repayment (**full = deferred-debit mode**, or **minimum = revolving at
TAEG**) → servicing → **on default: PARI → PERSI** *before* any legal action or CRC default
marking → charge-off / recovery.

The bolded steps — solvency assessment + CRC, FIN, and the PARI/PERSI gateway — are the
Portugal-specific insertions into the generic lifecycle.

---

## Figures to verify `[REFRESH]`

1. **Maximum TAEG caps** — current quarter, per category; confirm the ×1.25 / ×1.5 multipliers.
2. **Imposto do selo** — Verba 17.2 monthly utilization rate, the 50% consumer surcharge
   status, Verba 17.3 ~4% on interest/fees; the resulting *effective* cost.
3. **Revolving-vs-deferred-debit split** and overall card penetration (BdP/SIBS).
4. **CCD2 transposition** status and PT application dates.
5. Exact **DL 133/2009 scope boundary** for pay-in-full deferred-debit cards.

---

*Previous:* [← 01 — Fundamentals](./01-fundamentals.md) ·
*Next:* [03 — Competitive landscape in Portugal →](./03-competitive-landscape-pt.md)
