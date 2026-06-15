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
| Backend needed | none | the engine on `:8080` + `serve.py` | the orchestrator on `:8090` + Core-ACL stub + `serve.py` |
| Bring-up | `open index.html` | `scripts/demo-mcp.sh up` | `scripts/demo-saga.sh up` |
| What it proves | the math, on a stage with no network | the engine kernel is genuinely real | the **intended** command-plane: edge, saga, dispatcher, settlement |
| Contract | — | `POST /v1/deposits` (ADR-PC-029) | `POST /api/v1/deposits/constitute` → 202 + SSE (ADR-IC-006 §P4) |

Flip between them with the **Mode** toggle, top-right. DEMO is the default.

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
(`ReserveAccountBalance`) to the **Core-ACL stub** over idempotent HTTP. Nothing rides the durable
bus but events.

```bash
# 1. bring up the saga path: Postgres + Redpanda + Core-ACL stub + the orchestrator host
scripts/demo-saga.sh up           # orchestrator edge comes up on http://localhost:8090

# 2. start Mission Control (same proxy; it also forwards /api/v1/* to the orchestrator)
python3 docs/demo/mission-control/serve.py

# 3. open http://localhost:9000  → flip the Mode toggle to LIVE·saga → Constitute deposit
```

You'll watch the saga walk out of the edge: `ConstitutionRequested` → `PARALLEL_VALIDATION`
(`ReserveAccountBalance` dispatched to the Core-ACL stub) → `VALIDATIONS_COMPLETE` → **`APPROVED`**,
where the **irreversible `ConfirmDebit`** fires and `ActivateDeposit` is dispatched to the engine.
The position column tracks the milestones (Requested → Validating → **Approved & debited** →
Constituted).

**How far it goes — and the one honest gap.** With the **result-event bridge** (bd
`babelstone-t7o3.8`, now merged) the orchestrator synthesizes each result event from the command's
delivery outcome and self-advances the saga, so the happy path walks all the way to **`APPROVED`** —
the reversible reserve *and* the irreversible debit both fire. It does **not** reach `COMPLETED`:
`ActivateDeposit`-applied is deliberately *not* synthesized (ADR-PC-029 slot 2 — the saga advances on
the engine's real `DepositConstituted`, not the command's HTTP 2xx), and the engine→saga completion
correlation that would carry it `APPROVED → COMPLETED` is a separate, still-unbuilt bridge. So the
demo stops at `APPROVED`; `ActivateDeposit` is dispatched to the engine on `:8080` — start the engine
(`scripts/demo-mcp.sh up`) for it to land a **real deposit**. Same honest framing as the MCP and
Telemetry tabs: show what's real, name what's aspirational.

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

**Smoke-tested 2026-06-14** against a real orchestrator (`scripts/demo-saga.sh up` → `serve.py`):
the happy path walks to `APPROVED` (reserve **and** the irreversible debit both hit the Core-ACL stub
— `POST /v1/reservations` + `POST /v1/debits`), and the refusal path reaches terminal
`DEPOSIT_CONSTITUTION_FAILED` — both confirmed in the browser and the orchestrator DB. (A pre-existing
Postgres volume is fine — the orchestrator uses its own `babelstone_orchestrator` database, distinct
from the engine's, so there's no `inbox`-table collision with `demo-mcp.sh`.)

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

Flip the **Operator** toggle from `YOU` to `CLAUDE`. The same buttons now also surface the
**MCP tool call** behind each action (`constitute_deposit(...)`, `mature_deposit(...)`) in the
bottom drawer's **MCP tool surface** tab — making concrete that every operation here is a
governed MCP tool an LLM can call. (The real MCP server is `mcp-server/`; this is the visual of
what it exposes — labelled illustrative, not live model traffic.)

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
  **LIVE·saga** it's illustrative too (the engine isn't on the request path).

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

**Bring-up gotcha:** on a *pre-existing* Postgres volume, `scripts/demo-mcp.sh` skips all migrations
when the `events` table already exists, leaving newer tables (`command_dedup`, …) absent → constitute
500s. Fix for a clean run: wipe the volume first (`docker compose -f infra/compose.yaml down -v`) so
migrations apply fresh, then `up`. (The step-5 `Idempotency-Key` staleness is fixed on this branch;
the migration-skip remains — tracked as a separate bug.)

## Notes / scope

- The full lifecycle (AT_MATURITY, PERIODIC coupons, ADVANCE) is wired in **DEMO and LIVE·engine**;
  LIVE·saga covers **constitution** only (the saga's current scope). In LIVE·engine the PERIODIC/
  ADVANCE paths reconstruct ledger cards from the maturity/coupon response deltas (the engine exposes
  no per-event feed), so the figures are the engine's and the card split is presentational.
- Early termination is DEMO-only until the engine grows a termination endpoint.
- In **LIVE·engine** mode the engine doesn't expose a per-event feed, so the `InterestAccrued` /
  `WithholdingApplied` / `DepositMatured` cards are reconstructed from the maturity response
  deltas — the figures are the engine's, the card breakdown is presentational.
- In **LIVE·saga** mode the orchestrator exposes no per-event feed either, so the ledger is
  reconstructed from the SSE **state** frames + the dispatched commands — the saga states are the
  orchestrator's truth, the card breakdown is presentational.
- Design tokens are shared with the deck (`docs/demo/index.html`) for one visual language.
