using System.Text.Json.Serialization;

namespace Babelstone.Engine.Api;

/// <summary>
/// The lifecycle state of an asynchronously-dispatched command (I.1, bd babelstone-pxj9). A process
/// starts <see cref="Processing"/> and reaches exactly one terminal state: <see cref="Succeeded"/>
/// (the command path committed), <see cref="Rejected"/> (a domain precondition said no — the async
/// analogue of the synchronous 422), or <see cref="Failed"/> (an infrastructure/wiring fault).
/// </summary>
/// <remarks>
/// Serialized as its NAME on the wire (a stable string the SSE consumer switches on), not a brittle
/// ordinal — the converter is scoped to this enum so the host's other contracts are untouched.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<ProcessStatus>))]
public enum ProcessStatus
{
    /// <summary>The command has been accepted and is being dispatched through the engine command path.</summary>
    Processing,

    /// <summary>Terminal: the command committed; <c>aggregate_id</c> + <c>commit_sequence</c> are set.</summary>
    Succeeded,

    /// <summary>Terminal: a domain precondition rejected the command (the async 422 analogue).</summary>
    Rejected,

    /// <summary>Terminal: an infrastructure/wiring fault aborted the dispatch (the async 500 analogue).</summary>
    Failed,
}

/// <summary>
/// One immutable point-in-time view of a tracked process — the unit the SSE stream emits
/// (<see cref="ProcessStreamEndpoints"/>) and the 202 response references by <see cref="ProcessId"/>.
/// snake_case on the wire (the host's JSON options); no PII, structural facts only (ADR-PC-004 §P2).
/// </summary>
/// <param name="ProcessId">The host-assigned process identity carried in the 202 and the stream URL.</param>
/// <param name="Status">The current lifecycle state.</param>
/// <param name="AggregateId">The affected aggregate (e.g. the deposit id), once known on success.</param>
/// <param name="CommitSequence">The per-stream head version the append reached, on success (ADR-IC-005 §P3
/// read-your-writes token the caller threads as <c>If-Min-Sequence</c> on the follow-up GET).</param>
/// <param name="Detail">A human-readable reason on a <see cref="ProcessStatus.Rejected"/> /
/// <see cref="ProcessStatus.Failed"/> terminal — null while processing or on success.</param>
/// <param name="UpdatedAt">When this snapshot was produced (host wall-clock, for honest progress display).</param>
public sealed record ProcessSnapshot(
    Guid ProcessId,
    ProcessStatus Status,
    Guid? AggregateId,
    long? CommitSequence,
    string? Detail,
    DateTimeOffset UpdatedAt)
{
    /// <summary>True once the process has reached one of the three terminal states.</summary>
    public bool IsTerminal => Status is not ProcessStatus.Processing;

    /// <summary>The opening snapshot: PROCESSING, nothing decided yet.</summary>
    public static ProcessSnapshot Initial(Guid processId, DateTimeOffset at) =>
        new(processId, ProcessStatus.Processing, AggregateId: null, CommitSequence: null, Detail: null, at);

    /// <summary>Terminal SUCCEEDED, carrying the affected aggregate id + its commit_sequence.</summary>
    public ProcessSnapshot Succeeded(ProcessOutcome outcome, DateTimeOffset at) => this with
    {
        Status = ProcessStatus.Succeeded,
        AggregateId = outcome.AggregateId,
        CommitSequence = outcome.CommitSequence,
        Detail = null,
        UpdatedAt = at,
    };

    /// <summary>Terminal REJECTED, carrying the domain rejection reason.</summary>
    public ProcessSnapshot Rejected(string detail, DateTimeOffset at) => this with
    {
        Status = ProcessStatus.Rejected,
        Detail = detail,
        UpdatedAt = at,
    };

    /// <summary>Terminal FAILED, carrying the infrastructure-fault reason.</summary>
    public ProcessSnapshot Failed(string detail, DateTimeOffset at) => this with
    {
        Status = ProcessStatus.Failed,
        Detail = detail,
        UpdatedAt = at,
    };
}
