using Babelstone.Engine;
using Babelstone.EventStore;
using Babelstone.FinancialTypes;
using Babelstone.RateSheets;
using Npgsql;
using Xunit;

namespace Babelstone.Families.TermDeposit.Application.Tests;

/// <summary>
/// The operator correction COMMAND path (D.5 / F.6, bd babelstone-k6r8.11). In plain English: the hard
/// part — the bitemporal "what we thought vs what we now know" supersession — is already built and proven
/// by <see cref="ForcedCorrectionRoundTripTests"/>, which appends <c>DepositCorrected</c> BY HAND. This
/// suite covers the missing FRONT DOOR: the <see cref="TermDepositConstitutionService.CorrectAsync"/>
/// command that appends that same event, with its valid-time set to <c>effective_from</c> (the input the
/// ADR-PC-002 §P2 supersession reads), guarded to OPERATOR actors only and idempotent on a command id
/// (ADR-PC-029 slot 4). It does NOT re-test the supersession runtime (that is the D.5 test's job); it
/// tests that the COMMAND produces the right event the same way the hand-rolled append did.
/// </summary>
public sealed class CorrectionCommandTests
{
    private static readonly DateOnly Start = new(2026, 1, 15);
    private static readonly DateOnly Maturity = new(2027, 1, 15);
    private static readonly DateOnly EffectiveFrom = new(2026, 3, 1);

    // ---- pure unit tests (no Docker): the operator guard + the F.3 lifecycle gate, BEFORE any append ----

    [Fact]
    public async Task CorrectAsync_rejects_a_non_operator_actor()
    {
        // A correction is operator-only. A customer/agent actor (mcp:dev) must be refused BEFORE any
        // append — the ThrowingRateSheetStore/ThrowingSettlementPort prove no resolve/settle happens, and
        // the NullSink would throw if the append were reached.
        var depositId = Guid.NewGuid();
        var service = ServiceOverStream(depositId, ActiveStream(depositId));

        var ex = await Assert.ThrowsAsync<DomainRejectedException>(
            () => service.CorrectAsync(Command(depositId, actor: "mcp:dev")));

        Assert.Contains("not an operator", ex.Message);
        Assert.Contains("mcp:dev", ex.Message);
    }

    [Theory]
    [InlineData("ops:clerk")]
    [InlineData("operator:regulatory-ops")]
    public async Task CorrectAsync_allows_an_operator_actor_on_an_active_deposit(string actor)
    {
        // Both operator namespaces (ops:* and operator:*) pass the guard AND the F.3 Active gate, so the
        // command proceeds past every check to the append. The append is discarded by the NullSink — the
        // guard + lifecycle integration is what is under test here (the durable fold is the integration
        // test below). It must NOT throw.
        var depositId = Guid.NewGuid();
        var service = ServiceOverStream(depositId, ActiveStream(depositId));

        await service.CorrectAsync(Command(depositId, actor: actor));
    }

    [Fact]
    public async Task CorrectAsync_rejects_correcting_a_matured_deposit_the_f3_gate()
    {
        // Correct is legal only from Active (the F.3 LifecycleTransitions table). A Matured deposit is
        // business-terminal, so even an operator actor is refused — the lifecycle gate fires after the
        // operator gate passes.
        var depositId = Guid.NewGuid();
        var service = ServiceOverStream(depositId, MaturedAtMaturityStream(depositId));

        var ex = await Assert.ThrowsAsync<DomainRejectedException>(
            () => service.CorrectAsync(Command(depositId, actor: "ops:clerk")));

        Assert.Contains("Matured", ex.Message);
        Assert.Contains("Correct", ex.Message); // names the illegal transition
    }

    [Fact]
    public async Task CorrectAsync_rejects_correcting_a_non_existent_deposit()
    {
        // Pending (no events) → no deposit to correct; the F.3 gate rejects Correct from Pending.
        var depositId = Guid.NewGuid();
        var service = ServiceOverStream(depositId, []);

        var ex = await Assert.ThrowsAsync<DomainRejectedException>(
            () => service.CorrectAsync(Command(depositId, actor: "ops:clerk")));

        Assert.Contains("Pending", ex.Message);
        Assert.Contains("Correct", ex.Message);
    }

    // ---- integration test (Testcontainers): the COMMAND appends DepositCorrected and folds it ----

    /// <summary>
    /// End-to-end on real PostgreSQL: constitute a deposit, then run the correction COMMAND (not a
    /// hand-appended event). It proves (a) the durable rehydrate folds <c>CorrectionCount</c> to 1 — the
    /// command really appended <c>DepositCorrected</c>; (b) the appended event's <c>valid_time</c> equals
    /// <c>effective_from</c> at midnight UTC — the exact input the ADR-PC-002 §P2 bitemporal supersession
    /// reads, mirroring how the D.5 hand-rolled append sets it; and (c) the command is idempotent on its
    /// command id (ADR-PC-029 slot 4) — a replay with the same id raises <see cref="DuplicateCommandException"/>
    /// and does not append a second event (CorrectionCount stays 1).
    /// </summary>
    [Trait("Category", "Integration")]
    public sealed class Integration(ConstitutionFixture fixture) : IClassFixture<ConstitutionFixture>
    {
        [Fact]
        public async Task Correction_command_appends_DepositCorrected_with_validtime_effective_from_and_is_idempotent()
        {
            await fixture.EnsureRateSheetAsync(SharedSheet);

            var store = new PostgresEventStore(fixture.ConnectionString);
            var runtime = new AggregateRuntime<DepositPosition>(
                store, new EventStoreSink(store), TermDepositFamilyModule.Registry(),
                new JsonEventSerializer(), new NullPiiProtector(), TimeProvider.System,
                () => DepositPosition.Empty);
            var service = new TermDepositConstitutionService(
                runtime, new PostgresRateSheetStore(fixture.ConnectionString), new RecordingSettlementPort(),
                SkeletonPack.LoadPt2026(), dayCountPrimitive: "act_360", withholdingPrimitive: "irs_juros");

            var depositId = Guid.NewGuid();
            await service.ConstituteAsync(new ConstituteDepositCommand(
                DepositId: depositId, PrincipalCents: 1_000_000, ProductId: "dpz_pt_12m_juros_venc", Role: "standard",
                TermDays: 365, StartDate: Start, ConstitutedAt: new DateTimeOffset(Start, TimeOnly.MinValue, TimeSpan.Zero),
                InterestVariant: "AT_MATURITY", AutoRenewalPolicy: "NONE", FundingAccount: "PT50-DDA-001", Actor: "mcp:dev"));

            var commandId = Guid.NewGuid();
            var command = new CorrectDepositCommand(
                DepositId: depositId, CorrectionId: "corr-001", CorrectedField: "principal",
                PreviousValueRef: "ref:old", CorrectedValueRef: "ref:new",
                EffectiveFrom: EffectiveFrom, CorrectionReason: "clerk-entry",
                Actor: "ops:clerk", CommandId: commandId);

            // The COMMAND path (not a hand-appended event): it appends DepositCorrected through the service.
            await service.CorrectAsync(command);

            // (a) The durable fold folds the correction: CorrectionCount advanced to 1, deposit stays Active.
            var hydrated = await runtime.LoadAsync(depositId);
            Assert.Equal(1, hydrated.State.CorrectionCount);
            Assert.Equal(DepositLifecycle.Active, hydrated.State.Lifecycle);

            // The correction added exactly one event (constitution + correction = 2 on the stream).
            Assert.Equal(2, await fixture.CountAsync("events", "stream_id", depositId));

            // (b) The appended DepositCorrected carries valid_time = effective_from at midnight UTC — the
            //     bitemporal supersession input the D.5 hand-rolled append sets the same way.
            var validTime = await CorrectedEventValidTimeAsync(depositId);
            Assert.Equal(new DateTimeOffset(EffectiveFrom, TimeOnly.MinValue, TimeSpan.Zero), validTime);

            // (c) Idempotent on the command id (ADR-PC-029 slot 4): a replay with the SAME id raises
            //     DuplicateCommandException and appends NOTHING — CorrectionCount stays 1.
            await Assert.ThrowsAsync<DuplicateCommandException>(() => service.CorrectAsync(command));

            var afterReplay = await runtime.LoadAsync(depositId);
            Assert.Equal(1, afterReplay.State.CorrectionCount);
            Assert.Equal(2, await fixture.CountAsync("events", "stream_id", depositId));
        }

        /// <summary>The <c>valid_time</c> of the single <c>term_deposit.DepositCorrected</c> event on the
        /// stream — read back (via the same <see cref="DateTimeOffset"/> mapping the event store uses) to
        /// prove the command stamped AppendContext.ValidTime = effective_from.</summary>
        private async Task<DateTimeOffset> CorrectedEventValidTimeAsync(Guid streamId)
        {
            await using var connection = new NpgsqlConnection(fixture.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT valid_time FROM events WHERE stream_id = @id AND event_type = 'term_deposit.DepositCorrected';",
                connection);
            command.Parameters.AddWithValue("id", streamId);
            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                throw new InvalidOperationException($"no DepositCorrected on stream {streamId}");
            }

            return reader.GetFieldValue<DateTimeOffset>(0);
        }

        private static RateSheet SharedSheet => TestRateSheets.MultiPriced(
            versionId: "pt-deposits-2026.1",
            effectiveFrom: new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            ("dpz_pt_12m_juros_venc", "standard", 300),
            ("dpz_pt_12m_juros_mensal", "standard", 325),
            ("dpz_pt_12m_juros_antecip", "standard", 300));
    }

    // ---- seed streams + helpers for the pure unit tests -----------------------------------------

    /// <summary>A bare constituted AT_MATURITY deposit → folds to Active.</summary>
    private static DomainEvent[] ActiveStream(Guid depositId) =>
    [
        new DepositConstituted(
            depositId, new Money(1_000_000), 300, "rs-1", 365, Start, Maturity, "AT_MATURITY", "NONE"),
    ];

    /// <summary>A constituted + matured AT_MATURITY deposit → folds to the terminal Matured state.</summary>
    private static DomainEvent[] MaturedAtMaturityStream(Guid depositId) =>
    [
        new DepositConstituted(
            depositId, new Money(1_000_000), 300, "rs-1", 365, Start, Maturity, "AT_MATURITY", "NONE"),
        new InterestAccrued(new Money(30_417), Maturity),
        new WithholdingApplied(new Money(8_517), new Money(21_900)),
        new DepositMatured(new Money(1_000_000), new Money(21_900), new Money(1_021_900), Maturity),
    ];

    private static CorrectDepositCommand Command(Guid depositId, string actor) =>
        new(
            DepositId: depositId, CorrectionId: "corr-001", CorrectedField: "principal",
            PreviousValueRef: "ref:old", CorrectedValueRef: "ref:new",
            EffectiveFrom: EffectiveFrom, CorrectionReason: "clerk-entry",
            Actor: actor, CommandId: Guid.NewGuid());

    /// <summary>Compose the durable runtime over an in-memory store seeded with <paramref name="stream"/>,
    /// a discard sink, and rate-sheet/settlement stubs that throw if touched (a correction never resolves a
    /// sheet or settles — it is store-only). Only LoadAsync is exercised; the append is discarded by the
    /// NullSink, so the guard + F.3 gate (which fire BEFORE the append) are what these tests pin.</summary>
    private static TermDepositConstitutionService ServiceOverStream(Guid depositId, DomainEvent[] stream)
    {
        var serializer = new JsonEventSerializer();
        var registry = TermDepositFamilyModule.Registry();
        var store = new InMemoryEventStore(depositId, stream, serializer, registry);
        var runtime = new AggregateRuntime<DepositPosition>(
            store, new NullSink(), registry, serializer, new NullPiiProtector(), TimeProvider.System,
            () => DepositPosition.Empty);

        return new TermDepositConstitutionService(
            runtime, new ThrowingRateSheetStore(), new ThrowingSettlementPort(failOnSettle: true),
            SkeletonPack.LoadPt2026(), dayCountPrimitive: "act_360", withholdingPrimitive: "irs_juros");
    }
}
