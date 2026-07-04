using Babelstone.Engine;
using Babelstone.Engine.Hosting;
using Babelstone.EventStore;
using Babelstone.FinancialTypes;
using Babelstone.RateSheets;
using Xunit;

namespace Babelstone.Families.TermDeposit.Application.Tests;

/// <summary>
/// In plain English: paying a PERIODIC coupon used to carry no idempotency key at all, so a duplicate
/// "pay interest" had no dedupe backstop — its only guard was optimistic concurrency plus the coupon-window
/// legality gate. These tests pin the new behaviour — a coupon now carries a key the SERVER computes from the
/// deposit id and the coupon NUMBER, so two firings of the SAME still-due coupon collapse to a single append
/// at the engine's dedupe ledger, while the NEXT coupon derives a fresh key.
///
/// ADR-PC-036 Decision 1+3 (bd babelstone-6cpq.18). Interest mirrors the loan installment endpoint (LCD-1) —
/// but where maturity is the degenerate ONE-SHOT occurrence (<c>Derive(deposit_id, "mature", 1)</c>), a coupon
/// is a RECURRING occurrence pinned to the coupon number: <c>Derive(deposit_id, "pay_interest",
/// coupons_paid + 1)</c>. Two parts, exactly the <see cref="MatureCommandIdempotencyTests"/> shape: PURE unit
/// checks that the derivation is deterministic, per-deposit distinct, and PROGRESSES with the occurrence
/// number, and an integration check (real PostgreSQL) that
/// <see cref="TermDepositConstitutionService.PayInterestAsync"/> threads the derived id into
/// <c>command_dedup</c> (ADR-PC-029 slot 4) and that a second append carrying the SAME id is rejected by
/// <c>command_dedup</c> itself — independent of the F.3 legality gate.
/// </summary>
public sealed class PayInterestCommandIdempotencyTests
{
    // The coupon command space, mirroring the DepositsEndpoints constant (ADR-PC-036 Decision 1+3): the
    // stable command_kind, and the RECURRING occurrence number (coupons_paid + 1), unlike maturity's
    // constant one-shot 1.
    private const string PayInterestKind = "pay_interest";

    // ---- pure unit tests on the coupon key derivation (no Postgres) --------------------------------

    [Fact]
    public void Coupon_key_is_deterministic_in_the_deposit_id_and_occurrence()
    {
        var depositId = Guid.NewGuid();
        // The same coupon occurrence ALWAYS derives the byte-identical id — the property a manual caller,
        // the MCP agent, and the lifecycle driver lean on to converge on ONE key (one append) for a
        // re-dated retry of the SAME still-due coupon.
        Assert.Equal(
            LifecycleCommandKey.Derive(depositId, PayInterestKind, 1),
            LifecycleCommandKey.Derive(depositId, PayInterestKind, 1));
    }

    [Fact]
    public void Coupon_key_is_distinct_per_deposit()
    {
        Assert.NotEqual(
            LifecycleCommandKey.Derive(Guid.NewGuid(), PayInterestKind, 1),
            LifecycleCommandKey.Derive(Guid.NewGuid(), PayInterestKind, 1));
    }

    [Fact]
    public void Coupon_key_progresses_with_the_occurrence_number()
    {
        // Unlike the one-shot maturity (constant occurrence 1), a coupon is RECURRING: coupon N and coupon
        // N+1 on the SAME deposit derive DIFFERENT keys, so paying coupon 2 after coupon 1 is a NEW command
        // (a fresh dedupe slot), never a replay of coupon 1. This is the recurring progression the loan
        // installment key also relies on (InstallmentsPaid + 1).
        var depositId = Guid.NewGuid();
        var coupon1 = LifecycleCommandKey.Derive(depositId, PayInterestKind, 1);
        var coupon2 = LifecycleCommandKey.Derive(depositId, PayInterestKind, 2);
        var coupon3 = LifecycleCommandKey.Derive(depositId, PayInterestKind, 3);
        Assert.NotEqual(coupon1, coupon2);
        Assert.NotEqual(coupon2, coupon3);
        Assert.NotEqual(coupon1, coupon3);
    }

    [Fact]
    public void Coupon_key_is_a_nonempty_uuid_distinct_from_a_maturity_occurrence()
    {
        var instance = Guid.NewGuid();
        var couponKey = LifecycleCommandKey.Derive(instance, PayInterestKind, 1);
        Assert.NotEqual(Guid.Empty, couponKey);
        // The command_kind discriminates: a coupon ("pay_interest", 1) never collides with the deposit's
        // one-shot maturity ("mature", 1) on the same aggregate id, so the two command spaces stay disjoint.
        Assert.NotEqual(couponKey, LifecycleCommandKey.Derive(instance, "mature", 1));
    }

    // ---- integration (Testcontainers): the COMMAND threads the derived id into command_dedup ----------

    /// <summary>
    /// End-to-end on real PostgreSQL: constitute a PERIODIC deposit, then pay its first coupon with the
    /// SERVER-DERIVED command id — exactly what the interest endpoint / MCP tool / lifecycle driver compute
    /// for coupon occurrence 1. It proves (a) the derived id landed in <c>command_dedup</c> pointing at the
    /// deposit stream — so a retry presenting the SAME id is a recognized replay (ADR-PC-029 slot 4), no
    /// longer merely a concurrency race; and (b) <c>command_dedup</c> is the dedupe AUTHORITY, independent of
    /// the F.3 legality gate — a second append carrying the SAME derived id (on a fresh stream, to show the
    /// guard is GLOBAL on command_id, migration 0015) raises <see cref="DuplicateCommandException"/>.
    /// </summary>
    [Trait("Category", "Integration")]
    public sealed class Integration(ConstitutionFixture fixture) : IClassFixture<ConstitutionFixture>
    {
        [Fact]
        public async Task PayInterest_threads_the_server_derived_id_into_command_dedup_independent_of_the_legality_gate()
        {
            await fixture.EnsureRateSheetAsync(SharedSheet);
            var (runtime, service) = Compose(fixture.ConnectionString);
            var commandLog = new PostgresCommandLog(fixture.ConnectionString);

            var depositId = Guid.NewGuid();
            await service.ConstituteAsync(new ConstituteDepositCommand(
                DepositId: depositId, PrincipalCents: 1_000_000, ProductId: "dpz_pt_12m_juros_mensal",
                Role: "standard", TermDays: 365, StartDate: new DateOnly(2026, 1, 15),
                ConstitutedAt: new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero),
                InterestVariant: "PERIODIC", AutoRenewalPolicy: "NONE", FundingAccount: "PT50-DDA-001",
                Actor: "mcp:dev", PaymentPeriodMonths: 1));

            // Pay coupon occurrence 1 with the SERVER-DERIVED command id (ADR-PC-036 Decision 1+3) — the
            // interest endpoint, the MCP tool, and the driver all compute THIS canonical id for the first
            // still-due coupon (CouponsPaid + 1 = 1 on a fresh, un-couponed deposit).
            var commandId = LifecycleCommandKey.Derive(depositId, "pay_interest", 1);
            await service.PayInterestAsync(new PayInterestCommand(
                DepositId: depositId, PaidAt: new DateTimeOffset(2026, 2, 15, 0, 0, 0, TimeSpan.Zero),
                PayoutAccount: "PT50-DDA-001", Actor: "mcp:dev", CommandId: commandId));

            // (a) The derived id landed in command_dedup, pointing at the deposit stream — the append
            //     threaded it (ADR-PC-029 slot 4). A retry presenting the SAME derived id is now a
            //     recognized replay.
            var receipt = await commandLog.TryGetAsync(commandId);
            Assert.NotNull(receipt);
            Assert.Equal(depositId, receipt!.StreamId);

            // The deposit paid its coupon exactly ONCE — CouponsPaid advanced to 1.
            Assert.Equal(1, (await runtime.LoadAsync(depositId)).State.CouponsPaid);

            // (b) command_dedup is the dedupe AUTHORITY, independent of the F.3 legality gate. A SECOND
            //     append carrying the SAME derived command id collides on command_dedup_pkey →
            //     DuplicateCommandException. We append on a FRESH stream to make the point sharply: the guard
            //     is GLOBAL on command_id (migration 0015) and fires at the append's dedup INSERT — it never
            //     consults, and never relies on, the per-stream lifecycle legality. This is exactly the
            //     crash-atomic guard a concurrent coupon racer hits, the at-least-once safety interest
            //     formerly lacked entirely.
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
                        new DateTimeOffset(2026, 2, 15, 0, 0, 0, TimeSpan.Zero), CommandId: commandId)));
        }

        /// <summary>The single shared family sheet, pricing the PERIODIC product used here (and the
        /// AT_MATURITY racer product), effective before any constitution — mirroring
        /// <see cref="MatureCommandIdempotencyTests"/>.</summary>
        private static RateSheet SharedSheet => TestRateSheets.MultiPriced(
            versionId: "pt-deposits-2026.1",
            effectiveFrom: new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            ("dpz_pt_12m_juros_venc", "standard", 300),
            ("dpz_pt_12m_juros_mensal", "standard", 325),
            ("dpz_pt_12m_juros_antecip", "standard", 300));

        /// <summary>Compose the durable runtime + decider over the term-deposit family (the same composition
        /// the maturity idempotency test uses): a real Postgres event store, the JSON codec stand-in, no
        /// eager settlement (every money leg rides an append-first Movement, ADR-PC-032).</summary>
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
