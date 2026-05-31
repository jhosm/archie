using Babelstone.Pii;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Babelstone.Pii.Tests;

/// <summary>
/// The application-credential secret boundary (M.2 / ADR-PC-004 Amendment A1) over the
/// dev/test/CI <see cref="ConfigurationSecretProvider"/>. Pure — no live OpenBao — so these
/// run in the default (Docker-free) lane. The OpenBao KV round-trip is the separate
/// [Integration]-tagged suite.
/// </summary>
public sealed class SecretProviderTests
{
    private const string Secret = "Host=db;Username=u;Password=super-secret-value";

    private static IConfiguration Config(params (string Key, string? Value)[] entries)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();

    [Fact]
    public async Task GetSecretAsync_resolves_a_configured_connection_string()
    {
        ISecretProvider sut = new ConfigurationSecretProvider(
            Config(("ConnectionStrings:Engine", Secret)));

        Assert.Equal(Secret, await sut.GetSecretAsync("Engine"));
    }

    [Fact]
    public async Task GetSecretAsync_throws_SecretProviderException_when_the_key_is_missing()
    {
        ISecretProvider sut = new ConfigurationSecretProvider(Config());

        var ex = await Assert.ThrowsAsync<SecretProviderException>(() => sut.GetSecretAsync("Engine"));
        Assert.Contains("Engine", ex.Message); // names the secret, nothing more
    }

    [Fact]
    public async Task RefreshAsync_returns_the_updated_value_after_rotation()
    {
        const string rotated = "Host=db;Username=u;Password=rotated-secret";
        // ConfigurationManager is a mutable IConfiguration — stands in for a rotated store.
        var config = new ConfigurationManager();
        config.AddInMemoryCollection([new KeyValuePair<string, string?>("ConnectionStrings:Engine", Secret)]);
        ISecretProvider sut = new ConfigurationSecretProvider(config);

        Assert.Equal(Secret, await sut.GetSecretAsync("Engine"));

        config["ConnectionStrings:Engine"] = rotated; // the "version bump"
        Assert.Equal(rotated, await sut.RefreshAsync("Engine"));
    }

    [Fact]
    public async Task Exception_text_never_contains_the_secret_value()
    {
        // A whitespace-only value is treated as missing; the message must still be purely
        // the logical name + path, never a fragment of any real secret.
        ISecretProvider sut = new ConfigurationSecretProvider(
            Config(("ConnectionStrings:Engine", "   ")));

        var ex = await Assert.ThrowsAsync<SecretProviderException>(() => sut.GetSecretAsync("Engine"));
        Assert.DoesNotContain("Password", ex.Message);
        Assert.DoesNotContain(Secret, ex.Message);
    }
}
