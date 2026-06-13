using System.Text.Json.Serialization;

namespace Babelstone.Orchestrator.Edge;

/// <summary>
/// The edge's constitution-request body (ADR-IC-006 §P4 / Document 05 §Step 0
/// <c>POST /api/v1/deposits/constitute</c>). The edge STARTS the saga from this; it is NOT a
/// direct engine append (PR #149's rejected anti-pattern).
/// </summary>
/// <remarks>
/// <para>
/// <b>References, not PII (ADR-PC-004 §P2 / no-PII-on-the-durable-bus).</b> This DTO deliberately
/// carries OPAQUE account REFERENCES (<c>source_account_ref</c>), not raw IBANs/NIFs. Document 05's
/// illustrative body shows a raw <c>PT50…</c> IBAN at the synchronous edge for tangibility; the
/// engine's PII boundary tokenises an account at ingestion (the engine's <c>IPiiProtector</c> /
/// OpenBao boundary), and the saga and its durable <c>saga_outbox</c> commands carry only the
/// resulting references. The orchestrator never persists or forwards a raw account identifier, so
/// the edge accepts the already-tokenised reference — keeping no PII on any saga row or the bus.
/// </para>
/// <para>
/// <b><c>client_id</c> is in the BODY</b> for the constitution request (Document 05 §Step 0 body) —
/// it names the owning client the saga is started for. The SSE read enforces the SAME client against
/// the gateway-attested caller (<see cref="EdgeAuth"/>). An opaque business reference, not PII.
/// </para>
/// </remarks>
public sealed record ConstituteRequest
{
    /// <summary>The owning client this deposit is constituted for (e.g. <c>CLI-2026-007842</c>).
    /// Persisted as the saga's owning client; the SSE read enforces ownership against it.</summary>
    [JsonPropertyName("client_id")]
    public string? ClientId { get; init; }

    /// <summary>The product code being constituted (e.g. <c>TD-TRAD-12M</c>). A catalogue
    /// reference, not PII.</summary>
    [JsonPropertyName("product_code")]
    public string? ProductCode { get; init; }

    /// <summary>The deposit principal in integer cents (the engine's money discipline). A
    /// structural amount on the request, never persisted to a saga row or the bus.</summary>
    [JsonPropertyName("amount")]
    public long Amount { get; init; }

    /// <summary>The OPAQUE source-account reference to reserve/debit against — a token the engine's
    /// PII boundary already issued, NOT a raw IBAN (ADR-PC-004 §P2).</summary>
    [JsonPropertyName("source_account_ref")]
    public string? SourceAccountRef { get; init; }

    /// <summary>The OPAQUE interest-account reference — a token, NOT a raw IBAN.</summary>
    [JsonPropertyName("interest_account_ref")]
    public string? InterestAccountRef { get; init; }
}
