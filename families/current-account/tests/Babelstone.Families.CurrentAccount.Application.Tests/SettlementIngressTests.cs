using System.Text.Json;
using Xunit;

namespace Babelstone.Families.CurrentAccount.Application.Tests;

/// <summary>
/// The engine-CA SETTLEMENT INGRESS wire + idempotency commitments (ADR-PC-043). In
/// plain English: the settlement saga POSTs to three fixed paths and the ingress adapts them onto the
/// current-account authorize/capture/credit writers. These pin the two DB-free invariants the adapter rests
/// on: (1) it binds the settlement leg wire — the snake_case account_ref / amount_cents / intent_reference the
/// producers emit, with backward-compatible fall-backs to the leg's own reservation / hold / credit reference;
/// and (2) the exactly-once + hold-linking key is derived DETERMINISTICALLY from the intent reference, so a
/// reissue collapses to one append and the confirm leg reconstructs exactly the hold the reserve leg placed.
/// </summary>
/// <remarks>
/// Docker-free: the wire binding is pure System.Text.Json over the ingress's own <see cref="SettlementLegRequest"/>
/// DTO, and the idempotency-key derivation is the pure, name-based v5 <see cref="SettlementIntentKey.Derive"/>
/// (no clock, no randomness). The impure shell's authorize→capture→credit against the CA writers + the
/// command_dedup collapse is the Testcontainers integration tier (mirroring the sibling capture/credit tests,
/// which likewise pin the decision here and defer the append to the integration lane).
/// </remarks>
public sealed class SettlementIngressTests
{
    // The host's wire policy is SnakeCaseLower, but the DTO pins every field with an explicit
    // [JsonPropertyName], so binding is policy-independent — assert against a plain options set.
    private static readonly JsonSerializerOptions Wire = new();

    [Fact]
    public void The_ingress_binds_the_settlement_leg_wire_account_ref_amount_and_intent()
    {
        // The engine-ca reserve/confirm/credit bodies carry the account GUID as account_ref, integer cents,
        // and the economic-intent reference — the three fields the ingress reads to resolve the destination
        // account, guard the amount, and key idempotency (ADR-PC-043).
        var accountId = Guid.NewGuid();
        var json = $$"""
            {
              "account_ref": "{{accountId}}",
              "amount_cents": 500000,
              "intent_reference": "RSV-abc123",
              "settlement_target": "engine-ca"
            }
            """;

        var request = JsonSerializer.Deserialize<SettlementLegRequest>(json, Wire);

        Assert.NotNull(request);
        Assert.Equal(accountId.ToString(), request!.AccountRef);
        Assert.Equal(500_000L, request.AmountCents);
        Assert.Equal("RSV-abc123", request.IntentReference);
        Assert.Equal("engine-ca", request.SettlementTarget);
    }

    [Fact]
    public void The_ingress_falls_back_to_the_legs_own_reference_when_no_intent_reference_is_threaded()
    {
        // A body built before intent_reference was threaded carries only the leg's own reference — the ingress
        // effective intent key falls back through reservation_ref -> core_hold_ref -> credit_ref, so a
        // pre-threading producer still resolves an exactly-once key.
        var reserve = JsonSerializer.Deserialize<SettlementLegRequest>(
            """{ "account_ref": "x", "amount_cents": 1, "reservation_ref": "RSV-1" }""", Wire);
        Assert.Equal("RSV-1", EffectiveIntent(reserve!));

        var confirm = JsonSerializer.Deserialize<SettlementLegRequest>(
            """{ "account_ref": "x", "amount_cents": 1, "core_hold_ref": "CORE-HOLD-1" }""", Wire);
        Assert.Equal("CORE-HOLD-1", EffectiveIntent(confirm!));

        var credit = JsonSerializer.Deserialize<SettlementLegRequest>(
            """{ "account_ref": "x", "amount_cents": 1, "credit_ref": "CREDIT-1" }""", Wire);
        Assert.Equal("CREDIT-1", EffectiveIntent(credit!));

        // An explicit intent_reference wins over every fall-back.
        var explicitIntent = JsonSerializer.Deserialize<SettlementLegRequest>(
            """{ "account_ref": "x", "amount_cents": 1, "intent_reference": "INT-1", "reservation_ref": "RSV-1" }""",
            Wire);
        Assert.Equal("INT-1", EffectiveIntent(explicitIntent!));
    }

    [Fact]
    public void The_reserve_and_confirm_legs_link_to_the_same_deterministic_hold()
    {
        // The reserve->confirm HOLD LINK: the ingress derives the authorize command_id
        // (and thus the placed hold id, hold-{id:N}) from a hold-namespaced projection of the intent
        // reference — deterministically, no round-trip of the returned hold id. Given the SAME intent
        // reference the reserve and confirm legs carry, both reconstruct the SAME hold id.
        const string intentReference = "RSV-8f14e45fceea467a9ba36b1e6a2c9f01";

        var authorizeCommandIdFromReserve = SettlementIntentKey.Derive("AUTHORIZE-HOLD:" + intentReference);
        var authorizeCommandIdFromConfirm = SettlementIntentKey.Derive("AUTHORIZE-HOLD:" + intentReference);
        Assert.Equal(authorizeCommandIdFromReserve, authorizeCommandIdFromConfirm);
        Assert.Equal($"hold-{authorizeCommandIdFromReserve:N}", $"hold-{authorizeCommandIdFromConfirm:N}");

        // The APPLY key (the capture/credit append command_id) is derived from the bare intent reference, so
        // it is DISTINCT from the authorize/hold key — two independent single-sided appends never collide.
        var applyCommandId = SettlementIntentKey.Derive(intentReference);
        Assert.NotEqual(authorizeCommandIdFromReserve, applyCommandId);

        // Determinism: the apply key is a pure function of the intent reference (a reissue collapses to it).
        Assert.Equal(applyCommandId, SettlementIntentKey.Derive(intentReference));
    }

    // Mirror the ingress's intent-key fall-back order (intent_reference -> reservation_ref -> core_hold_ref ->
    // credit_ref) so the wire-binding assertion is meaningful without exposing the private resolver.
    private static string? EffectiveIntent(SettlementLegRequest r)
    {
        foreach (var candidate in new[] { r.IntentReference, r.ReservationRef, r.CoreHoldRef, r.CreditRef })
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
