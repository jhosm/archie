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
/// <param name="PublicProcessId">The client-facing <c>PROC-…</c> reference the edge minted and
/// returned (Document 05 §Step 0); the SSE <c>stream_url</c> is keyed on it. A structural,
/// PII-free handle — NOT a capability token (ADR-IC-006 §P4). Null for a saga started by the
/// consume loop rather than the edge (it has no public reference).</param>
/// <param name="OwningClientId">The client that OWNS this process (the request's
/// <c>client_id</c>). The SSE read enforces the requester's <c>client_id</c> matches this so a
/// guessed/stolen <c>process_id</c> yields no updates (ADR-IC-006 §P4 / Document 05 §Step 0). An
/// opaque business reference, NOT PII. Null for a consume-loop-started saga.</param>
/// <param name="SubjectId">The account/instrument this instance belongs to (the persisted, NOT NULL
/// <c>saga_state.subject_id</c>, migration 0009): the triggering event's real <c>ce_subject</c>. For a
/// per-occurrence settlement instance (ADR-PC-032 §A9/§A10 Revised 2026-07-04) it differs from the
/// derived <see cref="ProcessId"/>; for every other saga it equals it. <c>null</c> only on an in-memory
/// instance constructed by a caller that did not thread it — a LOADED row always carries it. Structural
/// GUID, never PII (ADR-PC-004 §P2).</param>
public sealed record SagaInstance(
    Guid ProcessId,
    string SagaType,
    string State,
    long Version,
    Guid? CorrelationId,
    string? PublicProcessId = null,
    string? OwningClientId = null,
    Guid? SubjectId = null);
