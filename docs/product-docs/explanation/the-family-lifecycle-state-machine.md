# The family lifecycle state machine

Every product family has a notion of "what state is this instance in, and what
may legally happen to it next?" — a deposit is opened, then accrues, then
matures or is broken early; it cannot mature twice, and it cannot accrue after
it is closed. babelstone answers that question in **one place**: a small,
explicit **legality table**, owned by the family. This page explains why it is a
single table, why legality lives there rather than inside the folds, and why
*terminality is absence from the table* rather than a flag.

It is background reading, not a procedure. The how-tos quietly assume this model;
this is where it is made explicit. The worked example throughout is the real
[`LifecycleTransitions.cs`](../../../families/term-deposit/src/Babelstone.Families.TermDeposit/LifecycleTransitions.cs)
in the `term_deposit` family — the source wins, and this page only explains it.

> Why this page exists: the legality table's *shape* — one table, a pure
> predicate, terminality-as-absence — was the working knowledge of the engineers
> who built it, captured in the file's own comments but with no home in the
> reader-facing docs. It is not a task you follow (that is a how-to) and not a
> field list you look up (that is the reference) — it is the *understanding* that
> makes a new family's lifecycle correct by construction. That gap is exactly
> what Diátaxis calls [explanation](https://diataxis.fr/explanation/).

---

## Two questions, deliberately kept apart

The most important thing to grasp is that a family answers **two different
questions** about its lifecycle, in two different places, on purpose:

| Question | Answered by | Kind of code |
|---|---|---|
| *What state did this instance fold into?* | the **folds** (`Handlers.cs`) | pure `(state, event) → state`, label-only |
| *Is this transition legal from the current state?* | the **legality table** (`LifecycleTransitions.cs`) | pure data + a pure `IsLegal` predicate |

A fold **labels**: `DepositConstituted`'s handler writes `Lifecycle =
DepositLifecycle.Active`, and that is all it does about the lifecycle — it
records where the instance now is. It does **not** check whether the move was
allowed. The folds are guard-free; they assume the event happened and just
account for it.

The *legality* — whether a deposit may be matured, whether you can accrue on a
closed deposit — is the orthogonal question, and it is asked **before** an event
is ever appended. The decider (the impure command side) calls `IsLegal(current,
transition)` and, on a `false`, refuses the command with a
`DomainRejectedException`. No illegal event is ever written, so the folds never
need to defend against one.

This split is load-bearing. If folds carried their own guards, the rule "you
cannot mature a matured deposit" would live in the maturity fold *and* be
implicit in the lifecycle enum *and* be re-checked by the decider — three copies
to keep in sync. Pulling legality into one table means a fold stays a trivial,
replay-safe accounting step, and the rule lives once.

---

## One table, read directly

The whole machine is a map from a transition to the set of states it may fire
*from*, plus a one-line lookup. From the reference family:

```csharp
private static readonly IReadOnlyDictionary<Transition, IReadOnlySet<DepositLifecycle>> LegalSources =
    new Dictionary<Transition, IReadOnlySet<DepositLifecycle>>
    {
        // Opening / rejecting: only from the seed Pending state (constitute-once).
        [Transition.Constitute]      = Set(DepositLifecycle.Pending),
        [Transition.FailConstitution]= Set(DepositLifecycle.Pending),

        // Operating on a live deposit: only from Active.
        [Transition.AccrueInterest]  = Set(DepositLifecycle.Active),
        [Transition.Mature]          = Set(DepositLifecycle.Active),
        [Transition.TerminateEarly]  = Set(DepositLifecycle.Active),
        // … the rest of the operating and closing transitions, all from Active
    };

public static bool IsLegal(DepositLifecycle current, Transition transition) =>
    LegalSources.TryGetValue(transition, out var sources) && sources.Contains(current);
```

(That is an abridged illustration — the
[authoritative table](../../../families/term-deposit/src/Babelstone.Families.TermDeposit/LifecycleTransitions.cs)
carries every transition with its full source set and the reasoning comments.
This page does not restate it field-for-field; the source is the truth.)

You can read this and predict every rejection without running anything:
`Constitute` is legal only from `Pending`, so a second constitution on a live
deposit is refused. `Mature` is legal only from `Active`, so maturing a matured
deposit is refused. The table *is* the specification.

---

## A transition is named by the event that drives it

Notice the `Transition` enum has one value per event the family emits —
`Constitute` ↔ `DepositConstituted`, `Mature` ↔ `DepositMatured`, and so on,
including the state-preserving ones like `AccrueInterest` ↔ `InterestAccrued`
(whose legal source is still `Active`, so the decider can reject accruing on a
*closed* deposit even though the move does not relabel the lifecycle). That
coupling is deliberate. Naming a transition after its driving event keeps the
table **in lock-step with the event taxonomy the family already owns**: adding a
new event forces you to add a row here, because otherwise the decider has no
legality answer for it. There is no way for a new transition to sneak in
unmodelled — and that is precisely the auditability the table buys.

So when you author a new family, the rule is: **one `Transition` per event, one
row in `LegalSources` per `Transition`.** A reviewer reading the table sees the
complete set of moves the aggregate can make, in one screen, with each move's
legal origins beside it.

---

## Terminality is absence, not a flag

Here is the design choice most worth carrying into your own family. There is **no
`IsTerminal` boolean** anywhere. A state is terminal precisely because **no
transition lists it as a legal source**.

`Matured`, `Failed`, `Renewed`, `TerminatedEarly`, `TransferredToHeirs` are all
business-terminal — and the way the code *expresses* that is simply that none of
them appears in any business transition's source set. Try to mature a matured
deposit and the lookup finds `Matured` is not in `Mature`'s source set →
`false` → rejected. The terminality emerges from the one table; it is not stored
a second time.

The payoff: there is no way for "the enum says terminal" and "the table says you
can still act on it" to disagree, because there is only one fact. Add a new
terminal state to a family and you get its terminality for free the moment you
*don't* list it as a source — no second place to remember to update.

---

## The one principled exception: a cross-cutting regulatory transition

There is exactly one transition in `term_deposit` that breaks the "business
transitions only fire from `Active`" pattern, and it is worth understanding
because your family may need the same shape: **GDPR Article 17 erasure**
(`Transition.Erase`).

Erasure is legal from *any* state that still holds the subject's personal data —
a live `Active` deposit **and** an already-closed one (`Matured`,
`TerminatedEarly`, …). A matured deposit is closed to every *business* move but
still carries the customer's PII until erased, so the regulatory obligation must
be able to reach it. The table encodes this by listing the business-terminal
states as legal sources of `Erase` only:

```csharp
[Transition.Erase] = Set(
    DepositLifecycle.Active,
    DepositLifecycle.Matured,
    DepositLifecycle.Failed,
    DepositLifecycle.Renewed,
    DepositLifecycle.TerminatedEarly,
    DepositLifecycle.TransferredToHeirs),
```

So "terminal to business operations" and "still erasable" coexist without a
contradiction — because terminality was defined against the *business*
transitions, and `Erase` is a regulatory one that lives orthogonally to the
business lifecycle. The genuinely-final state is `Erased`: it is the legal source
of **no** transition at all, so even a re-erasure is rejected — which doubles as
the idempotency guard. (The crypto-shredding mechanics behind erasure are
[ADR-PC-004 §P3](../../product-management/product_concepts/adrs/ADR-PC-004-pii-crypto-shredding.md);
the fold for the erasure event only *labels* the deposit `Erased` — the PII lived
behind an OpenBao key, never in the projection.)

The lesson for a new family: if you have a cross-cutting obligation that must
reach closed instances, model it as a transition with an *explicit, wider*
source set — never by weakening the definition of terminal.

---

## Why this is built this way

Pulling the lifecycle into one declarative table, consulted by the decider and
left out of the folds, buys several things at once:

- **One auditable specification.** A reviewer (or an auditor) reads every legal
  move and its origins in a single, small file — not by tracing guards scattered
  across handlers.
- **Replay-safe folds.** Because legality is checked on the write path, only
  legal events ever exist, so a fold can be a trivial label-and-accumulate step
  that replays identically every time. That determinism is the engine's core
  contract ([ADR-PC-010 §P5](../../product-management/product_concepts/adrs/ADR-PC-010-dotnet-hand-rolled-engine.md)).
- **No drift between "what state" and "what's allowed."** Terminality-as-absence
  means there is no second representation of the lifecycle to fall out of sync.
- **The decider owns rejection, the family owns the rule.** The family declares
  the table; the application-layer decider enforces it — the family-owned-decider
  arrangement of
  [ADR-PC-021 §P3](../../product-management/product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md).

That is what makes a family's lifecycle *correct by construction*: the legal
moves are data, the enforcement is one predicate, and the folds never have to
care.

---

## Where to go next

- To build one: [Tutorial: author your first family schema](../tutorials/author-your-first-family-schema.md)
  walks the reference family, including reading this table (Step 3).
- The folds the table keeps guard-free: [Write and test pure event handlers
  (folds)](../how-to/write-and-test-event-handlers.md).
- The full scaffolding procedure (the legality table is its Step 6): the
  [`new-family-schema` skill](../../../plugins/babelstone-engine/skills/new-family-schema/SKILL.md).
- Normative sources: [ADR-PC-021](../../product-management/product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md)
  (family-owned deciders), [ADR-PC-010](../../product-management/product_concepts/adrs/ADR-PC-010-dotnet-hand-rolled-engine.md)
  (the hand-rolled engine and its determinism contract),
  [ADR-PC-004](../../product-management/product_concepts/adrs/ADR-PC-004-pii-crypto-shredding.md)
  (the erasure transition).
- Back to the [product-docs front door](../README.md).
</content>
