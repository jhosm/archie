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

        var (runtime, service) = Compose(fixture.ConnectionString);

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

            // The fold rebuilds a terminal position — the lifecycle ran to completion, not a stub.
            var hydrated = await runtime.LoadAsync(depositId);
            Assert.Equal(DepositLifecycle.Matured, hydrated.State.Lifecycle);
            Assert.Equal(instance.RateBasisPoints, hydrated.State.TanBasisPoints);
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
    /// Drive one canonical instance to a terminal (Matured) state through EXPLICIT commands only —
    /// the same manual triggering the E.3 happy-path test uses (no clock-advance, A.8b out of scope).
    /// AT_MATURITY: constitute → mature. ADVANCE: constitute (pays interest up front) → mature.
    /// PERIODIC: constitute → pay every intermediate coupon → mature (the final coupon rides with
    /// the principal). The interest shape and cadence are read off the variant the corpus names.
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

        var variant = TermDepositVariants.For(instance.VariantId);
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
    /// ADR-PC-006 §P4) — the identical composition root the E.3 happy-path test uses.</summary>
    private static (AggregateRuntime<DepositPosition> Runtime, TermDepositConstitutionService Service)
        Compose(string connectionString)
    {
        var store = new PostgresEventStore(connectionString);
        var runtime = new AggregateRuntime<DepositPosition>(
            store, new EventStoreSink(store), TermDepositFamilyModule.Registry(),
            new JsonEventSerializer(), new NullPiiProtector(), TimeProvider.System,
            () => DepositPosition.Empty);
        var service = new TermDepositConstitutionService(
            runtime, new PostgresRateSheetStore(connectionString), new RecordingSettlementPort(),
            SkeletonPack.LoadPt2026(), dayCountPrimitive: "act_360", withholdingPrimitive: "irs_juros");
        return (runtime, service);
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
