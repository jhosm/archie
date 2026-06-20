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
        new("term_deposit.DepositConstitutionFailed", typeof(DepositConstitutionFailed),
            new DispatchableHandler<DepositPosition, DepositConstitutionFailed>(new DepositConstitutionFailedHandler())),
        new("term_deposit.InterestPaid", typeof(InterestPaid),
            new DispatchableHandler<DepositPosition, InterestPaid>(new InterestPaidHandler())),
        new("term_deposit.DepositRenewed", typeof(DepositRenewed),
            new DispatchableHandler<DepositPosition, DepositRenewed>(new DepositRenewedHandler())),
        new("term_deposit.DepositTerminatedEarly", typeof(DepositTerminatedEarly),
            new DispatchableHandler<DepositPosition, DepositTerminatedEarly>(new DepositTerminatedEarlyHandler())),
        new("term_deposit.DepositPartiallyWithdrawn", typeof(DepositPartiallyWithdrawn),
            new DispatchableHandler<DepositPosition, DepositPartiallyWithdrawn>(new DepositPartiallyWithdrawnHandler())),
        new("term_deposit.DepositCorrected", typeof(DepositCorrected),
            new DispatchableHandler<DepositPosition, DepositCorrected>(new DepositCorrectedHandler())),
        new("term_deposit.DepositTransferredToHeirs", typeof(DepositTransferredToHeirs),
            new DispatchableHandler<DepositPosition, DepositTransferredToHeirs>(new DepositTransferredToHeirsHandler())),
        new("term_deposit.PersonalDataErasureRequested", typeof(PersonalDataErasureRequested),
            new DispatchableHandler<DepositPosition, PersonalDataErasureRequested>(new PersonalDataErasureRequestedHandler())),
        // The engine-declared cross-cutting operational events (event-store §4.1), bound against this
        // family's DepositPosition. The engine owns the event records + generic handlers (they name no
        // family — ADR-PC-021 §P2); the family supplies only its TState here, splicing in every
        // cross-cutting binding in one call so it cannot forget one as the set grows. Currently
        // operations.PackVersionMigrated (ADR-PC-009 §P3): an operator pack re-pin. STORE-ONLY — these
        // fold deterministically (the pin lives on the envelope, so the fold is a no-op) but carry no
        // .avsc, so the fail-closed catalog gate keeps them off the bus (ADR-IC-017 §P1).
        .. CrossCuttingEventRegistrations.For<DepositPosition>(),
    ];

    /// <summary>Convenience for tests and the durable runtime: the registry for this family alone.</summary>
    public static HandlerRegistry Registry() => new(new TermDepositFamilyModule().Handlers);
}
