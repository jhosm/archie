namespace Babelstone.Orchestrator.Saga;

/// <summary>
/// One loaded <c>saga_state</c> row — the persisted <c>ConstitutionProcess</c> aggregate
/// (Document 05 "the saga aggregate IS itself a domain entity"). Carries only structural,
/// PII-free fields (ADR-PC-004 §P2): a process reference, the saga type, the current
/// business <see cref="State"/>, the optimistic-concurrency <see cref="Version"/>, and the
/// correlation reference. A subject's PII is NEVER on this row — the saga carries
/// references and resolves PII internally behind the engine's OpenBao boundary.
/// </summary>
/// <param name="ProcessId">The saga instance id (the Document 05 PROC-… reference).</param>
/// <param name="SagaType">Which state machine governs this row (e.g. <c>ConstitutionProcess</c>).</param>
/// <param name="State">The current business state (ADR-IC-003 §P3).</param>
/// <param name="Version">The optimistic-concurrency guard (ADR-IC-003 §Residual "Concurrent
/// writer race"). An advance succeeds only against the version it read.</param>
/// <param name="CorrelationId">The originating request's correlation id, carried unchanged
/// through the saga (ADR-IC-003 §P7). Null only for a row started without one.</param>
public sealed record SagaInstance(
    Guid ProcessId,
    string SagaType,
    SagaState State,
    long Version,
    Guid? CorrelationId);
