using Babelstone.EventStore;

namespace Babelstone.Engine;

/// <summary>
/// An advanceable wall-clock seam for the simulation forward-lifecycle (A.8b). It is a real
/// <see cref="TimeProvider"/> — so it slots into the SAME injected-clock seam the live runtime uses
/// (<see cref="AggregateRuntime{TState}"/> takes a <see cref="TimeProvider"/> to stamp
/// transaction_time) — but its "now" is settable, so a simulation can FAST-FORWARD it through a
/// deposit's future milestones. The clock stays an injected seam of the impure shell, never reachable
/// from a handler (ADR-PC-010): a handler is a pure fold and never reads a clock; the simulation
/// advances THIS clock and the real lifecycle commands run against it.
/// </summary>
/// <remarks>
/// Monotonic by contract: <see cref="AdvanceTo"/> only ever moves "now" FORWARD (a lifecycle runs
/// forward in time), so fast-forwarding through an ordered milestone schedule never rewinds the clock
/// under a step that already ran. Constructed at the simulation's start instant (typically the
/// deposit's constitution moment) and handed to BOTH the runtime that stamps events and this
/// simulation, so the two share one notion of "now".
/// </remarks>
public sealed class SimulationClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    /// <summary>
    /// Fast-forwards "now" to <paramref name="instant"/>. Moves forward only — an instant at or
    /// before the current "now" is a no-op (the clock never rewinds), so an ordered milestone walk is
    /// safe even when two milestones share an instant.
    /// </summary>
    public void AdvanceTo(DateTimeOffset instant)
    {
        if (instant > _now)
        {
            _now = instant;
        }
    }
}

/// <summary>
/// One scheduled milestone in a deposit's forward lifecycle (A.8b): the instant it falls DUE and the
/// REAL lifecycle step to run when the simulation's clock reaches it. The step is a family-supplied
/// closure over a real lifecycle command (e.g. the term-deposit family's <c>PayInterestAsync</c> /
/// <c>MatureAsync</c>) — so advancing the clock GENERATES the milestone's events through the real
/// deciders and pure handlers, never by hand-faking events.
/// </summary>
/// <remarks>
/// <para>
/// The schedule is supplied by the FAMILY, which alone knows what a deposit's milestones are (coupon
/// boundaries, month-ends, maturity) and how to fire them. The engine spine stays family-agnostic
/// (ENGINE_FAMILY_AGNOSTIC, ADR-PC-021): <see cref="SimulationRuntime{TState}"/> only walks an
/// ordered list of <c>(DueAt, Step)</c> pairs and fast-forwards the clock between them — it names no
/// coupon, no maturity, no family type.
/// </para>
/// <para>
/// <b>The forecast is a fitness function (ADR-PC-036 §Decision 7).</b> A milestone that corresponds to a
/// PRODUCTION clock-driven lifecycle command (a deposit maturity, a loan installment — the occurrences the
/// downstream lifecycle-command driver fires) MAY carry that command's identity in
/// <see cref="CommandKind"/> / <see cref="OccurrenceKey"/> — the same <c>(command_kind,
/// stable_occurrence_key)</c> pair the driver's canonical number-pinned idempotency key is derived from
/// (LCD-1). The family builds such milestones from its ONE shared dispatch mapping (the same mapping its
/// production driver rule consumes), so a forecast milestone and the production command for the same
/// occurrence cannot silently diverge — the per-family <c>LifecycleDispatchFitnessTests</c> compare the
/// two and fail on drift. The identity is
/// OPTIONAL and family-agnostic (a plain kind string + occurrence number): a milestone with no production
/// driver counterpart (e.g. a coupon the driver does not yet fire) carries <see langword="null"/>s, and the
/// runtime itself never reads either — it still only walks due instants and fires steps.
/// </para>
/// </remarks>
/// <param name="DueAt">The instant the milestone falls due — the clock is advanced to here before the
/// step runs, and the step stamps its event's valid time from this same instant.</param>
/// <param name="Step">The real lifecycle command to run at <paramref name="DueAt"/>. Receives the
/// due instant so it can stamp the command's timestamp from the advanced clock, not a separate read.</param>
/// <param name="CommandKind">The STABLE production command-kind this milestone forecasts (e.g.
/// <c>"mature"</c>, <c>"pay_installment"</c>) — the kind half of the driver's number-pinned occurrence
/// identity (ADR-PC-036 §Decision 7) — or <see langword="null"/> for a milestone with no production
/// driver counterpart.</param>
/// <param name="OccurrenceKey">The STABLE per-occurrence key this milestone forecasts (the installment
/// NUMBER; <c>1</c> for a one-shot maturity) — or <see langword="null"/> when <paramref name="CommandKind"/>
/// is null.</param>
public sealed record LifecycleMilestone(
    DateTimeOffset DueAt,
    Func<DateTimeOffset, CancellationToken, Task> Step,
    string? CommandKind = null,
    long? OccurrenceKey = null);

/// <summary>
/// Runs the pure engine core (dispatch + fold) for forward projection (capability #4)
/// and counterfactual replay (#3) WITHOUT side effects (A.8). Side-effect-freedom is
/// structural, not a flag: this type has no <see cref="IEventSink"/>, no
/// <see cref="IPiiProtector"/>, no snapshot store as constructor dependencies — so it
/// physically cannot write the log/outbox, mint OpenBao material, or persist a snapshot.
/// </summary>
/// <remarks>
/// <para>
/// Rehydration reads the durable log read-only (A.3) and folds <em>structural</em> state;
/// it deliberately does NOT decrypt PII — state transitions run on structural fields
/// (ADR-PC-004: PII is off the structural hot path), so a simulation never needs to
/// reach OpenBao. Counterfactual inputs (pack version, rate-sheet, clock) flow in per
/// invocation.
/// </para>
/// <para>
/// <b>Forward lifecycle by clock-advance (A.8b, ADR-PC-011 clock-advance pattern).</b>
/// <see cref="RunForwardLifecycleAsync"/> generates a deposit's FUTURE life — daily accrual,
/// month-end, maturity — by fast-forwarding an INJECTED <see cref="SimulationClock"/> through the
/// REAL lifecycle handlers, instead of hand-faking events. This is the auto-firing time scheduler the
/// E.3 command surface (PayInterest/Mature) deferred to A.8b: each milestone runs the real
/// decider→append path against the advanced clock, so the produced stream is byte-identical to one a
/// live deposit running over real time would produce, and cold-replays to the same terminal state.
/// </para>
/// </remarks>
public sealed class SimulationRuntime<TState>(
    IEventStore store,
    HandlerRegistry handlers,
    IEventSerializer serializer,
    Func<TState> seedState)
{
    /// <summary>Folds a stream's committed history (read-only), then the supplied hypothetical events, into projected state.</summary>
    public async Task<TState> ProjectAsync(
        Guid streamId, IReadOnlyList<DomainEvent> hypotheticalEvents, CancellationToken ct = default)
    {
        var state = seedState();

        await foreach (var envelope in store.LoadAsync(streamId, fromSequence: 0, ct))
        {
            if (!handlers.TryResolveByEventType(envelope.EventType, out var registration))
            {
                throw new InvalidOperationException($"No handler registered for event type '{envelope.EventType}'.");
            }

            // Decode but do NOT unprotect: folding is structural; PII stays sealed.
            var @event = serializer.Decode(envelope.Payload, registration.PayloadType);
            state = (TState)registration.Handler.ApplyBoxed(state!, @event).NewState;
        }

        return Fold(state, hypotheticalEvents);
    }

    /// <summary>Pure forward projection from the seed state over a sequence of hypothetical events — no I/O at all.</summary>
    public TState ProjectFromScratch(IReadOnlyList<DomainEvent> events) => Fold(seedState(), events);

    /// <summary>
    /// Generate a deposit's forward lifecycle by fast-forwarding an INJECTED clock through the REAL
    /// lifecycle handlers (A.8b, ADR-PC-011 clock-advance pattern). Walks the family-supplied
    /// <paramref name="milestones"/> in due-instant order; for each one it advances
    /// <paramref name="clock"/> to the milestone's <see cref="LifecycleMilestone.DueAt"/> and then runs
    /// the milestone's real lifecycle <see cref="LifecycleMilestone.Step"/> at that instant. Because the
    /// runtime that appends the milestone's events reads the SAME injected <paramref name="clock"/> to
    /// stamp transaction_time, advancing the clock first is what makes each event land at its scheduled
    /// moment — the auto-firing time scheduler the explicit command surface deferred to A.8b.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Real handlers, never hand-faked events.</b> Each step is a real lifecycle command (the
    /// family's <c>PayInterestAsync</c> / <c>MatureAsync</c>, etc.), so the events are produced by the
    /// real deciders and folded by the pure handlers. The simulation contributes ONLY the clock advance
    /// and the firing ORDER — it never constructs a domain event itself. A clock-advanced run therefore
    /// produces the SAME stream a live deposit ticking over real time would, and cold-replays
    /// (ProjectAsync) to the identical terminal state — replay-determinism by construction
    /// (ADR-PC-010): the clock is the impure shell's, every date the step stamps is event-captured,
    /// and a rebuild never re-reads a clock.
    /// </para>
    /// <para>
    /// <b>Family-agnostic.</b> This method names no coupon, no maturity, no family type — it only
    /// fast-forwards a clock through an ordered list of due instants and fires the caller's steps. The
    /// FAMILY builds the schedule (it alone knows the milestone dates and the commands), so the spine
    /// stays family-agnostic (ENGINE_FAMILY_AGNOSTIC, ADR-PC-021).
    /// </para>
    /// <para>
    /// <b>Not structurally side-effect-free.</b> Unlike <see cref="ProjectAsync"/> /
    /// <see cref="ProjectFromScratch"/>, this DOES persist — it drives the real append path through the
    /// family steps, materialising a real stream. It is the engine's generator of a deposit's life, not
    /// a read-only what-if; the side-effect-free projection methods sit alongside it for counterfactuals.
    /// </para>
    /// </remarks>
    /// <param name="clock">The injected, advanceable clock the live runtime ALSO reads — fast-forwarded
    /// to each milestone's due instant so the milestone's events stamp at their scheduled moment.</param>
    /// <param name="milestones">The family-supplied lifecycle milestones. Run in ascending
    /// <see cref="LifecycleMilestone.DueAt"/> order regardless of the order supplied, so a caller may
    /// hand them in any order.</param>
    public async Task RunForwardLifecycleAsync(
        SimulationClock clock, IReadOnlyList<LifecycleMilestone> milestones, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(milestones);

        // Fire in due-instant order so the clock only ever moves forward and the produced stream's
        // commit order matches real chronological order. OrderBy is a STABLE sort, so milestones that
        // share an instant keep their supplied relative order (e.g. a same-day coupon-then-maturity).
        foreach (var milestone in milestones.OrderBy(m => m.DueAt))
        {
            ct.ThrowIfCancellationRequested();
            clock.AdvanceTo(milestone.DueAt);
            await milestone.Step(milestone.DueAt, ct);
        }
    }

    private TState Fold(TState state, IReadOnlyList<DomainEvent> events)
    {
        foreach (var @event in events)
        {
            if (!handlers.TryResolveByPayloadType(@event.GetType(), out var registration))
            {
                throw new InvalidOperationException($"No handler registered for event payload type '{@event.GetType()}'.");
            }

            state = (TState)registration.Handler.ApplyBoxed(state!, @event).NewState;
        }

        return state;
    }
}
