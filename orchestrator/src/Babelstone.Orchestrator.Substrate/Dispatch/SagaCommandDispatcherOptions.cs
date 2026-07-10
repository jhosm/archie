namespace Babelstone.Orchestrator.Dispatch;

/// <summary>
/// Configuration for the saga command DISPATCHER (bd babelstone-t7o3.3, ADR-PC-029). The dispatcher
/// drains <c>saga_outbox</c> and delivers each decided command to its target over idempotent HTTP.
/// Everything else (the Idempotency-Key, the traceparent, the body) it derives from the outbox row
/// itself — the same "the row IS the message" discipline the engine's outbox relay follows.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two configurable targets, one routing seam.</b> An engine-bound command (ActivateDeposit) goes
/// to the engine's command surface (<see cref="EngineBaseUrl"/>, the ADR-PC-029 / ADR-PC-027 HTTP
/// boundary); the settlement commands (ReserveAccountBalance / ConfirmDebit / … ) go to the Core ACL
/// (<see cref="SettlementBaseUrl"/>), which at v1 is a WireMock stub (the real ACL is DEF-1, bd
/// ub9s). Both are service ENDPOINTS, not credentials, so they resolve straight from configuration —
/// distinct from the runtime DB credential, which goes through the ADR-PC-004 Amendment A1 boundary.
/// </para>
/// <para>
/// <b>No PII (ADR-PC-004 §P2).</b> Every field is a base URL or a tuning knob — never a NIF/IBAN/
/// name/amount, and never a credential that could ride a message (ADR-IC-003 §P7).
/// </para>
/// </remarks>
public sealed record SagaCommandDispatcherOptions
{
    /// <summary>PostgreSQL connection string for the orchestrator application database (the
    /// <c>saga_outbox</c> table the dispatcher drains and flips).</summary>
    public required string ConnectionString { get; init; }

    /// <summary>The engine command surface base URL (e.g. "http://engine:8080"), no trailing slash
    /// required. The dispatcher appends the per-command route (ActivateDeposit → <c>/v1/deposits</c>).</summary>
    public required string EngineBaseUrl { get; init; }

    /// <summary>The Core-ACL / settlement base URL the settlement commands target (a WireMock stub at
    /// v1; the real ACL is DEF-1, bd ub9s). Configurable so v1 can point it at the stub and a later
    /// deploy at the real adapter without a code change. This is the LEGACY-DDA counterparty
    /// (<c>ce_settlementtarget = legacy-dda</c>, the default), and the target for any leg with no promoted
    /// settlement-target header (ADR-PC-043 — legacy routing UNCHANGED).</summary>
    public required string SettlementBaseUrl { get; init; }

    /// <summary>The engine-OWNED current-account settlement base URL — the ADR-PC-043 engine-CA counterparty a
    /// leg is routed to when its promoted <c>ce_settlementtarget</c> header is <c>engine-ca</c>. Optional: when
    /// null (or blank) no leg is engine-CA-routed and every settlement command stays on
    /// <see cref="SettlementBaseUrl"/> — so an estate that has not stood up the engine-CA surface keeps the
    /// pre-ADR-PC-043 behaviour with no config change. The engine's own command surface (ADR-PC-029), which the
    /// settlement-facing CA <c>/capture</c> and <c>/credit</c> endpoints live behind.</summary>
    public string? EngineCaSettlementBaseUrl { get; init; }

    /// <summary>Max rows drained per poll cycle (the PENDING tail, ORDER BY seq).</summary>
    public int BatchSize { get; init; } = 256;

    /// <summary>Poll interval for the hosted background loop (mirrors the engine relay's default).</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Per-request HTTP timeout. A timeout is treated as a TRANSIENT failure (ADR-PC-029
    /// slot 5): the row stays PENDING and the loop retries — idempotency makes the retry safe.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);
}
