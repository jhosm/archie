# Babelstone — Mission Control

A live, single-screen demo UI for the babelstone deposit engine. It's the wow the deck
hands off to (slide 5, "Watch this"): you constitute a deposit, watch immutable events
stream into the **living ledger**, and see the **position** fold out of them — then mature it
and watch the interest accrue, 28% withholding apply, and the payout resolve.

## In plain English

This is a one-page web app that shows the bank working. The left column is the controls, the
middle is the event stream (every change is a permanent, numbered fact), and the right is the
deposit's current state — which is *computed* from those events, never stored. It runs in two
modes: **DEMO** (no backend, perfectly deterministic, safe for a stage) and **LIVE** (driving
the real engine). It shares the deck's exact look so the two feel like one product.

## The two modes

| | DEMO | LIVE |
| --- | --- | --- |
| Backend needed | none | the engine on `:8080` + `serve.py` |
| Determinism | total — same every time | real engine output |
| Use it for | the stage, a laptop with no network, a leave-behind | proving it's genuinely real |
| The money math | computed in-browser the engine's way (ACT/360, 28%) | computed by the engine kernel |

Flip between them with the **Mode** toggle, top-right. DEMO is the default.

### DEMO mode — zero setup

Just open the file:

```
open docs/demo/mission-control/index.html
```

Everything works offline. The numbers are computed with the engine's own method
(ACT/360 simple interest, 28% withholding), so a €10,000 / 12-month / 3% deposit matures to
**€10,219.00** — the same figure the real engine produces.

### LIVE mode — drives the real engine

The engine has **no CORS**, so a browser can't call it cross-origin. `serve.py` solves this by
serving the UI and the engine's `/v1/*` API from one origin (a reverse proxy) — no CORS, no
preflight.

```bash
# 1. start the engine + a constituted smoke-test deposit (Postgres-only walking skeleton)
scripts/demo-mcp.sh up            # engine comes up on http://localhost:8080

# 2. start Mission Control (stdlib only — no pip install)
python3 docs/demo/mission-control/serve.py

# 3. open http://localhost:9000  → flip the Mode toggle to LIVE
```

The connection LED (top-right) turns green when the engine is reachable. Override defaults
with env vars: `MC_PORT` (default 9000), `ENGINE_URL` (default `http://localhost:8080`).

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

This panel is the **map**; Grafana Tempo is the **territory**. In LIVE mode the same spans
export through the OTel Collector to Grafana LGTM (`make up`, then Grafana on `localhost:3000`)
— search Tempo by `babelstone.partition_key`. The in-app waterfall is a faithful, on-brand
visualization of the instrumentation contract (deterministic in DEMO); it is *not* pulled from
Tempo (see the epic for that follow-up). Honest framing, same as the MCP tab.

Known gaps (engine-side, not demo bugs): the orchestrator isn't OTel-wired yet, so saga spans
don't export; and W3C traceparent propagation across Redpanda is planned — so the cross-service
saga trace is aspirational, while the per-deposit 3-span trace is real.

## How it maps to the real contract

LIVE mode speaks the engine's actual API (verified against `engine/src/Babelstone.Engine.Api`):
snake_case JSON, integer cents, a required `Idempotency-Key` UUID on constitute, and
`If-Min-Sequence` on the follow-up read for read-your-writes. Endpoints used:
`POST /v1/deposits`, `GET /v1/deposits/{id}`, `POST /v1/deposits/{id}/maturity`.

## Product variants

The **Product** selector wires the three real engine products; the rate (TAN) shown comes from
the rate sheet, not user input:

- **12m · interest at maturity** (`dpz_pt_12m_juros_venc`, 3.00%) — the canonical flow:
  constitute → mature pays gross − 28% withholding + principal.
- **12m · monthly coupons** (`dpz_pt_12m_juros_mensais`, 3.25%, PERIODIC) — a **Pay coupon**
  button appears; each click pays one coupon via the real `POST /v1/deposits/{id}/interest`
  endpoint, emitting an `InterestPaid` event with its own flow-by-flow 28% withholding. The
  position tracks coupons paid (e.g. 3 / 12) and net interest to date; maturity settles the
  final coupon with the principal.
- **12m · interest in advance** (`dpz_pt_12m_juros_antecip`, 3.00%, ADVANCE) — the full-term
  interest is paid **at t=0**: constitution emits `DepositConstituted` + an upfront
  `InterestPaid`, and maturity returns principal only.

**Early termination** is a **DEMO-only** action (the **Terminate early** button, disabled in LIVE).
The engine has the decider — penalty bands, basis, payout floor — but **no HTTP endpoint yet**, so
this can't run against the real engine. The demo shows an illustrative `DepositTerminatedEarly`
(≈50% term elapsed, a 50%-of-accrued penalty band) and is labelled as such.

## LIVE mode — verified

Smoke-tested against a real engine (`scripts/demo-mcp.sh up` → `serve.py`) on 2026-06-14:

- **All three variants work end-to-end.** AT_MATURITY: constitute (`POST 201` with `Idempotency-Key`),
  read-your-writes (`GET` with `If-Min-Sequence`), idempotent retry (same key → replays the same
  commit, no second event), mature (`POST …/maturity 200`) → canonical €10,219.00. PERIODIC: coupons
  via `POST …/interest` (each a real `InterestPaid` with its own withholding). ADVANCE: full-term
  interest paid upfront at constitution. Confirmed in the UI and the proxy access log.
- **`demo-mcp.sh` now prices all three products** (`venc` 300bps, `mensais` 325bps, `antecip` 300bps)
  so the engine no longer 422s the non-AT_MATURITY variants. (Production pricing belongs in the
  regulatory pack; this is the dev-fixture sheet the script deploys.)
- **DEMO vs LIVE coupon nuance:** DEMO splits the term into even coupon windows; the engine computes
  each coupon over the real calendar month (ACT/360), so LIVE per-coupon amounts differ slightly
  between months (e.g. €19.50 then €20.15). LIVE is the engine's truth; DEMO is illustrative.

**Bring-up gotcha:** on a *pre-existing* Postgres volume, `scripts/demo-mcp.sh` skips all migrations
when the `events` table already exists, leaving newer tables (`command_dedup`, …) absent → constitute
500s. Fix for a clean run: wipe the volume first (`docker compose -f infra/compose.yaml down -v`) so
migrations apply fresh, then `up`. (The step-5 `Idempotency-Key` staleness is fixed on this branch;
the migration-skip remains — tracked as a separate bug.)

## Notes / scope

- AT_MATURITY, PERIODIC (coupons), and ADVANCE are wired in both modes; PERIODIC/ADVANCE LIVE
  paths reconstruct ledger cards from the maturity/coupon response deltas (the engine exposes no
  per-event feed), so the figures are the engine's and the card split is presentational.
- Early termination is DEMO-only until the engine grows a termination endpoint.
- In LIVE mode the engine doesn't expose a per-event feed, so the `InterestAccrued` /
  `WithholdingApplied` / `DepositMatured` cards are reconstructed from the maturity response
  deltas — the figures are the engine's, the card breakdown is presentational.
- Design tokens are shared with the deck (`docs/demo/index.html`) for one visual language.
