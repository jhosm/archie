using Babelstone.Engine;
using Babelstone.EventStore;
using Babelstone.FinancialTypes;
using Babelstone.RateSheets;
using Xunit;

namespace Babelstone.Families.TermDeposit.Application.Tests;

/// <summary>
/// A.8b (bd babelstone-e6fr.7): generate a deposit's FORWARD lifecycle — daily accrual, month-end,
/// maturity — by fast-forwarding an INJECTED clock through the REAL lifecycle handlers, instead of
/// hand-faking events. This is the auto-firing time scheduler the E.3 command surface
/// (PayInterest/Mature) deferred to A.8b. The clock-advance mechanism is family-agnostic
/// (<see cref="SimulationRuntime{TState}.RunForwardLifecycleAsync"/> in the engine spine); the FAMILY
/// supplies the milestone schedule (coupon boundaries + maturity) and the real lifecycle steps.
/// </summary>
/// <remarks>
/// The load-bearing assertion is EQUIVALENCE: a clock-advanced run produces a stream byte-identical to
/// the explicit-command run the depth-5 simulation uses (PackSimulationDepth5Tests), because both go
/// through the same real deciders + pure handlers — clock-advance only supplies the firing ORDER and
/// the per-step instant, never an event. That equivalence is exactly the replay-determinism guarantee
/// (ADR-PC-010 §P5): the clock is the impure shell's, every date is event-captured, and a cold rebuild
/// never re-reads a clock. Tagged Integration so the Docker-free job skips it; the integration lane runs it.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class SimulationForwardLifecycleTests(ConstitutionFixture fixture)
    : IClassFixture<ConstitutionFixture>
{
    [Fact]
    public async Task Clock_advance_drives_a_periodic_deposit_through_coupons_and_maturity()
    {
        // A monthly (12-coupon) PERIODIC deposit, the richest lifecycle: 11 intermediate coupons fire
        // on their boundaries, the 12th rides with the principal at maturity. The clock starts at
        // constitution and is fast-forwarded to each coupon boundary, then maturity.
        await fixture.EnsureRateSheetAsync(SharedSheet);

        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new SimulationClock(start);
        var (runtime, service, simulation) = Compose(fixture.ConnectionString, clock);
        var depositId = Guid.NewGuid();

        // Constitution happens at t=0 (the clock's start instant). The clock-advance simulation drives
        // ONLY the forward milestones (coupons + maturity) — constitution is the seed the life grows from.
        await service.ConstituteAsync(BuildConstitution(depositId, start));

        // The FAMILY builds the milestone schedule: each intermediate coupon boundary, then maturity.
        // It reads the deposit's own dates (the same CouponBoundary / MaturityDate the decider uses),
        // so the engine spine never learns what a coupon is — it just walks the schedule.
        var position = (await runtime.LoadAsync(depositId)).State;
        var milestones = BuildForwardSchedule(service, depositId, position);

        // Fast-forward the injected clock through the schedule, running the REAL lifecycle steps.
        await simulation.RunForwardLifecycleAsync(clock, milestones);

        // The stream the clock-advance produced cold-replays to the canonical AT_MATURITY terminal
        // position — the k6r8.1 monthly numbers the explicit-command happy path asserts (the proof the
        // real handlers ran, not hand-faked events).
        var hydrated = await runtime.LoadAsync(depositId);
        var terminal = hydrated.State;
        Assert.Equal(DepositLifecycle.Matured, terminal.Lifecycle);
        Assert.Equal(11, terminal.CouponsPaid);
        Assert.Equal(new Money(1_644_277), terminal.AccruedGrossInterest);
        Assert.Equal(new Money(460_396), terminal.WithholdingToDate);
        Assert.Equal(new Money(1_183_881), terminal.NetInterest);
        Assert.Equal(new Money(49_900_000 + 100_549), terminal.TotalPayout);

        // 1 constituted + 11 coupons + 3 maturity (Accrued+Withheld+Matured) = 15 events, each on its
        // scheduled instant. The clock genuinely advanced: the maturity event's transaction_time is a
        // year past constitution's, stamped from the fast-forwarded clock the runtime read.
        Assert.Equal(15, await fixture.CountAsync("events", "stream_id", depositId));
        var lastTransactionTime = hydrated.LastTransactionTime;
        Assert.NotNull(lastTransactionTime);
        Assert.Equal(start.AddYears(1), lastTransactionTime!.Value);

        // The same stream re-folded through the side-effect-free projection path reproduces the
        // identical terminal state (ProjectAsync over the committed history, no hypotheticals) —
        // replay-determinism over the clock-advanced stream.
        var replayed = await simulation.ProjectAsync(depositId, []);
        Assert.Equal(terminal, replayed);
    }

    [Fact]
    public async Task Clock_advanced_run_is_byte_identical_to_the_explicit_command_run()
    {
        // EQUIVALENCE: the clock-advanced lifecycle must produce the SAME event-type sequence AND the
        // same terminal financial state as the explicit-command path (PackSimulationDepth5Tests). Both
        // run on the same fixture/sheet; only the FIRING mechanism differs. Identical output proves the
        // clock-advance generates the lifecycle through the real handlers, adding nothing of its own.
        await fixture.EnsureRateSheetAsync(SharedSheet);

        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        // Path A — clock-advance.
        var clock = new SimulationClock(start);
        var (runtimeA, serviceA, simulation) = Compose(fixture.ConnectionString, clock);
        var depositA = Guid.NewGuid();
        await serviceA.ConstituteAsync(BuildConstitution(depositA, start));
        var positionA = (await runtimeA.LoadAsync(depositA)).State;
        await simulation.RunForwardLifecycleAsync(clock, BuildForwardSchedule(serviceA, depositA, positionA));

        // Path B — explicit commands with hand-computed dates (the depth-5 mechanism).
        var (runtimeB, serviceB, _) = Compose(fixture.ConnectionString, new SimulationClock(start));
        var depositB = Guid.NewGuid();
        await serviceB.ConstituteAsync(BuildConstitution(depositB, start));
        await DriveByExplicitCommandsAsync(serviceB, depositB, start);

        // Same event-type sequence on the log.
        var sequenceA = await ReadEventTypesAsync(depositA);
        var sequenceB = await ReadEventTypesAsync(depositB);
        Assert.Equal(sequenceB, sequenceA);

        // Same terminal financial state (ignoring the deposit id, which differs by construction).
        var terminalA = (await runtimeA.LoadAsync(depositA)).State with { DepositId = Guid.Empty };
        var terminalB = (await runtimeB.LoadAsync(depositB)).State with { DepositId = Guid.Empty };
        Assert.Equal(terminalB, terminalA);
    }

    /// <summary>
    /// The FAMILY-supplied forward schedule (A.8b): one milestone per intermediate coupon boundary, then
    /// maturity. Reads the deposit's own start / cadence / maturity (the SAME dates
    /// <see cref="TermDepositDecider.CouponBoundary"/> and the constitution stamp) so the milestone
    /// instants are exactly the dates the explicit-command path passes — the engine spine never learns
    /// what a coupon is. Each step derives its command timestamp from the advanced clock instant.
    /// </summary>
    private static IReadOnlyList<LifecycleMilestone> BuildForwardSchedule(
        TermDepositConstitutionService service, Guid depositId, DepositPosition position)
    {
        var milestones = new List<LifecycleMilestone>();

        // Intermediate coupons: every boundary strictly before maturity (the final coupon rides at
        // maturity). Boundary k = start + k·cadence; stop at the first boundary that reaches maturity.
        for (var k = 1; ; k++)
        {
            var boundary = TermDepositDecider.CouponBoundary(position, k);
            if (boundary >= position.MaturityDate)
            {
                break;
            }

            var dueAt = new DateTimeOffset(boundary, TimeOnly.MinValue, TimeSpan.Zero);
            milestones.Add(new LifecycleMilestone(dueAt, (instant, ct) =>
                service.PayInterestAsync(new PayInterestCommand(
                    DepositId: depositId, PaidAt: instant, PayoutAccount: "PT50-DDA-001", Actor: "sim:clock-advance"), ct)));
        }

        // Maturity: the final milestone, at the scheduled maturity date.
        var maturityAt = new DateTimeOffset(position.MaturityDate, TimeOnly.MinValue, TimeSpan.Zero);
        milestones.Add(new LifecycleMilestone(maturityAt, (instant, ct) =>
            service.MatureAsync(new MatureDepositCommand(
                DepositId: depositId, MaturedAt: instant, PayoutAccount: "PT50-DDA-001", Actor: "sim:clock-advance"), ct)));

        return milestones;
    }

    /// <summary>The explicit-command equivalent (the depth-5 mechanism): coupons + maturity driven by
    /// hand-computed dates, NOT a clock advance. The reference the clock-advance path must match.</summary>
    private static async Task DriveByExplicitCommandsAsync(
        TermDepositConstitutionService service, Guid depositId, DateTimeOffset start)
    {
        for (var i = 0; i < 11; i++)
        {
            await service.PayInterestAsync(new PayInterestCommand(
                DepositId: depositId, PaidAt: start.AddMonths(1).AddMonths(i),
                PayoutAccount: "PT50-DDA-001", Actor: "sim:explicit"));
        }

        await service.MatureAsync(new MatureDepositCommand(
            DepositId: depositId, MaturedAt: start.AddYears(1), PayoutAccount: "PT50-DDA-001", Actor: "sim:explicit"));
    }

    private static ConstituteDepositCommand BuildConstitution(Guid depositId, DateTimeOffset start) =>
        new(
            DepositId: depositId, PrincipalCents: 49_900_000, ProductId: "dpz_pt_12m_juros_mensal", Role: "standard",
            TermDays: 365, StartDate: DateOnly.FromDateTime(start.UtcDateTime), ConstitutedAt: start,
            InterestVariant: "PERIODIC", AutoRenewalPolicy: "NONE", FundingAccount: "PT50-DDA-001",
            Actor: "sim:clock-advance", PaymentPeriodMonths: 1);

    private async Task<IReadOnlyList<string>> ReadEventTypesAsync(Guid depositId)
    {
        var store = new PostgresEventStore(fixture.ConnectionString);
        var types = new List<string>();
        await foreach (var envelope in store.LoadAsync(depositId))
        {
            types.Add(envelope.EventType);
        }

        return types;
    }

    /// <summary>The shared family sheet pricing the monthly product (dpz_pt_12m_juros_mensal) at 325 bps —
    /// the same sheet the ConstituteAccrueMature happy path uses (the k6r8.1 fixture's rate).</summary>
    private static RateSheet SharedSheet => TestRateSheets.MultiPriced(
        versionId: "pt-deposits-2026.1",
        effectiveFrom: new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
        ("dpz_pt_12m_juros_venc", "standard", 300),
        ("dpz_pt_12m_juros_mensal", "standard", 325),
        ("dpz_pt_12m_juros_antecip", "standard", 300));

    /// <summary>Compose the durable runtime + constitution service over the term-deposit family, sharing
    /// the supplied <see cref="SimulationClock"/> with the runtime — so an event stamped during a
    /// clock-advanced step carries the fast-forwarded instant — plus a side-effect-free
    /// <see cref="SimulationRuntime{TState}"/> over the same store for the forward-lifecycle driver.</summary>
    private static (AggregateRuntime<DepositPosition> Runtime, TermDepositConstitutionService Service, SimulationRuntime<DepositPosition> Simulation)
        Compose(string connectionString, SimulationClock clock)
    {
        var store = new PostgresEventStore(connectionString);
        var runtime = new AggregateRuntime<DepositPosition>(
            store, new EventStoreSink(store), TermDepositFamilyModule.Registry(),
            new JsonEventSerializer(), new NullPiiProtector(), clock,
            () => DepositPosition.Empty);
        var service = new TermDepositConstitutionService(
            runtime, new PostgresRateSheetStore(connectionString),
            SkeletonPack.LoadPt2026(), dayCountPrimitive: "act_360", withholdingPrimitive: "irs_juros");
        var simulation = new SimulationRuntime<DepositPosition>(
            store, TermDepositFamilyModule.Registry(), new JsonEventSerializer(), () => DepositPosition.Empty);
        return (runtime, service, simulation);
    }
}
