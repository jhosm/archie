# Guides — Tutorials & How-To

The **hand-authored half** of the documentation overlay ([ADR-PC-022](../product_concepts/adrs/ADR-PC-022-product-documentation-architecture.md)). Where the three concern-axis series explain *what is true and why*, the guides here are **task-shaped**: they get a reader from "what is this" to "I did the thing."

## How this fits the corpus ([Diátaxis](https://diataxis.fr) typing)

The corpus is split by what a reader needs in the moment:

| Type | Where it lives | Reader's need |
|---|---|---|
| **Tutorial** | [`tutorials/`](./tutorials/README.md) | *Learn by doing* — a hand-held, run-it-yourself first contact |
| **How-to** | [`how-to/`](./how-to/README.md) | *Achieve a specific goal* — a procedure for a reader who already knows the terrain |
| **Reference** | [`../reference/`](../reference/README.md) | *Look something up* — generated, exhaustive, dry |
| **Explanation** | the three series ([financial](../financial_concepts/banking_products_financial_mathematics.md) · [product](../product_concepts/README.md) · [integration](../integration_concepts/00-introduction-and-decisions.md)) | *Understand* — the design rationale and the math |

If you don't know where to start, the [reading paths](../reading-paths/README.md) sequence these by role.

## The one rule for everything under `guides/`

> **Guides link to normative content; they never restate it.**

A decision, a contract slot, a financial formula, or a defined term has exactly one authoritative home — an ADR, a concept doc, the [glossary](../reference/README.md), or a generated reference page. Guides **cite and sequence** those homes; they do not copy them. This is the load-bearing invariant of [ADR-PC-022 §P3](../product_concepts/adrs/ADR-PC-022-product-documentation-architecture.md): it is what keeps this newcomer-facing layer *outside* the set of things that can silently drift from the spec. Keep guides thin and link-heavy.
