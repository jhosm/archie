using Babelstone.Engine;
using Babelstone.EventStore;
using Babelstone.RateSheets;
using Xunit;

namespace Babelstone.Families.TermDeposit.Application.Tests;

/// <summary>
/// F.9 commercial-eligibility preconditions end-to-end (ADR-PC-024, babelstone-k6r8.2). Drives the
/// real durable service path: a product whose <c>required_preconditions</c> is unmet by the command's
/// verdicts is REFUSED — the service appends <c>DepositConstitutionFailed</c> (reason
/// <c>ELIGIBILITY_NOT_MET</c>) as the stream's first and only event, folds to <c>Failed</c>, and
/// performs NO settlement (the refusal precedes the irreversible Core debit, ADR-PC-024 §5). A gated
/// product whose verdicts ARE all satisfied constitutes normally. Tagged Integration so it runs in the
/// Testcontainers lane, not the default unit lane.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ConstitutionPreconditionTests(ConstitutionFixture fixture)
    : IClassFixture<ConstitutionFixture>
{
    private static readonly DateTimeOffset EvaluatedAt = new(2026, 1, 14, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Gated_product_with_unmet_precondition_refuses_without_settling()
    {
        await fixture.EnsureRateSheetAsync(SharedSheet);

        // The product gates on is_new_money + salary_domiciled; the saga resolved is_new_money satisfied
        // but salary_domiciled NOT satisfied — the decider must refuse before debiting the principal.
        var (runtime, service) = Compose(
            fixture.ConnectionString,
            requiredPreconditions:
            [
                TermDepositDecider.PreconditionIsNewMoney,
                TermDepositDecider.PreconditionSalaryDomiciled,
            ]);
        var depositId = Guid.NewGuid();

        await service.ConstituteAsync(Command(depositId, verdicts: new Dictionary<string, PreconditionVerdict>
        {
            [TermDepositDecider.PreconditionIsNewMoney] = new(true, "ref-001", EvaluatedAt),
            [TermDepositDecider.PreconditionSalaryDomiciled] = new(false, "ref-002", EvaluatedAt),
        }));

        // Exactly ONE event — DepositConstitutionFailed — on the stream; the deposit folds to Failed.
        var hydrated = await runtime.LoadAsync(depositId);
        Assert.Equal(0, hydrated.Version); // single event at sequence 0
        Assert.Equal(DepositLifecycle.Failed, hydrated.State.Lifecycle);
        Assert.Equal(1, await fixture.CountAsync("events", "stream_id", depositId));
        // The recorded event is the eligibility refusal — not a constitution.
        Assert.Equal(1, await fixture.CountAsync("events", "stream_id", depositId));
        await fixture.EventIdAsync(depositId, "term_deposit.DepositConstitutionFailed"); // throws if absent/non-unique

        // No settlement at all — the irreversible Core debit never fired (ADR-PC-024 §5: a refusal, not a
        // compensation). The eager settlement port is GONE (bd babelstone-t7o3.17): a refusal appends only
        // DepositConstitutionFailed, with no money leg of any kind.
    }

    [Fact]
    public async Task Gated_product_with_all_preconditions_satisfied_constitutes_normally()
    {
        await fixture.EnsureRateSheetAsync(SharedSheet);

        var (runtime, service) = Compose(
            fixture.ConnectionString,
            requiredPreconditions:
            [
                TermDepositDecider.PreconditionIsNewMoney,
                TermDepositDecider.PreconditionSalaryDomiciled,
            ]);
        var depositId = Guid.NewGuid();

        await service.ConstituteAsync(Command(depositId, verdicts: new Dictionary<string, PreconditionVerdict>
        {
            [TermDepositDecider.PreconditionIsNewMoney] = new(true, "ref-001", EvaluatedAt),
            [TermDepositDecider.PreconditionSalaryDomiciled] = new(true, "ref-002", EvaluatedAt),
        }));

        // All required verdicts satisfied ⇒ a normal constitution: the deposit is Active and
        // DepositConstituted was appended. Per bd babelstone-t7o3.4 the constitution path is now
        // DE-SETTLED — the principal debit is the saga's gated ReserveAccountBalance→ConfirmDebit step
        // (ADR-PC-016 §68/§127), NOT an eager in-engine debit — so NO settlement leg fires here, exactly
        // as in the refusal case above (the difference is the deposit is Active, not Failed). The eager
        // settlement port is GONE (bd babelstone-t7o3.17), so there is nothing eager to assert against.
        var hydrated = await runtime.LoadAsync(depositId);
        Assert.Equal(DepositLifecycle.Active, hydrated.State.Lifecycle);
        await fixture.EventIdAsync(depositId, "term_deposit.DepositConstituted"); // throws if absent
    }

    private static ConstituteDepositCommand Command(
        Guid depositId, IReadOnlyDictionary<string, PreconditionVerdict> verdicts) =>
        new(
            DepositId: depositId, PrincipalCents: 1_000_000, ProductId: "dpz_pt_12m_juros_venc", Role: "standard",
            TermDays: 365, StartDate: new DateOnly(2026, 1, 15),
            ConstitutedAt: new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero),
            InterestVariant: "AT_MATURITY", AutoRenewalPolicy: "NONE", FundingAccount: "PT50-DDA-001",
            Actor: "mcp:dev", Preconditions: verdicts);

    // The shared family sheet prices the AT_MATURITY product at 300 bps, effective before constitution.
    private static RateSheet SharedSheet => TestRateSheets.MultiPriced(
        versionId: "pt-deposits-2026.1",
        effectiveFrom: new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
        ("dpz_pt_12m_juros_venc", "standard", 300),
        ("dpz_pt_12m_juros_mensal", "standard", 325),
        ("dpz_pt_12m_juros_antecip", "standard", 300));

    /// <summary>Compose the durable runtime + decider, passing the product's required preconditions
    /// (ADR-PC-024 engine-instance config) — otherwise identical to the happy-path composition.</summary>
    private static (AggregateRuntime<DepositPosition> Runtime, TermDepositConstitutionService Service)
        Compose(string connectionString, IReadOnlyCollection<string> requiredPreconditions)
    {
        var store = new PostgresEventStore(connectionString);
        var runtime = new AggregateRuntime<DepositPosition>(
            store, new EventStoreSink(store), TermDepositFamilyModule.Registry(),
            new JsonEventSerializer(), new NullPiiProtector(), TimeProvider.System,
            () => DepositPosition.Empty);
        var service = new TermDepositConstitutionService(
            runtime, new PostgresRateSheetStore(connectionString), SkeletonPack.LoadPt2026(),
            dayCountPrimitive: "act_360", withholdingPrimitive: "irs_juros",
            requiredPreconditions: requiredPreconditions);
        return (runtime, service);
    }
}
