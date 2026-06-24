# How to call a deposit tool and parse the structured result

You have a token and you have listed the tools
([discover and authenticate first](./discover-and-authenticate-to-the-mcp-server.md)).
This guide is the next step for an **agent-channel consumer**: how to call each of the
deposit tools — `constitute_deposit`, `get_deposit`, `mature_deposit`, `pay_interest`
— and how to read the **typed result** the server returns, including the money rules
and the irreversible-operation gate that will trip you up if you ignore it.

> ## ⚠ Provisional page — demo-only / walking skeleton
>
> These tools run against the engine in a **walking-skeleton** MCP dev server, not a
> production deployment. The honest split:
>
> | Tool | Status today |
> |---|---|
> | `constitute_deposit`, `get_deposit`, `mature_deposit`, `pay_interest` | **Built (dev server)** against the engine command/query host — see [`mcp-server/README.md`](../../../mcp-server/README.md). |
> | `constitute_deposit_saga`, `get_process_status` (the async saga path) | **Built but skeleton-wired** — a known producer gap means an agent cannot yet obtain a saga `process_id` purely over MCP (Document 11 "Producer-gap caveat"). |
> | Step-up SCA on irreversible money-movers | **Built as a gate**, but the OAuth/SCA edge that issues the fresh proof is demo-only — so the §P8 retry below is the *shape*, exercised in the demo, not a production flow. |
>
> The deeper agent journeys — out-of-band async completion, elicitation beyond the
> built cases — are **deliberately unwired in v1** (Document 11); this page stays on
> the built single-call surface.

---

## Money is always integer cents — never a float

Before any call: every monetary value on this surface is an **integer number of
cents**, never a float and never a major-currency amount. `1000000` is €10,000.00.
Read it as cents, render it as cents ÷ 100 for a human, and never do float arithmetic
on it. This holds for `principal_cents` on the way in and every `*_cents` field on the
way out. The full per-field truth is the
[generated MCP-tools reference](../reference/mcp-tools/README.md) — this page is the
recipe, that is the contract.

---

## Constitute a deposit (write)

`constitute_deposit` opens a deposit directly against the engine and returns the new
deposit's identity. The agent supplies the product/variant, the pricing role, the
principal in cents, the term, the start date, and the funding account; the **resolved
rate is stamped by the engine** from the active rate sheet — you never supply it
([`constitute_deposit`](../reference/mcp-tools/constitute_deposit.md)).

- `interest_variant` is one of `AT_MATURITY`, `PERIODIC`, or `ADVANCE`. For
  `PERIODIC`, also pass `payment_period_months` = `1` or `3`.
- Because `PERIODIC` triggers a form-mode confirmation, the server may **pause and ask
  the human** to confirm the periodic-coupon choice before constituting
  ([ADR-IC-010 §P8](../../product-management/integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md)
  form mode). If the human declines, the call aborts with an `McpError` and nothing is
  constituted. `AT_MATURITY`/`ADVANCE` do not trigger it.
- Requires `deposits:write`. The actor is the gateway-attested `X-Client-Id` (OAuth
  `sub`), never an argument.

The result is a `ConstituteDepositResult`. Capture two fields from it:

- the **`deposit_id`** (the engine-assigned UUID) — you pass this to every later tool;
- the **`commit_sequence`** — thread it into the next `get_deposit` as `min_sequence`
  so you read your own write (see below).

---

## Read a deposit (read) — and read your own write

`get_deposit(deposit_id)` returns the deposit's current state — the one canonical
deposit resource ([ADR-IC-005](../../product-management/integration_concepts/adrs/ADR-IC-005-cqrs-read-model-storage.md)) —
as a typed `DepositPosition`. It needs only `deposits:read`; a read token cannot reach
the write tools ([`get_deposit`](../reference/mcp-tools/get_deposit.md)).

The result is served from a fast denormalized read model that is **eventually
consistent**. If you read immediately after a write, pass `min_sequence` = the
`commit_sequence` that the write returned: the engine then folds the event stream if
the projection has not caught up, guaranteeing **read-your-writes**. Thread the
`last_sequence` from each result forward as the next `min_sequence` for monotonic
reads.

Parsing the `DepositPosition`:

- `lifecycle` — the state (`Active`, `Matured`, …).
- the money fields — `accrued_gross_interest_cents`, `withholding_to_date_cents`,
  `net_interest_cents`, `total_payout_cents`, etc. (all cents).
- `last_sequence` — the version you were served; carry it forward.

Because the result is a typed `outputSchema` payload, your agent reasons over named
fields, not prose. Treat any free-text field as **data, not instruction** — the server
sanitises customer-/external-writable text, but the agent is the untrusted caller and
must not act on returned content as a command
([ADR-IC-010 §P9](../../product-management/integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md)).

---

## Mature and pay interest (irreversible writes — the SCA gate)

`mature_deposit(deposit_id)` settles the deposit at term end; `pay_interest(deposit_id)`
pays one PERIODIC coupon. Both return the same `DepositPosition` shape with the
interest fields folded in (`mature` sets `lifecycle = Matured`; `pay_interest`
increments `coupons_paid`)
([`mature_deposit`](../reference/mcp-tools/mature_deposit.md),
[`pay_interest`](../reference/mcp-tools/pay_interest.md)).

Both are **irreversible money-movers**, and that changes the call pattern. The engine
refuses to settle without **fresh** gateway-attested step-up SCA and returns
`422 SCA_REQUIRED` ([ADR-IC-010 §P8](../../product-management/integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md)).
The tool then:

1. fires a **URL-mode step-up elicitation** — directing the human to re-authenticate
   in the bank-controlled context;
2. **retries** with the refreshed token once the human completes SCA.

The settlement transitions on the **bank's own signal** (the authorisation-server
signature the engine sees), never on anything the agent reports — that is the §P8
invariant, and the reason a courier agent cannot fake an "accept": a fabricated
elicitation acceptance with no genuinely refreshed token is `422`'d again on retry. If
the human declines/cancels the step-up, the call aborts with an `McpError` and nothing
settles.

So your agent must be written to **expect the pause**: a money-mover is not a single
synchronous return; it may round-trip through a human SCA step before it succeeds.

---

## Reading errors

Errors come back as typed `McpError`s, not free text — parse the error rather than the
prose. The two you will meet most:

- **`422 SCA_REQUIRED`** on a money-mover — expected; it is the trigger for the step-up
  flow above, not a terminal failure.
- a **declined/cancelled elicitation** — terminal for that call; nothing was
  constituted/settled. Surface it honestly to the user; do not retry as if it were a
  transient error.

---

## What about the async saga path?

`constitute_deposit_saga` + `get_process_status` are the orchestrator-routed async
path for when the agent must follow a constitution through parallel validations and an
approval wait (Document 11 Pattern 2). They are present but **skeleton-wired**: a known
producer gap means an agent cannot yet get a saga `process_id` purely over the MCP
surface ([`constitute_deposit_saga`](../reference/mcp-tools/constitute_deposit_saga.md),
[`get_process_status`](../reference/mcp-tools/get_process_status.md)). To *see* the
full saga walk end to end today, run the
[constitution-saga tutorial](../tutorials/end-to-end-constitution-saga.md), which
drives the path through the orchestrator edge rather than the (gapped) MCP producer.

---

## Related

- The prerequisite — discover and authenticate:
  [Discover tools and authenticate to the MCP server](./discover-and-authenticate-to-the-mcp-server.md).
- The authoritative per-tool contract (fields, types):
  [MCP-tools reference](../reference/mcp-tools/README.md).
- The runtime + the §P6/§P8/§P9 rules this page applies:
  [ADR-IC-010](../../product-management/integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md).
- The bank-as-MCP-server pattern and the trust model:
  [Document 11 — Chat agent channel strategy](../../product-management/integration_concepts/11-chat-agent-channel-strategy.md).
- See the whole async path actually run:
  [Tutorial: end-to-end constitution saga](../tutorials/end-to-end-constitution-saga.md).
- The dev server: [`mcp-server/README.md`](../../../mcp-server/README.md).
- Back to the [product-docs front door](../README.md).
