using Babelstone.Cadence;
using Babelstone.Notification;
using Xunit;

namespace Babelstone.Notification.Tests;

/// <summary>
/// The core's outbound hand-off (bd babelstone-60n8.4): when a delivery sink is composed, the schedule
/// pass forwards EXACTLY the reminders it newly raised — deduped repeats are never re-forwarded (the
/// at-least-once producer side stays anchored on the composite id, ADR-PC-025 slot 4) — and with NO sink
/// composed the pass runs exactly as before (delivery is optional and post-flag).
/// </summary>
public sealed class NotificationSchedulePassDeliverySinkTests
{
    private const string TemplateRef = "pt.test.reminder";
    private static readonly DateOnly Today = new(2026, 7, 2);

    [Fact]
    public async Task Newly_raised_reminders_are_forwarded_to_the_sink_once()
    {
        var instance = Guid.NewGuid();
        var sink = new RecordingSink();
        var rule = new FakeRule(_ => [Decision(instance, Today.AddDays(7))]);
        var pass = new NotificationSchedulePass([rule], new InMemoryDedupeLedger(), logger: null, sink);

        var first = await pass.RunOnceAsync(Today);
        var second = await pass.RunOnceAsync(Today); // same world — dedupe absorbs, nothing forwarded

        Assert.Single(first);
        Assert.Empty(second);
        var forwarded = Assert.Single(sink.Batches);
        Assert.Equal(first[0].NotificationId, Assert.Single(forwarded).NotificationId);
    }

    [Fact]
    public async Task Without_a_sink_the_pass_runs_unchanged()
    {
        var rule = new FakeRule(_ => [Decision(Guid.NewGuid(), Today.AddDays(7))]);
        var pass = new NotificationSchedulePass([rule], new InMemoryDedupeLedger());

        Assert.Single(await pass.RunOnceAsync(Today)); // the pre-delivery scheduler shape still works
    }

    private static ReminderDecision Decision(Guid instance, DateOnly occurrence) => new(
        InstanceId: instance,
        TemplateRef: TemplateRef,
        OccurrenceKey: occurrence,
        DueAt: Today,
        Amounts: new Dictionary<string, long> { ["total_payout_cents"] = 100_000 });

    private sealed class RecordingSink : INotificationDeliverySink
    {
        public List<IReadOnlyList<RaisedReminder>> Batches { get; } = [];

        public Task EnqueueAsync(IReadOnlyList<RaisedReminder> raised, CancellationToken ct = default)
        {
            Batches.Add(raised);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRule(Func<DateOnly, IReadOnlyList<ReminderDecision>> evaluate) : INotificationScheduleRule
    {
        public string FamilyName => "fake_family";

        public Task<IReadOnlyList<ReminderDecision>> EvaluateAsync(DateOnly asOf, CancellationToken ct = default) =>
            Task.FromResult(evaluate(asOf));
    }
}
