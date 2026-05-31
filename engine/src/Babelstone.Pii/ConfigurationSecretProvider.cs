using Microsoft.Extensions.Configuration;

namespace Babelstone.Pii;

/// <summary>
/// An <see cref="IConfiguration"/>-backed <see cref="ISecretProvider"/> for dev / test / CI —
/// the secret-boundary analogue of the null PII protector: it resolves application
/// credentials from configuration (appsettings / environment) instead of an external secret
/// store, so <c>make up</c> and the Docker-free test lane keep working without OpenBao.
/// </summary>
/// <remarks>
/// <c>GetSecretAsync("Engine")</c> reads <c>ConnectionStrings:Engine</c>, preserving the
/// existing throw-if-missing semantics (an empty/absent value raises
/// <see cref="SecretProviderException"/>). <see cref="RefreshAsync"/> re-reads configuration —
/// for reloadable providers this picks up a changed value.
/// </remarks>
public sealed class ConfigurationSecretProvider(IConfiguration configuration) : ISecretProvider
{
    public Task<string> GetSecretAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var value = configuration[$"ConnectionStrings:{name}"];
        if (string.IsNullOrWhiteSpace(value))
        {
            // Name + path only — never echo a (possibly partially-set) value.
            throw new SecretProviderException(
                $"No configured secret for '{name}' (ConnectionStrings:{name}).");
        }

        return Task.FromResult(value);
    }

    public Task<string> RefreshAsync(string name, CancellationToken ct = default)
        => GetSecretAsync(name, ct);
}
