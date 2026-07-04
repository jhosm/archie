using Babelstone.Engine.Hosting;
using Babelstone.EventStore;
using Babelstone.FinancialTypes;
using Xunit;

namespace Babelstone.Engine.Tests;

/// <summary>
/// The four production bulk-operation adapters (bd babelstone-qpiw.5, ADR-PC-035): each maps a
/// frozen work-table row to a store-only cross-cutting event. In plain English: these pin the pure
/// per-item logic — which instances a job skips vs applies, and exactly what event it builds —
/// with no runner, no HTTP, no database, so the money-cents and no-PII rules are covered in
/// isolation. Every adapter is PURE data mapping (no clock, no I/O), so a re-claimed row
/// re-derives the identical event and the command-id dedupe holds (ADR-PC-035).
/// </summary>
public sealed class BulkOperationStrategyTests
{
    private static BulkOperationTargetRow Target(
        Guid? instanceId = null, string? itemParamsJson = null, string? preconditionInputJson = null)
        => new(
            TargetId: Guid.NewGuid(),
            JobId: Guid.NewGuid(),
            InstanceId: instanceId ?? Guid.NewGuid(),
            Status: "PENDING",
            ItemParamsJson: itemParamsJson,
            PreconditionInputJson: preconditionInputJson,
            Attempts: 0,
            FailureReason: null,
            CommitSequence: null,
            ClaimedAt: null,
            ProcessedAt: null,
            CreatedAt: default);

    // --- PackVersionMigrated ---

    [Fact]
    public void PackVersion_applies_when_the_instance_is_still_on_from_version()
    {
        var strategy = new PackVersionMigratedBulkStrategy();
        var target = Target(
            itemParamsJson: """{"from_pack_version":"pt.2026.1","to_pack_version":"pt.2027.1","migration_id":"mig-1","operator_actor":"operator:ops"}""",
            preconditionInputJson: """{"current_pack_version":"pt.2026.1"}""");

        Assert.IsType<BulkPreconditionVerdict.Apply>(strategy.EvaluatePrecondition(target));

        var @event = Assert.IsType<PackVersionMigrated>(strategy.CreateEvent(target));
        Assert.Equal(target.InstanceId, @event.InstanceId);
        Assert.Equal("pt.2026.1", @event.FromPackVersion);
        Assert.Equal("pt.2027.1", @event.ToPackVersion);
        Assert.Equal("mig-1", @event.MigrationId);
        Assert.Equal("operator:ops", @event.OperatorActor);
    }

    [Fact]
    public void PackVersion_skips_an_instance_that_already_moved_off_from_version()
    {
        var strategy = new PackVersionMigratedBulkStrategy();
        var target = Target(
            itemParamsJson: """{"from_pack_version":"pt.2026.1","to_pack_version":"pt.2027.1","migration_id":"mig-1","operator_actor":"operator:ops"}""",
            preconditionInputJson: """{"current_pack_version":"pt.2027.1"}""");

        var verdict = Assert.IsType<BulkPreconditionVerdict.Skip>(strategy.EvaluatePrecondition(target));
        Assert.Contains("pt.2027.1", verdict.Reason);
    }

    [Fact]
    public void PackVersion_applies_when_no_current_pin_was_frozen()
    {
        // No precondition_input means the registering surface already narrowed the set.
        var strategy = new PackVersionMigratedBulkStrategy();
        var target = Target(
            itemParamsJson: """{"from_pack_version":"pt.2026.1","to_pack_version":"pt.2027.1","migration_id":"mig-1","operator_actor":"operator:ops"}""");

        Assert.IsType<BulkPreconditionVerdict.Apply>(strategy.EvaluatePrecondition(target));
    }

    [Fact]
    public void PackVersion_refuses_an_identical_from_and_to()
    {
        var strategy = new PackVersionMigratedBulkStrategy();
        var target = Target(
            itemParamsJson: """{"from_pack_version":"pt.2026.1","to_pack_version":"pt.2026.1","migration_id":"mig-1","operator_actor":"operator:ops"}""");

        Assert.Throws<InvalidOperationException>(() => strategy.CreateEvent(target));
    }

    [Fact]
    public void PackVersion_refuses_missing_item_params()
    {
        var strategy = new PackVersionMigratedBulkStrategy();
        Assert.Throws<InvalidOperationException>(() => strategy.CreateEvent(Target()));
    }

    // --- SchemaVersionMigrated ---

    [Fact]
    public void SchemaVersion_applies_when_still_on_from_version_and_builds_the_event()
    {
        var strategy = new SchemaVersionMigratedBulkStrategy();
        var target = Target(
            itemParamsJson: """{"from_schema_version":"term_deposit@2026.1","to_schema_version":"term_deposit@2027.1","migration_id":"mig-2","operator_actor":"operator:ops"}""",
            preconditionInputJson: """{"current_schema_version":"term_deposit@2026.1"}""");

        Assert.IsType<BulkPreconditionVerdict.Apply>(strategy.EvaluatePrecondition(target));

        var @event = Assert.IsType<SchemaVersionMigrated>(strategy.CreateEvent(target));
        Assert.Equal("term_deposit@2026.1", @event.FromSchemaVersion);
        Assert.Equal("term_deposit@2027.1", @event.ToSchemaVersion);
        Assert.Equal("mig-2", @event.MigrationId);
    }

    [Fact]
    public void SchemaVersion_skips_an_instance_that_already_moved()
    {
        var strategy = new SchemaVersionMigratedBulkStrategy();
        var target = Target(
            itemParamsJson: """{"from_schema_version":"term_deposit@2026.1","to_schema_version":"term_deposit@2027.1","migration_id":"mig-2","operator_actor":"operator:ops"}""",
            preconditionInputJson: """{"current_schema_version":"term_deposit@2027.1"}""");

        Assert.IsType<BulkPreconditionVerdict.Skip>(strategy.EvaluatePrecondition(target));
    }

    // --- FundsHeld ---

    [Fact]
    public void FundsHeld_applies_every_target_and_carries_integer_cents()
    {
        var strategy = new FundsHeldBulkStrategy();
        var target = Target(
            itemParamsJson: """{"hold_id":"hold-1","held_amount_cents":150075,"legal_reference":"case-42","hold_expires_at":"2026-12-31"}""");

        Assert.IsType<BulkPreconditionVerdict.Apply>(strategy.EvaluatePrecondition(target));

        var @event = Assert.IsType<FundsHeld>(strategy.CreateEvent(target));
        Assert.Equal(target.InstanceId, @event.InstanceId);
        Assert.Equal("hold-1", @event.HoldId);
        Assert.Equal(new Money(150075), @event.HeldAmount);
        Assert.Equal("case-42", @event.LegalReference);
        Assert.Equal(new DateOnly(2026, 12, 31), @event.HoldExpiresAt);
    }

    [Fact]
    public void FundsHeld_allows_an_open_ended_hold_with_no_expiry()
    {
        var strategy = new FundsHeldBulkStrategy();
        var target = Target(
            itemParamsJson: """{"hold_id":"hold-1","held_amount_cents":100,"legal_reference":"case-42"}""");

        var @event = Assert.IsType<FundsHeld>(strategy.CreateEvent(target));
        Assert.Null(@event.HoldExpiresAt);
    }

    [Fact]
    public void FundsHeld_refuses_a_fractional_amount_rather_than_rounding_it()
    {
        // A float that leaked into the money field is a bug, not something to round (ADR-PC-010).
        var strategy = new FundsHeldBulkStrategy();
        var target = Target(
            itemParamsJson: """{"hold_id":"hold-1","held_amount_cents":150.5,"legal_reference":"case-42"}""");

        Assert.Throws<InvalidOperationException>(() => strategy.CreateEvent(target));
    }

    [Fact]
    public void FundsHeld_refuses_a_stringly_typed_amount()
    {
        var strategy = new FundsHeldBulkStrategy();
        var target = Target(
            itemParamsJson: """{"hold_id":"hold-1","held_amount_cents":"150","legal_reference":"case-42"}""");

        Assert.Throws<InvalidOperationException>(() => strategy.CreateEvent(target));
    }

    [Fact]
    public void FundsHeld_refuses_a_non_positive_amount()
    {
        var strategy = new FundsHeldBulkStrategy();
        var target = Target(
            itemParamsJson: """{"hold_id":"hold-1","held_amount_cents":0,"legal_reference":"case-42"}""");

        Assert.Throws<InvalidOperationException>(() => strategy.CreateEvent(target));
    }

    // --- AccountFrozen ---

    [Fact]
    public void AccountFrozen_applies_every_target_and_carries_a_machine_reason_code()
    {
        var strategy = new AccountFrozenBulkStrategy();
        var target = Target(
            itemParamsJson: """{"freeze_id":"frz-1","freeze_reason":"AML_SCREENING","compliance_actor":"service:compliance","freeze_expires_at":"2026-09-30"}""");

        Assert.IsType<BulkPreconditionVerdict.Apply>(strategy.EvaluatePrecondition(target));

        var @event = Assert.IsType<AccountFrozen>(strategy.CreateEvent(target));
        Assert.Equal(target.InstanceId, @event.InstanceId);
        Assert.Equal("frz-1", @event.FreezeId);
        Assert.Equal("AML_SCREENING", @event.FreezeReason);
        Assert.Equal("service:compliance", @event.ComplianceActor);
        Assert.Equal(new DateOnly(2026, 9, 30), @event.FreezeExpiresAt);
    }

    [Theory]
    [InlineData("Account frozen for John Smith")] // free-text PII risk
    [InlineData("aml_screening")]                 // lowercase — not a stable machine code
    [InlineData("123_BAD")]                        // must start with a letter
    [InlineData("")]
    public void AccountFrozen_refuses_a_non_machine_code_freeze_reason(string reason)
    {
        var strategy = new AccountFrozenBulkStrategy();
        var target = Target(
            itemParamsJson: $$"""{"freeze_id":"frz-1","freeze_reason":{{System.Text.Json.JsonSerializer.Serialize(reason)}},"compliance_actor":"service:compliance"}""");

        Assert.Throws<InvalidOperationException>(() => strategy.CreateEvent(target));
    }

    [Fact]
    public void The_four_adapters_expose_the_expected_operation_kinds()
    {
        Assert.Equal("PackVersionMigrated", new PackVersionMigratedBulkStrategy().OperationKind);
        Assert.Equal("SchemaVersionMigrated", new SchemaVersionMigratedBulkStrategy().OperationKind);
        Assert.Equal("FundsHeld", new FundsHeldBulkStrategy().OperationKind);
        Assert.Equal("AccountFrozen", new AccountFrozenBulkStrategy().OperationKind);
    }
}
