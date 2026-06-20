using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Babelstone.Engine;
using Babelstone.EventStore;
using Babelstone.FinancialMath;
using Babelstone.FinancialTypes;
using Babelstone.RateSheets;
using Xunit;
using Xunit.Abstractions;

namespace Babelstone.Families.TermDeposit.Application.Tests;

/// <summary>
/// Depth-5 simulation (C.3; ADR-PC-006 §P4, commitment-catalogue row 11
/// <c>PACK_SIM_DEPTH5_BUDGET</c>): drive the pinned pack's sealed test corpus
/// (<c>packs/pt.2026.1/test-corpus/canonical-instances.yaml</c>) through the engine's
/// own hand-rolled append/replay substrate against a session-scoped Testcontainers
/// PostgreSQL fixture, cold-replay each constituted stream, and assert the produced
/// event-type sequence is the one each interest shape is expected to emit — the whole
/// corpus completing in &lt; 30 s.
/// </summary>
/// <remarks>
/// <para>
/// This is the dynamic depth-5 sibling of the static depths 1–4 the Go validator
/// (pack-validate) runs: depths 1–4 check the variant against the schema and pack
/// constraints; depth 5 proves "engine + pack do what the brief claims" by actually
/// constituting the canonical instances and rebuilding their state from the durable log
/// (ADR-PC-006 §P4: "no CUE at depth 5 — it is a constraint language, not a simulator").
/// </para>
/// <para>
/// It builds ONLY on the existing rehydrate / ordered-stream-load substrate (A.3) and the
/// append-only events table (A.1), exactly as the E.3 happy-path and D.5 cold-replay tests
/// do — the lifecycle is driven by EXPLICIT commands (constitute → coupons → mature), never
/// by a clock/scheduler. The time-based clock-advance simulation (A.8b) is deliberately out
/// of scope and NOT pulled in here.
/// </para>
/// <para>
/// Determinism: each instance pins its own resolved <c>rate_basis_points</c> in the corpus,
/// so the simulation deploys a rate sheet pricing each product at exactly that rate (the
/// corpus is the source of truth for the deterministic regression, surface §2 / C.6). Every
/// date is a command INPUT, so the produced sequence is identical on every run and on every
/// CI host. The fixture is session-scoped (one container for the whole test class) to keep
/// the budget comfortable.
/// </para>
/// <para>
/// SEALED CORPUS (<c>expected-events.yaml</c>, F.8 / bd up7t): ADR-PC-007 §P5 names the sealed
/// <c>expected-events.yaml</c> as the GENERATED artefact this gate regenerates and compares against.
/// It is now GENERATED and a HARD gate (no longer the logged-skip placeholder): the depth-5 run
/// replays each canonical instance through the engine substrate, captures every decided event's
/// financial facts off the durable log (gross / withholding / net per FLOW, principal returned, total
/// payout, remaining principal — all integer cents), and asserts them field-for-field against the
/// committed <c>expected-events.yaml</c> via <see cref="AssertOrGenerateExpectedEvents"/>. A single
/// drifted cent fails CI. The figures are the engine's OWN flow-by-flow math (each coupon withholds
/// 2800 bp on its own gross, rounded once per cash-flow boundary, then summed — never a rate-scaled
/// total, financial_concepts §5.4), read back from the store rather than recomputed in the test, so the
/// corpus cannot be hand-fudged. Regenerate intentionally with <c>BABELSTONE_DEPTH5_GENERATE=1</c>. The
/// structural event-TYPE sequence is still asserted alongside (<see cref="ExpectedSequenceFor"/>), and
/// the budget half of the commitment (&lt; 30 s, the named <c>PACK_SIM_DEPTH5_BUDGET</c>) is exercised.
/// (The bus-published Avro codec's array-of-record limitation, bd babelstone-vcxq, only constrains the
/// BUS payload; this corpus seals the STORE-side decided figures, which carry no such limit.)
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class PackSimulationDepth5Tests(ConstitutionFixture fixture, ITestOutputHelper output)
    : IClassFixture<ConstitutionFixture>
{
    // The < 30 s aggregate ceiling for the full depth-5 corpus run (ADR-PC-006 §P3/§P4,
    // diag.AggregateBudget on the Go side). Overridable UPWARD only for an unusually slow
    // shared CI runner (documented escape hatch); never tightened below the spec.
    private static readonly TimeSpan Depth5Budget = ResolveBudget(TimeSpan.FromSeconds(30));

    [Fact]
    public async Task PACK_SIM_DEPTH5_BUDGET_corpus_replays_expected_sequences_under_30s()
    {
        var corpus = CanonicalCorpus.Load();
        Assert.NotEmpty(corpus.Instances);

        // One rate sheet prices every corpus product at the rate that instance pins (the engine
        // resolves the latest sheet effective for the FAMILY, not by product, so all live in one
        // sheet; the shared container means this is deployed once). Effective before every
        // constitution so as-of resolution always finds it.
        await fixture.EnsureRateSheetAsync(BuildCorpusSheet(corpus));

        var (runtime, service) = Compose(fixture.ConnectionString, EarlyTerminationPolicyFor(corpus));

        // F.8 (bd up7t): the generated sealed corpus of expected event sequences, captured flow-by-flow
        // from the engine substrate. Each instance's decided events (types + financial cents) are
        // accumulated here, then either WRITTEN to expected-events.yaml (the explicit regeneration run,
        // BABELSTONE_DEPTH5_GENERATE=1) or ASSERTED field-for-field against the committed file (the CI
        // gate — a HARD failure on drift, no logged skip).
        var captured = new List<(string TestId, string VariantId, IReadOnlyList<CapturedEvent> Events)>();

        var sw = Stopwatch.StartNew();
        foreach (var instance in corpus.Instances)
        {
            var depositId = Guid.NewGuid();
            await DriveLifecycleAsync(service, depositId, instance);

            // Cold replay through the durable substrate (A.3): read the committed event-type
            // sequence straight off the append-only log, in sequence order.
            var actualSequence = await ReadEventTypeSequenceAsync(fixture.ConnectionString, depositId);
            var expectedSequence = ExpectedSequenceFor(instance);

            Assert.True(
                expectedSequence.SequenceEqual(actualSequence),
                $"{instance.TestId} ({instance.VariantId}): expected event sequence " +
                $"[{string.Join(", ", expectedSequence)}] but the corpus replay produced " +
                $"[{string.Join(", ", actualSequence)}].");

            // Capture the FULL decided events (types + financial cents, flow-by-flow) for the sealed
            // expected-events corpus. Read straight off the durable log + deserialized through the family
            // registry, so the captured figures are exactly what the engine wrote — never recomputed here.
            captured.Add((instance.TestId, instance.VariantId,
                await ReadDecidedEventsAsync(fixture.ConnectionString, depositId)));

            // The fold rebuilds the expected end position. Most variants mature; the 18-month `resgate
            // escalonado` is driven to a BANDED early termination (its load-bearing behaviour, bd
            // babelstone-3h64), so its end state is TerminatedEarly; the `resgate parcial` variant is
            // driven through a PARTIAL withdrawal (F.12, bd k6r8.10) and STAYS Active — a partial
            // withdrawal is state-preserving (F.3), so it does not reach a terminal state. The
            // withdraw→mature re-base leg (bd babelstone-aviw) DOES run on to maturity. The pinned TAN
            // survives every fold.
            var variant = TermDepositVariants.For(instance.TestId, instance.VariantId);
            var expectedTerminal = variant.Lifecycle switch
            {
                SimulatedLifecycle.BandedEarlyTermination => DepositLifecycle.TerminatedEarly,
                SimulatedLifecycle.PartialWithdrawal => DepositLifecycle.Active,
                _ => DepositLifecycle.Matured,
            };
            var hydrated = await runtime.LoadAsync(depositId);
            Assert.Equal(expectedTerminal, hydrated.State.Lifecycle);
            Assert.Equal(instance.RateBasisPoints, hydrated.State.TanBasisPoints);

            // The load-bearing F.12 evidence (bd k6r8.10): the terminal fold carries the REDUCED
            // remaining principal (original − withdrawn) — proving DepositPartiallyWithdrawn replayed and
            // the fold applied it, not just that the event was appended. Holds for BOTH the state-preserving
            // leg (still Active) and the withdraw→mature leg (the matured RemainingPrincipal is the reduced
            // principal the payout returns, bd babelstone-aviw).
            if (variant.Lifecycle is SimulatedLifecycle.PartialWithdrawal
                or SimulatedLifecycle.PartialWithdrawalThenMature)
            {
                var withdrawal = variant.PartialWithdrawal!;
                Assert.Equal(
                    instance.PrincipalCents - withdrawal.WithdrawnCents,
                    hydrated.State.RemainingPrincipal.Cents);
            }

            // The re-base proof at the simulation level (bd babelstone-aviw): after withdraw→mature, the
            // matured payout returns the REDUCED principal PLUS the piecewise net interest — and that net
            // is STRICTLY LESS than the net a never-withdrawn deposit of the SAME original principal would
            // pay (interest re-based onto the smaller held principal for the post-withdrawal segment), so
            // the engine did not accrue on the withdrawn money. The exact figures are sealed in
            // expected-events.yaml; this is the direction-of-effect guard the corpus numbers must honour.
            if (variant.Lifecycle == SimulatedLifecycle.PartialWithdrawalThenMature)
            {
                Assert.Equal(DepositLifecycle.Matured, hydrated.State.Lifecycle);
                Assert.Equal(
                    hydrated.State.RemainingPrincipal + hydrated.State.NetInterest,
                    hydrated.State.TotalPayout);
                var fullTermNet = NeverWithdrawnNetInterestCents(instance, variant);
                Assert.True(
                    hydrated.State.NetInterest.Cents < fullTermNet,
                    $"{instance.TestId}: withdraw→mature net interest {hydrated.State.NetInterest.Cents}c must be " +
                    $"strictly less than the never-withdrawn full-principal net {fullTermNet}c (the re-base must " +
                    "have priced the post-withdrawal segment on the reduced principal, bd babelstone-aviw).");
            }
        }

        sw.Stop();
        output.WriteLine(
            $"PACK_SIM_DEPTH5_BUDGET: replayed {corpus.Instances.Count} canonical instances of " +
            $"{corpus.PackKey} in {sw.Elapsed.TotalMilliseconds:F0} ms (budget {Depth5Budget.TotalMilliseconds:F0} ms).");

        Assert.True(
            sw.Elapsed < Depth5Budget,
            $"depth-5 simulation of {corpus.Instances.Count} instances took {sw.Elapsed.TotalMilliseconds:F0} ms, " +
            $"over the §P4 budget of {Depth5Budget.TotalMilliseconds:F0} ms.");

        // F.8 (bd up7t): seal or assert the GENERATED expected-events corpus. The numbers were just
        // produced by the engine substrate flow-by-flow (the captured InterestPaid/WithholdingApplied
        // figures are each coupon's own 2800-bp withholding rounded once, never a rate-scaled total —
        // financial_concepts §5.4), so this leg is now a HARD assertion, not the old logged skip.
        AssertOrGenerateExpectedEvents(captured);
    }

    /// <summary>
    /// The F.8 sealed-corpus gate (bd up7t): compare the freshly-replayed event sequences against the
    /// committed <c>expected-events.yaml</c>, field-for-field, and FAIL CI on any drift — OR regenerate
    /// the file when <c>BABELSTONE_DEPTH5_GENERATE=1</c>. The committed corpus is the GENERATED artefact
    /// (ADR-PC-007 §P5); a single drifted cent (an interest-math regression, a withholding bug, a fold
    /// change) trips this gate. The empty <c>expected: []</c> placeholder is now rejected loud — the
    /// depth-5 leg no longer passes as a logged skip.
    /// </summary>
    private void AssertOrGenerateExpectedEvents(
        IReadOnlyList<(string TestId, string VariantId, IReadOnlyList<CapturedEvent> Events)> captured)
    {
        var repoRoot = RepoRoot();
        var path = ExpectedEventsCorpus.Path(repoRoot);

        if (Environment.GetEnvironmentVariable("BABELSTONE_DEPTH5_GENERATE") == "1")
        {
            File.WriteAllText(path, ExpectedEventsCorpus.Render(captured));
            output.WriteLine($"F.8: regenerated sealed corpus at {path} ({captured.Count} instances).");
            return;
        }

        var yaml = File.ReadAllText(path);
        Assert.False(
            ExpectedEventsCorpus.IsPlaceholder(yaml),
            $"expected-events.yaml at {path} is still the empty placeholder; regenerate it with " +
            "BABELSTONE_DEPTH5_GENERATE=1 (F.8, bd up7t — the depth-5 leg is a hard gate, not a logged skip).");

        var expected = ExpectedEventsCorpus.Parse(yaml);

        // Every canonical instance must have a sealed expected sequence (no silently-unsealed instance).
        foreach (var (testId, variantId, actualEvents) in captured)
        {
            Assert.True(
                expected.TryGetValue(testId, out var expectedEvents),
                $"{testId} ({variantId}) has no entry in the sealed expected-events.yaml; regenerate with " +
                "BABELSTONE_DEPTH5_GENERATE=1 (F.8, bd up7t).");

            Assert.True(
                expectedEvents!.Count == actualEvents.Count
                && expectedEvents.Zip(actualEvents).All(p => p.First.Matches(p.Second)),
                $"{testId} ({variantId}): the replayed event sequence drifted from the sealed corpus.\n" +
                $"  sealed:   [{string.Join("; ", expectedEvents.Select(e => e.Describe()))}]\n" +
                $"  replayed: [{string.Join("; ", actualEvents.Select(e => e.Describe()))}]");
        }

        // And the corpus must not seal an instance the run no longer produces (a deleted canonical input).
        var ran = captured.Select(c => c.TestId).ToHashSet(StringComparer.Ordinal);
        foreach (var sealedId in expected.Keys)
        {
            Assert.True(
                ran.Contains(sealedId),
                $"expected-events.yaml seals '{sealedId}', which the depth-5 run did not produce; " +
                "regenerate with BABELSTONE_DEPTH5_GENERATE=1 (F.8, bd up7t).");
        }
    }

    /// <summary>The repo root (carrying packs/pt.2026.1/pack.yaml) — the sealed corpus lives under it.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "packs", "pt.2026.1", "pack.yaml")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException(
                $"repo root (containing packs/pt.2026.1/pack.yaml) not found from {AppContext.BaseDirectory}");
    }

    /// <summary>
    /// Drive one canonical instance to a terminal state through EXPLICIT commands only — the same
    /// manual triggering the E.3 happy-path test uses (no clock-advance, A.8b out of scope).
    /// AT_MATURITY: constitute → mature. ADVANCE: constitute (pays interest up front) → mature.
    /// PERIODIC: constitute → pay every intermediate coupon → mature (the final coupon rides with
    /// the principal). BANDED early termination (the 18-month <c>resgate escalonado</c> variant,
    /// bd babelstone-3h64): constitute → break early on the resolved band schedule, so the
    /// first-match banded penalty path is actually replayed rather than mapped to a plain
    /// at-maturity shape. The interest shape, cadence, and (banded) break schedule are read off the
    /// variant the corpus names.
    /// </summary>
    private static async Task DriveLifecycleAsync(
        TermDepositConstitutionService service, Guid depositId, CanonicalInstance instance)
    {
        var variant = TermDepositVariants.For(instance.TestId, instance.VariantId);
        var startDate = DateOnly.FromDateTime(instance.ConstitutedAt.UtcDateTime);
        var maturityDate = startDate.AddDays(variant.TermDays);

        await service.ConstituteAsync(new ConstituteDepositCommand(
            DepositId: depositId,
            PrincipalCents: instance.PrincipalCents,
            ProductId: instance.VariantId,
            Role: "standard",
            TermDays: variant.TermDays,
            StartDate: startDate,
            ConstitutedAt: instance.ConstitutedAt,
            InterestVariant: variant.InterestVariant,
            AutoRenewalPolicy: "NONE",
            FundingAccount: "PT50-DDA-001",
            Actor: "depth5-sim",
            PaymentPeriodMonths: variant.PaymentPeriodMonths));

        // The banded `resgate escalonado` variant breaks early on the resolved schedule instead of
        // maturing — its distinctive behaviour (bd babelstone-3h64). The break date is an INPUT
        // (start + the band-selecting BreakAfterDays), so the band first-match is deterministic and
        // the produced sequence is identical on every run.
        if (variant.Lifecycle == SimulatedLifecycle.BandedEarlyTermination)
        {
            var termination = variant.EarlyTermination
                ?? throw new InvalidOperationException(
                    $"{instance.VariantId} is a BANDED early-termination variant but carries no resolved schedule.");
            var breakDate = startDate.AddDays(termination.BreakAfterDays);
            await service.TerminateEarlyAsync(new TerminateEarlyCommand(
                DepositId: depositId,
                TerminatedAt: new DateTimeOffset(breakDate, TimeOnly.MinValue, TimeSpan.Zero),
                PayoutAccount: "PT50-DDA-001",
                TerminationReason: "CUSTOMER_REQUEST",
                Actor: "depth5-sim"));
            return;
        }

        // The `resgate parcial` variant withdraws part of its principal instead of maturing (F.12, bd
        // k6r8.10). The withdrawal date (start + WithdrawAfterDays) and amount are INPUTS, so the band/
        // carência evaluation is deterministic and the produced sequence is identical on every run. The
        // F.12 policy is resolved engine-side from the deposit's product config (k6r8.8). A partial
        // withdrawal is STATE-PRESERVING (F.3): the deposit stays Active afterward, so this leg does NOT
        // run on to maturity — it ends at the withdrawal, the load-bearing event this corpus guards.
        if (variant.Lifecycle == SimulatedLifecycle.PartialWithdrawal)
        {
            var withdrawal = variant.PartialWithdrawal
                ?? throw new InvalidOperationException(
                    $"{instance.VariantId} is a PARTIAL-withdrawal variant but carries no resolved withdrawal inputs.");
            var withdrawnOn = startDate.AddDays(withdrawal.WithdrawAfterDays);
            await service.WithdrawPartiallyAsync(new PartialWithdrawCommand(
                DepositId: depositId,
                WithdrawnAt: new DateTimeOffset(withdrawnOn, TimeOnly.MinValue, TimeSpan.Zero),
                WithdrawnAmountCents: withdrawal.WithdrawnCents,
                Actor: "depth5-sim",
                // A deterministic command id (ADR-PC-029 slot 4) derived from the deposit's own id, so the
                // simulated withdrawal carries a stable Idempotency-Key without a clock/random read — the
                // partial-withdrawal append now dedupes on it (bd babelstone-9w0g).
                CommandId: DeterministicCommandId(depositId, "partial-withdrawal")));
            return;
        }

        // The withdraw→MATURE re-base leg (bd babelstone-aviw): withdraw part of the principal, then run
        // ON to maturity. This locks the F.12 re-base (bd emtr) at the simulation level — maturity must
        // accrue PIECEWISE on the principal actually held in each segment (full up to the withdrawal, the
        // reduced principal after), and the matured payout returns the reduced principal plus that
        // piecewise net. Same deterministic withdrawal inputs as the state-preserving leg, then maturity.
        if (variant.Lifecycle == SimulatedLifecycle.PartialWithdrawalThenMature)
        {
            var withdrawal = variant.PartialWithdrawal
                ?? throw new InvalidOperationException(
                    $"{instance.TestId} is a withdraw→mature variant but carries no resolved withdrawal inputs.");
            var withdrawnOn = startDate.AddDays(withdrawal.WithdrawAfterDays);
            await service.WithdrawPartiallyAsync(new PartialWithdrawCommand(
                DepositId: depositId,
                WithdrawnAt: new DateTimeOffset(withdrawnOn, TimeOnly.MinValue, TimeSpan.Zero),
                WithdrawnAmountCents: withdrawal.WithdrawnCents,
                Actor: "depth5-sim",
                CommandId: DeterministicCommandId(depositId, "partial-withdrawal")));
            // Fall through to the maturity append below (no early return) so the deposit runs to maturity.
        }

        if (variant.InterestVariant == "PERIODIC")
        {
            // Pay each intermediate coupon. The final coupon is paid WITH the principal at
            // maturity, so we stop at the last boundary strictly before maturity (the service
            // rejects a coupon whose window reaches the maturity date). Coupon dates are derived
            // from the start date + cadence and passed as inputs — deterministic, no clock read.
            var couponDate = startDate;
            while (true)
            {
                couponDate = couponDate.AddMonths(variant.PaymentPeriodMonths);
                if (couponDate >= maturityDate)
                {
                    break;
                }

                await service.PayInterestAsync(new PayInterestCommand(
                    DepositId: depositId,
                    PaidAt: new DateTimeOffset(couponDate, TimeOnly.MinValue, TimeSpan.Zero),
                    PayoutAccount: "PT50-DDA-001",
                    Actor: "depth5-sim"));
            }
        }

        await service.MatureAsync(new MatureDepositCommand(
            DepositId: depositId,
            MaturedAt: new DateTimeOffset(maturityDate, TimeOnly.MinValue, TimeSpan.Zero),
            PayoutAccount: "PT50-DDA-001",
            Actor: "depth5-sim"));
    }

    /// <summary>
    /// A deterministic command id (ADR-PC-029 slot 4 Idempotency-Key) derived from the deposit id and the
    /// command verb — a stable MD5-hashed Guid, so the simulated command carries a reproducible key with no
    /// clock/random read (the corpus must be deterministic). MD5 is used as a non-cryptographic hash-to-Guid
    /// here (a deterministic 16-byte digest), never as a security primitive.
    /// </summary>
    private static Guid DeterministicCommandId(Guid depositId, string verb)
    {
        var seed = Encoding.UTF8.GetBytes($"{depositId:N}:{verb}");
        return new Guid(MD5.HashData(seed));
    }

    /// <summary>
    /// The net interest a NEVER-WITHDRAWN AT_MATURITY deposit of the instance's ORIGINAL principal would
    /// pay over the full term — the counterfactual the withdraw→mature re-base must beat (bd babelstone-aviw).
    /// A single full-term flow on the whole principal (the same Act/360 + 2800-bp withholding the engine
    /// uses), so this is the comparison ceiling: the actual withdraw→mature net must be STRICTLY LESS,
    /// proving interest was re-based onto the reduced principal rather than accrued on the withdrawn money.
    /// This is a DIRECTION-of-effect guard only; the exact figures live in the sealed corpus.
    /// </summary>
    private static long NeverWithdrawnNetInterestCents(CanonicalInstance instance, VariantShape variant)
    {
        var start = DateOnly.FromDateTime(instance.ConstitutedAt.UtcDateTime);
        var maturity = start.AddDays(variant.TermDays);
        var gross = Accrual.SimpleInterest(
            new Money(instance.PrincipalCents), instance.RateBasisPoints,
            DayCount.Between(start, maturity, DayCountConvention.Act360));
        return Withholding.Withhold(gross, 2800).Net.Cents;
    }

    /// <summary>
    /// The event-type sequence each interest shape is expected to emit, in commit order. These are
    /// the engine's documented per-variant lifecycle shapes (Events.cs / the E.3 happy-path
    /// assertions), expressed as the durable <c>family.EventType</c> strings the log carries.
    /// </summary>
    private static IReadOnlyList<string> ExpectedSequenceFor(CanonicalInstance instance)
    {
        const string c = "term_deposit.DepositConstituted";
        const string accrued = "term_deposit.InterestAccrued";
        const string withheld = "term_deposit.WithholdingApplied";
        const string paid = "term_deposit.InterestPaid";
        const string matured = "term_deposit.DepositMatured";
        const string terminatedEarly = "term_deposit.DepositTerminatedEarly";
        const string partiallyWithdrawn = "term_deposit.DepositPartiallyWithdrawn";

        var variant = TermDepositVariants.For(instance.TestId, instance.VariantId);

        // The banded `resgate escalonado` variant breaks early rather than maturing: the elapsed
        // flow accrues+withholds, then the deposit settles net of the first-match band penalty and
        // closes with DepositTerminatedEarly (bd babelstone-3h64). Its terminal shape differs from
        // the maturing variants' DepositMatured, so the corpus replay asserts the break sequence.
        if (variant.Lifecycle == SimulatedLifecycle.BandedEarlyTermination)
        {
            return [c, accrued, withheld, terminatedEarly];
        }

        // The withdraw→MATURE re-base leg (bd babelstone-aviw): constitution, the principal-reducing
        // DepositPartiallyWithdrawn, then the AT_MATURITY close (accrue+withhold+mature) — but accrued
        // PIECEWISE on the reduced principal. The event SHAPE is the same as an at-maturity deposit with a
        // withdrawal spliced in; the re-based FIGURES are what the sealed corpus pins.
        if (variant.Lifecycle == SimulatedLifecycle.PartialWithdrawalThenMature)
        {
            return [c, partiallyWithdrawn, accrued, withheld, matured];
        }

        // The `resgate parcial` variant withdraws part of its principal and STAYS Active (F.12, bd
        // k6r8.10): a partial withdrawal is a principal reduction only — no accrual, withholding, or
        // settlement leg (02 §2.4.1) — so the sequence is exactly the constitution followed by the single
        // DepositPartiallyWithdrawn. It does NOT run on to maturity (the deposit is still open).
        if (variant.Lifecycle == SimulatedLifecycle.PartialWithdrawal)
        {
            return [c, partiallyWithdrawn];
        }

        switch (variant.InterestVariant)
        {
            case "AT_MATURITY":
                // constitute, then the single full-term flow accrues+withholds and matures.
                return [c, accrued, withheld, matured];

            case "ADVANCE":
                // constitute pays the full-term interest up front (a self-contained InterestPaid),
                // then maturity returns the principal alone.
                return [c, paid, matured];

            case "PERIODIC":
                // constitute, one self-contained InterestPaid per intermediate coupon, then the
                // final coupon rides at maturity as accrue+withhold+mature.
                var startDate = DateOnly.FromDateTime(instance.ConstitutedAt.UtcDateTime);
                var maturityDate = startDate.AddDays(variant.TermDays);
                var intermediateCoupons = 0;
                var couponDate = startDate;
                while (true)
                {
                    couponDate = couponDate.AddMonths(variant.PaymentPeriodMonths);
                    if (couponDate >= maturityDate)
                    {
                        break;
                    }

                    intermediateCoupons++;
                }

                var sequence = new List<string> { c };
                for (var i = 0; i < intermediateCoupons; i++)
                {
                    sequence.Add(paid);
                }

                sequence.AddRange([accrued, withheld, matured]);
                return sequence;

            default:
                throw new InvalidOperationException($"unhandled interest variant {variant.InterestVariant}");
        }
    }

    /// <summary>Read the committed event-type sequence off the append-only log, in sequence order
    /// (the ordered-stream-load contract A.3 guarantees), via the engine's own store.</summary>
    private static async Task<IReadOnlyList<string>> ReadEventTypeSequenceAsync(string connectionString, Guid depositId)
    {
        var store = new PostgresEventStore(connectionString);
        var types = new List<string>();
        await foreach (var envelope in store.LoadAsync(depositId))
        {
            types.Add(envelope.EventType);
        }

        return types;
    }

    /// <summary>
    /// Read the committed events off the append-only log, in sequence order, DESERIALIZE each through the
    /// family registry + the same JSON codec the runtime writes with, and capture its sealed financial
    /// facts (F.8, bd up7t). The figures are exactly what the engine wrote — read back from the durable
    /// store, never recomputed in the test — so the sealed corpus reflects the engine's flow-by-flow math.
    /// </summary>
    private static async Task<IReadOnlyList<CapturedEvent>> ReadDecidedEventsAsync(string connectionString, Guid depositId)
    {
        var store = new PostgresEventStore(connectionString);
        var registry = TermDepositFamilyModule.Registry();
        var serializer = new JsonEventSerializer();
        var events = new List<CapturedEvent>();
        await foreach (var envelope in store.LoadAsync(depositId))
        {
            if (!registry.TryResolveByEventType(envelope.EventType, out var registration))
            {
                throw new InvalidOperationException(
                    $"no handler registration for stored event type '{envelope.EventType}' on stream {depositId}.");
            }

            var decided = serializer.Decode(envelope.Payload, registration.PayloadType);
            events.Add(ExpectedEventsCorpus.Capture(decided));
        }

        return events;
    }

    /// <summary>One sheet pricing every corpus product at the rate that instance pins — the
    /// deterministic regression rate (surface §2), not live rate-sheet data.</summary>
    private static RateSheet BuildCorpusSheet(CanonicalCorpus corpus)
    {
        var pricings = corpus.Instances
            .Select(i => (i.VariantId, "standard", i.RateBasisPoints))
            .Distinct()
            .ToArray();

        return TestRateSheets.MultiPriced(
            versionId: $"{corpus.PackKey}-depth5-corpus",
            effectiveFrom: new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            pricings);
    }

    /// <summary>Compose the durable runtime + constitution service over the term-deposit family,
    /// loading the SAME committed pt.2026.1 pack the corpus pins (the depth-5 pack-load path,
    /// ADR-PC-006 §P4) — the identical composition root the E.3 happy-path test uses. The banded
    /// early-termination policy is the engine-instance config the `resgate escalonado` break resolves
    /// (ADR-PC-009 stand-in); the maturing variants never touch it (bd babelstone-3h64).</summary>
    private static (AggregateRuntime<DepositPosition> Runtime, TermDepositConstitutionService Service)
        Compose(string connectionString, EarlyTerminationPolicy? earlyTerminationPolicy)
    {
        var store = new PostgresEventStore(connectionString);
        var runtime = new AggregateRuntime<DepositPosition>(
            store, new EventStoreSink(store), TermDepositFamilyModule.Registry(),
            new JsonEventSerializer(), new NullPiiProtector(), TimeProvider.System,
            () => DepositPosition.Empty);
        var service = new TermDepositConstitutionService(
            runtime, new PostgresRateSheetStore(connectionString), new RecordingSettlementPort(),
            SkeletonPack.LoadPt2026(), dayCountPrimitive: "act_360", withholdingPrimitive: "irs_juros",
            earlyTerminationPolicy: earlyTerminationPolicy,
            // The F.12 partial-withdrawal policy rides on the product config (k6r8.8). ConstituteAsync
            // resolves it from the REAL resgate-parcial variant on disk through this store AT CONSTITUTION
            // and PINS it on the deposit; the withdrawal leg then reads the pinned policy off the position
            // — exercising the whole F.12 chain (schema → config → variant → wiring → corpus) end-to-end.
            // Harmless for the maturing/banded variants, which never withdraw.
            productConfigStore: new YamlProductConfigStore(productConfigsDir: null));
        return (runtime, service);
    }

    /// <summary>The single banded early-termination policy the corpus's break-early variant resolves
    /// to (the 18-month `resgate escalonado`), or <c>null</c> if the corpus names no banded variant.
    /// One policy per engine instance is the walking-skeleton stand-in (ADR-PC-009); the corpus only
    /// breaks the one banded variant, so a single resolved schedule serves the whole run.</summary>
    private static EarlyTerminationPolicy? EarlyTerminationPolicyFor(CanonicalCorpus corpus)
    {
        var banded = corpus.Instances
            .Select(i => TermDepositVariants.For(i.VariantId).EarlyTermination)
            .FirstOrDefault(t => t is not null);
        return banded?.Policy;
    }

    private static TimeSpan ResolveBudget(TimeSpan spec)
    {
        var overrideMs = Environment.GetEnvironmentVariable("BABELSTONE_DEPTH5_BUDGET_MS");
        if (overrideMs is not null && long.TryParse(overrideMs, out var ms) && ms > spec.TotalMilliseconds)
        {
            return TimeSpan.FromMilliseconds(ms);
        }

        return spec;
    }
}
