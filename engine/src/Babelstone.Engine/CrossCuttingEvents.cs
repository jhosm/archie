using Babelstone.FinancialTypes;

namespace Babelstone.Engine;

/// <summary>
/// The engine-declared cross-cutting operational events (feature-design event-store §4.1). In plain
/// English: a handful of facts apply to ANY instance regardless of product family — a regulator forcing
/// a pack change, the engine evolving its own schemas, a court freezing an account. The engine declares
/// these and owns their handler runtime; family schemas do not (§4.1). They live in the spine so they
/// stay FAMILY-AGNOSTIC (ADR-PC-021): the record and its pure fold name no family, and a family
/// only supplies the concrete projection <c>TState</c> the fold runs over when it BINDS the handler in
/// its module.
/// </summary>
/// <remarks>
/// <para>
/// These engine-declared events — <see cref="PackVersionMigrated"/>, <see cref="SchemaVersionMigrated"/>,
/// <see cref="FundsHeld"/>, <see cref="AccountFrozen"/> (and the promoted
/// <see cref="PersonalDataErasureRequested"/>), plus the ADR-PC-033 hold lifecycle
/// (<see cref="HoldPlaced"/> / <see cref="HoldCaptured"/> / <see cref="HoldExpired"/>, declared in
/// <c>AccountHoldEvents.cs</c>) — take a synthetic <c>operations</c> aggregate_type, so
/// their stored <c>event_type</c> is <c>operations.&lt;EventName&gt;</c> (no family prefix — they are
/// family-agnostic). The convention is recorded set-level in event-store §4.3. The fifth §4.1 event,
/// <c>LegacyInstanceObserved</c> (the legacy batch-ingest fact, ADR-PC-017), is deferred to the DEF-1
/// epic and not built here.
/// </para>
/// <para>
/// Most are STORE-ONLY by construction (ADR-IC-017): appended, folded, and replayable, but
/// carrying NO governed <c>.avsc</c> and so never reaching the durable bus — there is no NAMED external
/// consumer that must react (ADR-PC-009's only downstream reaction is engine-internal projection
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
/// An operator re-pinned a live instance to a newer regulatory pack version (ADR-PC-009). In plain
/// English: a deposit is locked for life to the pack it was opened under; the ONLY sanctioned way to
/// move it to a newer pack — when a regulator forces a retroactive change — is this explicit, audited,
/// operator-driven migration. The engine emits one of these per affected instance.
/// </summary>
/// <remarks>
/// The re-pin itself is carried by the EVENT ENVELOPE, not this payload: the migration event and every
/// event appended after it carry <c>pack_version = to_pack_version</c> on the envelope, while history
/// before it stays pinned to <c>from_pack_version</c> (ADR-PC-009 — the pin is a per-event fact,
/// and the migration boundary is intrinsic to the stream). So the projection fold is a NO-OP (the
/// position record has no pack-version field; writing one would re-introduce the rejected
/// projection-column pin, ADR-PC-009). This payload records the AUDIT facts — what moved, by whom,
/// under which migration — so "which instances were re-pinned, when, by whom" is answerable from the
/// stream (surface §3.6; DORA/PSD2 auditability).
/// <para>
/// STRUCTURAL only, never PII (ADR-PC-004): version strings and a migration id are opaque
/// operational identifiers, and <paramref name="OperatorActor"/> is an OPERATOR/service identity — a
/// back-office actor reference, never a data subject's personal data (ADR-PC-009: "a pin is two
/// short version strings — no PII"). <paramref name="MigrationId"/> is the idempotency / dedupe key:
/// re-issuing a migration for an already-migrated instance is a no-op (ADR-PC-009).
/// </para>
/// </remarks>
/// <param name="InstanceId">The instance (stream) this migration re-pins — a structural id, not PII.</param>
/// <param name="FromPackVersion">The pack version the instance was pinned to before this event (e.g. <c>pt.2026.1</c>).</param>
/// <param name="ToPackVersion">The pack version the instance is pinned to from this event's sequence forward (e.g. <c>pt.2027.1</c>).</param>
/// <param name="MigrationId">The operator migration's id — the audit handle AND the idempotency dedupe key (ADR-PC-009).</param>
/// <param name="OperatorActor">The operator/service actor that initiated the migration — an operator identity reference, never PII (ADR-PC-004).</param>
public sealed record PackVersionMigrated(
    Guid InstanceId,
    string FromPackVersion,
    string ToPackVersion,
    string MigrationId,
    string OperatorActor) : DomainEvent
{
    /// <summary>
    /// A pack migration is a snapshot lifecycle boundary (ADR-PC-003 / event-store §8.1): the
    /// re-pin is a natural point where the instance's state — now governed by a new pack from here
    /// forward — is interpretable on its own, so a snapshot fires here regardless of the per-N count.
    /// A pure structural property of the event TYPE (no clock, no I/O), so the engine stays
    /// family-agnostic when it ORs this into the per-append boundary signal.
    /// </summary>
    public override bool IsLifecycleBoundary => true;
}

/// <summary>
/// The pure fold for <see cref="PackVersionMigrated"/>, generic over ANY family projection
/// <typeparamref name="TState"/> so it stays FAMILY-AGNOSTIC (ADR-PC-021): the engine owns this
/// handler; a family BINDS it against its own state in its module (e.g.
/// <c>new DispatchableHandler&lt;DepositPosition, PackVersionMigrated&gt;(new PackVersionMigratedHandler&lt;DepositPosition&gt;())</c>).
/// </summary>
/// <remarks>
/// The fold returns the state UNCHANGED. This is the conformant shape, not an omission: the pin lives on
/// the EVENT ENVELOPE (<c>pack_version</c>), and re-pinning is achieved by the append stamping
/// <c>to_pack_version</c> onto this event's envelope and every later event copying it forward (ADR-PC-009
/// §P1/§P3). Writing the new pin onto the projection would re-introduce the explicitly-rejected
/// projection-column pin (ADR-PC-009), which cannot represent the pre/post-migration split.
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
/// engine names no family (ADR-PC-021), the family implements <see cref="WithErased"/> on its
/// projection record.
/// </summary>
/// <typeparam name="TState">The family's folded projection state (self-referential, F-bounded).</typeparam>
public interface IErasable<out TState>
{
    /// <summary>Return this state relabelled to its family's terminal <c>Erased</c> lifecycle. A PURE
    /// transformation — no clock, no I/O (BENG001/002/003); it touches only the lifecycle label, never
    /// the structural fields (which stay queryable post-erasure; the PII lived behind the OpenBao key,
    /// ADR-PC-004).</summary>
    TState WithErased();
}

/// <summary>
/// The GDPR Article 17 right-to-be-forgotten was exercised on an instance (ADR-PC-004 / Amendment
/// A4). In plain English: a data subject asked to be forgotten, the engine destroyed their per-subject
/// encryption key, and this fact records the act on every instance that held their PII so the audit
/// trail survives while the data does not. It is a CROSS-CUTTING event — the same structural fact for
/// any product family — so the engine declares it ONCE in the spine (it names no family, ADR-PC-021)
/// rather than each family re-deriving it. Each family BINDS the generic fold against its own projection
/// state via <see cref="CrossCuttingEventRegistrations.For{TState}"/>.
/// </summary>
/// <remarks>
/// <para>
/// Cross-cutting, but NOT store-only: unlike <see cref="PackVersionMigrated"/>, erasure has NAMED
/// external consumers (acl cascades downstream deletion, notification suppresses further messaging), so
/// it is a promoted integration event (ADR-IC-017) carrying the governed
/// <c>contracts/avro/operations/PersonalDataErasureRequested.avsc</c> (subject
/// <c>operations.PersonalDataErasureRequested-value</c>) and the matching AsyncAPI catalogue entry. The
/// synthetic <c>operations</c> aggregate_type keeps the stored <c>event_type</c>
/// <c>operations.PersonalDataErasureRequested</c> family-agnostic (event-store §4.3).
/// </para>
/// <para>
/// NO PII rides this event (ADR-PC-004): <paramref name="SubjectPseudonym"/> is a SALTED one-way
/// hash of the subject id (ADR-IC-016), an opaque correlation reference a consumer can cascade its
/// own deletion on — never the raw subject id. The crypto-shred itself (<c>IPiiKeyStore.DestroyKeyAsync</c>)
/// is performed by the impure command shell BEFORE this event is appended; the fold only LABELS the
/// instance erased.
/// </para>
/// </remarks>
/// <param name="InstanceId">The instance (stream) whose subject's PII was erased — a structural id (the
/// aggregate id, e.g. a DepositId or LoanId), not PII.</param>
/// <param name="SubjectPseudonym">A salted one-way hash of the data-subject id (ADR-IC-016 / ADR-PC-004
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
/// (ADR-PC-021): the engine owns this handler; a family BINDS it against its own state in its module
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
/// An operator re-pinned a live instance to a newer family-SCHEMA version (ADR-PC-009, authoring §6).
/// In plain English: just as a deposit is locked for life to the regulatory pack it opened under, it is
/// equally locked to the family-schema version active at constitution — and the ONLY sanctioned way to
/// move it to a newer schema (when the bank chooses to consolidate) is this explicit, audited,
/// operator-driven migration. The engine emits one per affected instance. This is the SCHEMA twin of
/// <see cref="PackVersionMigrated"/>: identical store-only shape, the schema pin in place of the pack pin.
/// </summary>
/// <remarks>
/// The re-pin itself rides the EVENT ENVELOPE, not this payload: the migration event and every event
/// appended after it carry <c>schema_version = to_schema_version</c> on the envelope, while history before
/// it stays pinned to <c>from_schema_version</c> (ADR-PC-009 — the pin is a per-event fact, the
/// migration boundary intrinsic to the stream). So the projection fold is a NO-OP (the position record has
/// no schema-version field; writing one would re-introduce the rejected projection-column pin, ADR-PC-009
/// §B). This payload records the AUDIT facts — what moved, by whom, under which migration — so "which
/// instances were re-pinned, when, by whom" is answerable from the stream (authoring §6; DORA/PSD2
/// auditability).
/// <para>
/// STRUCTURAL only, never PII (ADR-PC-004): schema-version strings and a migration id are opaque
/// operational identifiers, and <paramref name="OperatorActor"/> is an OPERATOR/service identity — a
/// back-office actor reference, never a data subject's personal data (ADR-PC-009: a pin is two short
/// version strings — no PII). <paramref name="MigrationId"/> is the idempotency / dedupe key: re-issuing a
/// migration for an already-migrated instance is a no-op (ADR-PC-009).
/// </para>
/// </remarks>
/// <param name="InstanceId">The instance (stream) this migration re-pins — a structural id, not PII.</param>
/// <param name="FromSchemaVersion">The family-schema version the instance was pinned to before this event (e.g. <c>term_deposit@2026.1</c>).</param>
/// <param name="ToSchemaVersion">The family-schema version the instance is pinned to from this event's sequence forward (e.g. <c>term_deposit@2027.1</c>).</param>
/// <param name="MigrationId">The operator migration's id — the audit handle AND the idempotency dedupe key (ADR-PC-009).</param>
/// <param name="OperatorActor">The operator/service actor that initiated the migration — an operator identity reference, never PII (ADR-PC-004).</param>
public sealed record SchemaVersionMigrated(
    Guid InstanceId,
    string FromSchemaVersion,
    string ToSchemaVersion,
    string MigrationId,
    string OperatorActor) : DomainEvent
{
    /// <summary>
    /// A schema migration is a snapshot lifecycle boundary (ADR-PC-003 / event-store §8.1): the
    /// re-pin is a natural point where the instance's state — now interpreted under a new schema from
    /// here forward — stands on its own, so a snapshot fires here regardless of the per-N count. A pure
    /// structural property of the event TYPE (no clock, no I/O), so the engine stays family-agnostic when
    /// it ORs this into the per-append boundary signal.
    /// </summary>
    public override bool IsLifecycleBoundary => true;
}

/// <summary>
/// The pure fold for <see cref="SchemaVersionMigrated"/>, generic over ANY family projection
/// <typeparamref name="TState"/> so it stays FAMILY-AGNOSTIC (ADR-PC-021): the engine owns this
/// handler; a family BINDS it against its own state in its module (via
/// <see cref="CrossCuttingEventRegistrations.For{TState}"/>).
/// </summary>
/// <remarks>
/// The fold returns the state UNCHANGED — the conformant shape, not an omission: the pin lives on the
/// EVENT ENVELOPE (<c>schema_version</c>), and re-pinning is achieved by the append stamping
/// <c>to_schema_version</c> onto this event's envelope and every later event copying it forward
/// (ADR-PC-009). Writing the new pin onto the projection would re-introduce the
/// explicitly-rejected projection-column pin (ADR-PC-009). Pure — no clock, no I/O, no
/// randomness (BENG001/002/003) — so replay is deterministic.
/// </remarks>
public sealed class SchemaVersionMigratedHandler<TState> : IEventHandler<TState, SchemaVersionMigrated>
{
    public HandlerResult<TState> Apply(TState state, SchemaVersionMigrated @event)
        => HandlerResult<TState>.From(state);
}

/// <summary>
/// A legal hold was placed on an instance — a court order, garnishment, or external hold instruction
/// (event-store §4.1). In plain English: a court or external authority instructs the bank to set aside a
/// specific sum on a customer's instance pending a legal process; the engine records that instruction as
/// a cross-cutting audit fact. It applies to any product family, so the engine declares it ONCE in the
/// spine (it names no family, ADR-PC-021) rather than each family re-deriving it. Each family BINDS
/// the generic fold against its own projection state via
/// <see cref="CrossCuttingEventRegistrations.For{TState}"/>.
/// </summary>
/// <remarks>
/// <para>
/// STORE-ONLY (ADR-IC-017): appended, folded, and replayable, but carrying NO governed <c>.avsc</c>
/// — there is no NAMED external consumer that must react in v1. It is a v1 AUDIT fact only: the fold is a
/// NO-OP and adds NO Held/Frozen lifecycle state and NO operation-blocking guard. Any
/// operation-constraining semantics (available-balance / hold ledger) are a NEW ADR-PC decision — the v4
/// conta-à-ordem hold model in ADR-PC-033 (the <c>operations.Hold*</c> lifecycle), not a completeness
/// fix on this store-only fact. The synthetic <c>operations</c> aggregate_type keeps the stored
/// <c>event_type</c> <c>operations.FundsHeld</c> family-agnostic (event-store §4.3).
/// </para>
/// <para>
/// STRUCTURAL only, never PII (ADR-PC-004): <paramref name="InstanceId"/> and <paramref name="HoldId"/>
/// are opaque structural identifiers, and <paramref name="LegalReference"/> is a case/court reference, not
/// the data subject's personal data. <see cref="HeldAmount"/> is integer-cents <c>Money</c>, never a
/// float (ADR-PC-010). <paramref name="HoldId"/> is the idempotency / dedupe key. The amount is
/// supplied by the command, not computed in the fold (which stays pure).
/// </para>
/// </remarks>
/// <param name="InstanceId">The instance (stream) the hold applies to — a structural id, not PII.</param>
/// <param name="HoldId">The hold's id — the audit handle AND the idempotency dedupe key.</param>
/// <param name="HeldAmount">The amount placed on hold, as integer-cents <c>Money</c> (ADR-PC-010) — the <c>held_amount_cents</c> fact (event-store §4.1).</param>
/// <param name="LegalReference">The court order / garnishment reference (a case/instruction id) — STRUCTURAL, never PII (ADR-PC-004).</param>
/// <param name="HoldExpiresAt">When the hold lapses, if it is time-bounded — OPTIONAL (an open-ended hold carries <c>null</c>). An input date, never a clock read.</param>
public sealed record FundsHeld(
    Guid InstanceId,
    string HoldId,
    Money HeldAmount,
    string LegalReference,
    DateOnly? HoldExpiresAt = null) : DomainEvent;

/// <summary>
/// The pure fold for <see cref="FundsHeld"/>, generic over ANY family projection
/// <typeparamref name="TState"/> so it stays FAMILY-AGNOSTIC (ADR-PC-021): the engine owns this
/// handler; a family BINDS it against its own state in its module (via
/// <see cref="CrossCuttingEventRegistrations.For{TState}"/>).
/// </summary>
/// <remarks>
/// The fold returns the state UNCHANGED. In v1 a legal hold is a STORE-ONLY audit fact: it adds no
/// Held lifecycle state and constrains no operation. Operation-blocking / available-balance semantics
/// would be a new ADR-PC decision (ADR-PC-033's hold ledger), not a property of this fact. Pure — no
/// clock, no I/O, no randomness (BENG001/002/003) — so replay is deterministic.
/// </remarks>
public sealed class FundsHeldHandler<TState> : IEventHandler<TState, FundsHeld>
{
    public HandlerResult<TState> Apply(TState state, FundsHeld @event)
        => HandlerResult<TState>.From(state);
}

/// <summary>
/// A compliance freeze was placed on an instance — fraud, AML, or sanctions-screening hold (event-store
/// §4.1). In plain English: a compliance/AML/sanctions process instructs the bank to freeze a customer's
/// instance; the engine records that action as a cross-cutting audit fact. It applies to any product
/// family, so the engine declares it ONCE in the spine (it names no family, ADR-PC-021) rather than
/// each family re-deriving it. Each family BINDS the generic fold against its own projection state via
/// <see cref="CrossCuttingEventRegistrations.For{TState}"/>.
/// </summary>
/// <remarks>
/// <para>
/// STORE-ONLY (ADR-IC-017): appended, folded, and replayable, but carrying NO governed <c>.avsc</c>
/// — there is no NAMED external consumer that must react in v1. It is a v1 AUDIT fact only: the fold is a
/// NO-OP and adds NO Frozen lifecycle state and NO operation-blocking guard (any operation-constraining
/// semantics would be a new ADR-PC decision, not a completeness fix). The synthetic <c>operations</c>
/// aggregate_type keeps the stored <c>event_type</c> <c>operations.AccountFrozen</c> family-agnostic
/// (event-store §4.3).
/// </para>
/// <para>
/// STRUCTURAL only, never PII (ADR-PC-004): <paramref name="InstanceId"/> and
/// <paramref name="FreezeId"/> are opaque structural identifiers; <paramref name="FreezeReason"/> is a
/// stable machine code (e.g. <c>AML_SCREENING</c>, <c>SANCTIONS_MATCH</c>), never free-text PII; and
/// <paramref name="ComplianceActor"/> is a back-office/service identity, never a data subject.
/// <paramref name="FreezeId"/> is the idempotency / dedupe key.
/// </para>
/// </remarks>
/// <param name="InstanceId">The instance (stream) the freeze applies to — a structural id, not PII.</param>
/// <param name="FreezeId">The freeze's id — the audit handle AND the idempotency dedupe key.</param>
/// <param name="FreezeReason">A stable machine code for why the freeze was placed (e.g. <c>AML_SCREENING</c>, <c>SANCTIONS_MATCH</c>) — never PII (ADR-PC-004).</param>
/// <param name="ComplianceActor">The compliance operator/service actor that placed the freeze — an operator identity reference, never PII (ADR-PC-004).</param>
/// <param name="FreezeExpiresAt">When the freeze lapses, if it is time-bounded — OPTIONAL (an open-ended freeze carries <c>null</c>). An input date, never a clock read.</param>
public sealed record AccountFrozen(
    Guid InstanceId,
    string FreezeId,
    string FreezeReason,
    string ComplianceActor,
    DateOnly? FreezeExpiresAt = null) : DomainEvent;

/// <summary>
/// The pure fold for <see cref="AccountFrozen"/>, generic over ANY family projection
/// <typeparamref name="TState"/> so it stays FAMILY-AGNOSTIC (ADR-PC-021): the engine owns this
/// handler; a family BINDS it against its own state in its module (via
/// <see cref="CrossCuttingEventRegistrations.For{TState}"/>).
/// </summary>
/// <remarks>
/// The fold returns the state UNCHANGED. In v1 a compliance freeze is a STORE-ONLY audit fact: it adds
/// no Frozen lifecycle state and constrains no operation. Operation-blocking semantics would be a new
/// ADR-PC decision, not a property of this fact. Pure — no clock, no I/O, no randomness
/// (BENG001/002/003) — so replay is deterministic.
/// </remarks>
public sealed class AccountFrozenHandler<TState> : IEventHandler<TState, AccountFrozen>
{
    public HandlerResult<TState> Apply(TState state, AccountFrozen @event)
        => HandlerResult<TState>.From(state);
}

/// <summary>
/// A legal hold was lifted — the release instruction that ends a <see cref="FundsHeld"/> earmark
/// (ADR-PC-041). In plain English: the court order or garnishment that set money aside is
/// discharged, so the held funds become spendable again. This is the legal-hold twin of the
/// authorization lifecycle's <see cref="HoldCaptured"/>/<see cref="HoldExpired"/>, but with a
/// crucial difference: a legal hold is NEVER captured — releasing it moves NO money (a garnishment
/// that is actually paid out is a separate debit <c>Movement</c> the legal process instructs, not a
/// capture of this hold). It applies to any product family, so the engine declares it ONCE in the
/// spine (it names no family, ADR-PC-021); each family BINDS the generic no-op fold via
/// <see cref="CrossCuttingEventRegistrations.For{TState}"/>.
/// </summary>
/// <remarks>
/// <para>
/// STORE-ONLY (ADR-IC-017): appended, folded, and replayable, carrying NO governed <c>.avsc</c> —
/// there is no NAMED external consumer in v1. Unlike the store-only NO-OP its <see cref="FundsHeld"/>
/// placement had before ADR-PC-041, the pair now DOES something: the SPINE-owned
/// <see cref="AccountHoldProjector"/> folds <see cref="FundsHeld"/> into the <c>account_ref</c>-keyed
/// active-hold set (kind <c>LEGAL</c>) so it lowers <c>available balance</c>, and this event
/// transitions that row out of the active set — restoring the balance, no posting. The synthetic
/// <c>operations</c> aggregate_type keeps the stored <c>event_type</c> <c>operations.FundsReleased</c>
/// family-agnostic (event-store §4.3).
/// </para>
/// <para>
/// Two idempotency keys for two seams (ADR-PC-041 slot 4): the legal-hold LIFECYCLE is keyed by
/// <paramref name="HoldId"/> — a re-delivered release folds at most once (a reconciliation signal,
/// never a double-restore) — while the APPEND is keyed by the command's <c>CommandId</c>
/// (ADR-PC-029), carried on the envelope, never here. STRUCTURAL only, never PII (ADR-PC-004):
/// <paramref name="InstanceId"/>/<paramref name="HoldId"/> are opaque ids and
/// <paramref name="ReleaseReference"/> is a court/case discharge reference.
/// </para>
/// </remarks>
/// <param name="InstanceId">The instance (stream) the released hold applied to — a structural id, not PII.</param>
/// <param name="HoldId">The <see cref="FundsHeld"/> this release lifts — the ADR-PC-041 legal-hold lifecycle key.</param>
/// <param name="ReleaseReference">The discharge/release instruction reference (a case/court reference) — STRUCTURAL, never PII (ADR-PC-004).</param>
public sealed record FundsReleased(
    Guid InstanceId,
    string HoldId,
    string ReleaseReference) : DomainEvent;

/// <summary>
/// The pure per-family fold for <see cref="FundsReleased"/>, generic over ANY family projection
/// <typeparamref name="TState"/> so it stays FAMILY-AGNOSTIC (ADR-PC-021): the engine owns this
/// handler; a family BINDS it against its own state via
/// <see cref="CrossCuttingEventRegistrations.For{TState}"/>.
/// </summary>
/// <remarks>
/// The fold returns the state UNCHANGED — the conformant shape, not an omission: the active-hold set
/// (both authorization AND legal holds, ADR-PC-041) is the SPINE-owned <see cref="AccountHoldProjector"/>
/// fold, never family projection state. Pure — no clock, no I/O, no randomness (BENG001/002/003) — so
/// replay is deterministic.
/// </remarks>
public sealed class FundsReleasedHandler<TState> : IEventHandler<TState, FundsReleased>
{
    public HandlerResult<TState> Apply(TState state, FundsReleased @event)
        => HandlerResult<TState>.From(state);
}

/// <summary>
/// A compliance freeze was lifted — the instruction that ends an <see cref="AccountFrozen"/> block
/// (ADR-PC-041). In plain English: the AML/sanctions process that froze the account clears it, so
/// debits are allowed again. From this event's sequence forward the instance is no longer frozen and
/// the stages-3–5 authorization decider stops refusing its debits. It applies to any product family,
/// so the engine declares it ONCE in the spine (it names no family, ADR-PC-021); each family BINDS
/// the generic no-op fold via <see cref="CrossCuttingEventRegistrations.For{TState}"/>.
/// </summary>
/// <remarks>
/// <para>
/// STORE-ONLY (ADR-IC-017): appended, folded, replayable, NO governed <c>.avsc</c> in v1. The
/// frozen predicate the decider consults is the SPINE-owned <c>AccountFreezeProjector</c> fold over
/// the <c>account_freezes</c> read model (migration 0022): <see cref="AccountFrozen"/> marks the
/// instance frozen, this event lifts it. A freeze is a total block, NOT an amount, so neither event
/// touches <c>available balance</c> (ADR-PC-041 slot 2). The synthetic <c>operations</c>
/// aggregate_type keeps the stored <c>event_type</c> <c>operations.AccountUnfrozen</c> family-agnostic.
/// </para>
/// <para>
/// Idempotency: the freeze LIFECYCLE is keyed by <paramref name="FreezeId"/> — a re-delivered
/// unfreeze folds at most once (a reconciliation signal). STRUCTURAL only, never PII (ADR-PC-004):
/// <paramref name="UnfreezeActor"/> is an operator/service identity; <paramref name="UnfreezeReason"/>
/// is a stable machine code, never free-text.
/// </para>
/// </remarks>
/// <param name="InstanceId">The instance (stream) the lifted freeze applied to — a structural id, not PII.</param>
/// <param name="FreezeId">The <see cref="AccountFrozen"/> this event lifts — the ADR-PC-041 freeze lifecycle key.</param>
/// <param name="UnfreezeActor">The compliance operator/service actor that lifted the freeze — an operator identity, never PII (ADR-PC-004).</param>
/// <param name="UnfreezeReason">A stable machine code for why the freeze was lifted (e.g. <c>SCREENING_CLEARED</c>) — never PII.</param>
public sealed record AccountUnfrozen(
    Guid InstanceId,
    string FreezeId,
    string UnfreezeActor,
    string UnfreezeReason) : DomainEvent;

/// <summary>
/// The pure per-family fold for <see cref="AccountUnfrozen"/>, generic over ANY family projection
/// <typeparamref name="TState"/> so it stays FAMILY-AGNOSTIC (ADR-PC-021): the engine owns this
/// handler; a family BINDS it against its own state via
/// <see cref="CrossCuttingEventRegistrations.For{TState}"/>.
/// </summary>
/// <remarks>
/// The fold returns the state UNCHANGED — the conformant shape, not an omission: the frozen predicate
/// is the SPINE-owned <c>AccountFreezeProjector</c> fold, never family projection state. Pure — no
/// clock, no I/O, no randomness (BENG001/002/003) — so replay is deterministic.
/// </remarks>
public sealed class AccountUnfrozenHandler<TState> : IEventHandler<TState, AccountUnfrozen>
{
    public HandlerResult<TState> Apply(TState state, AccountUnfrozen @event)
        => HandlerResult<TState>.From(state);
}

/// <summary>
/// The engine-declared cross-cutting event bindings, yielded for a given family projection state. In
/// plain English: the engine owns these family-agnostic operational events (§4.1), but the handler
/// registry needs each one bound to a concrete projection state — so a family calls
/// <see cref="For{TState}"/> and splices the result into its own <see cref="IFamilyModule.Handlers"/>
/// rather than hand-rolling the binding (and risking forgetting one). The engine owns the event RECORD
/// + the generic HANDLER; the family supplies only its state type — the <c>family → engine</c> arrow
/// stays one-way (ADR-PC-021).
/// </summary>
/// <remarks>
/// Binds the engine-declared cross-cutting set (the file-header remarks list the events and the
/// <c>operations.&lt;EventName&gt;</c> naming convention) against one family's state. A family that
/// splices this in gets the whole set without changing its module again.
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
        new("operations.SchemaVersionMigrated", typeof(SchemaVersionMigrated),
            new DispatchableHandler<TState, SchemaVersionMigrated>(new SchemaVersionMigratedHandler<TState>())),
        new("operations.FundsHeld", typeof(FundsHeld),
            new DispatchableHandler<TState, FundsHeld>(new FundsHeldHandler<TState>())),
        // The ADR-PC-041 legal-hold release: lifts a FundsHeld earmark out of the spine-owned
        // active-hold set. Bound for EVERY family so it decodes (and replays fail-closed) on any
        // family stream that carries it; the fold is a no-op because the legal-hold set is the
        // SPINE-owned AccountHoldProjector fold.
        new("operations.FundsReleased", typeof(FundsReleased),
            new DispatchableHandler<TState, FundsReleased>(new FundsReleasedHandler<TState>())),
        new("operations.AccountFrozen", typeof(AccountFrozen),
            new DispatchableHandler<TState, AccountFrozen>(new AccountFrozenHandler<TState>())),
        // The ADR-PC-041 freeze lift: clears the spine-owned frozen predicate the stages-3–5 decider
        // consults. Bound for EVERY family (decode/replay fail-closed); the fold is a no-op because
        // the frozen predicate is the SPINE-owned AccountFreezeProjector fold.
        new("operations.AccountUnfrozen", typeof(AccountUnfrozen),
            new DispatchableHandler<TState, AccountUnfrozen>(new AccountUnfrozenHandler<TState>())),
        new("operations.PersonalDataErasureRequested", typeof(PersonalDataErasureRequested),
            new DispatchableHandler<TState, PersonalDataErasureRequested>(new PersonalDataErasureRequestedHandler<TState>())),
        // The ADR-PC-033 hold lifecycle (AccountHoldEvents.cs): three cross-cutting facts any
        // transactional family's authorization path appends. Bound for EVERY family so the events
        // decode (and replay fail-closed) on any family stream that carries them; the folds are
        // no-ops because the active-hold set is the SPINE-owned AccountHoldProjector fold.
        new("operations.HoldPlaced", typeof(HoldPlaced),
            new DispatchableHandler<TState, HoldPlaced>(new HoldPlacedHandler<TState>())),
        new("operations.HoldCaptured", typeof(HoldCaptured),
            new DispatchableHandler<TState, HoldCaptured>(new HoldCapturedHandler<TState>())),
        new("operations.HoldExpired", typeof(HoldExpired),
            new DispatchableHandler<TState, HoldExpired>(new HoldExpiredHandler<TState>())),
        // The ADR-PC-043 slot-5 undeliverable-credit pair (CreditUnappliedEvents.cs): the two
        // cross-cutting facts a family appends when a payout has nowhere to land (CreditUnapplied)
        // and when the destination later exists (CreditReapplied). Bound for EVERY family so the
        // events decode (and replay fail-closed) on any family stream that can carry them; the folds
        // are no-ops because the undeliverable-credit IOU/escheat ledger is a SPINE-owned fold.
        new("operations.CreditUnapplied", typeof(CreditUnapplied),
            new DispatchableHandler<TState, CreditUnapplied>(new CreditUnappliedHandler<TState>())),
        new("operations.CreditReapplied", typeof(CreditReapplied),
            new DispatchableHandler<TState, CreditReapplied>(new CreditReappliedHandler<TState>())),
    ];
}
