using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Configuration;

namespace Babelstone.Engine.Api;

/// <summary>
/// Server-side internal mTLS for the engine's Kestrel command surface (bd babelstone-zla1.12.25;
/// ADR-IC-006 §P5 Boundary 2 / ADR-IC-016 plane (i), commitment <c>SVC_ENGINE_ORCH_MTLS</c>).
/// </summary>
/// <remarks>
/// <para>
/// In plain English: every in-cluster caller of the engine — the saga command dispatcher, the
/// notification read worker, the mcp-server tool edge, Mission Control's proxy, and Kong — already
/// presents a client certificate signed by the shared internal CA (the caller-side leg, bd
/// babelstone-zla1.12.10). This is the matching SERVER half: it makes the engine's Kestrel host
/// REQUIRE a client certificate and VALIDATE it by chaining to that same internal CA, rather than to
/// the container's system trust store — which does not carry the cluster-generated CA, so .NET's
/// default validation would reject every otherwise-valid caller. This is the "known remainder" the
/// gated <c>overlays/staging/internal-mtls.patch.yaml</c> header resolves code-side (its option (b)):
/// the pinned-CA validation is code; the HTTPS transport (endpoint URL + server cert) stays config.
/// </para>
/// <para>
/// It is the exact server-side mirror of the caller-side pinned-CA validation shipped in
/// <c>Babelstone.Orchestrator.InternalMtls.ValidateAgainstInternalCa</c> /
/// <c>Babelstone.Notification.Host.InternalMtls</c> — one internal CA underwrites both directions of
/// every hop (the load-bearing §P5 constraint). It lives in the composition-root host project
/// (alongside <c>HostSasl</c>/<c>HostServices</c>), never in the <c>Babelstone.Engine</c> kernel, so
/// the family→engine one-way boundary and extraction-readiness stay intact (ADR-PC-019 §P2).
/// </para>
/// <para>
/// It is OFF by default and gated purely on configuration: with no internal-CA path configured the
/// Kestrel host is left untouched, so the CA-env-UNSET hosts (the demo, the local stack, tests) keep
/// their plain-HTTP behaviour byte-for-byte. On STAGING the gated server patch mounts the engine's
/// server cert + CA and sets <c>InternalMtls__CaCertPath=/certs/ca.crt</c>, so this turns on together
/// with the callers and the deck-sync in ONE maintenance window (the patch's ROLLOUT ORDER — caller
/// and server flip together) — enabling the require-a-cert side while a caller is still plain HTTP
/// would break that hop.
/// </para>
/// <para>
/// Config key (env form in brackets): <c>InternalMtls:CaCertPath</c> [<c>InternalMtls__CaCertPath</c>]
/// — the internal CA PEM the presented client cert must chain to (the cert-manager Secret's
/// <c>ca.crt</c>). The engine is a pure server on this boundary — its only outbound HTTP is to OpenBao,
/// a separate boundary — so it needs no client leg, only this validator.
/// </para>
/// </remarks>
internal static class InternalMtls
{
    internal const string CaCertPathKey = "InternalMtls:CaCertPath";

    /// <summary>
    /// True when an internal CA path is configured — i.e. the command surface is expected to require +
    /// validate an internal-CA client cert. When false the host leaves Kestrel untouched (plain HTTP).
    /// </summary>
    internal static bool IsConfigured(IConfiguration configuration)
        => !string.IsNullOrWhiteSpace(configuration[CaCertPathKey]);

    /// <summary>
    /// Require + CA-pin-validate a client certificate on the engine's HTTPS endpoints. Sets
    /// <see cref="ClientCertificateMode.RequireCertificate"/> and installs the pinned-CA
    /// <see cref="HttpsConnectionAdapterOptions.ClientCertificateValidation"/> callback as the Kestrel
    /// HTTPS defaults, so it applies to the config-defined HTTPS endpoint (Kestrel__Endpoints__Https__*).
    /// The transport bind (endpoint URL + server cert path) stays config-driven; only the client-cert
    /// policy is code. Throws <see cref="InvalidOperationException"/> if called without a CA configured —
    /// call <see cref="IsConfigured"/> first.
    /// </summary>
    internal static void ConfigureKestrel(IWebHostBuilder webHost, IConfiguration configuration)
    {
        var validate = BuildClientCertificateValidation(configuration);
        webHost.ConfigureKestrel(kestrel =>
            kestrel.ConfigureHttpsDefaults(https =>
            {
                https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                https.ClientCertificateValidation = validate;
            }));
    }

    /// <summary>
    /// Build the Kestrel client-certificate validation delegate: accepts a presented client cert only
    /// when it chains to the configured internal CA (pinned trust), never the ambient system store. The
    /// CA is loaded once here and captured by the returned callback. Throws
    /// <see cref="InvalidOperationException"/> if called without a CA configured.
    /// </summary>
    internal static Func<X509Certificate2, X509Chain?, SslPolicyErrors, bool> BuildClientCertificateValidation(
        IConfiguration configuration)
    {
        var caCertPath = configuration[CaCertPathKey];
        if (string.IsNullOrWhiteSpace(caCertPath))
        {
            throw new InvalidOperationException(
                $"Internal mTLS is not configured: set '{CaCertPathKey}' to the internal CA PEM path "
                + "before configuring the mTLS listener (call InternalMtls.IsConfigured first).");
        }

        // The pinned trust anchor: the internal CA every valid caller's client cert MUST chain to. The
        // container's system trust store is deliberately NOT consulted (it does not carry this CA).
        var caCertificate = X509CertificateLoader.LoadCertificateFromFile(caCertPath);
        return (clientCert, _, sslPolicyErrors) => ValidateAgainstInternalCa(clientCert, sslPolicyErrors, caCertificate);
    }

    /// <summary>
    /// Validate a presented client certificate by chaining it to the pinned internal CA. Any policy
    /// error other than an untrusted-root chain (EXPECTED — the CA is not in the system store) fails
    /// the handshake; the untrusted-root case is resolved by rebuilding the chain with the internal CA
    /// as the sole custom trust root. Mirrors the caller-side
    /// <c>Babelstone.Orchestrator.InternalMtls.ValidateAgainstInternalCa</c>.
    /// </summary>
    private static bool ValidateAgainstInternalCa(
        X509Certificate2? clientCert, SslPolicyErrors sslPolicyErrors, X509Certificate2 caCertificate)
    {
        if (clientCert is null)
        {
            return false;
        }

        if ((sslPolicyErrors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None)
        {
            return false;
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(caCertificate);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(clientCert);
    }
}
