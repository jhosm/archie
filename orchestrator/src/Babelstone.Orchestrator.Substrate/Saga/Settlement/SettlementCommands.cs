using System.Text.Json;
using System.Text.Json.Serialization;

namespace Babelstone.Orchestrator.Saga.Settlement;

/// <summary>
/// The LOGICAL payload bodies of the settlement commands the substrate-owned <see cref="SettlementProcess"/>
/// saga emits to the Core ACL (ADR-IC-003 §P1; ADR-PC-032 / feature-design money-movement-settlement §8).
/// One record per command name on <see cref="SettlementProcess"/>. The account-generic debit bodies
/// (reserve / confirm-debit / debit-clearance) are RELOCATED from the term-deposit
/// <c>ConstitutionProcessCommands</c> — they were always account-generic, not deposit-specific — and the
/// credit bodies (confirm-credit / credit-clearance) are NEW.
/// </summary>
/// <remarks>
/// <para>
/// <b>Family-agnostic by construction (the narrowed ORCH-2 allow-list).</b> Every field is a GENERIC,
/// account-level reference — an opaque <c>AccountRef</c>, a derived reservation/hold/txn reference — never a
/// deposit/loan-typed shape. That is what lets these live in the substrate beside the saga (ADR-IC-018
/// Amendment 2026-06-24): the settlement command surface names no family.
/// </para>
/// <para>
/// <b>Replay-stable bytes (ADR-PC-010 §P5).</b> A command body is a pure function of the saga facts in scope
/// — the <see cref="ProcessId"/>, the identity trio, and structural REFERENCES. It contains NO freshly
/// minted GUID and NO wall-clock timestamp: emitting the same logical command twice yields byte-identical
/// bodies. Any GUID here is a CAUSATION reference (an id that already exists), never <c>Guid.NewGuid()</c>.
/// </para>
/// <para>
/// <b>No PII (ADR-PC-004 §P2 / no-PII-on-the-durable-bus).</b> Every field is a structural reference — a
/// process id, a correlation/causation id, an opaque account/hold/txn reference. NEVER a raw IBAN, NIF,
/// name, or amount-bearing identity.
/// </para>
/// </remarks>
[JsonDerivedType(typeof(ReserveAccountBalanceCommand), typeDiscriminator: SettlementProcess.ReserveAccountBalance)]
[JsonDerivedType(typeof(ConfirmDebitCommand), typeDiscriminator: SettlementProcess.ConfirmDebit)]
[JsonDerivedType(typeof(ConfirmCreditCommand), typeDiscriminator: SettlementProcess.ConfirmCredit)]
[JsonDerivedType(typeof(QueryCoreDebitStatusCommand), typeDiscriminator: SettlementProcess.QueryCoreDebitStatus)]
[JsonDerivedType(typeof(QueryCoreCreditStatusCommand), typeDiscriminator: SettlementProcess.QueryCoreCreditStatus)]
public abstract record SettlementCommandPayload
{
    /// <summary>The saga instance this command belongs to (the PROC-… reference). Structural, PII-free.</summary>
    public required Guid ProcessId { get; init; }

    /// <summary>The triggering event's message id — the CAUSATION reference (ADR-IC-003 §P7). An id that
    /// ALREADY EXISTS, carried through; NEVER a freshly minted GUID.</summary>
    public required Guid CausationMessageId { get; init; }

    /// <summary>The originating request's correlation reference, carried UNCHANGED through the saga
    /// (ADR-IC-003 §P7). Null only for a saga started without one. A reference, not a minted value.</summary>
    public Guid? CorrelationId { get; init; }

    /// <summary>The command NAME this payload is the body of — the same constant the
    /// <see cref="SettlementProcess"/> transition table emits.</summary>
    [JsonIgnore]
    public abstract string CommandType { get; }

    /// <summary>The byte-stable serialization options for command bodies (ADR-PC-010 §P5): no indentation, a
    /// fixed property order (source declaration order), no culture-sensitive formatting. Nothing injects a
    /// clock or a GUID — the SAME logical command serializes to the SAME bytes every time.</summary>
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Serialize this command body to its replay-stable bytes. PURE: a function of the record's
    /// fields alone — no clock, no GUID minting, no ambient state (ADR-PC-010 §P5).</summary>
    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes<SettlementCommandPayload>(this, SerializerOptions);
}

/// <summary>Core ACL: place the reversible balance hold (the debit path's reversible leg). Carries the
/// opaque account reference to hold against — NEVER the raw IBAN — and a derived reservation reference. No
/// PII.</summary>
public sealed record ReserveAccountBalanceCommand : SettlementCommandPayload
{
    /// <summary>The opaque account reference to reserve against (a token, not an IBAN). On an engine-CA leg
    /// this is the promoted customer conta-à-ordem account_ref the engine-CA authorize WRITER reads as the
    /// destination (ADR-PC-043); on a legacy leg the ACL resolves the real account behind
    /// the OpenBao boundary.</summary>
    [JsonPropertyName("account_ref")]
    public required string AccountRef { get; init; }

    /// <summary>The saga-chosen idempotency reference for THIS reservation — a derived, stable reference (the
    /// process id namespaced for the reserve leg), NOT a minted GUID, so the body is byte-stable on re-emit
    /// and the ACL dedups on it. Also the HOLD-LINKING key the engine-CA ingress derives the authorize hold
    /// from, so the confirm leg's <c>intent_reference</c> captures exactly it.</summary>
    [JsonPropertyName("reservation_ref")]
    public required string ReservationRef { get; init; }

    /// <summary>The shared HOLD-LINKING + exactly-once reference: equal to
    /// <see cref="ReservationRef"/>, so the engine-CA ingress reconstructs the SAME deterministic authorize
    /// hold on the reserve and confirm legs. Snake_case on the ingress wire. Optional so a body built before
    /// this field is unchanged — the ingress falls back to <see cref="ReservationRef"/> when it is null.</summary>
    [JsonPropertyName("intent_reference")]
    public string? IntentReference { get; init; }

    /// <summary>The amount to HOLD in integer cents — exactly the promoted source <c>Movement.Amount</c> the
    /// engine-CA authorize WRITER sizes the reversible hold to (ADR-PC-043 slot 1, bd babelstone-u79p.22). The
    /// engine-CA authorize ingress REQUIRES a positive amount to place a hold, so an <c>engine-ca</c> reserve
    /// that omits it is a 400 — the saga never gets past the reversible leg. Carries the SAME value as the
    /// capture leg's <see cref="ConfirmDebitCommand.AmountCents"/> on an engine-ca leg (the type differs — this
    /// is nullable, that is required — but the amount agrees), so reserve and confirm settle the same cents.
    /// Money-as-integer-cents on the wire (ADR-PC-010), never a float; a value reference, never PII. Null on a
    /// legacy leg (the legacy ACL sizes the hold from the reservation): the field serializes as an explicit
    /// null there; the logical command and its replay-stability are unchanged.</summary>
    [JsonPropertyName("amount_cents")]
    public long? AmountCents { get; init; }

    /// <summary>The settlement COUNTERPARTY this reserve routes to (ADR-PC-043 slot 1, bd babelstone-u79p.22).
    /// The dispatcher's <c>ProjectSettlementTargetHeader</c> reads THIS body field and flips the router's base
    /// URL: <c>engine-ca</c> → the engine-owned CA authorize ingress; <c>null</c> (the default) → the
    /// LEGACY-DDA counterparty, so a legacy reserve keeps its routing UNCHANGED. Set from the source family's
    /// promoted <c>ce_settlementtarget</c> header, forwarded untouched (never re-derived). A closed-enum
    /// routing token, never PII. Mirrors <see cref="ConfirmDebitCommand.SettlementTarget"/> — both debit legs
    /// must route to the SAME counterparty, so the hold the reserve places is the hold the confirm captures.</summary>
    [JsonPropertyName("settlement_target")]
    public string? SettlementTarget { get; init; }

    /// <inheritdoc />
    public override string CommandType => SettlementProcess.ReserveAccountBalance;
}

/// <summary>Core ACL: convert the hold into a real debit — the IRREVERSIBLE debit leg. Carries the opaque
/// Core hold reference; no PII.</summary>
public sealed record ConfirmDebitCommand : SettlementCommandPayload
{
    /// <summary>The opaque Core hold reference to confirm (e.g. CORE-HOLD-…). For an engine-CA leg this is the
    /// ECONOMIC-INTENT-derived reference (ADR-PC-043 slot 4) — the CA-apply <c>command_id</c> derives from the
    /// body's <c>IntentId</c>, NOT the HTTP Idempotency-Key — so a saga reissue (byte-identical body, fresh
    /// dispatch <c>message_id</c>) presents the SAME reference and collapses at <c>command_dedup</c> to one
    /// append. For the legacy-DDA leg it is the process-id-derived reference (unchanged). NOT minted here.</summary>
    [JsonPropertyName("core_hold_ref")]
    public required string CoreHoldRef { get; init; }

    /// <summary>The promoted DESTINATION account_ref the captured debit lands on (ADR-PC-043).
    /// On an engine-CA leg the customer's real conta-à-ordem account_ref the
    /// engine-CA capture WRITER reads; substrate-forwarded untouched. Optional (null on the legacy-DDA path /
    /// the pre-promotion default), so a body built before this field is unchanged.</summary>
    [JsonPropertyName("account_ref")]
    public string? AccountRef { get; init; }

    /// <summary>The shared HOLD-LINKING key: equal to the reserve leg's
    /// <c>reservation_ref</c>, so the engine-CA ingress captures exactly the hold the reserve's authorize
    /// placed (<c>target_hold_id = f(intent_reference)</c>). Optional; the ingress falls back to
    /// <see cref="CoreHoldRef"/> when null. Snake_case on the ingress wire.</summary>
    [JsonPropertyName("intent_reference")]
    public string? IntentReference { get; init; }

    /// <summary>The amount to land, in integer cents (ADR-PC-043 slot 1) — exactly the source
    /// <c>Movement.Amount</c>. The only in-band guard against <c>WRONG-AMOUNT</c>, which every identity-keyed
    /// dedup misses. Money-as-integer-cents on the wire (ADR-PC-004 / ADR-PC-010), never a float; a reference,
    /// never PII. The substrate carries it as a bare <c>long</c> (it does not reference the engine's
    /// <c>Money</c> type — the extraction-ready boundary, ADR-PC-019 §P2); the receiver re-hydrates
    /// <c>Money</c>.</summary>
    [JsonPropertyName("amount_cents")]
    public required long AmountCents { get; init; }

    /// <summary>The settlement COUNTERPARTY this debit routes to (ADR-PC-043 slot 1, bd babelstone-u79p.22).
    /// The dispatcher's <c>ProjectSettlementTargetHeader</c> reads THIS body field and flips the router's base
    /// URL: <c>engine-ca</c> → the engine-owned CA capture ingress; <c>null</c> (the default) → the LEGACY-DDA
    /// counterparty, so a legacy debit keeps its routing UNCHANGED. Set from the source family's promoted
    /// <c>ce_settlementtarget</c> header — forward-propagated across the reserve→confirm hop on the synthesized
    /// result event (the debit-path fix bd babelstone-u79p.22), never re-derived. A closed-enum routing token,
    /// never PII. Mirrors <see cref="ConfirmCreditCommand.SettlementTarget"/> and
    /// <see cref="ReserveAccountBalanceCommand.SettlementTarget"/>.</summary>
    [JsonPropertyName("settlement_target")]
    public string? SettlementTarget { get; init; }

    /// <inheritdoc />
    public override string CommandType => SettlementProcess.ConfirmDebit;
}

/// <summary>Core ACL: confirm the credit — the IRREVERSIBLE, confirmation-gated credit leg (the NEW generic
/// credit command, ADR-PC-032 / feature-design §8). Carries the opaque account reference the credit lands on
/// and a derived credit reference the ACL dedups on. No PII.</summary>
public sealed record ConfirmCreditCommand : SettlementCommandPayload
{
    /// <summary>The opaque account reference the value enters (a token, not an IBAN). For a credit, the
    /// <c>Movement.Direction</c> is relative to THIS account: <c>Credit</c> = value enters it. On an engine-CA
    /// leg the promoted customer conta-à-ordem account_ref the engine-CA credit WRITER lands on
    /// (ADR-PC-043).</summary>
    [JsonPropertyName("account_ref")]
    public required string AccountRef { get; init; }

    /// <summary>The saga-chosen idempotency reference for THIS credit. For an engine-CA leg it is the
    /// ECONOMIC-INTENT-derived reference (ADR-PC-043 slot 4) — the CA-apply <c>command_id</c> derives from the
    /// body's <c>IntentId</c>, NOT the HTTP Idempotency-Key — so a reissue (byte-identical body, fresh dispatch
    /// <c>message_id</c>) presents the SAME reference and the CA's single-guard <c>command_dedup</c> collapses
    /// it to one credit append. For the legacy-DDA leg it is the process-id-derived reference (unchanged). NOT
    /// minted here.</summary>
    [JsonPropertyName("credit_ref")]
    public required string CreditRef { get; init; }

    /// <summary>The exactly-once reference the engine-CA ingress derives the credit's append command_id from:
    /// equal to <see cref="CreditRef"/>. Optional; the ingress falls back to
    /// <see cref="CreditRef"/> when null. Snake_case on the ingress wire.</summary>
    [JsonPropertyName("intent_reference")]
    public string? IntentReference { get; init; }

    /// <summary>The amount to land, in integer cents (ADR-PC-043 slot 1) — exactly the source
    /// <c>Movement.Amount</c>. The only in-band guard against <c>WRONG-AMOUNT</c>. Money-as-integer-cents on
    /// the wire (ADR-PC-004 / ADR-PC-010), never a float; a reference, never PII. Carried as a bare
    /// <c>long</c> so the substrate does not reference the engine's <c>Money</c> type (ADR-PC-019 §P2).</summary>
    [JsonPropertyName("amount_cents")]
    public required long AmountCents { get; init; }

    /// <summary>The settlement COUNTERPARTY this credit routes to (ADR-PC-043 slot 1). The dispatcher's
    /// <c>ProjectSettlementTargetHeader</c> reads THIS body field and flips the router's base URL:
    /// <c>engine-ca</c> → the engine-owned CA credit ingress; <c>null</c> (the default) → the LEGACY-DDA
    /// counterparty, so a legacy credit keeps its routing UNCHANGED. Set from the source family's promoted
    /// <c>ce_settlementtarget</c> header, forwarded untouched (never re-derived). A closed-enum routing token,
    /// never PII. Mirrors the <c>settlement_target</c> field the constitution funding commands carry.</summary>
    [JsonPropertyName("settlement_target")]
    public string? SettlementTarget { get; init; }

    /// <inheritdoc />
    public override string CommandType => SettlementProcess.ConfirmCredit;
}

/// <summary>Core ACL: query the Core for the actual outcome of an INDETERMINATE debit — the v1 clearance
/// mechanism (ADR-IC-012 §P5). Carries the same opaque hold reference the indeterminate debit used, so the
/// query resolves exactly that in-flight operation. No PII.</summary>
public sealed record QueryCoreDebitStatusCommand : SettlementCommandPayload
{
    /// <summary>The opaque Core hold reference the indeterminate ConfirmDebit targeted — the SAME derived
    /// reference, so the clearance query resolves exactly that operation. Deterministic, NOT minted.</summary>
    public required string CoreHoldRef { get; init; }

    /// <inheritdoc />
    public override string CommandType => SettlementProcess.QueryCoreDebitStatus;
}

/// <summary>Core ACL: query the Core for the actual outcome of an INDETERMINATE credit — the credit
/// counterpart of <see cref="QueryCoreDebitStatusCommand"/> (the new credit-clearance surface). Carries the
/// same opaque credit reference the indeterminate confirm used. No PII.</summary>
public sealed record QueryCoreCreditStatusCommand : SettlementCommandPayload
{
    /// <summary>The opaque credit reference the indeterminate ConfirmCredit targeted — the SAME derived
    /// reference, so the clearance query resolves exactly that operation. Deterministic, NOT minted.</summary>
    public required string CreditRef { get; init; }

    /// <inheritdoc />
    public override string CommandType => SettlementProcess.QueryCoreCreditStatus;
}
