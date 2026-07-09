using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;

namespace Babelstone.Orchestrator;

/// <summary>
/// Caller-side internal mTLS for the orchestrator's outbound HTTP hops (bd babelstone-zla1.12.10;
/// ADR-IC-006 §P5 Boundary 2 / ADR-IC-016 plane (i)).
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
/// handler is left untouched, so every non-staging host (the demo, the local stack, tests) keeps its
/// plain-HTTP behaviour byte-for-byte. Staging turns it on by mounting the client cert + the CA and
/// setting the env keys below. The engine/orchestrator stay extraction-ready (ADR-PC-019 §P2) — this
/// is host-composition wiring, not an engine-kernel reference.
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
    /// Validate a server certificate by chaining it to the pinned internal CA. Any policy error other
    /// than an untrusted-root chain (which is EXPECTED — the CA is not in the system store) fails the
    /// handshake; the untrusted-root case is resolved by rebuilding the chain with the internal CA as
    /// the sole custom trust root.
    /// </summary>
    private static bool ValidateAgainstInternalCa(
        X509Certificate? serverCert, SslPolicyErrors sslPolicyErrors, X509Certificate2 caCertificate)
    {
        if (serverCert is null)
        {
            return false;
        }

        // A name mismatch or a total absence of a cert is never acceptable, regardless of the CA.
        if ((sslPolicyErrors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None)
        {
            return false;
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(caCertificate);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(new X509Certificate2(serverCert));
    }
}
