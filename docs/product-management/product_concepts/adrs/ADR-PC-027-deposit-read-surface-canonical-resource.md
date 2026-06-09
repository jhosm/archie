# ADR-PC-027: Deposit Read Surface — One Canonical Resource, Read-Model-Backed with Fold-on-Token

| Field | Value |
|---|---|
| Status | Proposed |
| Date | 2026-06-09 |
| Deciders | jhosm |
| Shape | Contract-shape |
| Counterparty | The query consumer of the I.2 Query API — the channel/web front-end and the MCP server ([ADR-IC-010](../../integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md)) |
| Depends on | [ADR-IC-005](../../integration_concepts/adrs/ADR-IC-005-cqrs-read-model-storage.md), [ADR-PC-018](./ADR-PC-018-channel-routing-coexistence.md), [ADR-PC-021](./ADR-PC-021-application-layer-family-owned-deciders.md) |
| Resolves | bd `babelstone-edi1.1` |

## Context

The CQRS read model ([ADR-IC-005](../../integration_concepts/adrs/ADR-IC-005-cqrs-read-model-storage.md), [integration_concepts §03](../../integration_concepts/03-cqrs-and-read-models.md)) gives the engine two ways to answer *"GET me this deposit"*:

1. **fold the live event stream** through the aggregate runtime — strongly consistent, read-your-writes, slightly slower; and
2. **query the denormalized projection** (`read_model.deposits`) — fast, eventually consistent, the 500 ms-budget hot path.

The D.4 walking skeleton first exposed **both** as sibling URLs: `GET /v1/deposits/{id}` (the fold) and `GET /v1/deposits/{id}/read-model` (the projection). That manufactures a *duality of GETs* — two URLs addressing the same deposit — and the `/read-model` suffix leaks **storage** into the contract (the consumer is told *how* the answer is materialised, not *what* it is). REST resource-modelling guidance is unambiguous that a URL names a resource, not the mechanism behind it (Google AIP-121: an API that mirrors storage is an anti-pattern; the *RESTful Web Services Cookbook* §4: leave storage/mechanism out of URIs). The observed failure mode is exactly the one the suffix invites: consumers gravitate to the bare `/{id}` and the read model goes unused.

[ADR-PC-021 §D5](./ADR-PC-021-application-layer-family-owned-deciders.md) deferred the external HTTP **boundary**, which Epic E.5 then realised as `Babelstone.Engine.Api` (the `GET /v1/deposits/{id}` host); only the *secured edge* (OAuth/Kong authn) stays deferred to Epic J. What §D5 never settled is the **shape** of the read contract on that boundary — and the read model now exists, so it is decidable. This ADR fixes that read contract (the channel and the MCP server consume it). It does not touch the write side, the event schema, or the projection storage decision (those stay [ADR-IC-005](../../integration_concepts/adrs/ADR-IC-005-cqrs-read-model-storage.md)).

## Decision

There is **one canonical deposit read resource** — `GET /v1/deposits/{id}` — served from the read model by default and folded from the event stream only as an internal read-your-writes fallback. The aggregate fold is **not a public URL**; it is a capability the one read endpoint reaches for. All six contract slots:

1. **Payload shape.** One response record, `DepositResponse` (snake_case on the wire). It carries the deposit's identity, terms, the **live financial position** (`accrued_gross_interest_cents`, `withholding_to_date_cents`, `net_interest_cents`, `total_payout_cents`, `coupons_paid`), the routing-truth `sor` (the [ADR-PC-018](./ADR-PC-018-channel-routing-coexistence.md) channel-routing column, detailed at [coexistence §6.2](../feature-design-strangler-fig-coexistence.md)), both product keys under their honest names (`rate_sheet_version_id`, `product_code`), and the freshness pair (`last_sequence`, `last_updated`). All money is **integer cents** ([ADR-PC-010 §P1](./ADR-PC-010-dotnet-hand-rolled-engine.md)); **no PII** rides the read surface ([ADR-PC-004 §P2](./ADR-PC-004-pii-crypto-shredding.md)) — structural facts only. The cross-aggregate maturities query returns `DepositMaturitiesResponse` (a list of the same `DepositResponse`). The command response `ConstituteDepositResponse` carries `commit_sequence` (the token of slot 3).

2. **Semantics.** `GET /v1/deposits/{id}` returns *the deposit*. The server chooses the source: it serves the read-model row when one exists and is fresh enough; otherwise it folds the stream and returns the authoritative head. **Both sources fill the identical `DepositResponse`**, so the consumer never observes which path served it — that equivalence is what makes a *single* resource honest. Storage/mechanism never appears in a read path (no `/read-model`, `/aggregate`, `/projection`, `/event-store`). The maturities range scan (`GET /v1/deposits/maturities?from=&to=`) is a **query-named collection** with no write-side twin — the fold cannot answer a cross-stream range scan — so it has no duality and is the read model's natural, unambiguous home.

3. **Ordering and delivery.** The freshness barrier is the **per-stream `sequence_number`** — this engine has no cluster-wide offset, so [ADR-IC-005 §P3](../../integration_concepts/adrs/ADR-IC-005-cqrs-read-model-storage.md)'s `last_event_offset` (the read-after-write field; the same per-stream value [§P2](../../integration_concepts/adrs/ADR-IC-005-cqrs-read-model-storage.md)'s UPSERT monotonicity guard keys on) is realised per stream. A state-changing command returns `commit_sequence` (the head version its append reached); the read accepts that value as the `If-Min-Sequence` request header and treats it as a **minimum-version barrier**: if the projection's `last_sequence ≥ token`, serve the row; otherwise fold the stream (which is at or past the token by construction). This delivers read-your-writes **and** monotonic reads for that stream. The token is opaque to the consumer beyond "echo what the write returned."

4. **Idempotency.** `GET` is safe and idempotent. The token compare (`row.last_sequence ≥ min_sequence`) is a pure, repeatable freshness gate; the fold fallback is deterministic ([ADR-PC-010 §P5](./ADR-PC-010-dotnet-hand-rolled-engine.md)), so repeated reads at a given token return the same state. No write is ever performed on the read path ([ADR-PC-018 §6](./ADR-PC-018-channel-routing-coexistence.md) — the engine never staples a command onto its read surface).

5. **Error model — post-flagged, not gated.** An **unknown** deposit (no events, fold to `Version < 0`) is `404`. A **missing or stale** read-model row is **not an error** — the server folds and returns `200`; staleness is *surfaced*, never *gated*, via `last_updated` (the consumer may display "as of …"). A `from ≥ to` maturities window is an empty, well-formed `200`. There is no path on which the read blocks, retries server-side indefinitely, or unwinds a write.

6. **Ownership and versioning.** The **engine owns** the read contract; the channel/gateway and the MCP server consume it. Because the URL names the resource and not the storage, the projection technology may change (Postgres → Valkey/OpenSearch/DuckDB per [ADR-IC-005](../../integration_concepts/adrs/ADR-IC-005-cqrs-read-model-storage.md)'s upgrade path) with **zero contract change**. Response evolution is **additive** — new `DepositResponse` fields are forward-compatible (consumers ignore unknown keys); the MCP `outputSchema` ([ADR-IC-010](../../integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md) P6) widens additively. **No event/Avro schema changes**: `commit_sequence`/`last_sequence` are the engine's per-stream version, derived, never a new persisted field. A breaking change to the read shape ships as a new versioned path (`/v2/deposits`), never an in-place break.

## Consequences

**Easier.** One obvious URL — the fast path *is* the default, so no consumer can pick "the wrong GET", and the read model is no longer bypassed. Read-your-writes needs no second endpoint and no client-side fallback logic: the caller threads a token it already holds and the server does the rest. The MCP agent gets it for free — `constitute_deposit` returns `commit_sequence`, `get_deposit(min_sequence=…)` threads it, with no `get_deposit_read_model` tool to mis-pick ([ADR-IC-010](../../integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md): a read fetched on demand is a *tool*, control-ownership not CQRS). The contract is projection-technology-agnostic.

**Harder / locked-in.** The single read endpoint must implement **two read paths kept value-identical** — a real correctness obligation (the read-model row was enriched to a complete stand-in for the fold precisely so the shapes cannot diverge). The fold-on-lag branch adds CPU on the read path; this is bounded because deposit streams are short, but a pathologically long stream would make the fallback expensive (mitigated by snapshots when they land). Consumers can no longer address "the projection" as a distinct resource — intentional, but it forecloses a consumer that *wanted* to assert on raw projection state over HTTP (that belongs to an internal/ops surface, not the public contract).

**Impossible by construction.** A consumer cannot observe whether a given `GET` was served from the projection or the fold (identical shape) — so it cannot couple to the storage tier. A command cannot be stapled onto the read surface ([ADR-PC-018 §6](./ADR-PC-018-channel-routing-coexistence.md)). A read cannot silently return state staler than a presented token (it folds instead).

## Residual risks

- **Absolute-latest without a token is out of scope.** This contract commits to *read-your-writes* (you see at least your own committed write) and *monotonic reads*, both keyed on a token the caller holds. It does **not** commit to a "give me the engine's absolute head regardless" mode — a `?consistency=strong` knob (the classic Microsoft-Graph `ConsistencyLevel` shape) is deliberately deferred; a caller with no token gets the read model, or a fold only when the row is absent.
- **`last_updated` on the fold path depends on the stream having tail events.** A stream fully covered by a snapshot with no tail would leave `last_updated` unstamped; v1 has **no snapshots**, so this is unreachable today, but when snapshotting lands the snapshot must carry the transaction-time it was taken at (tracked with the snapshot work, [ADR-PC-003](./ADR-PC-003-postgresql-snapshots.md)).
- **No HTTP caching contract.** ETag/`If-None-Match`/`304` are not committed; the version travels as `If-Min-Sequence` / a body field, not an ETag. Cacheability is a later decision once a real CDN/edge tier exists.
- **Two-path value parity is a standing obligation, not a one-time check.** The contract's honesty rests on the read-model row carrying the same facts the fold computes; a future field added to one path but not the other would silently break the equivalence. The fitness function (below) pins the read-your-writes behaviour; field-parity is guarded by the shared `DepositResponse` shape and the integration assertions, and must be revisited whenever the position grows a field.

## Verifiable commitments

This contract's load-bearing commitment is a fitness function in the [commitment catalogue](./commitment-catalogue.md) — the single source of truth for its exact claim, gate, and `Live`/`Planned`/`Gap` status ([ADR-PC-020 §P5–§P7](./ADR-PC-020-llm-toolchain-and-conformance-governance.md)):

- `READ_YOUR_WRITES_FOLD_ON_TOKEN` — one canonical `GET /v1/deposits/{id}` (no storage-named sibling) that serves the read model by default and folds the event stream for read-your-writes when an `If-Min-Sequence` token outruns the projection, both paths filling the identical `DepositResponse` (slots 2 · Semantics / 3 · Ordering).
