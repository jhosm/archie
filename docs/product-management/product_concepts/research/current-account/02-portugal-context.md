# Brief 02 — Conta à Ordem in the Portuguese Context

> Part 2 of 3. Re-draws the universal picture from [01 — fundamentals](./01-fundamentals.md)
> with Portugal's terminology, regulation, taxes, and market habits. Feeds the provider
> comparison in [03 — competitive landscape](./03-competitive-landscape-pt.md).
> The PT regime the *descoberto* (overdraft) **shares with credit cards and crédito pessoal**
> (DL 133/2009, the quarterly maximum-TAEG mechanism, imposto do selo, CRC, PARI/PERSI) is
> cross-referenced to those briefs rather than repeated; this brief focuses on **what is
> specific to the demand account** — and on its two defining Portuguese/EU consumer-protection
> layers, the *serviços mínimos bancários* and the Payment Accounts Directive transposition.
> **Every perishable figure is tagged `[REFRESH]`** — verify before relying on a number.

## In plain English

A Portuguese *conta à ordem* (a current / demand account) works like the generic transactional
account in Brief 01 — money available on demand, two balances tracked (what has posted versus
what you can actually spend), a debit card and direct debits hanging off it. But three local
forces make the Portuguese account distinctive in ways its three sibling products are not.

**First, the law guarantees a basic account at a capped cost.** The *serviços mínimos bancários*
regime gives anyone the right to a current account with a debit card, transfers, and direct
debits for a small, regulated annual fee — a price floor that simply does not exist for a credit
card or a personal loan. **Second, the law forces fee transparency and easy switching.** Banks
must hand you a standardised fee document up front, send you an annual summary of every fee you
paid, and run a free service that moves your account (and all its direct debits) from one bank to
another inside a fixed legal deadline. **Third, when the account can go negative, that negative is
regulated borrowing.** An arranged overdraft (*descoberto autorizado*) is consumer credit, so it
inherits the same rate cap, stamp tax, and default-protection machinery as a credit card — and it
literally shares the credit card's published rate-cap bucket.

On top of that sit a distinctly Portuguese payment rail (**SIBS / Multibanco / MB WAY**) that the
account lives behind, and a few endpoints with no clean foreign analogue — a balance can be
*frozen by a court* (*penhora*), and a long-forgotten account eventually escheats to the State.

---

## 1. Portuguese vocabulary

| Term | Meaning |
|---|---|
| *Conta à ordem* / *conta de depósito à ordem (DO)* | Current / demand-deposit account — funds available on demand, no maturity |
| *Depósito à ordem* vs *depósito a prazo* | Demand deposit vs **term deposit** (the locked, maturity-bound product → the [term_deposit](../../02-v1-scope-term-deposits.md) family; the conta à ordem is its liquid opposite) |
| *Saldo contabilístico* | **Accounting / ledger balance** — what has actually posted |
| *Saldo disponível* | **Available balance** — what is spendable right now (accounting balance net of holds, plus any authorized-overdraft headroom) |
| *Saldo cativo* / *cativos* / *cativação* | **Held / earmarked** amount — funds blocked but not yet settled; *cativação* is the act of placing the block. (Note: the word also covers a **legal freeze** — see *penhora*, §2.) |
| *Descoberto* | Overdraft — the account going below zero |
| *Descoberto autorizado* / *facilidade de descoberto* | **Arranged overdraft** — a pre-agreed credit line on the account |
| *Ultrapassagem de crédito* / *descoberto não autorizado* | **Unarranged overdraft** — going below zero (or beyond the limit) without prior agreement |
| *Comissão de manutenção de conta* | Account-maintenance fee — the recurring fee for holding the account |
| *Autorização de débito direto* / *domiciliação* | Direct-debit mandate (SEPA Direct Debit) — a pull authorization; *domiciliação* is the everyday word for a bill set to debit automatically |
| *Transferência* (SEPA) / *MB WAY* | Credit transfer (push) over SEPA; **MB WAY** for instant mobile P2P/payments |
| *Domiciliação de ordenado* | **Salary domiciliation** — having your salary paid into the account; the usual fee-waiver lever |
| *IBAN (PT50…)* / *NIB* | The account identifier — the Portuguese **IBAN** begins `PT50`; the older 21-digit **NIB** is the domestic predecessor now embedded in the IBAN |

A subtlety that recurs downstream: ***saldo cativo* means two different things** — an
authorization **earmark** (a product mechanic) and a court-ordered **freeze** (*penhora*, a legal
act). Brief 01 separates these generically — its §2 draws the two balances and its §8 names the
two-meanings-of-hold (earmark vs legal freeze); §2 below names the PT legal form. *How the engine
models these two as distinct facts is a babelstone boundary question, decided in the family ADR,
not here* (it is the [ADR-PC-030](../../adrs/ADR-PC-030-product-scope-and-boundary.md) hold
lifecycle vs a legal freeze).

---

## 2. Regulatory framework

**Supervisor.** *Banco de Portugal* (BdP) supervises retail-banking conduct; deposit-taking sits
under the **RGICSF** (*Regime Geral das Instituições de Crédito e Sociedades Financeiras*). The
shared consumer-credit spine that touches the *descoberto* (DL 133/2009, CRC, PARI/PERSI) is
cross-referenced to the sibling briefs rather than re-derived.

**★ Serviços Mínimos Bancários (SMB) — the defining PT/EU consumer feature.** A regulated **basic
bank account** — a conta à ordem bundled with a **debit card**, **SEPA transfers**, and **direct
debits** — available **as of right** to anyone without another account, at a **capped annual
cost**. The cap is **reported to be pegged to a fraction of the IAS** (*Indexante dos Apoios
Sociais*), so on that reading it moves with the index rather than being a fixed euro figure — but
the **basis itself** (an IAS peg vs a reshaped fixed figure) has drifted across amendments, so
treat the *mechanism*, not just the number, as uncertain. The originating regime is **DL 27-C/2000**
as amended (the SMB provisions were substantially reshaped by later decree-laws — **DL 107/2017**
and **DL 7/2020** are the ones usually cited). `[REFRESH]` the **current cost cap**, the **basis
and exact fraction** of any IAS peg, and the originating/amending decree-law numbers — this is a
market-shaping price floor with **no analogue in the card or loan briefs**, so getting the figure
right matters.

**★ Payment Accounts Directive (PAD, 2014/92/EU) — the other defining layer,** every part of it
account-specific. The transposing decree-law `[REFRESH]` (DL 107/2017 is the value cited; confirm
it is the PAD instrument):

- **Fee comparability.** Pre-contract, the customer receives the **FID** (*Documento de
  Informação sobre Comissões* — the Fee Information Document), and annually the **Extrato de
  Comissões** (Statement of Fees), both using **EU-standardised fee terminology** so accounts are
  comparable across banks; BdP runs a **fee-comparison portal** (*comparador de comissões*).
- **Account-switching service** (*serviço de mudança de conta*). A bank-to-bank assisted switch
  that moves the account and re-points its direct debits / standing orders, which the receiving
  and transferring banks must complete **within a legally fixed window** (the order of **~12
  business days** domestically is the figure usually quoted). `[REFRESH]` the exact window.
- **Right to a basic payment account.** Dovetails with the SMB regime above — the entitlement to
  a no-frills account at capped cost.

**Comissões regime + contas-pacote.** BdP regulates fee conduct and publishes each institution's
***preçário*** (price list); the **comissão de manutenção de conta** (account-maintenance fee)
and bundled ***contas-pacote*** (package accounts bundling card + insurance + a transfer/transaction
quota for one monthly fee) are a **contentious, politically charged consumer topic**. Certain fees
are **prohibited or capped** by BdP rules. `[REFRESH]` the current set of prohibited/capped fees
and any statutory limits on the maintenance fee.

**Descoberto as consumer credit — DL 133/2009.** Both a *facilidade de descoberto* (arranged
overdraft) and an *ultrapassagem* (unarranged) are **regulated consumer credit**: they require an
**avaliação de solvabilidade** (creditworthiness assessment), delivery of the **FIN** (*Ficha de
Informação Normalizada* — the PT form of the EU SECCI), and — critically — they fall under the
**maximum-TAEG cap**. The BdP quarterly cap bucket *"cartões de crédito, linhas de crédito, contas
correntes bancárias e **facilidades de descoberto**"* is the **same bucket as the credit card** →
reference [`../credit-cards/02-portugal-context.md` §2](../credit-cards/02-portugal-context.md);
the **×1.25 / ×1.5 multiplier mechanism is the same** one the sibling briefs describe (a category's
maximum TAEG may not exceed the previous quarter's category average by more than ~25%, nor the
overall consumer-credit average by more than ~50%, with the lower result binding). `[REFRESH]` the
**current-quarter** published maximum TAEG for that bucket and **verify the exact multipliers** and
the exact label. Confirm `[REFRESH]` the DL number (**133/2009** is the value cited).

**Imposto do selo (stamp duty) on the *descoberto*.** Like a credit card's revolving balance — and
**unlike** a fixed-term *crédito pessoal* — an arranged overdraft is *undetermined-term* revolving
credit, so it attracts a **recurring monthly utilization duty** (a credit-use duty under the
**Verba 17.x** family, on the average monthly debit balance), **contrasted with** the **one-off**
principal duty a *crédito pessoal* pays — reference
[`../personal-loan/02-portugal-context.md` §2](../personal-loan/02-portugal-context.md)
for that contrast, and [`../credit-cards/02-portugal-context.md` §2](../credit-cards/02-portugal-context.md)
for the revolving rate and the consumer-credit *agravamento* (surcharge). On top of the
utilization duty there is a duty on **interest and commissions** (the card brief cites ~4% under
Verba 17.3) `[REFRESH]`. The verba scheme is **not yet reconciled across the sibling briefs**:
`[REFRESH]` the exact verba number and monthly rate (the card brief cites ~0.128%/month and "Verba
17.2"; the loan brief argues it is Verba 17.1.4 — reconcile against the live *Tabela Geral do
Imposto do Selo*), the surcharge status, and the ~4% Verba 17.3 figure. As in the siblings, **selo
is a tax and sits outside the TAEG**, so the true cost of running an overdraft exceeds what the
TAEG implies.

**Fundo de Garantia de Depósitos (FGD).** Demand deposits are covered up to **€100,000 `[REFRESH]`
per depositor per institution**. `[REFRESH]` the ceiling and the scope (per-depositor /
per-institution aggregation, temporary high-balance exceptions). The conta à ordem is exactly the
kind of balance this protects — the card and loan briefs have no equivalent, since neither holds
the customer's money.

**Dormancy / contas inativas / *saldos prescritos*.** A long-inactive account becomes a *conta
inativa*, and after a statutory period the balance is **prescribed (*saldos prescritos*) and
reverts to the State** — a notably PT-specific lifecycle endpoint with no card/loan analogue. The
inactivity period before prescription is often quoted around **15 years** of no movement, but this
is the figure most easily asserted wrongly. `[REFRESH]` the inactivity period, the prescription
mechanism, and the receiving State entity.

**PSD2 — DL 91/2018.** Imposes **strong customer authentication (SCA)** on electronic payments and
opens the account to **open banking** — licensed third parties acting as **AISP** (account
information) and **PISP** (payment initiation) can, with consent, read the account and initiate
payments from it. `[REFRESH]` the transposing decree-law number (**91/2018** is the value cited).

**Penhora de saldos (garnishment) — the legal *cativo*.** A court or the **AT** (tax authority)
can order a balance **frozen / seized** to satisfy a debt. This is the **legal meaning of
*cativo*** — a *penhora* / *cativação por ordem judicial* — and is **distinct from an authorization
earmark**: an earmark is a product mechanic that resolves on capture or expiry; a *penhora* is a
legal encumbrance lifted only by the issuing authority. Both reduce the *saldo disponível*, but
they are different facts. (Brief 01 §8 flags the generic two-meanings-of-hold; this is its PT legal
form.)

**CRC + PARI / PERSI — DL 227/2012, on *descoberto* default.** A persistent unauthorized overdraft
is a credit default, so it engages the **Central de Responsabilidades de Crédito (CRC)** (BdP's
central credit register, consulted in the solvency assessment) and the **PARI / PERSI** procedures
the bank must run before legal action — identical machinery to the [card](../credit-cards/02-portugal-context.md) §2
and [loan](../personal-loan/02-portugal-context.md) §2 briefs; here it bites on the
unpaid-*descoberto* path. `[REFRESH]` the DL number (**227/2012** is the value cited).

---

## 3. Domestic infrastructure

- **SIBS / Multibanco / MB WAY — the rail the account lives behind.** The conta à ordem is the
  account *behind* the **debit card** and **MB WAY**; **SIBS** operates **Multibanco** (the
  unusually integrated national ATM + POS network) and **MB WAY** (mobile P2P, payments, instant
  transfers). The domestic rail is table stakes for any account offered in Portugal (reference the
  [card brief](../credit-cards/02-portugal-context.md) §3 for the SIBS/Multibanco detail).
- **SEPA / IBAN (PT50).** Credit transfers (**transferências**), **débitos diretos SEPA**
  (*domiciliações*), and standing orders all ride SEPA off the **PT50** IBAN; **MB WAY** layers
  instant P2P on top. *Where a ledger engine's responsibility starts and stops relative to these
  rails is a babelstone boundary question, decided in
  [ADR-PC-030](../../adrs/ADR-PC-030-product-scope-and-boundary.md) (the engine decides and records;
  the rails physically move) — not asserted here.*

---

## 4. Market characteristics — "why Portugal is different"

- **Near-universal ownership; the relationship anchor.** Almost every adult holds a conta à ordem,
  and it is the **anchor the bank cross-sells from** — the term deposit, the loan, and the card all
  settle against it (the [ADR-PC-030](../../adrs/ADR-PC-030-product-scope-and-boundary.md)
  "*hub the others settle against*" role, described here at the market level only). `[REFRESH]`
  account penetration.
- **Maintenance fees rose and turned political → SMB floor + switching valve + contas-pacote.**
  Account-maintenance fees climbed over the past decade and became a charged public issue; the
  **SMB** acts as the regulated **floor**, the **switching service** as the **pressure valve**, and
  **contas-pacote** bundling as the banks' packaging response. `[REFRESH]` average maintenance fees
  and SMB uptake.
- **Domiciliação de ordenado as the fee-waiver lever.** The standard way to get the maintenance fee
  (and often a package's conditions) waived is to **domicile your salary** in the account — the
  single most common waiver condition.
- **Strong débito direto / domiciliação culture.** Utilities, telecoms, and recurring bills are
  overwhelmingly paid by **direct debit**, and **MB WAY** adoption is high — both deepen the
  account's role as the daily-money hub.
- **Neobanks as widely-held secondary accounts.** **Revolut** and **N26** are commonly held
  **alongside** a main domestic account, used for FX, travel, and UX — secondary, not primary.
  Whether each offers a **true PT IBAN bank account** under the **FGD** or an **e-money/foreign-DGS**
  product is the easiest thing to assert wrongly. `[REFRESH]` the neobank-as-secondary share and the
  per-provider IBAN-country / deposit-guarantee distinction (developed in Brief 03).

---

## 5. PT-adapted lifecycle (delta vs. Brief 01 §7)

Opening under PT **KYC/AML** (Lei 83/2017 — **NIF**, *comprovativo de morada*) → **IBAN (PT50)
issued** → **SMB option offered** (the basic-account entitlement) → active, with *débitos diretos* /
*domiciliações* set up and (typically) **salary domiciliated** to waive the maintenance fee →
optional ***descoberto autorizado*** governed by **DL 133/2009** (solvency + FIN + TAEG cap) →
on overdraft default, **PARI → PERSI** before legal action / CRC default marking → **dormancy
(*conta inativa*) → prescription / escheat to the State (*saldos prescritos*)**; on the holder's
death, **succession is handled upstream** — a court/notary decides who inherits and the payout is
made upstream/legacy, not by the engine (reference
[ADR-PC-030 §Decision "Succession is upstream-decided"](../../adrs/ADR-PC-030-product-scope-and-boundary.md)).

The bolded steps — the **SMB entitlement**, the **switching service** (§2), the
**DL-133/2009-governed *descoberto*** with its shared cap and **PARI/PERSI** gateway, the
**penhora / legal *cativo***, and the **dormancy → prescription** endpoint — are the
Portugal-specific insertions into the generic demand-account lifecycle.

---

## Figures to verify `[REFRESH]`

1. **Serviços Mínimos Bancários** — the current **cost cap**, the **basis** of any IAS peg
   (an IAS peg vs a reshaped fixed figure) and the exact **IAS fraction**; the
   originating/amending decree-law numbers (**DL 27-C/2000**, **DL 107/2017**, **DL 7/2020**).
2. **PAD transposition** — confirm **DL 107/2017** is the PAD instrument; the **account-switching
   window** (~12 business days quoted); FID / Extrato de Comissões specifics.
3. **Maintenance-fee rules & averages** — prohibited/capped fees, any statutory limit, and average
   *comissão de manutenção* (politically noisy data → hard `[REFRESH]`).
4. **Descoberto maximum-TAEG cap** — current quarter for the *cartões / linhas / contas correntes /
   facilidades de descoberto* bucket; verify the ×1.25 / ×1.5 multipliers, the exact label, and
   **DL 133/2009**.
5. **Imposto do selo on the *descoberto*** — the verba number and monthly utilization rate
   (reconcile the card brief's ~0.128% / "17.2" against the loan brief's "17.1.4"), the surcharge
   status, and Verba 17.3 ~4% on interest/fees; that selo sits **outside** the TAEG.
6. **FGD** — the **€100,000** per-depositor/per-institution ceiling and scope.
7. **Dormancy / prescription** — the inactivity period (~15 years quoted), the prescription
   mechanism, and the receiving State entity.
8. **PSD2 / open banking** — confirm **DL 91/2018**.
9. **PARI / PERSI / CRC** — confirm **DL 227/2012**.
10. **Market** — account penetration, SMB uptake, average maintenance fee, switching-service
    volumes, and the neobank-as-secondary-account share (with per-provider IBAN-country / FGD vs
    foreign-DGS / e-money distinction).

---

*Previous:* [← 01 — Fundamentals](./01-fundamentals.md) ·
*Next:* [03 — Competitive landscape in Portugal →](./03-competitive-landscape-pt.md)
