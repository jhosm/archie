using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Babelstone.Pii;

/// <summary>
/// <see cref="ISecretProvider"/> over OpenBao's <c>KV v2</c> secrets engine for
/// <b>application / integration</b> credentials (the DB connection string today, Redpanda
/// SASL later). Authenticates with AppRole and reads versioned KV v2 secrets. This is the
/// second, additive OpenBao usage recorded in ADR-PC-004 <i>Amendment A1</i> (2026-05-31).
/// </summary>
/// <remarks>
/// Unlike <see cref="OpenBaoTransitClient"/> — which keeps key material at the boundary so
/// the engine never holds a key (§P2) — a KV secret <b>is</b> handed to the engine: the
/// resolved credential lives in process memory at the composition root, never on a saga
/// message (ADR-IC-003 §P7) nor the durable bus (§P2). Rotation is a KV v2 version bump
/// observed via <see cref="RefreshAsync"/>, the inverse of transit crypto-shredding. No SDK
/// is used (ADR-PC-010 hand-rolled-core exception); a single <see cref="SendAsync"/>
/// chokepoint attaches the AppRole client token, and no response or error path ever
/// surfaces secret material.
/// </remarks>
/// <param name="httpClient">Client whose <c>BaseAddress</c> is the OpenBao address.</param>
/// <param name="roleId">The AppRole <c>role_id</c>.</param>
/// <param name="secretId">The AppRole <c>secret_id</c>.</param>
/// <param name="mountPath">The KV v2 engine mount (default <c>secret</c>).</param>
public sealed class OpenBaoKvSecretProvider(
    HttpClient httpClient,
    string roleId,
    string secretId,
    string mountPath = "secret") : ISecretProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private string? _clientToken;

    public Task<string> GetSecretAsync(string name, CancellationToken ct = default)
        => ResolveAsync(name, forceLogin: false, ct);

    public Task<string> RefreshAsync(string name, CancellationToken ct = default)
        => ResolveAsync(name, forceLogin: true, ct);

    private async Task<string> ResolveAsync(string name, bool forceLogin, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        // Refresh re-authenticates and invalidates the cached token so a rotated secret
        // (a KV v2 version bump) is observed on the next read.
        if (forceLogin)
        {
            _clientToken = null;
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"v1/{Uri.EscapeDataString(mountPath)}/data/{Uri.EscapeDataString(name)}");
        using var response = await SendAsync(request, ct);

        // A missing KV path is a benign not-found — distinct from a real fault (auth
        // failure, sealed store), which EnsureSuccessAsync surfaces below. Either way the
        // secret value is never echoed.
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new SecretProviderException(
                $"OpenBao KV secret '{name}' not found at '{mountPath}/data/{name}'.");
        }

        await EnsureSuccessAsync(response, $"read secret '{name}'", ct);

        var payload = await response.Content.ReadFromJsonAsync<KvReadResponse>(Json, ct);
        if (payload?.Data?.Data is not { Count: > 0 } data
            || !data.TryGetValue(name, out var value)
            || string.IsNullOrWhiteSpace(value))
        {
            // The path resolved but carries no entry under this key — a real fault, never
            // a value echo.
            throw new SecretProviderException(
                $"OpenBao KV secret '{name}' contained no value at '{mountPath}/data/{name}'.");
        }

        return value;
    }

    /// <summary>
    /// Single send chokepoint: lazily logs in via AppRole and attaches the resulting client
    /// token as <c>X-Vault-Token</c> on every request.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var token = _clientToken ?? await LoginAsync(ct);
        request.Headers.Remove("X-Vault-Token");
        request.Headers.Add("X-Vault-Token", token);
        return await httpClient.SendAsync(request, ct);
    }

    private async Task<string> LoginAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/auth/approle/login")
        {
            Content = JsonContent.Create(new { role_id = roleId, secret_id = secretId }, options: Json),
        };
        using var response = await httpClient.SendAsync(request, ct);

        await EnsureSuccessAsync(response, "AppRole login", ct);

        var payload = await response.Content.ReadFromJsonAsync<AuthResponse>(Json, ct);
        var clientToken = payload?.Auth?.ClientToken;
        if (string.IsNullOrEmpty(clientToken))
        {
            throw new SecretProviderException("OpenBao AppRole login returned no client token.");
        }

        _clientToken = clientToken;
        return clientToken;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        // OpenBao error bodies are JSON {"errors":[...]}; surface the operation + detail
        // without ever leaking secret material.
        string detail;
        try
        {
            var error = await response.Content.ReadFromJsonAsync<OpenBaoErrorResponse>(Json, ct);
            detail = error?.Errors is { Count: > 0 } errors
                ? string.Join("; ", errors)
                : $"HTTP {(int)response.StatusCode}";
        }
        catch (JsonException)
        {
            detail = $"HTTP {(int)response.StatusCode}";
        }

        throw new SecretProviderException($"OpenBao {operation} failed: {detail}");
    }

    private sealed record AuthResponse([property: JsonPropertyName("auth")] AuthData? Auth);

    private sealed record AuthData([property: JsonPropertyName("client_token")] string? ClientToken);

    private sealed record KvReadResponse([property: JsonPropertyName("data")] KvReadData? Data);

    private sealed record KvReadData(
        [property: JsonPropertyName("data")] IReadOnlyDictionary<string, string>? Data);

    private sealed record OpenBaoErrorResponse(
        [property: JsonPropertyName("errors")] IReadOnlyList<string>? Errors);
}
