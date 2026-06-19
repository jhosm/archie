using System.Net.Http.Json;
using Babelstone.Pii;
using Babelstone.TestFixtures;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace Babelstone.LoadHarness.Tests;

/// <summary>
/// A dev-mode OpenBao (matching the dev stack image, <c>infra/compose.yaml</c>) with the transit engine
/// enabled, for the §M.6 key-cardinality probe (bd c14p.2). Mirrors the engine's own
/// <c>Babelstone.Pii.Tests.OpenBaoFixture</c> — the harness test assembly cannot reach that internal
/// fixture, so the small dev-container setup is repeated here, reusing the shared
/// <see cref="ContainerStartupGate"/> so the harness lane participates in the same startup throttle.
/// </summary>
/// <remarks>
/// DEV-MODE CAVEAT: <c>-dev</c> OpenBao is single-node, in-memory, auto-unsealed — it proves the
/// per-key-op latency SLOPE against cardinality but NOT the Raft snapshot size / unseal / join / replication
/// dimensions, which need a real Integrated-Storage cluster. Those are recorded as the residual HA/DR
/// sizing budget in ADR-PC-004 / ADR-PC-005, not measured here.
/// </remarks>
public sealed class OpenBaoCardinalityFixture : IAsyncLifetime
{
    private const string Token = "root";

    private readonly IContainer _container = new ContainerBuilder("openbao/openbao:2.5.4")
        .WithEnvironment("BAO_DEV_ROOT_TOKEN_ID", Token)
        .WithCommand("server", "-dev", "-dev-listen-address=0.0.0.0:8200")
        .WithPortBinding(8200, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilHttpRequestIsSucceeded(r => r.ForPort(8200).ForPath("/v1/sys/health")))
        .Build();

    public IPiiKeyStore CreateKeyStore() => new OpenBaoTransitClient(NewHttpClient(), Token);

    public async Task InitializeAsync()
    {
        await ContainerStartupGate.GatedStartAsync(() => _container.StartAsync());

        // Dev mode does not mount transit by default — enable it once for the suite.
        using var http = NewHttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/sys/mounts/transit");
        request.Headers.Add("X-Vault-Token", Token);
        request.Content = JsonContent.Create(new { type = "transit" });
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    private HttpClient NewHttpClient() => new()
    {
        BaseAddress = new Uri($"http://{_container.Hostname}:{_container.GetMappedPublicPort(8200)}/"),
    };
}
