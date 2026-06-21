# Snapshot operations — ops runbook (L.6, bd `babelstone-0uau.2`)

**In plain English.** The engine keeps "snapshots" — saved checkpoints of an
account's state — so it can rebuild a long-lived account's view by starting from
the checkpoint and replaying only what came after, instead of replaying every
event from the very beginning. Snapshots are a *speed* cache, never the source of
truth: the raw event log always is, and a rebuild from the log alone must always
work. This runbook is what an operator does to keep that cache healthy — spot a
snapshotter that has fallen behind, recover from a snapshot that fails its
integrity check, run the monthly drill that proves the snapshots are faithful,
and promote snapshots from "advisory" to "trusted" once they have earned it.

The formal frame: this operationalises [ADR-PC-003 §P6](../../docs/product-management/product_concepts/adrs/ADR-PC-003-postgresql-snapshots.md)
(the snapshot operational runbook) and its §P4 validation discipline
(hash-and-verify, advisory-until-six-months, monthly discard-and-rebuild). It
pairs with the [projection-rebuild drill runbook](./projection-rebuild-drill.md)
— that drill is the §7.2 correctness exercise this runbook's §3 now runs against
**populated** snapshots — and with the snapshot-operations alert group in
[`infra/grafana/prometheus/alert-rules.yaml`](../grafana/prometheus/alert-rules.yaml).

> **Scope.** Snapshots accelerate replay; they are **recomputable performance
> state, not source of truth** (ADR-PC-003 §Context). Every recovery below is
> safe by construction: the worst outcome of any snapshot problem is a slower
> rebuild, because discarding a snapshot and re-folding cold from the log always
> reproduces correct state.

---

## 1 · What the engine guarantees (so you know what "healthy" means)

| Property | Where it lives | What it means operationally |
|---|---|---|
| **Cold replay always works** | ADR-PC-003 §P3 / [event-store §8.2](../../docs/product-management/product_concepts/feature-design-event-store-projections.md) | You can ALWAYS discard every snapshot and rebuild from the log. No snapshot problem is unrecoverable. |
| **Snapshot-then-tail == cold fold, byte-for-byte** | ADR-PC-003 §P3 (`SnapshotEquivalenceProperties`) | A correct snapshot + the tail of events lands on the EXACT same state as a from-zero fold. The load harness asserts this every RC (L.5; see §5). |
| **Hash-and-verify on read** | ADR-PC-003 §P4 / §8.3 | Every snapshot read verifies its `(state ‖ last_event_id)` hash; a tampered or mis-sequenced snapshot is rejected, never trusted (`SnapshotStore.Verify` throws). |
| **Advisory until six months of passing drills** | ADR-PC-003 §P4 / [event-store §8.3](../../docs/product-management/product_concepts/feature-design-event-store-projections.md) | New snapshots are written `trusted = false` and are not relied on in production replays until they have passed the monthly drill for six months (§4). |

---

## 2 · Signal (1): snapshot lag — the snapshotter has fallen behind

**What it is.** The number of events appended to a stream since its last snapshot.
The composing snapshot policy (ADR-PC-003 §P2 — per-N, lifecycle, calendar) takes
a snapshot every N events (the v1 per-N default is 100, [event-store §8.1](../../docs/product-management/product_concepts/feature-design-event-store-projections.md)),
so a stream sitting far past N un-snapshotted events means the post-commit
snapshot writer is unhealthy.

**Why it is only a WARNING.** Snapshot writes are **eventually-consistent with the
log, not transactional** (ADR-PC-003 §P2): "if a write fails the engine continues
and the next rebuild is merely slower, never wrong." A deep un-snapshotted stream
makes the next cold replay slower; it never makes it *wrong*. So lag is a health
warning, not a correctness page.

**Alert.** `SnapshotLagHigh` in the `snapshot-operations` group of
[`alert-rules.yaml`](../grafana/prometheus/alert-rules.yaml). It is **live** (bd
`babelstone-sk7e`): the engine emits `snapshot_lag_events`, an observable gauge of
the largest un-snapshotted event count observed across streams, raised in the
runtime's post-commit snapshot path (`AggregateRuntime.TrySnapshotAsync` →
`SnapshotMetrics.RecordLag`) and reported each collection cycle. It is gauge-shaped
because the alert reads it instantaneously (`snapshot_lag_events > 500`); the per-N
default is 100, so >500 means the snapshotter is ~5 thresholds behind. A host turns
it on with `AddMeter(BabelstoneTelemetry.MeterName)`.

**Operator response.**

1. Confirm the snapshotter is running and the post-commit write path is not
   failing silently. The runtime's fail-soft `onSnapshotError` callback logs every
   post-commit snapshot-write failure (ADR-PC-003 §P2: the commit is the book of
   record, the snapshot is a rebuildable cache) — grep the host logs for the
   snapshot-write warning.
2. Check the per-N policy config (`Engine:SnapshotEveryNEvents`) and the calendar
   granularity (`Engine:SnapshotCalendarGranularity`) — a misconfiguration (N set
   absurdly high) presents as lag.
3. Recovery is benign: nothing is lost. Once the snapshotter recovers it catches
   up on the next qualifying append; you can force a fresh snapshot by replaying a
   lifecycle boundary or waiting for the next per-N trigger. No data action is
   required.

> **Gauge semantics — the lag is a per-process PEAK.** `snapshot_lag_events` is the
> *largest* un-snapshotted depth observed since the host process started, not a
> live current depth. It rises but does not decay within a process, so once a
> stream has been seen deeply behind, `SnapshotLagHigh` stays latched until the
> host restarts (which re-establishes the baseline from live appends). That is
> intentional — the operator signal is "the snapshotter WAS this far behind",
> which warrants the investigation above even if a later append has since
> snapshotted. After you have confirmed the post-commit path is healthy (steps 1–2),
> a rolling restart clears a latched-but-resolved warning. A finer per-stream,
> decaying gauge is a possible future refinement (bd `babelstone-sk7e` follow-up).

---

## 3 · Signal (2): hash-mismatch on read — a snapshot failed verification

**What it is.** A snapshot whose stored `snapshot_hash` does not match a recompute
of `SHA-256(state ‖ last_event_id)` on read. This is the §8.3 worst-case guard:
the single worst event-sourcing failure mode is a silently-wrong snapshot read as
truth, so the engine verifies every read and **rejects** a mismatch
(`SnapshotStore.Verify` throws `InvalidOperationException`) rather than trusting
it.

**Alert.** `SnapshotHashMismatch` (live, in the same group) — the counter
`snapshot_hash_mismatch_total`, incremented where verification throws
(`SnapshotStore.Verify` → `SnapshotMetrics.RecordHashMismatch`, bd
`babelstone-sk7e`). The alert reads `increase(snapshot_hash_mismatch_total[1h]) > 0`
at `severity: critical` because a recurring mismatch is a snapshot-infrastructure
bug, not transient noise.

**Operator response — the discard-and-rebuild recovery (ADR-PC-003 §P6 (2)).**

1. **Single mismatch:** the read already fell back to a cold fold from the log
   (the §P3 correctness fallback), so the answer the caller got is correct. Discard
   the offending snapshot so the next read does not re-hit it, then let the stream
   re-snapshot cold:

   ```bash
   # discard every snapshot for the affected stream; the next rebuild re-folds
   # cold from the log and re-writes a correct snapshot.
   psql "$PG" -c "DELETE FROM snapshots WHERE stream_id = '<stream-uuid>';"
   ```

   The harness exposes the same primitive end-to-end (`--measure discard-rebuild`,
   §5) and the engine exposes `ISnapshotStorage.DiscardAsync(stream_id)`.
2. **Recurring mismatch:** discard alone is a band-aid. A snapshot that keeps
   failing verification means the snapshot *infrastructure* is wrong (a serializer
   change, a hash drift, a write bug). **Page on-call**, capture the
   `projection_kind` + `stream_id` + the stored vs recomputed hash, and hand it to
   the engine owner. Do not silence the alert.
3. **PII note.** A snapshot can materialise PII into its serialised state. After a
   GDPR erasure (ADR-PC-004), the stale snapshot is discarded and rebuilt so the
   post-erasure rebuild shows null PII — the SAME discard-and-rebuild path above.

---

## 4 · The monthly discard-and-rebuild drill (now on POPULATED snapshots)

**What changed (L.6).** Snapshots are now generated in the live runtime
(bd `e6fr.11`/`e6fr.12`), so the monthly drill is the real correctness exercise
ADR-PC-003 §8.3/§P4 always intended: **discard populated snapshots, rebuild cold,
prove byte-identity.** Previously the drill ran with snapshots off, so it only
proved cold-fold — it never exercised the discard-of-real-snapshots path.

**What the drill proves now.**

1. The deep stream actually **snapshotted** (the per-N policy fired) — a
   precondition the old drill could not check.
2. Discarding every snapshot and re-folding cold reproduces every running belief
   **byte-for-byte** (the §8.3 no-rebuild-divergence invariant). A match proves the
   snapshots were faithful; a mismatch is a snapshot-infrastructure bug.

**How to run it.**

- The monthly [projection-rebuild drill](./projection-rebuild-drill.md)
  (`make projection-rebuild-drill`, and the scheduled
  `projection-rebuild-drill.yml` workflow) drives the engine's
  `FullRebuildDrillAsync` path — supersede-all + checkpoint reset + cold re-fold —
  which discards snapshots as part of the rebuild.
- The load harness exercises the **populated-snapshot** discard-rebuild directly:

  ```bash
  make load-test LOAD_ARGS="--measure discard-rebuild --depth 64 --no-bus"
  ```

  This builds a deep stream WITH snapshots, asserts snapshots exist, discards them
  all, rebuilds cold, and asserts zero divergence (an explicit FAIL if the deep
  stream did not snapshot — a drill that proved nothing cannot read green). It is
  one dimension of the composite [`make load-gate`](../../Makefile) RC gate (§5).

**A missed drill is a process incident** (ADR-PC-005 §P5) — the same posture the
projection-rebuild drill runbook takes; the `ProjectionRebuildDrillStale` alert
fires when no green drill landed within the monthly cadence.

---

## 5 · The advisory → trusted promotion path

**The discipline (ADR-PC-003 §P4).** A newly written snapshot is **advisory only**
(`trusted = false`) and is not relied on in production replays until it has passed
the hash-verify + monthly discard-and-rebuild checks **for six months**. Only then
is it promoted to `trusted = true`.

**The mechanism (it exists today).** The snapshot store's write is idempotent on
`(stream_id, at_sequence)`: re-putting the same key with `trusted = true`
**promotes** the row (`PostgresSnapshotStore.PutAsync` →
`INSERT … ON CONFLICT (stream_id, at_sequence) DO UPDATE SET … trusted =
EXCLUDED.trusted`). So promotion is a re-put with the trusted flag set — no schema
change, no new code path. A direct SQL promotion of a stream whose snapshots have
cleared the six-month bar:

```sql
-- promote the latest snapshot of a stream to trusted once it has passed six
-- months of green drills (ADR-PC-003 §P4). Audit which streams you promote.
UPDATE snapshots
   SET trusted = TRUE
 WHERE stream_id = '<stream-uuid>'
   AND at_sequence = (SELECT max(at_sequence) FROM snapshots WHERE stream_id = '<stream-uuid>');
```

**How it is EXERCISED.** The L.5 snapshot-accelerated replay gate (`--measure
snapshot-replay`, one dimension of `make load-gate`) proves on every RC that the
snapshot-accelerated rebuild is **byte-identical** to the cold fold and is
**faster** — the exact §P3 equivalence the trusted-promotion discipline relies on.
A snapshot that diverges from the cold fold fails that gate outright (a
fast-but-wrong snapshot is the worst failure mode), so the promotion bar is backed
by a falsifiable, every-RC check, not by assertion. The monthly discard-rebuild
drill (§4) is the other half: six months of green drills is the promotion
evidence.

> **Eligibility gate.** Do not promote a stream whose snapshots have not cleared
> both checks for six months. Promotion makes the engine *rely* on the snapshot in
> production replays; an un-earned promotion reintroduces the exact "buggy snapshot
> trusted blindly" risk §P4 exists to prevent.

---

## 6 · Cross-references

- [ADR-PC-003 §P2/§P3/§P4/§P6](../../docs/product-management/product_concepts/adrs/ADR-PC-003-postgresql-snapshots.md)
  — snapshot generation triggers, replay-from-snapshot semantics, the
  hash-verify + advisory-until-six-months validation discipline, and the §P6
  operational runbook this document realises.
- [event-store §8.1–§8.3](../../docs/product-management/product_concepts/feature-design-event-store-projections.md)
  — snapshot cadence, the cold-replay budget, and the §8.3 snapshot-correctness
  guard.
- [projection-rebuild drill runbook](./projection-rebuild-drill.md) — the monthly
  §7.2 drill this runbook's §3/§4 drives against populated snapshots.
- [`alert-rules.yaml`](../grafana/prometheus/alert-rules.yaml) — the
  `snapshot-operations` group (snapshot-lag + hash-mismatch alerts, now LIVE — the
  engine emits `snapshot_lag_events` and `snapshot_hash_mismatch_total`, bd
  `babelstone-sk7e`).
- [ADR-PC-004](../../docs/product-management/product_concepts/adrs/ADR-PC-004-pii-crypto-shredding.md)
  — PII materialised in a snapshot must be discardable so a post-erasure rebuild
  shows null PII (the §3 discard-and-rebuild path).
- [ADR-PC-005 §P5](../../docs/product-management/product_concepts/adrs/ADR-PC-005-dr-rto-rpo.md)
  — drills as resilience-testing evidence; a missed drill is a process incident.
- [`make load-gate`](../../Makefile) / [`scripts/load-gate.sh`](../../scripts/load-gate.sh)
  — the composite RC gate whose L.5 (snapshot-replay) and L.6 (discard-rebuild)
  dimensions exercise the snapshot equivalence + discard-rebuild paths every RC.
