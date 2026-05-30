using Babelstone.Engine;

namespace Babelstone.Families.TermDeposit;

/// <summary>
/// The term-deposit family module (E.1, archie-uqlm): the first loaded family schema.
/// Exports the event-type → handler bindings the engine dispatches and folds. Discovered by
/// <see cref="FamilyModuleLoader"/> via its public parameterless constructor.
/// </summary>
/// <remarks>
/// <see cref="FamilyName"/> and <see cref="SchemaVersion"/> match the CUE family schema
/// (contracts/cue/families/term-deposit.cue) and ride on the EventEnvelope via AppendContext.
/// The FamilyModuleLoader CUE cross-check (declared event types == the CUE-declared taxonomy,
/// ADR-PC-006) is deferred (archie-e6fr.6); the loader registers these bindings as-is.
/// </remarks>
public sealed class TermDepositFamilyModule : IFamilyModule
{
    public string FamilyName => "term_deposit";

    public string SchemaVersion => "term_deposit@2026.1";

    public IReadOnlyList<HandlerRegistration> Handlers =>
    [
        new("term_deposit.DepositConstituted", typeof(DepositConstituted),
            new DispatchableHandler<DepositPosition, DepositConstituted>(new DepositConstitutedHandler())),
        new("term_deposit.InterestAccrued", typeof(InterestAccrued),
            new DispatchableHandler<DepositPosition, InterestAccrued>(new InterestAccruedHandler())),
        new("term_deposit.WithholdingApplied", typeof(WithholdingApplied),
            new DispatchableHandler<DepositPosition, WithholdingApplied>(new WithholdingAppliedHandler())),
        new("term_deposit.DepositMatured", typeof(DepositMatured),
            new DispatchableHandler<DepositPosition, DepositMatured>(new DepositMaturedHandler())),
    ];

    /// <summary>Convenience for tests and the durable runtime: the registry for this family alone.</summary>
    public static HandlerRegistry Registry() => new(new TermDepositFamilyModule().Handlers);
}
