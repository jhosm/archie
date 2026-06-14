namespace Babelstone.Orchestrator.Edge;

/// <summary>
/// Configuration for the edge HTTP surface (I.1). The runtime-role PostgreSQL connection the edge
/// starts the saga through and the SSE read observes state through — the SAME
/// <c>babelstone_orchestrator</c> runtime credential the consume loop and dispatcher use, resolved
/// at the composition root through the ADR-PC-004 Amendment A1 boundary, never on a saga row or the
/// bus (ADR-PC-004 §P2). The SSE poll cadence is operational tuning, not a delivery deadline.
/// </summary>
public sealed record EdgeOptions
{
    /// <summary>The orchestrator runtime-role connection string.</summary>
    public required string ConnectionString { get; init; }

    /// <summary>How often the SSE loop polls the saga state for a move. A short cadence keeps the
    /// stream responsive; the loop blocks on the delay, so an unchanged state is a cheap spin, not a
    /// busy one. The full notification-hook alternative (LISTEN/NOTIFY) is a later refinement
    /// (ADR-IC-011); polling is the substrate that needs no DB-side trigger.</summary>
    public TimeSpan StreamPollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>A keep-alive comment is emitted on each idle poll so the connection (and any
    /// intermediary) does not consider a long wait — a saga in AWAIT_WORKFLOW_APPROVAL for minutes —
    /// a dead stream (SSE comment line, ADR-IC-006 §P4 "connections stay open for the full saga
    /// duration").</summary>
    public bool EmitKeepAlive { get; init; } = true;

    /// <summary>
    /// The auto-approval ceiling, in integer cents, PINNED onto each saga at start (bd
    /// babelstone-t7o3.1; Document 05 step 3 "€25,000"). The approval fork compares the request's
    /// amount against this scalar — a saga at or below it auto-approves (for an existing client),
    /// above it routes to the external workflow. It is the policy in force at admission, captured
    /// once at the edge and pinned onto the saga's business references so the fork decides
    /// replay-stably; it is NEVER re-dereferenced from live config at decision time (ADR-PC-010 §P5).
    /// Defaults to the Document 05 worked threshold (€25,000 = 2,500,000 cents).
    /// </summary>
    public long AutoApprovalThresholdMinorUnits { get; init; } = 25_000_00;
}
