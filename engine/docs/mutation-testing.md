# Mutation testing — engine spine (A.10) + financial-math kernel (B.10)

Mutation testing measures *test effectiveness*, not coverage: Stryker.NET makes small
behaviour-changing edits ("mutants") to the source and re-runs the suite. A mutant the
tests still pass through ("survives") is a behaviour the suite does not actually pin.
Where a one-character slip is a correctness or data-integrity incident — the event-sourcing
spine and the money kernel both — surviving mutants are the signal that a test is asserting
less than it looks.

Two scopes share the one periodic lane, each with its own config and score floor:

- **Engine spine (A.10)** — `Babelstone.EventStore` + `Babelstone.Engine`, mutated under
  `stryker-config.json`. The companion to A.9's property suite: A.9 asserts invariants hold;
  A.10 asserts the tests would *notice* if they stopped holding.
- **Financial-math kernel (B.10)** — `Babelstone.FinancialTypes` (Money) +
  `Babelstone.FinancialMath` (day-count, accrual, withholding, rates), mutated under
  `stryker-config.kernel.json`. The kernel is pure — no clock, no I/O, no Docker — so its
  legs are fast, and its property + golden-corpus + boundary-fixture suite drives it to a
  **100 %** mutation score: every genuine mutant is killed. This is what "the suite would
  notice a wrong line, not merely cover it" means for `MONEY_BOUNDARY_FIXTURES`.

## How it runs

- **Periodic, never per-push.** Stryker re-executes the whole suite once per mutant, so it
  is far too slow for the PR gate. It runs weekly and on demand via
  `.github/workflows/mutation.yml` (`schedule` + `workflow_dispatch`), one matrix leg per
  mutated project. (The spine legs additionally re-run the Testcontainers integration tier
  each time, which is why they are the slow ones.)
- **Tool.** `dotnet-stryker` is pinned in `engine/.config/dotnet-tools.json`
  (`dotnet tool restore`). The pin must support the `mise.toml` .NET SDK — Stryker ≥ 4.14
  for .NET 10; older builds fail analysis with `Commandline could not be parsed`.
- **Config per scope.** Each matrix leg passes `--config-file`: the spine legs use
  `stryker-config.json`, the kernel legs `stryker-config.kernel.json`. Both carry reporters
  and thresholds; the kernel config additionally `ignore-mutations: [string]` (the kernel's
  only behavioural string is `Money.ToString`'s `"0.00"` format, separately pinned by a
  value assertion — every other string is an exception *message*, whose text is not a
  behavioural contract) and `ignore-methods: [ArgumentNullException.ThrowIfNull]` (null
  guards are not mutation-tested). Each leg also passes `--project` and `--test-project`.
- **Locally:** from `engine/`, `dotnet tool restore` then e.g. the kernel (no Docker):
  `dotnet tool run dotnet-stryker --project Babelstone.FinancialMath.csproj --test-project tests/Babelstone.FinancialMath.Tests/Babelstone.FinancialMath.Tests.csproj --config-file stryker-config.kernel.json`.
  The spine legs swap in `stryker-config.json` and need Docker (the integration tier kills
  most spine mutants).

## Score floors

Each scope carries its own `thresholds` block, because the achievable score differs: the
pure kernel is fully pinnable, the Docker-backed spine is held to a more modest start.

| Scope | Config | `break` | `low` | `high` |
|---|---|---|---|---|
| Engine spine (A.10) | `stryker-config.json` | 60 | 70 | 85 |
| Financial-math kernel (B.10) | `stryker-config.kernel.json` | 90 | 95 | 100 |

`break` is the hard gate — a run below it **fails** the lane. The kernel floor sits at 90
against an achieved 100 %: the headroom absorbs normal evolution (a new primitive landing a
few mutants ahead of its test) without masking real erosion. A floor starts at the
achievable score and ratchets **up** as triage closes gaps — it never moves down to
accommodate a regression. Lowering a `break` requires the same explicit-drift
acknowledgement as any other gate change.

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

## Financial-math kernel mutants of particular interest (B.10)

The kernel's whole job is to be arithmetically exact, so the dangerous mutants are the ones
that change a cent or a rate without changing a type:

- **Rounding direction at the Money boundary** — `MidpointRounding.ToEven` → `AwayFromZero`,
  or a midpoint case flipping. Killed by the `MONEY_BOUNDARY_FIXTURES` midpoint corpus.
- **Int64 overflow guards** — the `< long.MinValue` / `> long.MaxValue` edges in
  `Money.FromCents`, and the `checked` keyword on `+`/`-`/unary-`-`. A dropped `checked`
  silently *wraps* instead of throwing; killed by the exact-boundary fixtures and the
  overflow tests (one per operator). These are the textbook "coverage sees the line, only
  mutation proves the assertion" cases — `checked`'s only observable effect is the throw.
- **Day-count off-by-one** — the `min(D, 30)` cap in 30E/360, the `Days`/`Basis` selection
  per convention, or a `< 0` reversed-interval guard slipping to `<= 0`. Killed by the
  day-count tables and the zero-day / reversed-interval accrual tests.
- **Withholding flow-by-flow** — the basis-point arithmetic and the net = gross − tax
  residual. Killed by the withholding fixtures and the `Net + Tax == Gross` property.
- **The IRR/TAEG solver core** — `PresentValue` and `PresentValueAndDerivative`. These are
  the kernel's subtlest case: the public `InternalRateOfReturn` runs Newton-Raphson with a
  **bisection fallback**, so a corrupted PV or derivative is *invisible* through the public
  API — the other path rescues the root, and the root of −PV equals the root of PV. A
  black-box IRR test therefore cannot distinguish a correct solver core from several broken
  ones. The two helpers are `internal` (with `InternalsVisibleTo` to the test assembly) and
  pinned **by value** at a known rate, including a `t ≥ 2` flow so the derivative's period
  weighting (`cents * t`) is separated from its mutants (`cents / t`). TAEG (APR) is a
  regulated figure; this is the difference between a backstopped solver and a *proven* one.

## Surviving-mutant triage

When the lane reports survivors, work the HTML report (uploaded as the
`stryker-report-*` artifact) top-down:

1. **Genuine gap** — the most common case: add or strengthen a test that pins the
   mutated behaviour, then re-run. This is the intended outcome.
2. **Equivalent mutant** — the mutation produces behaviour indistinguishable from the
   original (no test *could* tell them apart). Each one is argued, not assumed, and marked
   ignored at the right granularity:
   - a **whole class** (e.g. exception-message text, null guards) declaratively in the
     config — `ignore-mutations` / `ignore-methods`, justified in this doc;
   - a **single site** with an inline `// Stryker disable once <Mutator>: <reason>` comment.
     The kernel uses two, both in `DecimalMath.Pow`: the signed→unsigned right-shift
     (`>>>=` is bit-identical for the non-negative `e` this loop holds) and the final
     loop-guard squaring (discarded as the loop exits; cannot overflow for near-unity bases).
3. **Unproductive code** — if a mutant survives because the code it touches has no
   observable effect, the code, not the test, is suspect.

Never raise `break` to make a red run green; close the gap or justify the equivalent.
