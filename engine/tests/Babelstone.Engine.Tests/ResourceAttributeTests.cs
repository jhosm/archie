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
        var attributes = WithEnvironment("Staging", () =>
            BuildResource(serviceName).Attributes.ToDictionary(a => a.Key, a => a.Value));

        Assert.Equal(serviceName, Assert.Contains("service.name", attributes));
        Assert.Equal(BabelstoneResource.ServiceNamespace, Assert.Contains(BabelstoneResource.ServiceNamespaceKey, attributes));

        // deployment.environment is present and carries the explicitly-set environment — the
        // resolver fails fast rather than defaulting, so a trace is never attributed to an
        // assumed environment.
        var environment = Assert.Contains(BabelstoneResource.DeploymentEnvironmentKey, attributes);
        Assert.Equal("Staging", environment as string);
    }

    [Fact]
    public void Service_namespace_is_the_estate_constant()
        => Assert.Equal("babelstone", BabelstoneResource.ServiceNamespace);

    [Fact]
    public void Environment_resolution_reads_the_explicit_variable()
    {
        var resolved = WithEnvironment("Production", BabelstoneResource.ResolveEnvironment);
        Assert.Equal("Production", resolved);
    }

    [Fact]
    public void Environment_resolution_fails_fast_when_unset()
    {
        // Neither variable set: the resolver MUST throw rather than default — a host cannot boot
        // with traces mis-attributed to an assumed environment (ADR-IC-007 §P1).
        WithBothEnvironmentVariablesCleared(() =>
            Assert.Throws<InvalidOperationException>(() => BabelstoneResource.ResolveEnvironment()));
    }

    private const string DotnetEnvVar = "DOTNET_ENVIRONMENT";
    private const string AspNetEnvVar = "ASPNETCORE_ENVIRONMENT";

    /// <summary>Runs <paramref name="body"/> with DOTNET_ENVIRONMENT forced and ASPNETCORE_ENVIRONMENT cleared, then restores both.</summary>
    private static T WithEnvironment<T>(string value, Func<T> body)
    {
        var savedDotnet = Environment.GetEnvironmentVariable(DotnetEnvVar);
        var savedAspNet = Environment.GetEnvironmentVariable(AspNetEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(DotnetEnvVar, value);
            Environment.SetEnvironmentVariable(AspNetEnvVar, null);
            return body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(DotnetEnvVar, savedDotnet);
            Environment.SetEnvironmentVariable(AspNetEnvVar, savedAspNet);
        }
    }

    /// <summary>Runs <paramref name="body"/> with both environment variables cleared, then restores them.</summary>
    private static void WithBothEnvironmentVariablesCleared(Action body)
    {
        var savedDotnet = Environment.GetEnvironmentVariable(DotnetEnvVar);
        var savedAspNet = Environment.GetEnvironmentVariable(AspNetEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(DotnetEnvVar, null);
            Environment.SetEnvironmentVariable(AspNetEnvVar, null);
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(DotnetEnvVar, savedDotnet);
            Environment.SetEnvironmentVariable(AspNetEnvVar, savedAspNet);
        }
    }
}
