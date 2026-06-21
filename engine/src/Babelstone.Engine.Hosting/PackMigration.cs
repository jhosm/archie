using System.Security.Cryptography;
using System.Text;
using Babelstone.Engine;
using Babelstone.EventStore;

namespace Babelstone.Engine.Hosting;

/// <summary>
/// The operator pack-migration write-path (ADR-PC-009 §P3, surface §3.6), generic over ANY family
/// projection <typeparamref name="TState"/> so it stays FAMILY-AGNOSTIC (ADR-PC-021 §A9/§A11). In plain
/// English: a live instance is locked for life to the regulatory pack it was opened under, and the ONLY
/// sanctioned way to move it to a newer pack — when a regulator forces a retroactive change — is this
/// explicit, audited operator migration. The operator names a target instance set, a <c>from</c>→<c>to</c>
/// pack pair, and a migration id; this service re-pins each matched instance by appending one
/// engine-declared <see cref="PackVersionMigrated"/> event, pinned (on the envelope) to the new pack.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hosting library, not the engine kernel and not a family decider.</b> The mechanics here run NO
/// family domain logic — no money math, no rate/pack resolution, no product rules. They are pure
/// structural plumbing for an engine-declared event: read the head pin, append a no-op
/// <see cref="PackVersionMigrated"/> pinned to the target pack. It names no family, so it is NOT the
/// family-owned command-side decider ADR-PC-021 §D1 keeps in the family Application layer; but it IS a
/// command-side write-path that <em>consumes</em> the engine's append spine (a port consumer, not a
/// port), so it does NOT belong in the family-agnostic engine kernel either (ADR-PC-021 §D1/§C rejected
/// the decider-in-the-generic-engine placement). Its home is <c>Babelstone.Engine.Hosting</c> — the
/// non-spine assembly ADR-PC-021 §A9/§A11 created for exactly this family-agnostic host/command-side
/// plumbing, beside the <see cref="PackMigrationsEndpoints"/> operator surface. A family closes the
/// generic by resolving <c>PackMigrationService&lt;ItsState&gt;</c> in its host module and exposing the
/// operator surface through <c>PackMigrationsEndpoints.Map&lt;ItsState&gt;</c>.
/// </para>
/// <para>
/// <b>Re-pin lives on the ENVELOPE.</b> The migration event is appended with
/// <c>AppendContext.PackVersion = to_pack_version</c>, so the event itself and every event after it
/// carry the new pin while prior history stays pinned to <c>from_pack_version</c> (ADR-PC-009 §P1/§P3 —
/// the pin is a per-event fact; the migration boundary is intrinsic to the stream). The projection fold
/// is a no-op (the engine-owned <see cref="PackVersionMigratedHandler{TState}"/>); nothing about the
/// instance's money/lifecycle changes here.
/// </para>
/// <para>
/// <b>Previewable before emission</b> (ADR-PC-009 Residual-risks: a wrong filter could re-pin the wrong
/// instances). <see cref="PreviewAsync"/> returns the matched set — instances whose CURRENT pin equals
/// <c>from_pack_version</c> — without appending anything, so the operator confirms the affected set
/// first. An instance not currently on <c>from_pack_version</c> (already migrated, or never on it) is
/// SKIPPED, never re-pinned, so re-issuing the same migration is a no-op on the already-migrated ones.
/// </para>
/// <para>
/// <b>Idempotent per (migration, instance)</b> (ADR-PC-009 §P3: <c>migration_id</c> is the dedupe key;
/// ADR-PC-029 slot 4). Each per-instance append carries a deterministic command id derived from
/// <c>(migration_id, instance_id)</c>, so an at-least-once retry of the whole migration replays the
/// original per-instance outcome rather than appending a second <see cref="PackVersionMigrated"/>.
/// </para>
/// <para>
/// <b>Per-family runtime, engine-declared event.</b> The event is engine-owned and family-agnostic, but
/// it is appended to a concrete family stream through that family's <see cref="AggregateRuntime{TState}"/>
/// (which knows the family + schema_version pins). The instance set is an EXPLICIT id list — the sound
/// minimal filter. A predicate <c>instance_filter</c> (e.g. <c>{ product_family, currently_active }</c>,
/// surface §3.6) resolved over the read model is a follow-up: it needs a cross-stream query the family
/// read model owns, out of this write-path's scope.
/// </para>
/// </remarks>
/// <typeparam name="TState">The family's folded projection state the migration's no-op fold runs over.</typeparam>
public sealed class PackMigrationService<TState>(AggregateRuntime<TState> runtime, IEventStore store)
{
    /// <summary>
    /// The matched set for a migration WITHOUT emitting anything: the instances (of those supplied)
    /// whose current pin equals <paramref name="fromPackVersion"/>. An instance with no events, or
    /// pinned to a different version, is excluded — so the operator sees exactly what
    /// <see cref="MigrateAsync"/> would re-pin.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> PreviewAsync(
        string fromPackVersion, IReadOnlyList<Guid> instanceIds, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(fromPackVersion);
        ArgumentNullException.ThrowIfNull(instanceIds);

        var matched = new List<Guid>();
        foreach (var instanceId in instanceIds)
        {
            var head = await HeadEnvelopeAsync(instanceId, ct);
            // Match only instances CURRENTLY pinned to the from-version — never re-pin one that is on a
            // different (e.g. already-migrated) pack, so the migration is targeted and idempotent.
            if (head is not null && string.Equals(head.PackVersion, fromPackVersion, StringComparison.Ordinal))
            {
                matched.Add(instanceId);
            }
        }

        return matched;
    }

    /// <summary>
    /// Re-pin each matched instance: append one <see cref="PackVersionMigrated"/> per instance whose
    /// current pin is <paramref name="fromPackVersion"/>, pinned (on the envelope) to
    /// <paramref name="toPackVersion"/>. Records <paramref name="operatorActor"/> and
    /// <paramref name="migrationId"/> on each event so the affected set is fully auditable. Returns the
    /// instances actually re-pinned (the same set <see cref="PreviewAsync"/> reports), so a re-run that
    /// finds them already migrated re-pins none.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> MigrateAsync(
        string fromPackVersion,
        string toPackVersion,
        IReadOnlyList<Guid> instanceIds,
        string migrationId,
        string operatorActor,
        DateTimeOffset migratedAt,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(fromPackVersion);
        ArgumentException.ThrowIfNullOrEmpty(toPackVersion);
        ArgumentException.ThrowIfNullOrEmpty(migrationId);
        ArgumentException.ThrowIfNullOrEmpty(operatorActor);
        ArgumentNullException.ThrowIfNull(instanceIds);

        var migrated = new List<Guid>();
        foreach (var instanceId in instanceIds)
        {
            var head = await HeadEnvelopeAsync(instanceId, ct);

            // Skip an instance not currently on the from-version (already migrated, or never on it): a
            // wrong/duplicate filter never re-pins it. This is what makes re-issuing the migration safe.
            if (head is null || !string.Equals(head.PackVersion, fromPackVersion, StringComparison.Ordinal))
            {
                continue;
            }

            var migration = new PackVersionMigrated(
                InstanceId: instanceId,
                FromPackVersion: fromPackVersion,
                ToPackVersion: toPackVersion,
                MigrationId: migrationId,
                OperatorActor: operatorActor);

            // The re-pin: append at the current head, with the AppendContext pinned to the TARGET pack —
            // so this event and every later event carry to_pack_version on the envelope (ADR-PC-009
            // §P1/§P3). The family + schema_version pins are carried forward from the existing head
            // (the migration moves only the PACK pin, never the schema pin). Idempotent on the
            // deterministic (migration_id, instance_id) command id (ADR-PC-029 slot 4): an at-least-once
            // retry replays rather than appends twice.
            var context = new AppendContext(
                Family: head.Family,
                PackVersion: toPackVersion,
                SchemaVersion: head.SchemaVersion,
                Actor: operatorActor,
                ValidTime: migratedAt,
                CommandId: DeterministicCommandId(migrationId, instanceId));

            try
            {
                await runtime.AppendAsync(instanceId, head.SequenceNumber, [migration], context, ct);
                migrated.Add(instanceId);
            }
            catch (DuplicateCommandException)
            {
                // This exact (migration, instance) was already applied — a benign retry. The instance is
                // already re-pinned; count it as migrated (the operator's set is satisfied) without a
                // second append.
                migrated.Add(instanceId);
            }
        }

        return migrated;
    }

    /// <summary>The head (latest) envelope of a stream, or null when the stream has no events.</summary>
    private async Task<EventEnvelope?> HeadEnvelopeAsync(Guid streamId, CancellationToken ct)
    {
        EventEnvelope? head = null;
        await foreach (var envelope in store.LoadAsync(streamId, fromSequence: 0, ct))
        {
            head = envelope; // the store streams in sequence order; the last one is the head
        }

        return head;
    }

    // A fixed namespace GUID for pack-migration command ids — an arbitrary, stable constant distinct
    // from other deterministic id spaces (e.g. the renewal new-deposit space), so a migration command
    // id cannot collide with another command's.
    private static readonly Guid PackMigrationCommandNamespace =
        Guid.Parse("b1be1570-0000-5e1f-e317-000000000009");

    /// <summary>
    /// A deterministic command id for the per-instance append, derived from <c>(migration_id,
    /// instance_id)</c> so the SAME migration re-applied to the SAME instance dedupes (ADR-PC-029 slot 4
    /// / ADR-PC-009 §P3 migration_id dedupe). A v5-style namespaced SHA-1 UUID over the two — stable
    /// across retries, distinct per (migration, instance). Pure: no clock, no randomness (the same
    /// inputs always yield the same id), so an at-least-once retry of the whole migration targets the
    /// same dedup receipt. Mirrors the renewal new-deposit-id derivation.
    /// </summary>
    private static Guid DeterministicCommandId(string migrationId, Guid instanceId)
    {
        var namespaceBytes = PackMigrationCommandNamespace.ToByteArray();
        var nameBytes = Encoding.UTF8.GetBytes($"{migrationId}:{instanceId:D}");

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
