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
/// Most are STORE-ONLY by construction (ADR-IC-017 §P1/§P4): appended, folded, and replayable, but
/// carrying NO governed <c>.avsc</c> and so never reaching the durable bus — there is no NAMED external
/// consumer that must react (ADR-PC-009 §P3's only downstream reaction is engine-internal projection
/// rebuild). The fail-closed catalog gate (<c>AvroSchemaCatalog.IsCataloguedIntegrationEvent</c>) keeps
/// an uncatalogued event store-only; the store payload is the self-describing JSON book of record
/// (ADR-PC-028). Promotion to the bus is authoring the <c>.avsc</c> + AsyncAPI entry and recording the
/// consumer — exactly what <see cref="PersonalDataErasureRequested"/> does (ADR-PC-004 Amendment A4): a
/// cross-cutting event with NAMED downstream consumers (acl cascades the deletion, notification
/// suppresses further messaging), so it carries a governed <c>operations.PersonalDataErasureRequested</c>
/// schema and rides the bus, while still being engine-declared and folded per family through the same
/// <see cref="CrossCuttingEventRegistrations.For{TState}"/> seam.
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
/// A family projection state that knows how to label itself ERASED. In plain English: GDPR erasure is
/// the same cross-cutting fact for every product — "this subject's PII key was crypto-shredded" — but
/// each family records the terminal state on its OWN lifecycle enum (a deposit becomes
/// <c>DepositLifecycle.Erased</c>, a loan <c>LoanLifecycle.Erased</c>). This is the seam that lets the
/// engine own ONE generic erasure fold while each family supplies only its own terminal transition: the
/// engine names no family (ADR-PC-021 §P2), the family implements <see cref="WithErased"/> on its
/// projection record.
/// </summary>
/// <typeparam name="TState">The family's folded projection state (self-referential, F-bounded).</typeparam>
public interface IErasable<out TState>
{
    /// <summary>Return this state relabelled to its family's terminal <c>Erased</c> lifecycle. A PURE
    /// transformation — no clock, no I/O (BENG001/002/003); it touches only the lifecycle label, never
    /// the structural fields (which stay queryable post-erasure; the PII lived behind the OpenBao key,
    /// ADR-PC-004 §P3).</summary>
    TState WithErased();
}

/// <summary>
/// The GDPR Article 17 right-to-be-forgotten was exercised on an instance (ADR-PC-004 §P3 / Amendment
/// A4). In plain English: a data subject asked to be forgotten, the engine destroyed their per-subject
/// encryption key, and this fact records the act on every instance that held their PII so the audit
/// trail survives while the data does not. It is a CROSS-CUTTING event — the same structural fact for
/// any product family — so the engine declares it ONCE in the spine (it names no family, ADR-PC-021 §P2)
/// rather than each family re-deriving it. Each family BINDS the generic fold against its own projection
/// state via <see cref="CrossCuttingEventRegistrations.For{TState}"/>.
/// </summary>
/// <remarks>
/// <para>
/// Cross-cutting, but NOT store-only: unlike <see cref="PackVersionMigrated"/>, erasure has NAMED
/// external consumers (acl cascades downstream deletion, notification suppresses further messaging), so
/// it is a promoted integration event (ADR-IC-017 §P4) carrying the governed
/// <c>contracts/avro/operations/PersonalDataErasureRequested.avsc</c> (subject
/// <c>operations.PersonalDataErasureRequested-value</c>) and the matching AsyncAPI catalogue entry. The
/// synthetic <c>operations</c> aggregate_type keeps the stored <c>event_type</c>
/// <c>operations.PersonalDataErasureRequested</c> family-agnostic (event-store §4.3).
/// </para>
/// <para>
/// NO PII rides this event (ADR-PC-004 §P2): <paramref name="SubjectPseudonym"/> is a SALTED one-way
/// hash of the subject id (ADR-IC-016 §8), an opaque correlation reference a consumer can cascade its
/// own deletion on — never the raw subject id. The crypto-shred itself (<c>IPiiKeyStore.DestroyKeyAsync</c>)
/// is performed by the impure command shell BEFORE this event is appended; the fold only LABELS the
/// instance erased.
/// </para>
/// </remarks>
/// <param name="InstanceId">The instance (stream) whose subject's PII was erased — a structural id (the
/// aggregate id, e.g. a DepositId or LoanId), not PII.</param>
/// <param name="SubjectPseudonym">A salted one-way hash of the data-subject id (ADR-IC-016 §8 / ADR-PC-004
/// §P2) — an opaque correlation reference, NEVER the raw subject id.</param>
/// <param name="ErasedOn">The date the erasure took effect (audit lineage) — supplied by the command, not
/// read from a clock in the fold.</param>
/// <param name="ErasureReason">Stable machine code for why erasure happened (e.g. <c>GDPR_ARTICLE_17</c>) — never PII.</param>
public sealed record PersonalDataErasureRequested(
    Guid InstanceId,
    string SubjectPseudonym,
    DateOnly ErasedOn,
    string ErasureReason) : DomainEvent;

/// <summary>
/// The pure fold for <see cref="PersonalDataErasureRequested"/>, generic over ANY family projection
/// <typeparamref name="TState"/> that is <see cref="IErasable{TState}"/> so it stays FAMILY-AGNOSTIC
/// (ADR-PC-021 §P2): the engine owns this handler; a family BINDS it against its own state in its module
/// (via <see cref="CrossCuttingEventRegistrations.For{TState}"/>). The fold delegates the terminal
/// transition to <see cref="IErasable{TState}.WithErased"/> — the engine knows "mark it erased", the
/// family knows what "erased" means on its own lifecycle. Pure (no clock, no I/O, no randomness,
/// BENG001/002/003) — replay is deterministic.
/// </summary>
public sealed class PersonalDataErasureRequestedHandler<TState> : IEventHandler<TState, PersonalDataErasureRequested>
    where TState : IErasable<TState>
{
    public HandlerResult<TState> Apply(TState state, PersonalDataErasureRequested @event)
        => HandlerResult<TState>.From(state.WithErased());
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
    /// <typeparam name="TState">The family's folded projection state the generic folds run over; it must
    /// be <see cref="IErasable{TState}"/> so the generic erasure fold can mark it erased.</typeparam>
    public static IReadOnlyList<HandlerRegistration> For<TState>() where TState : IErasable<TState> =>
    [
        new("operations.PackVersionMigrated", typeof(PackVersionMigrated),
            new DispatchableHandler<TState, PackVersionMigrated>(new PackVersionMigratedHandler<TState>())),
        new("operations.PersonalDataErasureRequested", typeof(PersonalDataErasureRequested),
            new DispatchableHandler<TState, PersonalDataErasureRequested>(new PersonalDataErasureRequestedHandler<TState>())),
    ];
}
