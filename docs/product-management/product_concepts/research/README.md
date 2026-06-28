# Product Research

Market research that **feeds the product engine's scope decisions** — the descriptive
"what is this product, how does it work in Portugal, who sells it" groundwork that an
engine ADR then turns into a build decision. The research is *descriptive*: it performs no
engine/ADR/contract design itself. The decisions it informs live in the
[product-engine ADRs](../adrs/README.md).

## In plain English

Before babelstone models a new banking product, we research it from the ground up — what
the product fundamentally is, how Portugal's rules and taxes bend it, and who offers it
today. These briefs are that research. The first two started as scratch notes and were **promoted
here** once they began feeding a real decision — [ADR-PC-030](../adrs/ADR-PC-030-product-scope-and-boundary.md),
which fixes what babelstone will and won't model; the conta à ordem research was written here
directly, to feed its already-committed family ADR (bd `babelstone-xvcx`).

## Contents

| Folder | Product | Engine relevance |
|---|---|---|
| [`personal-loan/`](./personal-loan/00-research-plan.md) | Crédito pessoal — a fixed-amount, fixed-term, fully amortizing unsecured personal loan | **Next family** on the roadmap (a closed-end asset; the mirror of the term deposit). Origination/underwriting stays upstream. |
| [`credit-cards/`](./credit-cards/00-research-plan.md) | Credit cards in Portugal | The **account/revolving slice** is in scope (open-end revolving asset); the four-party scheme — authorization, clearing, settlement, chargeback, interchange — is **out of boundary**. |
| [`current-account/`](./current-account/00-research-plan.md) | Conta à ordem — the current / demand account | The **4th product shape** (the *transactional balance account*) and **the hub the others settle against** — already in scope, and the [ADR-PC-016](../adrs/ADR-PC-016-legacy-current-account-adapter.md) **v4 destination**. The rails/scheme/clearing/SCA/fraud stay **out of boundary**. Feeds the conta à ordem family ADR (bd `babelstone-xvcx`). |

Each folder is a three-brief funnel: **01** (jurisdiction-agnostic fundamentals) → **02**
(the Portuguese regulatory/tax/market context) → **03** (the live competitive landscape),
fronted by a **00** research plan. Every perishable figure carries a `[REFRESH]` tag and is
collected in a "Figures to verify" appendix — these are *structure-first* briefs, written
from domain knowledge, with the live-number verification pass deliberately deferred.

## How this relates to the scope decision

[ADR-PC-030](../adrs/ADR-PC-030-product-scope-and-boundary.md) reads these products across
the engine's boundary — crédito pessoal fits the deterministic-fold kernel whole; a credit
card fits only as its account slice; and the conta à ordem is the most fundamental shape of
all, the **transactional balance account the other three settle against**. It uses that
contrast to fix babelstone as a **core product & account ledger** (the accepted posture B)
spanning the full retail product topology — **liability** (term deposit) / **closed-end
asset** (crédito pessoal) / **open-end revolving asset** (credit-card slice) / **transactional
account** (conta à ordem) — owning balances, rules, and the funds-and-rules core of real-time
authorization while stopping at the wire. Read the research for the *products*; read the ADR
for *what babelstone does with them*. Unlike its two siblings, the conta à ordem is **already
in scope**, so its research feeds the conta à ordem **family ADR** (bd `babelstone-xvcx`)
directly.
