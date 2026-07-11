using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Configuration;

namespace Babelstone.Orchestrator;

/// <summary>
/// Internal mTLS for the orchestrator's engine/orchestrator hops (ADR-IC-006 §P5 Boundary 2 /
/// ADR-IC-016 plane (i)): the caller side <see cref="BuildHandler"/> for its outbound dispatcher hop
/// (bd babelstone-zla1.12.10), and the server side <see cref="ConfigureKestrel"/> for its own Kestrel
/// saga edge (bd babelstone-zla1.12.25, commitment <c>SVC_ENGINE_ORCH_MTLS</c>). Both pin the SAME
/// internal CA.
/// </summary>
/// <remarks>
/// <para>
/// In plain English: the saga command dispatcher POSTs commands to the engine over HTTP. Once the
/// engine's Kestrel host is flipped to HTTPS-with-a-REQUIRED-client-cert (the gated
/// <c>overlays/staging/internal-mtls.patch.yaml</c>), a caller that presents no client cert — or one
/// the engine cannot chain to the shared internal CA — is rejected at the TLS handshake. This helper
/// configures the dispatcher's default <see cref="System.Net.Http.HttpClient"/> handler to (a) PRESENT
/// the orchestrator's own client cert and (b) PIN the engine's server cert to that same internal CA
/// (rather than the container's system trust store, which does not carry it — the "known remainder"
/// the patch header names, fix (b)).
/// </para>
/// <para>
/// It is OFF by default and gated purely on configuration: with no internal-CA path configured the
/// handler is left untouched, so the CA-env-UNSET hosts (the demo, the local stack, tests) keep their
/// plain-HTTP behaviour byte-for-byte. On STAGING the app manifest sets the CA env and mounts the
/// client cert UNCONDITIONALLY, so this helper is NOT inert there — the dispatcher dials https and
/// presents a client cert the moment the manifest is applied. That is why the staging rollout flips
/// the callers, the engine/orchestrator server patch (internal-mtls.patch.yaml), and the deck-sync
/// TOGETHER in one maintenance window (that patch's ROLLOUT ORDER steps 3–4): applying the caller half
/// while the server is still plain HTTP would break this hop. The engine/orchestrator stay
/// extraction-ready (ADR-PC-019 §P2) — this is host-composition wiring, not an engine-kernel reference.
/// </para>
/// <para>
/// Config keys (env form in brackets): <c>InternalMtls:CaCertPath</c>
/// [<c>InternalMtls__CaCertPath</c>] — PEM of the internal CA the server cert must chain to;
/// <c>InternalMtls:ClientCertPath</c> [<c>InternalMtls__ClientCertPath</c>] and
/// <c>InternalMtls:ClientKeyPath</c> [<c>InternalMtls__ClientKeyPath</c>] — the caller's PEM client
/// cert + key (the cert-manager Secret's <c>tls.crt</c>/<c>tls.key</c>). The CA path alone enables
/// server-cert pinning; the client cert/key pair adds the presented cert.
/// </para>
/// </remarks>
internal static class InternalMtls
{
    internal const string CaCertPathKey = "InternalMtls:CaCertPath";
    internal const string ClientCertPathKey = "InternalMtls:ClientCertPath";
    internal const string ClientKeyPathKey = "InternalMtls:ClientKeyPath";

    /// <summary>
    /// True when an internal CA path is configured — i.e. the outbound hops are expected to speak
    /// internal mTLS. When false the caller must leave its <c>HttpClient</c> handler unconfigured
    /// (the plain-HTTP default).
    /// </summary>
    internal static bool IsConfigured(IConfiguration configuration)
        => !string.IsNullOrWhiteSpace(configuration[CaCertPathKey]);

    /// <summary>
    /// Build the primary message handler for an internal-mTLS caller: presents the configured client
    /// cert (when a cert+key pair is configured) and validates the server cert by CHAINING it to the
    /// configured internal CA (pinned trust), not the ambient system store. Throws
    /// <see cref="InvalidOperationException"/> if called without a CA configured — call
    /// <see cref="IsConfigured"/> first.
    /// </summary>
    internal static SocketsHttpHandler BuildHandler(IConfiguration configuration)
    {
        var caCertPath = configuration[CaCertPathKey];
        if (string.IsNullOrWhiteSpace(caCertPath))
        {
            throw new InvalidOperationException(
                $"Internal mTLS is not configured: set '{CaCertPathKey}' to the internal CA PEM path "
                + "before building the mTLS handler (call InternalMtls.IsConfigured first).");
        }

        // The pinned trust anchor: the internal CA the server cert MUST chain to. Loaded once here and
        // captured by the validation callback — the container's system trust store is deliberately NOT
        // consulted (it does not carry this CA — the patch header's "known remainder").
        var caCertificate = X509CertificateLoader.LoadCertificateFromFile(caCertPath);

        var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                // Pin server-cert trust to the internal CA (fix (b) from the patch header): build the
                // chain with ONLY our CA as the trust root; anything that does not chain to it is
                // rejected, and the ambient machine store is ignored (CustomTrustStore +
                // CustomRootTrust). Name mismatches / expiry etc. still fail normally.
                RemoteCertificateValidationCallback = (_, serverCert, _, sslPolicyErrors) =>
                    ValidateAgainstInternalCa(serverCert, sslPolicyErrors, caCertificate),
            },
        };

        // Present the caller's client cert (the RequireCertificate side): the engine's Kestrel host
        // demands one, verified against the SAME internal CA. Configured as a PEM cert+key pair (the
        // cert-manager Secret's tls.crt/tls.key). When only the CA is configured (no client pair) we
        // still pin the server but present nothing — a defence-in-depth-only posture.
        var clientCertPath = configuration[ClientCertPathKey];
        var clientKeyPath = configuration[ClientKeyPathKey];
        if (!string.IsNullOrWhiteSpace(clientCertPath) && !string.IsNullOrWhiteSpace(clientKeyPath))
        {
            var clientCertificate = X509Certificate2.CreateFromPemFile(clientCertPath, clientKeyPath);
            handler.SslOptions.ClientCertificates = new X509CertificateCollection { clientCertificate };
        }

        return handler;
    }

    /// <summary>
    /// Require + CA-pin-validate a client certificate on the orchestrator's HTTPS endpoints (the saga
    /// 202 + SSE edge). Sets <see cref="ClientCertificateMode.RequireCertificate"/> and installs the
    /// pinned-CA <see cref="HttpsConnectionAdapterOptions.ClientCertificateValidation"/> callback as the
    /// Kestrel HTTPS defaults, so it applies to the config-defined HTTPS endpoint
    /// (Kestrel__Endpoints__Https__*). The transport bind stays config-driven; only the client-cert
    /// policy is code. Throws <see cref="InvalidOperationException"/> if called without a CA configured.
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
    /// Build the Kestrel client-certificate validation delegate for the orchestrator's SERVER surface —
    /// the saga 202 + SSE edge that Kong and Mission Control dial (bd babelstone-zla1.12.25). Accepts a
    /// presented client cert only when it chains to the configured internal CA (pinned trust), never the
    /// ambient system store — the inbound mirror of the outbound <see cref="BuildHandler"/>, reusing the
    /// identical pinned-CA check. The CA is loaded once here and captured by the returned callback.
    /// Throws <see cref="InvalidOperationException"/> if called without a CA configured — call
    /// <see cref="IsConfigured"/> first.
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
    /// Validate a peer certificate by chaining it to the pinned internal CA — the server cert on the
    /// outbound dispatcher hop, or the presented client cert on the inbound saga edge. Any policy error
    /// other than an untrusted-root chain (EXPECTED — the CA is not in the system store) fails the
    /// handshake; the untrusted-root case is resolved by rebuilding the chain with the internal CA as
    /// the sole custom trust root.
    /// </summary>
    private static bool ValidateAgainstInternalCa(
        X509Certificate? peerCert, SslPolicyErrors sslPolicyErrors, X509Certificate2 caCertificate)
    {
        if (peerCert is null)
        {
            return false;
        }

        // Any error beyond an untrusted-root chain (e.g. a server-hop name mismatch) is never acceptable.
        if ((sslPolicyErrors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None)
        {
            return false;
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(caCertificate);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(new X509Certificate2(peerCert));
    }
}
