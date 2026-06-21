using Babelstone.Engine;
using Babelstone.EventStore;
using Babelstone.FinancialTypes;
using Xunit;

namespace Babelstone.Families.PersonalLoan.Tests;

/// <summary>
/// Projection tests for the personal_loan family's two projections (loan position + amortization
/// schedule). They cover the schedule fold's financial-math discipline (the interest/capital split is
/// recorded as stamped, never recomputed), the reconciliation with the position's running totals, and the
/// runtime properties the runner gives them (handler-skip, at-least-once idempotency, byte-identical rebuild).
/// </summary>
public sealed class PersonalLoanProjectionTests
{
    [Fact]
    public void Module_declares_two_projections_with_distinct_kinds()
    {
        var module = new PersonalLoanProjectionModule();
        var infra = new ProjectionInfra(new InMemoryProjectionStorage(), new JsonEventSerializer());

        var runners = module.CreateRunners(infra);

        Assert.Equal(2, runners.Count);
        Assert.Equal(
            new[] { "personal_loan.loan_position", "personal_loan.amortization_schedule" },
            runners.Select(r => r.Kind).ToArray());
        Assert.All(runners, r => Assert.Equal(ProjectionMode.Async, r.Mode));
        Assert.All(runners, r => Assert.Equal("personal_loan", r.Family));
    }

    [Fact]
    public void AmortizationSchedule_records_installment_legs_as_stamped()
    {
        var registry = PersonalLoanProjectionModule.AmortizationScheduleRegistry();
        var loanId = Guid.NewGuid();

        var schedule = AmortizationScheduleProjection.Empty;
        schedule = Fold(schedule, registry, new LoanInstallmentPaid(
            loanId, 1, new Money(5_000), new Money(81_066), new Money(918_934), new DateOnly(2026, 2, 1)));
        schedule = Fold(schedule, registry, new LoanInstallmentPaid(
            loanId, 2, new Money(4_595), new Money(81_471), new Money(837_463), new DateOnly(2026, 3, 1)));

        Assert.Equal(2, schedule.Entries.Count);
        Assert.All(schedule.Entries, e => Assert.Equal("installment", e.Source));
        // The legs are recorded exactly as stamped — no day-count, no rate-scaling re-derivation.
        Assert.Equal(1, schedule.Entries[0].InstallmentNumber);
        Assert.Equal(new Money(81_066), schedule.Entries[0].Capital);
        Assert.Equal(new Money(5_000 + 4_595), schedule.TotalInterest);
        Assert.Equal(new Money(81_066 + 81_471), schedule.TotalCapital);
    }

    [Fact]
    public void AmortizationSchedule_records_an_early_repayment_as_a_zero_interest_capital_flow()
    {
        var registry = PersonalLoanProjectionModule.AmortizationScheduleRegistry();
        var loanId = Guid.NewGuid();

        var schedule = Fold(AmortizationScheduleProjection.Empty, registry, new LoanRepaidEarly(
            loanId, new Money(500_000), new Money(2_500), new Money(300_000), new DateOnly(2026, 6, 1)));

        var entry = Assert.Single(schedule.Entries);
        Assert.Equal("early_repayment", entry.Source);
        Assert.Equal(Money.Zero, entry.Interest);
        Assert.Equal(new Money(500_000), entry.Capital);
        Assert.Equal(new Money(300_000), entry.OutstandingBalance);
        Assert.Equal(new Money(500_000), schedule.TotalCapital);
    }

    [Fact]
    public void AmortizationSchedule_ignores_non_balance_changing_events()
    {
        var registry = PersonalLoanProjectionModule.AmortizationScheduleRegistry();

        // A disbursement / settlement carries no installment leg — the runner skips them.
        Assert.False(registry.TryResolve("personal_loan.LoanDisbursed", out _));
        Assert.False(registry.TryResolve("personal_loan.LoanSettled", out _));
        Assert.True(registry.TryResolve("personal_loan.LoanInstallmentPaid", out _));
        Assert.True(registry.TryResolve("personal_loan.LoanRepaidEarly", out _));
    }

    [Fact]
    public void AmortizationSchedule_totals_reconcile_with_the_loan_position()
    {
        // The schedule folds the SAME interest/capital the LoanPosition fold sums, so its totals equal the
        // position's TotalInterestPaid / TotalCapitalRepaid (the reconciliation property).
        var scheduleRegistry = PersonalLoanProjectionModule.AmortizationScheduleRegistry();
        var positionRegistry = PersonalLoanFamilyModule.Registry();
        var loanId = Guid.NewGuid();
        var events = new DomainEvent[]
        {
            new LoanInstallmentPaid(loanId, 1, new Money(5_000), new Money(81_066), new Money(918_934), new DateOnly(2026, 2, 1)),
            new LoanInstallmentPaid(loanId, 2, new Money(4_595), new Money(81_471), new Money(837_463), new DateOnly(2026, 3, 1)),
        };

        var schedule = AmortizationScheduleProjection.Empty;
        var position = LoanPosition.Empty;
        foreach (var e in events)
        {
            schedule = Fold(schedule, scheduleRegistry, e);
            position = FoldPosition(position, positionRegistry, e);
        }

        Assert.Equal(position.TotalInterestPaid, schedule.TotalInterest);
        Assert.Equal(position.TotalCapitalRepaid, schedule.TotalCapital);
    }

    [Fact]
    public async Task Runner_skips_unhandled_event_types_leaving_the_belief_unchanged()
    {
        var storage = new InMemoryProjectionStorage();
        var runner = ScheduleRunner(storage);
        var streamId = Guid.NewGuid();

        // A LoanSettled is NOT a schedule flow — the runner must skip it (no row written).
        await runner.ApplyAsync(Envelope(streamId, 0, "personal_loan.LoanSettled",
            new LoanSettled(new Money(1_000_000), new Money(50_000), new DateOnly(2027, 1, 1))));

        Assert.Null(await storage.ReadCurrentBeliefAsync(
            streamId, PersonalLoanProjectionModule.AmortizationScheduleKind));
    }

    [Fact]
    public async Task Runner_is_idempotent_under_at_least_once_redelivery()
    {
        var storage = new InMemoryProjectionStorage();
        var runner = ScheduleRunner(storage);
        var serializer = new JsonStateSerializer<AmortizationScheduleProjection>();
        var streamId = Guid.NewGuid();

        var seq0 = Envelope(streamId, 0, "personal_loan.LoanInstallmentPaid",
            new LoanInstallmentPaid(streamId, 1, new Money(5_000), new Money(81_066), new Money(918_934), new DateOnly(2026, 2, 1)));
        var seq1 = Envelope(streamId, 1, "personal_loan.LoanInstallmentPaid",
            new LoanInstallmentPaid(streamId, 2, new Money(4_595), new Money(81_471), new Money(837_463), new DateOnly(2026, 3, 1)));

        await runner.ApplyAsync(seq0);
        await runner.ApplyAsync(seq1);
        await runner.ApplyAsync(seq1); // crash-replay of seq1 — the source_sequence guard must skip it

        var record = await storage.ReadCurrentBeliefAsync(
            streamId, PersonalLoanProjectionModule.AmortizationScheduleKind);
        var schedule = serializer.Deserialize(record!.StructuralPayload);
        Assert.Equal(2, schedule.Entries.Count); // two flows, not three
        Assert.Equal(new Money(5_000 + 4_595), schedule.TotalInterest);
    }

    [Fact]
    public async Task Runner_rebuild_reproduces_a_byte_identical_belief()
    {
        var streamId = Guid.NewGuid();
        var events = new[]
        {
            Envelope(streamId, 0, "personal_loan.LoanInstallmentPaid",
                new LoanInstallmentPaid(streamId, 1, new Money(5_000), new Money(81_066), new Money(918_934), new DateOnly(2026, 2, 1))),
            Envelope(streamId, 1, "personal_loan.LoanInstallmentPaid",
                new LoanInstallmentPaid(streamId, 2, new Money(4_595), new Money(81_471), new Money(837_463), new DateOnly(2026, 3, 1))),
        };

        var first = new InMemoryProjectionStorage();
        var firstRunner = ScheduleRunner(first);
        foreach (var e in events) await firstRunner.ApplyAsync(e);

        var second = new InMemoryProjectionStorage();
        var secondRunner = ScheduleRunner(second);
        foreach (var e in events) await secondRunner.ApplyAsync(e);

        var a = await first.ReadCurrentBeliefAsync(streamId, PersonalLoanProjectionModule.AmortizationScheduleKind);
        var b = await second.ReadCurrentBeliefAsync(streamId, PersonalLoanProjectionModule.AmortizationScheduleKind);
        Assert.Equal(a!.StructuralPayload.ToArray(), b!.StructuralPayload.ToArray());
        Assert.Equal(a.SourceSequence, b.SourceSequence);
    }

    // --- helpers ---

    private static IProjectionRunner ScheduleRunner(IProjectionStorage storage) =>
        new ProjectionRunner<AmortizationScheduleProjection>(
            kind: PersonalLoanProjectionModule.AmortizationScheduleKind,
            family: "personal_loan",
            mode: ProjectionMode.Async,
            handlers: PersonalLoanProjectionModule.AmortizationScheduleRegistry(),
            serializer: new JsonEventSerializer(),
            seed: () => AmortizationScheduleProjection.Empty,
            store: new ProjectionStore<AmortizationScheduleProjection>(
                storage, new JsonStateSerializer<AmortizationScheduleProjection>()));

    private static TState Fold<TState>(TState state, HandlerRegistry registry, DomainEvent @event)
        where TState : class
    {
        var eventType = $"personal_loan.{@event.GetType().Name}";
        Assert.True(registry.TryResolve(eventType, out var handler), $"no handler for {eventType}");
        return (TState)handler.ApplyBoxed(state, @event).NewState;
    }

    private static LoanPosition FoldPosition(LoanPosition state, HandlerRegistry registry, DomainEvent @event) =>
        Fold(state, registry, @event);

    private static readonly DateTimeOffset Origin = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static EventEnvelope Envelope(Guid streamId, long sequence, string eventType, DomainEvent @event) => new(
        EventId: Guid.NewGuid(),
        StreamId: streamId,
        SequenceNumber: sequence,
        EventType: eventType,
        EventSchemaVersion: 1,
        Family: "personal_loan",
        PartitionKey: streamId,
        PackVersion: "pt.2026.1",
        SchemaVersion: "personal_loan@2026.1",
        ValidTime: Origin.AddDays(sequence),
        TransactionTime: Origin.AddHours(sequence),
        CausationId: null,
        CorrelationId: null,
        Actor: "test",
        Payload: new JsonEventSerializer().Encode(@event).Bytes,
        PayloadSchemaId: 0);

    /// <summary>A minimal in-memory <see cref="IProjectionStorage"/> for the family-side runner tests.</summary>
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
            => throw new NotSupportedException("query helper not exercised by these tests.");

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
