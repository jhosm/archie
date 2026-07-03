using System.Net;
using System.Text;
using Babelstone.Notification.Delivery;
using Xunit;
using static Babelstone.Notification.Delivery.Tests.DeliveryTestSupport;

namespace Babelstone.Notification.Delivery.Tests;

/// <summary>
/// The engine PII-resolve client (ADR-PC-025 §PII) over a fake HTTP seam: it calls
/// <c>GET /v1/pii/resolve?subject=…&amp;fields=…</c>, binds the snake_case field map, treats a shredded /
/// unknown / not-yet-deployed answer (404/410/204 or null fields) as "render without PII" — never an
/// error (ADR-PC-004 §P3) — and lets a 5xx surface as retryable backpressure.
/// </summary>
public sealed class EnginePiiResolveClientTests
{
    private static readonly string[] Fields = ["name", "nif"];

    [Fact]
    public async Task Resolves_the_requested_fields_by_reference()
    {
        var handler = new FakeHandler(_ => Json("""{"name":"Maria Silva","nif":"123456789"}"""));
        var client = Client(handler);
        var subject = Guid.NewGuid();

        var pii = await client.ResolveAsync(subject, Fields);

        Assert.Equal("Maria Silva", pii["name"]);
        Assert.Equal("123456789", pii["nif"]);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(
            $"https://engine.example.test/v1/pii/resolve?subject={subject:D}&fields=name%2Cnif",
            request.Uri!.ToString());
    }

    [Fact]
    public async Task A_shredded_field_arrives_null_and_is_dropped_not_rendered_as_a_string()
    {
        var handler = new FakeHandler(_ => Json("""{"name":null,"nif":"123456789"}"""));

        var pii = await Client(handler).ResolveAsync(Guid.NewGuid(), Fields);

        Assert.False(pii.ContainsKey("name"));
        Assert.Equal("123456789", pii["nif"]);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Gone)]
    [InlineData(HttpStatusCode.NoContent)]
    public async Task An_unknown_or_shredded_subject_or_undeployed_surface_resolves_to_no_pii(HttpStatusCode status)
    {
        var handler = new FakeHandler(_ => FakeHandler.Status(status));

        var pii = await Client(handler).ResolveAsync(Guid.NewGuid(), Fields);

        Assert.Empty(pii);
    }

    [Fact]
    public async Task A_5xx_surfaces_as_retryable_backpressure()
    {
        var handler = new FakeHandler(_ => FakeHandler.Status(HttpStatusCode.ServiceUnavailable));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => Client(handler).ResolveAsync(Guid.NewGuid(), Fields));
    }

    [Fact]
    public async Task No_requested_fields_means_no_call_at_all()
    {
        var handler = new FakeHandler(_ => FakeHandler.Status(HttpStatusCode.OK));

        var pii = await Client(handler).ResolveAsync(Guid.NewGuid(), []);

        Assert.Empty(pii);
        Assert.Empty(handler.Requests);
    }

    private static EnginePiiResolveClient Client(FakeHandler handler)
    {
        var factory = new BaseAddressHttpClientFactory(handler, new Uri("https://engine.example.test/"));
        return new EnginePiiResolveClient(factory);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class BaseAddressHttpClientFactory(HttpMessageHandler handler, Uri baseAddress) : IHttpClientFactory
    {
        private readonly HttpClient _client = new(handler, disposeHandler: false) { BaseAddress = baseAddress };

        public HttpClient CreateClient(string name) => _client;
    }
}
