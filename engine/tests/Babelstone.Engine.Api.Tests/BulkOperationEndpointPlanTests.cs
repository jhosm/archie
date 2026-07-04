using System.Text.Json;
using Babelstone.Engine.Hosting;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Babelstone.Engine.Api.Tests;

/// <summary>
/// The bulk-operations endpoint's pure validation + normalization plan
/// (<c>BulkOperationsEndpoints.Plan</c>, bd babelstone-qpiw.4). In plain English: before any
/// database work, the endpoint decides whether the register request names a coherent frozen set —
/// a bare id list XOR a rich per-item target list, no duplicates, and (when the caller carries
/// one) a matching <c>set_digest</c> fingerprint. These pin that decision in isolation — no HTTP
/// stack, no database — the same discipline as <c>PackMigrationEndpointPlanTests</c>.
/// </summary>
public sealed class BulkOperationEndpointPlanTests
{
    private static readonly Guid[] SomeIds = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];

    private static BulkOperationRequest Request(
        Guid? jobId = null,
        string operationKind = "TestOp",
        string actor = "operator:ops",
        int? requestedBatchSize = null,
        IReadOnlyList<Guid>? instanceIds = null,
        IReadOnlyList<BulkTargetRequest>? targets = null,
        string? setDigest = null,
        bool preview = false)
        => new(jobId ?? Guid.NewGuid(), operationKind, actor,
            requestedBatchSize, instanceIds, targets, setDigest, preview);

    [Fact]
    public void Missing_job_id_operation_kind_or_actor_is_400()
    {
        Assert.Equal(StatusCodes.Status400BadRequest,
            BulkOperationsEndpoints.Plan(Request(jobId: Guid.Empty, instanceIds: SomeIds)).ErrorStatus);
        Assert.Equal(StatusCodes.Status400BadRequest,
            BulkOperationsEndpoints.Plan(Request(operationKind: " ", instanceIds: SomeIds)).ErrorStatus);
        Assert.Equal(StatusCodes.Status400BadRequest,
            BulkOperationsEndpoints.Plan(Request(actor: "", instanceIds: SomeIds)).ErrorStatus);
    }

    [Fact]
    public void A_non_positive_batch_size_is_400()
    {
        var plan = BulkOperationsEndpoints.Plan(Request(requestedBatchSize: 0, instanceIds: SomeIds));

        Assert.False(plan.Ok);
        Assert.Equal(StatusCodes.Status400BadRequest, plan.ErrorStatus);
    }

    [Fact]
    public void Both_instance_ids_and_targets_is_422_xor()
    {
        var plan = BulkOperationsEndpoints.Plan(Request(
            instanceIds: SomeIds,
            targets: [new BulkTargetRequest(Guid.NewGuid())]));

        Assert.False(plan.Ok);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, plan.ErrorStatus);
    }

    [Fact]
    public void Neither_instance_ids_nor_targets_is_422_xor()
    {
        var plan = BulkOperationsEndpoints.Plan(Request());

        Assert.False(plan.Ok);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, plan.ErrorStatus);

        // Empty lists are the same absence, not a zero-instance plan.
        Assert.Equal(StatusCodes.Status422UnprocessableEntity,
            BulkOperationsEndpoints.Plan(Request(instanceIds: [])).ErrorStatus);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity,
            BulkOperationsEndpoints.Plan(Request(targets: [])).ErrorStatus);
    }

    [Fact]
    public void A_duplicate_instance_id_in_the_set_is_422()
    {
        var duplicated = Guid.NewGuid();

        var plan = BulkOperationsEndpoints.Plan(Request(instanceIds: [duplicated, Guid.NewGuid(), duplicated]));

        Assert.False(plan.Ok);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, plan.ErrorStatus);
    }

    [Fact]
    public void An_empty_uuid_target_is_422()
    {
        var plan = BulkOperationsEndpoints.Plan(Request(instanceIds: [Guid.NewGuid(), Guid.Empty]));

        Assert.False(plan.Ok);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, plan.ErrorStatus);
    }

    [Fact]
    public void A_mismatched_set_digest_is_422()
    {
        var plan = BulkOperationsEndpoints.Plan(Request(
            instanceIds: SomeIds,
            setDigest: BulkOperationSetDigest.Compute([Guid.NewGuid()])));

        Assert.False(plan.Ok);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, plan.ErrorStatus);
        Assert.Contains("set_digest", plan.ErrorMessage);
    }

    [Fact]
    public void A_matching_set_digest_proceeds()
    {
        var plan = BulkOperationsEndpoints.Plan(Request(
            instanceIds: SomeIds,
            setDigest: BulkOperationSetDigest.Compute(SomeIds)));

        Assert.True(plan.Ok);
        Assert.Equal(BulkOperationSetDigest.Compute(SomeIds), plan.SetDigest);
    }

    [Fact]
    public void The_bare_ids_arm_normalizes_to_targets_with_no_item_json()
    {
        var plan = BulkOperationsEndpoints.Plan(Request(instanceIds: SomeIds));

        Assert.True(plan.Ok);
        Assert.Equal(SomeIds, plan.Targets!.Select(target => target.InstanceId));
        Assert.All(plan.Targets!, target =>
        {
            Assert.Null(target.ItemParamsJson);
            Assert.Null(target.PreconditionInputJson);
        });
        Assert.Equal(BulkOperationsEndpoints.DefaultBatchSize, plan.BatchSize);
    }

    [Fact]
    public void The_rich_targets_arm_freezes_per_item_json_verbatim()
    {
        var instanceId = Guid.NewGuid();
        using var itemParams = JsonDocument.Parse("""{"held_amount_cents":1050,"hold_id":"hold-1"}""");
        using var preconditionInput = JsonDocument.Parse("""{"current_pack_version":"pt.2026.1"}""");

        var plan = BulkOperationsEndpoints.Plan(Request(
            requestedBatchSize: 25,
            targets: [new BulkTargetRequest(instanceId, itemParams.RootElement, preconditionInput.RootElement)]));

        Assert.True(plan.Ok);
        var target = Assert.Single(plan.Targets!);
        Assert.Equal(instanceId, target.InstanceId);
        Assert.Equal("""{"held_amount_cents":1050,"hold_id":"hold-1"}""", target.ItemParamsJson);
        Assert.Equal("""{"current_pack_version":"pt.2026.1"}""", target.PreconditionInputJson);
        Assert.Equal(25, plan.BatchSize);
    }

    [Fact]
    public void The_matched_set_snapshot_carries_digest_count_and_sample_but_never_the_full_set()
    {
        var sample = SomeIds.Take(2).ToList();
        var digest = BulkOperationSetDigest.Compute(SomeIds);

        var json = BulkOperationsEndpoints.BuildMatchedSetJson(digest, totalCount: 500_000, sample);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("explicit_set", root.GetProperty("kind").GetString());
        Assert.Equal(digest, root.GetProperty("set_digest").GetString());
        Assert.Equal(500_000, root.GetProperty("total_count").GetInt64());
        Assert.Equal(2, root.GetProperty("sample_instance_ids").GetArrayLength());
        // The digest round-trips back out — the register-level idempotency comparison path.
        Assert.Equal(digest, BulkOperationsEndpoints.ReadSetDigest(json));
    }

    [Fact]
    public void A_snapshot_without_a_digest_reads_as_null_not_an_error()
    {
        // Jobs registered straight through BulkOperationService (tests, other hosts) carry an
        // arbitrary matched_set — the endpoint must treat them as "no digest to compare".
        Assert.Null(BulkOperationsEndpoints.ReadSetDigest("""{"kind":"explicit_ids"}"""));
        Assert.Null(BulkOperationsEndpoints.ReadSetDigest("not json at all"));
        Assert.Null(BulkOperationsEndpoints.ReadSetDigest("""["an","array"]"""));
    }
}

/// <summary>
/// Pins the <see cref="BulkOperationSetDigest"/> canonical form the register-level idempotency
/// check rests on (bd babelstone-qpiw.4): order-insensitive over the SET, deterministic, and
/// stable across releases (the hardcoded vector below would catch any canonicalization drift).
/// </summary>
public sealed class BulkOperationSetDigestTests
{
    [Fact]
    public void The_digest_is_order_insensitive_over_the_set()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();

        Assert.Equal(
            BulkOperationSetDigest.Compute([a, b, c]),
            BulkOperationSetDigest.Compute([c, a, b]));
    }

    [Fact]
    public void Distinct_sets_derive_distinct_digests()
    {
        var shared = Guid.NewGuid();

        Assert.NotEqual(
            BulkOperationSetDigest.Compute([shared, Guid.NewGuid()]),
            BulkOperationSetDigest.Compute([shared, Guid.NewGuid()]));
    }

    [Fact]
    public void The_canonical_form_is_pinned_by_a_known_vector()
    {
        // sha256 over "11111111-1111-1111-1111-111111111111\n22222222-2222-2222-2222-222222222222"
        // (lowercase D-format ids, ordinal-sorted, joined by \n, UTF-8). A change to any part of
        // the canonicalization breaks this vector — deliberately, because operators pin digests.
        var digest = BulkOperationSetDigest.Compute([
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
        ]);

        Assert.Equal("sha256:408ad5bf936cfc2ba06ed5b95fdf1c0a8b634ce563c000a46a4f3869f16ff983", digest);
    }
}
