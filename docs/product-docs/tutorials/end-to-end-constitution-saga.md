# Tutorial: end-to-end constitution saga (edge → orchestrator → ACL → engine → COMPLETED)

In this tutorial we watch a deposit be opened the **full intended way**: a client
hits the edge front door, an orchestrator runs a saga that reserves money on the core
banking system, gets the deposit approved, makes the irreversible debit, lands a real
deposit in the engine — and then the engine's own event flows back over the bus to
carry the saga to a terminal `COMPLETED`. We do not write any of this; we run one
script and read what it reports, step by step, so the moving parts of the command
plane become concrete.

This is a **learning** path (the Diátaxis tutorial quadrant), written for the
**integrator / solution-architect** who needs the lived feel of the whole saga
topology before reading the normative walkthrough. One route, no detours — the *why*
behind each hop lives in
[Document 05](../../product-management/integration_concepts/05-constitution-saga-walkthrough.md)
and the ADRs we link at the end.

> ## ⚠ Provisional tutorial — PoC / demo-only stack
>
> Everything here runs on the **demo stack**, which is a proof-of-concept, not
> production. The honest split:
>
> | Piece | Status in this tutorial |
> |---|---|
> | Engine, orchestrator (edge + saga + dispatcher) | **Built** — real .NET hosts the script starts. |
> | Core-ACL settlement target | **A stub** (`core-acl-stub`, WireMock). The real ACL has **no source** (`acl/` is a Dockerfile + README only); the saga's settlement legs hit a stub that records requests, not a real core. |
> | Postgres + Redpanda | **Built** — the real dev infra (`infra/compose.yaml`). |
> | The whole bring-up | **A single demo script** (`scripts/demo-saga.sh`), not a deployment. It leaves hosts running for inspection and tears them down on demand. |
>
> So: this proves the *topology and the contracts* end to end against a stub core. It
> is not evidence of a production-ready settlement integration.

---

## What we are about to see

The script `scripts/demo-saga.sh` brings up the command-plane topology and drives one
happy-path constitution plus one deliberate refusal. The hops, in order:

1. A client `POST`s the **edge** (`POST /api/v1/deposits/constitute`) → `202 Accepted`
   with a `process_id` and an SSE `stream_url`.
2. The **orchestrator** starts the constitution saga
   ([ADR-IC-003](../../product-management/integration_concepts/adrs/ADR-IC-003-saga-orchestrator.md),
   [Document 05](../../product-management/integration_concepts/05-constitution-saga-walkthrough.md)).
3. The saga's dispatcher delivers the **reversible** settlement leg
   (`ReserveAccountBalance`) to the **Core-ACL stub** over idempotent HTTP, then the
   **irreversible** leg (`ConfirmDebit`) after approval.
4. `ActivateDeposit` lands a **real deposit in the engine** — over synchronous
   idempotent REST
   ([ADR-PC-029](../../product-management/product_concepts/adrs/ADR-PC-029-engine-command-ingress.md)).
5. The engine's `DepositConstituted` event flows back over **Redpanda**; the
   orchestrator correlates it and advances the saga `APPROVED → COMPLETED` — the saga
   advances on the **event**, never on the `ActivateDeposit` HTTP `2xx`
   ([ADR-PC-029 slot-2 contract](../../product-management/product_concepts/adrs/ADR-PC-029-engine-command-ingress.md)).

The key idea to hold: **nothing but events ride the durable bus**
([Primitive 1](../../product-management/integration_concepts/01-the-six-primitives.md)).
Commands reach the engine over REST; the saga only ever *advances* on a real engine
event.

---

## Before we start

You need the dev toolchain and Docker. From the repository root:

```sh
make bootstrap        # pinned .NET 10 + tools (first time only)
make doctor           # confirm pinned versions are active
```

The script uses Docker (Postgres, Redpanda, the Core-ACL stub), `mise`, and a couple
of small CLIs; its preflight checks them for you and tells you what is missing. No
manual schema/seed steps — the script applies the event-store migrations and seeds the
rate sheet itself (it has to: the engine does **not** apply event-store migrations on
boot).

---

## Step 1 — Bring the whole path up

One command stands up infra, the engine, and the orchestrator, and runs all seven
assertions:

```sh
scripts/demo-saga.sh up
```

Watch the banner. The script narrates each of its seven phases and prints a green `✓`
per assertion (or a yellow `!` warning if one did not reach its expected terminal —
the script *warns* rather than tearing down, so you can inspect). The first run builds
the hosts and restores NuGet, so be patient.

---

## Step 2 — Watch the happy path reach COMPLETED

Phases 4 and 5 are the heart of it. Phase 4 drives the edge:

```
POST /api/v1/deposits/constitute  →  202 Accepted (process PROC-…)
```

A `202` (not a `200`) is the point: the edge **optimistically accepts** and hands back
a `process_id` and a `stream_url`, exactly as a mobile app would receive
([ADR-IC-006 §P4](../../product-management/integration_concepts/adrs/ADR-IC-006-edge-api-gateway.md),
[Document 05 Step 0](../../product-management/integration_concepts/05-constitution-saga-walkthrough.md)).
The work happens behind that acceptance.

Phase 5 reads the SSE stream and waits for the saga's terminal state. A successful run
reports the full walk:

```
saga walked STARTED → PARALLEL_VALIDATION → VALIDATIONS_COMPLETE → APPROVED → COMPLETED
```

Notice the saga *rests at* `APPROVED` and only moves to `COMPLETED` once the engine's
`DepositConstituted` arrives over Redpanda — that last hop crosses the bus. Then the
script confirms the bank actually holds the deposit by `GET`-ting the engine directly
(the deposit's id is the saga's internal `process_id`, the correlation key):

```
engine holds deposit <uuid> (HTTP 200, lifecycle Active) — the bank actually opened it
```

That is the whole thesis made observable: the saga did not *claim* success on an HTTP
`2xx`; it earned `COMPLETED` from a real engine event.

---

## Step 3 — Confirm both settlement legs hit the (stub) core

Phase 6 confirms the two money legs landed on the Core-ACL **stub**:

```
ReserveAccountBalance delivered (POST /v1/reservations ×1) — the reversible hold
ConfirmDebit          delivered (POST /v1/debits ×1)       — the IRREVERSIBLE money leg
```

The ordering is the saga's whole safety story: the **reversible** reserve fires first
(it can be compensated), and the **irreversible** debit fires only after approval —
the reversibility-ordering principle
([Document 05](../../product-management/integration_concepts/05-constitution-saga-walkthrough.md)).
Remember these legs hit a **stub**, not a real core (`acl/` has no source) — what is
proven is the contract and the ordering, not a production settlement.

---

## Step 4 — Watch the refusal fail closed

Phase 7 runs the same flow with a source account flagged "insufficient". The stub
`422`s the reservation, so the saga fails **closed before approval** — and therefore
before `ActivateDeposit` — so the engine is **never touched** and no deposit is
appended:

```
refusal saga <PROC-…> reached terminal DEPOSIT_CONSTITUTION_FAILED
  (fail-closed — nothing committed, engine never touched)
```

This is the half that makes the happy path trustworthy: when a precondition fails, the
money is never moved and the engine never opens a deposit. Fail-closed, not
fail-dirty.

---

## Step 5 — (Optional) drive it from the UI, then tear down

The script leaves the engine and orchestrator running. You can flip Mission Control
into `LIVE·saga` mode and constitute a deposit through the browser against the same
stack:

```sh
python3 docs/demo/mission-control/serve.py    # serves the UI + proxies /api/v1/* + /v1/*
open http://localhost:9000                     # Mode → LIVE·saga → Constitute deposit
```

When you are done, stop the hosts the script started (infra is left up; use
`make down` for the whole stack):

```sh
scripts/demo-saga.sh down
```

---

## You did it

You watched a deposit travel the full command plane: optimistic `202` at the edge, a
saga that reserves then (after approval) irreversibly debits a core, a real deposit
landed in the engine over idempotent REST, and the saga carried to `COMPLETED` by the
engine's own event over the bus — plus the refusal that fails closed. The shape you
saw is the production topology; only the **core was a stub** and the **bring-up was a
demo script**.

What we deliberately did **not** do here:

- **Drive the saga over the MCP agent surface.** A known producer gap means an agent
  cannot yet obtain a saga `process_id` purely over MCP (Document 11) — we drove the
  orchestrator edge directly. The agent-channel recipes are the
  [discover/authenticate](../how-to/discover-and-authenticate-to-the-mcp-server.md) and
  [call-a-tool](../how-to/call-a-deposit-tool-and-parse-the-result.md) how-tos.
- **Integrate a real core.** The settlement legs hit a WireMock stub; the real ACL is
  unbuilt (`acl/`).

### Where to go next

- The normative walkthrough of every saga step and state:
  [Document 05 — Constitution saga walkthrough](../../product-management/integration_concepts/05-constitution-saga-walkthrough.md).
- Why commands go over REST and the saga advances on the event:
  [ADR-PC-029](../../product-management/product_concepts/adrs/ADR-PC-029-engine-command-ingress.md).
- The saga orchestrator pattern itself:
  [ADR-IC-003](../../product-management/integration_concepts/adrs/ADR-IC-003-saga-orchestrator.md).
- The one-way facts the engine emits to downstream systems:
  [the five boundary signal contracts](../explanation/the-five-boundary-signal-contracts.md).
- Back to the [product-docs front door](../README.md).
