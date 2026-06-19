using Confluent.Kafka;

namespace Babelstone.InboxConsumer;

/// <summary>
/// The SASL/SCRAM credential a Kafka client presents to Redpanda to prove its service identity
/// (ADR-IC-016 plane ii §4–§6). The username/password are resolved at the host composition root
/// through the existing <c>ISecretProvider</c> seam (ADR-PC-004 §A1 — "Redpanda SASL credentials
/// later") and passed in here; this assembly never reaches the secret boundary itself, and the
/// resolved secret lives only in process memory to open the connection — it is NEVER logged, placed
/// on a span attribute, or carried on the durable bus (ADR-IC-016 §Residual-risks / ADR-PC-004 §P2).
///
/// <para>When <see cref="Username"/> is null/empty the credential is treated as <b>absent</b> and
/// SASL is left OFF — the plaintext local-dev posture (no auth on a loopback Redpanda). In a
/// deployment the host resolves the credential and supplies it, so this consumer authenticates with
/// its own distinct identity and topic ACLs can restrict it to the topics it is allowed to read. This
/// is additive: an unconfigured consumer behaves exactly as before.</para>
/// </summary>
public sealed record KafkaSaslOptions
{
    /// <summary>The per-service SCRAM username — a distinct identity per consumer, so topic ACLs can
    /// scope it to only the topics it subscribes to (ADR-IC-016 §5). Null/empty ⇒ SASL disabled.</summary>
    public string? Username { get; init; }

    /// <summary>The resolved SCRAM password (an <c>ISecretProvider.GetSecretAsync</c> result at the
    /// composition root). Held only to open the connection; never logged or spanned.</summary>
    public string? Password { get; init; }

    /// <summary>
    /// The SASL mechanism. SCRAM-SHA-256 is the chosen baseline (ADR-IC-016 §4); SCRAM-SHA-512 is the
    /// stronger variant a deployment may pick. PLAIN is intentionally NOT offered.
    /// </summary>
    public SaslMechanism Mechanism { get; init; } = SaslMechanism.ScramSha256;

    /// <summary>
    /// The transport security protocol. <see cref="SecurityProtocol.SaslSsl"/> (SASL over TLS) is the
    /// deployment posture; <see cref="SecurityProtocol.SaslPlaintext"/> is acceptable only on a trusted
    /// local network.
    /// </summary>
    public SecurityProtocol SecurityProtocol { get; init; } = SecurityProtocol.SaslSsl;

    /// <summary>True when a real credential is present and SASL should be applied.</summary>
    public bool IsConfigured => !string.IsNullOrEmpty(Username);

    /// <summary>
    /// Applies the SASL/SCRAM credential to a client config (a <see cref="ConsumerConfig"/> or any
    /// <see cref="ClientConfig"/>) when one is configured; a no-op otherwise. Idempotent and additive —
    /// it sets only the SASL properties, leaving group-id/auto-commit/offset-reset untouched. The
    /// resolved password is written straight into librdkafka's config and never echoed.
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
