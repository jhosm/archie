using System.Text.Json;
using System.Text.Json.Serialization;

namespace Babelstone.Families.TermDeposit.Orchestration;

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
[JsonDerivedType(typeof(QueryCoreDebitStatusCommand), typeDiscriminator: ConstitutionProcess.QueryCoreDebitStatus)]
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
    /// <summary>The opaque account reference to reserve against (a token, not an IBAN). On an
    /// <c>engine-ca</c> funding leg this is the customer's real conta-à-ordem account_ref (a GUID string —
    /// <c>AccountRef == AccountId.ToString()</c>), which the engine-CA authorize/hold WRITER reads as the
    /// destination (ADR-PC-043); on a legacy leg the ACL resolves the real account behind
    /// the OpenBao boundary.</summary>
    [JsonPropertyName("account_ref")]
    public required string AccountRef { get; init; }

    /// <summary>The saga-chosen idempotency reference for THIS reservation — a derived, stable
    /// reference (the process id namespaced for the reserve leg), NOT a minted GUID, so the body
    /// is byte-stable on re-emit and the ACL dedups on it.</summary>
    [JsonPropertyName("reservation_ref")]
    public required string ReservationRef { get; init; }

    /// <summary>The settlement-COUNTERPARTY discriminator the dispatcher routes on (ADR-PC-043):
    /// <c>engine-ca</c> routes this funding hold to the engine-owned CA authorize
    /// writer, <c>legacy-dda</c> (or null) to the legacy Core ACL (UNCHANGED). Header-only routing: this is
    /// the ONLY body field the substrate router reads for the counterparty — never <c>account_ref</c>
    /// (ADR-IC-018 / ADR-PC-043). Null on a legacy leg: the new field serializes as an explicit null there
    /// (the serializer keeps nulls), and the logical command and its replay-stability are unchanged.</summary>
    [JsonPropertyName("settlement_target")]
    public string? SettlementTarget { get; init; }

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
    [JsonPropertyName("core_hold_ref")]
    public required string CoreHoldRef { get; init; }

    /// <summary>The opaque destination account_ref the captured debit lands on (ADR-PC-043). On an
    /// <c>engine-ca</c> funding leg this is the customer's real
    /// conta-à-ordem account_ref (a GUID string) the engine-CA capture WRITER reads as the destination; the
    /// value is family-promoted and substrate-forwarded UNTOUCHED (never re-derived). Null on a legacy leg
    /// (the legacy core resolves the account from the process-scoped business reference): the new field
    /// serializes as an explicit null there, and the logical command and its replay-stability are
    /// unchanged.</summary>
    [JsonPropertyName("account_ref")]
    public string? AccountRef { get; init; }

    /// <summary>The shared HOLD-LINKING + exactly-once reference: the SAME derived
    /// reference the reserve leg used (the reservation reference), so on an <c>engine-ca</c> leg the engine
    /// ingress reconstructs the SAME deterministic hold the reserve leg's authorize placed
    /// (<c>target_hold_id = f(intent_reference)</c>) and targets it. Also the ADR-PC-043 economic-intent
    /// axis the CA capture's append command_id derives from. Null on a legacy leg (unchanged).</summary>
    [JsonPropertyName("intent_reference")]
    public string? IntentReference { get; init; }

    /// <summary>The captured amount in integer cents — exactly the funded principal (the in-band
    /// WRONG-AMOUNT guard the engine-CA capture writer enforces, ADR-PC-043). Money-as-integer-cents
    /// on the wire (ADR-PC-010), never a float. Null on a legacy leg (the legacy ACL derives the amount
    /// from the reserved hold): the field serializes as an explicit null there; the logical command and its
    /// replay-stability are unchanged.</summary>
    [JsonPropertyName("amount_cents")]
    public long? AmountCents { get; init; }

    /// <summary>The settlement-COUNTERPARTY discriminator the dispatcher routes on (ADR-PC-043):
    /// <c>engine-ca</c> routes this capture to the engine-owned CA capture writer,
    /// <c>legacy-dda</c> (or null) to the legacy Core ACL. Header-only routing — the ONLY body field the
    /// substrate router reads for the counterparty (never <c>account_ref</c>; ADR-IC-018 / ADR-PC-043).
    /// Null on a legacy leg (unchanged).</summary>
    [JsonPropertyName("settlement_target")]
    public string? SettlementTarget { get; init; }

    /// <inheritdoc />
    public override string CommandType => ConstitutionProcess.ConfirmDebit;
}

/// <summary>
/// Deposit aggregate: activate (constitute) the deposit after the debit (Document 05 step 4b).
/// Reachable ONLY from APPROVED (§P5). Unlike the other saga commands, this one is delivered to the
/// ENGINE's <c>POST /v1/deposits</c> command surface (the Pact-pinned route, ADR-PC-029 slot 1), so its
/// wire body is the <c>Babelstone.Families.TermDeposit.Application.ConstituteDepositRequest</c> shape
/// — NOT the polymorphic saga-command envelope. The body carries the MINIMAL per-deposit facts: the
/// product code, principal cents, and funding account, plus <c>deposit_id = process_id</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The orchestrator carries NO product-family knowledge (Fork B rework, bd t7o3.11 / 3k10 / c8d8).</b>
/// The rejected v1 stand-in shipped the structural product facts (term / interest variant / renewal
/// policy / coupon cadence / pricing role / start date) on this body, pinning them at the orchestrator
/// edge. They are GONE: the ENGINE resolves them from the product code at constitution — the
/// maintainer's Q2 choice that makes the engine the single home of product config (ADR-PC-009). The
/// body is now the minimal <c>{deposit_id, product_id, principal_cents, funding_account}</c>.
/// </para>
/// <para>
/// <b>deposit_id = process_id (the ce_subject correlation pin, bd babelstone-t7o3.11 / 3k10).</b> The
/// engine honours the supplied <c>deposit_id</c> AS the stream/aggregate id, so the
/// <c>DepositConstituted</c> the engine relays carries <c>ce_subject = aggregate_id = process_id</c>.
/// That is what lets the orchestrator's consume loop correlate the engine's REAL integration fact
/// (arriving on the <c>term_deposit</c> family topic) back to THIS saga by identity. We send the raw
/// <see cref="CommandPayload.ProcessId"/> GUID as the engine's <c>deposit_id</c>; the <c>DEP-…</c>
/// <see cref="DepositRef"/> is the EDGE-facing client reference, a separate concern.
/// </para>
/// <para>
/// <b>The engine resolves the RATE and the SHAPE in-transaction (bd babelstone-3k10 / c8d8, ADR-PC-008
/// §S2).</b> This body carries neither the TAN nor the structural facts — the engine resolves the
/// product config (term / variant / renewal / cadence / role) and the active rate sheet, and stamps both
/// in the SAME transaction as the constitution append + outbox (the de-settled constitution path).
/// </para>
/// <para>
/// <b>Byte-stable, PII-free (ADR-PC-010 §P5 / ADR-PC-004 §P2).</b> Every field is a structural reference
/// or an integer-cents scalar — the catalogue product code, integer-cents principal, the opaque
/// funding-account token, the process-id deposit id. NO clock, NO minted GUID inside the body (the
/// process id is a carried reference), so re-emitting the same logical command yields identical bytes.
/// NEVER a raw IBAN/NIF/name.
/// </para>
/// </remarks>
public sealed record ActivateDepositCommand : CommandPayload
{
    /// <summary>The deposit aggregate reference to activate (the EDGE-facing <c>DEP-…</c> handle, kept
    /// for the audit trail; the engine stream id is <see cref="CommandPayload.ProcessId"/>).</summary>
    public required string DepositRef { get; init; }

    /// <summary>The opaque Core transaction reference the debit produced (e.g. CT-…) — issued
    /// upstream by Core, carried through; NOT minted here.</summary>
    public required string CoreTxnRef { get; init; }

    /// <summary>The product catalogue code the engine prices and constitutes (e.g.
    /// <c>dpz_pt_12m_juros_venc</c>). Maps to the engine's <c>product_id</c> — the engine resolves both
    /// the structural shape and the rate from it.</summary>
    public required string ProductCode { get; init; }

    /// <summary>The deposit principal in integer cents — the engine's <c>principal_cents</c>.</summary>
    public required long PrincipalCents { get; init; }

    /// <summary>The opaque funding-account token to debit — the engine's <c>funding_account</c>. A
    /// token, NOT a raw IBAN (ADR-PC-004 §P2).</summary>
    public required string FundingAccount { get; init; }

    /// <inheritdoc />
    public override string CommandType => ConstitutionProcess.ActivateDeposit;

    /// <summary>
    /// Serialize to the ENGINE's MINIMAL <c>ConstituteDepositRequest</c> wire shape (snake_case, money as
    /// integer cents) — NOT the polymorphic saga-command envelope, because this command is delivered to
    /// the engine's <c>POST /v1/deposits</c> surface, whose SnakeCaseLower deserializer would reject a
    /// <c>$type</c> discriminator. <c>deposit_id</c> is the raw <see cref="CommandPayload.ProcessId"/>, so
    /// <c>ce_subject = process_id</c> on the relayed <c>DepositConstituted</c> (bd babelstone-t7o3.11).
    /// The structural facts are NOT sent — the engine resolves them from the product code (Fork B rework).
    /// PURE and byte-stable: every field is a structural reference — no clock, no minted GUID.
    /// </summary>
    public override byte[] ToBytes() =>
        System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new EngineConstituteBody(
                PrincipalCents: PrincipalCents,
                ProductId: ProductCode,
                FundingAccount: FundingAccount,
                DepositId: ProcessId),
            EngineConstituteSerializerOptions);

    /// <summary>The byte-stable, snake_case serializer the engine's constitute surface expects
    /// (SnakeCaseLower). A FIXED, explicit policy — no indentation, declaration-order properties — so
    /// the same logical command yields identical bytes.</summary>
    internal static readonly System.Text.Json.JsonSerializerOptions EngineConstituteSerializerOptions = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    /// <summary>
    /// The engine's MINIMAL <c>ConstituteDepositRequest</c> shape mirrored here so the orchestrator emits
    /// the engine wire body WITHOUT a project reference to <c>Babelstone.Engine.Api</c> (extraction-ready,
    /// ADR-PC-019 §P2 — the orchestrator never depends on the engine kernel/host). Field order + names
    /// mirror the engine contract; the snake_case policy maps them onto the wire. The TAN and the
    /// structural facts are deliberately absent — the engine resolves both in-transaction (bd
    /// babelstone-3k10 / c8d8). The Pact-style CDC (<c>EngineCommandContract</c> +
    /// <c>EngineCommandPactProviderTests</c>) pins this shape against the REAL engine, so a drift between
    /// this mirror and the engine contract is a build failure.
    /// </summary>
    private readonly record struct EngineConstituteBody(
        long PrincipalCents,
        string ProductId,
        string FundingAccount,
        Guid DepositId);
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

/// <summary>Core ACL: query the Core for the actual outcome of an INDETERMINATE debit — the v1
/// clearance-job mechanism (Document 05 Scenario C; bd babelstone-t7o3.10). Emitted on entering
/// AWAIT_CORE_CLEARANCE, it asks the Core "was this debit actually executed?" BY REFERENCE — it
/// carries the same opaque deposit and Core hold/txn references the debit used so the ACL can resolve
/// the in-flight operation, never a fresh transaction. A single event-driven query (ADR-IC-003 §P4 —
/// a long wait is a first-class state, never a poll loop), not a poll. No PII — both fields are
/// structural references.</summary>
public sealed record QueryCoreDebitStatusCommand : CommandPayload
{
    /// <summary>The deposit aggregate reference whose debit is being cleared (the Document 05
    /// "reference: TD-DEP-…" the clearance job queries Core by).</summary>
    public required string DepositRef { get; init; }

    /// <summary>The opaque Core hold reference the indeterminate ConfirmDebit targeted — the SAME
    /// derived reference the debit used, so the clearance query resolves exactly that operation. A
    /// deterministic derived reference, NOT minted here.</summary>
    public required string CoreHoldRef { get; init; }

    /// <inheritdoc />
    public override string CommandType => ConstitutionProcess.QueryCoreDebitStatus;
}
