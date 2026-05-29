namespace Babelstone.Pii;

/// <summary>
/// The per-subject key boundary (ADR-PC-004 §P2/§P3). Each data subject has a named
/// key; PII is encrypted under it, and GDPR Article 17 erasure is the destruction of
/// that key (<see cref="DestroyKeyAsync"/>) — after which ciphertext is permanently
/// unrecoverable. <see cref="OpenBaoTransitClient"/> is the production implementation.
/// </summary>
public interface IPiiKeyStore
{
    /// <summary>Encrypts <paramref name="plaintext"/> under the subject's key, creating the key if absent.</summary>
    Task<byte[]> EncryptAsync(string subjectId, byte[] plaintext, CancellationToken ct = default);

    /// <summary>
    /// Decrypts under the subject's key. Returns <c>null</c> when the key has been
    /// destroyed — the GDPR-compliant post-erasure state (§P3), not an error.
    /// </summary>
    Task<byte[]?> DecryptAsync(string subjectId, byte[] ciphertext, CancellationToken ct = default);

    /// <summary>
    /// Destroys the subject's key = crypto-shred erasure (§P3). Idempotent: destroying
    /// an already-absent key is a no-op. After this, <see cref="DecryptAsync"/> for the
    /// subject returns null.
    /// </summary>
    Task DestroyKeyAsync(string subjectId, CancellationToken ct = default);
}
