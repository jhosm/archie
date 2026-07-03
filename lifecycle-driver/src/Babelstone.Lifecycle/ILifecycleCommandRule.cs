namespace Babelstone.Lifecycle;

/// <summary>
/// A family's contribution to the lifecycle-command driver — the per-family rule that reads its forward
/// calendar as-of a date and says which clock-driven lifecycle commands are DUE (ADR-PC-036 §Decision 2 +
/// §Decision 5; the write-side twin of the notification core's <c>INotificationScheduleRule</c>, ADR-IC-019
/// §D1). In plain terms: the generic driver owns the clock, the dedupe ledger and the command-POST sink, but
/// it must NOT know that a term deposit "matures" or that a personal loan owes a monthly "installment". Those
/// family-shaped decisions — <em>which instances are due as-of a date</em>, <em>which engine command endpoint
/// fires them</em>, and <em>with what body</em> — live in a per-family rule that the driver enumerates each tick.
/// </summary>
/// <remarks>
/// The rule is a deterministic function of the as-of date and whatever forward calendar it reads (ADR-PC-023:
/// the projection IS the temporal signal) — the clock lives one layer up, in the <see cref="LifecycleWorker"/>
/// (ADR-PC-023 §6), never inside a rule — so it is trivially testable with a fixed date and no real wall-clock
/// wait. It does NOT own the dispatch-id derivation or the dedupe: those are driver primitives
/// (the <see cref="ILifecycleDispatchLedger"/> claim over <c>LifecycleCommandKey</c>) the pass applies to every decision,
/// so a family rule never reimplements idempotency and may return the same due occurrence on every pass without
/// double-firing (the dispatch ledger + the engine's <c>command_dedup</c> both absorb the repeat).
/// <para>
/// The driver fires recurring occurrence N+1 only when N's de-settled cash leg is healthy
/// (<c>LIFECYCLE_DRIVER_SETTLEMENT_HEALTH_GATE</c>, ADR-PC-036 §Decision 4) — that settlement-health gate is a
/// recurring-rule concern (the family rule consults the <see cref="ISettlementHealthProbe"/> and does not
/// surface N+1 while N is parked), not generic-driver machinery; maturity (one-shot) needs no such gate. The
/// first concrete rules are the sibling bd issues babelstone-6cpq.8 (term-deposit maturity, one-shot) and
/// babelstone-6cpq.9 (personal-loan installment, recurring — the rule carrying the LCD-2 gate,
/// bd babelstone-6cpq.10); this issue stands up the host they plug into.
/// </para>
/// </remarks>
public interface ILifecycleCommandRule
{
    /// <summary>The family this rule drives lifecycle commands for (e.g. <c>"term_deposit"</c>,
    /// <c>"personal_loan"</c>) — for diagnostics and the host's duplicate-family collision check.</summary>
    string FamilyName { get; }

    /// <summary>
    /// Produce the lifecycle commands that are due as-of <paramref name="asOf"/> (supplied by the driver's
    /// clock-owning worker loop — ADR-PC-023 §6, never read inside the rule, so the rule is a deterministic
    /// function of the date). The pass derives each returned decision's number-pinned dispatch id and dedupes
    /// it, so a rule may return the same due occurrence on every pass without re-firing.
    /// </summary>
    /// <param name="asOf">Today, supplied by the clock-owning worker loop.</param>
    /// <param name="ct">Cancellation propagated from the host's stopping token.</param>
    Task<IReadOnlyList<LifecycleCommandDecision>> EvaluateAsync(DateOnly asOf, CancellationToken ct = default);
}

/// <summary>
/// One "this lifecycle command is due" decision a family's <see cref="ILifecycleCommandRule"/> produces
/// (ADR-PC-036 §Decision 2) — BEFORE the driver derives its number-pinned dispatch id, dedupes it, and POSTs
/// it. It carries the three identity parts the canonical key is derived from
/// (<see cref="InstanceId"/>, <see cref="CommandKind"/>, <see cref="OccurrenceKey"/> — ADR-PC-036 §Decision 1+3,
/// LCD-1) plus the concrete HTTP shape the generic sink POSTs (the engine's ADR-PC-029 command endpoint path +
/// the request body). It carries NO PII (ADR-PC-004 §P2): the body is structural references + cents-native
/// money only.
/// </summary>
/// <param name="InstanceId">The aggregate/stream the command mutates (e.g. the loan id or deposit id) — the
/// <c>instance_id</c> part of the canonical idempotency key.</param>
/// <param name="CommandKind">The STABLE command-kind code (e.g. <c>"pay_installment"</c>, <c>"mature_deposit"</c>)
/// — never caller input, the part that separates two command spaces on the same aggregate. It must match the
/// kind the engine endpoint derives its key under (e.g. the loan installment endpoint's <c>pay_installment</c>),
/// so the driver-derived key and the engine-derived key are identical.</param>
/// <param name="OccurrenceKey">The STABLE per-occurrence key — for a recurring installment the installment
/// NUMBER (never the due-date, never caller-supplied), so a re-dated or backfilled retry of occurrence N reuses
/// the same id (ADR-PC-036 §Decision 3, number-pinned). A one-shot lifecycle step (deposit maturity) uses a
/// constant such as <c>1</c>.</param>
/// <param name="RequestPath">The engine's ADR-PC-029 command endpoint to POST, already instance-substituted
/// (e.g. <c>/v1/loans/{id}/installment</c>, <c>/v1/deposits/{id}/maturity</c>) — relative to the engine base
/// URL the host configures.</param>
/// <param name="Body">The POST request body as canonical snake_case JSON fields (the engine API's
/// <c>JsonNamingPolicy.SnakeCaseLower</c> wire shape), money as integer cents, NO PII. Empty when the endpoint
/// needs no body.</param>
/// <param name="DueAt">The occurrence's due date — for diagnostics and the "what fired today?" view. The engine
/// stamps the business <c>valid_time</c> from the command's own date field, so a late firing still records the
/// correct business date (ADR-PC-002 bitemporality).</param>
/// <param name="ServicePrincipalScope">The scoped, gateway-attested SCA service-principal token to present on
/// the money-mover route (ADR-PC-036 §Decision 1 / the <c>X-SCA-Service-Principal</c> non-interactive principal,
/// e.g. <c>lifecycle:deposit-money-mover</c>), or <see langword="null"/> when the route needs none (e.g. the
/// loan installment endpoint, which derives its key server-side and is not SCA-step-up-gated).</param>
public sealed record LifecycleCommandDecision(
    Guid InstanceId,
    string CommandKind,
    long OccurrenceKey,
    string RequestPath,
    IReadOnlyDictionary<string, object?> Body,
    DateOnly DueAt,
    string? ServicePrincipalScope = null);

/// <summary>
/// A <see cref="LifecycleCommandDecision"/> the driver has stamped with its canonical, server-derived,
/// number-pinned command id (ADR-PC-036 §Decision 1+3) and DISPATCHED — i.e. a command actually POSTed to the
/// engine this pass, not a deduped re-tick. The <see cref="CommandId"/> is both the engine
/// <c>Idempotency-Key</c> the sink presented and the dispatch-ledger key the next tick dedupes against — the
/// two are the SAME value, derived the same way the engine derives it, so the driver and the engine converge.
/// </summary>
/// <param name="CommandId">The canonical server-derived idempotency key (= the dispatch-ledger key).</param>
/// <param name="InstanceId">The aggregate/stream the command mutated.</param>
/// <param name="CommandKind">The stable command-kind code.</param>
/// <param name="OccurrenceKey">The stable per-occurrence key (installment number; <c>1</c> for one-shot).</param>
/// <param name="RequestPath">The engine command endpoint POSTed.</param>
/// <param name="DueAt">The occurrence's due date.</param>
public sealed record DispatchedCommand(
    Guid CommandId,
    Guid InstanceId,
    string CommandKind,
    long OccurrenceKey,
    string RequestPath,
    DateOnly DueAt);

/// <summary>
/// The generic command-POST sink the driver effects a due <see cref="LifecycleCommandDecision"/> through
/// (ADR-PC-036 §Decision 2) — the lifecycle-driver mirror of the notification estate's "raise a reminder"
/// emit, except the side effect is an HTTP POST to the engine's ADR-PC-029 command endpoint. In plain terms:
/// once the pass has decided an occurrence is due and not-yet-dispatched, THIS is what actually reaches the
/// engine. It is the ONLY runtime path the driver takes to the engine — the engine stays clockless and is
/// reached solely through its command surface (NO_CLOCK_DRIVEN_ENGINE_SIGNAL holds). The production
/// implementation is <see cref="HttpLifecycleCommandSink"/>; a test substitutes a fake to assert the POST
/// without a live engine.
/// </summary>
public interface ILifecycleCommandSink
{
    /// <summary>
    /// POST the due command to the engine's ADR-PC-029 endpoint, presenting <paramref name="commandId"/> as
    /// the deterministic <c>Idempotency-Key</c> so an at-least-once retry replays the original outcome at the
    /// engine's <c>command_dedup</c> rather than moving money twice (<c>ENGINE_COMMAND_IDEMPOTENT</c>,
    /// ADR-PC-029 slot 4). A non-success engine response throws — the caller treats it as backpressure and the
    /// occurrence is NOT recorded dispatched, so the next pass retries it (the engine dedupes the re-POST).
    /// </summary>
    /// <param name="decision">The due command to fire (path, body, and the scoped SCA principal to present).</param>
    /// <param name="commandId">The canonical server-derived idempotency key for this occurrence.</param>
    /// <param name="ct">Cancellation propagated from the host's stopping token.</param>
    Task DispatchAsync(LifecycleCommandDecision decision, Guid commandId, CancellationToken ct = default);
}
