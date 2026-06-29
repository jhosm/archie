using Confluent.Kafka;

namespace Babelstone.OutboxPublisher;

/// <summary>
/// The SASL/SCRAM credential a Kafka client presents to Redpanda to prove its service identity
/// (ADR-IC-016 plane ii). The username/password are resolved at the host composition root
/// through the existing <c>ISecretProvider</c> seam (ADR-PC-004 — "Redpanda SASL credentials
/// later") and passed in here; this assembly never reaches the secret boundary itself, and the
/// resolved secret lives only in process memory to open the connection — it is NEVER logged, placed
/// on a span attribute, or carried on the durable bus (ADR-IC-016 / ADR-PC-004).
///
/// <para>When <see cref="Username"/> is null/empty the credential is treated as <b>absent</b> and
/// SASL is left OFF — the plaintext local-dev posture (no auth on a loopback Redpanda). In a
/// deployment the host resolves the credential and supplies it, turning every client into an
/// authenticated, distinct identity. This is additive: an unconfigured client behaves exactly as
/// before.</para>
/// </summary>
public sealed record KafkaSaslOptions
{
    /// <summary>
    /// The per-service SCRAM username (a distinct identity per client — the outbox publisher's is
    /// distinct from the Deposits API's, so a compromised publisher can only publish to the topics it
    /// is authorized for, ADR-IC-016). Null/empty ⇒ SASL disabled.
    /// </summary>
    public string? Username { get; init; }

    /// <summary>The resolved SCRAM password (an <c>ISecretProvider.GetSecretAsync</c> result at the
    /// composition root). Held only to open the connection; never logged or spanned.</summary>
    public string? Password { get; init; }

    /// <summary>
    /// The SASL mechanism. SCRAM-SHA-256 is the chosen baseline (ADR-IC-016 — the simpler
    /// credential to issue and rotate at this scale); SCRAM-SHA-512 is the stronger variant a
    /// deployment may pick. PLAIN is intentionally NOT offered — it would put the password on the wire.
    /// </summary>
    public SaslMechanism Mechanism { get; init; } = SaslMechanism.ScramSha256;

    /// <summary>
    /// The transport security protocol. <see cref="SecurityProtocol.SaslSsl"/> (SASL over TLS) is the
    /// deployment posture so the SCRAM exchange is never in cleartext; <see cref="SecurityProtocol.SaslPlaintext"/>
    /// is acceptable only on a trusted local network. SCRAM's challenge-response means the password is
    /// not sent even over SASL_PLAINTEXT, but the channel should still be TLS in production.
    /// </summary>
    public SecurityProtocol SecurityProtocol { get; init; } = SecurityProtocol.SaslSsl;

    /// <summary>True when a real credential is present and SASL should be applied.</summary>
    public bool IsConfigured => !string.IsNullOrEmpty(Username);

    /// <summary>
    /// Applies the SASL/SCRAM credential to a client config (a <see cref="ProducerConfig"/> or any
    /// <see cref="ClientConfig"/>) when one is configured; a no-op otherwise. Idempotent and additive —
    /// it sets only the SASL properties, leaving idempotence/acks/bootstrap untouched. The resolved
    /// password is written straight into librdkafka's config and never echoed.
    /// </summary>
    public void ApplyTo(ClientConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!IsConfigured)
        {
            return;
        }

        config.SecurityProtocol = SecurityProtocol;
        config.SaslMechanism = Mechanism;
        config.SaslUsername = Username;
        config.SaslPassword = Password;
    }
}
