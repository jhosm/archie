namespace Babelstone.Engine;

/// <summary>
/// The engine-declared cross-cutting operational events (feature-design event-store §4.1). In plain
/// English: a handful of facts apply to ANY instance regardless of product family — a regulator forcing
/// a pack change, the engine evolving its own schemas, a court freezing an account. The engine declares
/// these and owns their handler runtime; family schemas do not (§4.1). They live in the spine so they
/// stay FAMILY-AGNOSTIC (ADR-PC-021 §P2): the record and its pure fold name no family, and a family
/// only supplies the concrete projection <c>TState</c> the fold runs over when it BINDS the handler in
/// its module.
/// </summary>
/// <remarks>
/// <para>
/// These five engine-declared events (only <see cref="PackVersionMigrated"/> is built so far;
/// <c>SchemaVersionMigrated</c>, <c>LegacyInstanceObserved</c>, <c>FundsHeld</c>, <c>AccountFrozen</c>
/// follow the same pattern) take a synthetic <c>operations</c> aggregate_type, so their stored
/// <c>event_type</c> is <c>operations.&lt;EventName&gt;</c> (no family prefix — they are
/// family-agnostic). The convention is recorded set-level in event-store §4.3.
/// </para>
/// <para>
/// STORE-ONLY by construction (ADR-IC-017 §P1/§P4): they are appended, folded, and replayable, but
/// carry NO governed <c>.avsc</c> and so never reach the durable bus — there is no NAMED external
/// consumer that must react (ADR-PC-009 §P3's only downstream reaction is engine-internal projection
/// rebuild). The fail-closed catalog gate (<c>AvroSchemaCatalog.IsCataloguedIntegrationEvent</c>) keeps
/// an uncatalogued event store-only; the store payload is the self-describing JSON book of record
/// (ADR-PC-028). If a downstream consumer ever states a need, promotion is authoring the <c>.avsc</c> +
/// AsyncAPI entry and recording the consumer — not done here.
/// </para>
/// </remarks>

/// <summary>
/// An operator re-pinned a live instance to a newer regulatory pack version (ADR-PC-009 §P3). In plain
/// English: a deposit is locked for life to the pack it was opened under; the ONLY sanctioned way to
/// move it to a newer pack — when a regulator forces a retroactive change — is this explicit, audited,
/// operator-driven migration. The engine emits one of these per affected instance.
/// </summary>
/// <remarks>
/// The re-pin itself is carried by the EVENT ENVELOPE, not this payload: the migration event and every
/// event appended after it carry <c>pack_version = to_pack_version</c> on the envelope, while history
/// before it stays pinned to <c>from_pack_version</c> (ADR-PC-009 §P1/§P3 — the pin is a per-event fact,
/// and the migration boundary is intrinsic to the stream). So the projection fold is a NO-OP (the
/// position record has no pack-version field; writing one would re-introduce the rejected
/// projection-column pin, ADR-PC-009 §B). This payload records the AUDIT facts — what moved, by whom,
/// under which migration — so "which instances were re-pinned, when, by whom" is answerable from the
/// stream (surface §3.6; DORA/PSD2 auditability).
/// <para>
/// STRUCTURAL only, never PII (ADR-PC-004 §P2): version strings and a migration id are opaque
/// operational identifiers, and <paramref name="OperatorActor"/> is an OPERATOR/service identity — a
/// back-office actor reference, never a data subject's personal data (ADR-PC-009 §F2: "a pin is two
/// short version strings — no PII"). <paramref name="MigrationId"/> is the idempotency / dedupe key:
/// re-issuing a migration for an already-migrated instance is a no-op (ADR-PC-009 §P3).
/// </para>
/// </remarks>
/// <param name="InstanceId">The instance (stream) this migration re-pins — a structural id, not PII.</param>
/// <param name="FromPackVersion">The pack version the instance was pinned to before this event (e.g. <c>pt.2026.1</c>).</param>
/// <param name="ToPackVersion">The pack version the instance is pinned to from this event's sequence forward (e.g. <c>pt.2027.1</c>).</param>
/// <param name="MigrationId">The operator migration's id — the audit handle AND the idempotency dedupe key (ADR-PC-009 §P3).</param>
/// <param name="OperatorActor">The operator/service actor that initiated the migration — an operator identity reference, never PII (ADR-PC-004 §P2).</param>
public sealed record PackVersionMigrated(
    Guid InstanceId,
    string FromPackVersion,
    string ToPackVersion,
    string MigrationId,
    string OperatorActor) : DomainEvent
{
    /// <summary>
    /// A pack migration is a snapshot lifecycle boundary (ADR-PC-003 §P2 / event-store §8.1): the
    /// re-pin is a natural point where the instance's state — now governed by a new pack from here
    /// forward — is interpretable on its own, so a snapshot fires here regardless of the per-N count.
    /// A pure structural property of the event TYPE (no clock, no I/O), so the engine stays
    /// family-agnostic when it ORs this into the per-append boundary signal.
    /// </summary>
    public override bool IsLifecycleBoundary => true;
}

/// <summary>
/// The pure fold for <see cref="PackVersionMigrated"/>, generic over ANY family projection
/// <typeparamref name="TState"/> so it stays FAMILY-AGNOSTIC (ADR-PC-021 §P2): the engine owns this
/// handler; a family BINDS it against its own state in its module (e.g.
/// <c>new DispatchableHandler&lt;DepositPosition, PackVersionMigrated&gt;(new PackVersionMigratedHandler&lt;DepositPosition&gt;())</c>).
/// </summary>
/// <remarks>
/// The fold returns the state UNCHANGED. This is the conformant shape, not an omission: the pin lives on
/// the EVENT ENVELOPE (<c>pack_version</c>), and re-pinning is achieved by the append stamping
/// <c>to_pack_version</c> onto this event's envelope and every later event copying it forward (ADR-PC-009
/// §P1/§P3). Writing the new pin onto the projection would re-introduce the explicitly-rejected
/// projection-column pin (ADR-PC-009 §B/§Decision), which cannot represent the pre/post-migration split.
/// Pure — no clock, no I/O, no randomness (BENG001/002/003) — so replay is deterministic: a migrated
/// instance re-pins to the same per-event boundary on every rebuild (REPLAY_PIN_PER_EVENT).
/// </remarks>
public sealed class PackVersionMigratedHandler<TState> : IEventHandler<TState, PackVersionMigrated>
{
    public HandlerResult<TState> Apply(TState state, PackVersionMigrated @event)
        => HandlerResult<TState>.From(state);
}

/// <summary>
/// The engine-declared cross-cutting event bindings, yielded for a given family projection state. In
/// plain English: the engine owns these family-agnostic operational events (§4.1), but the handler
/// registry needs each one bound to a concrete projection state — so a family calls
/// <see cref="For{TState}"/> and splices the result into its own <see cref="IFamilyModule.Handlers"/>
/// rather than hand-rolling the binding (and risking forgetting one). The engine owns the event RECORD
/// + the generic HANDLER; the family supplies only its state type — the <c>family → engine</c> arrow
/// stays one-way (ADR-PC-021 §P2).
/// </summary>
/// <remarks>
/// Each stored <c>event_type</c> is <c>operations.&lt;EventName&gt;</c> — the synthetic
/// <c>operations</c> aggregate_type for the engine-declared cross-cutting set, no family prefix
/// (event-store §4.3). The set grows as the other four §4.1 events are built; a family that splices
/// this in gets them all without changing its module again.
/// </remarks>
public static class CrossCuttingEventRegistrations
{
    /// <summary>
    /// The cross-cutting <see cref="HandlerRegistration"/>s for a family whose projection state is
    /// <typeparamref name="TState"/>. Splice into the family module's <c>Handlers</c> list.
    /// </summary>
    /// <typeparam name="TState">The family's folded projection state the generic folds run over.</typeparam>
    public static IReadOnlyList<HandlerRegistration> For<TState>() =>
    [
        new("operations.PackVersionMigrated", typeof(PackVersionMigrated),
            new DispatchableHandler<TState, PackVersionMigrated>(new PackVersionMigratedHandler<TState>())),
    ];
}
