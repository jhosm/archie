using System.Security.Cryptography;
using System.Text;

namespace Babelstone.Telemetry;

/// <summary>
/// Derives the salted, one-way <b>pseudonym</b> that a span carries under
/// <see cref="BabelstoneAttributes.SubjectPseudonym"/> in place of a raw <c>client_id</c>
/// (ADR-IC-016 plane iii / Document 10 Principle 4).
///
/// <para>The raw client id is PII — it keys directly into the Customer Data Store — so it may never
/// ride a telemetry signal. Attaching a non-reversible hash instead lets an operator correlate the
/// spans of one customer's debugging session <i>without</i> the trace backend becoming a searchable
/// index of personal data. The mapping pseudonym→client is recoverable only inside the Customer Data
/// Store, which holds the same salt; without the salt the hash is just an opaque token.</para>
///
/// <para><b>Why HMAC-SHA-256 over a bare hash.</b> A salt is only protective if an attacker who sees
/// the pseudonym cannot brute-force the (low-entropy, enumerable) client-id space back to the raw id.
/// A bare <c>SHA256(salt + id)</c> is vulnerable to length-extension and to offline dictionary attacks
/// once the salt leaks; keying the hash (HMAC) makes the salt a true secret key, so the residual-risk
/// ADR-IC-016 flags ("a weak or unsalted hash re-introduces the personal-data-index risk") is closed.
/// The salt is resolved at the host composition root through the <c>ISecretProvider</c> seam
/// (ADR-PC-004) — it is a secret, never a compile-time constant, and never logged or spanned.</para>
///
/// <para><b>Purity.</b> Derivation is a pure function of (salt, clientId): no clock, no I/O, no
/// randomness — the salt is supplied by the caller. The same inputs always yield the same pseudonym,
/// so a customer's spans correlate stably across services and across a trace's lifetime.</para>
/// </summary>
public static class ClientPseudonym
{
    /// <summary>
    /// The number of hex characters of the HMAC digest the pseudonym keeps. 16 hex chars = 64 bits =
    /// 2^64 of collision space — ample to tell customers apart in a debugging window while staying a
    /// short, human-scannable span tag. Truncation also strengthens non-reversibility: the full
    /// 256-bit pre-image is never exposed.
    /// </summary>
    public const int PseudonymHexLength = 16;

    /// <summary>
    /// Returns the salted one-way pseudonym for <paramref name="clientId"/> under
    /// <paramref name="salt"/>. The result is lowercase hex, <see cref="PseudonymHexLength"/> chars,
    /// safe to attach to a span under <see cref="BabelstoneAttributes.SubjectPseudonym"/>.
    /// </summary>
    /// <param name="clientId">The raw client identifier. Never set this value on a span directly —
    /// only its pseudonym leaves the service.</param>
    /// <param name="salt">The HMAC key, resolved from the secret boundary at the composition root
    /// (the same store the Customer Data Store uses to reverse the mapping). Treated as a UTF-8 secret.</param>
    /// <exception cref="ArgumentException">If <paramref name="clientId"/> or <paramref name="salt"/>
    /// is null or empty — a missing salt would silently produce an un-keyed (reversible) token, which
    /// is exactly the failure mode this guard fails loud on rather than degrade into.</exception>
    public static string Of(string clientId, string salt)
    {
        if (string.IsNullOrEmpty(clientId))
        {
            throw new ArgumentException("clientId must be non-empty to derive a pseudonym.", nameof(clientId));
        }

        if (string.IsNullOrEmpty(salt))
        {
            throw new ArgumentException(
                "salt must be non-empty — an un-keyed pseudonym would be reversible (ADR-IC-016 §8 residual risk).",
                nameof(salt));
        }

        var key = Encoding.UTF8.GetBytes(salt);
        var message = Encoding.UTF8.GetBytes(clientId);
        var digest = HMACSHA256.HashData(key, message);

        return Convert.ToHexStringLower(digest)[..PseudonymHexLength];
    }
}
