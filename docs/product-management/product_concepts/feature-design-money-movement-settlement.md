# Feature Design — Money Movement: the `Movement` Primitive and the Settlement Leg

> Companion to [ADR-PC-032](./adrs/ADR-PC-032-money-movement-primitive.md) (the decision: every money movement is a first-class `Movement`). This document is the **implementation design** that realises it — how the atom is shaped, where the settlement leg lives, what gets built, and what gets deleted.
>
> Interlocks with [ADR-PC-016](./adrs/ADR-PC-016-legacy-current-account-adapter.md) (the legacy current-account settlement contract the cash leg obeys), [ADR-PC-029](./adrs/ADR-PC-029-engine-command-ingress.md) (the de-settled command ingress this generalises), [ADR-IC-003](../integration_concepts/adrs/ADR-IC-003-saga-orchestrator.md) (the saga substrate the settlement leg is built into), and [feature-design event-store + projections](./feature-design-event-store-projections.md) (the events the `Movement` rides inside).
>
> Reading order: §1 frame · §2 the `Movement` value object · §3 two mechanisms today → one · §4 the substrate-owned settlement saga · §5 retiring the eager port · §6 finality and ordering · §7 decider discipline · §8 the settlement command surface · §9 migration sequence, renames, deletions · §10 fitness functions and risks.

---

## 1. Frame: one append-first atom for every cash leg

Babelstone moves money in many places, and today every place hand-writes the same risky shape: a family decider calls `settlement.SettleAsync(…)` **synchronously**, then `runtime.AppendAsync(…)`. The money move (legacy Core, across the ACL) and the fact (event store) are two systems with no shared transaction, and the cash moves **before** the fact is durable — so a crash between them leaves money gone with no record (bd `babelstone-5r9n.1`, the orphan window). Worse, each family re-decides *eager-vs-gated* from scratch (bd `babelstone-5r9n.2`, bd `babelstone-t7o3.13` — the per-operation fork).

[ADR-PC-032](./adrs/ADR-PC-032-money-movement-primitive.md) fixes the shape with one concept: a **`Movement`** — a single-sided, append-first record of value moving against one engine-owned account. The decider produces it as **data**; the spine records it **inside the event's transaction**; the cash leg is then a **downstream, confirmation-gated consequence**, not a precondition. This document specifies the build. Two structural decisions drive everything:

- **The settlement leg is lifted into the orchestrator substrate** (§4), parameterised by the `Movement` — not re-hand-coded per family.
- **The eager `ISettlementPort` mechanism is deleted** (§5), not preserved — `Movement` is its successor. Babelstone is not live; we carry no compromise forward.

---

## 2. The `Movement` value object

`Movement` is a spine value object (`Babelstone.Engine`), beside the event-store primitives. It **names no family**, so the [`ENGINE_FAMILY_AGNOSTIC`](./adrs/commitment-catalogue.md) gate holds ([ADR-PC-021 §P2](./adrs/ADR-PC-021-application-layer-family-owned-deciders.md)):

| Field | Meaning |
|---|---|
| `account_ref` | the legacy-Core / engine-owned account the value moves against — an **opaque** reference, never PII ([ADR-PC-004](./adrs/ADR-PC-004-pii-crypto-shredding.md)). |
| `direction` | `Debit \| Credit`, **always relative to `account_ref`**: `Debit` = value leaves that account, `Credit` = value enters it. |
| `amount` | `Money` (integer cents; crosses from `decimal` exactly once, [ADR-PC-010 §P2](./adrs/ADR-PC-010-dotnet-hand-rolled-engine.md)). |
| `value_date` | the economic date (`valid_time`), never wall-clock. |
| `operation` | a **closed** operation code (`disburse`, `collect_installment`, `pay_maturity`, `pay_coupon`, `pay_early_termination`, `repay_early`, `rollover_debit`, …) — the engine-side name for the movement, which the settlement leg maps to the ACL `operation_type` (§8). Replaces the old free-string `Reason`. |
| `origin` | `Originated \| Observed` (§4) — did the engine decide it (cash leg to drive) or observe it already cleared. |
| `command_id` | the [ADR-PC-029](./adrs/ADR-PC-029-engine-command-ingress.md) append-idempotency `CommandId`, carried for correlation (§6). |

**Direction is relative to the named account — this kills a real wrinkle.** Today the loan disbursement is coded `SettlementDirection.Debit` against `DisbursementAccountRef`, even though a loan *pays out* (the borrower's account is **credited**). That ambiguity exists only because the old DTO never pinned *whose* account. Under `Movement` each leg's `(direction, account_ref)` pair is re-derived from the real cash flow and must be internally consistent: a disbursement is a **`Credit` to the borrower's account** (or a `Debit` to the bank's funding account — whichever account the leg names). Every migrated leg (§9) re-states its pair explicitly; none is carried over unexamined.

**The `Movement` rides inside the event.** It is **data on the money-moving event's existing opaque payload** ([ADR-PC-010 §P3](./adrs/ADR-PC-010-dotnet-hand-rolled-engine.md) / [ADR-PC-001 §P1](./adrs/ADR-PC-001-event-store-technology.md) column contract untouched — no new `events` column, no envelope change), so it is written **append-first** in the event's outbox transaction. An event may carry **more than one** `Movement` (renewal = a rollover `Debit` **and** an interest `Credit`); the carrier is `IReadOnlyList<Movement>`. Its concrete Avro shape is the carrying event's own schema, governed like every emitted contract and pinned by the **contract-reviewer** when the first family migrates.

---

## 3. Two mechanisms today → one

The current estate has **two** settlement paths, and the design collapses them into one:

1. **Eager (the anti-pattern).** `ISettlementPort.SettleAsync(SettlementInstruction)` — 8 call-sites (3 loan: disburse/installment/repay; 5 deposit: maturity/coupon/early-termination/renewal-debit/renewal-credit), each wired in-engine to the `LoggingSettlementPort` stub. This is what the orphan window and the fork live in.
2. **Gated (the target shape, proven once).** The constitution debit (bd `babelstone-t7o3.4`) does **not** use `SettlementInstruction`; it appends `DepositConstituted` only and the principal debit is the saga's gated `ReserveAccountBalance → ConfirmDebit` step, dispatched to the ACL (`/v1/reservations`, `/v1/debits`). But those settlement commands + routing currently live **inside the term-deposit** `ConstitutionProcessCommands.cs` / `SagaCommandRouter.cs` — family-owned, though conceptually generic.

The design **deletes mechanism 1** and **generalises mechanism 2** out of the family and into the substrate. Every cash leg becomes: *decider emits `Movement` on the event → substrate settlement saga effects it, gated.*

---

## 4. The substrate-owned settlement saga (the leg's home)

The settlement leg lives in the **orchestrator substrate** ([ADR-IC-003](../integration_concepts/adrs/ADR-IC-003-saga-orchestrator.md)), which [bd `babelstone-t7o3.12`](./adrs/ADR-PC-021-application-layer-family-owned-deciders.md) already made family-agnostic. This stays the right side of [ADR-PC-021 §D1](./adrs/ADR-PC-021-application-layer-family-owned-deciders.md) ("no shared cross-family *application* project"): the substrate is orchestrator infrastructure, not a family app layer.

### 4.1 Standalone vs embedded legs

The migration surface splits cleanly:

- **Standalone legs (7 of 8).** The engine appends the fact (`DepositMatured`, `LoanDisbursed`, …) carrying its `Movement`(s); the only remaining work is "effect the cash, gated, park on failure." A degenerate 2–3-state flow.
- **Embedded leg (1).** The constitution debit is one step inside a *multi-step* saga (parallel validation → approval fork → confirm → activate, with compensation). The money move is interleaved with the approval lifecycle.

### 4.2 The decision: one substrate-owned, `Movement`-triggered settlement saga

A new generic saga module — `saga_type` **`settlement`**, the substrate's `SettlementProcess` — handles every **standalone** leg. It is **event-auto-started**: `ISagaModule.StartMode` already offers an event-triggered mode (a start-event type + CloudEvents-header predicate), so a `Movement`-bearing event auto-starts a settlement instance with **no family saga code**. The family emits the `Movement`; the substrate does the rest.

The saga is **parameterised by `direction`** (the gating asymmetry of [ADR-PC-016 slot 5](./adrs/ADR-PC-016-legacy-current-account-adapter.md)):

- **`Debit`** → **funds-gated**: `Reserve → Confirm` two-phase; a refused reserve (`InsufficientBalance`) compensates/parks; an indeterminate confirm enters clearance ([ADR-IC-012 §P5](../integration_concepts/adrs/ADR-IC-012-anti-corruption-layer-implementation.md)).
- **`Credit`** → **confirmation-gated only**: a single `Confirm` (legacy always accepts a credit, but it must confirm for reconciliation flow 1); a non-confirm enters clearance, never silent.

An event carrying multiple `Movement`s (renewal) effects each, gated by its own `direction`, under per-account ordering (§6).

The **embedded** leg (constitution) keeps its rich saga; in a later pass it **composes** the same leg machinery as a sub-step (rule-of-three cleanup). It was **not** refactored in the first pass — it already worked gated, and its reserve/confirm is interleaved with the approval fork. *Leave working code working; prove the new saga on the 7 standalone legs first.* That later pass has now landed (bd `babelstone-t7o3.18`): the constitution debit leg composes the shared `SettlementReferences` derivation rather than a hand-coded copy, so the constitution and the substrate settlement leg derive the identical Core-facing references — the approval-fork lifecycle stays exactly where it is (§10).

---

## 5. Retiring the eager port

`ISettlementPort`, `SettlementInstruction`, and the `LoggingSettlementPort` stub are **deleted**, not preserved. They are the eager mechanism; the gated path never used them, so there is nothing to "keep as the wire shape." `Movement` strictly supersets the old DTO (it adds `value_date`, `origin`, `operation`, `command_id`), so it is the successor as the **engine-side** record; the **ACL-side** wire shape is the substrate's settlement commands (§8).

**Deletion is the last migration step**, gated by the `MOVEMENT_APPEND_FIRST` fitness function: while any leg is still eager, the port survives; when the call-site assertion is green (zero eager `SettleAsync`), the port + DTO + stub are removed in the same change. The fitness function *is* the safe-to-delete signal.

The free-string `Reason` (`"maturity"`, `"renewal_rollover"`, …) hardens into the closed `Movement.operation` code (§2), which maps to the ACL `operation_type` half of the [ADR-IC-012 §P4](../integration_concepts/adrs/ADR-IC-012-anti-corruption-layer-implementation.md) idempotency key.

---

## 6. Finality and ordering

**The fact is final append-first; the cash leg is a downstream consequence.** This reconciles a wording tension: bd `babelstone-t7o3.13` currently says "the engine fact is *not treated as final* until the ACL credit is confirmed." Under `Movement` the cleaner model holds: `DepositMatured` (with its `Movement`) **is** final the moment it appends — the deposit *did* mature on the engine's books. The credit is a separate, gated consequence; if it cannot be effected, it parks in `HUMAN_INTERVENTION_REQUIRED` with **no compensation** ([ADR-IC-003 §P6](../integration_concepts/adrs/ADR-IC-003-saga-orchestrator.md) — the money either moved or did not; the saga never invents an undo). *The fact is durable first; the cash is retryable after.* The `t7o3.13` wording is updated to this model (§9).

**Two idempotency keys, two seams** ([ADR-PC-032 slot 4](./adrs/ADR-PC-032-money-movement-primitive.md)): the `CommandId` dedupes the **engine append** (a replayed command returns the original `commit_sequence`, no second `Movement`, [ADR-PC-029 slot 4](./adrs/ADR-PC-029-engine-command-ingress.md)); the **cash leg**, as a saga step, inherits the ACL's `(operation_type, saga_step_id, external_reference)` key + the refuse-to-send guard ([ADR-IC-012 §P4/§P5](../integration_concepts/adrs/ADR-IC-012-anti-corruption-layer-implementation.md)) the eager call bypassed. The design does **not** collapse or derive one from the other.

**Ordering.** `Movement`s against one `account_ref` are effected in append order, inheriting the outbox's per-aggregate FIFO ([ADR-IC-004](../integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md)); the settlement saga preserves it (the dispatcher's per-`process_id` FIFO, bd `babelstone-t7o3.7`). A multi-`Movement` event (renewal) effects its legs in declared order.

---

## 7. Decider discipline

Each migrated decider method changes the same way: **stop calling `SettleAsync`; attach the `Movement`(s) to the event; append.** The flow inverts from *settle-then-append* to *append-the-fact-carrying-the-movement* — the cash leg is the substrate's job, downstream. The decider stays pure-orchestration ([ADR-PC-021](./adrs/ADR-PC-021-application-layer-family-owned-deciders.md)); the **fold** still only records the already-decided facts (the `Movement` is data on the event, not recomputed in a handler). No clock, no I/O, no settlement call on the append path.

This also retires the `ISettlementPort` constructor dependency from `PersonalLoanConstitutionService` and `TermDepositConstitutionService` — the family application services no longer know about settlement at all; they emit facts.

---

## 8. The settlement command surface in the substrate

The settlement commands move from the term-deposit `ConstitutionProcessCommands.cs` / `SagaCommandRouter.cs` into the **substrate**, generalised:

- **Debit leg**: `ReserveAccountBalance`, `ConfirmDebit` (+ compensation `ReleaseBalanceReservation`, `ReverseCoreDebit`; clearance `QueryCoreDebitStatus`) — already exist, relocate verbatim (names are account-generic, not deposit-specific).
- **Credit leg**: a **new** generic `ConfirmCredit` command + its ACL endpoint (e.g. `/v1/credits`) — the gated path only has debit commands today because only the constitution **debit** was de-settled; the credit legs (maturity/coupon/early-termination/renewal-credit) need their gated command.
- **Routing**: a substrate `SettlementCommandRouter` maps each to the ACL `SettlementBaseUrl`; the `operation` code selects the [ADR-PC-016](./adrs/ADR-PC-016-legacy-current-account-adapter.md) settlement command (`debitForConstitution`-class) and the `operation_type` half of the ACL idempotency key.

The constitution saga, while still family-owned in pass 1, **switches to consume these substrate commands** (one settlement-command home from day one, per the agreed mandate) — its `SagaCommandRouter` references the substrate commands instead of family-local copies; its *saga lifecycle* (the approval fork) stays where it is.

---

## 9. Migration sequence, renames, deletions

A vertical slice closes the P1 first, then generalises:

1. **Platform** — the `Movement` value object; the substrate `SettlementProcess` (`settlement` saga) + `ConfirmCredit`/ACL credit endpoint; the settlement commands relocated to the substrate; the WireMock Core ACL extended to credits.
2. **Disbursement (bd `babelstone-5r9n.1`, the P1)** — de-settle `DisburseAsync` onto a `Movement`; first standalone leg through `SettlementProcess`. Closes the orphan window.
3. **Host-wire the loan (bd `babelstone-9g77`)** — the loan lands **Movement-shaped**, never eager (its prior "settlement shape is separate" premise is superseded).
4. **Loan installment + early-repay legs** — the loan's other two standalone legs onto `Movement`.
5. **Deposit legs (bd `babelstone-t7o3.13`)** — maturity / coupon / early-termination / renewal onto `Movement` via `SettlementProcess`; converges with the loan by construction.
6. **Delete the eager port** — `ISettlementPort` / `SettlementInstruction` / `LoggingSettlementPort` removed once `MOVEMENT_APPEND_FIRST` is green (§5).
7. **(Later, optional)** constitution saga composes the shared leg (rule-of-three cleanup).

**Explicit renames / deletions (no compromise carried forward):**

- **Delete**: `ISettlementPort`, `SettlementInstruction`, `LoggingSettlementPort`.
- **Rename**: the `Movement` field `Reason` (free string) → `operation` (closed code); `SettlementDirection` keeps `Debit`/`Credit` but its meaning is **fixed as relative to `account_ref`** (§2), and each leg's pair is re-derived (the disbursement-`Debit`-on-the-borrower's-account wrinkle is corrected).
- **Relocate**: the settlement commands + router from `families/term-deposit/**/Orchestration` into the substrate.
- **Re-word**: the bd `babelstone-t7o3.13` "fact not final until confirmed" framing → the append-first finality model (§6).

---

## 10. Fitness functions and risks

**Fitness functions** ([commitment catalogue](./adrs/commitment-catalogue.md), registered as the ADR-PC-032 commitments flip `Planned → Live`):

- `MOVEMENT_APPEND_FIRST` — a call-site/architecture assertion: no decider calls a settlement port before its append (the orphan window cannot reopen). Also the safe-to-delete signal for the eager port (§5).
- `MOVEMENT_CASH_LEG_IDEMPOTENT` — an integration test (gated-settlement, WireMock Core): a retried `Originated` cash leg cannot double-move (it inherits the ACL guard; the eager bypass is gone).
- `SETTLEMENT_LEG_SCA_GATE_CANNOT_BYPASS` (bd `babelstone-t7o3.19`, `Live`) — an integration test: an `Originated` money-mover cash leg with absent/stale step-up SCA is refused at the RECEIVER (the Core ACL settlement leg) `422 SCA_REQUIRED` before `ConfirmDebit`/`ConfirmCredit`, re-checked at the settlement-dispatch instant; the substrate attests, never denies ([ADR-PC-032 §A7/§A8](./adrs/ADR-PC-032-money-movement-primitive.md)). The settlement-leg analogue of `MCP_SCA_GATE_CANNOT_BYPASS`.

**Risks:**

- **The constitution refactor is DONE (bd `babelstone-t7o3.18`), so the leg machinery is shared, not copied.** Pass 1 left two leg-invocation styles (standalone `SettlementProcess` vs the constitution's embedded reserve/confirm) with the *commands* unified day one (§8). The rule-of-three cleanup then collapsed the duplicated derived-reference machinery: the constitution debit leg and the substrate settlement leg now compose the SAME shared `SettlementReferences` derivation, so they derive the IDENTICAL `external_reference` for a given process id (the cross-saga no-double-debit invariant is structural). The constitution's approval-fork *lifecycle* is unchanged — only the leg's reference assembly is shared.
- **Multi-`Movement` ordering** (renewal's debit + credit) must hold per-account FIFO through the settlement saga — covered by the dispatcher's per-`process_id` order (bd `babelstone-t7o3.7`), exercised by a renewal integration test.
- **Credit clearance** is new surface: credits are now confirmation-gated (not eager), so the indeterminate-clearance path must cover credits, not just debits — the WireMock ACL and `SettlementProcess` model the credit non-confirm case.
- **The `operation` closed code is a contract** the ACL keys on; widening it later is forward-only schema evolution ([ADR-IC-002](../integration_concepts/adrs/ADR-IC-002-schema-format-and-registry.md)), pinned by a consumer-driven contract test ([ADR-IC-009](../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md)).
