using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Babelstone.Engine.Hosting;

/// <summary>
/// The canonical, SERVER-DERIVED idempotency key for a clock-driven lifecycle command
/// (ADR-PC-036 §Decision 1+3 — the lifecycle-command driver's Layer-1 safe-trigger foundation; LCD-1).
/// <para>
/// In plain English: when something drives the engine on a schedule — a person, the MCP agent, or the
/// automated lifecycle-command driver — it must NOT invent its own retry key. If a manual operator and
/// the driver each paid the SAME loan installment with a DIFFERENT caller-supplied key, the engine could
/// not recognise the two as the same act and would collect twice. This helper computes the ONE key
/// everyone derives the SAME way from the occurrence's own identity, so a repeat is dedupe-able at the
/// engine's <c>command_dedup</c> ledger.
/// </para>
/// <para>
/// The key is a v5-style namespaced SHA-1 UUID over <c>(instance_id, command_kind,
/// stable_occurrence_key)</c>. For a personal-loan installment the occurrence key is the STABLE
/// installment NUMBER, never the due-date (ADR-PC-036 §Decision 3) — so a re-dated or backfilled retry of
/// occurrence N reuses the same key and dedupes to ONE money leg (ADR-PC-029 slot 4,
/// <c>ENGINE_COMMAND_IDEMPOTENT</c>). Pure: no clock, no randomness — the same inputs always yield the
/// same id, so an at-least-once retry targets the same dedup receipt. Mirrors
/// <c>PackMigration.DeterministicCommandId</c>'s derivation style (same assembly), with its own distinct
/// namespace so a lifecycle-command id can never collide with another deterministic command-id space.
/// </para>
/// </summary>
public static class LifecycleCommandKey
{
    // A fixed namespace GUID for lifecycle-command idempotency keys — an arbitrary, stable constant
    // distinct from the pack-migration command-id space (PackMigration.PackMigrationCommandNamespace,
    // …009) and the renewal new-deposit-id space, so a driven lifecycle command id cannot collide with
    // another deterministic command id.
    private static readonly Guid LifecycleCommandNamespace =
        Guid.Parse("b1be1570-0000-5e1f-e317-00000000000c");

    /// <summary>
    /// Derive the deterministic command id for one driven lifecycle occurrence.
    /// </summary>
    /// <param name="instanceId">The aggregate/stream the command mutates (e.g. the loan id).</param>
    /// <param name="commandKind">The stable command kind, e.g. <c>pay_installment</c> — a fixed code, never
    /// caller input, that separates two command spaces on the same aggregate.</param>
    /// <param name="occurrenceKey">The STABLE per-occurrence key — for a recurring installment the
    /// installment NUMBER (never the due-date), so a re-dated retry of occurrence N reuses the same id. A
    /// one-shot lifecycle step (deposit maturity) uses a constant such as <c>1</c>.</param>
    public static Guid Derive(Guid instanceId, string commandKind, long occurrenceKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(commandKind);

        var namespaceBytes = LifecycleCommandNamespace.ToByteArray();
        // Invariant formatting keeps the derivation culture-independent (a plain integer has no
        // separator, but pinning the culture makes the determinism explicit, not incidental).
        var nameBytes = Encoding.UTF8.GetBytes(
            $"{instanceId:D}:{commandKind}:{occurrenceKey.ToString(CultureInfo.InvariantCulture)}");

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
}
