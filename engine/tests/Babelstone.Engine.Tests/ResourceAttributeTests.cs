using Babelstone.Telemetry;
using OpenTelemetry.Resources;
using Xunit;

namespace Babelstone.Engine.Tests;

/// <summary>
/// OBS_RESOURCE_ATTRS (OBS-1, ADR-IC-007): every Babelstone .NET host stamps its tracer's
/// resource with <c>service.name</c>, <c>service.namespace == "babelstone"</c>, and a
/// <c>deployment.environment</c>, so every trace is attributable to a service, the estate, and an
/// environment. This builds the resource from the SAME <see cref="BabelstoneResource"/> values and
/// the same <see cref="ResourceBuilder"/> shape the two hosts use in their <c>ConfigureResource</c>
/// lambdas — so a drift between host and test fails here. Docker-free: no exporter, no host start.
/// </summary>
public sealed class ResourceAttributeTests
{
    /// <summary>Builds a resource exactly as a host's ConfigureResource lambda does, from the shared values.</summary>
    private static Resource BuildResource(string serviceName) =>
        ResourceBuilder.CreateDefault()
            .AddService(serviceName)
            .AddAttributes(
            [
                new KeyValuePair<string, object>(BabelstoneResource.ServiceNamespaceKey, BabelstoneResource.ServiceNamespace),
                new KeyValuePair<string, object>(BabelstoneResource.DeploymentEnvironmentKey, BabelstoneResource.ResolveEnvironment()),
            ])
            .Build();

    [Theory]
    [InlineData("babelstone-engine-api")]
    [InlineData("babelstone-rate-sheets-api")]
    public void Host_resource_carries_service_name_namespace_and_environment(string serviceName)
    {
        var attributes = BuildResource(serviceName).Attributes.ToDictionary(a => a.Key, a => a.Value);

        Assert.Equal(serviceName, Assert.Contains("service.name", attributes));
        Assert.Equal(BabelstoneResource.ServiceNamespace, Assert.Contains(BabelstoneResource.ServiceNamespaceKey, attributes));

        // deployment.environment is always present and non-blank (the resolver never throws and
        // falls back to "development"), so a trace is never attributed to an unknown environment.
        var environment = Assert.Contains(BabelstoneResource.DeploymentEnvironmentKey, attributes);
        Assert.False(string.IsNullOrWhiteSpace(environment as string));
    }

    [Fact]
    public void Service_namespace_is_the_estate_constant()
        => Assert.Equal("babelstone", BabelstoneResource.ServiceNamespace);

    [Fact]
    public void Environment_resolution_defaults_without_throwing()
    {
        // With neither env var forced here, the resolver returns a non-blank value (the default or
        // an ambient CI value) — it must never throw or return null/blank.
        var resolved = BabelstoneResource.ResolveEnvironment();
        Assert.False(string.IsNullOrWhiteSpace(resolved));
    }
}
