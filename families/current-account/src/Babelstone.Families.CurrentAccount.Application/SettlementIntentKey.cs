using System.Security.Cryptography;
using System.Text;

namespace Babelstone.Families.CurrentAccount.Application;

/// <summary>
/// Derives the settlement-facing append <c>command_id</c> from the body's economic-INTENT reference — the
/// ADR-PC-043 slot-4 rule and the scoped ADR-PC-029 inversion. In plain English: for the
/// engine-owned CA's <c>/credit</c> and <c>/capture</c> endpoints, the exactly-once key is NOT the HTTP
/// Idempotency-Key header (as every other CA endpoint) but a deterministic function of the intent reference
/// carried in the request body — so a saga REISSUE (byte-identical body, a fresh dispatch message_id)
/// collapses at <c>command_dedup</c> to exactly ONE append, and a re-route to the same intent lands once.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deterministic and byte-stable (ADR-PC-010).</b> The command id is a name-based (v5, SHA-1) UUID
/// over a FIXED committed namespace and the intent reference string — no clock, no minted GUID, no
/// randomness — so the SAME intent reference always yields the SAME command id (across process restarts and
/// re-emissions). It mirrors the substrate's own RFC-4122 §4.3 v5 construction (SettlementMovementFanout's
/// <c>DeriveV5</c> / SagaSettlementResultEmit.MessageId) rather than referencing the orchestrator (the
/// family depends only on the engine — ADR-IC-018's family→substrate arrow is never inverted).
/// </para>
/// <para>
/// <b>Distinct leg namespaces.</b> The credit and the debit(capture) leg for one intent MUST get DISTINCT
/// command ids (they are two independent single-sided appends), so each leg's reference is namespaced by its
/// prefix at the source (<c>CREDIT-</c> / <c>CORE-HOLD-</c> via
/// <c>SettlementReferences.DeriveFromIntent</c>); this helper hashes whatever reference string it is handed,
/// so two differently-prefixed references for one intent hash to two ids while a reissue of EITHER leg hashes
/// to that leg's identical id.
/// </para>
/// </remarks>
public static class SettlementIntentKey
{
    /// <summary>
    /// The FIXED, committed namespace GUID the CA settlement command ids are derived under (ADR-PC-043 —
    /// "the <c>CaSettlementNamespace</c> GUID is a fixed committed constant, never regenerated"). Changing it
    /// would re-key every in-flight intent and reopen the replay-into-double floor, so it is a constant, not
    /// config.
    /// </summary>
    public static readonly Guid CaSettlementNamespace = new("b6f0e0d2-9a4c-5f18-8b3a-2e7c1d5a4f90");

    /// <summary>
    /// Derive the append <c>command_id</c> for a settlement leg from its economic-intent reference
    /// (<paramref name="intentReference"/>) — the exactly-once axis. A name-based v5 UUID over
    /// <see cref="CaSettlementNamespace"/> + the reference, so a byte-identical reissue collapses to one
    /// append at command_dedup. NEVER a minted GUID, NEVER the HTTP Idempotency-Key.
    /// </summary>
    /// <param name="intentReference">The ADR-PC-043 slot-4 economic-intent reference string (the
    /// <c>SettlementReferences.DeriveFromIntent</c> token) carried on the request body. Must be non-empty.</param>
    public static Guid Derive(string intentReference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(intentReference);
        return DeriveV5(CaSettlementNamespace, intentReference);
    }

    // RFC-4122 §4.3 name-based (SHA-1) UUID over the namespace + name. Mirrors the substrate's DeriveV5: no
    // clock, no randomness, so a replay reproduces the id exactly (ADR-PC-010).
    private static Guid DeriveV5(Guid namespaceId, string name)
    {
        var namespaceBytes = namespaceId.ToByteArray();
        var nameBytes = Encoding.UTF8.GetBytes(name);

        var input = new byte[namespaceBytes.Length + nameBytes.Length];
        Buffer.BlockCopy(namespaceBytes, 0, input, 0, namespaceBytes.Length);
        Buffer.BlockCopy(nameBytes, 0, input, namespaceBytes.Length, nameBytes.Length);

        Span<byte> hash = stackalloc byte[20];
        SHA1.HashData(input, hash);

        Span<byte> guidBytes = stackalloc byte[16];
        hash[..16].CopyTo(guidBytes);

        // Set the version (5) and RFC-4122 variant bits, exactly as the substrate construction does.
        guidBytes[6] = (byte)((guidBytes[6] & 0x0F) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);

        return new Guid(guidBytes);
    }
}
