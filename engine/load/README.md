# /engine/load — the load-test harness

**In plain English:** this is the engine's "can it take the load?" test. It pushes synthetic
deposit traffic at a configured rate against a *running* dev stack, watches how fast the engine
commits its projections (by reading the engine's *own* telemetry, not a stopwatch on the sender),
and prints a single **PASS/FAIL** with the random seed needed to reproduce the run. The process
**exit code is the verdict** (`0` = PASS, `1` = FAIL), so a CI step can gate a release candidate on it.

It is the runnable expression of **[ADR-PC-011 — in-house load-test harness](../../docs/product-management/product_concepts/adrs/ADR-PC-011-in-house-load-test-harness.md)**
(bd epic `babelstone-2e6q`, "Epic L.3 — Acceptance gates").

---

## What it checks

One host (`Babelstone.LoadHarness.Runner`) satisfies the whole L.3 acceptance ladder. Each slice
folds a verdict into the shared `RunArtefact`; the run passes only if **every** verdict that ran passed:

| Slice | bd issue | What it asserts |
|-------|----------|-----------------|
| **L.3a** sync latency | `babelstone-2e6q.1` | The two §8.3 sync bands stay within budget — `current_balance` (p50/p95/p99 < 20/80/**200** ms) and `hold_freeze_ledger` (< 30/100/**250** ms) — and "no spans captured" is an explicit FAIL. |
| **L.3b** sustained throughput | `babelstone-2e6q.2` | A wall-clock-paced loop holds a target TPS (the §8.3 rig figure is **250 TPS**) for a duration, and the *achieved* rate lands within tolerance — so a producer that silently collapses to 40 TPS can't pass the latency bands vacuously. |
| **L.3c** burst | `babelstone-2e6q.3` | Sequences `sustained → burst (1000 TPS / 15 min) → recovery`, in that order. |
| **L.3d** cold replay + no-divergence | `babelstone-2e6q.4` | A cold projection rebuild over the populated store finishes inside the §8.2 budget (**5 s** with-a-plan / **30 s** irregular), **and** the rebuilt belief is byte-identical to the running belief (the no-rebuild-divergence invariant). |
| **L.3e** sync-replication append cost | `babelstone-2e6q.5` | Append p50/p99 with PostgreSQL `synchronous_commit` **on vs off** and the §P1 delta — the write-path latency cost the RPO≈0 guarantee imposes (ADR-PC-005 §P1). **GATING** only against a real warm standby (`--standby-confirmed`, the HA overlay); **advisory** on the single-node dev stack (the delta is then a floor, not the production cost). |
| **L.5** snapshot-accelerated replay | `babelstone-0uau.1` | Over one deep stream, a snapshot-then-tail rebuild is **byte-identical** to the cold fold (the §P3 invariant) **and** demonstrably **faster**, within budget. A fast-but-divergent snapshot FAILS outright — correctness gates, speed only qualifies (ADR-PC-003 §P3/§P4). |
| **L.6** discard-rebuild on populated snapshots | `babelstone-0uau.2` | Builds a deep stream WITH snapshots, confirms they exist, **discards** them all, rebuilds cold, and asserts zero divergence — the real §8.3/§P4 correctness exercise (the old drill ran snapshots-off, proving only cold-fold). |

> **The composite RC gate** (bd `babelstone-2e6q.6`, `make load-gate`) aggregates **all** of the
> above into one binary PASS/FAIL and wires the every-RC cadence
> ([`.github/workflows/load-gate.yml`](../../.github/workflows/load-gate.yml)). This harness is what
> that gate runs; see the [snapshot-operations runbook](../../infra/runbooks/snapshot-operations.md)
> for the §P6 operational surface (snapshot-lag alarm, hash-mismatch recovery, advisory→trusted
> promotion) the L.5/L.6 dimensions back.

---

## Prerequisites

The measured path drives the engine's **real** append + projection code in-process, so it needs a
live event store. The bus path additionally needs Redpanda + Schema Registry.

1. **Bring the stack up:**
   ```bash
   make up      # PostgreSQL + Redpanda + Schema Registry (+ the rest of the dev stack)
   ```
2. **Apply the event-store migrations to the `babelstone` DB.** The engine does **not** apply
   event-store migrations on boot (see the gotcha in [`CLAUDE.md`](../../CLAUDE.md)). The demo
   scripts do it for you — the quickest route is:
   ```bash
   make demo-mcp        # applies engine/src/Babelstone.EventStore.Migrations/Sql/*.sql, then leaves the DB ready
   ```
   (or apply `engine/src/Babelstone.EventStore.Migrations/Sql/*.sql` to the `babelstone` DB yourself).

If you only want a **latency smoke and have just PostgreSQL**, pass `--no-bus` to skip the Redpanda
producer entirely (see below).

---

## Quick start

```bash
make load-test                                                       # low-TPS latency smoke (L.3a)
make load-test LOAD_ARGS="--profile sustained --tps 250 --duration 60s"   # L.3b sustained
make load-test LOAD_ARGS="--profile burst"                           # L.3c burst (1000 TPS / 15 min)
make load-test LOAD_ARGS="--measure replay"                          # L.3d replay budget + no-divergence
make load-test LOAD_ARGS="--measure snapshot-replay --depth 64"      # L.5 snapshot-vs-cold parity + speedup
make load-test LOAD_ARGS="--measure discard-rebuild --depth 64"      # L.6 discard populated snapshots, rebuild cold
make load-test LOAD_ARGS="--measure repl-latency"                    # L.3e sync-replication append cost (§P1, advisory)
make load-gate                                                       # the composite RC gate — one PASS/FAIL over ALL dimensions
```

The **composite gate** runs every dimension in sequence and exits `0` only if all pass:

```bash
make load-gate                                                       # per-RC fast pass (a smoke of each dimension)
# the full §8.3 soak (24h sustained, 15m burst) substitutes bigger numbers without code change:
make load-gate LOAD_GATE_SUSTAINED_ARGS="--profile sustained --tps 250 --duration 24h --no-bus" \
               LOAD_GATE_BURST_ARGS="--profile burst --burst-tps 1000 --burst-duration 15m --no-bus"
# against the HA overlay, make the §P1 dimension GATING (it is advisory on the single-node stack):
make load-gate LOAD_GATE_REPL_ARGS="--measure repl-latency --standby-confirmed --pg <overlay-write-endpoint>"
```

`make load-test` is a thin wrapper over `dotnet run`; the equivalent direct invocation is:

```bash
mise exec -- dotnet run --project engine/load/Babelstone.LoadHarness.Runner -c Release -- [flags]
mise exec -- dotnet run --project engine/load/Babelstone.LoadHarness.Runner -c Release -- --help
```

> Always go through `mise exec --` (or `make`, which does) so the pinned .NET 10 toolchain is used —
> see the toolchain note in [`CLAUDE.md`](../../CLAUDE.md).

---

## Flags

| Flag | Default | Meaning |
|------|---------|---------|
| `--profile smoke\|sustained\|burst` | `smoke` | Run shape. `smoke` = one short low-TPS phase; `sustained` = hold a rate for a duration; `burst` = sustained → burst → recovery. |
| `--measure <mode>` | `latency` | What to fold into the verdict. `latency` = the §8.3 sync bands; `replay` = the §8.2 cold-replay budget + no-divergence drill (L.3d); `snapshot-replay` = snapshot-vs-cold parity + speedup (L.5); `repl-latency` = sync-replication append cost §P1 (L.3e); `discard-rebuild` = discard populated snapshots, rebuild cold (L.6). |
| `--seed <int>` | `1234` | RNG seed. A failure reproduces from `(seed, run-id, revision)` (§8.5). |
| `--run-id <guid>` | fresh GUID | Stream-id namespace nonce. Defaults fresh so repeated runs over a populated store don't collide on the optimistic-concurrency head; set it to reproduce a prior run's exact stream ids. |
| `--warmup <int>` | `5` | Unmeasured warmup events appended before the observer starts, so the p99 reflects steady state, not process cold-start. `0` disables. |
| `--tps <double>` | `50` | Sustained target TPS (the §8.3 rig uses `250`). |
| `--burst-tps <double>` | `1000` | Burst-phase target TPS. |
| `--duration <Ns\|Nm\|Nh\|N>` | `10s` | Sustained drive duration (`90` = 90 s, `60s`, `15m`, `24h`). |
| `--burst-duration <Ns\|Nm\|Nh\|N>` | `15m` | Burst-phase hold duration. |
| `--tolerance <0..1)` | `0.10` | How far below target the achieved TPS may dip and still pass (`0.10` = within 90%). |
| `--pg <connstring>` | `Host=localhost;Port=5432;Database=babelstone;Username=babelstone;Password=babelstone` | Event-store connection string. |
| `--bootstrap <host:port>` | `localhost:19092` | Redpanda bootstrap for the producer path. |
| `--schema-registry <url>` | `http://localhost:18081` | Schema Registry the producer's Avro codec registers/resolves against. |
| `--no-bus` | (bus on) | Skip the Redpanda producer — in-process append/projection only (PostgreSQL is then the only dependency). |
| `--irregular` | (with-a-plan) | Use the §8.2 irregular replay budget (30 s) instead of the with-a-plan budget (5 s). |
| `--depth <int>` | `64` | L.5/L.6 deep-stream event depth (1 constitution + depth-1 accruals). Must exceed the rig's per-N snapshot threshold so a snapshot lands before the head and the accelerated fold has a tail to skip. |
| `--repl-samples <int>` | `50` | L.3e: appends timed per side (sync on / off) — the p99 sample depth. |
| `--standby-confirmed` | (advisory) | L.3e: the run targets a real warm standby (the HA overlay), so `synchronous_commit=on` genuinely blocks on a second node — makes the repl-latency verdict **GATING**. Without it the verdict is advisory (the single-node delta is a floor, not the production cost; ADR-PC-005 §P1). |
| `-h`, `--help` | — | Print usage and exit. |

---

## Reading the output

The run prints a one-line summary followed by each verdict, then exits with the gate code:

```
PASS — 641 events, 2/2 bands within budget; reproduce with seed=1234, revision=local-unversioned.
  [PASS] current_balance: within budget
  [PASS] hold_freeze_ledger: within budget
```

A breached band prints its percentiles instead of `within budget`, e.g.
`breach: p50=12.0/20 p95=90.0/80 p99=210.0/200 (ms)`. A throughput / replay / no-divergence verdict,
when measured, prints on its own line below the bands.

**Exit codes:** `0` = PASS · `1` = FAIL · `2` = usage error · `130` = interrupted (Ctrl-C).

---

## How it works (architecture notes)

Two distinct paths run per event, and they exist for different reasons:

- **§G2 — the measured-latency path (in-process).** There is no separate running "engine consumer"
  host wired to Redpanda today, so to measure boundary-to-commit latency the harness drives the
  engine's *real* append + projection code directly, in the same process, against the live database
  (ADR-PC-011 §S2). That append opens the engine's own OpenTelemetry product span
  (`accrual.computed` / `withholding.applied`); the observer reads **that span's duration** as the
  latency — never the driver's send clock (§P2/§G2).
- **§G1 — the production producer path (onto live Redpanda).** Exercised separately by
  `WorkloadDriver` using the engine's **own** Avro serializer (no parallel encoder), so the
  bytes-on-the-bus path is real. Skipped by `--no-bus`.

**Reproducibility (§8.5).** Every run is named by `(seed, run-id, revision)`. The same seed +
run-id regenerate the same synthetic deposits and stream ids; the revision is read from
`BABELSTONE_REVISION` / `GITHUB_SHA` (falling back to `local-unversioned`). A failure that does *not*
reproduce from that triple is escalated above an ordinary failure — it implies engine-level
non-determinism.

---

## Project layout

```
engine/load/
├── Babelstone.LoadHarness/             # the reusable primitives (workload generator/driver,
│                                       #   latency observer, verdicts, RunArtefact)
├── Babelstone.LoadHarness.Runner/      # the runnable host: CLI parsing, the drive loop, the
│                                       #   in-process engine rig — composes the primitives
├── Babelstone.LoadHarness.Tests/       # library unit + Testcontainers integration tests
└── Babelstone.LoadHarness.Runner.Tests/# Docker-free component tests (CLI parse/validate,
                                        #   throughput/replay verdicts, burst sequencing)
```

## Testing & coverage

The Docker-free tests run in the normal engine unit lane:

```bash
mise exec -- dotnet test engine/load/Babelstone.LoadHarness.Runner.Tests/        # CLI + verdict folding
mise exec -- dotnet test engine/load/Babelstone.LoadHarness.Tests/ --filter "Category!=Integration"
```

The host's pure, Docker-free-testable surface — CLI parsing/validation, the verdict value objects,
the `RunArtefact` fold, and the static phase planner — is unit-tested directly. The irreducibly
integration-only members (the live-PostgreSQL `EngineProjectionRig` and `LoadRunner`'s drive/measure
loop) carry `[ExcludeFromCodeCoverage]` with justifications: they are unreachable from the unit lane
and are verified instead by the live-stack acceptance runs above and by `make load-test`. Bringing
those under automated Testcontainers coverage is tracked in bd `babelstone-2e6q.7`.

---

## See also

- [ADR-PC-011 — in-house load-test harness](../../docs/product-management/product_concepts/adrs/ADR-PC-011-in-house-load-test-harness.md) — the decision and its §G1/§G2/§8.x commitments this host implements.
- [feature-design — event store & projections](../../docs/product-management/product_concepts/feature-design-event-store-projections.md) — the §8.2 replay budgets and the §7.2 rebuild drill the L.3d verdicts honour.
- [`/engine` README](../README.md) — the product engine this harness drives.
