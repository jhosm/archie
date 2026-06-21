using System.Diagnostics.Metrics;
using Babelstone.EventStore;
using Babelstone.Telemetry;
using Xunit;

namespace Babelstone.Engine.Tests;

/// <summary>
/// bd babelstone-sk7e — the engine emits the two ADR-PC-003 §P6 snapshotter operational signals so the
/// (previously guarded) snapshot-operations alert group goes live:
/// <list type="bullet">
///   <item><c>snapshot_lag_events</c> (observable gauge) — the largest un-snapshotted event count
///   observed across streams, raised in the runtime's post-commit snapshot path.</item>
///   <item><c>snapshot_hash_mismatch_total</c> (counter) — incremented where <c>SnapshotStore.Verify</c>
///   rejects a snapshot whose stored hash did not verify.</item>
/// </list>
/// Metrics-listener style (the same MeterListener pattern the reconciliation/inbox counter tests use).
/// Real PG18 for the lag emission (it drives the runtime's post-commit path); the hash-mismatch case is
/// store-side and needs no live append, but shares the fixture for one wiring.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SnapshotMetricsTests(EngineFixture fixture) : IClassFixture<EngineFixture>
{
    [Fact]
    public async Task Post_commit_snapshot_path_emits_the_lag_gauge()
    {
        // Append a stream WITHOUT crossing the per-N threshold (every-100) so no snapshot is taken but the
        // post-commit path still runs and computes the un-snapshotted depth (events-since-snapshot). The
        // depth here is newHead + 1 == 6 (no prior snapshot). RecordLag raises the process high-water
        // mark, which the observable gauge reports.
        var runtime = fixture.SnapshottingRuntime(everyNEvents: 100);
        var streamId = Guid.NewGuid();
        await runtime.AppendAsync(
            streamId, -1,
            [new Incremented(1), new Incremented(1), new Incremented(1),
             new Incremented(1), new Incremented(1), new Incremented(1)],
            fixture.Context());

        // The lag gauge is a per-PROCESS monotone high-water mark, so a parallel test may have raised it
        // higher — assert it is at LEAST the depth this append produced, which is the contract the alert
        // (snapshot_lag_events > 500) reads. The point is that it emits a real, non-null value at all.
        var lag = ObserveGauge(BabelstoneAttributes.SnapshotLagEventsMetric);
        Assert.NotNull(lag);
        Assert.True(lag.Value.Value >= 6, $"expected lag gauge >= 6 (this append's depth), saw {lag.Value.Value}");
    }

    [Fact]
    public async Task Hash_verification_failure_increments_the_mismatch_counter()
    {
        // Write a snapshot row whose stored hash is DELIBERATELY wrong, then read it back through the
        // typed SnapshotStore.Verify — which must reject it (throw) AND increment the §P6 (2) counter.
        var streamId = Guid.NewGuid();
        var state = new byte[] { 0x01, 0x02, 0x03 };
        var lastEventId = Guid.NewGuid();
        var tampered = new SnapshotRecord(
            StreamId: streamId,
            AtSequence: 0,
            LastEventId: lastEventId,
            StateHash: "deadbeef-not-the-real-hash",   // does NOT match Compute(state ‖ last_event_id)
            State: state,
            Trusted: false,
            CreatedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await fixture.SnapshotStorage.PutAsync(tampered);

        var store = new SnapshotStore<CounterState>(fixture.SnapshotStorage, new JsonStateSerializer<CounterState>());

        var captured = await CaptureCounterAsync(
            BabelstoneAttributes.SnapshotHashMismatchMetric,
            async () =>
            {
                // The read verifies the hash, finds the mismatch, counts it, and throws (the §8.3 guard).
                await Assert.ThrowsAsync<InvalidOperationException>(() => store.TryGetAsync(streamId));
            });

        Assert.Equal(1, captured.Hits);
        Assert.Equal(1, captured.Sum);
    }

    // --- metrics-listener helpers (the reconciliation/inbox counter-test pattern) ---

    private static Measurement<long>? ObserveGauge(string instrumentName)
    {
        Measurement<long>? match = null;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == BabelstoneTelemetry.MeterName && instrument.Name == instrumentName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => match = new Measurement<long>(value));
        listener.Start();
        listener.RecordObservableInstruments();
        listener.Dispose();
        return match;
    }

    private static async Task<(int Hits, long Sum)> CaptureCounterAsync(string instrumentName, Func<Task> act)
    {
        var hits = new List<long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == BabelstoneTelemetry.MeterName && instrument.Name == instrumentName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) =>
        {
            lock (hits)
            {
                hits.Add(value);
            }
        });
        listener.Start();

        await act();

        listener.Dispose(); // flush
        return (hits.Count, hits.Sum());
    }
}
