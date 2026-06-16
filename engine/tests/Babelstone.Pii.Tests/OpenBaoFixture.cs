using System.Net.Http.Json;
using Babelstone.TestFixtures;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace Babelstone.Pii.Tests;

/// <summary>
/// A dev-mode OpenBao (matching the dev stack image) with the transit engine enabled.
/// Shared across an integration test class.
/// </summary>
public sealed class OpenBaoFixture : IAsyncLifetime
{
    private const string Token = "root";

    private readonly IContainer _container = new ContainerBuilder("openbao/openbao:2.5.4")
        .WithEnvironment("BAO_DEV_ROOT_TOKEN_ID", Token)
        .WithCommand("server", "-dev", "-dev-listen-address=0.0.0.0:8200")
        .WithPortBinding(8200, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilHttpRequestIsSucceeded(r => r.ForPort(8200).ForPath("/v1/sys/health")))
        .Build();

    public OpenBaoTransitClient CreateClient() => new(NewHttpClient(), Token);

    public async Task InitializeAsync()
    {
        await _container.GatedStartAsync();

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
