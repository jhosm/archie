using Babelstone.Engine;
using Babelstone.EventStore;
using Babelstone.FinancialTypes;
using Xunit;

namespace Babelstone.Families.PersonalLoan.Tests;

/// <summary>
/// CQRS installment-calendar read-model tests (bd babelstone-6cpq.12): the personal_loan family
/// materialises the denormalized <c>read_model.installment_calendar</c> row (ADR-IC-005) by folding the
/// SAME <c>LoanPosition</c> the live read path computes, surfacing the next-unpaid occurrence per Active
/// loan. These cover the runner's runtime properties — handler-skip, at-least-once idempotency (the
/// ADR-IC-005 §P2 monotonicity guard), and the byte-identical rebuild the truncate-and-refold path (§P5)
/// relies on — the Active-gating of the forward pointer, the "installments due in [from, to)" range scan,
/// and the pure state→row mapper. The real Postgres store is integration-tested in the family's
/// Application tests.
/// </summary>
public sealed class InstallmentCalendarReadModelTests
{
    [Fact]
    public async Task Disbursing_materialises_the_next_occurrence_row()
    {
        var store = new InMemoryInstallmentCalendarStore();
        var runner = ReadModelRunner(store);
        var loanId = Guid.NewGuid();

        await runner.ApplyAsync(Envelope(loanId, 0, "personal_loan.LoanDisbursed", Disbursed(loanId)));

        var row = await store.GetAsync(loanId);
        Assert.NotNull(row);
        Assert.Equal("engine", row.Sor);                       // ADR-PC-018 §6.2 routing truth
        Assert.Equal(new DateOnly(2026, 2, 15), row.FirstInstallmentDate);
        Assert.Equal(12, row.TermMonths);
        Assert.Equal(88_849, row.InstallmentAmountCents);
        Assert.Equal(0, row.InstallmentsPaid);
        // No installment paid yet: the next unpaid occurrence is #1, due on the schedule anchor.
        Assert.Equal(1, row.NextInstallmentNumber);
        Assert.Equal(new DateOnly(2026, 2, 15), row.NextDueDate);
        Assert.Equal(0, row.LastSequence);
        // ADR-IC-005 §P3 / ADR-PC-010 §P5: last_updated is the event's transaction_time, not a clock.
        Assert.Equal(Origin, row.LastUpdated);
    }

    [Fact]
    public async Task Paying_an_installment_advances_the_forward_pointer()
    {
        var store = new InMemoryInstallmentCalendarStore();
        var runner = ReadModelRunner(store);
        var loanId = Guid.NewGuid();

        await runner.ApplyAsync(Envelope(loanId, 0, "personal_loan.LoanDisbursed", Disbursed(loanId)));
        await runner.ApplyAsync(Envelope(loanId, 1, "personal_loan.LoanInstallmentPaid",
            new LoanInstallmentPaid(loanId, 1, new Money(5_000), new Money(83_849), new Money(916_151), new DateOnly(2026, 2, 15))));

        var row = await store.GetAsync(loanId);
        Assert.Equal(1, row!.InstallmentsPaid);
        // Paying #1 advances the next unpaid occurrence to #2, due one cadence on from the anchor.
        Assert.Equal(2, row.NextInstallmentNumber);
        Assert.Equal(new DateOnly(2026, 3, 15), row.NextDueDate);
        Assert.Equal(1, row.LastSequence);
    }

    [Fact]
    public async Task Runner_skips_events_outside_the_position_fold()
    {
        var store = new InMemoryInstallmentCalendarStore();
        var runner = ReadModelRunner(store);
        var loanId = Guid.NewGuid();

        // An event type the position registry has no handler for leaves no row.
        await runner.ApplyAsync(Envelope(loanId, 0, "other_family.Unknown",
            new LoanInstallmentPaid(loanId, 1, new Money(1), new Money(1), new Money(1), new DateOnly(2026, 2, 15))));

        Assert.Null(await store.GetAsync(loanId));
    }

    [Fact]
    public async Task Runner_is_idempotent_under_at_least_once_redelivery()
    {
        var store = new InMemoryInstallmentCalendarStore();
        var runner = ReadModelRunner(store);
        var loanId = Guid.NewGuid();

        var seq0 = Envelope(loanId, 0, "personal_loan.LoanDisbursed", Disbursed(loanId));
        var seq1 = Envelope(loanId, 1, "personal_loan.LoanInstallmentPaid",
            new LoanInstallmentPaid(loanId, 1, new Money(5_000), new Money(83_849), new Money(916_151), new DateOnly(2026, 2, 15)));

        await runner.ApplyAsync(seq0);
        await runner.ApplyAsync(seq1);
        await runner.ApplyAsync(seq1); // crash-replay of seq1 — the §P2 guard must drop it

        var row = await store.GetAsync(loanId);
        Assert.Equal(1, row!.LastSequence);
        // The paid count folded exactly once: re-applying seq1 did not double-advance the pointer.
        Assert.Equal(1, row.InstallmentsPaid);
        Assert.Equal(2, row.NextInstallmentNumber);
    }

    [Fact]
    public async Task Rebuild_reproduces_a_byte_identical_read_model_row()
    {
        // ADR-IC-005 §P5 + ADR-PC-010 §P5: folds are deterministic and every stamp is event-derived, so
        // re-folding the same events (a truncate-and-refold rebuild) yields a byte-for-byte identical row.
        var loanId = Guid.NewGuid();
        var events = new[]
        {
            Envelope(loanId, 0, "personal_loan.LoanDisbursed", Disbursed(loanId)),
            Envelope(loanId, 1, "personal_loan.LoanInstallmentPaid",
                new LoanInstallmentPaid(loanId, 1, new Money(5_000), new Money(83_849), new Money(916_151), new DateOnly(2026, 2, 15))),
            Envelope(loanId, 2, "personal_loan.LoanInstallmentPaid",
                new LoanInstallmentPaid(loanId, 2, new Money(4_581), new Money(84_268), new Money(831_883), new DateOnly(2026, 3, 15))),
        };

        var first = new InMemoryInstallmentCalendarStore();
        var firstRunner = ReadModelRunner(first);
        var second = new InMemoryInstallmentCalendarStore();
        var secondRunner = ReadModelRunner(second);
        foreach (var e in events)
        {
            await firstRunner.ApplyAsync(e);
            await secondRunner.ApplyAsync(e);
        }

        var a = await first.GetAsync(loanId);
        var b = await second.GetAsync(loanId);
        Assert.NotNull(a);
        Assert.NotNull(b);
        // ReadOnlyMemory<byte> equality is by reference, so the Detail bytes are asserted with ToArray()
        // and the rest of the record by value (the same idiom the deposit read-model tests use).
        Assert.Equal(a.Detail.ToArray(), b.Detail.ToArray());
        Assert.Equal(a with { Detail = default }, b with { Detail = default });
        Assert.Equal(2, a.InstallmentsPaid);
        Assert.Equal(3, a.NextInstallmentNumber);
        Assert.Equal(new DateOnly(2026, 4, 15), a.NextDueDate);
    }

    [Fact]
    public async Task A_fully_paid_loan_surfaces_no_next_occurrence()
    {
        var store = new InMemoryInstallmentCalendarStore();
        var runner = ReadModelRunner(store);
        var loanId = Guid.NewGuid();

        // A two-installment loan, both paid: the calendar is exhausted (InstallmentsPaid == TermMonths).
        await runner.ApplyAsync(Envelope(loanId, 0, "personal_loan.LoanDisbursed", Disbursed(loanId, termMonths: 2)));
        await runner.ApplyAsync(Envelope(loanId, 1, "personal_loan.LoanInstallmentPaid",
            new LoanInstallmentPaid(loanId, 1, new Money(5_000), new Money(495_000), new Money(505_000), new DateOnly(2026, 2, 15))));
        await runner.ApplyAsync(Envelope(loanId, 2, "personal_loan.LoanInstallmentPaid",
            new LoanInstallmentPaid(loanId, 2, new Money(2_525), new Money(505_000), Money.Zero, new DateOnly(2026, 3, 15))));

        var row = await store.GetAsync(loanId);
        Assert.Equal(2, row!.InstallmentsPaid);
        Assert.Null(row.NextInstallmentNumber);
        Assert.Null(row.NextDueDate);
    }

    [Fact]
    public async Task A_written_off_loan_surfaces_no_next_occurrence_even_with_installments_remaining()
    {
        // A terminal (non-Active) loan is dropped from the forward calendar even though scheduled
        // installments remain — the range scan must not chase an installment on a closed loan.
        var store = new InMemoryInstallmentCalendarStore();
        var runner = ReadModelRunner(store);
        var loanId = Guid.NewGuid();

        await runner.ApplyAsync(Envelope(loanId, 0, "personal_loan.LoanDisbursed", Disbursed(loanId)));
        await runner.ApplyAsync(Envelope(loanId, 1, "personal_loan.LoanWrittenOff",
            new LoanWrittenOff(loanId, new Money(1_000_000), new DateOnly(2026, 6, 1), "DEFAULT_UNRECOVERABLE")));

        var row = await store.GetAsync(loanId);
        Assert.Null(row!.NextInstallmentNumber);
        Assert.Null(row.NextDueDate);
    }

    [Fact]
    public async Task Range_scan_returns_loans_with_an_installment_due_in_the_window_in_order()
    {
        // The acceptance criterion: "loans with an installment due in [from, to)" returns rows, ordered by
        // due date then id. A loan due on the exclusive upper bound, and a written-off loan (NULL due date),
        // are both excluded.
        var store = new InMemoryInstallmentCalendarStore();
        var runner = ReadModelRunner(store);

        var early = Guid.NewGuid();
        var mid = Guid.NewGuid();
        var outside = Guid.NewGuid();
        var closed = Guid.NewGuid();

        // mid: first installment due 2026-03-15 (anchor) after paying #1.
        await runner.ApplyAsync(Envelope(mid, 0, "personal_loan.LoanDisbursed", Disbursed(mid, firstInstallment: new DateOnly(2026, 2, 15))));
        await runner.ApplyAsync(Envelope(mid, 1, "personal_loan.LoanInstallmentPaid",
            new LoanInstallmentPaid(mid, 1, new Money(5_000), new Money(83_849), new Money(916_151), new DateOnly(2026, 2, 15))));
        // early: next due 2026-02-15 (no installment paid).
        await runner.ApplyAsync(Envelope(early, 0, "personal_loan.LoanDisbursed", Disbursed(early, firstInstallment: new DateOnly(2026, 2, 15))));
        // outside: next due 2026-05-15 — on/after the exclusive upper bound.
        await runner.ApplyAsync(Envelope(outside, 0, "personal_loan.LoanDisbursed", Disbursed(outside, firstInstallment: new DateOnly(2026, 5, 15))));
        // closed: written off → no due date.
        await runner.ApplyAsync(Envelope(closed, 0, "personal_loan.LoanDisbursed", Disbursed(closed, firstInstallment: new DateOnly(2026, 2, 15))));
        await runner.ApplyAsync(Envelope(closed, 1, "personal_loan.LoanWrittenOff",
            new LoanWrittenOff(closed, new Money(1_000_000), new DateOnly(2026, 1, 20), "DEFAULT_UNRECOVERABLE")));

        var due = await store.ListByDueDateAsync(new DateOnly(2026, 1, 1), new DateOnly(2026, 5, 15));

        // early (2026-02-15) then mid (2026-03-15); outside and closed excluded.
        Assert.Equal([early, mid], due.Select(r => r.StreamId).ToArray());
    }

    [Fact]
    public void Map_to_read_model_is_pure_and_active_gated()
    {
        // The mapper is a pure function of (state, event-derived context): same input → same output, no
        // clock, no I/O. An Active loan with installments remaining surfaces the forward pointer; a settled
        // loan surfaces none.
        var loanId = Guid.NewGuid();
        var active = LoanPosition.Empty with
        {
            LoanId = loanId,
            TermMonths = 12,
            InstallmentAmount = new Money(88_849),
            FirstInstallmentDate = new DateOnly(2026, 2, 15),
            InstallmentsPaid = 3,
            Lifecycle = LoanLifecycle.Active,
        };
        var fold = new ReadModelFold<LoanPosition>(active, loanId, 7, Origin);

        var a = PersonalLoanProjectionModule.MapToReadModel(fold);
        var b = PersonalLoanProjectionModule.MapToReadModel(fold);

        Assert.Equal(a.Detail.ToArray(), b.Detail.ToArray());
        Assert.Equal(a with { Detail = default }, b with { Detail = default });
        Assert.Equal("engine", a.Sor);
        Assert.Equal(7, a.LastSequence);
        Assert.Equal(Origin, a.LastUpdated);
        Assert.Equal(4, a.NextInstallmentNumber);                 // paid 3 → next is #4
        Assert.Equal(new DateOnly(2026, 5, 15), a.NextDueDate);   // anchor + 3 months
        Assert.Equal(88_849, a.InstallmentAmountCents);

        var settled = PersonalLoanProjectionModule.MapToReadModel(
            new ReadModelFold<LoanPosition>(active with { Lifecycle = LoanLifecycle.Settled }, loanId, 8, Origin));
        Assert.Null(settled.NextInstallmentNumber);
        Assert.Null(settled.NextDueDate);
    }

    // --- helpers ---

    private static IProjectionRunner ReadModelRunner(IInstallmentCalendarReadModelStore store) =>
        new PersonalLoanProjectionModule().CreateReadModelRunner(
            new ReadModelInfra<InstallmentCalendarReadModelRow>(store, new JsonEventSerializer()));

    private static LoanDisbursed Disbursed(Guid loanId, int termMonths = 12, DateOnly? firstInstallment = null) =>
        new(
            LoanId: loanId,
            Principal: new Money(1_000_000),
            TanBasisPoints: 600,
            RateSheetVersionId: "rs-loans-2026.1",
            TermMonths: termMonths,
            PeriodicRateBasisPoints: 50,
            InstallmentAmount: new Money(88_849),
            StartDate: new DateOnly(2026, 1, 15),
            FirstInstallmentDate: firstInstallment ?? new DateOnly(2026, 2, 15),
            Purpose: "general",
            ProductCode: "cp_pt_general_12m",
            DisbursementAccountRef: "acct-token-borrower");

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
        TransactionTime: Origin,
        CausationId: null,
        CorrelationId: null,
        Actor: "test",
        Payload: new JsonEventSerializer().Encode(@event).Bytes,
        PayloadSchemaId: 0);

    /// <summary>
    /// A minimal in-memory <see cref="IInstallmentCalendarReadModelStore"/> for the family-side runner
    /// tests — enough to exercise the UPSERT monotonicity guard, point lookup, due-date range scan, and
    /// truncate. The real Postgres store is integration-tested in the family's Application tests.
    /// </summary>
    private sealed class InMemoryInstallmentCalendarStore : IInstallmentCalendarReadModelStore
    {
        private readonly Dictionary<Guid, InstallmentCalendarReadModelRow> _rows = [];

        public Task UpsertAsync(InstallmentCalendarReadModelRow row, CancellationToken ct = default)
        {
            // ADR-IC-005 §P2: overwrite only on a strictly higher sequence.
            if (!_rows.TryGetValue(row.StreamId, out var existing) || existing.LastSequence < row.LastSequence)
            {
                _rows[row.StreamId] = row;
            }

            return Task.CompletedTask;
        }

        public Task<InstallmentCalendarReadModelRow?> GetAsync(Guid streamId, CancellationToken ct = default) =>
            Task.FromResult(_rows.TryGetValue(streamId, out var row) ? row : null);

        public Task<IReadOnlyList<InstallmentCalendarReadModelRow>> ListByDueDateAsync(
            DateOnly fromInclusive, DateOnly toExclusive, CancellationToken ct = default)
        {
            // Mirrors the SQL: a NULL next_due_date (no occurrence) is excluded; the window is half-open;
            // ordered by due date then id.
            IReadOnlyList<InstallmentCalendarReadModelRow> rows =
            [
                .. _rows.Values
                    .Where(r => r.NextDueDate is { } due && due >= fromInclusive && due < toExclusive)
                    .OrderBy(r => r.NextDueDate!.Value)
                    .ThenBy(r => r.StreamId),
            ];
            return Task.FromResult(rows);
        }

        public Task TruncateAsync(CancellationToken ct = default)
        {
            _rows.Clear();
            return Task.CompletedTask;
        }
    }
}
