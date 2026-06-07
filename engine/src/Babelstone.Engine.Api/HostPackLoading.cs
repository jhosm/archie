using Babelstone.EventStore;
using Babelstone.Packs;

namespace Babelstone.Engine.Api;

/// <summary>
/// The result of the host's startup pack load: the <see cref="IPackStore"/> the engine resolves
/// against on the hot path, plus the instance's primary <see cref="VerifiedPack"/> (the configured
/// <c>Engine:PackVersion</c>) that the family modules compose over (<see cref="FamilyHostContext"/>).
/// </summary>
public sealed record HostPackLoad(IPackStore Store, VerifiedPack PrimaryPack);

/// <summary>
/// Wires the engine-instance's pack load at startup (ADR-PC-007 §P4). Two modes, selected by
/// <c>Engine:PackRegistry</c> — never a silent fallback:
/// <list type="bullet">
/// <item><b>oci</b> (production, §P3/§P4): the durable Postgres <c>pack_versions</c> registry
/// resolves each pinned version to its OCI coordinates; the cosign-verifying, oras-pulling
/// <see cref="OciPackStore"/> eager-loads EVERY pack version any live instance references
/// (<c>events.pack_version</c>) plus the configured primary, fail-loud — the host process aborts
/// non-zero on the first unresolvable/unverifiable/unpullable pack.</item>
/// <item><b>disk</b> (dev default — what <c>make up</c>/compose and the host integration tests use):
/// the walking-skeleton on-disk <see cref="HostPack"/> structural parse, unchanged. This is the
/// DEFAULT precisely so existing dev wiring keeps booting; the durable registry is opt-in.</item>
/// </list>
/// </summary>
public static class HostPackLoading
{
    /// <summary>
    /// Loads the instance's packs per the configured mode. The OCI path is async (it pulls + verifies
    /// out-of-process); on any failure it throws — the caller lets that escape so the host exits
    /// non-zero with the <see cref="PackLoadException"/> message in the log (§P4 fail-loud).
    /// </summary>
    public static async Task<HostPackLoad> LoadAsync(
        IConfiguration configuration, string connectionString, ILogger logger, CancellationToken ct = default)
    {
        var primaryVersion = configuration.GetValue("Engine:PackVersion", "pt.2026.1");
        var mode = configuration.GetValue("Engine:PackRegistry", "disk");

        if (string.Equals(mode, "disk", StringComparison.OrdinalIgnoreCase))
        {
            // Walking-skeleton dev path (unchanged): a disk-loaded VerifiedPack stands in for the
            // OCI loader. No durable registry, no eager pull/verify — the explicit opt-out.
            var pack = HostPack.Load(configuration["Engine:PacksDir"], primaryVersion);
            return new HostPackLoad(new SinglePackStore(primaryVersion, pack), pack);
        }

        if (!string.Equals(mode, "oci", StringComparison.OrdinalIgnoreCase))
        {
            // An unknown mode is a configuration error, not a reason to guess — fail loud.
            throw new InvalidOperationException(
                $"Engine:PackRegistry='{mode}' is not recognised; expected 'disk' (dev) or 'oci' (ADR-PC-007 §P3/§P4).");
        }

        // ── OCI mode (ADR-PC-007 §P3/§P4) ──────────────────────────────────────────────────────
        // The durable pack_versions registry resolves pins; cosign verifies; oras pulls by digest.
        var registry = new PostgresPackVersionRegistry(connectionString);

        // useOciLayout=true ⇒ fully-offline OCI-layout dir refs (dev/CI); false ⇒ a real registry
        // reference (production). cosign verifies keyless OIDC in production (Q.5) or against a
        // public key locally — the concrete issuer/subject are configuration, never hardcoded.
        var source = new OrasPackSource(useOciLayout: configuration.GetValue("Engine:PackOciLayout", false));
        var verifier = BuildVerifier(configuration);
        var store = new OciPackStore(registry, verifier, source);

        // §P4 worklist: every pack version any live instance references (events.pack_version), UNION
        // the configured primary so a fresh instance with an empty event log still loads its pack.
        var liveVersions = await registry.ListLivePackVersionsAsync(ct);
        var toLoad = new HashSet<string>(liveVersions, StringComparer.Ordinal) { primaryVersion };

        logger.LogInformation(
            "Eager-loading {Count} pinned pack version(s) from the pack_versions registry (ADR-PC-007 §P4): {Versions}",
            toLoad.Count, string.Join(", ", toLoad));

        VerifiedPack? primaryPack = null;
        foreach (var version in toLoad)
        {
            try
            {
                // resolve → cosign verify → pull-by-digest → structural parse → cache, all fail-loud.
                var pack = await store.GetAsync(version, ct);
                if (string.Equals(version, primaryVersion, StringComparison.Ordinal))
                {
                    primaryPack = pack;
                }
            }
            catch (PackLoadException ex)
            {
                // §P4: a pull/verify/parse failure at startup is FATAL — no silent degradation. Log
                // the offending pin clearly, then rethrow so the host exits non-zero.
                logger.LogCritical(ex,
                    "FATAL: pack version '{Version}' referenced by a live instance could not be loaded/verified — refusing to serve (ADR-PC-007 §P4).",
                    version);
                throw;
            }
        }

        // primaryPack is guaranteed non-null: primaryVersion is always in the worklist and a failed
        // load would have thrown above.
        return new HostPackLoad(store, primaryPack!);
    }

    private static IPackVerifier BuildVerifier(IConfiguration configuration)
    {
        // Public-key verification (local dev/CI) when a key path is configured; otherwise keyless
        // OIDC (production / Q.5). The issuer + identity are configuration, never hardcoded (§P2).
        var publicKeyPath = configuration["Engine:CosignPublicKeyPath"];
        if (!string.IsNullOrEmpty(publicKeyPath))
        {
            return new CosignPackVerifier(CosignVerificationPolicy.PublicKey(publicKeyPath));
        }

        var issuer = configuration["Engine:CosignOidcIssuer"]
            ?? throw new InvalidOperationException(
                "Engine:PackRegistry='oci' needs cosign verification configured: set Engine:CosignPublicKeyPath (dev) " +
                "or Engine:CosignOidcIssuer + Engine:CosignCertificateIdentity (production, ADR-PC-007 §P2).");
        var identity = configuration["Engine:CosignCertificateIdentity"]
            ?? throw new InvalidOperationException(
                "Engine:CosignOidcIssuer is set but Engine:CosignCertificateIdentity is missing (ADR-PC-007 §P2).");
        return new CosignPackVerifier(CosignVerificationPolicy.Keyless(issuer, identity));
    }
}

/// <summary>
/// A degenerate <see cref="IPackStore"/> holding the single disk-loaded pack of the walking-skeleton
/// dev path. It satisfies the same load-time/hot-path split (<see cref="Resolve"/> is pure) so the
/// disk mode presents the identical seam as <see cref="OciPackStore"/> to anything downstream.
/// </summary>
internal sealed class SinglePackStore(string packVersion, VerifiedPack pack) : IPackStore
{
    public Task<VerifiedPack> GetAsync(string version, CancellationToken ct = default)
        => string.Equals(version, packVersion, StringComparison.Ordinal)
            ? Task.FromResult(pack)
            : throw new PackLoadException(version, null,
                $"the disk-backed dev pack store holds only '{packVersion}'; configure Engine:PackRegistry=oci to load others.");

    public VerifiedPack Resolve(string version)
        => string.Equals(version, packVersion, StringComparison.Ordinal)
            ? pack
            : throw new PackLoadException(version, null,
                $"the disk-backed dev pack store holds only '{packVersion}'.");
}
