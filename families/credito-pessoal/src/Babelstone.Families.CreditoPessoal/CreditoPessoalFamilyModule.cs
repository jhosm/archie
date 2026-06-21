using Babelstone.Engine;

namespace Babelstone.Families.CreditoPessoal;

/// <summary>
/// The credito_pessoal family module (ADR-PC-030 roadmap item 2): the 2nd loaded family schema, the
/// closed-end-asset (personal loan) sibling of the term-deposit liability. Exports the event-type →
/// handler bindings the engine dispatches and folds. Discovered by <see cref="FamilyModuleLoader"/>
/// via its public parameterless constructor — the host needs no per-family hand-edit to pick it up.
/// </summary>
/// <remarks>
/// <see cref="FamilyName"/> and <see cref="SchemaVersion"/> match the CUE family schema
/// (contracts/cue/families/credito-pessoal.cue) and ride on the EventEnvelope via AppendContext.
/// The <c>credito_pessoal.*</c> event-type prefix keeps the taxonomy in lock-step with the schema.
/// </remarks>
public sealed class CreditoPessoalFamilyModule : IFamilyModule
{
    public string FamilyName => "credito_pessoal";

    public string SchemaVersion => "credito_pessoal@2026.1";

    public IReadOnlyList<HandlerRegistration> Handlers =>
    [
        new("credito_pessoal.LoanDisbursed", typeof(LoanDisbursed),
            new DispatchableHandler<LoanPosition, LoanDisbursed>(new LoanDisbursedHandler())),
        new("credito_pessoal.LoanDisbursementFailed", typeof(LoanDisbursementFailed),
            new DispatchableHandler<LoanPosition, LoanDisbursementFailed>(new LoanDisbursementFailedHandler())),
        new("credito_pessoal.LoanInstallmentPaid", typeof(LoanInstallmentPaid),
            new DispatchableHandler<LoanPosition, LoanInstallmentPaid>(new LoanInstallmentPaidHandler())),
        new("credito_pessoal.LoanRepaidEarly", typeof(LoanRepaidEarly),
            new DispatchableHandler<LoanPosition, LoanRepaidEarly>(new LoanRepaidEarlyHandler())),
        new("credito_pessoal.LoanSettled", typeof(LoanSettled),
            new DispatchableHandler<LoanPosition, LoanSettled>(new LoanSettledHandler())),
        new("credito_pessoal.LoanWrittenOff", typeof(LoanWrittenOff),
            new DispatchableHandler<LoanPosition, LoanWrittenOff>(new LoanWrittenOffHandler())),
        new("credito_pessoal.PersonalDataErasureRequested", typeof(PersonalDataErasureRequested),
            new DispatchableHandler<LoanPosition, PersonalDataErasureRequested>(new PersonalDataErasureRequestedHandler())),
        // The engine-declared cross-cutting operational events (event-store §4.1), bound against this
        // family's LoanPosition. The engine owns the event records + generic handlers (they name no
        // family — ADR-PC-021 §P2); the family supplies only its TState here, splicing in every
        // cross-cutting binding in one call so it cannot forget one as the set grows. STORE-ONLY.
        .. CrossCuttingEventRegistrations.For<LoanPosition>(),
    ];

    /// <summary>Convenience for tests and the durable runtime: the registry for this family alone.</summary>
    public static HandlerRegistry Registry() => new(new CreditoPessoalFamilyModule().Handlers);
}
