using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace Babelstone.Orchestrator.Tests;

/// <summary>
/// A minimal in-process HTTP server (the lane's sanctioned stand-in for the engine / the settlement
/// ACL stub) the dispatcher tests POST against. It records every request — path, method, body, the
/// <c>Idempotency-Key</c>, <c>traceparent</c>, and gateway-attested SCA (<c>X-SCA-Acr</c> /
/// <c>X-SCA-Auth-Time</c>, bd babelstone-ls44) headers — and returns whatever (status, body) the
/// supplied responder chooses, so a test can assert the dispatcher's delivery and error-model
/// behaviour without standing up the real engine. Deliberately NOT a reference to the engine's
/// <c>WebApplicationFactory&lt;Program&gt;</c>: the orchestrator subtree (incl. its tests' build
/// graph here) stays extraction-ready (ADR-PC-019 §P2) — the dispatcher↔engine CONTRACT is pinned by
/// the separate Pact-style CDC tests against the real engine.
/// </summary>
public sealed class RecordingHttpServer : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly Func<RecordedRequest, (HttpStatusCode Status, string Body)> _responder;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();

    public RecordingHttpServer(Func<RecordedRequest, (HttpStatusCode Status, string Body)> responder)
    {
        _responder = responder;
        // Bind to an ephemeral loopback port. HttpListener needs a trailing slash on the prefix.
        var port = GetFreePort();
        BaseUrl = $"http://127.0.0.1:{port}";
        _listener = new HttpListener();
        _listener.Prefixes.Add($"{BaseUrl}/");
        _listener.Start();
        _loop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>The base URL the dispatcher targets (no trailing slash).</summary>
    public string BaseUrl { get; }

    /// <summary>Every request received, in arrival order.</summary>
    public IReadOnlyCollection<RecordedRequest> Requests => _requests.ToArray();

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception) when (_cts.IsCancellationRequested || !_listener.IsListening)
            {
                break;
            }

            try
            {
                using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                var body = await reader.ReadToEndAsync();
                var recorded = new RecordedRequest(
                    Path: context.Request.Url?.AbsolutePath ?? string.Empty,
                    Method: new HttpMethod(context.Request.HttpMethod),
                    Body: body,
                    IdempotencyKey: context.Request.Headers["Idempotency-Key"],
                    TraceParent: context.Request.Headers["traceparent"],
                    ScaAcr: context.Request.Headers["X-SCA-Acr"],
                    ScaAuthTime: context.Request.Headers["X-SCA-Auth-Time"]);
                _requests.Enqueue(recorded);

                var (status, responseBody) = _responder(recorded);
                var bytes = Encoding.UTF8.GetBytes(responseBody);
                context.Response.StatusCode = (int)status;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
            catch (Exception) when (_cts.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_listener.IsListening)
        {
            _listener.Stop();
        }

        _listener.Close();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }

        _cts.Dispose();
    }

    /// <summary>One recorded inbound request — the load-bearing fields the dispatcher contract sets.
    /// <paramref name="ScaAcr"/> / <paramref name="ScaAuthTime"/> are the gateway-attested step-up-SCA
    /// claims the dispatcher forwards for a money-mover (bd babelstone-ls44); null when the row carried
    /// no SCA attestation (the common case).</summary>
    public sealed record RecordedRequest(
        string Path, HttpMethod Method, string Body, string? IdempotencyKey, string? TraceParent,
        string? ScaAcr = null, string? ScaAuthTime = null);
}
