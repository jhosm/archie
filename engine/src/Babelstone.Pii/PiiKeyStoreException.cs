namespace Babelstone.Pii;

/// <summary>
/// A key-store operation failed for a reason that is NOT GDPR erasure — corrupt
/// ciphertext, a wrong-subject key, a sealed or misconfigured mount, a denied token,
/// or a transient transport error. Distinct from the erased-key case, which
/// <see cref="IPiiKeyStore.DecryptAsync"/> surfaces as <c>null</c>: a failure must never
/// masquerade as a legitimate erasure (intact PII silently read as gone), nor an erasure
/// as a failure. Callers that expect erasure check for <c>null</c>; everything else throws.
/// </summary>
public sealed class PiiKeyStoreException(string message) : Exception(message);
