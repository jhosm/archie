using DotNet.Testcontainers.Containers;

namespace Babelstone.TestFixtures;

/// <summary>
/// A process-wide throttle on how many Testcontainers containers may be in their START phase at
/// once. It does NOT cap how many containers RUN concurrently — once a container is up the
/// semaphore is released and the container keeps running for its test class's lifetime.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> xUnit runs test classes as parallel collections, and ~24 integration
/// classes each spin up their OWN Postgres / Redpanda / OpenBao container in
/// <c>InitializeAsync</c>. When the whole assembly starts, that many container startups race for
/// the Docker daemon, host ports, and image-pull/extraction bandwidth at the same instant — a
/// thundering herd that overwhelms the daemon, so a DIFFERENT random subset times out in
/// <c>InitializeAsync</c> on each run (every class passes in isolation). Throttling only the
/// STARTUP phase keeps per-class containers and class-level parallelism intact while ensuring the
/// daemon is never asked to bring up more than <see cref="DefaultConcurrency"/> containers at once.
/// </para>
/// <para>
/// <b>Scope.</b> The gate is a <c>static SemaphoreSlim</c>, i.e. PER-PROCESS. That is the correct
/// scope: the flakiness is within a single test-assembly run (xUnit parallelises classes inside
/// one <c>dotnet test</c>), and CI runs each test project as its own <c>dotnet test</c> process. A
/// cross-process OS semaphore would be unnecessary machinery.
/// </para>
/// <para>
/// <b>Tuning.</b> The limit defaults to <see cref="DefaultConcurrency"/> and is overridable via the
/// <c>BABELSTONE_TEST_CONTAINER_STARTUP_CONCURRENCY</c> environment variable (any positive integer;
/// a missing, blank, non-numeric, or non-positive value falls back to the default). Lower it on a
/// resource-constrained host or CI runner if startups still flake; raise it on a beefy machine to
/// trade Docker-daemon pressure for wall-clock speed.
/// </para>
/// </remarks>
public static class ContainerStartupGate
{
    /// <summary>
    /// Environment variable that overrides the concurrent-startup limit. Must parse to a positive
    /// integer; otherwise the default is used.
    /// </summary>
    public const string ConcurrencyEnvVar = "BABELSTONE_TEST_CONTAINER_STARTUP_CONCURRENCY";

    /// <summary>
    /// Conservative default cap on concurrent container startups. Chosen to keep the Docker daemon
    /// out of the thundering-herd regime that produced the integration-lane flakiness while still
    /// letting a few containers warm up in parallel.
    /// </summary>
    public const int DefaultConcurrency = 4;

    private static readonly SemaphoreSlim Gate = new(ResolveConcurrency(), ResolveConcurrency());

    private static int ResolveConcurrency()
    {
        var raw = Environment.GetEnvironmentVariable(ConcurrencyEnvVar);
        if (int.TryParse(raw, out var parsed) && parsed > 0)
        {
            return parsed;
        }

        return DefaultConcurrency;
    }

    /// <summary>
    /// Starts <paramref name="container"/> under the startup gate: it acquires the semaphore, awaits
    /// <see cref="IContainer.StartAsync"/> (only the STARTUP is gated — the container then runs for
    /// the rest of the class's lifetime), and releases the slot in a <c>finally</c> once the
    /// container is up.
    /// </summary>
    public static async Task GatedStartAsync(this IContainer container, CancellationToken ct = default)
        => await GatedStartAsync(() => container.StartAsync(ct), ct);

    /// <summary>
    /// Runs an arbitrary container-startup delegate under the startup gate. Use this overload for
    /// startups that are not a bare <see cref="IContainer.StartAsync"/> call — e.g. a fixture's own
    /// <c>InitializeAsync</c> that builds the container, starts it, and runs post-start setup.
    /// </summary>
    public static async Task GatedStartAsync(Func<Task> start, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            await start();
        }
        finally
        {
            Gate.Release();
        }
    }
}
