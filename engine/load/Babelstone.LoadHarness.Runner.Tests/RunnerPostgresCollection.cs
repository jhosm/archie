using Xunit;

namespace Babelstone.LoadHarness.Runner.Tests;

/// <summary>
/// The xUnit collection that shares ONE <see cref="RunnerPostgresFixture"/> (a single Testcontainers
/// PostgreSQL with the engine migration set applied) across every runner integration class, so the
/// suite stands up one event-store container rather than one per class. The classes run serially within
/// the collection — each uses fresh, run-nonce-namespaced stream ids, so a shared database is safe.
/// </summary>
[CollectionDefinition("RunnerPostgres")]
public sealed class RunnerPostgresCollection : ICollectionFixture<RunnerPostgresFixture>
{
    // Marker type only — the body is intentionally empty (xUnit binds the fixture by the interface).
}
