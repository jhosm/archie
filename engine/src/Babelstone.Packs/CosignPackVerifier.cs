namespace Babelstone.Packs;

/// <summary>
/// How <c>cosign verify</c> is configured (ADR-PC-007 §P2). Production is keyless OIDC (the
/// issuer + certificate identity come from CI signing, story Q.5); local dev/CI may verify
/// against a public key. The concrete prod issuer/subject are configuration, never hardcoded.
/// </summary>
public sealed class CosignVerificationPolicy
{
    private readonly Func<string, IReadOnlyList<string>> _verifyArgs;

    private CosignVerificationPolicy(Func<string, IReadOnlyList<string>> verifyArgs) => _verifyArgs = verifyArgs;

    public IReadOnlyList<string> BuildVerifyArgs(string reference) => _verifyArgs(reference);

    /// <summary>Keyless OIDC verification (production / Q.5): the signer's OIDC issuer + certificate identity.</summary>
    public static CosignVerificationPolicy Keyless(string oidcIssuer, string certificateIdentity)
    {
        ArgumentException.ThrowIfNullOrEmpty(oidcIssuer);
        ArgumentException.ThrowIfNullOrEmpty(certificateIdentity);
        return new(reference =>
            ["verify", "--certificate-oidc-issuer", oidcIssuer, "--certificate-identity", certificateIdentity, reference]);
    }

    /// <summary>Public-key verification (local dev / CI without OIDC), mirroring pack.sh's <c>--key</c> form.</summary>
    public static CosignVerificationPolicy PublicKey(string publicKeyPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(publicKeyPath);
        return new(reference => ["verify", "--key", publicKeyPath, reference]);
    }
}

/// <summary>
/// Verifies a pack's cosign signature with the <c>cosign</c> CLI (ADR-PC-007 §P2), at load time
/// only. A verified signature is the attestation that the pack's CUE depths 1–4 passed in CI
/// (ADR-PC-006 §P3) — so the loader trusts the parse and does not re-run <c>cue vet</c>. Any
/// non-zero exit (unsigned, wrong signer, tampered digest) is fatal: it throws
/// <see cref="PackLoadException"/> rather than letting an unverified pack load.
/// </summary>
/// <param name="policy">How <c>cosign verify</c> is invoked — keyless OIDC (production) or public-key (dev/CI); see <see cref="CosignVerificationPolicy"/>.</param>
/// <param name="cosignExecutable">The cosign binary; defaults to PATH lookup (the mise-pinned cosign 3.0.6 in dev/CI).</param>
public sealed class CosignPackVerifier(CosignVerificationPolicy policy, string cosignExecutable = "cosign") : IPackVerifier
{
    public async Task VerifyAsync(string ociRef, string digest, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(ociRef);
        ArgumentException.ThrowIfNullOrEmpty(digest);

        var result = await ProcessRunner.RunAsync(cosignExecutable, policy.BuildVerifyArgs($"{ociRef}@{digest}"), ct);
        if (result.ExitCode != 0)
        {
            throw new PackLoadException(null, digest,
                $"cosign verify failed (exit {result.ExitCode}) — refusing to load an unverified pack: {ProcessRunner.Tail(result.StdErr)}");
        }
    }
}
