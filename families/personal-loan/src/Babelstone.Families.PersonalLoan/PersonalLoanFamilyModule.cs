using Babelstone.Engine;

namespace Babelstone.Families.PersonalLoan;

/// <summary>
/// The personal_loan family module (ADR-PC-030 roadmap item 2): the 2nd loaded family schema, the
/// closed-end-asset (personal loan) sibling of the term-deposit liability. Exports the event-type →
/// handler bindings the engine dispatches and folds. Discovered by <see cref="FamilyModuleLoader"/>
/// via its public parameterless constructor — the host needs no per-family hand-edit to pick it up.
/// </summary>
/// <remarks>
/// <see cref="FamilyName"/> and <see cref="SchemaVersion"/> match the CUE family schema
/// (contracts/cue/families/personal-loan.cue) and ride on the EventEnvelope via AppendContext.
/// The <c>personal_loan.*</c> event-type prefix keeps the taxonomy in lock-step with the schema.
/// </remarks>
public sealed class PersonalLoanFamilyModule : IFamilyModule
{
    public string FamilyName => "personal_loan";

    public string SchemaVersion => "personal_loan@2026.1";

    public IReadOnlyList<HandlerRegistration> Handlers =>
    [
        new("personal_loan.LoanDisbursed", typeof(LoanDisbursed),
            new DispatchableHandler<LoanPosition, LoanDisbursed>(new LoanDisbursedHandler())),
        new("personal_loan.LoanDisbursementFailed", typeof(LoanDisbursementFailed),
            new DispatchableHandler<LoanPosition, LoanDisbursementFailed>(new LoanDisbursementFailedHandler())),
        new("personal_loan.LoanInstallmentPaid", typeof(LoanInstallmentPaid),
            new DispatchableHandler<LoanPosition, LoanInstallmentPaid>(new LoanInstallmentPaidHandler())),
        new("personal_loan.LoanRepaidEarly", typeof(LoanRepaidEarly),
            new DispatchableHandler<LoanPosition, LoanRepaidEarly>(new LoanRepaidEarlyHandler())),
        new("personal_loan.LoanSettled", typeof(LoanSettled),
            new DispatchableHandler<LoanPosition, LoanSettled>(new LoanSettledHandler())),
        new("personal_loan.LoanWrittenOff", typeof(LoanWrittenOff),
            new DispatchableHandler<LoanPosition, LoanWrittenOff>(new LoanWrittenOffHandler())),
        new("personal_loan.PersonalDataErasureRequested", typeof(PersonalDataErasureRequested),
            new DispatchableHandler<LoanPosition, PersonalDataErasureRequested>(new PersonalDataErasureRequestedHandler())),
        // The engine-declared cross-cutting operational events (event-store §4.1), bound against this
        // family's LoanPosition. The engine owns the event records + generic handlers (they name no
        // family — ADR-PC-021 §P2); the family supplies only its TState here, splicing in every
        // cross-cutting binding in one call so it cannot forget one as the set grows. STORE-ONLY.
        .. CrossCuttingEventRegistrations.For<LoanPosition>(),
    ];

    /// <summary>Convenience for tests and the durable runtime: the registry for this family alone.</summary>
    public static HandlerRegistry Registry() => new(new PersonalLoanFamilyModule().Handlers);
}
