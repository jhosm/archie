using Xunit;

namespace Babelstone.Pii.Tests;

/// <summary>
/// Subject-id validation in the OpenBao transit client (archie-e6fr.9). The key name is
/// interpolated into the request path, so a subject id outside [a-zA-Z0-9_-] must be
/// rejected at the chokepoint before it can inject a path segment or escape the key
/// namespace. These run in the default (Docker-free) lane: the guard throws before any
/// HTTP request is built, so no OpenBao is needed.
/// </summary>
public sealed class OpenBaoTransitClientGuardTests
{
    // BaseAddress is required for the HttpClient, but the guard fires before it is used.
    private static OpenBaoTransitClient Client()
        => new(new HttpClient { BaseAddress = new Uri("http://localhost:8200/") }, token: "unused");

    [Theory]
    [InlineData("../../sys/health")]   // path traversal
    [InlineData("subject/with/slash")] // extra path segment
    [InlineData("subject with spaces")]
    [InlineData("subject?inject=1")]   // query injection
    [InlineData("name#frag")]
    [InlineData("")]                   // empty
    public async Task A_subject_id_outside_the_safe_charset_is_rejected_on_every_path(string subjectId)
    {
        // The guard throws before any HttpRequestMessage is built, so these never touch
        // the network — a malicious id cannot reach OpenBao to inject a path. (Valid ids
        // flowing end-to-end are covered by the integration suite, which uses subject-{guid}.)
        await Assert.ThrowsAsync<ArgumentException>(() => Client().EncryptAsync(subjectId, [0x01]));
        await Assert.ThrowsAsync<ArgumentException>(() => Client().DecryptAsync(subjectId, [0x01]));
        await Assert.ThrowsAsync<ArgumentException>(() => Client().DestroyKeyAsync(subjectId));
    }
}
