# Tutorial 01 — Constitute a term deposit end-to-end

**Persona:** [Agent-channel consumer](../../reading-paths/README.md) (and anyone who wants to see the engine actually run).
**You will finish with:** a deposit constituted, read back, and matured — through the engine's HTTP boundary and then through the [MCP](../../reference/glossary.md#mcp-model-context-protocol) tool surface — with the canonical payout numbers reproduced on your laptop.

Prerequisite: [Tutorial 00](./00-bring-up-the-dev-stack.md) (toolchain installed; Docker running). You do **not** need the full stack up for this one — the demo brings up only PostgreSQL. For *why* the write path is a saga and what each step settles, this tutorial links the [constitution saga walkthrough](../../integration_concepts/05-constitution-saga-walkthrough.md); it does not re-explain the design ([guides invariant](../README.md)).

## Step 1 — Free the ports the demo needs

The demo binds the engine on `8080` and the MCP server on `8000`. Those collide with the full stack's Redpanda Console and Kong proxy, so if you ran `make up` in Tutorial 00, stop it first:

```bash
make down
```

(The demo needs PostgreSQL only — `make down` keeps your data volume.)

## Step 2 — Run the walking-skeleton demo

One command drives the whole slice and leaves the engine + MCP running:

```bash
make demo-mcp
```

`make demo-mcp` runs `scripts/demo-mcp.sh up`. It chains six steps; you will see a labelled `▶ N/6` banner and a `✓` per assertion. In order, it:

1. **Starts PostgreSQL only** — the engine's sole dependency (no Redpanda needed).
2. **Applies the event-store migrations** — the forward-only SQL under `engine/src/Babelstone.EventStore.Migrations/Sql` (events, outbox, snapshots, rate_sheets).
3. **Deploys the rate sheet via the real deploy API** and asserts its `201` / `200` idempotent-replay / `409` forward-only-conflict semantics — the validated seam, not a raw `INSERT`. The behaviour is decided in [ADR-PC-008](../../product_concepts/adrs/ADR-PC-008-rate-sheet-storage-and-deploy-api.md); the [rate sheet](../../reference/glossary.md#rate-sheet) glossary entry is the one-liner.
4. **Starts the engine command/query host** on `http://localhost:8080`.
5. **Drives constitute → read → mature** over HTTP and asserts the canonical numbers (Step 4 below).
6. **Starts the Python MCP server** in front of the engine and prints the Claude Code wiring.

This is the same sequence a person would run by hand; the script just makes it one command. The end-to-end run is also what proves the [decider](../../reference/glossary.md#decider) and [folds](../../reference/glossary.md#fold) reproduce the same result every time.

## Step 3 — What the demo constitutes

The scenario is fixed (it is 1:1 with the engine's integration test). The request the demo POSTs to the engine is:

```json
{"principal_cents":1000000,"product_id":"dpz_pt_12m_juros_venc","role":"standard","term_days":365,"start_date":"2026-01-15","interest_variant":"AT_MATURITY","auto_renewal_policy":"NONE","funding_account":"PT50-DDA-001"}
```

That is one million [cents](../../reference/glossary.md#money-cents) (€10,000) into a 12-month PT term deposit paying interest at maturity. The field-by-field contract for this call lives in the generated reference: [`constitute_deposit`](../../reference/mcp-tools/constitute_deposit.md).

## Step 4 — The canonical numbers you should see

After constituting, the demo reads the active position and asserts the rate it resolved, then matures it and asserts the payout. Expected `✓` lines:

```
  ✓ tan_basis_points = 300
  ✓ rate_sheet_version_id = pt-deposits-2026.1
  ✓ lifecycle = Active
  ...
  ✓ accrued_gross_interest_cents = 30417
  ✓ withholding_to_date_cents = 8517
  ✓ net_interest_cents = 21900
  ✓ total_payout_cents = 1021900
  ✓ lifecycle = Matured
```

So: gross interest **30417** cents, [withholding](../../reference/glossary.md#withholding) **8517**, net **21900**, total payout **1021900** (principal + net). These are the load-bearing numbers — if your run prints different figures, the demo fails loudly rather than continuing. The maths behind them (day count, accrual, withholding) is defined once in the [financial mathematics reference](../../financial_concepts/banking_products_financial_mathematics.md); the [`mature_deposit`](../../reference/mcp-tools/mature_deposit.md) page gives the response contract.

## Step 5 — Drive the same loop through MCP

When the demo finishes it prints the wiring to attach the running MCP server to Claude Code. Run those two commands as printed:

```bash
claude mcp add --transport http babelstone-deposits http://127.0.0.1:8000/mcp
claude mcp list        # babelstone-deposits should show ✓ connected
```

Then, in a Claude Code session, ask it to call the tools — `constitute_deposit`, then [`get_deposit`](../../reference/mcp-tools/get_deposit.md), then `mature_deposit` — with the demo's printed prompts. The whole constitute → read → mature loop now runs through MCP tools with no curl. The tool catalogue is the generated [MCP tools reference](../../reference/mcp-tools/README.md); the channel strategy behind it is [chat-agent channel strategy](../../integration_concepts/11-chat-agent-channel-strategy.md).

## Step 6 — Tear down

```bash
make demo-mcp-down
```

This stops the engine + MCP processes the demo started. PostgreSQL is left running (use `make down` to stop the container too).

## What just happened

You reproduced a full write path — constitute, project, mature — end-to-end, twice: once over the engine's HTTP boundary and once through the agent channel. To understand the saga that orchestrates it (and the compensation when a step fails), read the [constitution saga walkthrough](../../integration_concepts/05-constitution-saga-walkthrough.md). The [constitution](../../reference/glossary.md#constitution) glossary entry is the one-line anchor.

- **Next:** [Tutorial 02 — Author and load a PT pack](./02-author-and-load-a-pt-pack.md) opens up the [pack](../../reference/glossary.md#pack-regulatory-pack) that supplied this deposit's primitives.
- **All tutorials:** the [tutorials index](./README.md) · the [guides root](../README.md).
