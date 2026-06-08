using System.Diagnostics;
using Babelstone.EventStore;
using Babelstone.Families.TermDeposit;
using Babelstone.Families.TermDeposit.Application;
using Babelstone.Telemetry;
using Microsoft.AspNetCore.Mvc;

namespace Babelstone.Engine.Api;

/// <summary>
/// The deposits command/query endpoints (ADR-PC-021 §D5). The Python MCP server (ADR-IC-010)
/// maps these to model-invokable tools — <c>constitute_deposit</c> (this POST), <c>get_deposit</c>
/// (the GET position), and <c>mature_deposit</c> (the maturity POST) — per IC-010's 2026-05-31
/// amendment (the tool/resource axis is control-ownership, not CQRS). The host owns the wall-clock
/// at this boundary (it stamps a missing constituted_at / matured_at); the decider stays pure.
/// </summary>
public static class DepositsEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/deposits", ConstituteAsync);
        app.MapGet("/v1/deposits/{id:guid}", GetPositionAsync);
        app.MapPost("/v1/deposits/{id:guid}/maturity", MatureAsync);
        app.MapPost("/v1/deposits/{id:guid}/interest", PayInterestAsync);

        // D.4 CQRS read-model query surface (ADR-IC-005), the I.2 Query API seam. Distinct from the
        // write-side GET above (which folds the live event log): these serve the denormalized
        // read_model.deposits table — a point lookup by id and a maturity-date range scan. Read-only,
        // no command path here (ADR-PC-018 §6 — the engine never staples a command onto its read
        // surface). The literal "/maturities" route is registered before the {id:guid} point lookup
        // shares the prefix, but the :guid constraint already excludes the word, so order is moot.
        app.MapGet("/v1/deposits/maturities", ListMaturitiesAsync);
        app.MapGet("/v1/deposits/{id:guid}/read-model", GetReadModelAsync);
    }

    private static async Task<IResult> ConstituteAsync(
        ConstituteDepositRequest request,
        TermDepositConstitutionService service,
        TimeProvider clock,
        CancellationToken ct)
    {
        var depositId = request.DepositId ?? Guid.NewGuid();
        var command = new ConstituteDepositCommand(
            DepositId: depositId,
            PrincipalCents: request.PrincipalCents,
            ProductId: request.ProductId,
            Role: request.Role,
            TermDays: request.TermDays,
            StartDate: request.StartDate,
            ConstitutedAt: request.ConstitutedAt ?? clock.GetUtcNow(),
            InterestVariant: request.InterestVariant,
            AutoRenewalPolicy: request.AutoRenewalPolicy,
            FundingAccount: request.FundingAccount,
            Actor: request.Actor ?? "mcp:dev",
            PaymentPeriodMonths: request.PaymentPeriodMonths);

        // The host shell is the composition root that knows the command, so the product-semantic
        // span is opened HERE, never in the pure decider/fold (ADR-PC-010 §P5 / ADR-IC-007 P2/P3).
        // Only structural identifiers are tagged — partition_key (v1 = the deposit/stream id) and
        // product_code — no PII (ADR-PC-004 §P2 / catalogue OBS_NO_PII_ATTRS). With no tracer
        // listening, StartActivity returns null and the using-block is a no-op.
        using var span = BabelstoneTelemetry.ActivitySource.StartActivity(
            BabelstoneAttributes.SpanConstituted, ActivityKind.Internal);
        span?.SetTag(BabelstoneAttributes.PartitionKey, depositId.ToString());
        span?.SetTag(BabelstoneAttributes.ProductCode, request.ProductId);

        try
        {
            await service.ConstituteAsync(command, ct);
        }
        catch (ConcurrencyException)
        {
            return Results.Problem($"Deposit {depositId} already exists.", statusCode: StatusCodes.Status409Conflict);
        }
        catch (DomainRejectedException e)
        {
            // A domain precondition rejected the request (no rate sheet effective, or an unpriced
            // (product, role)). Surface the reason as a 422 — never constitute on a silent default.
            // Corrupt data / wiring faults (a malformed band, a missing handler, a mis-pinned pack)
            // are NOT caught here: they propagate to UseExceptionHandler as a 500, not a client 422.
            return Results.Problem(e.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        return Results.Created($"/v1/deposits/{depositId}", new ConstituteDepositResponse(depositId, "ACTIVE"));
    }

    private static async Task<IResult> GetPositionAsync(
        Guid id, AggregateRuntime<DepositPosition> runtime, CancellationToken ct)
    {
        var hydrated = await runtime.LoadAsync(id, ct);
        return hydrated.Version < 0
            ? Results.NotFound()
            : Results.Ok(DepositPositionResponse.From(hydrated.State));
    }

    private static async Task<IResult> GetReadModelAsync(
        Guid id, IReadModelStore readModel, CancellationToken ct)
    {
        // The CQRS point lookup (ADR-IC-005 deposit_detail): serve the denormalized read-model row,
        // not the live fold. 404 when the projector has not yet materialised this deposit — the
        // caller falls back to the write-side GET for read-your-writes (ADR-IC-005 staleness note).
        var row = await readModel.GetAsync(id, ct);
        return row is null
            ? Results.NotFound()
            : Results.Ok(DepositReadModelResponse.From(row));
    }

    private static async Task<IResult> ListMaturitiesAsync(
        [FromQuery] DateOnly from, [FromQuery] DateOnly to, IReadModelStore readModel, CancellationToken ct)
    {
        // The CQRS range scan (ADR-IC-005 upcoming_maturities): deposits maturing in the half-open
        // [from, to) window, ordered by maturity date. A from >= to window is an empty, well-formed
        // result, not an error.
        if (to <= from)
        {
            return Results.Ok(new DepositMaturitiesResponse([]));
        }

        var rows = await readModel.ListByMaturityAsync(from, to, ct);
        var deposits = rows.Select(DepositReadModelResponse.From).ToList();
        return Results.Ok(new DepositMaturitiesResponse(deposits));
    }

    private static async Task<IResult> MatureAsync(
        Guid id,
        MatureDepositRequest request,
        TermDepositConstitutionService service,
        AggregateRuntime<DepositPosition> runtime,
        TimeProvider clock,
        CancellationToken ct)
    {
        var command = new MatureDepositCommand(
            DepositId: id,
            MaturedAt: request.MaturedAt ?? clock.GetUtcNow(),
            PayoutAccount: request.PayoutAccount ?? "PT50-DDA-001",
            Actor: request.Actor ?? "mcp:dev");

        Hydrated<DepositPosition> hydrated;

        // Maturity is where interest accrues and withholding tax is applied (decider AT_MATURITY
        // flow). The two product-semantic spans are opened HERE, in the impure host shell — never
        // in the pure decider/fold (ADR-PC-010 §P5 / ADR-IC-007 P2/P3). Tags are structural
        // identifiers and cents-native money only — no PII (ADR-PC-004 §P2 / catalogue
        // OBS_NO_PII_ATTRS). With no tracer listening, StartActivity returns null (a no-op).
        using (var accrual = BabelstoneTelemetry.ActivitySource.StartActivity(
            BabelstoneAttributes.SpanAccrualComputed, ActivityKind.Internal))
        using (var withholding = BabelstoneTelemetry.ActivitySource.StartActivity(
            BabelstoneAttributes.SpanWithholdingApplied, ActivityKind.Internal))
        {
            try
            {
                await service.MatureAsync(command, ct);
            }
            catch (ConcurrencyException)
            {
                return Results.Problem($"Deposit {id} was modified concurrently.", statusCode: StatusCodes.Status409Conflict);
            }
            catch (DomainRejectedException e)
            {
                // Not constituted / already matured (the lifecycle guard) — surface, don't double-mature.
                // A mis-pinned pack throws PackLoadException and corrupt rows throw other types: both
                // propagate as a 500, never masquerade as a client 422.
                return Results.Problem(e.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            hydrated = await runtime.LoadAsync(id, ct);
            var matured = hydrated.State;

            // partition_key is the stream id (v1: partition_key = stream_id, AggregateRuntime).
            // The accrual span carries the gross interest accrued; the withholding span the tax —
            // both cents-native off the folded position, never a formatted decimal.
            accrual?.SetTag(BabelstoneAttributes.PartitionKey, id.ToString());
            accrual?.SetTag(BabelstoneAttributes.InterestCents, matured.AccruedGrossInterest.Cents);
            withholding?.SetTag(BabelstoneAttributes.PartitionKey, id.ToString());
            withholding?.SetTag(BabelstoneAttributes.TaxCents, matured.WithholdingToDate.Cents);
        }

        return Results.Ok(DepositPositionResponse.From(hydrated.State));
    }

    private static async Task<IResult> PayInterestAsync(
        Guid id,
        PayInterestRequest request,
        TermDepositConstitutionService service,
        AggregateRuntime<DepositPosition> runtime,
        TimeProvider clock,
        CancellationToken ct)
    {
        var command = new PayInterestCommand(
            DepositId: id,
            PaidAt: request.PaidAt ?? clock.GetUtcNow(),
            PayoutAccount: request.PayoutAccount ?? "PT50-DDA-001",
            Actor: request.Actor ?? "mcp:dev");

        Hydrated<DepositPosition> hydrated;

        // A PERIODIC coupon is its own accrual + withholding flow, so the same two product-semantic
        // spans the maturity path opens are opened HERE, in the impure host shell — never in the pure
        // decider/fold (ADR-PC-010 §P5 / ADR-IC-007 P2/P3). Tags are structural identifiers and
        // cents-native money only — no PII (ADR-PC-004 §P2 / catalogue OBS_NO_PII_ATTRS).
        using (var accrual = BabelstoneTelemetry.ActivitySource.StartActivity(
            BabelstoneAttributes.SpanAccrualComputed, ActivityKind.Internal))
        using (var withholding = BabelstoneTelemetry.ActivitySource.StartActivity(
            BabelstoneAttributes.SpanWithholdingApplied, ActivityKind.Internal))
        {
            try
            {
                await service.PayInterestAsync(command, ct);
            }
            catch (ConcurrencyException)
            {
                return Results.Problem($"Deposit {id} was modified concurrently.", statusCode: StatusCodes.Status409Conflict);
            }
            catch (DomainRejectedException e)
            {
                // Not constituted / not PERIODIC / no intermediate coupon left (the lifecycle + variant
                // guards) — surface as a 422, never pay a phantom coupon. Wiring faults propagate as 500.
                return Results.Problem(e.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            hydrated = await runtime.LoadAsync(id, ct);
            var paid = hydrated.State;

            accrual?.SetTag(BabelstoneAttributes.PartitionKey, id.ToString());
            accrual?.SetTag(BabelstoneAttributes.InterestCents, paid.AccruedGrossInterest.Cents);
            withholding?.SetTag(BabelstoneAttributes.PartitionKey, id.ToString());
            withholding?.SetTag(BabelstoneAttributes.TaxCents, paid.WithholdingToDate.Cents);
        }

        return Results.Ok(DepositPositionResponse.From(hydrated.State));
    }
}
