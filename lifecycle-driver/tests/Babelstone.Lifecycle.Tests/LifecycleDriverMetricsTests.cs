using System.Diagnostics.Metrics;
using Babelstone.Lifecycle;
using Babelstone.Telemetry;
using Xunit;

namespace Babelstone.Lifecycle.Tests;

/// <summary>
/// Tests for the driver's operational metrics surface (bd babelstone-1nkm.4) — Docker-free, driving the
/// REAL <see cref="LifecycleSchedulePass"/> over the in-memory claim ledger and observing the shared
/// <c>Babelstone.Engine</c> meter through a <see cref="MeterListener"/> (the same listener pattern the
/// engine's snapshot/reconciliation metric tests use). They pin the emit contract the
/// <c>lifecycle-driver</c> alert group reads:
/// <list type="bullet">
/// <item>a successful dispatch counts <c>lifecycle_dispatch_total</c> ONCE, tagged with the structural
/// <c>command_kind</c> (never PII), and records one <c>lifecycle_dispatch_lag_seconds</c> measurement;</item>
/// <item>a failed POST counts <c>lifecycle_dispatch_failure_total</c> and does NOT count a dispatch —
/// the backpressure signal is distinct from the throughput signal;</item>
/// <item>a completed pass refreshes the <c>lifecycle_pass_last_success_timestamp_seconds</c> tick-liveness
/// heartbeat — the always-on host's health surface.</item>
/// </list>
/// The instruments live on a process-wide static meter that parallel test classes also drive, so each
/// test mints a UNIQUE command kind and filters its measurements by that tag — the same dimension the
/// alert rules group by.
/// </summary>
public sealed class LifecycleDriverMetricsTests
{
    private static readonly DateOnly Today = new(2026, 7, 3);

    [Fact]
    public async Task A_successful_dispatch_counts_once_with_the_command_kind_tag_and_records_lag()
    {
        var kind = UniqueKind();
        var pass = NewPass(new NoopSink(), Decision(kind, dueAt: Today.AddDays(-2)));

        var dispatchHits = 0;
        var lags = new List<double>();
        using (ListenTo<long>(BabelstoneAttributes.LifecycleDispatchedMetric, kind, _ => dispatchHits++))
        using (ListenTo<double>(BabelstoneAttributes.LifecycleDispatchLagMetric, kind, lags.Add))
        {
            Assert.Single(await pass.RunOnceAsync(Today));
        }

        // Counted once, under this test's kind tag (the dimension the alert rules group by), and one lag
        // measurement landed: the occurrence was due two days ago, so the recorded lag is comfortably
        // >= one day — the "how late did it fire" signal.
        Assert.Equal(1, dispatchHits);
        var lag = Assert.Single(lags);
        Assert.True(lag >= TimeSpan.FromDays(1).TotalSeconds, $"expected >= 1 day of lag, saw {lag}s");
    }

    [Fact]
    public async Task A_failed_post_counts_a_failure_and_no_dispatch()
    {
        var kind = UniqueKind();
        var pass = NewPass(new AlwaysFailingSink(), Decision(kind, dueAt: Today));

        var failures = 0;
        var dispatches = 0;
        using (ListenTo<long>(BabelstoneAttributes.LifecycleDispatchFailureMetric, kind, _ => failures++))
        using (ListenTo<long>(BabelstoneAttributes.LifecycleDispatchedMetric, kind, _ => dispatches++))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => pass.RunOnceAsync(Today));
        }

        Assert.Equal(1, failures);
        Assert.Equal(0, dispatches); // the claim released un-recorded; nothing was dispatched.
    }

    [Fact]
    public async Task A_completed_pass_refreshes_the_tick_liveness_heartbeat()
    {
        var floor = DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeMilliseconds() / 1000.0;
        var pass = NewPass(new NoopSink(), Decision(UniqueKind(), dueAt: Today));
        await pass.RunOnceAsync(Today);

        // The observable gauge emits the Unix-epoch seconds of the most recent COMPLETED pass. It is a
        // process-wide high-water mark (parallel tests may also tick it — that only makes it fresher), so
        // assert freshness against the instant captured just before this pass — exactly the
        // `time() - metric` contract the LifecycleDriverTickStale alert reads.
        double? heartbeat = null;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == BabelstoneTelemetry.MeterName
                && instrument.Name == BabelstoneAttributes.LifecyclePassFreshnessMetric)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, value, _, _) => heartbeat = value);
        listener.Start();
        listener.RecordObservableInstruments();

        Assert.NotNull(heartbeat);
        Assert.True(heartbeat >= floor, $"expected heartbeat >= {floor}, saw {heartbeat}");
    }

    // --- helpers ---

    /// <summary>A per-test command kind, so this test's measurements are separable from parallel test
    /// classes driving the same process-wide meter (filtered on the command_kind tag).</summary>
    private static string UniqueKind() => $"test_kind_{Guid.NewGuid():N}";

    private static LifecycleSchedulePass NewPass(ILifecycleCommandSink sink, LifecycleCommandDecision decision) =>
        new([new FixedRule(decision)], new InMemoryLifecycleDispatchLedger(), sink);

    private static LifecycleCommandDecision Decision(string kind, DateOnly dueAt) =>
        new(
            InstanceId: Guid.NewGuid(),
            CommandKind: kind,
            OccurrenceKey: 1,
            RequestPath: "/v1/loans/00000000-0000-0000-0000-000000000000/installment",
            Body: new Dictionary<string, object?> { ["collection_account_ref"] = "acct-ref-001" },
            DueAt: dueAt);

    /// <summary>Listen to one instrument on the shared meter, forwarding only measurements tagged with
    /// THIS test's command kind.</summary>
    private static MeterListener ListenTo<T>(string instrumentName, string kind, Action<T> onMeasurement)
        where T : struct
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == BabelstoneTelemetry.MeterName && instrument.Name == instrumentName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<T>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == BabelstoneAttributes.LifecycleCommandKindTag && Equals(tag.Value, kind))
                {
                    onMeasurement(value);
                    return;
                }
            }
        });
        listener.Start();
        return listener;
    }

    private sealed class FixedRule(LifecycleCommandDecision decision) : ILifecycleCommandRule
    {
        public string FamilyName => "fake";

        public Task<IReadOnlyList<LifecycleCommandDecision>> EvaluateAsync(
            DateOnly asOf, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LifecycleCommandDecision>>([decision]);
    }

    private sealed class NoopSink : ILifecycleCommandSink
    {
        public Task DispatchAsync(
            LifecycleCommandDecision decision, Guid commandId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class AlwaysFailingSink : ILifecycleCommandSink
    {
        public Task DispatchAsync(
            LifecycleCommandDecision decision, Guid commandId, CancellationToken ct = default) =>
            throw new InvalidOperationException("simulated engine backpressure (5xx/timeout)");
    }
}
