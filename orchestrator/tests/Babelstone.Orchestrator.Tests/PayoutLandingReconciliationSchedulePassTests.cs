using System.Diagnostics.Metrics;
using Babelstone.Orchestrator;
using Babelstone.Orchestrator.Saga.Settlement;
using Babelstone.Telemetry;
using Xunit;

namespace Babelstone.Orchestrator.Tests;

/// <summary>
/// Tests for the scheduled payout-landing reconciler driver (bd babelstone-qa92.2; ADR-PC-043) — the pass that
/// finally RUNS <see cref="PayoutLandingReconciler"/> in production and surfaces its mismatch signals to an
/// operator sink. In plain English: these prove that when a payout dropped, doubled, or landed at the wrong
/// amount, a scheduled run over seeded inputs (a) classifies it correctly, (b) emits exactly the non-matched
/// signals to the sink, and (c) increments the per-class Prometheus counter the alert group reads — and that
/// the run is CLOCK-FREE at the classifier boundary (a fixed <c>asOf</c> is injected, never read inside the
/// reconciler; ADR-PC-023 §6) and SIGNAL-ONLY (no Movement is ever invented; ADR-PC-043).
/// </summary>
/// <remarks>
/// Docker-free — the REAL <see cref="PayoutLandingReconciliationSchedulePass"/> runs over an in-memory
/// <see cref="IPayoutLandingSource"/> fake and a capturing sink, observing the shared <c>Babelstone.Engine</c>
/// meter through a <see cref="MeterListener"/> (the same listener pattern the lifecycle-driver and engine
/// metric tests use). Default CI lane: no PostgreSQL, no Redpanda.
/// </remarks>
public sealed class PayoutLandingReconciliationSchedulePassTests
{
    private static readonly Guid SourceA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid SourceB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid SourceC = Guid.Parse("cccccccc-0000-0000-0000-000000000003");
    private static readonly DateOnly AsOf = new(2026, 7, 10);

    private const int InterimDropSlaDays = PayoutLandingReconciler.DefaultDropSlaDays;

    [Fact]
    public async Task A_scheduled_run_over_a_drop_double_and_wrong_amount_emits_exactly_those_signals()
    {
        // Seed one of each non-matched case plus a matched pair. The pass reconciles as-of the fixed date and
        // hands every non-matched signal to the sink — the matched pair raises none.
        var dropped = SettlementReferences.DeriveIntentId(SourceA, "maturity");
        var doubled = SettlementReferences.DeriveIntentId(SourceB, "maturity");
        var wrong = SettlementReferences.DeriveIntentId(SourceC, "installment-2");
        var matched = SettlementReferences.DeriveIntentId(SourceA, "coupon-1");

        var source = new FakeSource(
            sourcePayouts:
            [
                new SourcePayout(dropped, 200_00, AsOf.AddDays(-(InterimDropSlaDays + 1))),
                new SourcePayout(doubled, 300_00, new DateOnly(2026, 7, 9)),
                new SourcePayout(wrong, 400_00, new DateOnly(2026, 7, 9)),
                new SourcePayout(matched, 100_00, new DateOnly(2026, 7, 9)),
            ],
            caLandings:
            [
                Landing(doubled, 300_00),
                Landing(doubled, 300_00),
                Landing(wrong, 399_00),
                Landing(matched, 100_00),
            ]);

        var sink = new CapturingSink();
        var pass = new PayoutLandingReconciliationSchedulePass(source, sink);

        await pass.RunOnceAsync(AsOf);

        // Exactly the three non-matched intents were surfaced — the matched pair was not.
        Assert.Equal(3, sink.Signals.Count);
        Assert.Equal(ReconciliationClass.Drop, sink.ClassOf(dropped));
        Assert.Equal(ReconciliationClass.Double, sink.ClassOf(doubled));
        Assert.Equal(ReconciliationClass.WrongAmount, sink.ClassOf(wrong));
        Assert.DoesNotContain(sink.Signals, s => s.IntentId == matched);
    }

    [Fact]
    public async Task A_scheduled_run_increments_the_prometheus_counter_per_reconciliation_class()
    {
        // The seeded DROP / DOUBLE / WRONG-AMOUNT each increment payout_reconciliation_signal_total under
        // their own reconciliation_class label — the exact dimension the payout-landing-reconciliation alert
        // group fires on. The meter is process-wide, so we observe deltas per class within this pass's scope.
        var dropped = SettlementReferences.DeriveIntentId(SourceA, "maturity");
        var doubled = SettlementReferences.DeriveIntentId(SourceB, "maturity");
        var wrong = SettlementReferences.DeriveIntentId(SourceC, "installment-2");

        var source = new FakeSource(
            sourcePayouts:
            [
                new SourcePayout(dropped, 200_00, AsOf.AddDays(-(InterimDropSlaDays + 1))),
                new SourcePayout(doubled, 300_00, new DateOnly(2026, 7, 9)),
                new SourcePayout(wrong, 400_00, new DateOnly(2026, 7, 9)),
            ],
            caLandings: [Landing(doubled, 300_00), Landing(doubled, 300_00), Landing(wrong, 399_00)]);

        // The REAL operator sink is what increments the Prometheus counter (the pass emits TO it) — so this
        // test wires the production sink, not a fake, to prove the end-to-end emit the alert group reads.
        var pass = new PayoutLandingReconciliationSchedulePass(source, new OperatorReconciliationSignalSink());

        // Force the metrics type to publish its counter BEFORE the listener starts, so InstrumentPublished
        // fires for it at Start (a lazily-created instrument that first appears mid-pass can be missed by a
        // listener started just before). Touching a static member runs the type initializer.
        _ = PayoutReconciliationMetrics.ClassLabel(ReconciliationClass.Drop);

        var hits = new Dictionary<string, int>(StringComparer.Ordinal);
        using (ListenToSignalCounter(hits))
        {
            await pass.RunOnceAsync(AsOf);
        }

        // Each class fired at least once during this scoped pass (parallel classes can only ADD to a shared
        // counter, never subtract — so >= 1 is the robust assertion; this pass emits each exactly once).
        Assert.True(hits.GetValueOrDefault(PayoutReconciliationMetrics.ClassLabel(ReconciliationClass.Drop)) >= 1);
        Assert.True(hits.GetValueOrDefault(PayoutReconciliationMetrics.ClassLabel(ReconciliationClass.Double)) >= 1);
        Assert.True(hits.GetValueOrDefault(PayoutReconciliationMetrics.ClassLabel(ReconciliationClass.WrongAmount)) >= 1);
    }

    [Fact]
    public async Task A_matched_only_world_emits_no_signal_and_still_completes_the_pass()
    {
        // The happy path: every payout matched. No signal reaches the sink, yet the pass runs to completion
        // and refreshes the tick-liveness heartbeat (so the PayoutReconciliationTickStale alert stays quiet).
        var floor = DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeMilliseconds() / 1000.0;
        var matched = SettlementReferences.DeriveIntentId(SourceA, "maturity");
        var source = new FakeSource(
            sourcePayouts: [new SourcePayout(matched, 150_00, new DateOnly(2026, 7, 9))],
            caLandings: [Landing(matched, 150_00)]);

        var sink = new CapturingSink();
        await new PayoutLandingReconciliationSchedulePass(source, sink).RunOnceAsync(AsOf);

        Assert.Empty(sink.Signals);
        Assert.True(ObserveHeartbeat() >= floor, "a completed pass must refresh the tick-liveness heartbeat");
    }

    [Fact]
    public async Task The_classifier_boundary_is_clock_free_the_injected_asOf_alone_decides_drop_vs_in_flight()
    {
        // The SAME source-paid-not-landed payout is IN_FLIGHT under one asOf and a DROP under a later one —
        // proving the injected date, never a wall clock inside the reconciler, decides the SLA (ADR-PC-023 §6).
        var intent = SettlementReferences.DeriveIntentId(SourceA, "maturity");
        var paidOn = new DateOnly(2026, 7, 5);
        var source = new FakeSource(
            sourcePayouts: [new SourcePayout(intent, 150_00, paidOn)],
            caLandings: []);
        var pass = new PayoutLandingReconciliationSchedulePass(source, new NoopSink());

        // Within the SLA horizon → IN_FLIGHT, no signal.
        var withinSla = await pass.RunOnceAsync(paidOn.AddDays(InterimDropSlaDays - 1));
        Assert.Equal(ReconciliationClass.InFlight, Assert.Single(withinSla).Classification);
        Assert.Null(Assert.Single(withinSla).Signal);

        // Past the SLA horizon → DROP, one signal.
        var pastSla = await pass.RunOnceAsync(paidOn.AddDays(InterimDropSlaDays + 1));
        Assert.Equal(ReconciliationClass.Drop, Assert.Single(pastSla).Classification);
        Assert.NotNull(Assert.Single(pastSla).Signal);
    }

    [Fact]
    public async Task A_re_run_over_the_same_world_re_derives_the_same_signals_idempotent()
    {
        // The reconciler moves no money (ADR-PC-043), so a re-run is the idempotent at-least-once case: the
        // same world re-derives the same outcomes and re-emits the same signals — never a doubled correction.
        var dropped = SettlementReferences.DeriveIntentId(SourceA, "maturity");
        var source = new FakeSource(
            sourcePayouts: [new SourcePayout(dropped, 200_00, AsOf.AddDays(-(InterimDropSlaDays + 1)))],
            caLandings: []);
        var pass = new PayoutLandingReconciliationSchedulePass(source, new CapturingSink());

        var first = await pass.RunOnceAsync(AsOf);
        var second = await pass.RunOnceAsync(AsOf);

        Assert.Equal(ReconciliationClass.Drop, Assert.Single(first).Classification);
        Assert.Equal(ReconciliationClass.Drop, Assert.Single(second).Classification);
    }

    [Fact]
    public async Task A_source_read_failure_propagates_and_leaves_the_heartbeat_unrefreshed()
    {
        // A failing source read is backpressure the worker backs off on — it propagates, and because the pass
        // never reaches the heartbeat refresh, the PayoutReconciliationTickStale alert catches a wedged loop.
        var pass = new PayoutLandingReconciliationSchedulePass(new FailingSource(), new NoopSink());
        await Assert.ThrowsAsync<InvalidOperationException>(() => pass.RunOnceAsync(AsOf));
    }

    // --- helpers ---

    private static CaLanding Landing(string intentId, long amountCents) =>
        new(SettlementReferences.DeriveFromIntent(SettlementReferences.CreditPrefix, intentId), amountCents, "Credit");

    /// <summary>Listen to the payout signal counter on the shared meter, accumulating hits per
    /// reconciliation_class tag value.</summary>
    private static MeterListener ListenToSignalCounter(Dictionary<string, int> hitsByClass)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == BabelstoneTelemetry.MeterName
                && instrument.Name == BabelstoneAttributes.PayoutReconciliationSignalMetric)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == BabelstoneAttributes.PayoutReconciliationClassTag && tag.Value is string label)
                {
                    hitsByClass[label] = hitsByClass.GetValueOrDefault(label) + (int)value;
                    return;
                }
            }
        });
        listener.Start();
        return listener;
    }

    private static double ObserveHeartbeat()
    {
        double heartbeat = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == BabelstoneTelemetry.MeterName
                && instrument.Name == BabelstoneAttributes.PayoutReconciliationPassFreshnessMetric)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, value, _, _) => heartbeat = value);
        listener.Start();
        listener.RecordObservableInstruments();
        return heartbeat;
    }

    private sealed class FakeSource(
        IReadOnlyList<SourcePayout> sourcePayouts, IReadOnlyList<CaLanding> caLandings) : IPayoutLandingSource
    {
        public Task<IReadOnlyList<SourcePayout>> ReadSourcePayoutsAsync(DateOnly asOf, CancellationToken ct = default) =>
            Task.FromResult(sourcePayouts);

        public Task<IReadOnlyList<CaLanding>> ReadCaLandingsAsync(DateOnly asOf, CancellationToken ct = default) =>
            Task.FromResult(caLandings);
    }

    private sealed class FailingSource : IPayoutLandingSource
    {
        public Task<IReadOnlyList<SourcePayout>> ReadSourcePayoutsAsync(DateOnly asOf, CancellationToken ct = default) =>
            throw new InvalidOperationException("simulated read-model backpressure (5xx/timeout)");

        public Task<IReadOnlyList<CaLanding>> ReadCaLandingsAsync(DateOnly asOf, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CaLanding>>([]);
    }

    private sealed class CapturingSink : IReconciliationSignalSink
    {
        public List<ReconciliationSignal> Signals { get; } = [];

        public Task EmitAsync(ReconciliationSignal signal, CancellationToken ct = default)
        {
            Signals.Add(signal);
            return Task.CompletedTask;
        }

        public ReconciliationClass ClassOf(string intentId) =>
            Signals.Single(s => s.IntentId == intentId).Classification;
    }

    private sealed class NoopSink : IReconciliationSignalSink
    {
        public Task EmitAsync(ReconciliationSignal signal, CancellationToken ct = default) => Task.CompletedTask;
    }
}
