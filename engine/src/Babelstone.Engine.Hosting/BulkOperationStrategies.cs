using System.Text.Json;
using Babelstone.EventStore;
using Babelstone.FinancialTypes;

namespace Babelstone.Engine.Hosting;

// The four production IBulkOperationStrategy adapters (ADR-PC-035, bd babelstone-qpiw.5): the
// engine-declared cross-cutting operational events of CrossCuttingEvents.cs, each ridden over the
// ONE generic runner as an optional precondition + a per-instance event factory — never a bespoke
// execution path. All four are PURE data mapping over the frozen work-table row (no I/O, no
// clock, ADR-PC-035), family-agnostic (they read only opaque ids and frozen JSON, ADR-PC-021),
// and build STORE-ONLY events (ADR-IC-017 — BulkInstanceAppender enforces it fail-loud). A
// malformed frozen param THROWS, which the drainer records as that ONE row FAILED (operational-
// tier reason, per-item isolation) — never a silent skip and never an aborted job.

/// <summary>
/// Shared frozen-JSON readers for the bulk adapters: strict, fail-loud accessors over a target
/// row's <c>item_params</c> / <c>precondition_input</c>. In plain English: the params were frozen
/// at registration, so if they are missing or the wrong shape the ITEM is broken — these throw a
/// precise <see cref="InvalidOperationException"/> (naming the parameter, never echoing amounts
/// or PII) so the row fails classifiably and stays selectively retryable (ADR-PC-035).
/// </summary>
internal static class BulkItemParams
{
    /// <summary>The row's <c>item_params</c> as a parsed JSON object; throws when absent or not an object.</summary>
    public static JsonElement Require(BulkOperationTargetRow target, string operationKind)
    {
        if (target.ItemParamsJson is null)
        {
            throw new InvalidOperationException(
                $"operation '{operationKind}' requires frozen item_params on every target; this row has none.");
        }

        var root = JsonDocument.Parse(target.ItemParamsJson).RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                $"operation '{operationKind}' item_params must be a JSON object; got {root.ValueKind}.");
        }

        return root;
    }

    /// <summary>A required non-blank string property.</summary>
    public static string RequireString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidOperationException(
                $"item_params.{name} is required and must be a non-blank JSON string.");
        }

        return value.GetString()!;
    }

    /// <summary>
    /// A required INTEGER-cents amount (ADR-PC-010 §P1/§P2): the value must be a JSON NUMBER that
    /// is an exact <see cref="long"/> — a fractional number (a float that leaked into a money
    /// field) or a string is refused loud, never rounded and never coerced. The single sanctioned
    /// decimal→cents boundary is <see cref="Money.FromCents(decimal)"/>; a frozen wire param gets
    /// no rounding boundary at all.
    /// </summary>
    public static long RequireIntegerCents(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            throw new InvalidOperationException(
                $"item_params.{name} is required and must be a JSON integer number of cents (ADR-PC-010) — never a string and never a float.");
        }

        if (!value.TryGetInt64(out var cents))
        {
            throw new InvalidOperationException(
                $"item_params.{name} must be an exact integer number of cents (ADR-PC-010) — a fractional or out-of-range amount is refused, not rounded.");
        }

        return cents;
    }

    /// <summary>An optional ISO-8601 date property (<c>yyyy-MM-dd</c>); null when absent.</summary>
    public static DateOnly? OptionalDate(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String
            || !DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", out var date))
        {
            throw new InvalidOperationException(
                $"item_params.{name} must be an ISO-8601 date string (yyyy-MM-dd) when present.");
        }

        return date;
    }

    /// <summary>An optional string property read from a raw frozen JSON document (the
    /// precondition-input side); null when the document or the property is absent.</summary>
    public static string? OptionalStringFrom(string? json, string name)
    {
        if (json is null)
        {
            return null;
        }

        var root = JsonDocument.Parse(json).RootElement;
        return root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }
}

/// <summary>
/// The bulk adapter for <see cref="PackVersionMigrated"/> (ADR-PC-009 / ADR-PC-035): re-pin a
/// frozen set of instances from one regulatory pack version to a newer one, as one audited job.
/// The precondition NARROWS by <c>from_pack_version</c> — only an instance still on the from
/// version applies; one that already moved (e.g. migrated by an earlier job between this job's
/// registration and its drain) is recorded SKIPPED, never re-migrated. Pure over the frozen row:
/// the instance's registration-time pin rides <c>precondition_input.current_pack_version</c>
/// (frozen by the registering surface), because an adapter may not read the store (ADR-PC-035).
/// </summary>
public sealed class PackVersionMigratedBulkStrategy : IBulkOperationStrategy
{
    public string OperationKind => "PackVersionMigrated";

    public BulkPreconditionVerdict EvaluatePrecondition(BulkOperationTargetRow target)
    {
        var fromVersion = BulkItemParams.RequireString(
            BulkItemParams.Require(target, OperationKind), "from_pack_version");
        var current = BulkItemParams.OptionalStringFrom(target.PreconditionInputJson, "current_pack_version");

        // No frozen current-pin means the registering surface already narrowed the set — apply.
        return current is not null && !string.Equals(current, fromVersion, StringComparison.Ordinal)
            ? new BulkPreconditionVerdict.Skip(
                $"instance is pinned to '{current}', not from_pack_version '{fromVersion}'")
            : new BulkPreconditionVerdict.Apply();
    }

    public DomainEvent CreateEvent(BulkOperationTargetRow target)
    {
        var parameters = BulkItemParams.Require(target, OperationKind);
        var fromVersion = BulkItemParams.RequireString(parameters, "from_pack_version");
        var toVersion = BulkItemParams.RequireString(parameters, "to_pack_version");
        if (string.Equals(fromVersion, toVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "item_params.from_pack_version and to_pack_version must differ — a migration moves the pin (ADR-PC-009).");
        }

        return new PackVersionMigrated(
            target.InstanceId,
            fromVersion,
            toVersion,
            BulkItemParams.RequireString(parameters, "migration_id"),
            BulkItemParams.RequireString(parameters, "operator_actor"));
    }
}

/// <summary>
/// The bulk adapter for <see cref="SchemaVersionMigrated"/> — the SCHEMA twin of
/// <see cref="PackVersionMigratedBulkStrategy"/> (ADR-PC-009 authoring §6 / ADR-PC-035):
/// identical shape, the family-schema pin in place of the pack pin. The precondition narrows by
/// <c>from_schema_version</c> against the frozen <c>precondition_input.current_schema_version</c>.
/// </summary>
public sealed class SchemaVersionMigratedBulkStrategy : IBulkOperationStrategy
{
    public string OperationKind => "SchemaVersionMigrated";

    public BulkPreconditionVerdict EvaluatePrecondition(BulkOperationTargetRow target)
    {
        var fromVersion = BulkItemParams.RequireString(
            BulkItemParams.Require(target, OperationKind), "from_schema_version");
        var current = BulkItemParams.OptionalStringFrom(target.PreconditionInputJson, "current_schema_version");

        return current is not null && !string.Equals(current, fromVersion, StringComparison.Ordinal)
            ? new BulkPreconditionVerdict.Skip(
                $"instance is pinned to '{current}', not from_schema_version '{fromVersion}'")
            : new BulkPreconditionVerdict.Apply();
    }

    public DomainEvent CreateEvent(BulkOperationTargetRow target)
    {
        var parameters = BulkItemParams.Require(target, OperationKind);
        var fromVersion = BulkItemParams.RequireString(parameters, "from_schema_version");
        var toVersion = BulkItemParams.RequireString(parameters, "to_schema_version");
        if (string.Equals(fromVersion, toVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "item_params.from_schema_version and to_schema_version must differ — a migration moves the pin (ADR-PC-009).");
        }

        return new SchemaVersionMigrated(
            target.InstanceId,
            fromVersion,
            toVersion,
            BulkItemParams.RequireString(parameters, "migration_id"),
            BulkItemParams.RequireString(parameters, "operator_actor"));
    }
}

/// <summary>
/// The bulk adapter for <see cref="FundsHeld"/> (event-store §4.1 / ADR-PC-035): record a legal
/// hold — a court order, garnishment, or external hold instruction — on every instance of a
/// frozen set, e.g. all accounts tied to one order. No precondition (the v1 fact is a store-only
/// audit record; there is no state to narrow by): every frozen target applies. The per-item
/// <c>held_amount_cents</c> is an INTEGER count of cents parsed into <see cref="Money"/> —
/// never a float, never rounded here (ADR-PC-010 §P1/§P2).
/// </summary>
public sealed class FundsHeldBulkStrategy : IBulkOperationStrategy
{
    public string OperationKind => "FundsHeld";

    public BulkPreconditionVerdict EvaluatePrecondition(BulkOperationTargetRow target)
        => new BulkPreconditionVerdict.Apply();

    public DomainEvent CreateEvent(BulkOperationTargetRow target)
    {
        var parameters = BulkItemParams.Require(target, OperationKind);
        var cents = BulkItemParams.RequireIntegerCents(parameters, "held_amount_cents");
        if (cents <= 0)
        {
            throw new InvalidOperationException(
                "item_params.held_amount_cents must be a positive integer number of cents — a zero or negative hold is malformed.");
        }

        return new FundsHeld(
            target.InstanceId,
            BulkItemParams.RequireString(parameters, "hold_id"),
            new Money(cents),
            BulkItemParams.RequireString(parameters, "legal_reference"),
            BulkItemParams.OptionalDate(parameters, "hold_expires_at"));
    }
}

/// <summary>
/// The bulk adapter for <see cref="AccountFrozen"/> (event-store §4.1 / ADR-PC-035): record a
/// compliance freeze — fraud, AML, or sanctions screening — on every instance of a frozen set.
/// No precondition (the v1 fact is a store-only audit record): every frozen target applies. The
/// <c>freeze_reason</c> is enforced to be a stable MACHINE CODE (e.g. <c>AML_SCREENING</c>,
/// <c>SANCTIONS_MATCH</c>) — free text is refused so no PII can ride the reason field into the
/// immutable stream (ADR-PC-004 §P2).
/// </summary>
public sealed class AccountFrozenBulkStrategy : IBulkOperationStrategy
{
    public string OperationKind => "AccountFrozen";

    public BulkPreconditionVerdict EvaluatePrecondition(BulkOperationTargetRow target)
        => new BulkPreconditionVerdict.Apply();

    public DomainEvent CreateEvent(BulkOperationTargetRow target)
    {
        var parameters = BulkItemParams.Require(target, OperationKind);
        var freezeReason = BulkItemParams.RequireString(parameters, "freeze_reason");
        if (!IsMachineCode(freezeReason))
        {
            throw new InvalidOperationException(
                "item_params.freeze_reason must be a stable machine code (UPPER_SNAKE_CASE, e.g. AML_SCREENING) "
                + "— free text is refused so no PII can enter the immutable stream (ADR-PC-004).");
        }

        return new AccountFrozen(
            target.InstanceId,
            BulkItemParams.RequireString(parameters, "freeze_id"),
            freezeReason,
            BulkItemParams.RequireString(parameters, "compliance_actor"),
            BulkItemParams.OptionalDate(parameters, "freeze_expires_at"));
    }

    // UPPER_SNAKE_CASE machine code: A-Z start, then A-Z / 0-9 / '_'. A hand-rolled scan (no
    // regex) keeps the adapter trivially pure and allocation-free on the hot path.
    private static bool IsMachineCode(string value)
    {
        if (value.Length == 0 || value[0] is < 'A' or > 'Z')
        {
            return false;
        }

        foreach (var c in value)
        {
            if (c is (< 'A' or > 'Z') and (< '0' or > '9') and not '_')
            {
                return false;
            }
        }

        return true;
    }
}
