using Babelstone.Engine;

namespace Babelstone.Families.TermDeposit;

/// <summary>
/// The term-deposit family's projection declarations (two-modes §5.4: declared in the family,
/// not hardcoded in the engine). D.2 declares ONE projection — the deposit position — folded
/// from the family's existing handlers; F.6 adds the accrual schedule, maturity calendar, and
/// withholding ledger as further runners here. Every v1 projection is <see cref="ProjectionMode.Async"/>.
/// </summary>
/// <remarks>
/// The runner reuses the family's own folds (the same <see cref="TermDepositFamilyModule.Registry"/>
/// the durable runtime uses), so the deposit-position projection materialised into the bitemporal
/// table is the SAME fold the live read path computes — that equivalence is what D.5's
/// reconciliation drill asserts. The engine spine never names this family; the host composes infra
/// + this declaration (ADR-PC-021 §D4).
/// </remarks>
public sealed class TermDepositProjectionModule : IProjectionModule
{
    /// <summary>The family-prefixed discriminator for the deposit-position projection (migration 0010).</summary>
    public const string DepositPositionKind = "term_deposit.deposit_position";

    public string FamilyName => "term_deposit";

    public IReadOnlyList<IProjectionRunner> CreateRunners(ProjectionInfra infra)
    {
        var store = new ProjectionStore<DepositPosition>(infra.Storage, new JsonStateSerializer<DepositPosition>());

        var depositPosition = new ProjectionRunner<DepositPosition>(
            kind: DepositPositionKind,
            family: FamilyName,
            mode: ProjectionMode.Async,
            handlers: TermDepositFamilyModule.Registry(),
            serializer: infra.EventSerializer,
            seed: () => DepositPosition.Empty,
            store: store);

        return [depositPosition];
    }
}
