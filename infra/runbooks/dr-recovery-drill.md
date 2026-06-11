# DR recovery drill — event store + key store (M.4, bd `babelstone-f0ui`)

The operational runbook for the **full recovery drill** that
[ADR-PC-005 §P5](../../docs/product-management/product_concepts/adrs/ADR-PC-005-dr-rto-rpo.md)
mandates as **DORA resilience-testing evidence**: restore the event store from
backup into a clean environment, rebuild projections, verify, and restore the
OpenBao key store. The drill validates that the named RTO/RPO targets are
**real, not aspirational** — *"a missed drill is a process incident"* (§P5).

> **Scope.** This runbook is the operational procedure. The infrastructure it
> drives lives alongside it:
> - **§P1 synchronous warm standby** (RPO ≈ 0):
>   [`infra/k8s/overlays/ha/postgres-standby-ha.yaml`](../k8s/overlays/ha/postgres-standby-ha.yaml)
> - **§P2 PITR (pgBackRest)**: [`infra/pgbackrest/pgbackrest.conf`](../pgbackrest/pgbackrest.conf),
>   [`infra/k8s/overlays/ha/postgres-pitr-pgbackrest.yaml`](../k8s/overlays/ha/postgres-pitr-pgbackrest.yaml),
>   [`postgres-pitr-resources.yaml`](../k8s/overlays/ha/postgres-pitr-resources.yaml)
> - **§P4 key-store DR**: [`infra/k8s/overlays/ha/openbao-dr-ha.yaml`](../k8s/overlays/ha/openbao-dr-ha.yaml)
>
> These are **POC defaults** (ADR-PC-005 Status: RTO/RPO numbers are POC
> defaults pending operating-bank sign-off). The off-site object store,
> OpenBao real raft storage, and credentials are seams replaced by **M.2**
> (babelstone-puu3) and **Q.6** (babelstone-4c81).

---

## 0 · Targets the drill validates (ADR-PC-005 §Decision)

| Target | v1 default | This drill proves it |
|---|---|---|
| **RPO — committed events** | ≈ 0 | §3 failover loses no acknowledged event |
| **RPO — base-backup floor** (both nodes lost) | ≤ 60 s | §4 PITR replays to within the last archived WAL segment |
| **RTO — failover to warm standby** | ≤ 15 min | §3 time-to-promote |
| **RTO — full restore from backup** (both nodes lost) | ≤ 4 h | §4 + §5 restore + rebuild within the window |
| **Recovery cold-replay budget** | ≤ 24 h at v4 scale | §5 full-book projection rebuild |

Record the wall-clock for §3, §4+§5, and §6 against these. A miss is a finding,
not a silent pass.

---

## 1 · Cadence and roles

- **Failover rehearsal (§3):** monthly, riding the existing monthly
  projection-rebuild drill ([event-store §7.2](../../docs/product-management/product_concepts/feature-design-event-store-projections.md)).
- **Full restore-from-backup drill (§4–§6):** quarterly (heavier; needs a clean
  environment).
- **Roles:** a *drill lead* (runs the steps, records timings) and a *verifier*
  (independently confirms the post-recovery assertions in §5/§6). The drill
  produces a dated record (§7) — the DORA evidence artefact.

---

## 2 · Pre-drill checklist

- [ ] Confirm the warm standby is **caught up** (no replication lag) before
      starting — `SELECT * FROM pg_stat_replication;` on the primary shows
      `state=streaming`, `sync_state=sync` for `standby1`, `flush_lsn` ≈
      primary's `pg_current_wal_lsn()`.
- [ ] Confirm WAL archiving is healthy: the last archived segment is recent —
      `SELECT last_archived_wal, last_archived_time FROM pg_stat_archiver;` and
      `pgbackrest --stanza=event-store info` shows a recent backup + WAL range.
- [ ] Confirm the OpenBao key-store snapshot CronJob has a recent successful run
      (`openbao-raft-snapshot`) — see §6.
- [ ] Provision a **clean** target environment for §4–§6 (never restore over the
      live primary).
- [ ] Note the current primary LSN and a known sentinel event id (a recent
      append) so §5 can assert "this event survived".

---

## 3 · Failover rehearsal — promote the warm standby (RTO ≤ 15 min, RPO ≈ 0)

Validates the **§P1** first line: a committed event is durable on two nodes
before ack, so promoting the standby loses no acknowledged event.

1. **Record start time.**
2. Simulate primary loss (cordon/delete the primary pod, or stop its Postgres).
3. **Promote the standby** to primary:
   ```bash
   kubectl -n babelstone-dev exec postgres-standby-0 -c postgres -- \
     pg_ctl promote -D /var/lib/postgresql/data/pgdata
   ```
   The standby exits recovery and accepts writes.
4. Repoint the engine's write Service at the promoted node (in the HA overlay,
   the `postgres` write Service selects `pg-role: primary`; relabel the promoted
   pod or repoint the Service — record the exact step your env uses).
5. **Verify RPO ≈ 0:** the sentinel event from §2 is present on the promoted
   node (`SELECT count(*) FROM events WHERE id = <sentinel>;` → 1). No
   acknowledged event was lost.
6. **Record end time** → time-to-recover. Target ≤ 15 min.
7. **Re-establish protection:** rebuild a new warm standby against the promoted
   primary (re-run the standby bootstrap, §P1). The drill is not done until the
   topology is back to primary + synchronous standby.

---

## 4 · PITR restore — both nodes lost (RTO ≤ 4 h, RPO floor ≤ 60 s)

Validates the **§P2** second line: WAL archiving + base backups give
point-in-time recovery when the synchronous pair is gone.

1. **Record start time.** Bring up a clean Postgres in the target environment
   with an EMPTY PGDATA and the pgBackRest config mounted (`/etc/pgbackrest`,
   the `event-store` stanza).
2. **Restore the base backup + replay WAL to a target time:**
   ```bash
   pgbackrest --stanza=event-store --type=time \
     --target="2026-06-11 12:00:00+00" \
     --delta restore
   ```
   (`--type=time` for PITR to a wall-clock target; omit `--target` to restore to
   the latest archived WAL — the ≤ 60 s floor.)
3. Start Postgres; it replays archived WAL up to the target, then opens.
4. **Verify the RPO floor:** the recovered LSN is within one WAL segment of the
   target — the gap between `--target` and the last replayed commit is ≤ 60 s
   (the archive_timeout). Record the actual gap.
5. **Verify integrity:** `pgbackrest --stanza=event-store --log-level-console=info check`
   passes (manifest checksums) — *"backup restore is drilled, not assumed
   correct"* (§P2).

---

## 5 · Rebuild projections from the log (cold-replay budget)

Validates **§P3**: projections and snapshots are NOT separately backed up —
recovery IS rebuild from the restored event log.

1. Point the engine at the restored event store (§4) with projections EMPTY.
2. Trigger a **full-book projection rebuild** from the log
   ([event-store §8.2](../../docs/product-management/product_concepts/feature-design-event-store-projections.md));
   snapshots ([ADR-PC-003](../../docs/product-management/product_concepts/adrs/ADR-PC-003-postgresql-snapshots.md))
   accelerate but cold replay must work without them.
3. **Verify:** rebuilt projections match expectations (row counts, a sampled set
   of deposit balances, the sentinel from §2 reflected in the read model). The
   verifier confirms independently.
4. **Record end time** for §4+§5 → full-restore RTO. Target ≤ 4 h (v1 book);
   the cold-replay itself stays within the published ≤ 24 h window at v4 scale.

> The rebuild determinism + cold-replay budget are gated by the existing engine
> fitness functions (`PROJECTION_REBUILD_DETERMINISM`, `REPLAY_BUDGET_5S_30S`) —
> this drill exercises them end-to-end on RESTORED data, it does not re-assert
> them (ADR-PC-005 §Verifiable-commitments).

---

## 6 · Restore the OpenBao key store — and respect the crypto-shred horizon (§P4)

Validates **§P4**: key loss is irreversible loss of all PII, so the key store is
restored with guarantees at least as strong as the event store.

1. In the clean environment, restore the most recent key-store raft snapshot
   (produced by the `openbao-raft-snapshot` CronJob):
   ```bash
   bao operator raft snapshot restore /snapshots/keystore-<timestamp>.snap
   ```
   (Seam note: against the dev-mode `-dev` OpenBao there is no raft store to
   restore — this step goes live once **M.2** provisions real `raft` storage.)
2. **Verify** the engine can decrypt a known subject's PII against the restored
   key store (a round-trip on the sentinel subject from §2). PII that was
   readable before the drill is readable after.
3. **CROSS-SHRED HORIZON CHECK (§P4 / [ADR-PC-004 §P5](../../docs/product-management/product_concepts/adrs/ADR-PC-004-pii-crypto-shredding.md)):**
   confirm the alignment between the **event-store PITR retention** (pgBackRest
   `repo1-retention-full=14`, time-based) and the **key-store snapshot
   retention** (also 14 days). A GDPR erasure destroys a subject's key, but a
   pre-erasure snapshot still holds it and event-store backups still hold the
   ciphertext — so **erasure is only complete once every backup/snapshot
   containing the key has rolled past its retention horizon**. Record the
   current effective erasure-completion horizon (= max of the two retention
   windows). If they have drifted apart, that is a finding — they MUST stay
   aligned so the documented horizon is honest.

---

## 7 · Record the drill — the DORA evidence artefact

Produce a dated record (the §P5 resilience-testing evidence). Keep it where the
ops/audit trail lives (not in this repo — it is operational data, not config):

```
DR drill — <YYYY-MM-DD>
Type: [monthly failover | quarterly full-restore]
Lead: <name>   Verifier: <name>

§3 Failover:        start <t0>  promoted <t1>  RTO=<…>  (target ≤15m)  RPO sentinel: PASS/FAIL
§4 PITR restore:    start <t0>  open <t1>   RPO-floor gap=<…s> (target ≤60s)  check: PASS/FAIL
§5 Projection rebuild: <t1>→<t2>  full-restore RTO=<…> (target ≤4h)  verify: PASS/FAIL
§6 Key store:       restore: PASS/FAIL   PII round-trip: PASS/FAIL
   crypto-shred horizon: event-store retention=<…d>  key-store retention=<…d>  ALIGNED: Y/N

Findings: <…>
Re-protection re-established (new warm standby): Y/N
```

A missed scheduled drill, an unmet target, or a misaligned retention horizon is
a **process incident** (§P5) — raise it, don't bury it.

---

## Cross-references

- [ADR-PC-005](../../docs/product-management/product_concepts/adrs/ADR-PC-005-dr-rto-rpo.md)
  — §P1 synchronous standby, §P2 PITR, §P3 rebuild-is-recovery, §P4 key-store DR
  + crypto-shred horizon, §P5 drills as DORA evidence.
- [ADR-PC-004 §P5](../../docs/product-management/product_concepts/adrs/ADR-PC-004-pii-crypto-shredding.md)
  — crypto-shredding; the backup-retention horizon bounds true erasure.
- [ADR-PC-003](../../docs/product-management/product_concepts/adrs/ADR-PC-003-postgresql-snapshots.md)
  — snapshots accelerate (but are not required for) cold replay.
- [event-store §7.2, §8.2](../../docs/product-management/product_concepts/feature-design-event-store-projections.md)
  — rebuild drills (extended to recovery drills); cold-replay budgets.
- [`infra/k8s/README.md`](../k8s/README.md) — the HA overlay this drill operates;
  M.4 is named there as the DR-drill/PITR lane.
