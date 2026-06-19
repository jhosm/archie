using System.Diagnostics;
using Babelstone.Engine;
using Babelstone.EventStore;
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
/// SCOPE NOTE on <c>expected-events.yaml</c>: ADR-PC-007 §P5 names the sealed
/// <c>expected-events.yaml</c> as the generated artefact this gate would regenerate and
/// compare against. The bus-published Avro codec enforces strict C#↔.avsc parity and has no
/// array-of-record support (see <see cref="DepositConstituted"/> remarks), so serialising a
/// full per-event payload corpus is bus-contract-widening work tracked separately (bd babelstone-vcxq). This gate
/// therefore asserts the structural EVENT-TYPE sequence each interest shape emits — the
/// regression-meaningful "did the engine produce the right lifecycle shape from the pack" —
/// rather than a byte corpus; <c>expected-events.yaml</c> stays the logged-skip placeholder
/// (pack.sh "generation pending") until that codec work lands. The budget half of the
/// commitment (&lt; 30 s, the named <c>PACK_SIM_DEPTH5_BUDGET</c> number) is fully exercised.
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

            // The fold rebuilds the expected end position. Most variants mature; the 18-month `resgate
            // escalonado` is driven to a BANDED early termination (its load-bearing behaviour, bd
            // babelstone-3h64), so its end state is TerminatedEarly; the `resgate parcial` variant is
            // driven through a PARTIAL withdrawal (F.12, bd k6r8.10) and STAYS Active — a partial
            // withdrawal is state-preserving (F.3), so it does not reach a terminal state. The pinned TAN
            // survives every fold.
            var variant = TermDepositVariants.For(instance.VariantId);
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
            // the fold applied it, not just that the event was appended.
            if (variant.Lifecycle == SimulatedLifecycle.PartialWithdrawal)
            {
                var withdrawal = variant.PartialWithdrawal!;
                Assert.Equal(
                    instance.PrincipalCents - withdrawal.WithdrawnCents,
                    hydrated.State.RemainingPrincipal.Cents);
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
        var variant = TermDepositVariants.For(instance.VariantId);
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
                Actor: "depth5-sim"));
            return;
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

        var variant = TermDepositVariants.For(instance.VariantId);

        // The banded `resgate escalonado` variant breaks early rather than maturing: the elapsed
        // flow accrues+withholds, then the deposit settles net of the first-match band penalty and
        // closes with DepositTerminatedEarly (bd babelstone-3h64). Its terminal shape differs from
        // the maturing variants' DepositMatured, so the corpus replay asserts the break sequence.
        if (variant.Lifecycle == SimulatedLifecycle.BandedEarlyTermination)
        {
            return [c, accrued, withheld, terminatedEarly];
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
            // The F.12 partial-withdrawal policy rides on the product config (k6r8.8), so the
            // partial-withdrawal leg resolves it from the REAL resgate-parcial variant on disk through
            // this store — exercising the whole F.12 chain (schema → config → variant → wiring) end-to-end,
            // not a pinned stand-in. Harmless for the maturing/banded variants, which never withdraw.
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
