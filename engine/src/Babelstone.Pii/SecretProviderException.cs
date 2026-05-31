namespace Babelstone.Pii;

/// <summary>
/// An application-credential resolution failed through an <see cref="ISecretProvider"/> —
/// a missing/empty secret, an AppRole login failure, a sealed or misconfigured store, or
/// a transport error. The message NEVER contains the secret value: only the secret's
/// logical name, its path, and the provider's error detail. Distinct from
/// <see cref="PiiKeyStoreException"/> (the per-subject transit-key boundary): a credential
/// secret is held by the engine, a transit key never is.
/// </summary>
public sealed class SecretProviderException(string message) : Exception(message);
