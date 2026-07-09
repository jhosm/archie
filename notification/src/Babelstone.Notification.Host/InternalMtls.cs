using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;

namespace Babelstone.Notification.Host;

/// <summary>
/// Caller-side internal mTLS for the notification worker's engine-read hop (bd babelstone-zla1.12.10;
/// ADR-IC-006 §P5 Boundary 2 / ADR-IC-016 plane (i)).
/// </summary>
/// <remarks>
/// <para>
/// In plain English: the notification worker READS deposit facts from the engine over HTTP
/// (<c>Engine:BaseUrl</c>, the ADR-PC-027 canonical read surface). Once the engine's Kestrel host is
/// flipped to HTTPS-with-a-REQUIRED-client-cert (the gated
/// <c>overlays/staging/internal-mtls.patch.yaml</c>), a reader that presents no client cert — or one
/// the engine cannot chain to the shared internal CA — is rejected at the TLS handshake. This helper
/// configures the host's outbound <see cref="System.Net.Http.HttpClient"/> handler to (a) PRESENT the
/// notification worker's own client cert and (b) PIN the engine's server cert to that same internal CA
/// (not the container's system trust store, which does not carry it — the "known remainder" the patch
/// header names, fix (b)).
/// </para>
/// <para>
/// The typed engine read client is FAMILY-OWNED — the term-deposit module registers
/// <c>AddHttpClient&lt;DepositReadClient&gt;</c> (ADR-IC-019 §D1), so the host cannot name it. Instead
/// this wires the mTLS primary handler through <c>ConfigureHttpClientDefaults</c> in the host (the §A2
/// composition root), which applies to EVERY factory client the family modules register — the correct
/// seam, since the trust decision is a host/deployment concern, not a family one.
/// </para>
/// <para>
/// It is OFF by default and gated purely on configuration: with no internal-CA path configured the
/// handler is left untouched, so the CA-env-UNSET hosts (the demo, the local stack, tests) keep their
/// plain-HTTP behaviour byte-for-byte. On STAGING the app manifest sets the CA env and mounts the
/// client cert UNCONDITIONALLY, so this helper is NOT inert there — the worker reads over https and
/// presents a client cert the moment the manifest is applied. That is why the staging rollout flips
/// the callers, the engine server patch (internal-mtls.patch.yaml), and the deck-sync TOGETHER in one
/// maintenance window (that patch's ROLLOUT ORDER steps 3–4): applying the caller half while the engine
/// is still plain HTTP would break this read hop.
/// Config keys (env form in brackets): <c>InternalMtls:CaCertPath</c>
/// [<c>InternalMtls__CaCertPath</c>], <c>InternalMtls:ClientCertPath</c>
/// [<c>InternalMtls__ClientCertPath</c>], <c>InternalMtls:ClientKeyPath</c>
/// [<c>InternalMtls__ClientKeyPath</c>] — the internal CA PEM, and the caller's PEM client cert + key
/// (the cert-manager Secret's <c>tls.crt</c>/<c>tls.key</c>). The CA path alone enables server-cert
/// pinning; the client cert/key pair adds the presented cert. The notification worker holds no engine
/// kernel reference — this is host-composition wiring (ADR-PC-019 §P2).
/// </para>
/// </remarks>
internal static class InternalMtls
{
    internal const string CaCertPathKey = "InternalMtls:CaCertPath";
    internal const string ClientCertPathKey = "InternalMtls:ClientCertPath";
    internal const string ClientKeyPathKey = "InternalMtls:ClientKeyPath";

    /// <summary>
    /// True when an internal CA path is configured — i.e. the engine-read hop is expected to speak
    /// internal mTLS. When false the host must leave its <c>HttpClient</c> handler unconfigured (the
    /// plain-HTTP default).
    /// </summary>
    internal static bool IsConfigured(IConfiguration configuration)
        => !string.IsNullOrWhiteSpace(configuration[CaCertPathKey]);

    /// <summary>
    /// Build the primary message handler for the internal-mTLS engine-read hop: presents the configured
    /// client cert (when a cert+key pair is configured) and validates the server cert by CHAINING it to
    /// the configured internal CA (pinned trust), not the ambient system store. Throws
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

        // The pinned trust anchor: the internal CA the engine's server cert MUST chain to. The
        // container's system trust store is deliberately NOT consulted (it does not carry this CA).
        var caCertificate = X509CertificateLoader.LoadCertificateFromFile(caCertPath);

        var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, serverCert, _, sslPolicyErrors) =>
                    ValidateAgainstInternalCa(serverCert, sslPolicyErrors, caCertificate),
            },
        };

        // Present the worker's client cert (the RequireCertificate side): the engine verifies it against
        // the SAME internal CA. Configured as a PEM cert+key pair (the cert-manager Secret's
        // tls.crt/tls.key). CA-only (no client pair) still pins the server but presents nothing.
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
    /// than an untrusted-root chain (EXPECTED — the CA is not in the system store) fails the handshake;
    /// the untrusted-root case is resolved by rebuilding the chain with the internal CA as the sole
    /// custom trust root.
    /// </summary>
    private static bool ValidateAgainstInternalCa(
        X509Certificate? serverCert, SslPolicyErrors sslPolicyErrors, X509Certificate2 caCertificate)
    {
        if (serverCert is null)
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
        return chain.Build(new X509Certificate2(serverCert));
    }
}
