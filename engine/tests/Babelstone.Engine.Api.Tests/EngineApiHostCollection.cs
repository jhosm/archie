using Xunit;

namespace Babelstone.Engine.Api.Tests;

/// <summary>
/// Serializes the host integration tests that boot the real <see cref="Program"/> host through
/// <c>WebApplicationFactory&lt;Program&gt;</c>. Those tests configure the composition root with
/// PROCESS-GLOBAL environment variables (<c>ConnectionStrings__Engine</c>, <c>Kafka__BootstrapServers</c>,
/// <c>ASPNETCORE_ENVIRONMENT</c>) read once at host-build time, and they share the static partial
/// <c>Program</c> — so two such classes running in parallel (xUnit's default across classes) would
/// stomp each other's env vars and bind a host to the wrong PostgreSQL container / broker. Sharing one
/// non-parallel collection makes them run sequentially without forcing the whole assembly serial.
/// </summary>
[CollectionDefinition(Name)]
public sealed class EngineApiHostCollection
{
    public const string Name = "EngineApiHost";
}
