using Xunit;

namespace Babelstone.Families.CurrentAccount.Application.Tests;

/// <summary>
/// The settlement-facing append-key derivation (ADR-PC-043, the scoped ADR-PC-029 inversion): the
/// /credit and /capture endpoints derive the append command_id from the BODY's economic-intent reference, NOT
/// the HTTP Idempotency-Key. In plain English: the same intent reference always yields the same command_id, so
/// a saga REISSUE (a byte-identical body with a fresh dispatch message_id) collapses at command_dedup to ONE
/// append — this is what makes a redelivered / reissued credit or capture land exactly once.
/// </summary>
public sealed class SettlementIntentKeyTests
{
    [Fact]
    public void The_same_intent_reference_always_derives_the_same_command_id()
    {
        // Deterministic (ADR-PC-010 — no clock, no mint): a reissue with the identical intent reference
        // derives the identical command id, so command_dedup collapses it to one append.
        const string intent = "CREDIT-INTENT-abcdef|maturity";

        Assert.Equal(SettlementIntentKey.Derive(intent), SettlementIntentKey.Derive(intent));
    }

    [Fact]
    public void Different_intent_references_derive_different_command_ids()
    {
        // The credit and the debit(capture) leg for one intent are namespaced by their prefix at the source
        // (CREDIT- vs CORE-HOLD-), so their references differ and hash to DISTINCT command ids — two
        // independent single-sided appends, never collapsed into one.
        var creditKey = SettlementIntentKey.Derive("CREDIT-INTENT-abcdef|maturity");
        var debitKey = SettlementIntentKey.Derive("CORE-HOLD-INTENT-abcdef|maturity");

        Assert.NotEqual(creditKey, debitKey);
    }

    [Fact]
    public void The_derived_command_id_is_a_non_empty_deterministic_hash_of_the_namespace_and_reference()
    {
        // A name-based (SHA-1) UUID over the fixed CaSettlementNamespace + the reference, mirroring the
        // substrate's DeriveV5 construction — never Guid.Empty, and a pure function of the namespace + name (so
        // the SAME reference always maps to the SAME id, which is the exactly-once axis). The version/variant
        // bit-fiddling matches the substrate byte-for-byte; the load-bearing property here is determinism +
        // non-emptiness, not the canonical string layout (which .NET's Guid(byte[]) endianness reorders).
        var key = SettlementIntentKey.Derive("CREDIT-INTENT-abcdef|maturity");

        Assert.NotEqual(Guid.Empty, key);
        Assert.Equal(key, SettlementIntentKey.Derive("CREDIT-INTENT-abcdef|maturity"));
    }

    [Fact]
    public void An_empty_intent_reference_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => SettlementIntentKey.Derive(""));
        Assert.Throws<ArgumentException>(() => SettlementIntentKey.Derive("   "));
    }
}
