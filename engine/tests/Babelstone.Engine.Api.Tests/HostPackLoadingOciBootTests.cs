using Babelstone.EventStore.Migrations;
using Babelstone.Packs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using Xunit;

namespace Babelstone.Engine.Api.Tests;

/// <summary>
/// The <c>Engine:PackRegistry=oci</c> host-boot path (bd babelstone-5grf, ADR-PC-007 §P4). The host
/// runs <see cref="HostPackLoading.LoadAsync"/> BEFORE <c>builder.Build()</c> in <c>Program.cs</c>,
/// so a thrown <see cref="PackLoadException"/> escapes Main and the process exits non-zero — there
/// is no degrade-and-serve. These tests drive <see cref="HostPackLoading.LoadAsync"/> directly (the
/// same call <c>Program.cs</c> awaits) so the fatal-on-boot posture is asserted as the exception the
/// host lets escape, without standing up a real OCI registry / oras / cosign: an unknown/unpinned
/// pack fails at the registry resolve, BEFORE any pull or verify.
/// </summary>
public sealed class HostPackLoadingOciBootTests
{
    // BuildVerifier needs cosign verification configured for oci mode; a public-key path satisfies it
    // without invoking cosign, because an unknown/unpinned pack throws at the registry resolve step
    // (OciPackStore step 1) before the verifier (step 2) is ever called.
    private static IConfiguration OciConfig(string primaryVersion = "pt.9999.1") =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
            [
                new KeyValuePair<string, string?>("Engine:PackRegistry", "oci"),
                new KeyValuePair<string, string?>("Engine:PackVersion", primaryVersion),
                new KeyValuePair<string, string?>("Engine:PackOciLayout", "true"),
                new KeyValuePair<string, string?>("Engine:CosignPublicKeyPath", "/dev/null"),
            ])
            .Build();

    [Trait("Category", "Integration")]
    [Fact]
    public async Task Oci_boot_fails_loud_when_the_primary_pack_is_unpinned()
    {
        // A real PostgreSQL with the schema applied but NO row in pack_versions for the configured
        // primary. The §P4 worklist read succeeds (empty), then the primary load resolves null and
        // throws — the exact PackLoadException Program.cs lets escape Main to exit non-zero.
        await using var pg = new PostgreSqlBuilder("postgres:18-alpine").Build();
        await pg.StartAsync();
        try
        {
            await new MigrationRunner(pg.GetConnectionString()).ApplyAsync();

            var ex = await Assert.ThrowsAsync<PackLoadException>(() => HostPackLoading.LoadAsync(
                OciConfig(primaryVersion: "pt.9999.1"),
                pg.GetConnectionString(),
                NullLogger.Instance));

            Assert.Equal("pt.9999.1", ex.PackVersion);
        }
        finally
        {
            await pg.DisposeAsync();
        }
    }

    [Fact]
    public async Task Oci_boot_fails_loud_when_the_worklist_read_cannot_reach_the_database()
    {
        // bd babelstone-5grf (c): ListLivePackVersionsAsync runs at startup, OUTSIDE the per-pack
        // try/catch. A DB-connectivity failure there must be FATAL-on-boot (no degrade-and-serve),
        // and is translated to a PackLoadException carrying the §P4 framing so the operator sees a
        // clear refuse-to-serve rather than a bare NpgsqlException. This needs no Docker — a
        // connection string to a closed port reproduces the connectivity failure deterministically.
        var unreachable =
            "Host=127.0.0.1;Port=1;Username=nobody;Password=nobody;Database=nope;Timeout=2;Command Timeout=2";

        var ex = await Assert.ThrowsAsync<PackLoadException>(() => HostPackLoading.LoadAsync(
            OciConfig(),
            unreachable,
            NullLogger.Instance));

        // The cause chain is preserved (the underlying connectivity exception is the inner), and the
        // message names the worklist read — so the boot failure is diagnosable, not opaque.
        Assert.NotNull(ex.InnerException);
        Assert.Contains("worklist", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
