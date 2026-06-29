using Babelstone.OutboxPublisher;
using Babelstone.Pii;
using Confluent.Kafka;

namespace Babelstone.Engine.Api;

/// <summary>
/// Builds the <see cref="KafkaSaslOptions"/> a co-hosted Kafka client (today: the outbox→Redpanda
/// relay producer) presents to the broker, resolving its SCRAM credential at the host composition
/// root through the existing <see cref="ISecretProvider"/> seam (ADR-IC-016 plane ii §6 / ADR-PC-004
/// §A1). It connects the secret seam to the relay's <see cref="KafkaSaslOptions"/>: the applier and the
/// <c>Sasl</c> property on the relay options carry no credential on their own, so this resolver supplies
/// it — without it SASL stays OFF for every client.
///
/// <para><b>The split (ADR-IC-016 §4–§6).</b> A Kafka client's identity is two parts handled
/// differently:</para>
/// <list type="bullet">
///   <item>The <b>username</b> is the declarative <i>service identity</i> (<c>svc-outbox-publisher</c>,
///   the SASL/SCRAM principal in <c>infra/redpanda/topic-acls.yaml</c>) — not a secret, so it comes
///   from <see cref="IConfiguration"/> (<c>Kafka:Sasl:Username</c>). One distinct identity per client
///   contains a compromise to that client's grants (§4).</item>
///   <item>The <b>password</b> IS the secret, so it resolves through <see cref="ISecretProvider"/> —
///   <see cref="OpenBaoKvSecretProvider"/> when <c>OpenBao:Enabled</c>, the configuration-backed
///   provider otherwise — exactly the <c>ConnectionStrings:Engine</c> pattern (§A1). The resolved
///   value lives only here in process memory to open the connection; it is NEVER logged, placed on a
///   span attribute, or carried on the durable bus (§Residual-risks / ADR-PC-004 §P2).</item>
/// </list>
///
/// <para><b>OFF-when-unconfigured (the additive local-dev posture).</b> With no
/// <c>Kafka:Sasl:Username</c> configured the username is empty, no secret is resolved, the returned
/// options report <see cref="KafkaSaslOptions.IsConfigured"/> = false, and <c>ApplyTo</c> is a no-op —
/// so a plaintext loopback Redpanda (<c>make up</c>) is unchanged. SASL turns on only when a deployment
/// supplies the identity; SASL_SSL + SCRAM-SHA-256 are the secure defaults (PLAIN is never offered).</para>
/// </summary>
public static class HostSasl
{
    /// <summary>The configuration section the per-client SASL identity is read from.</summary>
    public const string OutboxPublisherSection = "Kafka:Sasl:OutboxPublisher";

    /// <summary>
    /// Resolves the outbox relay producer's SASL/SCRAM credential. Reads its declarative username from
    /// <c>Kafka:Sasl:OutboxPublisher:Username</c> (the <c>svc-outbox-publisher</c> principal) and, only
    /// when that username is present, its password through <paramref name="secretProvider"/> under the
    /// secret name in <c>Kafka:Sasl:OutboxPublisher:SecretName</c> (default <c>OutboxPublisherSaslPassword</c>;
    /// the configuration-backed provider reads <c>ConnectionStrings:OutboxPublisherSaslPassword</c>, an
    /// OpenBao deployment reads the KV path of that name — mirroring <c>ConnectionStrings:Engine</c>).
    /// The mechanism and security protocol fall back to the secure SCRAM-SHA-256 / SASL_SSL defaults and
    /// are overridable per deployment.
    /// </summary>
    /// <remarks>
    /// Returns the OFF posture (empty options, <see cref="KafkaSaslOptions.IsConfigured"/> = false) when
    /// no username is configured — the secret provider is then never consulted, so a missing credential
    /// does not throw in local dev. A configured username with no resolvable password DOES throw
    /// (fail-loud, never silently falling back to no-auth — the fail-closed posture
    /// <c>KafkaSaslOptionsTests</c> pins).
    /// </remarks>
    public static async Task<KafkaSaslOptions> ResolveOutboxPublisherAsync(
        IConfiguration configuration,
        ISecretProvider secretProvider,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(secretProvider);

        var username = configuration[$"{OutboxPublisherSection}:Username"];
        if (string.IsNullOrWhiteSpace(username))
        {
            // No identity configured ⇒ SASL OFF (the additive plaintext local-dev posture). The secret
            // provider is deliberately NOT consulted here, so `make up` does not need a SASL secret.
            return new KafkaSaslOptions();
        }

        var secretName = configuration[$"{OutboxPublisherSection}:SecretName"] ?? "OutboxPublisherSaslPassword";
        // A configured username keys IsConfigured; resolving the password through the secret boundary
        // is what authenticates the connection. A missing secret throws (fail-loud), never a silent
        // fallback to no-auth.
        var password = await secretProvider.GetSecretAsync(secretName, ct);

        return new KafkaSaslOptions
        {
            Username = username,
            Password = password,
            Mechanism = ParseMechanism(configuration[$"{OutboxPublisherSection}:Mechanism"]),
            SecurityProtocol = ParseSecurityProtocol(configuration[$"{OutboxPublisherSection}:SecurityProtocol"]),
        };
    }

    /// <summary>
    /// Parses the configured SASL mechanism, defaulting to SCRAM-SHA-256 (the ADR-IC-016 §4 baseline)
    /// and rejecting the cleartext PLAIN mechanism outright — it would put the password on the wire.
    /// </summary>
    private static SaslMechanism ParseMechanism(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return SaslMechanism.ScramSha256;
        }

        var mechanism = Enum.Parse<SaslMechanism>(configured, ignoreCase: true);
        if (mechanism is SaslMechanism.Plain)
        {
            throw new InvalidOperationException(
                "SASL mechanism PLAIN is never offered (ADR-IC-016 §4 — it would put the password on the wire); use a SCRAM variant.");
        }

        return mechanism;
    }

    /// <summary>
    /// Parses the configured transport security protocol, defaulting to SASL_SSL (the deployment posture
    /// so the SCRAM exchange is never in cleartext, ADR-IC-016 §6).
    /// </summary>
    private static SecurityProtocol ParseSecurityProtocol(string? configured)
        => string.IsNullOrWhiteSpace(configured)
            ? SecurityProtocol.SaslSsl
            : Enum.Parse<SecurityProtocol>(configured, ignoreCase: true);
}
