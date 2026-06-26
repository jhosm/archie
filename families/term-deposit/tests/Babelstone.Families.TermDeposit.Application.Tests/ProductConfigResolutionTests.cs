using Babelstone.Engine;
using Babelstone.EventStore;
using Babelstone.FinancialTypes;
using Babelstone.RateSheets;
using Xunit;

namespace Babelstone.Families.TermDeposit.Application.Tests;

/// <summary>
/// The Fork B rework spec (bd t7o3.11 / 3k10 / c8d8): the ENGINE resolves a product code to its
/// structural facts at constitution, so the orchestrator carries NO product-family knowledge. The
/// saga now sends only <c>{deposit_id, product_id, principal_cents, funding_account}</c>; the engine
/// looks up the term / interest variant / renewal policy / coupon cadence / role from its deployed
/// <c>product-configs/</c> store IN-TRANSACTION, alongside the existing rate-sheet resolve (ADR-PC-008
/// §S2). This test pins that resolution on the family decider — the constitution boundary.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ProductConfigResolutionTests(ConstitutionFixture fixture)
    : IClassFixture<ConstitutionFixture>
{
    /// <summary>
    /// A MINIMAL constitution (product code + principal + funding account + deposit id, NO structural
    /// facts) resolves the structural facts from the product-config store and constitutes correctly:
    /// the folded position carries TermDays = 365, InterestVariant = AT_MATURITY, AutoRenewalPolicy =
    /// NONE, PaymentPeriodMonths = 0 for dpz_pt_12m_juros_venc — all looked up engine-side, none sent.
    /// The rate is still resolved in-transaction (TanBasisPoints > 0).
    /// </summary>
    [Fact]
    public async Task Constitute_with_minimal_body_resolves_structural_facts_from_product_config()
    {
        await fixture.EnsureRateSheetAsync(SharedSheet);

        var (runtime, service) = Compose(fixture.ConnectionString);
        var depositId = Guid.NewGuid();

        // The minimal command the saga now sends: product code + principal + funding account + the
        // deposit id (= process_id). NO term_days / interest_variant / auto_renewal_policy / role /
        // start_date — the engine resolves them. ConstitutedAt is host-stamped (the host owns the clock).
        var commitSequence = await service.ConstituteFromProductConfigAsync(
            new MinimalConstituteDepositRequest(
                DepositId: depositId,
                ProductId: "dpz_pt_12m_juros_venc",
                PrincipalCents: 1_000_000,
                FundingAccount: "PT50-DDA-001",
                ConstitutedAt: new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero),
                Actor: "mcp:dev",
                CommandId: Guid.NewGuid()));

        Assert.True(commitSequence >= 0);

        var position = (await runtime.LoadAsync(depositId)).State;
        Assert.Equal(depositId, position.DepositId);
        Assert.Equal(new Money(1_000_000), position.Principal);
        // The STRUCTURAL facts were resolved engine-side from product-configs/dpz_pt_12m_juros_venc.yaml.
        Assert.Equal(365, position.TermDays);
        Assert.Equal("AT_MATURITY", position.InterestVariant);
        Assert.Equal("NONE", position.AutoRenewalPolicy);
        Assert.Equal(0, position.PaymentPeriodMonths);
        Assert.Equal("dpz_pt_12m_juros_venc", position.ProductCode);
        // The rate is STILL resolved in-transaction (not sent) — the TAN is stamped from the sheet.
        Assert.True(position.TanBasisPoints > 0);
        Assert.Equal("pt-deposits-2026.1", position.RateSheetVersionId);
        // start_date is derived host-side from ConstitutedAt (the engine is the constitution authority).
        Assert.Equal(new DateOnly(2026, 1, 15), position.StartDate);
        Assert.Equal(DepositLifecycle.Active, position.Lifecycle);
    }

    /// <summary>
    /// A minimal constitution for a product code the engine holds NO config for fails LOUD
    /// (DomainRejectedException) rather than constituting on a silent default — the engine is the
    /// fail-loud authority on whether a product code is known, exactly as the rate-sheet resolve is.
    /// </summary>
    [Fact]
    public async Task Constitute_with_minimal_body_for_an_unknown_product_fails_loud()
    {
        await fixture.EnsureRateSheetAsync(SharedSheet);

        var (_, service) = Compose(fixture.ConnectionString);

        await Assert.ThrowsAsync<DomainRejectedException>(() =>
            service.ConstituteFromProductConfigAsync(
                new MinimalConstituteDepositRequest(
                    DepositId: Guid.NewGuid(),
                    ProductId: "dpz_pt_does_not_exist",
                    PrincipalCents: 1_000_000,
                    FundingAccount: "PT50-DDA-001",
                    ConstitutedAt: new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero),
                    Actor: "mcp:dev",
                    CommandId: Guid.NewGuid())));
    }

    /// <summary>
    /// bd babelstone-fk7m.9 / ADR-PC-009 §A2 (REPLAY_PIN_PER_EVENT): a constitution PINS the
    /// product-config generation it resolved — the content-hash <c>ConfigVersion</c> — onto
    /// <c>DepositConstituted</c>, and a COLD fold from the persisted event log (a fresh runtime over the
    /// same store) re-derives the identical pin. This proves the pin is a per-event fact on the stream,
    /// not in-memory state, so a replay can prove which product-config generation governed the deposit.
    /// </summary>
    [Fact]
    public async Task Constitution_pins_the_product_config_version_and_a_cold_replay_reproduces_it()
    {
        await fixture.EnsureRateSheetAsync(SharedSheet);

        var (_, service) = Compose(fixture.ConnectionString);
        var depositId = Guid.NewGuid();

        await service.ConstituteFromProductConfigAsync(
            new MinimalConstituteDepositRequest(
                DepositId: depositId,
                ProductId: "dpz_pt_12m_juros_venc",
                PrincipalCents: 1_000_000,
                FundingAccount: "PT50-DDA-001",
                ConstitutedAt: new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero),
                Actor: "mcp:dev",
                CommandId: Guid.NewGuid()));

        // The pin the disk loader computed for this product config — a sha256:<hex> content hash.
        var expected = new YamlProductConfigStore(productConfigsDir: null)
            .Resolve("dpz_pt_12m_juros_venc")!.ConfigVersion;
        Assert.Matches("^sha256:[0-9a-f]{64}$", expected);

        // A FRESH runtime over the SAME store folds the stream cold from the event log — the pin must
        // come off the persisted DepositConstituted event, not any in-memory state (REPLAY_PIN_PER_EVENT).
        var coldStore = new PostgresEventStore(fixture.ConnectionString);
        var coldRuntime = new AggregateRuntime<DepositPosition>(
            coldStore, new EventStoreSink(coldStore), TermDepositFamilyModule.Registry(),
            new JsonEventSerializer(), new NullPiiProtector(), TimeProvider.System,
            () => DepositPosition.Empty);
        var replayed = (await coldRuntime.LoadAsync(depositId)).State;

        Assert.Equal(expected, replayed.ProductConfigVersion);
    }

    private static RateSheet SharedSheet => TestRateSheets.MultiPriced(
        versionId: "pt-deposits-2026.1",
        effectiveFrom: new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
        ("dpz_pt_12m_juros_venc", "standard", 300),
        ("dpz_pt_12m_juros_mensal", "standard", 325),
        ("dpz_pt_12m_juros_antecip", "standard", 300));

    /// <summary>Compose the durable runtime + decider with the disk-backed product-config store loaded
    /// from the repo's committed product-configs/ tree — the engine's real resolution path.</summary>
    private static (AggregateRuntime<DepositPosition> Runtime, TermDepositConstitutionService Service)
        Compose(string connectionString)
    {
        var store = new PostgresEventStore(connectionString);
        var runtime = new AggregateRuntime<DepositPosition>(
            store, new EventStoreSink(store), TermDepositFamilyModule.Registry(),
            new JsonEventSerializer(), new NullPiiProtector(), TimeProvider.System,
            () => DepositPosition.Empty);
        var service = new TermDepositConstitutionService(
            runtime, new PostgresRateSheetStore(connectionString),
            SkeletonPack.LoadPt2026(), dayCountPrimitive: "act_360", withholdingPrimitive: "irs_juros",
            productConfigStore: new YamlProductConfigStore(productConfigsDir: null));
        return (runtime, service);
    }
}
