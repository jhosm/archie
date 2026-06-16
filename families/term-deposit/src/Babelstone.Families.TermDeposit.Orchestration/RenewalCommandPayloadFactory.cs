using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Babelstone.Families.TermDeposit.Orchestration;

/// <summary>
/// Assembles the typed, byte-stable command payloads the <see cref="RenewalProcess"/> saga emits, and
/// derives the renewed instance's deposit id DETERMINISTICALLY from the saga's process id (bd
/// babelstone-mtto; the renewal counterpart of <see cref="SagaCommandPayloadFactory"/>). The bodies
/// serialize to the engine's <c>ConstituteRenewalRequest</c> / <c>LinkRenewalRequest</c> wire shapes
/// (snake_case) that the idempotent renewal endpoints expect.
/// </summary>
/// <remarks>
/// <para>
/// <b>The new deposit id is DETERMINISTICALLY derived, NEVER minted (ADR-PC-010 §P5, crash-safe).</b>
/// <see cref="NewDepositId"/> is a v5-style (SHA-1, namespaced) hash of the saga's process id — NOT
/// <see cref="Guid.NewGuid"/>. That makes the renewal a REPLAYABLE command: a crash between
/// <c>ConstituteRenewal</c> and <c>LinkRenewal</c> re-derives the SAME new deposit id on the reissue, so
/// the constitute leg's idempotency (keyed on the saga_outbox row id) replays the original 201 and the
/// link leg targets the correct new stream. Mirrors <c>SagaSelfEmit</c>'s deterministic id
/// derivation. The derivation reads no clock and no randomness.
/// </para>
/// <para>
/// <b>The body is MINIMAL — the engine resolves EVERY renewal fact (ADR-PC-009; bd babelstone-mtto.5).</b>
/// The engine's <c>ConstituteRenewalAsync</c> reloads the Matured closing deposit and resolves from its
/// folded state the term, variant, cadence, policy, the rate (for SAME_TERM_CURRENT_RATE) AND the product
/// code, pricing role and funding-account token — now that <c>DepositConstituted</c> persists role +
/// funding alongside the product code. So the orchestrator carries NO product-family knowledge (ADR-IC-003
/// §A7): the body carries ONLY the deterministically-derived new deposit id. <c>renewed_at</c> and
/// <c>actor</c> are DELIBERATELY OMITTED so the engine applies its own defaults (host-stamped instant,
/// <c>saga:renewal</c> actor) — omitting them keeps the body byte-stable (no wall clock, no orchestrator
/// label leaks into a value the sink writes). Money never rides the body (the rolled-over principal is the
/// engine's, off the closing deposit's RemainingPrincipal). PII-free (ADR-PC-004 §P2): a single derived
/// deposit id — never a raw IBAN/NIF/name.
/// </para>
/// </remarks>
public static class RenewalCommandPayloadFactory
{
    // A fixed namespace GUID for renewal new-deposit ids — an arbitrary, stable constant DISTINCT from the
    // self-emit / settlement-result id spaces, so a derived new deposit id cannot collide with any of them.
    private static readonly Guid RenewalDepositNamespace = Guid.Parse("b1be1570-0000-5e1f-e317-000000000003");

    /// <summary>
    /// The renewed instance's deposit id, derived DETERMINISTICALLY from the closing deposit's
    /// <paramref name="processId"/> (a v5-style namespaced SHA-1 UUID). Pure: the same process id always
    /// yields the same new deposit id, with no clock and no randomness — so a crash-recovery reissue
    /// targets the same new stream and the renewal is fully replayable.
    /// </summary>
    public static Guid NewDepositId(Guid processId)
    {
        var namespaceBytes = RenewalDepositNamespace.ToByteArray();
        var nameBytes = Encoding.UTF8.GetBytes(processId.ToString("N"));

        var input = new byte[namespaceBytes.Length + nameBytes.Length];
        Buffer.BlockCopy(namespaceBytes, 0, input, 0, namespaceBytes.Length);
        Buffer.BlockCopy(nameBytes, 0, input, namespaceBytes.Length, nameBytes.Length);

        Span<byte> hash = stackalloc byte[20];
        SHA1.HashData(input, hash);

        Span<byte> guidBytes = stackalloc byte[16];
        hash[..16].CopyTo(guidBytes);

        // Version 5 in the high nibble of byte 6; RFC-4122 variant in the high bits of byte 8.
        guidBytes[6] = (byte)((guidBytes[6] & 0x0F) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);

        return new Guid(guidBytes);
    }

    /// <summary>
    /// Build the byte-stable engine wire body for <paramref name="commandType"/>, or null if there is no
    /// recipe for it (the caller surfaces that as an unroutable command). PURE and byte-stable: no clock,
    /// no GUID minting — the new deposit id is the deterministic derivation, and <c>renewed_at</c> +
    /// <c>actor</c> are DELIBERATELY OMITTED from the body so the engine applies its own defaults (the
    /// endpoint host-stamps a missing RenewedAt and defaults the Actor to <c>saga:renewal</c>). Omitting
    /// them keeps the body byte-stable (a wall clock here would break the re-emission-yields-identical-bytes
    /// property and inject time into a value the sink writes); the renewal valid time being the engine's
    /// apply instant is correct for a v1 immediate renewal.
    /// </summary>
    /// <param name="commandType">The command NAME the state machine decided.</param>
    /// <param name="processId">The closing deposit id (= the saga's process_id, the URL path id).</param>
    public static byte[]? Build(
        string commandType,
        Guid processId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandType);

        var newDepositId = NewDepositId(processId);

        return commandType switch
        {
            RenewalProcess.ConstituteRenewal => JsonSerializer.SerializeToUtf8Bytes(
                new ConstituteRenewalBody(NewDepositId: newDepositId),
                SerializerOptions),
            RenewalProcess.LinkRenewal => JsonSerializer.SerializeToUtf8Bytes(
                new LinkRenewalBody(NewDepositId: newDepositId),
                SerializerOptions),
            _ => null,
        };
    }

    /// <summary>The byte-stable, snake_case serializer PR B's renewal endpoints expect (SnakeCaseLower) —
    /// a FIXED, explicit policy: no indentation, declaration-order properties — so the same logical command
    /// yields identical bytes.</summary>
    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>
    /// The engine's <c>ConstituteRenewalRequest</c> shape mirrored here so the orchestrator emits the
    /// engine wire body WITHOUT a project reference to <c>Babelstone.Engine.Api</c> (extraction-ready,
    /// ADR-PC-019 §P2). The merged minimal contract is <c>{ new_deposit_id, renewed_at?, actor? }</c>; the
    /// body carries ONLY <c>new_deposit_id</c> and omits the two optionals so the engine applies its own
    /// defaults (host-stamped <c>renewed_at</c>, <c>saga:renewal</c> actor) and the body stays byte-stable.
    /// The engine resolves EVERY renewal fact (product code, role, funding, term, …) from the Matured
    /// closing deposit it loads (bd babelstone-mtto.5) — the orchestrator carries no product knowledge. The
    /// URL path carries the closing deposit id (= process_id), supplied by the dispatcher's {process_id}
    /// templating.
    /// </summary>
    private readonly record struct ConstituteRenewalBody(Guid NewDepositId);

    /// <summary>
    /// The engine's <c>LinkRenewalRequest</c> shape mirrored here (extraction-ready, ADR-PC-019 §P2). The
    /// merged minimal contract is <c>{ new_deposit_id, renewed_at?, actor? }</c>; the body carries ONLY
    /// <c>new_deposit_id</c> and omits the two optionals (engine host-stamps the instant, defaults the
    /// actor). The closing deposit id is in the URL path.
    /// </summary>
    private readonly record struct LinkRenewalBody(Guid NewDepositId);
}
