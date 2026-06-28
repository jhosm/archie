# Brief 03 — Competitive Landscape in Portugal

> Part 3 of 3. Surveys who offers a *conta à ordem* (current / demand account) in Portugal and
> how they compare, using the taxonomy from [01 — fundamentals](./01-fundamentals.md) seen through
> the PT lens of [02 — Portugal context](./02-portugal-context.md).
> **All pricing, fees, waiver thresholds, and market-share numbers are `[REFRESH]`** — this brief
> captures *who the players are, what they offer, and how they position*, not confirmed current
> figures. Treat every quantitative cell as "verify before use." Each provider is flagged for
> whether it genuinely offers a **Portuguese current account / PT IBAN** with **FGD** cover, vs an
> **e-money / foreign-IBAN** product — the single easiest thing to assert wrongly.

## In plain English

Almost everyone in Portugal has a current account, so the fight is not "do you want one" but
"whose, and at what monthly cost." Three kinds of provider compete. The **big universal banks**
(Caixa Geral de Depósitos, Millennium BCP, Santander Totta, Novo Banco, BPI…) sell the account as
the anchor of a whole relationship — salary in, bills out, and every other product cross-sold off
it; they mostly charge a **monthly maintenance fee** that you can dodge by routing your salary to
them. Their own **digital arms** (ActivoBank, Moey, Openbank) and the **low-fee postal bank**
(Banco CTT) compete by waiving or shrinking that fee. Then the **neobanks** (Revolut, N26, Wise)
win on app, FX, and "free" — but here lies the crucial catch this brief keeps flagging: several of
them are **e-money / payment institutions on a foreign IBAN**, not Portuguese deposit-taking banks,
so your money may sit under **e-money safeguarding or a foreign guarantee scheme** rather than the
Portuguese **Fundo de Garantia de Depósitos**. That distinction — true bank account vs e-money — is
a hard `[REFRESH]` per provider and must never be asserted from memory.

Two market truths from Brief 02 shape everything: the **maintenance fee is the competitive
battleground** (and salary domiciliation is the usual waiver lever), and underneath every
incumbent sits the **regulated floor** — *serviços mínimos bancários* (SMB), a capped basic
account everyone must offer. Because fees and package names churn and the *descoberto* cap moves
quarterly, this brief maps the **structure**; the figures are the refresh pass.

---

## 1. Market structure & sizing

`[REFRESH]` — populate from Banco de Portugal (retail-banking conduct reports, the *comparador de
comissões*, the *serviços mínimos bancários* pages) and SIBS statistics:

- **Account penetration** — near-universal adult account ownership; `[REFRESH]` the exact figure.
- ***Contas-pacote* vs standalone share** — how much of the market sits inside bundled monthly-fee
  packages vs unbundled accounts. `[REFRESH]`.
- **SMB uptake** — number of *serviços mínimos bancários* accounts open; historically low relative
  to eligibility. `[REFRESH]`.
- **Average maintenance fee** (*comissão de manutenção*) — noisy and politically charged; varies by
  source. Hard `[REFRESH]`.
- **Account-switching volumes** — *serviço de mudança de conta* usage; a proxy for real mobility.
  `[REFRESH]`.
- **Neobank secondary-account penetration** — Revolut/N26 widely held as *second* accounts, FX/UX
  driven. `[REFRESH]`.

---

## 2. Player taxonomy

*(✔ = offers a true PT current account / PT50 IBAN under FGD; ⚠ = confirm IBAN country +
guarantee scheme — may be e-money / foreign IBAN — hard `[REFRESH]`.)*

**Universal / incumbent banks** *(deposit-taking under RGICSF; FGD-covered; full product range)*
- **Caixa Geral de Depósitos (CGD)** ✔ — state-owned, largest reach; *Caixadirecta* app.
- **Millennium BCP** ✔ — largest private bank.
- **Santander Totta** ✔ — Santander group.
- **Novo Banco** ✔ — successor to BES.
- **BPI** ✔ — CaixaBank group.
- **Bankinter** ✔ — digitally aggressive incumbent.
- **Crédito Agrícola** ✔ — mutual/cooperative network, strong regional reach.
- **ABANCA** ✔ — *(incl. the former **EuroBic**, absorbed into ABANCA — `[REFRESH]` confirm EuroBic
  no longer stands alone as a brand/IBAN).*
- **Montepio** ✔ — mutualist-rooted incumbent.

**Digital arms of incumbents** *(PT bank licence behind them; FGD-covered — `[REFRESH]` confirm the
booking entity / IBAN per arm)*
- **ActivoBank** (BCP) ✔ — low-fee, younger-skewing digital bank; the reference low/zero-maintenance
  PT account. `[REFRESH]` whether on the BCP licence/IBAN.
- **Moey** (Crédito Agrícola) ⚠ — digital app proposition; `[REFRESH]` whether it issues a true
  current account / PT IBAN vs a card/wallet, and under which licence.
- **Openbank** (Santander) ⚠ — Santander-group digital bank; `[REFRESH]` whether the PT-marketed
  account is a Spanish (Santander) IBAN vs a PT50 IBAN, and the guarantee scheme that follows.

**Low-fee / postal**
- **Banco CTT** ✔ — postal-network bank positioned on **low or no maintenance fee**; a structural
  price-pressure player. `[REFRESH]` current account terms.

**Neobanks / e-money** *(the deposit-guarantee / IBAN-country caveat is decisive here)*
- **Revolut** ⚠ — very high PT adoption as a secondary account. Operates in the EEA via **Revolut
  Bank UAB (Lithuania)** — i.e. a **Lithuanian IBAN under the Lithuanian DGS**, *not* the PT FGD,
  unless/until local-IBAN issuance applies. `[REFRESH]` the current IBAN country, the entity, and
  whether PT IBANs are issued.
- **N26** ⚠ — German bank (**N26 Bank AG**); PT customers typically hold a **German IBAN under the
  German deposit-guarantee scheme**, not the PT FGD. `[REFRESH]` IBAN country + scheme.
- **Wise** ⚠ — **e-money / payment institution**, *not* a deposit-taking bank: balances are
  **safeguarded**, not deposit-guaranteed; multi-currency account details rather than a single PT
  current account. `[REFRESH]` legal status + safeguarding arrangement. **Do not describe Wise as a
  bank account.**

**The regulated floor (not a provider)**
- **Serviços Mínimos Bancários (SMB)** — a *capped* basic current account (account + debit card +
  transfers + direct debits) that the **incumbent set must offer as of right**, not a competitor. It
  is the price floor every player above is measured against (Brief 02 §2). Cost cap pegged to a
  fraction of the **IAS**; `[REFRESH]` the current cap.

> The *true bank account vs e-money* line is **described here, not designed** — whether babelstone
> treats a given provider's balance as a guaranteed deposit or an e-money reference is a boundary
> question decided in
> [ADR-PC-030](../../adrs/ADR-PC-030-product-scope-and-boundary.md), not in this brief; it also sits
> behind ADR-PC-030's *decide-and-record vs physically-move* boundary (the ADR excludes the payment
> rails / scheme / clearing / settlement). Separately,
> [ADR-PC-016](../../adrs/ADR-PC-016-legacy-current-account-adapter.md) supports only the narrower
> fact that the legacy *conta à ordem* is the v4 destination (the point at which the current
> account moves onto the engine) — not the deposit-guarantee-vs-e-money distinction itself.

---

## 3. Comparison matrix (flagship account/package × provider)

Dimensions derive from Brief 01 §6 (variant axes) and Brief 02. **Numeric cells are `[REFRESH]`** —
the matrix records *structure and known positioning*; **do not read blanks as zero.** The
*descoberto* TAN/TAEG sits under the **shared BdP quarterly cap bucket** with credit cards (Brief
02 §2 → [`../credit-cards/02-portugal-context.md`](../credit-cards/02-portugal-context.md)).

| Provider (example account) | Maintenance fee (*comissão de manutenção*) | Fee-waiver conditions (salary / min. balance / age) | Debit card | MB WAY / Apple·Google Pay | Descoberto autorizado (TAN/TAEG) | Interest on balance | Multi-currency | SMB offered | Switching service | Digital / app | Positioning |
|---|---|---|---|---|---|---|---|---|---|---|---|
| **CGD** (*Conta Caixa* package) | `[REFRESH]` | salary domiciliation `[REFRESH]` | yes | likely all | available, capped bucket `[REFRESH]` | ~0 `[REFRESH]` | no | yes (mandated) | yes (mandated) | *Caixadirecta* | Broadest reach, state bank |
| **Millennium BCP** (*Conta M*/pacote) | `[REFRESH]` | salary / bundle `[REFRESH]` | yes | likely all | available `[REFRESH]` | ~0 `[REFRESH]` | no | yes | yes | strong app | Largest private, full range |
| **Santander Totta** (*Conta Mundo*/pacote) | `[REFRESH]` | salary / min. balance `[REFRESH]` | yes | likely all | available `[REFRESH]` | ~0 `[REFRESH]` | no | yes | yes | yes | Group scale, package push |
| **Novo Banco** (*Conta* pacote) | `[REFRESH]` | salary `[REFRESH]` | yes | likely all | available `[REFRESH]` | ~0 `[REFRESH]` | no | yes | yes | yes | Recovering incumbent |
| **BPI** (*Conta Valor*/pacote) | `[REFRESH]` | salary / min. balance `[REFRESH]` | yes | likely all | available `[REFRESH]` | ~0 `[REFRESH]` | no | yes | yes | *BPI App* | CaixaBank-backed |
| **Bankinter** (current account) | `[REFRESH]` | salary `[REFRESH]` | yes | likely all | available `[REFRESH]` | sometimes promo `[REFRESH]` | no | yes | yes | online-strong | Digitally aggressive incumbent |
| **Crédito Agrícola** (*conta* pacote) | `[REFRESH]` | membership / salary `[REFRESH]` | yes | likely all | available `[REFRESH]` | ~0 `[REFRESH]` | no | yes | yes | app | Mutual/regional reach |
| **ABANCA** (incl. former EuroBic) | `[REFRESH]` | salary `[REFRESH]` | yes | `[REFRESH]` | available `[REFRESH]` | ~0 `[REFRESH]` | no | yes | yes | app | Iberian incumbent; EuroBic absorbed `[REFRESH]` |
| **Montepio** (*conta* pacote) | `[REFRESH]` | salary `[REFRESH]` | yes | `[REFRESH]` | available `[REFRESH]` | ~0 `[REFRESH]` | no | yes | yes | app | Mutualist-rooted incumbent |
| **ActivoBank** (BCP) | low / often €0 `[REFRESH]` | often **no fee / age-based** `[REFRESH]` | yes | yes | `[REFRESH]` | ~0 `[REFRESH]` | no | `[REFRESH]` | yes | **app-first** | Digital, low-fee, younger |
| **Moey** (Crédito Agrícola) ⚠ | `[REFRESH]` | `[REFRESH]` | `[REFRESH]` | `[REFRESH]` | `[REFRESH]` | `[REFRESH]` | no | `[REFRESH]` | `[REFRESH]` | app-native | Confirm true PT account / IBAN |
| **Openbank** (Santander) ⚠ | `[REFRESH]` | `[REFRESH]` | yes | `[REFRESH]` | `[REFRESH]` | promo `[REFRESH]` | `[REFRESH]` | `[REFRESH]` | `[REFRESH]` | online | Confirm PT50 vs ES IBAN + scheme |
| **Banco CTT** (*Conta* CTT) | low / often €0 `[REFRESH]` | low-/no-fee positioning `[REFRESH]` | yes | yes `[REFRESH]` | `[REFRESH]` | ~0 `[REFRESH]` | no | yes `[REFRESH]` | yes | app + post offices | Low-fee postal disruptor |
| **Revolut** ⚠ | tiered plans `[REFRESH]` | n/a (plan-based) | yes (debit) | Apple/Google Pay; **MB WAY** `[REFRESH]` | `[REFRESH]` | vaults/promo `[REFRESH]` | **yes (native)** | n/a | n/a | app-native | **LT IBAN / LT DGS, not FGD** `[REFRESH]` |
| **N26** ⚠ | tiered plans `[REFRESH]` | n/a (plan-based) | yes (debit) | Apple/Google Pay; **MB WAY** `[REFRESH]` | `[REFRESH]` | promo `[REFRESH]` | partial `[REFRESH]` | n/a | n/a | app-native | **DE IBAN / German DGS, not FGD** `[REFRESH]` |
| **Wise** ⚠ | per-use fees `[REFRESH]` | n/a | yes (debit) | Apple/Google Pay `[REFRESH]` | n/a (no overdraft) | n/a `[REFRESH]` | **yes (multi-currency)** | n/a | n/a | app-native | **E-money: safeguarded, not deposit-guaranteed** `[REFRESH]` |
| **SMB (regulated floor)** | **capped** (IAS-pegged) `[REFRESH]` | as of right (eligibility rules) `[REFRESH]` | yes | `[REFRESH]` | typically excluded `[REFRESH]` | ~0 | no | — (is the floor) | yes | varies by host bank | Statutory basic account |

> Reading the matrix: **incumbents** cluster as *package accounts with a maintenance fee waived by
> salary domiciliation*; **digital arms + Banco CTT** compete by **driving that fee toward zero**;
> **neobanks** win on *FX, multi-currency, and app*, but the ⚠ rows turn on the **IBAN country +
> guarantee scheme**, not price. Underneath all of them sits **SMB** as the capped floor. The blanks
> are deliberate — they are exactly the live-verification work.

---

## 4. Cross-cutting themes

1. **Maintenance-fee competition & waiver-via-salary.** The *comissão de manutenção* is the headline
   battleground (Brief 02 §4). Incumbents set a monthly fee and then **waive it for salary
   domiciliation** (sometimes a minimum balance or an age bracket) — making *domiciliação de
   ordenado* the central relationship lever, not the rate.
2. ***Contas-pacote* bundling.** Banks wrap the account with a card, insurance, transfer allowances,
   and sometimes overdraft into a single monthly-fee **package**, which blurs the true cost of the
   account itself and complicates comparison — the exact problem the **FID** / *Extrato de
   Comissões* were created to counter (Brief 02 §2).
3. **SMB as the regulated floor.** *Serviços mínimos bancários* caps the cost of a basic account
   *for everyone*, bounding how aggressively incumbents can price the bottom of the market — a
   market-shaping floor with no analogue in the card or loan briefs. Uptake stays low relative to
   eligibility (`[REFRESH]`), a recurring conduct-supervision theme.
4. **Neobank disruption + the deposit-guarantee / IBAN-country caveat.** Free or near-free accounts,
   strong FX, and superior UX pull large secondary-account adoption (Revolut, N26). **But the honest
   distinction is not price — it is protection:** a **PT50 IBAN under the FGD** (€100,000 per
   depositor per institution, `[REFRESH]`) is materially different from a **foreign IBAN under
   another DGS** (Revolut → Lithuania, N26 → Germany) or **e-money safeguarding** (Wise), which is
   *not* deposit guarantee at all. Any fair comparison must state the scheme, not just the fee.
5. **The account as the cross-sell hub.** The conta à ordem is the **relationship anchor**: salary
   lands in it, bills leave it, and the term deposit, loan, and credit card all **settle against
   it** — the "*hub the others settle against*" role
   [ADR-PC-030](../../adrs/ADR-PC-030-product-scope-and-boundary.md) gives the 4th product shape.
   This is why incumbents fight to be the *primary* account: it is the gateway to everything else.
6. **Switching friction despite the mandated service.** PAD (DL 107/2017 `[REFRESH]`) guarantees a
   free *serviço de mudança de conta* within a fixed window (Brief 02 §2, `[REFRESH]`), yet real
   mobility stays low — inertia, direct-debit re-pointing, and the cross-sell stickiness above keep
   customers put. The *service* exists; the *behaviour* lags.
7. ***Descoberto* as a fee/credit lever.** The **authorized overdraft** (*descoberto autorizado*) is
   a regulated consumer-credit feature priced under the **shared BdP quarterly cap bucket** with
   credit cards (Brief 02 §2). It is a margin and stickiness lever on an otherwise low-/no-interest
   product; the *unauthorized* overshoot (*ultrapassagem*) carries its own charges. The mechanism is
   durable; the cap number is perishable (`[REFRESH]`).
8. **Open-banking aggregation.** PSD2 (DL 91/2018 `[REFRESH]`) AISP/PISP access lets apps
   **aggregate accounts across providers** and initiate payments — which both *intensifies* the
   cross-provider price comparison and lets neobanks present themselves as the front-end over an
   incumbent's FGD-covered account. (The transposing decree-law number is the value cited but is not
   asserted from memory — confirm it on the refresh pass, matching the hedge in Brief 02.)

---

## Figures to verify `[REFRESH]`

- **Every numeric cell** in the §3 matrix (maintenance fees, waiver thresholds, *descoberto*
  TAN/TAEG, plan prices).
- §1 market sizing: account penetration, *contas-pacote* vs standalone share, **SMB uptake**,
  average maintenance fee, switching-service volumes, neobank secondary-account share.
- **Per-provider IBAN country + deposit-guarantee scheme** for the ⚠ rows — **Moey**, **Openbank**,
  **Revolut**, **N26**, **Wise**: confirm PT50-vs-foreign IBAN and **FGD vs foreign DGS vs e-money
  safeguarding** *before* describing any of them as a bank account.
- **ActivoBank's booking entity / IBAN** (on the BCP licence) and whether maintenance is truly €0.
- **EuroBic's standalone status** (absorbed into ABANCA) and whether its brand/IBAN still stands.
- **SMB cost cap + IAS peg** and the **FGD ceiling** — quote from the current text, never from memory.
- The **current-quarter *descoberto* max-TAEG cap** (shared card/overdraft bucket).
- **PAD switching window + the transposing decree-law number** (DL 107/2017 is the value cited) —
  confirm both before use.
- **PSD2 transposing decree-law number** (DL 91/2018 is the value cited) — confirm before use,
  matching the hedge Brief 02 already applies.
- Each provider's **current flagship account/package names** and **MB WAY support** (marketing names
  and wallet support churn).

---

## Suggested authoritative sources for the refresh pass

- **Banco de Portugal** — the **comparador de comissões** (fee-comparison portal) and provider
  *preçários*; the **serviços mínimos bancários** pages (cap + eligibility); the quarterly **taxas
  máximas no crédito aos consumidores** tables that bound the *descoberto*; the *Todos Contam* portal
  and retail-banking conduct reports (penetration, SMB uptake, switching volumes).
- **Fundo de Garantia de Depósitos (FGD)** — the deposit-guarantee **ceiling and scope**, and which
  institutions are members (the decisive check for the ⚠ rows).
- **Provider *preçários* + FID** (*Documento de Informação sobre Comissões*) + the annual *Extrato de
  Comissões* — the legally-published ground truth for maintenance fees, waiver conditions, and
  package contents.
- **DECO / Deco Proteste** — independent consumer comparisons of current-account costs and packages.

*(Per the plan, `sources.md` with exact pointers — including each neobank's IBAN-country /
guarantee-scheme citation — is produced during the verification pass, not invented now.)*

---

*Previous:* [← 02 — Portugal context](./02-portugal-context.md) ·
*Up:* [00 — Research plan](./00-research-plan.md)
