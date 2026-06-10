using System.Text.Json;
using System.Text.Json.Serialization;
using Babelstone.Orchestrator.Saga;

namespace Babelstone.Orchestrator.Commands;

/// <summary>
/// The LOGICAL payload bodies of the commands the <see cref="ConstitutionProcess"/> saga emits
/// (ADR-IC-003 §P1 "the specific commands it emits"; Document 05). One record per command name
/// declared on <see cref="ConstitutionProcess"/>. These are the bytes the outbox row carries —
/// the seam (<c>SagaCommandOutboxSink</c>) wraps them in an envelope that adds the operational
/// identity (a fresh delivery message id, the created-at stamp); the BODY here carries none of
/// that.
/// </summary>
/// <remarks>
/// <para>
/// <b>Replay-stable bytes (ADR-PC-010 §P5).</b> A command body is a pure function of the saga
/// facts in scope at the transition — the <see cref="CommandPayload.ProcessId"/>, the identity
/// trio (<see cref="CommandPayload.CausationMessageId"/> /
/// <see cref="CommandPayload.CorrelationId"/>), and structural REFERENCES. It contains NO freshly
/// minted GUID and NO wall-clock timestamp: emitting the same logical command twice yields
/// byte-identical bodies (the <c>SagaCommandOutboxSink</c> byte-stability assertion). Any GUID
/// here is a CAUSATION reference (an id that already exists upstream), never <c>Guid.NewGuid()</c>;
/// any "time" is the saga's own correlation, not <c>DateTimeOffset.UtcNow</c>.
/// </para>
/// <para>
/// <b>No PII (ADR-PC-004 §P2 / no-PII-on-the-durable-bus).</b> Every field is a structural
/// reference — a process id, a correlation/causation id, an opaque Core hold/txn reference, a
/// deposit reference. NEVER a raw IBAN, NIF, name, or amount-bearing identity. A subject's PII
/// is resolved internally behind the engine's OpenBao boundary; the durable bus carries only
/// references (the allow-list the no-PII test asserts positively).
/// </para>
/// </remarks>
[JsonDerivedType(typeof(ReserveAccountBalanceCommand), typeDiscriminator: ConstitutionProcess.ReserveAccountBalance)]
[JsonDerivedType(typeof(ValidateProductLimitsCommand), typeDiscriminator: ConstitutionProcess.ValidateProductLimits)]
[JsonDerivedType(typeof(ConfirmDebitCommand), typeDiscriminator: ConstitutionProcess.ConfirmDebit)]
[JsonDerivedType(typeof(ActivateDepositCommand), typeDiscriminator: ConstitutionProcess.ActivateDeposit)]
[JsonDerivedType(typeof(ReleaseBalanceReservationCommand), typeDiscriminator: ConstitutionProcess.ReleaseBalanceReservation)]
[JsonDerivedType(typeof(ReverseCoreDebitCommand), typeDiscriminator: ConstitutionProcess.ReverseCoreDebit)]
public abstract record CommandPayload
{
    /// <summary>The saga instance this command belongs to (the Document 05 PROC-… reference).
    /// Structural, PII-free.</summary>
    public required Guid ProcessId { get; init; }

    /// <summary>The triggering event's message id — the CAUSATION reference (ADR-IC-003 §P7).
    /// An id that ALREADY EXISTS (the inbox event's ce_id), carried through; NEVER a freshly
    /// minted GUID. Part of the identity trio that rides every emission.</summary>
    public required Guid CausationMessageId { get; init; }

    /// <summary>The originating request's correlation reference, carried UNCHANGED through the
    /// whole saga (ADR-IC-003 §P7). Null only for a saga started without one. A reference, not
    /// a minted value.</summary>
    public Guid? CorrelationId { get; init; }

    /// <summary>The command NAME this payload is the body of — the same constant the
    /// <see cref="ConstitutionProcess"/> transition table emits. Used as the outbox
    /// <c>command_type</c> column and the JSON type discriminator.</summary>
    [JsonIgnore]
    public abstract string CommandType { get; }

    /// <summary>
    /// The byte-stable serialization options for command bodies. Deterministic by construction:
    /// no indentation, a fixed property order (the source declaration order, stable across runs),
    /// and no culture/locale-sensitive formatting. The SAME logical command serializes to the
    /// SAME bytes every time — the property the <c>SagaCommandOutboxSink</c> byte-stability
    /// assertion locks in. Crucially, nothing here injects a clock or a GUID.
    /// </summary>
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        // A stable, explicit converter set — no reflection-time ordering surprises. The derived
        // polymorphic types are resolved by the [JsonDerivedType] discriminators above.
    };

    /// <summary>
    /// Serialize this command body to its replay-stable bytes. PURE: a function of the record's
    /// fields alone — no clock, no GUID minting, no ambient state (ADR-PC-010 §P5). The
    /// discriminator the polymorphic options emit is the command name, so the same logical
    /// command always yields the same bytes.
    /// </summary>
    /// <remarks>
    /// <b>Virtual by design.</b> The seam-level <see cref="SagaCommandEnvelopeBody"/> serializes a
    /// DIFFERENT (non-polymorphic) projection because its command type is not in the
    /// <c>[JsonDerivedType]</c> set, so it <c>override</c>s this method. Declaring the base
    /// <c>virtual</c> (rather than letting the override <c>new</c>-shadow it) means the byte-stable
    /// projection is selected by DYNAMIC DISPATCH regardless of the static call-site type — a
    /// <see cref="CommandPayload"/>-typed reference to an envelope body still gets the seam bytes.
    /// </remarks>
    public virtual byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes<CommandPayload>(this, SerializerOptions);
}

/// <summary>
/// The minimal, seam-level logical command body the <c>SagaCommandOutboxSink</c> serializes
/// from what the advance handler hands it at a transition: the process reference + the command
/// name + the identity trio. The full business-reference payloads above (with AccountRef,
/// DepositRef, …) are assembled by the saga's own command-building logic once the per-saga
/// facts are plumbed through; THIS body is what the substrate's seam can write deterministically
/// today (the handler emits command NAMES, not assembled business payloads).
/// </summary>
/// <remarks>
/// Byte-stable by construction (the <c>SagaCommandOutboxSink</c> assertion): every field is a
/// reference the handler already holds — the <see cref="CommandPayload.ProcessId"/>, the
/// <see cref="CommandPayload.CommandType"/>, and the identity trio — NONE freshly minted. The
/// fresh delivery message id and the created-at stamp live in the OUTBOX ROW (operational
/// columns), never in this body. PII-free: process/causation/correlation are references.
/// </remarks>
public sealed record SagaCommandEnvelopeBody : CommandPayload
{
    private readonly string _commandType;

    /// <summary>Construct the seam-level body for <paramref name="commandType"/>.</summary>
    public SagaCommandEnvelopeBody(string commandType) =>
        _commandType = commandType ?? throw new ArgumentNullException(nameof(commandType));

    /// <inheritdoc />
    public override string CommandType => _commandType;

    /// <summary>
    /// Serialize this seam-level body to its replay-stable bytes. Unlike the business-reference
    /// payloads, the envelope body is NOT in the polymorphic <c>[JsonDerivedType]</c> set (its
    /// command type varies), so it serializes its OWN fixed-order projection — the process id,
    /// the command type, and the identity trio. PURE: a function of the record's fields, with no
    /// clock and no GUID minting (ADR-PC-010 §P5), so the same logical command yields the same
    /// bytes on every emission (the byte-stability assertion).
    /// </summary>
    /// <remarks>
    /// <b>Override, not <c>new</c>.</b> Overriding the <c>virtual</c> base means this seam
    /// projection is selected by dynamic dispatch even through a <see cref="CommandPayload"/>-typed
    /// reference, so a call site that does not know the static type still gets byte-stable seam
    /// bytes — the shadowing footgun (static-type-dependent byte selection) is closed.
    /// </remarks>
    public override byte[] ToBytes() =>
        JsonSerializer.SerializeToUtf8Bytes(
            new SeamProjection(ProcessId, CommandType, CausationMessageId, CorrelationId),
            SerializerOptions);

    /// <summary>The fixed-order DTO the envelope body serializes to — declaration order is the
    /// byte order, deterministic across runs. PII-free (all references).</summary>
    private readonly record struct SeamProjection(
        Guid ProcessId, string CommandType, Guid CausationMessageId, Guid? CorrelationId);
}

/// <summary>Core ACL: place the reversible balance hold (Document 05 step 2a). Carries the
/// opaque account reference to hold against — NEVER the raw IBAN — and a derived reservation
/// reference. No PII.</summary>
public sealed record ReserveAccountBalanceCommand : CommandPayload
{
    /// <summary>The opaque account reference to reserve against (a token, not an IBAN). The ACL
    /// resolves the real account behind the OpenBao boundary.</summary>
    public required string AccountRef { get; init; }

    /// <summary>The saga-chosen idempotency reference for THIS reservation — a derived, stable
    /// reference (the process id namespaced for the reserve leg), NOT a minted GUID, so the body
    /// is byte-stable on re-emit and the ACL dedups on it.</summary>
    public required string ReservationRef { get; init; }

    /// <inheritdoc />
    public override string CommandType => ConstitutionProcess.ReserveAccountBalance;
}

/// <summary>Deposit aggregate: validate product limits (Document 05 step 2b). Carries the
/// deposit reference and the opaque product reference whose limits to check — no amount-bearing
/// identity, no PII.</summary>
public sealed record ValidateProductLimitsCommand : CommandPayload
{
    /// <summary>The deposit aggregate reference being constituted (e.g. DEP-…).</summary>
    public required string DepositRef { get; init; }

    /// <summary>The opaque product reference whose limits the aggregate checks.</summary>
    public required string ProductRef { get; init; }

    /// <inheritdoc />
    public override string CommandType => ConstitutionProcess.ValidateProductLimits;
}

/// <summary>Core ACL: convert the hold into a real debit — the IRREVERSIBLE step (Document 05
/// step 4a). Reachable ONLY from APPROVED (§P5). Carries the opaque Core hold reference; no
/// PII.</summary>
public sealed record ConfirmDebitCommand : CommandPayload
{
    /// <summary>The opaque Core hold reference to confirm (e.g. CORE-HOLD-…). A reference issued
    /// upstream by the ACL on the reserve leg — NOT minted here.</summary>
    public required string CoreHoldRef { get; init; }

    /// <inheritdoc />
    public override string CommandType => ConstitutionProcess.ConfirmDebit;
}

/// <summary>Deposit aggregate: activate the deposit after the debit (Document 05 step 4b).
/// Reachable ONLY from APPROVED (§P5). Carries the deposit reference and the opaque
/// upstream-issued Core txn reference; no PII.</summary>
public sealed record ActivateDepositCommand : CommandPayload
{
    /// <summary>The deposit aggregate reference to activate.</summary>
    public required string DepositRef { get; init; }

    /// <summary>The opaque Core transaction reference the debit produced (e.g. CT-…) — issued
    /// upstream by Core, carried through; NOT minted here.</summary>
    public required string CoreTxnRef { get; init; }

    /// <inheritdoc />
    public override string CommandType => ConstitutionProcess.ActivateDeposit;
}

/// <summary>Core ACL: release the reversible hold — early compensation (Document 05 Scenario A).
/// A DOMAIN reversal command (§P6), never a rollback. Idempotent on a no-op if the hold never
/// existed. Carries the reservation reference; no PII.</summary>
public sealed record ReleaseBalanceReservationCommand : CommandPayload
{
    /// <summary>The reservation reference to release — the SAME derived reference the reserve leg
    /// used, so release targets exactly what was reserved.</summary>
    public required string ReservationRef { get; init; }

    /// <inheritdoc />
    public override string CommandType => ConstitutionProcess.ReleaseBalanceReservation;
}

/// <summary>Core ACL: reverse the committed debit with a compensating credit — late
/// compensation (Document 05 Scenario B). A two-movement DOMAIN reversal (§P6), never an undo.
/// Carries the opaque Core txn reference to reverse; no PII.</summary>
public sealed record ReverseCoreDebitCommand : CommandPayload
{
    /// <summary>The opaque Core transaction reference to reverse (the one the debit produced) —
    /// carried through, NOT minted here.</summary>
    public required string CoreTxnRef { get; init; }

    /// <inheritdoc />
    public override string CommandType => ConstitutionProcess.ReverseCoreDebit;
}
