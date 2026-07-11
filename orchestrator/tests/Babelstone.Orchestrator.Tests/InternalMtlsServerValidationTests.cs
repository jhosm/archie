using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Babelstone.Orchestrator;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Babelstone.Orchestrator.Tests;

/// <summary>
/// Unit coverage for the orchestrator's server-side internal-mTLS client-cert validator (bd
/// babelstone-zla1.12.25 — the unit leg of commitment SVC_ENGINE_ORCH_MTLS): the saga edge accepts a
/// presented cert that chains to the pinned CA, rejects
/// one from a different CA or with any policy error beyond an untrusted root, and stays OFF (plain HTTP)
/// when no CA is configured. The inbound mirror of the shipped outbound dispatcher leg.
/// </summary>
public sealed class InternalMtlsServerValidationTests
{
    [Fact]
    public void IsConfigured_is_false_without_a_ca_path()
        => Assert.False(InternalMtls.IsConfigured(new ConfigurationBuilder().Build()));

    [Fact]
    public void BuildClientCertificateValidation_throws_when_unconfigured()
        => Assert.Throws<InvalidOperationException>(
            () => InternalMtls.BuildClientCertificateValidation(new ConfigurationBuilder().Build()));

    [Fact]
    public void Accepts_a_client_cert_that_chains_to_the_pinned_ca()
    {
        using var ca = MintCa("CN=internal-ca");
        using var leaf = MintClientLeaf("CN=orchestrator-client", ca);
        var validate = InternalMtls.BuildClientCertificateValidation(Config(WriteCaPem(ca)));

        Assert.True(validate(leaf, null, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void Rejects_a_client_cert_from_a_different_ca()
    {
        using var pinnedCa = MintCa("CN=internal-ca");
        using var rogueCa = MintCa("CN=rogue-ca");
        using var rogueLeaf = MintClientLeaf("CN=rogue-client", rogueCa);
        var validate = InternalMtls.BuildClientCertificateValidation(Config(WriteCaPem(pinnedCa)));

        Assert.False(validate(rogueLeaf, null, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void Rejects_any_policy_error_beyond_an_untrusted_root()
    {
        using var ca = MintCa("CN=internal-ca");
        using var leaf = MintClientLeaf("CN=orchestrator-client", ca);
        var validate = InternalMtls.BuildClientCertificateValidation(Config(WriteCaPem(ca)));

        Assert.False(validate(leaf, null,
            SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch));
    }

    private static IConfiguration Config(string caCertPath)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["InternalMtls:CaCertPath"] = caCertPath })
            .Build();

    private static X509Certificate2 MintCa(string subject)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    private static X509Certificate2 MintClientLeaf(string subject, X509Certificate2 issuer)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.2") }, false)); // id-kp-clientAuth
        return request.Create(
            issuer, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1), Guid.NewGuid().ToByteArray());
    }

    private static string WriteCaPem(X509Certificate2 ca)
    {
        var path = Path.Combine(Path.GetTempPath(), $"internal-mtls-test-ca-{Guid.NewGuid():N}.pem");
        File.WriteAllText(path, ca.ExportCertificatePem());
        return path;
    }
}
