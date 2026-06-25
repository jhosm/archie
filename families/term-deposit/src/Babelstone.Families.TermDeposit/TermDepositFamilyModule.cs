using Babelstone.Engine;

namespace Babelstone.Families.TermDeposit;

// Stryker disable all : Pure DI/registration glue — the event-type→handler binding table the engine
// dispatches, not the folds/cent-math it wires up (mutation-testing.md "Mutation scope"). Disabled
// inline rather than via the family config's `mutate` list because this family project lives
// out-of-tree (../families/…) relative to the engine/ working dir, so Stryker's file-glob cannot
// reach it; the inline directive is path-independent. The behaviour files (folds, accrual,
// withholding, lifecycle) stay fully mutated.

/// <summary>
/// The term-deposit family module (E.1, archie-uqlm): the first loaded family schema.
/// Exports the event-type → handler bindings the engine dispatches and folds. Discovered by
/// <see cref="FamilyModuleLoader"/> via its public parameterless constructor.
/// </summary>
/// <remarks>
/// <see cref="FamilyName"/> and <see cref="SchemaVersion"/> match the CUE family schema
/// (contracts/cue/families/term-deposit.cue) and ride on the EventEnvelope via AppendContext.
/// The loader registers these bindings as-is; the originally-filed full CUE cross-check (declared event
/// types == a CUE-declared event taxonomy, ADR-PC-006) was DROPPED (bd babelstone-e6fr.6) — no CUE event
/// taxonomy exists and the fold is already fail-closed (see <see cref="FamilyModuleLoader"/>). The
/// catalogue→handler completeness half ships as FamilyHandlerCatalogueCompletenessTests.
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
        // The engine-declared cross-cutting operational events (event-store §4.1), bound against this
        // family's DepositPosition. The engine owns the event records + generic handlers (they name no
        // family — ADR-PC-021 §P2); the family supplies only its TState here, splicing in every
        // cross-cutting binding in one call so it cannot forget one as the set grows:
        // operations.PackVersionMigrated (ADR-PC-009 §P3, an operator pack re-pin; STORE-ONLY — the pin
        // lives on the envelope so the fold is a no-op, no .avsc, kept off the bus) and
        // operations.PersonalDataErasureRequested (ADR-PC-004 §P3/A4, the GDPR Article 17 erasure — a
        // PROMOTED cross-cutting event folded here to DepositPosition.WithErased via IErasable).
        .. CrossCuttingEventRegistrations.For<DepositPosition>(),
    ];

    /// <summary>Convenience for tests and the durable runtime: the registry for this family alone.</summary>
    public static HandlerRegistry Registry() => new(new TermDepositFamilyModule().Handlers);
}
