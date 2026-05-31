using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Babelstone.Pii;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace Babelstone.Pii.Tests;

/// <summary>
/// A dev-mode OpenBao (the dev-stack image) with the <c>kv</c> v2 engine and AppRole auth
/// enabled — the application-credential boundary (ADR-PC-004 Amendment A1), distinct from the
/// transit fixture. Shared across the integration class.
/// </summary>
public sealed class OpenBaoKvFixture : IAsyncLifetime
{
    private const string Token = "root";
    private const string MountPath = "secret";
    private const string ReadPolicyName = "engine-kv-read";

    private readonly IContainer _container = new ContainerBuilder("openbao/openbao:2.5.4")
        .WithEnvironment("BAO_DEV_ROOT_TOKEN_ID", Token)
        .WithCommand("server", "-dev", "-dev-listen-address=0.0.0.0:8200")
        .WithPortBinding(8200, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilHttpRequestIsSucceeded(r => r.ForPort(8200).ForPath("/v1/sys/health")))
        .Build();

    public string Mount => MountPath;

    /// <summary>
    /// The least-privilege ACL policy bound to each test AppRole: <c>read</c> on the KV v2
    /// data path only. A token's implicit <c>default</c> policy grants no KV access, so an
    /// AppRole carrying only it 403s ("permission denied") on every read.
    /// </summary>
    public string ReadPolicy => ReadPolicyName;

    public HttpClient NewHttpClient() => new()
    {
        BaseAddress = new Uri($"http://{_container.Hostname}:{_container.GetMappedPublicPort(8200)}/"),
    };

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        using var admin = NewAdminClient();

        // Dev mode mounts kv v2 at "secret/" by default, but approle auth is NOT — enable it
        // once for the suite. (kv v2 at secret/ is present in -dev.)
        using var enableApprole = await admin.PostAsJsonAsync("v1/sys/auth/approle", new { type = "approle" });
        enableApprole.EnsureSuccessStatusCode();

        // Author the least-privilege read policy each AppRole binds. Without a KV-granting
        // policy the AppRole token holds only the implicit `default` policy, which 403s on
        // every read — and a 403 is returned even for absent paths (the ACL check precedes the
        // existence check), masking the benign not-found. `read` on the `data/*` wildcard lets
        // real secrets resolve and absent paths surface as a genuine 404.
        var policy = $"path \"{MountPath}/data/*\" {{\n  capabilities = [\"read\"]\n}}\n";
        using var writePolicy = await admin.PutAsJsonAsync(
            $"v1/sys/policies/acl/{ReadPolicyName}", new { policy });
        writePolicy.EnsureSuccessStatusCode();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    public HttpClient NewAdminClient()
    {
        var client = NewHttpClient();
        client.DefaultRequestHeaders.Add("X-Vault-Token", Token);
        return client;
    }
}

/// <summary>
/// The OpenBao KV-v2 / AppRole secret boundary against a real OpenBao: AppRole login + KV v2
/// read round-trip, version-bump rotation observed by <see cref="ISecretProvider.RefreshAsync"/>,
/// and the benign-not-found vs real-fault distinction. Tagged Integration — deferred lane.
/// </summary>
[Trait("Category", "Integration")]
public sealed class OpenBaoKvSecretProviderIntegrationTests(OpenBaoKvFixture fixture)
    : IClassFixture<OpenBaoKvFixture>
{
    [Fact]
    public async Task Login_read_then_rotate_round_trips_the_new_version()
    {
        var role = $"engine-{Guid.NewGuid():N}";
        var name = $"conn-{Guid.NewGuid():N}";
        const string v1 = "Host=db;Username=u;Password=v1-secret";
        const string v2 = "Host=db;Username=u;Password=v2-rotated";

        using var admin = fixture.NewAdminClient();
        await WriteKvAsync(admin, name, v1);
        var (roleId, secretId) = await CreateAppRoleAsync(admin, role, fixture.ReadPolicy);

        var sut = new OpenBaoKvSecretProvider(fixture.NewHttpClient(), roleId, secretId, fixture.Mount);

        // AppRole login + KV v2 read.
        Assert.Equal(v1, await sut.GetSecretAsync(name));

        // Rotation: KV v2 version bump, then RefreshAsync re-resolves the latest version.
        await WriteKvAsync(admin, name, v2);
        Assert.Equal(v2, await sut.RefreshAsync(name));
    }

    [Fact]
    public async Task A_missing_path_is_a_benign_not_found_distinct_from_a_real_fault()
    {
        var role = $"engine-{Guid.NewGuid():N}";
        using var admin = fixture.NewAdminClient();
        var (roleId, secretId) = await CreateAppRoleAsync(admin, role, fixture.ReadPolicy);

        // Benign not-found: the KV path simply does not exist — surfaced as a clear message.
        var sut = new OpenBaoKvSecretProvider(fixture.NewHttpClient(), roleId, secretId, fixture.Mount);
        var notFound = await Assert.ThrowsAsync<SecretProviderException>(
            () => sut.GetSecretAsync($"absent-{Guid.NewGuid():N}"));
        Assert.Contains("not found", notFound.Message);

        // Real fault: a bad secret_id fails AppRole login — a DIFFERENT failure, never masked
        // as not-found, and never echoing the credential.
        var badSut = new OpenBaoKvSecretProvider(fixture.NewHttpClient(), roleId, "bogus-secret-id", fixture.Mount);
        var authFault = await Assert.ThrowsAsync<SecretProviderException>(() => badSut.GetSecretAsync("anything"));
        Assert.Contains("login", authFault.Message);
        Assert.DoesNotContain("bogus-secret-id", authFault.Message);
    }

    private async Task WriteKvAsync(HttpClient admin, string name, string value)
    {
        // KV v2 write: {"data": {"<name>": "<value>"}} → a new version each call.
        using var response = await admin.PostAsJsonAsync(
            $"v1/{fixture.Mount}/data/{name}",
            new { data = new Dictionary<string, string> { [name] = value } });
        response.EnsureSuccessStatusCode();
    }

    private static async Task<(string RoleId, string SecretId)> CreateAppRoleAsync(
        HttpClient admin, string role, string policy)
    {
        using var create = await admin.PostAsJsonAsync(
            $"v1/auth/approle/role/{role}",
            new { token_policies = policy, token_ttl = "10m" });
        create.EnsureSuccessStatusCode();

        using var roleIdResponse = await admin.GetAsync($"v1/auth/approle/role/{role}/role-id");
        roleIdResponse.EnsureSuccessStatusCode();
        var roleId = (await roleIdResponse.Content.ReadFromJsonAsync<RoleIdResponse>())?.Data?.RoleId
            ?? throw new InvalidOperationException("No role_id returned.");

        using var secretIdResponse = await admin.PostAsJsonAsync(
            $"v1/auth/approle/role/{role}/secret-id", new { });
        secretIdResponse.EnsureSuccessStatusCode();
        var secretId = (await secretIdResponse.Content.ReadFromJsonAsync<SecretIdResponse>())?.Data?.SecretId
            ?? throw new InvalidOperationException("No secret_id returned.");

        return (roleId, secretId);
    }

    private sealed record RoleIdResponse([property: JsonPropertyName("data")] RoleIdData? Data);

    private sealed record RoleIdData([property: JsonPropertyName("role_id")] string? RoleId);

    private sealed record SecretIdResponse([property: JsonPropertyName("data")] SecretIdData? Data);

    private sealed record SecretIdData([property: JsonPropertyName("secret_id")] string? SecretId);
}
