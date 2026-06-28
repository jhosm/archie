using Babelstone.Engine;
using Babelstone.Engine.Hosting;
using Babelstone.EventStore;
using Babelstone.FinancialTypes;
using Babelstone.RateSheets;
using Xunit;

namespace Babelstone.Families.TermDeposit.Application.Tests;

/// <summary>
/// In plain English: maturity used to carry no idempotency key, so the only thing stopping a duplicate
/// "mature" from running twice was the lifecycle rule that a deposit can mature only once. These tests pin
/// the new behaviour — maturity now carries a key the SERVER computes from the deposit id, so two firings
/// of the same maturity collapse to a single append at the engine's dedupe ledger, no longer leaning on
/// the lifecycle rule alone.
///
/// ADR-PC-036 Decision 1 (bd babelstone-6cpq.3). Maturity reuses the CANONICAL
/// <see cref="LifecycleCommandKey"/> the loan installment endpoint uses (LCD-1) — it is the degenerate
/// ONE-SHOT occurrence, <c>Derive(deposit_id, "mature", 1)</c>. Two parts: a PURE unit check that the
/// derivation is deterministic + per-deposit distinct, and an integration check (real PostgreSQL) that
/// <see cref="TermDepositConstitutionService.MatureAsync"/> threads the derived id into <c>command_dedup</c>
/// (ADR-PC-029 slot 4) and that a second append carrying the SAME id is rejected by <c>command_dedup</c>
/// itself — independent of the F.3 legality gate.
/// </summary>
public sealed class MatureCommandIdempotencyTests
{
    // The maturity command space, mirroring the DepositsEndpoints constants (ADR-PC-036 Decision 1): the
    // one-shot occurrence number is the constant 1.
    private const string MatureKind = "mature";
    private const long MatureOccurrence = 1;

    // ---- pure unit tests on the maturity key derivation (no Postgres) ------------------------------

    [Fact]
    public void Maturity_key_is_deterministic_in_the_deposit_id()
    {
        var depositId = Guid.NewGuid();
        // The same one-shot occurrence ALWAYS derives the byte-identical id — the property a manual caller,
        // the MCP mature_deposit tool, and the lifecycle driver lean on to converge on ONE key (one append).
        Assert.Equal(
            LifecycleCommandKey.Derive(depositId, MatureKind, MatureOccurrence),
            LifecycleCommandKey.Derive(depositId, MatureKind, MatureOccurrence));
    }

    [Fact]
    public void Maturity_key_is_distinct_per_deposit()
    {
        Assert.NotEqual(
            LifecycleCommandKey.Derive(Guid.NewGuid(), MatureKind, MatureOccurrence),
            LifecycleCommandKey.Derive(Guid.NewGuid(), MatureKind, MatureOccurrence));
    }

    [Fact]
    public void Maturity_key_is_a_nonempty_uuid_distinct_from_an_installment_occurrence()
    {
        var instance = Guid.NewGuid();
        var maturityKey = LifecycleCommandKey.Derive(instance, MatureKind, MatureOccurrence);
        Assert.NotEqual(Guid.Empty, maturityKey);
        // The command_kind discriminates: maturity ("mature", 1) never collides with a recurring installment
        // occurrence ("pay_installment", 1) on the same aggregate id.
        Assert.NotEqual(maturityKey, LifecycleCommandKey.Derive(instance, "pay_installment", 1));
    }

    // ---- integration (Testcontainers): the COMMAND threads the derived id into command_dedup ----------

    /// <summary>
    /// End-to-end on real PostgreSQL: constitute a deposit, then mature it with the SERVER-DERIVED command
    /// id — exactly what the maturity endpoint / MCP tool / lifecycle driver compute for this one-shot
    /// occurrence. It proves (a) the derived id landed in <c>command_dedup</c> pointing at the deposit
    /// stream — so a retry presenting the SAME id is a recognized replay (ADR-PC-029 slot 4), no longer a
    /// legality-gate rejection; and (b) <c>command_dedup</c> is the dedupe AUTHORITY, independent of the F.3
    /// legality gate — a second append carrying the SAME derived id (here on a fresh stream, to show the
    /// guard is GLOBAL on command_id, migration 0015, reached at the append's dedup INSERT and never at a
    /// per-stream lifecycle check) raises <see cref="DuplicateCommandException"/>.
    /// </summary>
    [Trait("Category", "Integration")]
    public sealed class Integration(ConstitutionFixture fixture) : IClassFixture<ConstitutionFixture>
    {
        [Fact]
        public async Task Mature_threads_the_server_derived_id_into_command_dedup_independent_of_the_legality_gate()
        {
            await fixture.EnsureRateSheetAsync(SharedSheet);
            var (runtime, service) = Compose(fixture.ConnectionString);
            var commandLog = new PostgresCommandLog(fixture.ConnectionString);

            var depositId = Guid.NewGuid();
            await service.ConstituteAsync(new ConstituteDepositCommand(
                DepositId: depositId, PrincipalCents: 1_000_000, ProductId: "dpz_pt_12m_juros_venc",
                Role: "standard", TermDays: 365, StartDate: new DateOnly(2026, 1, 15),
                ConstitutedAt: new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero),
                InterestVariant: "AT_MATURITY", AutoRenewalPolicy: "NONE", FundingAccount: "PT50-DDA-001",
                Actor: "mcp:dev"));

            // Mature with the SERVER-DERIVED command id (ADR-PC-036 Decision 1) — the maturity endpoint, the
            // MCP tool, and the driver all compute THIS canonical id for the deposit's one maturity occurrence.
            var commandId = LifecycleCommandKey.Derive(depositId, "mature", 1);
            await service.MatureAsync(new MatureDepositCommand(
                DepositId: depositId, MaturedAt: new DateTimeOffset(2027, 1, 15, 0, 0, 0, TimeSpan.Zero),
                PayoutAccount: "PT50-DDA-001", Actor: "mcp:dev", CommandId: commandId));

            // (a) The derived id landed in command_dedup, pointing at the deposit stream — the append threaded
            //     it (ADR-PC-029 slot 4). A retry presenting the SAME derived id is now a recognized replay.
            var receipt = await commandLog.TryGetAsync(commandId);
            Assert.NotNull(receipt);
            Assert.Equal(depositId, receipt!.StreamId);

            // The deposit folded to Matured, and the maturity appended its flow exactly ONCE (constituted +
            // accrued + withheld + matured = 4 events on the stream).
            Assert.Equal(DepositLifecycle.Matured, (await runtime.LoadAsync(depositId)).State.Lifecycle);
            Assert.Equal(4, await fixture.CountAsync("events", "stream_id", depositId));

            // (b) command_dedup is the dedupe AUTHORITY, independent of the F.3 legality gate. A SECOND append
            //     carrying the SAME derived command id collides on command_dedup_pkey → DuplicateCommandException.
            //     We append on a FRESH stream to make the point sharply: the guard is GLOBAL on command_id
            //     (migration 0015) and fires at the append's dedup INSERT — it never consults, and never relies
            //     on, the per-stream lifecycle legality. This is exactly the crash-atomic guard a concurrent
            //     maturity racer hits, the at-least-once safety maturity formerly lacked.
            var pack = SkeletonPack.LoadPt2026();
            var family = new TermDepositFamilyModule();
            var racerStream = Guid.NewGuid();
            var racerEvent = new DepositConstituted(
                racerStream, new Money(1_000_000), 300, "rs-1", 365,
                new DateOnly(2026, 1, 15), new DateOnly(2027, 1, 15), "AT_MATURITY", "NONE");
            await Assert.ThrowsAsync<DuplicateCommandException>(() =>
                runtime.AppendAsync(
                    racerStream, expectedVersion: -1, [racerEvent],
                    new AppendContext(
                        family.FamilyName, pack.VersionKey, family.SchemaVersion, "mcp:dev",
                        new DateTimeOffset(2027, 1, 15, 0, 0, 0, TimeSpan.Zero), CommandId: commandId)));
        }

        /// <summary>The single shared family sheet, pricing the AT_MATURITY product at 300 bps, effective
        /// before any constitution — mirroring <c>ConstituteAccrueMatureHappyPathTests</c>.</summary>
        private static RateSheet SharedSheet => TestRateSheets.MultiPriced(
            versionId: "pt-deposits-2026.1",
            effectiveFrom: new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            ("dpz_pt_12m_juros_venc", "standard", 300),
            ("dpz_pt_12m_juros_mensal", "standard", 325),
            ("dpz_pt_12m_juros_antecip", "standard", 300));

        /// <summary>Compose the durable runtime + decider over the term-deposit family (the same composition the
        /// AT_MATURITY happy-path test uses): a real Postgres event store, the JSON codec stand-in, no eager
        /// settlement (every money leg rides an append-first Movement, ADR-PC-032).</summary>
        private static (AggregateRuntime<DepositPosition> Runtime, TermDepositConstitutionService Service)
            Compose(string connectionString)
        {
            var store = new PostgresEventStore(connectionString);
            var runtime = new AggregateRuntime<DepositPosition>(
                store, new EventStoreSink(store), TermDepositFamilyModule.Registry(),
                new JsonEventSerializer(), new NullPiiProtector(), TimeProvider.System,
                () => DepositPosition.Empty);
            var service = new TermDepositConstitutionService(
                runtime, new PostgresRateSheetStore(connectionString), SkeletonPack.LoadPt2026(),
                dayCountPrimitive: "act_360", withholdingPrimitive: "irs_juros");
            return (runtime, service);
        }
    }
}
