using System.Text.Json;
using Babelstone.RateSheets;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Babelstone.RateSheets.Api;

/// <summary>
/// One-shot rate-sheet deploy (bd babelstone-zla1.21). Deploys the committed rate sheet(s) through
/// the SAME <see cref="DeployRateSheetEndpoint.HandleAsync"/> the HTTP endpoint uses, then exits — so
/// the staging <c>rate-sheet-deploy</c> Job provisions them on bring-up with the full ADR-PC-008
/// validation (pack bounds, envelope, forward-only immutability), never a raw INSERT.
///
/// The target may be a single JSON file OR a DIRECTORY: a directory deploys EVERY <c>*.json</c> sheet
/// under it (recursively, ordinal-sorted for determinism), so adding a new sheet version or a new
/// product family's sheet is just committing its YAML — no manifest edit. Idempotent by construction:
/// an unchanged re-deploy of any sheet is a 200 no-op; a changed body under an existing version id is
/// a 409 that fails the Job so the drift is visible. Every sheet is attempted even if an earlier one
/// fails; the process exits non-zero if ANY sheet failed.
/// </summary>
internal static class OneShotDeploy
{
    // snake_case, mirroring the HTTP endpoint's ConfigureHttpJsonOptions so a baked JSON file binds to
    // RateSheetDeployRequest exactly as a POSTed body would (rate_sheet_version_id, principal_cents…).
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
    };

    /// <summary>Deploy <paramref name="target"/> — a JSON rate-sheet file, or a directory of them —
    /// as <paramref name="actor"/>. Returns a process exit code: 0 = all deployed or idempotent
    /// no-ops, 1 = a handler refused a sheet (e.g. 409/400), 2 = a file could not be read/parsed or
    /// the directory held no sheets.</summary>
    public static async Task<int> RunAsync(IServiceProvider services, string target, string actor)
    {
        await using var scope = services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger("Babelstone.RateSheets.Api.OneShotDeploy");

        string[] files;
        if (Directory.Exists(target))
        {
            files = Directory.GetFiles(target, "*.json", SearchOption.AllDirectories);
            Array.Sort(files, StringComparer.Ordinal);   // deterministic order across runs
            if (files.Length == 0)
            {
                logger.LogError("one-shot deploy: no *.json rate sheets found under directory {Dir}", target);
                return 2;
            }
            logger.LogInformation("one-shot deploy: {Count} sheet(s) under {Dir}", files.Length, target);
        }
        else
        {
            files = [target];
        }

        var worst = 0;
        foreach (var file in files)
        {
            var code = await DeployOneAsync(sp, loggerFactory, logger, file, actor);
            if (code != 0 && worst == 0)
            {
                worst = code;   // remember the FIRST failure but keep going, so every sheet is attempted
            }
        }

        if (worst == 0)
        {
            logger.LogInformation("one-shot deploy: all {Count} sheet(s) deployed / idempotent", files.Length);
        }
        else
        {
            logger.LogError("one-shot deploy: at least one sheet failed (exit {Code})", worst);
        }
        return worst;
    }

    private static async Task<int> DeployOneAsync(
        IServiceProvider sp, ILoggerFactory loggerFactory, ILogger logger, string file, string actor)
    {
        RateSheetDeployRequest request;
        try
        {
            await using var stream = File.OpenRead(file);
            request = await JsonSerializer.DeserializeAsync<RateSheetDeployRequest>(stream, JsonOptions)
                      ?? throw new InvalidOperationException("the rate-sheet file deserialized to null");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "one-shot deploy: could not read/parse rate-sheet file {File}", file);
            return 2;
        }

        // A minimal HttpContext carries the X-Deploy-Actor header the handler attributes the deploy to
        // (ADR-PC-008 §P4), and captures the IResult's status without starting a listener.
        var ctx = new DefaultHttpContext { RequestServices = sp };
        ctx.Request.Headers["X-Deploy-Actor"] = actor;
        ctx.Response.Body = Stream.Null;

        var result = await DeployRateSheetEndpoint.HandleAsync(
            request,
            ctx.Request,
            sp.GetRequiredService<IRateSheetStore>(),
            sp.GetRequiredService<RateSheetValidator>(),
            sp.GetRequiredService<IRateBoundsSource>(),
            sp.GetRequiredService<IProductConfigSource>(),
            loggerFactory,
            CancellationToken.None);
        await result.ExecuteAsync(ctx);

        var status = ctx.Response.StatusCode;
        if (status is >= 200 and < 300)
        {
            logger.LogInformation("one-shot deploy of {File} succeeded (HTTP {Status})", file, status);
            return 0;
        }

        logger.LogError("one-shot deploy of {File} FAILED (HTTP {Status}) — see the handler log line above", file, status);
        return 1;
    }
}
