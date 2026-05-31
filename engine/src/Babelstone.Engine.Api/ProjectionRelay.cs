using Babelstone.Engine;

namespace Babelstone.Engine.Api;

/// <summary>Tuning for the in-process async projection relay.</summary>
public sealed record ProjectionRelayOptions
{
    /// <summary>How long to wait after an empty drain before polling again.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// Hosts the <see cref="ProjectionDrainer"/> as an in-process <see cref="BackgroundService"/>
/// (two-modes §5.4 async path) — the same co-hosted shape as the outbox relay. Each cycle drains
/// every async projection once; a clean empty cycle waits a poll interval, a backlog loops straight
/// on. A drain failure is backpressure: back off and retry, leaving the checkpoints where they are
/// (the projections are rebuildable and the apply is idempotent, so nothing is lost).
/// </summary>
public sealed class ProjectionRelayService(
    ProjectionDrainer drainer,
    ProjectionRegistry registry,
    ProjectionRelayOptions options,
    ILogger<ProjectionRelayService>? logger = null) : BackgroundService
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var asyncRunners = registry.AsyncRunners.ToList();
        var backoff = options.PollInterval;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var folded = 0;
                foreach (var runner in asyncRunners)
                {
                    folded += await drainer.DrainOnceAsync(runner, stoppingToken);
                }

                backoff = options.PollInterval; // a clean cycle resets the backoff
                if (folded == 0)
                {
                    await Task.Delay(options.PollInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // graceful shutdown
            }
            catch (Exception ex)
            {
                logger?.LogWarning(
                    ex, "Projection drain cycle failed; backing off {Backoff} and retrying (projections are rebuildable).", backoff);
                await Task.Delay(backoff, stoppingToken);
                backoff = backoff < MaxBackoff ? backoff + backoff : MaxBackoff;
            }
        }
    }
}

/// <summary>
/// The sync-mode post-commit hook (two-modes §5.4): after an append commits, drives the family's
/// SYNC projections within a bounded budget. A failure or timeout NEVER propagates — "the event is
/// true regardless of whether a projection consumed it" — it is logged and the lag surfaces via the
/// projection's own checkpoint. v1 declares every projection async, so this finds no sync runners
/// and no-ops; it is the v4 template, exercised by the post-commit-never-rolls-back test.
/// </summary>
public sealed class BudgetedPostCommitProjector(
    ProjectionDrainer drainer,
    ProjectionRegistry registry,
    TimeSpan budget,
    ILogger<BudgetedPostCommitProjector>? logger = null) : IPostCommitProjector
{
    public async Task NotifyAppendedAsync(string family, CancellationToken ct = default)
    {
        var syncRunners = registry.SyncRunnersForFamily(family).ToList();
        if (syncRunners.Count == 0)
        {
            return;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(budget);

        foreach (var runner in syncRunners)
        {
            try
            {
                await drainer.DrainOnceAsync(runner, cts.Token);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(
                    ex, "Sync projection '{Kind}' failed/timed out post-commit; the committed event is unaffected.", runner.Kind);
            }
        }
    }
}
