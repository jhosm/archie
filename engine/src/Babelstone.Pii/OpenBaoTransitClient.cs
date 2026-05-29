using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Babelstone.Pii;

/// <summary>
/// <see cref="IPiiKeyStore"/> over OpenBao's <c>transit</c> secrets engine (ADR-PC-004:
/// OpenBao is the deliberate exception to the hand-rolled-core posture). Per-subject
/// named transit keys; encrypt/decrypt never expose key material to the engine; key
/// destruction is crypto-shred erasure.
/// </summary>
/// <param name="httpClient">Client whose <c>BaseAddress</c> is the OpenBao address; the X-Vault-Token is added per request.</param>
/// <param name="token">The OpenBao token authorising transit operations.</param>
/// <param name="mountPath">The transit engine mount (default <c>transit</c>).</param>
public sealed class OpenBaoTransitClient(HttpClient httpClient, string token, string mountPath = "transit")
    : IPiiKeyStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<byte[]> EncryptAsync(string subjectId, byte[] plaintext, CancellationToken ct = default)
    {
        await EnsureKeyAsync(subjectId, ct);

        using var response = await SendAsync(
            HttpMethod.Post,
            $"v1/{mountPath}/encrypt/{KeyName(subjectId)}",
            new { plaintext = Convert.ToBase64String(plaintext) },
            ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<TransitResponse>(Json, ct);
        var ciphertext = body?.Data?.Ciphertext
            ?? throw new InvalidOperationException($"OpenBao encrypt returned no ciphertext for subject '{subjectId}'.");
        // The transit ciphertext is the self-describing "vault:vN:..." string; its bytes
        // are what we persist inside the event payload.
        return System.Text.Encoding.UTF8.GetBytes(ciphertext);
    }

    public async Task<byte[]?> DecryptAsync(string subjectId, byte[] ciphertext, CancellationToken ct = default)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            $"v1/{mountPath}/decrypt/{KeyName(subjectId)}",
            new { ciphertext = System.Text.Encoding.UTF8.GetString(ciphertext) },
            ct);

        // A destroyed (erased) subject key makes transit reject decryption of
        // previously-valid ciphertext with a 4xx. That is the GDPR post-erasure
        // state (§P3), surfaced as null rather than an exception.
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<TransitResponse>(Json, ct);
        var plaintext = body?.Data?.Plaintext
            ?? throw new InvalidOperationException($"OpenBao decrypt returned no plaintext for subject '{subjectId}'.");
        return Convert.FromBase64String(plaintext);
    }

    public async Task DestroyKeyAsync(string subjectId, CancellationToken ct = default)
    {
        // Deletion must be explicitly enabled per key before it can be removed.
        using (var config = await SendAsync(
            HttpMethod.Post,
            $"v1/{mountPath}/keys/{KeyName(subjectId)}/config",
            new { deletion_allowed = true },
            ct))
        {
            // A key that is already gone cannot be configured — transit answers 400
            // (or 404). Either way there is nothing left to destroy, so destruction
            // is idempotent.
            if (config.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
            {
                return;
            }

            config.EnsureSuccessStatusCode();
        }

        using var delete = await SendAsync(HttpMethod.Delete, $"v1/{mountPath}/keys/{KeyName(subjectId)}", null, ct);
        if (delete.StatusCode != HttpStatusCode.NotFound)
        {
            delete.EnsureSuccessStatusCode();
        }
    }

    private async Task EnsureKeyAsync(string subjectId, CancellationToken ct)
    {
        // Creating an existing key is a no-op, so this is safe to call before every encrypt.
        using var response = await SendAsync(HttpMethod.Post, $"v1/{mountPath}/keys/{KeyName(subjectId)}", new { }, ct);
        response.EnsureSuccessStatusCode();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Vault-Token", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        return await httpClient.SendAsync(request, ct);
    }

    // Transit key names are limited to [a-zA-Z0-9_-]; a subject id maps to one stable key.
    private static string KeyName(string subjectId) => $"pii-{subjectId}";

    private sealed record TransitResponse([property: JsonPropertyName("data")] TransitData? Data);

    private sealed record TransitData(
        [property: JsonPropertyName("ciphertext")] string? Ciphertext,
        [property: JsonPropertyName("plaintext")] string? Plaintext);
}
