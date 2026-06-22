using Confluent.Kafka;
using Xunit;

namespace Babelstone.InboxConsumer.Tests;

/// <summary>
/// KAFKA_SASL_TOPIC_ACL (C3 / ADR-IC-016 plane ii §4–§6): the consumer-side SASL/SCRAM credential
/// applier (the mirror of the outbox publisher's). These pure tests pin the security-relevant
/// behaviour without a broker — that a configured credential turns SASL on and lands the per-service
/// identity on the consumer config, that an unconfigured one is a no-op (the additive local-dev
/// posture), and that the cleartext PLAIN mechanism is never the default.
/// </summary>
public sealed class KafkaSaslOptionsTests
{
    [Fact]
    public void Unconfigured_options_leave_the_config_untouched()
    {
        var config = new ConsumerConfig { BootstrapServers = "localhost:19092", GroupId = "svc-inbox" };
        var sasl = new KafkaSaslOptions(); // no username ⇒ not configured

        Assert.False(sasl.IsConfigured);
        sasl.ApplyTo(config);

        // Additive no-op: SASL stays off and the load-bearing consumer settings are untouched.
        Assert.Null(config.SecurityProtocol);
        Assert.Null(config.SaslMechanism);
        Assert.Null(config.SaslUsername);
        Assert.Null(config.SaslPassword);
        Assert.Equal("svc-inbox", config.GroupId);
    }

    [Fact]
    public void Configured_options_apply_the_scram_identity_to_the_config()
    {
        var config = new ConsumerConfig { BootstrapServers = "localhost:19092", GroupId = "svc-inbox" };
        var sasl = new KafkaSaslOptions
        {
            Username = "svc-inbox-consumer",
            Password = "resolved-from-isecretprovider",
        };

        Assert.True(sasl.IsConfigured);
        sasl.ApplyTo(config);

        Assert.Equal(SecurityProtocol.SaslSsl, config.SecurityProtocol);
        Assert.Equal(SaslMechanism.ScramSha256, config.SaslMechanism);
        Assert.Equal("svc-inbox-consumer", config.SaslUsername);
        Assert.Equal("resolved-from-isecretprovider", config.SaslPassword);
        // The group id (a load-bearing consumer setting) is left untouched.
        Assert.Equal("svc-inbox", config.GroupId);
    }

    [Fact]
    public void A_non_default_mechanism_and_protocol_are_applied_verbatim()
    {
        // A deployment may pick the stronger SHA-512 variant and (on a trusted network) SASL_PLAINTEXT;
        // ApplyTo must carry the configured values through, not re-impose the defaults.
        var config = new ConsumerConfig { BootstrapServers = "localhost:19092", GroupId = "svc-inbox" };
        var sasl = new KafkaSaslOptions
        {
            Username = "svc-inbox-consumer",
            Password = "p",
            Mechanism = SaslMechanism.ScramSha512,
            SecurityProtocol = SecurityProtocol.SaslPlaintext,
        };

        sasl.ApplyTo(config);

        Assert.Equal(SaslMechanism.ScramSha512, config.SaslMechanism);
        Assert.Equal(SecurityProtocol.SaslPlaintext, config.SecurityProtocol);
    }

    [Fact]
    public void The_default_mechanism_is_scram_never_cleartext_plain()
    {
        // PLAIN would put the password on the wire (ADR-IC-016 §4 rejects it as the baseline).
        var sasl = new KafkaSaslOptions { Username = "svc-x", Password = "p" };

        Assert.Equal(SaslMechanism.ScramSha256, sasl.Mechanism);
        Assert.NotEqual(SaslMechanism.Plain, sasl.Mechanism);
    }

    [Fact]
    public void The_default_protocol_is_sasl_over_tls()
    {
        // The deployment posture is SASL over TLS so the SCRAM exchange is never in cleartext.
        var sasl = new KafkaSaslOptions { Username = "svc-x", Password = "p" };

        Assert.Equal(SecurityProtocol.SaslSsl, sasl.SecurityProtocol);
    }

    [Fact]
    public void A_username_without_a_password_is_still_considered_configured_so_a_missing_password_fails_at_connect_not_silently_off()
    {
        // IsConfigured keys on the username: a half-set credential must still attempt SASL (and fail
        // loud at connect) rather than silently fall back to no-auth — the fail-closed posture.
        var sasl = new KafkaSaslOptions { Username = "svc-x" };

        Assert.True(sasl.IsConfigured);
    }

    [Fact]
    public void An_empty_string_username_is_treated_as_absent()
    {
        // Null and "" are both "no credential": the additive local-dev posture leaves SASL off.
        Assert.False(new KafkaSaslOptions { Username = "" }.IsConfigured);
        Assert.False(new KafkaSaslOptions { Username = null }.IsConfigured);
    }
}
