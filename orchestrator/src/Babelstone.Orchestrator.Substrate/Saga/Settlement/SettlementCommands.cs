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
    /// <summary>The opaque account reference to reserve against (a token, not an IBAN). The ACL resolves the
    /// real account behind the OpenBao boundary.</summary>
    public required string AccountRef { get; init; }

    /// <summary>The saga-chosen idempotency reference for THIS reservation — a derived, stable reference (the
    /// process id namespaced for the reserve leg), NOT a minted GUID, so the body is byte-stable on re-emit
    /// and the ACL dedups on it.</summary>
    public required string ReservationRef { get; init; }

    /// <inheritdoc />
    public override string CommandType => SettlementProcess.ReserveAccountBalance;
}

/// <summary>Core ACL: convert the hold into a real debit — the IRREVERSIBLE debit leg. Carries the opaque
/// Core hold reference; no PII.</summary>
public sealed record ConfirmDebitCommand : SettlementCommandPayload
{
    /// <summary>The opaque Core hold reference to confirm (e.g. CORE-HOLD-…). Derived purely from the process
    /// id, so a RETRY_PERMITTED reissue out of AWAIT_DEBIT_CLEARANCE presents the SAME reference — the
    /// external_reference the ACL folds into its idempotency key (ADR-IC-012 §P4), so the reissue cannot
    /// double-debit. NOT minted here.</summary>
    public required string CoreHoldRef { get; init; }

    /// <inheritdoc />
    public override string CommandType => SettlementProcess.ConfirmDebit;
}

/// <summary>Core ACL: confirm the credit — the IRREVERSIBLE, confirmation-gated credit leg (the NEW generic
/// credit command, ADR-PC-032 / feature-design §8). Carries the opaque account reference the credit lands on
/// and a derived credit reference the ACL dedups on. No PII.</summary>
public sealed record ConfirmCreditCommand : SettlementCommandPayload
{
    /// <summary>The opaque account reference the value enters (a token, not an IBAN). For a credit, the
    /// <c>Movement.Direction</c> is relative to THIS account: <c>Credit</c> = value enters it.</summary>
    public required string AccountRef { get; init; }

    /// <summary>The saga-chosen idempotency reference for THIS credit — derived from the process id, stable
    /// across re-emit (a not-executed credit-clearance reissues with the SAME reference, so the ACL's
    /// idempotency key prevents a double-credit). NOT minted here.</summary>
    public required string CreditRef { get; init; }

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
