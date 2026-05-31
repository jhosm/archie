using Babelstone.EventStore;
using Babelstone.Families.TermDeposit;
using Babelstone.Families.TermDeposit.Application;

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
            Actor: request.Actor ?? "mcp:dev");

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

        var hydrated = await runtime.LoadAsync(id, ct);
        return Results.Ok(DepositPositionResponse.From(hydrated.State));
    }
}
