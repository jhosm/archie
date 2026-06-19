namespace Babelstone.Pii;

/// <summary>
/// The identity <see cref="IPiiKeyStore"/> — the no-OpenBao default, mirroring
/// <c>NullPiiProtector</c>'s role in the engine spine. It holds no real key material: because no PII
/// field is annotated/encrypted yet (the <c>NullPiiProtector</c> posture, ADR-PC-004 §P1, Epic C
/// pending), there is nothing to crypto-shred, so <see cref="DestroyKeyAsync"/> is a structural no-op
/// and <see cref="DecryptAsync"/> returns the ciphertext bytes unchanged.
///
/// <para>It exists so the GDPR right-to-be-forgotten flow (bd babelstone-nzw6) is wired and exercisable
/// end-to-end in local dev — the erasure endpoint calls <see cref="DestroyKeyAsync"/> and the lifecycle
/// folds to Erased — without standing up OpenBao. A deployment swaps in <see cref="OpenBaoTransitClient"/>
/// (the real per-subject transit keys) with NO code change: both implement the same seam, and the host
/// composition root picks one by <c>OpenBao:Enabled</c>. The behaviour difference is deliberate and
/// safe: with no real key, "erase" is already a no-op on data that does not exist.</para>
/// </summary>
public sealed class NullPiiKeyStore : IPiiKeyStore
{
    /// <summary>Identity: no key, nothing to encrypt — returns the plaintext bytes unchanged.</summary>
    public Task<byte[]> EncryptAsync(string subjectId, byte[] plaintext, CancellationToken ct = default)
        => Task.FromResult(plaintext);

    /// <summary>Identity: returns the ciphertext bytes unchanged (no real key to decrypt under).</summary>
    public Task<byte[]?> DecryptAsync(string subjectId, byte[] ciphertext, CancellationToken ct = default)
        => Task.FromResult<byte[]?>(ciphertext);

    /// <summary>
    /// Crypto-shred no-op: with no real key there is nothing to destroy (ADR-PC-004 §P3). Idempotent by
    /// construction. The erasure AUDIT FACT is still recorded by the caller's append, so the deposit
    /// folds to Erased exactly as it would with the real transit-key store.
    /// </summary>
    public Task DestroyKeyAsync(string subjectId, CancellationToken ct = default) => Task.CompletedTask;
}
