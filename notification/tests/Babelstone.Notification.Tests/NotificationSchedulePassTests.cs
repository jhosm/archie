using Babelstone.Cadence;
using Babelstone.Notification;
using Xunit;

namespace Babelstone.Notification.Tests;

/// <summary>
/// Tests for <see cref="NotificationSchedulePass"/> — the notification core's family-AGNOSTIC per-tick
/// engine (ADR-IC-019 §D2 / ADR-PC-025 slot 4). They cover the bd babelstone-60n8.2 idempotency acceptance
/// criterion at the layer that now owns it — the core, not a family rule:
/// <list type="bullet">
/// <item>running a pass TWICE over the same decisions produces NO duplicate (dedupe on the composite
/// <c>notification_id</c>);</item>
/// <item>a genuinely new decision on a later pass is still raised (dedupe is keyed, not a blunt "already
/// ran" flag);</item>
/// <item>the pass is family-agnostic — it dedupes whatever a <see cref="INotificationScheduleRule"/>
/// returns, driven here by a fake rule that names no family.</item>
/// </list>
/// The as-of date is an INPUT (no clock read inside the pass), and the real
/// <see cref="InMemoryDedupeLedger"/> backs the dedupe.
/// </summary>
public sealed class NotificationSchedulePassTests
{
    private const string TemplateRef = "pt.test.reminder";
    private static readonly DateOnly Today = new(2026, 6, 24);

    [Fact]
    public async Task Running_the_pass_twice_over_the_same_decisions_produces_no_duplicate()
    {
        var instance = Guid.NewGuid();
        var occurrence = Today.AddDays(7);
        var pass = NewPass(new FakeRule(_ => [Decision(instance, occurrence)]));

        var first = await pass.RunOnceAsync(Today);
        var second = await pass.RunOnceAsync(Today);

        Assert.Single(first);
        Assert.Empty(second);

        // The id the first pass minted is exactly the deterministic composite key (slot 4).
        Assert.Equal(NotificationId.Compute(instance, TemplateRef, occurrence), first[0].NotificationId);
        Assert.Equal(instance, first[0].InstanceId);
    }

    [Fact]
    public async Task A_genuinely_new_decision_on_a_later_pass_is_still_raised()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var calendar = new List<ReminderDecision> { Decision(first, Today.AddDays(2)) };
        var pass = NewPass(new FakeRule(_ => calendar.ToArray()));

        var pass1 = await pass.RunOnceAsync(Today);
        calendar.Add(Decision(second, Today.AddDays(4)));
        var pass2 = await pass.RunOnceAsync(Today);

        Assert.Single(pass1);
        Assert.Equal(first, pass1[0].InstanceId);
        Assert.Single(pass2);
        Assert.Equal(second, pass2[0].InstanceId);
    }

    [Fact]
    public async Task The_pass_enumerates_every_registered_rule()
    {
        // Family-agnostic by construction: the pass raises the decisions of ALL registered rules, so a
        // second family's rule contributes alongside the first with no core change (ADR-IC-019 §D2).
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var pass = NewPass(
            new FakeRule(_ => [Decision(a, Today.AddDays(1))]),
            new FakeRule(_ => [Decision(b, Today.AddDays(2))]));

        var raised = await pass.RunOnceAsync(Today);

        Assert.Equal(2, raised.Count);
        Assert.Contains(raised, x => x.InstanceId == a);
        Assert.Contains(raised, x => x.InstanceId == b);
    }

    // --- helpers ---

    private static NotificationSchedulePass NewPass(params INotificationScheduleRule[] rules) =>
        new(rules, new InMemoryDedupeLedger());

    private static ReminderDecision Decision(Guid instanceId, DateOnly occurrence) =>
        new(instanceId, TemplateRef, occurrence, Today, new Dictionary<string, long>());

    private sealed class FakeRule(Func<DateOnly, IReadOnlyList<ReminderDecision>> respond) : INotificationScheduleRule
    {
        public string FamilyName => "fake";

        public Task<IReadOnlyList<ReminderDecision>> EvaluateAsync(DateOnly asOf, CancellationToken ct = default) =>
            Task.FromResult(respond(asOf));
    }
}
