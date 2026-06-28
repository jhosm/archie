# Mutation testing — engine spine (A.10) + financial-math kernel (B.10)

Mutation testing measures *test effectiveness*, not coverage: Stryker.NET makes small
behaviour-changing edits ("mutants") to the source and re-runs the suite. A mutant the
tests still pass through ("survives") is a behaviour the suite does not actually pin.
Where a one-character slip is a correctness or data-integrity incident — the event-sourcing
spine and the money kernel both — surviving mutants are the signal that a test is asserting
less than it looks.

Several scopes share the one periodic lane, each with its own config and score floor:

- **Engine spine (A.10)** — `Babelstone.EventStore` + `Babelstone.Engine`, mutated under
  `stryker-config.json`. The companion to A.9's property suite: A.9 asserts invariants hold;
  A.10 asserts the tests would *notice* if they stopped holding.
- **Financial-math kernel (B.10)** — `Babelstone.FinancialTypes` (Money) +
  `Babelstone.FinancialMath` (day-count, accrual, withholding, rates), mutated under
  `stryker-config.kernel.json`. The kernel is pure — no clock, no I/O, no Docker — so its
  legs are fast, and its property + golden-corpus + boundary-fixture suite drives it to a
  **100 %** mutation score: every genuine mutant is killed. This is what "the suite would
  notice a wrong line, not merely cover it" means for `MONEY_BOUNDARY_FIXTURES`.
- **Term-deposit family** — `Babelstone.Families.TermDeposit` +
  `Babelstone.Families.TermDeposit.Application`, mutated under `stryker-config.family.json`.
  The family's cent-math is pure — accrual schedule, withholding ledger, coupon/maturity
  arithmetic, and the lifecycle-transition legality table — so the config borrows the kernel's
  `string` / `ArgumentNullException.ThrowIfNull` ignores, but the family starts at a more modest
  floor (`break` 70) than the fully-pinned kernel. The `.Application` leg's read-model store
  carries a Testcontainers tier (real PostgreSQL), so this leg is Docker-backed.
- **Integration seams** — `Babelstone.OutboxPublisher` (the outbox→Redpanda drainer) +
  `Babelstone.InboxConsumer` (the consumer-side `message_id` dedupe + poison-message sink),
  mutated under `stryker-config.json`. Both touch PostgreSQL via the Testcontainers tier, so
  these legs are Docker-backed. The dangerous survivors mirror the spine's: a dropped
  drain-batch `ORDER BY`, a weakened dedupe predicate that lets a duplicate through, a
  poison-sink branch that never fires.
- **Boundary codec/data** — `Babelstone.RateSheets` (TAN resolution), `Babelstone.Packs`
  (strict pack parse), and `Babelstone.Engine.Avro` (codec field-binding), mutated under
  `stryker-config.json`. RateSheets/Packs carry Postgres/OCI Testcontainers tiers, so those
  legs are Docker-backed. `Babelstone.Engine.Avro`'s round-trip codec tests live in
  `Babelstone.OutboxPublisher.Tests` (`AvroCodecRoundTripTests`, `AvroCatalogSweepTests`), so
  that is the Avro leg's test project. Dangerous survivors: a flipped effective-date comparison
  in TAN resolution, a relaxed strict-parse rejection, a swapped Avro field-binding offset.

## How it runs

- **Periodic, never per-push.** Stryker re-executes the whole suite once per mutant, so it
  is far too slow for the PR gate. It runs weekly and on demand via
  `.github/workflows/mutation.yml` (`schedule` + `workflow_dispatch`), one matrix leg per
  mutated project. (The spine legs additionally re-run the Testcontainers integration tier
  each time, which is why they are the slow ones.)
- **Tool.** `dotnet-stryker` is pinned in `engine/.config/dotnet-tools.json`
  (`dotnet tool restore`). The pin must support the `mise.toml` .NET SDK — Stryker ≥ 4.14
  for .NET 10; older builds fail analysis with `Commandline could not be parsed`.
- **Config per scope.** Each matrix leg passes `--config-file`: the spine and the `Engine.Avro`
  boundary-codec leg use `stryker-config.json`; the kernel legs use `stryker-config.kernel.json`;
  the term-deposit family legs use `stryker-config.family.json`; and the three integration-seam /
  boundary-data legs whose mutation scope is narrowed (below) each carry their own thin config —
  `stryker-config.outbox.json`, `stryker-config.inbox.json`, `stryker-config.packs.json`. All carry
  reporters and thresholds; the kernel, family, **and the three seam/boundary configs**
  `ignore-mutations: [string]` and `ignore-methods: [ArgumentNullException.ThrowIfNull]` (null
  guards are not mutation-tested). The string ignore is the same call the kernel makes — an ignored
  string mutation is a *whole-literal* swap (Stryker replaces the entire literal with `""` or a
  marker, never a subtly-edited variant), so the only strings it would have flagged are exception
  *messages*, whose text is not a behavioural contract. A behavioural string (the drain-batch
  `ORDER BY` SQL, the dedupe predicate, a `"yyyy-MM-dd"` date format, a pack file path) is killed
  *anyway* by any test that exercises it, because a whole-literal swap breaks the query/parse
  outright — so ignoring the class does not create a blind spot for the seams' "dropped `ORDER BY`"
  or the packs' "relaxed strict-parse" mutants of interest. The configs differ in score floor — the
  kernel is fully pinned (`break` 90), the family + seam/boundary legs start modestly (`break` 70 /
  60). Each NON-kernel leg also passes `--project` and `--test-project`; `--project` is resolved by
  name against the test project's references, so the family legs name the bare csproj
  (`Babelstone.Families.TermDeposit.csproj`) while pointing `--test-project` at the `../families/...`
  path (the family projects are members of `engine/Babelstone.slnx`). For these legs the whole
  solution is built and its full test set runs against each mutant (more killing tests ⇒ higher
  score), which is why they are *not* run in Stryker "project mode" — that would drop the
  cross-project tests and collapse the score.
- **Operating mode — the pure-kernel exception (Money + FinancialMath).** These two legs run in
  Stryker **TEST-PROJECT context**, not solution context: the workflow sets `working-directory` to
  the kernel's own test project and passes only `--project` (no `--test-project`), so Stryker runs
  ONLY that one Docker-free suite. Solution context is wrong for them on two counts. (1) It dragged
  the WHOLE Docker-backed solution test set — the Postgres / OCI / Redpanda Testcontainers tiers of
  the sibling legs — through *every* mutant, which made the supposedly "pure, fast" kernel legs slow
  and, over the FinancialMath leg's 264 IRR/TAEG-solver mutants, exhausted the runner ("runner
  received a shutdown signal" — a resource kill, never a score miss). (2) The kernel is *self-pinned*
  by its own property / golden-corpus / boundary suite, so the cross-project tests add no kills it
  needs. Scoped, the legs finish in **seconds** with no Docker: Money holds **100 %** and
  FinancialMath **95.88 %**, both ≥ the 90 floor. FinancialMath's ~4 % gap is boundary mutants in
  `RateSchedule` / `Amortization` that sibling-family tests had been killing in solution mode — most
  are *equivalent* (a `>` vs `>=` inside a max/min ternary yields the identical bound); pinning the
  genuine ones (the zero-day-window guards) in the kernel's own suite and justifying the equivalents
  is a tracked ratchet (**bd babelstone-nihb**).
- **Mutation scope — what each leg does NOT mutate.** Pure hosting / configuration / observability /
  shell-adapter glue is carved out of the mutated set, leaving the behaviour the leg exists to guard.
  This is the [triage](#surviving-mutant-triage) "unproductive code" verdict applied declaratively: a
  `BackgroundService` poll loop, an options record, a DI/registration module, an OTel gauge, or a
  `Process.Start` adapter has no cent/sequence/ordering contract a unit test can pin, so its mutants
  are noise that masks the real signal (and, against the Docker tiers, dead weight on the runtime).
  In-tree legs express the carve-out as a config `mutate` exclude list; the **out-of-tree family
  legs** (their source lives under `../families/…`, which Stryker's file-glob — rooted at the
  `engine/` working dir — cannot reach) express it as an **inline `// Stryker disable all`** directive
  at the carved-out site instead. The carve-outs:
  - **Term-deposit family** (inline `// Stryker disable all`, NOT config — the family project is
    out-of-tree): `TermDepositFamilyModule.cs` and `TermDepositProjectionModule.cs` whole (the
    event-type→handler and projection-runner *registration* tables — DI wiring). The folds/cent-math —
    `AccrualSchedule`, `WithholdingLedger`, `LifecycleTransitions`, `MaturityCalendar`, `Handlers`,
    `DepositPosition` (including its hand-rolled `Equals`/`GetHashCode` and `WithErased`) — stay fully
    mutated. They are pinned by three family-leg suites: `HandlerFoldTests` (every position fold, so the
    lifecycle handlers are covered, not just the AT_MATURITY happy path); `DepositPositionEqualityTests`
    (the per-field value-equality + element-wise-timeline contract that backstops replay determinism,
    ADR-PC-010 §P5 — equality is hand-rolled, so it must be *tested* here, not carved out, because the
    determinism/parity suites that exercise it live in the SEPARATE `.Application` leg); and
    `EventMetadataTests` (each event's `IsLifecycleBoundary` snapshot-cut flag).
  - **Outbox publisher** (`stryker-config.outbox.json`): `OutboxRelayService.cs` +
    `DedupRetentionSweepService.cs` (the `BackgroundService` poll/backoff loops),
    `OutboxLagObserver.cs` (the §P4 OTel gauge), `OutboxRelayOptions.cs` (an options record).
    `OutboxDrainer.cs` — the drain-batch `ORDER BY` / `FOR UPDATE SKIP LOCKED` / produce-then-mark
    logic, the leg's reason to exist — stays mutated, as does `KafkaSaslOptions.cs`.
  - **Inbox consumer** (`stryker-config.inbox.json`): `InboxConsumerService.cs` (the
    `BackgroundService` loop), `InboxConsumerOptions.cs` + `InboxMessage.cs` (records). `InboxPump.cs`
    — the `message_id` dedupe + poison-sink core — and `KafkaSaslOptions.cs` stay mutated.
  - **Packs** (`stryker-config.packs.json`): `ProcessRunner.cs` (the `Process.Start` shell-out),
    `CosignPackVerifier.cs`, `OrasPackSource.cs`, `OciPackStore.cs` (the oras/cosign OCI adapters,
    exercised only by the Docker `OciPackStoreIntegrationTests`). `PackParser.cs` — the fail-loud
    structural strict-parse, the boundary-data mutant of interest — stays mutated, as do
    `VerifiedPack.cs` and `OciToolchainGuard.cs`.
- **Locally:** run `dotnet tool restore` once from `engine/` (the tool manifest lives there). The
  kernel legs run in TEST-PROJECT context, so `cd` into the kernel test project and pass only
  `--project` (no `--test-project`) — the no-Docker, seconds-fast path CI uses:
  `cd tests/Babelstone.FinancialMath.Tests && dotnet tool run dotnet-stryker --project Babelstone.FinancialMath.csproj --config-file ../../stryker-config.kernel.json`
  (Money is the same shape from `tests/Babelstone.FinancialTypes.Tests`).
  The pure term-deposit leg, by contrast, runs in SOLUTION context from `engine/` against the family config:
  `dotnet tool run dotnet-stryker --project Babelstone.Families.TermDeposit.csproj --test-project ../families/term-deposit/tests/Babelstone.Families.TermDeposit.Tests/Babelstone.Families.TermDeposit.Tests.csproj --config-file stryker-config.family.json`.
  The spine, integration-seam, boundary codec/data, and `.Application` family legs swap in (or
  stay on) `stryker-config.json` / the family config and need Docker (the Testcontainers tier
  kills most of their mutants).

## Score floors

Each scope carries its own `thresholds` block, because the achievable score differs: the
pure kernel is fully pinnable, the Docker-backed spine is held to a more modest start.

| Scope | Config | `break` | `low` | `high` |
|---|---|---|---|---|
| Engine spine (A.10) | `stryker-config.json` | 60 | 70 | 85 |
| Financial-math kernel (B.10) | `stryker-config.kernel.json` | 90 | 95 | 100 |
| Term-deposit family | `stryker-config.family.json` | 70 | 80 | 90 |
| Integration seam — outbox | `stryker-config.outbox.json` | 60 | 70 | 85 |
| Integration seam — inbox | `stryker-config.inbox.json` | 60 | 70 | 85 |
| Boundary data — packs | `stryker-config.packs.json` | 60 | 70 | 85 |
| Boundary codec — Engine.Avro | `stryker-config.json` | 60 | 70 | 85 |

`break` is the hard gate — a run below it **fails** the lane. The kernel floor sits at 90
against an achieved 100 % (Money) / 95.88 % (FinancialMath, in the scoped test-project mode above —
the remaining boundary mutants are tracked in bd babelstone-nihb): the headroom absorbs normal
evolution (a new primitive landing a few mutants ahead of its test) without masking real erosion. A floor starts at the
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
