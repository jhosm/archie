# Babelstone — Mission Control

A live, single-screen demo UI for the babelstone deposit engine. It's the wow the deck
hands off to (slide 5, "Watch this"): you constitute a deposit, watch immutable events
stream into the **living ledger**, and see the **position** fold out of them — then mature it
and watch the interest accrue, 28% withholding apply, and the payout resolve.

## In plain English

This is a one-page web app that shows the bank working. The left column is the controls, the
middle is the event stream (every change is a permanent, numbered fact), and the right is the
deposit's current state — which is *computed* from those events, never stored. It runs in three
modes: **DEMO** (no backend, perfectly deterministic, safe for a stage), **LIVE·engine** (driving
the real engine directly), and **LIVE·saga** (opening the deposit the intended way — through the
orchestrator edge and the constitution saga). It shares the deck's exact look so they feel like one product.

## The three modes

| | DEMO | LIVE·engine | LIVE·saga |
| --- | --- | --- | --- |
| What it drives | nothing (in-browser) | the engine's command surface, **direct** | the deposit opened through the orchestrator **edge → constitution saga** |
| Backend needed | none | the engine on `:8080` + `serve.py` | the engine on `:8080` + the orchestrator on `:8090` + Core-ACL stub + `serve.py` |
| Bring-up | `open index.html` | `scripts/demo-mcp.sh up` | `scripts/demo-saga.sh up` |
| What it proves | the math, on a stage with no network | the engine kernel is genuinely real | the **intended** command-plane end to end: edge → saga → dispatcher → settlement → engine → terminal completion |
| Contract | — | `POST /v1/deposits` (ADR-PC-029) | `POST /api/v1/deposits/constitute` → 202 + SSE (ADR-IC-006 §P4) |

Flip between them with the **Mode** toggle, top-right. DEMO is the default. Independently of the
Mode, the **Operator** toggle (`YOU` / `CLAUDE`) overlays the real-Claude agent — see
[The AI-native framing](#the-ai-native-framing) below.

### One command for all of it — `make demo`

The three sections below each bring up **one** slice (and each proves one thing). If you just want to
**open the UI and flip between every mode**, there's a single launcher that stands up the whole
backend at once:

```bash
# ANTHROPIC_API_KEY is optional — set it to enable Operator=CLAUDE (the real agent)
ANTHROPIC_API_KEY=sk-ant-…  make demo
```

`make demo` starts `serve.py` for you — then open **http://localhost:9000**.

`make demo` (`scripts/demo-all.sh`) brings up Postgres + Redpanda + the Core-ACL stub, **one**
Redpanda-wired engine on `:8080`, the orchestrator on `:8090`, the MCP server on `:8000`, the
real-Claude agent host on `:8091` (only if `ANTHROPIC_API_KEY` is set — server-side only), and
Mission Control on `:9000`. Then **DEMO / LIVE·engine / LIVE·saga** and **YOU / CLAUDE** all work
against that one bring-up — no script-juggling, no port clash. Stop it with `make demo-down` (infra is
left up; `make down` stops the stack).

This works because the saga's Redpanda-wired engine is a strict **superset** of the walking-skeleton's
Postgres-only one: LIVE·engine just calls `/v1` directly and doesn't care that the outbox is also
publishing, so a single engine serves every mode. The four launchers share one bring-up library
(`scripts/demo-lib.sh`); the per-slice scripts below stay for the minimal, fast, single-purpose runs.

**The engine-direct vs saga distinction matters.** LIVE·engine calls the engine's `/v1` command
surface directly — a real, governed boundary (ADR-PC-029 lists the edge, MCP, and saga as
co-callers of it), and it's faithful to the **MCP-operator** framing this demo uses. But it
deliberately does *not* route through the **constitution saga**: the engine decides-and-appends, so
there's no settlement leg, no approval gate, no compensation. LIVE·saga is the other half — it opens
the deposit the way the production flow intends, edge → saga orchestrates → engine, so you see the
saga's reversible money leg actually fire against the Core-ACL settlement target. (See `babelstone-f0ic.11`.)

### DEMO mode — zero setup

Just open the file:

```
open docs/demo/mission-control/index.html
```

Everything works offline. The numbers are computed with the engine's own method
(ACT/360 simple interest, 28% withholding), so a €10,000 / 12-month / 3% deposit matures to
**€10,219.00** — the same figure the real engine produces.

### LIVE·engine mode — drives the engine directly

The engine has **no CORS**, so a browser can't call it cross-origin. `serve.py` solves this by
serving the UI and the engine's `/v1/*` API from one origin (a reverse proxy) — no CORS, no
preflight.

```bash
# 1. start the engine + a constituted smoke-test deposit (Postgres-only walking skeleton)
scripts/demo-mcp.sh up            # engine comes up on http://localhost:8080

# 2. start Mission Control (stdlib only — no pip install)
python3 docs/demo/mission-control/serve.py

# 3. open http://localhost:9000  → flip the Mode toggle to LIVE·engine
```

The connection LED (top-right) turns green when the engine is reachable. Override defaults
with env vars: `MC_PORT` (default 9000), `ENGINE_URL` (default `http://localhost:8080`).

### LIVE·saga mode — drives the constitution saga

This is the **intended** way a deposit is opened: a client hits the orchestrator's **edge front
door**, which *starts* the constitution saga (ADR-IC-003 / Document 05) and returns `202 Accepted`
with a `process_id` and an **SSE stream** that follows the saga to a terminal state. The saga decides
its commands; the dispatcher (ADR-PC-029) delivers the reversible settlement leg
(`ReserveAccountBalance`) to the **Core-ACL stub** over idempotent HTTP, and `ActivateDeposit` lands a
real deposit in the **engine**, whose `DepositConstituted` event flows back over the bus to complete
the saga. Nothing rides the durable bus but events.

```bash
# 1. bring up the full saga path: Postgres + Redpanda + Core-ACL stub + the ENGINE + the orchestrator
scripts/demo-saga.sh up           # engine on :8080, orchestrator edge on http://localhost:8090

# 2. start Mission Control (same proxy; it forwards /api/v1/* to the orchestrator and /v1/* to the engine)
python3 docs/demo/mission-control/serve.py

# 3. open http://localhost:9000  → flip the Mode toggle to LIVE·saga → Constitute deposit
```

You'll watch the saga walk out of the edge: `ConstitutionRequested` → `PARALLEL_VALIDATION`
(`ReserveAccountBalance` dispatched to the Core-ACL stub) → `VALIDATIONS_COMPLETE` → **`APPROVED`**,
where the **irreversible `ConfirmDebit`** fires and `ActivateDeposit` is dispatched to the engine →
**`COMPLETED`**, when the engine's real `DepositConstituted` event arrives back over the bus. The
position column tracks the milestones (Requested → Validating → **Approved & debited** → Constituted).

**How far it goes — all the way to terminal completion.** With the **result-event bridge** (bd
`babelstone-t7o3.8`) the orchestrator synthesizes each settlement result event from the command's
delivery outcome and self-advances the saga to **`APPROVED`** — the reversible reserve *and* the
irreversible debit both fire. From there `ActivateDeposit` lands a **real, de-settled deposit** in the
engine; the engine appends `DepositConstituted` and its catalog-gated outbox relay publishes that fact
onto the `term_deposit` family topic; the orchestrator's consume loop reads it off Redpanda,
correlates `ce_subject → process_id`, and advances **`APPROVED → COMPLETED`** (bd
`babelstone-t7o3.11`). This is the **ADR-PC-029 slot-2** contract working end to end: the saga advances
on the engine's real **event**, never on the `ActivateDeposit` HTTP `2xx`. At `COMPLETED` the bank
genuinely holds the deposit — `GET /v1/deposits/{process_id}` on the engine returns it `Active`.

**The refusal branch reaches a terminal state.** Tick **force insufficient funds** (a LIVE·saga
affordance) and the source account is flagged `insufficient`, so the Core-ACL stub `422`s the
reservation: the saga **fails closed** — `PreconditionRefused` → terminal `DEPOSIT_CONSTITUTION_FAILED`,
before any irreversible effect, nothing committed. That's the compensation/fail-closed beat, end to end.

`serve.py` plays the **gateway** for this mode: it injects the `X-Client-Id` the edge's per-process
authz binds ownership to (the claim Kong would propagate, ADR-IC-006 §P4) — the browser's
`EventSource` can't set headers, so injecting it at the proxy is what lets the SSE stream's
ownership check pass. Override defaults with env vars: `ORCHESTRATOR_URL` (default
`http://localhost:8090`), `DEMO_CLIENT_ID` (default `CLI-DEMO-0001`, an opaque reference — never PII).

Other lifecycle actions (mature, coupons, retry, terminate) are disabled in LIVE·saga: the saga
covers **constitution** only today. Use DEMO or LIVE·engine to drive those.

**Smoke-tested 2026-06-15** against a real engine + orchestrator (`scripts/demo-saga.sh up` →
`serve.py`): the happy path walks all the way to terminal `COMPLETED` (reserve **and** the irreversible
debit both hit the Core-ACL stub — `POST /v1/reservations` + `POST /v1/debits` — then the engine's real
`DepositConstituted` carries the saga `APPROVED → COMPLETED`, and the engine holds the deposit `Active`
at `GET /v1/deposits/{process_id}`), and the refusal path reaches terminal
`DEPOSIT_CONSTITUTION_FAILED` before approval — both confirmed in the browser and the orchestrator DB.
(A pre-existing Postgres volume is fine — the orchestrator uses its own `babelstone_orchestrator`
database, distinct from the engine's `babelstone`, so there's no `inbox`-table collision with
`demo-mcp.sh`.)

## The engine-CA demo path — a real `conta à ordem` a deposit and a loan move against

**In plain English.** So far the demo opens a deposit and settles its cash leg against a stubbed
external core. This path shows the newer, fuller story: the customer holds a **real current account
inside the engine itself** — their `conta à ordem` — and a term deposit *and* a personal loan
**fund from** and **pay into** that one account. You open the account, seed it with some cash, then
watch its balance move as each product settles: constituting a deposit takes money *out* of the CA,
maturing it puts money *back*; originating a loan puts the principal *in*, paying an installment
takes money *out*. Mission Control shows the account balance live, moving in lockstep with the
settlement saga. It is the runnable proof of the account-identity design in
[feature-design-money-movement-settlement.md §2A](../../product-management/product_concepts/feature-design-money-movement-settlement.md#2a-the-account-identity-model--the-customers-persistent-conta-à-ordem-on-the-settlement-leg)
(the engine-CA settlement decision is [ADR-PC-043](../../product-management/product_concepts/adrs/ADR-PC-043-intra-engine-settlement-counterparty.md);
the current-account family is [ADR-PC-037](../../product-management/product_concepts/adrs/ADR-PC-037-current-account-family.md)).

**The loop, move by move.** Each product move settles against the *same* engine-owned current
account (its persistent `Movement.AccountRef`), routed `engine-ca` (`ce_settlementtarget = engine-ca`)
so it lands on the engine's own CA writer, not the legacy Core-ACL stub:

| Product move | What happens to the `conta à ordem` | On the wire |
| --- | --- | --- |
| **Constitute a term deposit** (fund it from the CA) | **Debit** — a reversible **hold**, then an irreversible **capture** | funds-gated `Reserve → Confirm`; `HoldPlaced → HoldCaptured` |
| **Deposit matures** | **Credit** — the payout lands back on the CA | confirmation-gated `ConfirmCredit`; `AccountCredited` |
| **Originate a personal loan** (disburse) | **Credit** — the principal is credited to the borrower's CA | confirmation-gated `ConfirmCredit`; `AccountCredited` |
| **Pay a loan installment** (collect) | **Debit** — a **hold**, then a **capture** | funds-gated `Reserve → Confirm`; `HoldPlaced → HoldCaptured` |

Every leg is a `Debit`/`Credit` *relative to the customer's account*: a loan disbursement is a
**Credit** because the borrower's account *gains* value. What makes this real (vs. the legacy-DDA
path) is three pieces the epic `babelstone-u79p` wired: the families now **emit the engine-ca
target** on their CA-bound legs (before, every leg defaulted to legacy-DDA); the leg carries the
**real customer `account_ref`** instead of a per-saga `ACCT-{processId}` placeholder; and the
**engine serves the settlement routes itself** (`/v1/reservations`, `/v1/debits`, `/v1/credits`),
mapping each to its own current-account authorize / capture / credit.

### The `conta à ordem` visualization — two balances and active holds

Mission Control gets a **persistent conta-a-ordem panel** sourced from `GET /v1/accounts/{id}`. It
shows a **two-balance meter** and the **active holds**:

- **Available** vs **Booked** — the ADR-PC-033 split: `available balance = booked (accounting)
  balance − Σ active holds`. When a Debit leg places a **hold**, the *available* meter drops
  immediately while *booked* stays put; when the hold **captures**, *booked* drops and the hold
  clears. A Credit lands straight on *booked*. You see the reversible-then-irreversible beat as a
  gap that opens between the two bars and then closes.
- **Active holds** — a list of the outstanding reservations (the `HoldPlaced` that have not yet
  `HoldCaptured`/`HoldExpired`), each shrinking the available balance until it resolves.
- A **movement strip** (debit/credit history) sourced from the movement-history read surface
  (`GET /v1/accounts/{id}/movements`, ADR-PC-032) — a real posted-movement list, not one
  reconstructed from actions.

The panel updates **in lockstep with the LIVE·saga pane**: as the settlement saga walks
`HoldPlaced → HoldCaptured` (or a credit lands), the account meters and the ledger feed move
together, so the money story and the saga story are one screen.

### Mode behaviour for the engine-CA loop

The engine-CA path behaves differently across the three modes, the same way the rest of the demo does:

| | DEMO | LIVE·engine | LIVE·saga |
| --- | --- | --- | --- |
| The `conta à ordem` panel | illustrative — computed in-browser, deterministic (available/booked/holds move by the demo's own arithmetic, labelled illustrative) | **real** — the account, holds, and movements come straight from `GET /v1/accounts/{id}` on the engine | **real** — the same engine account, moving as the **saga** drives each settlement leg |
| The settlement leg | none (no backend) | the engine decides-and-appends directly — no saga, so no reversible-hold-then-capture beat; a Credit/Debit lands in one step against the CA | the **full** intended path: the saga fires `Reserve → Confirm` (Debit) or `ConfirmCredit` (Credit), so `HoldPlaced → HoldCaptured` and the credit landing are the *saga's* real effects against the engine CA |
| What it proves | the balance arithmetic on a stage with no network | the engine's current-account writer is genuinely real | the **whole loop** — a TD/loan cash leg settling `engine-ca` against a customer's `conta à ordem` end to end, holds and captures and all |

The **reversible-hold-then-capture** beat (a Debit's `HoldPlaced` before its `HoldCaptured`) is
only fully visible in **LIVE·saga**, where the saga owns the two-phase `Reserve → Confirm` — in
LIVE·engine the engine lands the leg in one step, and in DEMO the beat is illustrated. Legacy-DDA
settlement is unchanged in every mode: a deposit that does *not* target `engine-ca` still settles
against the Core-ACL stub exactly as the [LIVE·saga section](#livesaga-mode--drives-the-constitution-saga)
above describes — the engine-CA loop is a purely additive path, not a replacement.

## The demo beats (what to click)

1. **Constitute deposit** — a `DepositConstituted` event appears in the ledger; the position
   folds out on the right (ACTIVE, principal, rate from the rate sheet, maturity date).
2. **Retry — same key** — the wow. It re-sends with the *same* `Idempotency-Key`; the engine
   replays the original result. The ledger flashes but **no second event is appended** —
   "duplicate caught, same commit." This is what makes the bank safe to retry (and safe for an
   AI to drive). *(ADR-PC-029.)*
3. **Mature deposit** — `InterestAccrued → WithholdingApplied → DepositMatured` stream in, and
   the position counts up: gross €304.17, −€85.17 tax (28%), **€10,219.00** payout.
4. **Reset** — clear and run it again.

## The AI-native framing

Flip the **Operator** toggle from `YOU` to `CLAUDE` and an **instruction bar** appears in the
bottom drawer's **MCP tool surface** tab. Type what you want — *"open a €10,000 12-month deposit
and mature it"* — and hit **Run**: the browser POSTs it to the real-Claude **agent host**, which
calls Claude with the babelstone deposit tools bound, lets the model decide and invoke them
through the **real** secured MCP server (`mcp-server/`), and streams the model's narration + the
**actual** tool calls and results back into the console. The living ledger and the position pane
fold out of those real results — you watch the deposit materialise from the model's actions.

This is live, not a mockup. Bring up the whole path with one command:

```bash
ANTHROPIC_API_KEY=sk-ant-… make demo-agent   # engine + MCP server + agent host + this UI
```

The `ANTHROPIC_API_KEY` lives **server-side only** in the agent host — never the browser, never
committed. If the key (or the agent host) is missing, `CLAUDE` mode **degrades to an
illustrative narration** of what the model would do — clearly labelled, never passed off as live
traffic (the same degrade-to-illustrative posture the Telemetry tab uses when Grafana Tempo is
unreachable). DEMO mode and the manual `YOU`-driven buttons need none of this.

## The observability beat (OpenTelemetry)

Flip the **Telemetry** toggle to `ON` (or pick the **Telemetry · OpenTelemetry** tab in the
bottom drawer). After each operation it renders the **real spans** the engine emits, as a
waterfall:

- `deposit.constituted` → `accrual.computed` → `withholding.applied`, on ActivitySource
  `Babelstone.Engine`.
- Each span carries its **real, structural-only** attributes — `babelstone.partition_key`,
  `babelstone.product_code`, `babelstone.interest_cents`, `babelstone.tax_cents` — and the
  panel calls out **✓ No PII on spans** (operational tier only, ADR-IC-007 §P4 / ADR-PC-004 §P2).
- Below: the engine's real SLI metric names (`outbox_publish_lag_seconds`,
  `outbox_publish_latency_seconds`, `inbox_handled_total`).

This panel is the **map**; Grafana Tempo is the **territory** — and in **LIVE·engine** the tab pulls
the **real trace** from Tempo (bd `babelstone-f0ic.9`):

- The engine returns the active trace id on every response as **`X-Trace-Id`** (bd `babelstone-2dex`),
  and its inbound request is now a server span, so the `deposit.*` spans nest under it as one trace.
- After an operation the tab fetches `GET /tempo/api/traces/{id}` — `serve.py` proxies Grafana
  Tempo's query API (same-origin, same no-CORS reason as `/v1`) — and renders the **actual** spans
  with their real timings and `babelstone.*` attributes, labelled **✓ REAL · Grafana Tempo**. Open
  the same trace in Grafana on `localhost:3000`.
- Tempo has a few seconds of ingestion lag, so the fetch **polls** and shows "fetching the real
  trace…" until it lands; if the LGTM stack isn't up it **degrades** to the illustrative waterfall
  and says so. In **DEMO** the waterfall stays illustrative (deterministic, computed in-browser); in
  **LIVE·saga** it's illustrative too — the browser drives the orchestrator edge, not the engine
  directly, so no `X-Trace-Id` is surfaced to fetch the trace by (the engine's spans *do* reach Tempo
  on the saga path; the UI just has no id to correlate them).

**Bring-up for real traces** — the telemetry backend must be running so the engine's spans reach
Tempo:

```bash
docker compose -f infra/compose.yaml up -d otel-collector grafana-lgtm   # collector → Tempo (3200 exposed)
scripts/demo-mcp.sh up                                                    # the engine exports to the collector (:4317)
python3 docs/demo/mission-control/serve.py                               # proxies /tempo/* to Tempo
# open http://localhost:9000 → LIVE·engine → Telemetry ON → run an operation
```

Known gaps (engine-side, not demo bugs): the orchestrator isn't OTel-wired yet, so saga spans
don't export; and W3C traceparent propagation across Redpanda is planned — so the cross-service
saga trace is aspirational, while the per-deposit engine trace is real (and now shown from Tempo).

## How it maps to the real contract

**LIVE·engine** speaks the engine's actual API (verified against `engine/src/Babelstone.Engine.Api`):
snake_case JSON, integer cents, a required `Idempotency-Key` UUID on constitute, and
`If-Min-Sequence` on the follow-up read for read-your-writes. Endpoints used:
`POST /v1/deposits`, `GET /v1/deposits/{id}`, `POST /v1/deposits/{id}/maturity`.

**LIVE·saga** speaks the orchestrator edge's actual API (verified against
`orchestrator/src/Babelstone.Orchestrator/Edge`): `POST /api/v1/deposits/constitute` →
`202 Accepted` with `{deposit_id, process_id, status, stream_url}`, then a long-lived SSE
`GET /api/v1/processes/{id}/stream` emitting `event: state` frames carrying the **structural** saga
state only (`{process_id, state, version, terminal}` — never PII, ADR-PC-004 §P2). The `X-Client-Id`
ownership header is injected by `serve.py` (the gateway stand-in).

## Product variants

The **Product** selector wires the three real engine products; the rate (TAN) shown comes from
the rate sheet, not user input:

- **12m · interest at maturity** (`dpz_pt_12m_juros_venc`, 3.00%) — the canonical flow:
  constitute → mature pays gross − 28% withholding + principal.
- **12m · monthly coupons** (`dpz_pt_12m_juros_mensal`, 3.25%, PERIODIC) — a **Pay coupon**
  button appears; each click pays one coupon via the real `POST /v1/deposits/{id}/interest`
  endpoint, emitting an `InterestPaid` event with its own flow-by-flow 28% withholding. The
  position tracks coupons paid (e.g. 3 / 12) and net interest to date; maturity settles the
  final coupon with the principal.
- **12m · interest in advance** (`dpz_pt_12m_juros_antecip`, 3.00%, ADVANCE) — the full-term
  interest is paid **at t=0**: constitution emits `DepositConstituted` + an upfront
  `InterestPaid`, and maturity returns principal only.

**Early termination** is a **DEMO-only** action (the **Terminate early** button, disabled in both LIVE modes).
The engine has the decider — penalty bands, basis, payout floor — but **no HTTP endpoint yet**, so
this can't run against the real engine. The demo shows an illustrative `DepositTerminatedEarly`
(≈50% term elapsed, a 50%-of-accrued penalty band) and is labelled as such.

## LIVE·engine mode — verified

Smoke-tested against a real engine (`scripts/demo-mcp.sh up` → `serve.py`) on 2026-06-14:

- **All three variants work end-to-end.** AT_MATURITY: constitute (`POST 201` with `Idempotency-Key`),
  read-your-writes (`GET` with `If-Min-Sequence`), idempotent retry (same key → replays the same
  commit, no second event), mature (`POST …/maturity 200`) → canonical €10,219.00. PERIODIC: coupons
  via `POST …/interest` (each a real `InterestPaid` with its own withholding). ADVANCE: full-term
  interest paid upfront at constitution. Confirmed in the UI and the proxy access log.
- **`demo-mcp.sh` now prices all three products** (`venc` 300bps, `mensais` 325bps, `antecip` 300bps)
  so the engine no longer 422s the non-AT_MATURITY variants. (Production pricing belongs in the
  regulatory pack; this is the dev-fixture sheet the script deploys.)
- **DEMO vs LIVE·engine coupon nuance:** DEMO splits the term into even coupon windows; the engine
  computes each coupon over the real calendar month (ACT/360), so LIVE·engine per-coupon amounts
  differ slightly between months (e.g. €19.50 then €20.15). LIVE·engine is the engine's truth; DEMO
  is illustrative.

**Bring-up gotcha (pre-existing Postgres volume):** the forward-only migrations aren't individually
re-runnable, so the shared applier (`scripts/demo-lib.sh`) guards on the last *table-creating* migration
(`command_dedup`, 0015 — the trailing 0016 only adds a column via an idempotent `ALTER`): a
fully-migrated volume is skipped cleanly, a clean volume gets the full apply, and
a *partially*-migrated volume (has `events` but not `command_dedup` — e.g. seeded by an older
`demo-mcp.sh` that stopped at `0004`) now **fails loud** with the wipe instruction instead of silently
skipping into a runtime 500. To recover, wipe the volume and let migrations apply fresh:
`docker compose -f infra/compose.yaml down -v`, then re-run. (Older note: `demo-mcp.sh` used to guard
on the *first* table `events` and silently skip → constitute 500s; that bug is fixed by this unified guard.)

## Notes / scope

- The full lifecycle (AT_MATURITY, PERIODIC coupons, ADVANCE) is wired in **DEMO and LIVE·engine**;
  LIVE·saga covers **constitution** only (the saga's current scope). In LIVE·engine the PERIODIC/
  ADVANCE paths reconstruct ledger cards from the maturity/coupon response deltas (the engine exposes
  no per-event feed), so the figures are the engine's and the card split is presentational.
- Early termination is DEMO-only until the engine grows a termination endpoint.
- In **LIVE·saga** mode the orchestrator exposes no per-event feed either, so the ledger is
  reconstructed from the SSE **state** frames + the dispatched commands — the saga states are the
  orchestrator's truth, the card breakdown is presentational.
- Design tokens are shared with the deck (`docs/demo/index.html`) for one visual language.
