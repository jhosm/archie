---
name: new-store-migration
description: >-
  Author a new forward-only SQL migration for one of the three Postgres migration
  series — the engine event store, the orchestrator saga substrate, or a family
  read model — getting the numbering, the series-specific invariants (engine stays
  family-agnostic + append-only role grants for new tables; a family read model
  carries family-named tables in its own ledger), and the forward-only discipline
  right. Use when the user wants to add a migration, a table, an index, or any
  schema change to the event store, saga store, or a family read model.
---

# new-store-migration — author a forward-only Postgres migration

You add a **new SQL migration** to one of Babelstone's three independent, embedded migration
series. Each is a set of `NNNN_name.sql` files compiled in as `EmbeddedResource`s and applied in
version order by a runner. Pick the right series first — their invariants are *opposite* in
places, so a table that belongs in one is illegal in another.

> Migrations are **forward-only**: a shipped migration is immutable — you only ever **add** the
> next file, never edit or renumber one that has run anywhere. The runner enforces it
> (`MigrationSet.Discover()` rejects a duplicate or out-of-order version before any DDL runs).

## Step 1 — Which series?

| Series | Directory | Ledger | The invariant that defines it |
|---|---|---|---|
| **Engine event store** | [`engine/src/Babelstone.EventStore.Migrations/Sql/`](engine/src/Babelstone.EventStore.Migrations/Sql/) | `schema_migrations` | **Family-agnostic** — ZERO family-named tables/columns. The spine never names a family ([ADR-PC-021](docs/product-management/product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md)). |
| **Orchestrator saga** | [`orchestrator/src/Babelstone.Orchestrator.Substrate/Migrations/Sql/`](orchestrator/src/Babelstone.Orchestrator.Substrate/Migrations/Sql/) | `schema_migrations_orchestrator` | Saga substrate (state + outbox); orchestration concern only, no engine/family tables. |
| **Family read model** | `families/<family>/src/Babelstone.Families.<Family>.Application/Migrations/Sql/` | `schema_migrations_<family>` | **Family-named** tables in a dedicated `read_model` schema; runs on the SAME tier AFTER the engine migrations ([ADR-IC-005 §S1/§P1](docs/product-management/integration_concepts/adrs/ADR-IC-005-cqrs-read-model-storage.md)). The exact opposite of the engine rule. |

If you're adding the **read-model table for a brand-new family**, you're in the family series —
the `new-family-schema` skill scaffolds the family shell and hands the `0001_read_model.sql`
authoring to this skill. Model it on
[`term-deposit`'s `0001_read_model.sql`](families/term-deposit/src/Babelstone.Families.TermDeposit.Application/Migrations/Sql/0001_read_model.sql).

## Step 2 — Number it `NNNN_name.sql`

The next version is **`max(existing) + 1`**, zero-padded to four digits, snake_case name
(`0017_add_outbox_priority.sql`). Two rules, no more:

- **Gaps are fine.** The engine set already skips 0007–0009 and 0013. `MigrationSet.Discover()`
  ([`MigrationSet.cs`](engine/src/Babelstone.EventStore.Migrations/MigrationSet.cs)) sorts by
  version and rejects only a **duplicate** version — it does not require a contiguous sequence.
  Do **not** "fill" a gap and do **not** warn about one.
- **Never reuse or reorder a version.** Forward-only discipline: a duplicate or out-of-order
  version is a packaging error the runner throws on before any DDL runs.

The file is picked up automatically — the `.csproj` already globs
`<EmbeddedResource Include="…Sql/*.sql" />`, so a new file in the directory needs no project
edit. (Confirm the glob exists if you ever add a *new* series.)

## Step 3 — Honour the series' invariant

**Engine event store:**
- **No family-named tables or columns.** A query-shaped, family-typed table (e.g. one with
  `maturity_date`, `coupons_paid`) belongs in the family read-model series, not here — that is
  exactly why `read_model.deposits` was relocated out of the engine set (it was once `0013`).
- **A new table needs the append-only role envelope.** The runtime role `babelstone_engine` is
  deliberately denied UPDATE/DELETE on the log (append-only by privilege, not trigger —
  [ADR-PC-001 §P3](docs/product-management/product_concepts/adrs/ADR-PC-001-event-store-technology.md)).
  Any new table you add must `GRANT` exactly the privileges the runtime needs and nothing more,
  following the pattern in
  [`0002_append_only_role.sql`](engine/src/Babelstone.EventStore.Migrations/Sql/0002_append_only_role.sql)
  (`GRANT SELECT, INSERT`; add `UPDATE (specific_cols)` only where a status is mutated; never
  blanket UPDATE/DELETE on an event log). The 12a fitness test checks table/column/FK *names*,
  **not** role grants — the grant is yours to get right.
- **The engine does NOT apply event-store migrations on boot** (it applies only its family
  read-model migration). A host/deploy step applies `0001..NNNN` to the `babelstone` DB first —
  so your migration only takes effect once that step runs (the demo scripts do this via
  `scripts/demo-lib.sh`). Idempotent DDL (`IF NOT EXISTS`, guarded `DO $$…$$`) keeps re-runs safe.

**Family read model:**
- Tables are **family-named**, in the dedicated `read_model` schema (ADR-IC-005 §P1), under the
  family's own ledger `schema_migrations_<family>`, applied AFTER the engine migrations on the
  same Postgres tier (ADR-IC-005 §S1). This is the denormalized CQRS read side — distinct from
  the rebuildable bitemporal `projections` table
  ([ADR-PC-002](docs/product-management/product_concepts/adrs/ADR-PC-002-application-level-bitemporality.md)).

**Orchestrator saga:** saga state/outbox only; keep it to the orchestration concern.

## Step 4 — Write idempotent, additive DDL

- Prefer `CREATE TABLE IF NOT EXISTS`, `CREATE INDEX IF NOT EXISTS`, `ADD COLUMN IF NOT EXISTS`,
  and guarded `DO $$ … $$` blocks (the role provisioning in `0002` is the model) so re-applying
  the set is a no-op.
- **Additive only.** Don't drop/rename a column other migrations or replay paths depend on; the
  event log itself is never altered. If you genuinely must change a shape, add a new migration
  that migrates forward — never edit a prior file.
- Lead the file with a comment block in the house style: the migration's purpose, the ADR it
  serves, and any ordering/ownership note (read the existing files — they are densely annotated).

## Step 5 — Verify

```bash
# Engine series — the migration set + its assertions (12a fitness, append-only privilege):
mise exec -- dotnet test engine/tests/Babelstone.EventStore.Tests/ --nologo -v q
# Family series — the family's Application integration tests rehydrate against the read model:
mise exec -- dotnet test families/<family-kebab>/tests/Babelstone.Families.<Family>.Application.Tests/ --nologo -v q
```

`make preflight` runs the engine tier among its hermetic gates, so it catches a packaging error
(duplicate/out-of-order version) before you push.

## Guardrails

- **Forward-only** — add the next `NNNN`; never edit, renumber, or "gap-fill" a shipped migration.
- **Right series, right invariant** — engine = zero family names; family read model = family-named
  tables in `read_model` under `schema_migrations_<family>`, after the engine set. They are opposite.
- **New engine table ⇒ append-only grant** — mirror `0002`; least privilege, no blanket UPDATE/DELETE.
- **Idempotent DDL** — `IF NOT EXISTS` / guarded `DO $$…$$`; the set must be re-runnable.
- **Engine migrations are host-applied, not boot-applied** — your change lands only when the deploy
  step runs `0001..NNNN`.
