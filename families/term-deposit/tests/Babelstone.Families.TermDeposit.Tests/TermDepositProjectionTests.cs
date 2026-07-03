using Babelstone.Engine;
using Babelstone.EventStore;
using Babelstone.FinancialTypes;
using Xunit;

namespace Babelstone.Families.TermDeposit.Tests;

/// <summary>
/// F.6 (babelstone-3kjl) projection tests: the term-deposit family now ships all four projections
/// (deposit position, accrual schedule, maturity calendar, withholding ledger). These cover the
/// three NEW projections — their pure folds, their financial-math discipline (accrual recorded
/// never recomputed; withholding flow-by-flow never rate-scaled, §5.4), and the runtime properties
/// the runner gives them (handler-skip, at-least-once idempotency, byte-identical rebuild).
/// </summary>
public sealed class TermDepositProjectionTests
{
    // --- The module wires four projections, each its own kind ---

    [Fact]
    public void Module_declares_four_projections_with_distinct_kinds()
    {
        var module = new TermDepositProjectionModule();
        var infra = new ProjectionInfra(new InMemoryProjectionStorage(), new JsonEventSerializer());

        var runners = module.CreateRunners(infra);

        Assert.Equal(4, runners.Count);
        var kinds = runners.Select(r => r.Kind).ToArray();
        Assert.Equal(
            new[]
            {
                "term_deposit.deposit_position",
                "term_deposit.accrual_schedule",
                "term_deposit.maturity_calendar",
                "term_deposit.withholding_ledger",
            },
            kinds);
        // A duplicate kind would throw at registry construction — assert all are async (v1 default).
        Assert.All(runners, r => Assert.Equal(ProjectionMode.Async, r.Mode));
        Assert.All(runners, r => Assert.Equal("term_deposit", r.Family));
    }

    // --- Accrual schedule: records flows AS RECORDED, never recomputes ---

    [Fact]
    public void AccrualSchedule_folds_at_maturity_single_flow_as_recorded()
    {
        var registry = TermDepositProjectionModule.AccrualScheduleRegistry();

        var schedule = Fold(AccrualSchedule.Empty, registry,
            new InterestAccrued(new Money(30_417), new DateOnly(2027, 1, 15)));

        var entry = Assert.Single(schedule.Entries);
        Assert.Equal(new Money(30_417), entry.GrossInterest);
        Assert.Equal(new DateOnly(2027, 1, 15), entry.AsOf);
        Assert.Equal("accrued", entry.Source);
        // The total is the recorded gross — no day-count, no rate-scaling re-derivation.
        Assert.Equal(new Money(30_417), schedule.TotalGrossAccrued);
    }

    [Fact]
    public void AccrualSchedule_accumulates_periodic_coupons_in_fold_order()
    {
        var registry = TermDepositProjectionModule.AccrualScheduleRegistry();
        var depositId = Guid.NewGuid();

        var schedule = AccrualSchedule.Empty;
        schedule = Fold(schedule, registry, new InterestPaid(depositId, new Money(7_500), new Money(2_100), new Money(5_400), new DateOnly(2026, 4, 1)));
        schedule = Fold(schedule, registry, new InterestPaid(depositId, new Money(7_583), new Money(2_123), new Money(5_460), new DateOnly(2026, 7, 1)));
        schedule = Fold(schedule, registry, new InterestPaid(depositId, new Money(7_667), new Money(2_147), new Money(5_520), new DateOnly(2026, 10, 1)));

        Assert.Equal(3, schedule.Entries.Count);
        Assert.All(schedule.Entries, e => Assert.Equal("coupon", e.Source));
        // Fold order preserved (append-only) — first coupon first.
        Assert.Equal(new DateOnly(2026, 4, 1), schedule.Entries[0].AsOf);
        Assert.Equal(new DateOnly(2026, 10, 1), schedule.Entries[2].AsOf);
        // Total is the SUM of the per-flow recorded grosses (never a single bulk recompute).
        Assert.Equal(new Money(7_500 + 7_583 + 7_667), schedule.TotalGrossAccrued);
    }

    [Fact]
    public void AccrualSchedule_ignores_non_accrual_events()
    {
        var registry = TermDepositProjectionModule.AccrualScheduleRegistry();

        // A constitution and a maturity carry no accrual — the runner skips them; the schedule
        // stays empty. (Asserted via the registry resolving no handler for those types.)
        Assert.False(registry.TryResolve("term_deposit.DepositConstituted", out _));
        Assert.False(registry.TryResolve("term_deposit.WithholdingApplied", out _));
        Assert.True(registry.TryResolve("term_deposit.InterestAccrued", out _));
        Assert.True(registry.TryResolve("term_deposit.InterestPaid", out _));
    }

    [Fact]
    public void AccrualSchedule_total_reconciles_with_deposit_position_accrued_gross()
    {
        // The accrual schedule folds the SAME GrossInterest the DepositPosition fold sums, so its
        // total equals the position's AccruedGrossInterest — the D.5 reconciliation property.
        var accrualRegistry = TermDepositProjectionModule.AccrualScheduleRegistry();
        var positionRegistry = TermDepositFamilyModule.Registry();
        var events = new DomainEvent[]
        {
            new InterestAccrued(new Money(10_000), new DateOnly(2026, 6, 1)),
            new InterestAccrued(new Money(10_005), new DateOnly(2026, 7, 1)),
        };

        var schedule = AccrualSchedule.Empty;
        var position = DepositPosition.Empty;
        foreach (var e in events)
        {
            schedule = Fold(schedule, accrualRegistry, e);
            position = FoldPosition(position, positionRegistry, e);
        }

        Assert.Equal(position.AccruedGrossInterest, schedule.TotalGrossAccrued);
    }

    // --- Maturity calendar: records dates AS RECORDED, never derives them ---

    [Fact]
    public void MaturityCalendar_constitution_records_start_and_scheduled_maturity()
    {
        var registry = TermDepositProjectionModule.MaturityCalendarRegistry();

        var calendar = Fold(MaturityCalendar.Empty, registry, new DepositConstituted(
            DepositId: Guid.NewGuid(), Principal: new Money(1_000_000), TanBasisPoints: 300,
            RateSheetVersionId: "rs-1", TermDays: 365, StartDate: new DateOnly(2026, 1, 15),
            MaturityDate: new DateOnly(2027, 1, 15), InterestVariant: "AT_MATURITY",
            AutoRenewalPolicy: "NONE"));

        Assert.Equal(2, calendar.Entries.Count);
        Assert.Equal(MaturityEventKind.Constituted, calendar.Entries[0].Kind);
        Assert.Equal(new DateOnly(2026, 1, 15), calendar.Entries[0].Date);
        Assert.Equal(MaturityEventKind.ScheduledMaturity, calendar.Entries[1].Kind);
        Assert.Equal(new DateOnly(2027, 1, 15), calendar.Entries[1].Date);
    }

    [Fact]
    public void MaturityCalendar_folds_the_full_lifecycle_into_dated_milestones()
    {
        var registry = TermDepositProjectionModule.MaturityCalendarRegistry();
        var depositId = Guid.NewGuid();

        var calendar = MaturityCalendar.Empty;
        calendar = Fold(calendar, registry, new DepositConstituted(
            depositId, new Money(1_000_000), 300, "rs-1", 365, new DateOnly(2026, 1, 1),
            new DateOnly(2027, 1, 1), "PERIODIC", "NONE", PaymentPeriodMonths: 3));
        calendar = Fold(calendar, registry, new InterestPaid(depositId, new Money(7_500), new Money(2_100), new Money(5_400), new DateOnly(2026, 4, 1)));
        calendar = Fold(calendar, registry, new DepositPartiallyWithdrawn(depositId, new Money(200_000), new Money(800_000), new DateOnly(2026, 5, 1)));
        calendar = Fold(calendar, registry, new DepositMatured(new Money(800_000), new Money(5_400), new Money(805_400), new DateOnly(2027, 1, 1)));

        Assert.Equal(
            new[]
            {
                (MaturityEventKind.Constituted, new DateOnly(2026, 1, 1)),
                (MaturityEventKind.ScheduledMaturity, new DateOnly(2027, 1, 1)),
                (MaturityEventKind.CouponPaid, new DateOnly(2026, 4, 1)),
                (MaturityEventKind.PartiallyWithdrawn, new DateOnly(2026, 5, 1)),
                (MaturityEventKind.Matured, new DateOnly(2027, 1, 1)),
            },
            calendar.Entries.Select(e => (e.Kind, e.Date)).ToArray());
    }

    [Fact]
    public void MaturityCalendar_renewal_records_the_new_maturity_date()
    {
        var registry = TermDepositProjectionModule.MaturityCalendarRegistry();

        var calendar = Fold(MaturityCalendar.Empty, registry, new DepositRenewed(
            DepositId: Guid.NewGuid(), NewDepositId: Guid.NewGuid(), RolloverPrincipal: new Money(1_000_000),
            NewRateSheetVersionId: "rs-2", NewTanBasisPoints: 300, NewTermDays: 365,
            RenewalDate: new DateOnly(2027, 1, 1), NewMaturityDate: new DateOnly(2028, 1, 1)));

        var entry = Assert.Single(calendar.Entries);
        Assert.Equal(MaturityEventKind.Renewed, entry.Kind);
        Assert.Equal(new DateOnly(2028, 1, 1), entry.Date);
    }

    // --- Withholding ledger: flow-by-flow, never rate-scaled (financial-math §5.4) ---

    [Fact]
    public void WithholdingLedger_at_maturity_split_records_one_conserved_flow()
    {
        var registry = TermDepositProjectionModule.WithholdingLedgerRegistry();

        // Withhold(30_417, 2800) → Tax 8_517, Net 21_900 (the §5.4 single at-maturity split).
        var ledger = Fold(WithholdingLedger.Empty, registry, new WithholdingApplied(new Money(8_517), new Money(21_900)));

        var entry = Assert.Single(ledger.Entries);
        Assert.Equal(new Money(8_517), entry.Tax);
        Assert.Equal(new Money(21_900), entry.Net);
        // Gross is reconstructed by adding the recorded legs (exact integer arithmetic), conserved.
        Assert.Equal(new Money(30_417), entry.Gross);
        Assert.Equal(entry.Tax + entry.Net, entry.Gross);
        Assert.Equal("withholding", entry.Source);
        Assert.Equal(new Money(30_417), ledger.TotalGross);
        Assert.Equal(new Money(8_517), ledger.TotalTax);
        Assert.Equal(new Money(21_900), ledger.TotalNet);
    }

    [Fact]
    public void WithholdingLedger_withholds_each_periodic_coupon_flow_by_flow()
    {
        // §5.4: for a multi-period deposit, withholding is applied to EACH interest payment as it
        // accrues — the realised net is the SUM of per-flow nets, NOT the rate scaled over a gross
        // total. This is the load-bearing financial-math correctness property for the ledger.
        var registry = TermDepositProjectionModule.WithholdingLedgerRegistry();
        var depositId = Guid.NewGuid();

        // Three coupons; each gross/tax/net is the command-side per-flow Withhold result. The
        // grosses do not divide evenly by the rate, so a per-flow ledger and a bulk recompute
        // would disagree — the per-flow sum is the correct one.
        var coupons = new[]
        {
            new InterestPaid(depositId, new Money(7_501), new Money(2_100), new Money(5_401), new DateOnly(2026, 4, 1)),
            new InterestPaid(depositId, new Money(7_503), new Money(2_101), new Money(5_402), new DateOnly(2026, 7, 1)),
            new InterestPaid(depositId, new Money(7_505), new Money(2_101), new Money(5_404), new DateOnly(2026, 10, 1)),
        };

        var ledger = WithholdingLedger.Empty;
        foreach (var coupon in coupons)
        {
            ledger = Fold(ledger, registry, coupon);
        }

        Assert.Equal(3, ledger.Entries.Count);
        Assert.All(ledger.Entries, e => Assert.Equal("coupon", e.Source));
        // Totals are the per-flow sums, conserved gross = tax + net at the ledger level too.
        Assert.Equal(new Money(7_501 + 7_503 + 7_505), ledger.TotalGross);
        Assert.Equal(new Money(2_100 + 2_101 + 2_101), ledger.TotalTax);
        Assert.Equal(new Money(5_401 + 5_402 + 5_404), ledger.TotalNet);
        Assert.Equal(ledger.TotalTax + ledger.TotalNet, ledger.TotalGross);
    }

    [Fact]
    public void WithholdingLedger_totals_reconcile_with_deposit_position()
    {
        // The ledger folds the same tax/net the DepositPosition sums; totals must match (D.5).
        var ledgerRegistry = TermDepositProjectionModule.WithholdingLedgerRegistry();
        var positionRegistry = TermDepositFamilyModule.Registry();
        var depositId = Guid.NewGuid();
        var events = new DomainEvent[]
        {
            new InterestPaid(depositId, new Money(7_501), new Money(2_100), new Money(5_401), new DateOnly(2026, 4, 1)),
            new WithholdingApplied(new Money(8_517), new Money(21_900)),
        };

        var ledger = WithholdingLedger.Empty;
        var position = DepositPosition.Empty;
        foreach (var e in events)
        {
            ledger = Fold(ledger, ledgerRegistry, e);
            position = FoldPosition(position, positionRegistry, e);
        }

        Assert.Equal(position.WithholdingToDate, ledger.TotalTax);
        Assert.Equal(position.NetInterest, ledger.TotalNet);
    }

    // --- Pre-field attribution: an un-dated WithholdingApplied inherits its paired accrual's date ---

    [Fact]
    public void WithholdingLedger_pre_field_flow_is_dated_from_its_paired_accrual()
    {
        // A WithholdingApplied stored before the WithheldOn field existed replays with the default date.
        // The decider emits it immediately after the InterestAccrued it withholds, same date, gross
        // conserved — so the fold recovers the date from that paired accrual (event-derived, no clock).
        var registry = TermDepositProjectionModule.WithholdingLedgerRegistry();

        var ledger = WithholdingLedger.Empty;
        ledger = Fold(ledger, registry, new InterestAccrued(new Money(30_417), new DateOnly(2025, 7, 1)));
        ledger = Fold(ledger, registry, new WithholdingApplied(new Money(8_517), new Money(21_900)));

        var entry = Assert.Single(ledger.Entries);
        Assert.Equal(new DateOnly(2025, 7, 1), entry.WithheldOn);
        // Attribution assigns ONLY the date — the recorded amounts and their conservation are untouched.
        Assert.Equal(new Money(30_417), entry.Gross);
        Assert.Equal(new Money(8_517), entry.Tax);
        Assert.Equal(new Money(21_900), entry.Net);
        Assert.Equal(entry.Tax + entry.Net, entry.Gross);
        Assert.Equal(new Money(30_417), ledger.TotalGross);
        Assert.Equal(new Money(8_517), ledger.TotalTax);
        Assert.Equal(new Money(21_900), ledger.TotalNet);
        // The slot is consumed by the withholding flow that used it.
        Assert.Null(ledger.PendingAccrual);
    }

    [Fact]
    public void WithholdingLedger_dated_flow_keeps_its_own_stamp_over_the_pending_accrual()
    {
        // A post-field flow carries its own WithheldOn; the pending accrual must never override it.
        // The dates differ here only to PIN the precedence — the decider stamps both with the same date.
        var registry = TermDepositProjectionModule.WithholdingLedgerRegistry();

        var ledger = WithholdingLedger.Empty;
        ledger = Fold(ledger, registry, new InterestAccrued(new Money(30_417), new DateOnly(2025, 7, 1)));
        ledger = Fold(ledger, registry, new WithholdingApplied(new Money(8_517), new Money(21_900), new DateOnly(2025, 7, 2)));

        var entry = Assert.Single(ledger.Entries);
        Assert.Equal(new DateOnly(2025, 7, 2), entry.WithheldOn);
        Assert.Null(ledger.PendingAccrual);
    }

    [Fact]
    public void WithholdingLedger_refuses_attribution_when_the_gross_does_not_conserve()
    {
        // The conservation cross-check: the paired accrual's gross must equal the flow's Tax + Net to the
        // cent. A mismatched accrual is NOT this flow's pair, so the entry stays un-dated and the statement
        // rule's fail-loud guard surfaces it — a wrong statutory tax year is worse than a loud stop.
        var registry = TermDepositProjectionModule.WithholdingLedgerRegistry();

        var ledger = WithholdingLedger.Empty;
        ledger = Fold(ledger, registry, new InterestAccrued(new Money(30_418), new DateOnly(2025, 7, 1)));
        ledger = Fold(ledger, registry, new WithholdingApplied(new Money(8_517), new Money(21_900)));

        var entry = Assert.Single(ledger.Entries);
        Assert.Equal(default, entry.WithheldOn);
    }

    [Fact]
    public void WithholdingLedger_pre_field_flow_with_no_paired_accrual_stays_undated()
    {
        var registry = TermDepositProjectionModule.WithholdingLedgerRegistry();

        var ledger = Fold(WithholdingLedger.Empty, registry, new WithholdingApplied(new Money(8_517), new Money(21_900)));

        var entry = Assert.Single(ledger.Entries);
        Assert.Equal(default, entry.WithheldOn);
    }

    [Fact]
    public void WithholdingLedger_pending_accrual_is_consume_once()
    {
        // One accrual dates exactly one withholding flow. A second un-dated flow with the same amounts
        // must NOT inherit the already-consumed date — it stays un-dated and surfaces loud downstream.
        var registry = TermDepositProjectionModule.WithholdingLedgerRegistry();

        var ledger = WithholdingLedger.Empty;
        ledger = Fold(ledger, registry, new InterestAccrued(new Money(30_417), new DateOnly(2025, 7, 1)));
        ledger = Fold(ledger, registry, new WithholdingApplied(new Money(8_517), new Money(21_900)));
        ledger = Fold(ledger, registry, new WithholdingApplied(new Money(8_517), new Money(21_900)));

        Assert.Equal(2, ledger.Entries.Count);
        Assert.Equal(new DateOnly(2025, 7, 1), ledger.Entries[0].WithheldOn);
        Assert.Equal(default, ledger.Entries[1].WithheldOn);
    }

    [Fact]
    public void WithholdingLedger_per_tax_year_slices_sum_exactly_to_the_recorded_totals()
    {
        // The financial-math property the annual statement depends on: attribution assigns each flow to
        // exactly one tax year and never touches an amount, so the per-year slices PARTITION the ledger —
        // summed across years they reproduce the recorded totals to the cent (integer cents, no rounding).
        var registry = TermDepositProjectionModule.WithholdingLedgerRegistry();

        var ledger = WithholdingLedger.Empty;
        // A pre-field pair attributed into 2025 …
        ledger = Fold(ledger, registry, new InterestAccrued(new Money(30_417), new DateOnly(2025, 12, 31)));
        ledger = Fold(ledger, registry, new WithholdingApplied(new Money(8_517), new Money(21_900)));
        // … a second pre-field pair attributed into 2026 …
        ledger = Fold(ledger, registry, new InterestAccrued(new Money(7_501), new DateOnly(2026, 6, 30)));
        ledger = Fold(ledger, registry, new WithholdingApplied(new Money(2_100), new Money(5_401)));
        // … and a dated coupon in 2026.
        ledger = Fold(ledger, registry, new InterestPaid(Guid.NewGuid(), new Money(7_503), new Money(2_101), new Money(5_402), new DateOnly(2026, 10, 1)));

        Assert.All(ledger.Entries, e => Assert.NotEqual(default, e.WithheldOn));

        var y2025 = ledger.Entries.Where(e => e.WithheldOn.Year == 2025).ToList();
        var y2026 = ledger.Entries.Where(e => e.WithheldOn.Year == 2026).ToList();
        Assert.Equal(ledger.Entries.Count, y2025.Count + y2026.Count);

        Assert.Equal(new Money(8_517), new Money(y2025.Sum(e => e.Tax.Cents)));
        Assert.Equal(new Money(2_100 + 2_101), new Money(y2026.Sum(e => e.Tax.Cents)));
        // Per-year sums recompose the running totals exactly — gross, tax, and net.
        Assert.Equal(ledger.TotalTax.Cents, y2025.Sum(e => e.Tax.Cents) + y2026.Sum(e => e.Tax.Cents));
        Assert.Equal(ledger.TotalGross.Cents, y2025.Sum(e => e.Gross.Cents) + y2026.Sum(e => e.Gross.Cents));
        Assert.Equal(ledger.TotalNet.Cents, y2025.Sum(e => e.Net.Cents) + y2026.Sum(e => e.Net.Cents));
    }

    // --- Runtime properties via the real ProjectionRunner over an in-memory store ---

    [Fact]
    public async Task Runner_skips_unhandled_event_types_leaving_the_belief_unchanged()
    {
        var storage = new InMemoryProjectionStorage();
        var runner = WithholdingRunner(storage);
        var streamId = Guid.NewGuid();

        // A DepositConstituted is NOT a withholding flow — the runner must skip it (no row written).
        await runner.ApplyAsync(Envelope(streamId, 0, "term_deposit.DepositConstituted",
            new DepositConstituted(streamId, new Money(1_000_000), 300, "rs-1", 365,
                new DateOnly(2026, 1, 1), new DateOnly(2027, 1, 1), "AT_MATURITY", "NONE")));

        Assert.Null(await storage.ReadCurrentBeliefAsync(streamId, TermDepositProjectionModule.WithholdingLedgerKind));
    }

    [Fact]
    public async Task Runner_is_idempotent_under_at_least_once_redelivery()
    {
        var storage = new InMemoryProjectionStorage();
        var runner = WithholdingRunner(storage);
        var serializer = new JsonStateSerializer<WithholdingLedger>();
        var streamId = Guid.NewGuid();

        var seq0 = Envelope(streamId, 0, "term_deposit.WithholdingApplied", new WithholdingApplied(new Money(8_517), new Money(21_900)));
        var seq1 = Envelope(streamId, 1, "term_deposit.InterestPaid",
            new InterestPaid(streamId, new Money(7_500), new Money(2_100), new Money(5_400), new DateOnly(2026, 4, 1)));

        await runner.ApplyAsync(seq0);
        await runner.ApplyAsync(seq1);
        await runner.ApplyAsync(seq1); // crash-replay of seq1 — the source_sequence guard must skip it

        var record = await storage.ReadCurrentBeliefAsync(streamId, TermDepositProjectionModule.WithholdingLedgerKind);
        var ledger = serializer.Deserialize(record!.StructuralPayload);
        // Two flows, not three — the re-delivered event did not double-count.
        Assert.Equal(2, ledger.Entries.Count);
        Assert.Equal(new Money(8_517 + 2_100), ledger.TotalTax);
    }

    [Fact]
    public async Task Runner_attributes_a_pre_field_flow_across_the_stored_state_round_trip()
    {
        // The pending-accrual slot must SURVIVE the store round trip: the runner persists the belief after
        // the InterestAccrued (arming the slot) and reads it back before folding the pre-field
        // WithholdingApplied — so attribution works event-by-event through the real runner + JSON state
        // serializer, exactly how a projection rebuild replays a legacy stream.
        var storage = new InMemoryProjectionStorage();
        var runner = WithholdingRunner(storage);
        var serializer = new JsonStateSerializer<WithholdingLedger>();
        var streamId = Guid.NewGuid();

        await runner.ApplyAsync(Envelope(streamId, 0, "term_deposit.InterestAccrued",
            new InterestAccrued(new Money(30_417), new DateOnly(2025, 7, 1))));
        await runner.ApplyAsync(Envelope(streamId, 1, "term_deposit.WithholdingApplied",
            new WithholdingApplied(new Money(8_517), new Money(21_900))));

        var record = await storage.ReadCurrentBeliefAsync(streamId, TermDepositProjectionModule.WithholdingLedgerKind);
        var ledger = serializer.Deserialize(record!.StructuralPayload);
        var entry = Assert.Single(ledger.Entries);
        Assert.Equal(new DateOnly(2025, 7, 1), entry.WithheldOn);
        Assert.Null(ledger.PendingAccrual);
    }

    [Fact]
    public async Task Runner_rebuild_reproduces_a_byte_identical_belief()
    {
        // Folds are deterministic and every stamp is event-derived, so re-folding the same events
        // yields a byte-for-byte identical structural payload (ADR-PC-010 §P5).
        var streamId = Guid.NewGuid();
        var events = new[]
        {
            Envelope(streamId, 0, "term_deposit.WithholdingApplied", new WithholdingApplied(new Money(8_517), new Money(21_900))),
            Envelope(streamId, 1, "term_deposit.InterestPaid", new InterestPaid(streamId, new Money(7_500), new Money(2_100), new Money(5_400), new DateOnly(2026, 4, 1))),
        };

        var first = new InMemoryProjectionStorage();
        var firstRunner = WithholdingRunner(first);
        foreach (var e in events)
        {
            await firstRunner.ApplyAsync(e);
        }

        var second = new InMemoryProjectionStorage();
        var secondRunner = WithholdingRunner(second);
        foreach (var e in events)
        {
            await secondRunner.ApplyAsync(e);
        }

        var a = await first.ReadCurrentBeliefAsync(streamId, TermDepositProjectionModule.WithholdingLedgerKind);
        var b = await second.ReadCurrentBeliefAsync(streamId, TermDepositProjectionModule.WithholdingLedgerKind);
        Assert.Equal(a!.StructuralPayload.ToArray(), b!.StructuralPayload.ToArray());
        Assert.Equal(a.RecordedAt, b.RecordedAt);
        Assert.Equal(a.ValidFrom, b.ValidFrom);
        Assert.Equal(a.SourceSequence, b.SourceSequence);
    }

    // --- helpers ---

    private static IProjectionRunner WithholdingRunner(IProjectionStorage storage) =>
        new ProjectionRunner<WithholdingLedger>(
            kind: TermDepositProjectionModule.WithholdingLedgerKind,
            family: "term_deposit",
            mode: ProjectionMode.Async,
            handlers: TermDepositProjectionModule.WithholdingLedgerRegistry(),
            serializer: new JsonEventSerializer(),
            seed: () => WithholdingLedger.Empty,
            store: new ProjectionStore<WithholdingLedger>(storage, new JsonStateSerializer<WithholdingLedger>()));

    private static TState Fold<TState>(TState state, HandlerRegistry registry, DomainEvent @event)
        where TState : class
    {
        var eventType = $"term_deposit.{@event.GetType().Name}";
        Assert.True(registry.TryResolve(eventType, out var handler), $"no handler for {eventType}");
        return (TState)handler.ApplyBoxed(state, @event).NewState;
    }

    private static DepositPosition FoldPosition(DepositPosition state, HandlerRegistry registry, DomainEvent @event) =>
        Fold(state, registry, @event);

    private static readonly DateTimeOffset Origin = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static EventEnvelope Envelope(Guid streamId, long sequence, string eventType, DomainEvent @event) => new(
        EventId: Guid.NewGuid(),
        StreamId: streamId,
        SequenceNumber: sequence,
        EventType: eventType,
        EventSchemaVersion: 1,
        Family: "term_deposit",
        PartitionKey: streamId,
        PackVersion: "pt.2026.1",
        SchemaVersion: "term_deposit@2026.1",
        ValidTime: Origin.AddDays(sequence),
        TransactionTime: Origin.AddHours(sequence),
        CausationId: null,
        CorrelationId: null,
        Actor: "test",
        Payload: new JsonEventSerializer().Encode(@event).Bytes,
        PayloadSchemaId: 0);

    /// <summary>
    /// A minimal in-memory <see cref="IProjectionStorage"/> for the family-side runner tests —
    /// just enough to exercise supersede-and-write + read-current-belief and the one-current-belief
    /// invariant. The real bitemporal store is integration-tested in Babelstone.EventStore.Tests.
    /// </summary>
    private sealed class InMemoryProjectionStorage : IProjectionStorage
    {
        private readonly List<ProjectionRecord> _rows = [];

        public Task WriteAsync(ProjectionRecord record, CancellationToken ct = default)
        {
            _rows.Add(record);
            return Task.CompletedTask;
        }

        public Task SupersedeAsync(Guid streamId, string projectionKind, DateTimeOffset supersededAt, CancellationToken ct = default)
        {
            Supersede(streamId, projectionKind, supersededAt);
            return Task.CompletedTask;
        }

        public Task SupersedeAndWriteAsync(ProjectionRecord record, CancellationToken ct = default)
        {
            Supersede(record.StreamId, record.ProjectionKind, record.RecordedAt);
            _rows.Add(record);
            return Task.CompletedTask;
        }

        public Task SupersedeAllAsync(string projectionKind, DateTimeOffset supersededAt, CancellationToken ct = default)
        {
            for (var i = 0; i < _rows.Count; i++)
            {
                if (_rows[i].ProjectionKind == projectionKind && _rows[i].SupersededAt is null)
                {
                    _rows[i] = _rows[i] with { SupersededAt = supersededAt };
                }
            }

            return Task.CompletedTask;
        }

        public Task<ProjectionRecord?> ReadCurrentBeliefAsync(Guid streamId, string projectionKind, CancellationToken ct = default)
            => Task.FromResult(_rows.SingleOrDefault(r =>
                r.StreamId == streamId && r.ProjectionKind == projectionKind && r.SupersededAt is null));

        public Task<ProjectionRecord?> ReadAsOfAsync(Guid streamId, string projectionKind, DateTimeOffset validTime, DateTimeOffset knownAt, CancellationToken ct = default)
            => throw new NotSupportedException("D.3 query helper not exercised by these tests.");

        public Task<IReadOnlyList<ProjectionRecord>> ReadHistoryOfAsync(Guid streamId, string projectionKind, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ProjectionRecord>>(
                _rows.Where(r => r.StreamId == streamId && r.ProjectionKind == projectionKind).ToList());

        private void Supersede(Guid streamId, string projectionKind, DateTimeOffset supersededAt)
        {
            for (var i = 0; i < _rows.Count; i++)
            {
                if (_rows[i].StreamId == streamId && _rows[i].ProjectionKind == projectionKind && _rows[i].SupersededAt is null)
                {
                    _rows[i] = _rows[i] with { SupersededAt = supersededAt };
                }
            }
        }
    }
}
