using Babelstone.Engine.Hosting;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Babelstone.Engine.Tests;

/// <summary>
/// The pack-migration endpoint's pure validation + dispatch plan (<c>PackMigrationsEndpoints.Plan</c>,
/// bd babelstone-7giq). In plain English: before any database or event-store work, the endpoint decides
/// which family's write-path handles the request and how the target instances are named — an explicit id
/// list XOR a <c>{ product_family, currently_active }</c> predicate (surface §3.6). These assert that
/// decision in isolation — no HTTP stack, no database — over stub services/resolvers, so the dispatch
/// rules and the v1 guards are pinned independently of the integration end-to-end.
/// </summary>
public sealed class PackMigrationEndpointPlanTests
{
    private const string From = "pt.2026.1";
    private const string To = "pt.2027.1";

    private sealed class StubService(string family) : IPackMigrationService
    {
        public string ProductFamily => family;

        public int MigrationCap => int.MaxValue;

        public Task<IReadOnlyList<Guid>> PreviewAsync(
            string fromPackVersion, IReadOnlyList<Guid> instanceIds, CancellationToken ct = default)
            => Task.FromResult(instanceIds);

        public Task<IReadOnlyList<Guid>> MigrateAsync(
            string fromPackVersion, string toPackVersion, IReadOnlyList<Guid> instanceIds,
            string migrationId, string operatorActor, DateTimeOffset migratedAt, CancellationToken ct = default)
            => Task.FromResult(instanceIds);
    }

    private sealed class StubResolver(string family) : IPackMigrationInstanceResolver
    {
        public string ProductFamily => family;

        public Task<IReadOnlyList<Guid>> ResolveAsync(InstanceFilter filter, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Guid>>([]);
    }

    private static PackMigrationRequest Request(
        string? productFamily = null,
        IReadOnlyList<Guid>? instanceIds = null,
        InstanceFilter? instanceFilter = null,
        string from = From,
        string to = To,
        string migrationId = "mig-1",
        string operatorActor = "operator:ops")
        => new(from, to, migrationId, operatorActor, productFamily, instanceIds, instanceFilter);

    private static readonly IPackMigrationService[] OneService = [new StubService("term_deposit")];
    private static readonly IPackMigrationInstanceResolver[] OneResolver = [new StubResolver("term_deposit")];
    private static readonly Guid[] SomeIds = [Guid.NewGuid(), Guid.NewGuid()];

    [Fact]
    public void Missing_required_fields_is_400()
    {
        var plan = PackMigrationsEndpoints.Plan(
            Request(instanceIds: SomeIds, migrationId: " "), OneService, OneResolver);

        Assert.False(plan.Ok);
        Assert.Equal(StatusCodes.Status400BadRequest, plan.ErrorStatus);
    }

    [Fact]
    public void From_equals_to_is_400()
    {
        var plan = PackMigrationsEndpoints.Plan(
            Request(instanceIds: SomeIds, to: From), OneService, OneResolver);

        Assert.False(plan.Ok);
        Assert.Equal(StatusCodes.Status400BadRequest, plan.ErrorStatus);
    }

    [Fact]
    public void Both_instance_ids_and_filter_is_422_xor()
    {
        var plan = PackMigrationsEndpoints.Plan(
            Request(instanceIds: SomeIds, instanceFilter: new InstanceFilter("term_deposit", true)),
            OneService, OneResolver);

        Assert.False(plan.Ok);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, plan.ErrorStatus);
    }

    [Fact]
    public void Neither_instance_ids_nor_filter_is_422_xor()
    {
        var plan = PackMigrationsEndpoints.Plan(Request(), OneService, OneResolver);

        Assert.False(plan.Ok);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, plan.ErrorStatus);
    }

    [Fact]
    public void Explicit_ids_with_a_single_host_family_omitting_product_family_proceeds()
    {
        var plan = PackMigrationsEndpoints.Plan(Request(instanceIds: SomeIds), OneService, OneResolver);

        Assert.True(plan.Ok);
        Assert.Equal("term_deposit", plan.Service!.ProductFamily);
        Assert.Equal(SomeIds, plan.ExplicitInstanceIds);
        Assert.Null(plan.Resolver); // explicit arm: no resolver
    }

    [Fact]
    public void Explicit_ids_with_an_unknown_product_family_is_422()
    {
        var plan = PackMigrationsEndpoints.Plan(
            Request(productFamily: "personal_loan", instanceIds: SomeIds), OneService, OneResolver);

        Assert.False(plan.Ok);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, plan.ErrorStatus);
    }

    [Fact]
    public void Explicit_ids_omitting_product_family_across_multiple_families_is_422()
    {
        IPackMigrationService[] twoFamilies = [new StubService("term_deposit"), new StubService("personal_loan")];

        var plan = PackMigrationsEndpoints.Plan(Request(instanceIds: SomeIds), twoFamilies, OneResolver);

        Assert.False(plan.Ok);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, plan.ErrorStatus);
    }

    [Fact]
    public void Predicate_currently_active_false_is_422_in_v1()
    {
        var plan = PackMigrationsEndpoints.Plan(
            Request(instanceFilter: new InstanceFilter("term_deposit", false)), OneService, OneResolver);

        Assert.False(plan.Ok);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, plan.ErrorStatus);
    }

    [Fact]
    public void Predicate_for_a_family_with_no_write_path_is_422()
    {
        var plan = PackMigrationsEndpoints.Plan(
            Request(instanceFilter: new InstanceFilter("personal_loan", true)), OneService, OneResolver);

        Assert.False(plan.Ok);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, plan.ErrorStatus);
    }

    [Fact]
    public void Predicate_for_a_family_with_a_write_path_but_no_resolver_is_422()
    {
        // term_deposit has a service but instance_filter is unsupported (no resolver registered).
        var plan = PackMigrationsEndpoints.Plan(
            Request(instanceFilter: new InstanceFilter("term_deposit", true)),
            OneService, []);

        Assert.False(plan.Ok);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, plan.ErrorStatus);
    }

    [Fact]
    public void Predicate_happy_path_selects_the_service_resolver_and_filter()
    {
        var filter = new InstanceFilter("term_deposit", true);

        var plan = PackMigrationsEndpoints.Plan(Request(instanceFilter: filter), OneService, OneResolver);

        Assert.True(plan.Ok);
        Assert.Equal("term_deposit", plan.Service!.ProductFamily);
        Assert.Equal("term_deposit", plan.Resolver!.ProductFamily);
        Assert.Same(filter, plan.Filter);
        Assert.Null(plan.ExplicitInstanceIds); // predicate arm: ids come from the resolver, not the request
    }
}
