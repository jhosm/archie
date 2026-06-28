namespace Babelstone.Pii;

/// <summary>A cleartext PII field about to be sealed under a subject's key.</summary>
public sealed record PiiField(string Name, byte[] Plaintext);

/// <summary>A sealed PII field; <see cref="Ciphertext"/> is opaque OpenBao transit output.</summary>
public sealed record EncryptedField(string Name, byte[] Ciphertext);

/// <summary>
/// The field-level crypto envelope (ADR-PC-004 / event-store §6.2): seals a set of
/// PII fields under one subject's key and opens them again, returning <c>null</c> for
/// fields whose subject key has been destroyed (= erasure, ADR-PC-004). Structural fields are
/// never PII and never pass through here — they stay cleartext and queryable.
/// </summary>
/// <remarks>
/// Which fields are PII is, in the finished system, declared by the family's CUE schema
/// (ADR-PC-004). Until the CUE schema language (Epic C) and a family module (Epic E)
/// exist, the caller supplies the PII field set explicitly; this envelope is agnostic to
/// where that set comes from. The schema-driven annotation + the CI lint that rejects
/// unannotated string fields are tracked as a follow-up.
/// </remarks>
public interface IPiiEnvelope
{
    Task<IReadOnlyList<EncryptedField>> SealAsync(
        string subjectId, IReadOnlyList<PiiField> fields, CancellationToken ct = default);

    /// <summary>Opens sealed fields; an element is <c>null</c> when the subject key is destroyed.</summary>
    Task<IReadOnlyList<PiiField?>> OpenAsync(
        string subjectId, IReadOnlyList<EncryptedField> fields, CancellationToken ct = default);
}

/// <summary>
/// Drives <see cref="IPiiKeyStore"/> per field. Each field is encrypted under the same
/// subject key, so destroying that one key erases every PII field for the subject at once.
/// </summary>
public sealed class PiiEnvelope(IPiiKeyStore keyStore) : IPiiEnvelope
{
    public async Task<IReadOnlyList<EncryptedField>> SealAsync(
        string subjectId, IReadOnlyList<PiiField> fields, CancellationToken ct = default)
    {
        var sealed_ = new List<EncryptedField>(fields.Count);
        foreach (var field in fields)
        {
            var ciphertext = await keyStore.EncryptAsync(subjectId, field.Plaintext, ct);
            sealed_.Add(new EncryptedField(field.Name, ciphertext));
        }

        return sealed_;
    }

    public async Task<IReadOnlyList<PiiField?>> OpenAsync(
        string subjectId, IReadOnlyList<EncryptedField> fields, CancellationToken ct = default)
    {
        var opened = new List<PiiField?>(fields.Count);
        foreach (var field in fields)
        {
            var plaintext = await keyStore.DecryptAsync(subjectId, field.Ciphertext, ct);
            // null plaintext = the subject's key is gone (erased): the field is unrecoverable.
            opened.Add(plaintext is null ? null : new PiiField(field.Name, plaintext));
        }

        return opened;
    }
}
