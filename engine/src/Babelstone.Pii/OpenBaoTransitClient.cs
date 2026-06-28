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

        if (response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadFromJsonAsync<TransitResponse>(Json, ct);
            var plaintext = body?.Data?.Plaintext
                ?? throw new InvalidOperationException($"OpenBao decrypt returned no plaintext for subject '{subjectId}'.");
            return Convert.FromBase64String(plaintext);
        }

        // The ONLY benign decrypt failure is a destroyed/absent subject key — the GDPR
        // post-erasure state (ADR-PC-004), surfaced as null. A 4xx alone does NOT prove that:
        // transit also answers 4xx for corrupt ciphertext, a wrong-subject key, a sealed
        // or misconfigured mount, and a denied token. Treating any 4xx as erasure would
        // silently report intact PII as erased (data the bank must retain reads as gone),
        // or a transient error as permanent erasure. So we confirm the key is actually
        // gone before returning null; every other failure surfaces, never masquerades.
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound
            && !await KeyExistsAsync(subjectId, ct))
        {
            return null;
        }

        var error = await response.Content.ReadAsStringAsync(ct);
        throw new PiiKeyStoreException(
            $"OpenBao decrypt failed for subject '{subjectId}' (HTTP {(int)response.StatusCode}): {error}");
    }

    public async Task DestroyKeyAsync(string subjectId, CancellationToken ct = default)
    {
        // Idempotent by proof, not by inference: if the key is already gone there is
        // nothing to destroy. Confirming absence up front means a 4xx from the config
        // step below (a denied token, a sealed mount, a transient error) surfaces as a
        // real failure instead of being mistaken for "already destroyed" — which would
        // report a subject erased while their key, and recoverable PII, still exist.
        if (!await KeyExistsAsync(subjectId, ct))
        {
            return;
        }

        // Deletion must be explicitly enabled per key before it can be removed. The key
        // exists (checked above), so any failure here is real and must throw.
        using (var config = await SendAsync(
            HttpMethod.Post,
            $"v1/{mountPath}/keys/{KeyName(subjectId)}/config",
            new { deletion_allowed = true },
            ct))
        {
            config.EnsureSuccessStatusCode();
        }

        using var delete = await SendAsync(HttpMethod.Delete, $"v1/{mountPath}/keys/{KeyName(subjectId)}", null, ct);
        // Tolerate only a concurrent destroy having won the race (404); anything else throws.
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

    // The discriminator between "erased" and "failed": transit answers 404 for an absent
    // key and 200 for a present one. Read-only metadata; reaches no key material. This is
    // what lets DecryptAsync/DestroyKeyAsync tell a genuine erasure apart from a fault.
    private async Task<bool> KeyExistsAsync(string subjectId, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, $"v1/{mountPath}/keys/{KeyName(subjectId)}", null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
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
    // The name is interpolated into the request path, so the charset is also a security
    // boundary: validate before use (reject, never mangle) so a subject id can NEVER
    // inject a path segment ("../") or escape the key namespace, wherever the id came from.
    // This is the single chokepoint every call path (encrypt/decrypt/destroy/ensure/exists)
    // routes through, so guarding it here guards them all.
    private static string KeyName(string subjectId)
    {
        if (!IsValidSubjectId(subjectId))
        {
            throw new ArgumentException(
                $"Subject id '{subjectId}' contains characters not allowed in an OpenBao transit key name (permitted: [a-zA-Z0-9_-]).",
                nameof(subjectId));
        }

        return $"pii-{subjectId}";
    }

    private static bool IsValidSubjectId(string subjectId)
    {
        if (string.IsNullOrEmpty(subjectId))
        {
            return false;
        }

        foreach (var c in subjectId)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    private sealed record TransitResponse([property: JsonPropertyName("data")] TransitData? Data);

    private sealed record TransitData(
        [property: JsonPropertyName("ciphertext")] string? Ciphertext,
        [property: JsonPropertyName("plaintext")] string? Plaintext);
}
