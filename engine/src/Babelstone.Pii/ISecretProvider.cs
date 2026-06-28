namespace Babelstone.Pii;

/// <summary>
/// Resolves <b>application / integration</b> credentials — the database connection string
/// today, Redpanda SASL credentials later — from the secret boundary. This is the
/// deliberate second mode of OpenBao usage recorded in ADR-PC-004 <i>Amendment A1</i>
/// (2026-05-31), <b>distinct</b> from <see cref="IPiiKeyStore"/>:
/// <list type="bullet">
///   <item><see cref="IPiiKeyStore"/> is per-subject <i>transit</i> keys — key material
///   stays at the boundary and the engine never holds a key (ADR-PC-004).</item>
///   <item><see cref="ISecretProvider"/> is <i>KV</i> application secrets — the engine
///   <b>does</b> hold the resolved credential in memory to open connections.</item>
/// </list>
/// They are separate abstractions because the trust models differ; they are not unified.
/// </summary>
/// <remarks>
/// A resolved credential lives only at the composition root: it is NEVER carried by a
/// saga message (ADR-IC-003 — saga messages carry the identity trio only) nor placed
/// on the durable integration bus (ADR-PC-004 / the PII-bus rule).
///
/// <para><b>Rotation contract.</b> Credential rotation is a KV v2 <i>version bump</i> in
/// the store followed by <see cref="RefreshAsync"/> here — the inverse of transit
/// crypto-shredding, which <i>destroys</i> a key. <see cref="RefreshAsync"/> re-resolves
/// the latest version and invalidates any cached value so the next reconnect picks up the
/// rotated secret; it must never break a live reconnect.</para>
/// </remarks>
public interface ISecretProvider
{
    /// <summary>
    /// Resolves the secret named <paramref name="name"/>, returning its current value.
    /// Throws <see cref="SecretProviderException"/> if the secret is missing or empty.
    /// </summary>
    Task<string> GetSecretAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Re-resolves the secret named <paramref name="name"/> after a rotation (KV v2 version
    /// bump), invalidating any cached value and returning the latest. Reconnect paths call
    /// this so a rotated credential is picked up without a restart.
    /// </summary>
    Task<string> RefreshAsync(string name, CancellationToken ct = default);
}
