# Babelstone.FinancialMath

Pure financial-math primitives — day-count, simple-interest accrual, withholding, rate-schedule
folding (`RateSchedule`), and French-system loan amortization (`Amortization`) — that the engine's
family deciders call to compute money. Every primitive is pure (no clock, no I/O, no randomness),
crosses to `Money` exactly once at a single rounding boundary (ADR-PC-010 §P1–§P2), and replays
byte-for-byte. The kernel is one of the eight **generic engine spine** projects enumerated in
[ADR-PC-021 §P2](../../../docs/product-management/product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md):
it is shared infrastructure, referenced *by* family deciders, never the reverse.

## Cohesion policy / family-agnosticism

**In plain English.** The kernel is meant to be a shared toolbox that names no single product.
But its doc-comments and method names do talk about *deposits*, *crescente* / *escalonada* rate
shapes (`RateSchedule`), and *loans* / *borrowers* (`Amortization`). That is **allowed and
deliberate**, and this note records the rule so the next family author knows what is and is not a
violation: a math primitive may *name* the product it prices in its prose and identifiers, but the
kernel project must never *depend on* (reference) a family. The boundary that keeps the engine
family-agnostic is the dependency arrow, not the vocabulary.

### The decision

**Start from what the kernel is *for*.** The financial-math reference shows the mathematics is
*unified*: the same balance-evolution identity prices every banking product, the product being only
a parameterization — "the base algorithm is always the same … what differs … are only three
dimensions" ([fin-math §2.2](../../../docs/product-management/financial_concepts/banking_products_financial_mathematics.md)),
and the accrual `J = Σ S·r·Δt` is, in the doc's words, "universal to both" families (fin-math §9.2).
The product architecture makes that a structural mandate: **"One engine, one runtime, one
balance-evolution function — invoked with different parameters for different products"** — explicitly
*not* **"a shared library that each product module reuses"** ([01-product-architecture §1](../../../docs/product-management/product_concepts/01-product-architecture.md)).
`Babelstone.FinancialMath` *is* that one generic, parameterized math — kept as a single shared
kernel because the math itself is reusable across families. That placement is not re-decided here:
[ADR-PC-031](../../../docs/product-management/product_concepts/adrs/ADR-PC-031-personal-loan-family.md)
already settled it when it added the `Amortization` kernel, rejecting its option D ("amortization
kernel in the family project") precisely because "a future family … would re-implement it." This
note interprets and applies that decision.

**The vocabulary permission is a *consequence* of that, not a standalone licence.** Because the
kernel is shared parameterized math, a product noun in it is a *descriptive label on that shared
math*, not a coupling to a family. So **product vocabulary IS permitted in `Babelstone.FinancialMath`
— in doc-comments, summaries, XML remarks, parameter names, and method/type names that describe what
a primitive computes.** A primitive that prices a *term deposit* may say so (`RateSchedule` —
`crescente`/`escalonada`, "one deposit's rate", coupon windows); a primitive that amortizes a
*personal loan* may say so (`Amortization` — "closed-end personal loan", "borrower", "installment").
The next family author adds their product's math here and names it for what it does, **subject to the
two hard limits below.**

What family-agnosticism actually forbids — and what this kernel must keep honouring — is the
**dependency edge**, gated mechanically by `ENGINE_FAMILY_AGNOSTIC`
([ADR-PC-021 §P2/§D2](../../../docs/product-management/product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md),
Verifiable commitment 1):

1. **No project reference into `families/**`.** `Babelstone.FinancialMath.csproj` may reference
   only spine projects (today: `Babelstone.FinancialTypes`). The `family → engine` arrow is
   one-way; a kernel that referenced a family would invert it and is rejected at the build by the
   `EngineFamilyAgnosticTests` fitness function.
2. **No family-specific *type* dependency.** A primitive takes generic inputs — `Money`, `DateOnly`,
   `DayCountConvention`, basis-point `int`s, `RateSegment`/`PrincipalSegment` value records — never a
   family's event record, command shape, aggregate state, or projection type. It computes a number
   from numbers; it does not import a family's domain model. (The kernel is referenced by *both*
   families' `.Application` deciders and by the spine `Babelstone.Packs` — a family-typed parameter
   would either couple it to one family or fail to compile against the other.)

So the rule the next family author follows is: **name your math for the product it prices, keep
your inputs generic, and add no `families/**` reference.** Vocabulary describes; references couple.
Only the latter erodes the boundary.

**On the phrase "names no family."** [ADR-PC-031](../../../docs/product-management/product_concepts/adrs/ADR-PC-031-personal-loan-family.md)
describes this kernel as "generic, naming no family," and [ADR-PC-021 §P2](../../../docs/product-management/product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md)
likewise says a spine library "names no family" — which can read as a ban on product *words*. It is not:
the ADR estate scopes "names no family" to *code* — no `families/**` reference, no family-typed
table (ADR-PC-021 amendment 2026-06-13), no concrete family type — and ADR-PC-021 states the limit
outright for its host gate: **"a family named only in a COMMENT is fine"** (Verifiable commitment 3).
`RateSchedule` saying *deposit* in a doc-comment is exactly that — a comment-level label on generic
math, not a family dependency — so the prose here is consistent with "names no family," which is a
property of the dependency graph, not of the words.

### Why this and not literal agnosticism (relocate the deposit math into the family)

The alternative — hold the kernel to *literal* (vocabulary-level) agnosticism and move
deposit-shaped rate resolution into `families/term-deposit` — was rejected because it contradicts
the engine's own structure and would not even be self-consistent:

- **The kernel is *defined* as a spine project, not a family.** [ADR-PC-021 §P2](../../../docs/product-management/product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md)
  enumerates `Babelstone.FinancialMath` among the eight family-agnostic spine projects precisely so
  that family deciders (the impure, family-owned `.Application` layer, [ADR-PC-021 §S2 / the decider
  triangle](../../../docs/product-management/product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md))
  can call it. `TermDepositDecider` and `PersonalLoanDecider` both reference this kernel today. Math
  living in the spine and named for the product it serves is the intended shape, not a leak.
- **The vocabulary is *pervasive and symmetric*, not a `RateSchedule` anomaly.** `Amortization`
  ("personal loan", "borrower"), `Accrual`/`Withholding`/`Rates` ("deposit", "maturity", "coupon")
  all name products too. Relocating `RateSchedule` into term-deposit on a vocabulary rule would
  oblige relocating `Amortization` into personal-loan and the accrual/withholding primitives
  likewise — dissolving the shared kernel into per-family copies and re-opening the cross-family
  reuse the spine exists to provide.
- **Relocation would force a forbidden edge or duplication.** If shared math lived in a family, a
  *second* family needing it would have to reference the first family (the `family → family` /
  arrow-inverting edge §P2 forbids) or duplicate the primitive. Keeping it in the spine is the only
  placement that lets every family reuse it without coupling families to each other.

A future split *is* still legitimate, but only on a **reuse** test, never a vocabulary one: if a
primitive is provably single-family — used by exactly one family, with no plausible second consumer
— it MAY move into that family's `.Application` project (which already references the kernel, so the
arrow stays one-way). Today `RateSchedule` fails that test: stepped/tiered fixed-rate resolution is
generic rate-vector folding over simple interest, applicable to any term-priced product. It stays
in the kernel.

### Why the mechanical gate cannot enforce this

`ENGINE_FAMILY_AGNOSTIC` (`EngineFamilyAgnosticTests` in `Babelstone.Engine.Tests`) parses each
spine project's `.csproj` and fails only if a `<ProjectReference>` resolves under `families/**`. It
is a **dependency-graph** assertion and is, by construction, **blind to in-file content** — it never
reads a `.cs` file, so it cannot see (and is not meant to police) product nouns in a doc-comment or
a method name. That blindness is *correct under this policy*: the gate guards exactly the edge that
matters (the reference arrow) and stays silent on the vocabulary this note explicitly permits. The
consequence is that the "name your math, keep inputs generic, add no `families/**` reference" rule
above is a **review-time convention** for the type-dependency and vocabulary dimensions — upheld by
the financial-math / replay-determinism review lenses and this README — while only the project-
reference dimension is mechanically gated. There is deliberately no fitness function banning
product words in the kernel, because product words in the kernel are allowed.

## ADRs honoured

- [ADR-PC-031](../../../docs/product-management/product_concepts/adrs/ADR-PC-031-personal-loan-family.md)
  §D1/§P2 — the Accepted decision that placed the `Amortization` kernel in `Babelstone.FinancialMath`
  ("generic, naming no family") and rejected putting it in the family project (option D) on the
  reuse rationale. This note interprets and applies that placement to the kernel as a whole; it
  re-decides nothing.
- [ADR-PC-021](../../../docs/product-management/product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md)
  §P2/§D2 — the family-agnostic engine spine and the one-way `family → engine` arrow this kernel sits
  on the spine side of; `ENGINE_FAMILY_AGNOSTIC` (Verifiable commitment 1) is the gate this policy
  defers to for the reference edge.
- [ADR-PC-010](../../../docs/product-management/product_concepts/adrs/ADR-PC-010-dotnet-hand-rolled-engine.md)
  §P1–§P2, §P5 — integer-cent `Money`, round-once-at-the-boundary, and handler purity (no clock /
  I/O / randomness) the kernel's primitives uphold.
