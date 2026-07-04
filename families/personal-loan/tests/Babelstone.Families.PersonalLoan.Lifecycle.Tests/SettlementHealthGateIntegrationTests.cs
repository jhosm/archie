using Babelstone.Engine;
using Babelstone.Families.PersonalLoan;
using Babelstone.Lifecycle;
using Babelstone.Orchestrator.Saga.Settlement;
using Babelstone.TestFixtures;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Babelstone.Families.PersonalLoan.Lifecycle.Tests;

/// <summary>
/// The LCD-2 integration test (ADR-PC-036 §Decision 4 Revised 2026-07-04,
/// <c>LIFECYCLE_DRIVER_SETTLEMENT_HEALTH_GATE</c>): the held-then-resume walk of
/// <c>SettlementHealthGateTests</c>, but against the ORCHESTRATOR'S REAL <c>saga_state</c> schema (its own
/// migration set applied to a PostgreSQL container) read by the REAL
/// <see cref="PostgresSettlementHealthProbe"/>. In plain terms: this is the cross-service proof — the row
/// the settlement saga actually parks in is the row the lifecycle driver's gate actually reads, so
/// installment N+1 is held while occurrence N's cash leg sits in <c>HUMAN_INTERVENTION_REQUIRED</c> and
/// resumes once an operator drives it to <c>SETTLEMENT_COMPLETED</c>. Settlement identity is PER OCCURRENCE
/// (ADR-PC-032 §A9/§A10 Revised 2026-07-04): each occurrence's saga lives at a DERIVED <c>process_id</c>
/// and the probe keys on the <c>saga_state.subject_id</c> linkage — so the rows this test seeds carry
/// exactly that shape (derived process id ≠ subject), proving the gate sees a park the instance id alone
/// could no longer find.
/// </summary>
/// <remarks>
/// The probe's wire literals are PINNED (the driver core takes no orchestrator compile dependency), so this
/// project — which references BOTH sides — also asserts the lock-step byte-for-byte:
/// <see cref="PostgresSettlementHealthProbe.SettlementSagaType"/> ↔ <see cref="SettlementProcess.Type"/> and
/// <see cref="PostgresSettlementHealthProbe.ParkedState"/> ↔
/// <see cref="SettlementProcess.States.HumanInterventionRequired"/>. The probe's THIRD wire dependency — the
/// <c>subject_id</c> column shape (migration 0009) — is pinned by the real-schema walk itself: the probe's
/// EXISTS runs against the orchestrator's own migrated schema, so a reshape breaks here, not in production.
/// Tagged Integration so the Docker-free lane skips it; the integration lane runs it.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class SettlementHealthGateIntegrationTests(SettlementSagaPostgresFixture fixture)
    : IClassFixture<SettlementSagaPostgresFixture>
{
    private static readonly DateOnly Today = new(2026, 6, 28);

    [Fact]
    public void The_probe_wire_literals_are_in_lock_step_with_the_settlement_saga()
    {
        // The driver core pins the saga_type/state literals (extraction-ready: no orchestrator compile
        // dependency); THIS project references both sides, so drift on either side fails here.
        Assert.Equal(SettlementProcess.Type, PostgresSettlementHealthProbe.SettlementSagaType);
        Assert.Equal(
            SettlementProcess.States.HumanInterventionRequired,
            PostgresSettlementHealthProbe.ParkedState);
    }

    [Fact]
    public async Task N_plus_1_is_held_while_N_is_parked_in_the_real_saga_state_and_resumes_once_settled()
    {
        var loan = Guid.NewGuid();
        var store = new SwappableStore(Loan(loan, nextNumber: 1, nextDue: Today));
        var probe = new PostgresSettlementHealthProbe(fixture.ConnectionString);
        var sink = new RecordingSink();
        var pass = new LifecycleSchedulePass(
            [new InstallmentRule(store, probe)], new InMemoryLifecycleDispatchLedger(), sink);

        // Occurrence 1 fires: no settlement saga row exists for the loan yet.
        var first = await pass.RunOnceAsync(Today);
        Assert.Equal(1, Assert.Single(first).OccurrenceKey);

        // N's paid event lands (the calendar advances to occurrence 2) — and N's cash leg PARKS: the
        // settlement saga instance for THAT OCCURRENCE — its process_id a per-occurrence derivation of
        // (ce_subject, event id, movement index), its subject_id the loan's stream id (ADR-PC-032 §A9/§A10
        // Revised 2026-07-04) — lands in HUMAN_INTERVENTION_REQUIRED awaiting an operator.
        store.Replace(Loan(loan, nextNumber: 2, nextDue: Today));
        var occurrence1 = SettlementMovementFanout.OccurrenceProcessId(loan, Guid.NewGuid(), 0);
        Assert.NotEqual(loan, occurrence1); // the probe can no longer find this park by instance id alone
        await fixture.InsertSettlementSagaAsync(
            occurrence1, subjectId: loan, SettlementProcess.States.HumanInterventionRequired);

        // HELD: automated passes keep finding occurrence 2 due and keep refusing it — the paid-count
        // never outruns collected cash, tick after tick.
        Assert.Empty(await pass.RunOnceAsync(Today));
        Assert.Empty(await pass.RunOnceAsync(Today.AddDays(1)));
        Assert.Single(sink.Dispatched);

        // The operator resolves the parked leg (OperatorResolved → SETTLEMENT_COMPLETED). The schedule
        // resumes on the very next pass.
        await fixture.UpdateSettlementSagaAsync(occurrence1, SettlementProcess.States.SettlementCompleted);
        var resumed = await pass.RunOnceAsync(Today.AddDays(2));
        Assert.Equal(2, Assert.Single(resumed).OccurrenceKey);
        Assert.Equal(2, sink.Dispatched.Count);
    }

    [Fact]
    public async Task A_non_parked_settlement_saga_does_not_hold_the_schedule()
    {
        // A loan whose settlement saga is mid-flight (ConfirmingDebit) or completed is NOT parked: only
        // HUMAN_INTERVENTION_REQUIRED holds the schedule — the gate reads the operator-escalation state,
        // not "any saga exists".
        var loan = Guid.NewGuid();
        await fixture.InsertSettlementSagaAsync(
            SettlementMovementFanout.OccurrenceProcessId(loan, Guid.NewGuid(), 0),
            subjectId: loan, SettlementProcess.States.ConfirmingDebit);

        var probe = new PostgresSettlementHealthProbe(fixture.ConnectionString);
        Assert.False(await probe.IsParkedAsync(loan));

        var rule = new InstallmentRule(new SwappableStore(Loan(loan, nextNumber: 4, nextDue: Today)), probe);
        Assert.Equal(4, Assert.Single(await rule.EvaluateAsync(Today)).OccurrenceKey);
    }

    // --- helpers ---

    private static readonly JsonStateSerializer<LoanPosition> Codec = new();

    private static InstallmentCalendarReadModelRow Loan(Guid id, int nextNumber, DateOnly nextDue)
    {
        var position = LoanPosition.Empty with { LoanId = id, DisbursementAccountRef = "acct-collect-001" };
        return new InstallmentCalendarReadModelRow(
            StreamId: id,
            Sor: "engine",
            FirstInstallmentDate: nextDue.AddMonths(-(nextNumber - 1)),
            TermMonths: 12,
            InstallmentAmountCents: 10_000,
            InstallmentsPaid: nextNumber - 1,
            NextInstallmentNumber: nextNumber,
            NextDueDate: nextDue,
            Detail: Codec.Serialize(position),
            LastSequence: 1,
            LastUpdated: default);
    }

    private sealed class SwappableStore(params InstallmentCalendarReadModelRow[] rows)
        : IInstallmentCalendarReadModelStore
    {
        private InstallmentCalendarReadModelRow[] _rows = rows;

        public void Replace(params InstallmentCalendarReadModelRow[] next) => _rows = next;

        public Task<IReadOnlyList<InstallmentCalendarReadModelRow>> ListByDueDateAsync(
            DateOnly fromInclusive, DateOnly toExclusive, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<InstallmentCalendarReadModelRow>>(
                _rows.Where(r => r.NextDueDate is { } due && due >= fromInclusive && due < toExclusive)
                    .OrderBy(r => r.NextDueDate).ThenBy(r => r.StreamId).ToList());

        public Task UpsertAsync(InstallmentCalendarReadModelRow row, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<InstallmentCalendarReadModelRow?> GetAsync(Guid streamId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task TruncateAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class RecordingSink : ILifecycleCommandSink
    {
        private readonly List<Guid> _dispatched = [];

        public IReadOnlyList<Guid> Dispatched => _dispatched;

        public Task DispatchAsync(LifecycleCommandDecision decision, Guid commandId, CancellationToken ct = default)
        {
            _dispatched.Add(commandId);
            return Task.CompletedTask;
        }
    }
}

/// <summary>
/// One PostgreSQL container with the ORCHESTRATOR'S OWN migration set applied — the real
/// <c>saga_state</c> schema the settlement saga persists in and the lifecycle driver's
/// <see cref="PostgresSettlementHealthProbe"/> reads (the cross-service contract under test). Same fixture
/// shape as the orchestrator's <c>OrchestratorPostgresFixture</c>; tests use fresh loan ids, so a shared
/// database needs no per-test reset.
/// </summary>
public sealed class SettlementSagaPostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:18-alpine")
        .Build();

    public string ConnectionString => _pg.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _pg.GatedStartAsync();
        await new Babelstone.Orchestrator.Migrations.MigrationRunner(ConnectionString).ApplyAsync();
    }

    public async Task DisposeAsync() => await _pg.DisposeAsync();

    /// <summary>Insert a settlement saga row exactly as the substrate's auto-start does since the
    /// per-occurrence-identity revision (ADR-PC-032 §A9/§A10 Revised 2026-07-04): keyed by the DERIVED
    /// per-occurrence <paramref name="processId"/>, carrying the account/instrument linkage on
    /// <paramref name="subjectId"/> (the event's <c>ce_subject</c> = the aggregate id — the column the
    /// LCD-2 probe keys on), typed <c>SettlementProcess</c>, in the given state.</summary>
    public async Task InsertSettlementSagaAsync(Guid processId, Guid subjectId, string state)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "INSERT INTO saga_state (process_id, subject_id, saga_type, state) VALUES (@p, @subj, @t, @s);",
            connection);
        command.Parameters.AddWithValue("p", processId);
        command.Parameters.AddWithValue("subj", subjectId);
        command.Parameters.AddWithValue("t", SettlementProcess.Type);
        command.Parameters.AddWithValue("s", state);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Advance the saga row's state (the operator-resolution edge in miniature).</summary>
    public async Task UpdateSettlementSagaAsync(Guid processId, string state)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "UPDATE saga_state SET state = @s, version = version + 1 WHERE process_id = @p;", connection);
        command.Parameters.AddWithValue("p", processId);
        command.Parameters.AddWithValue("s", state);
        await command.ExecuteNonQueryAsync();
    }
}
