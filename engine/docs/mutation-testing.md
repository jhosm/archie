# Mutation testing — `Babelstone.EventStore` + `Babelstone.Engine` (A.10)

Mutation testing measures *test effectiveness*, not coverage: Stryker.NET makes small
behaviour-changing edits ("mutants") to the source and re-runs the suite. A mutant the
tests still pass through ("survives") is a behaviour the suite does not actually pin.
For the engine spine — where a one-character slip is a correctness or data-integrity
incident — surviving mutants are the signal that a test is asserting less than it looks.

This is the periodic companion to A.9's property suite: A.9 asserts invariants hold;
A.10 asserts the tests would *notice* if they stopped holding.

## How it runs

- **Periodic, never per-push.** Stryker re-executes the whole suite (including the
  Testcontainers integration tier) once per mutant, so it is far too slow for the PR
  gate. It runs weekly and on demand via `.github/workflows/mutation.yml`
  (`schedule` + `workflow_dispatch`), one matrix leg per mutated project.
- **Tool + config.** `dotnet-stryker` is pinned in `engine/.config/dotnet-tools.json`
  (`dotnet tool restore`); shared thresholds and reporters live in
  `engine/stryker-config.json`. Each leg passes `--project` (the project to mutate) and
  `--test-project` (the suite that exercises it).
- **Locally:** from `engine/`, `dotnet tool restore` then e.g.
  `dotnet tool run dotnet-stryker --project Babelstone.EventStore.csproj --test-project tests/Babelstone.EventStore.Tests/Babelstone.EventStore.Tests.csproj`.
  Requires Docker (the integration tier kills most spine mutants).

## Score floor

The documented floor lives in `stryker-config.json` `thresholds`:

| Threshold | Value | Meaning |
|---|---|---|
| `break` | 60 | Hard gate — a run scoring below this **fails** the lane. |
| `low` | 70 | Below this, the score is reported amber. |
| `high` | 85 | At or above this, green. |

The floor starts deliberately modest and ratchets **up** as triage closes gaps — it
never moves down to accommodate a regression. Lowering `break` requires the same
explicit-drift acknowledgement as any other gate change.

## Event-sourcing mutants of particular interest

These are the mutations whose survival would be most dangerous in an append-only,
replayable store — the suite must kill them:

- **Off-by-one on `sequence_number`** — a mutated `expectedVersion + 1 + i` or a
  flipped comparison in the optimistic-concurrency check. Killed by the A.9
  monotonicity / no-gaps properties and the concurrency tests.
- **A dropped `ORDER BY sequence_number`** in `LoadAsync` — replay would fold events
  out of order. Killed by the ordered-load and deterministic-replay tests.
- **A removed transaction boundary** in `AppendAsync` — events committing without
  their outbox row (or vice versa). Killed by the `ES_ATOMIC_APPEND_OUTBOX`
  rollback test.
- **A weakened optimistic-concurrency check** (e.g. `!=` → always-false, or the
  UNIQUE-violation catch widened/narrowed) — a stale append slipping through. Killed
  by the stale-version-rejection test and the concurrency property.

A surviving mutant in any of these classes is a release blocker, not a backlog item.

## Surviving-mutant triage

When the lane reports survivors, work the HTML report (uploaded as the
`stryker-report-*` artifact) top-down:

1. **Genuine gap** — the most common case: add or strengthen a test that pins the
   mutated behaviour, then re-run. This is the intended outcome.
2. **Equivalent mutant** — the mutation produces behaviour indistinguishable from the
   original (no test *could* tell them apart). Mark it ignored in `stryker-config.json`
   with a one-line justification; equivalents are rare and each one is argued, not
   assumed.
3. **Unproductive code** — if a mutant survives because the code it touches has no
   observable effect, the code, not the test, is suspect.

Never raise `break` to make a red run green; close the gap or justify the equivalent.
