using System.Diagnostics;
using Babelstone.EventStore;
using Babelstone.Families.TermDeposit;
using Babelstone.Families.TermDeposit.Application;
using Babelstone.Pii;
using Babelstone.Telemetry;
using Microsoft.AspNetCore.Mvc;

namespace Babelstone.Engine.Api;

/// <summary>
/// The deposits command/query endpoints (ADR-PC-021 §D5). The Python MCP server (ADR-IC-010)
/// maps these to model-invokable tools — <c>constitute_deposit</c> (this POST), <c>get_deposit</c>
/// (the GET; a <c>min_sequence</c> arg threads read-your-writes), and <c>mature_deposit</c> (the
/// maturity POST) — per IC-010's 2026-05-31 amendment (the tool/resource axis is control-ownership,
/// not CQRS). There is ONE deposit read resource: <c>GET /v1/deposits/{id}</c> serves the denormalized
/// read model by default and folds the event stream only for read-your-writes — the CQRS read/write
/// split is the engine's internal business, never two public URLs (storage/mechanism never appears in
/// a read path). The host owns the wall-clock at this boundary (it stamps a missing constituted_at /
/// matured_at); the decider stays pure.
/// </summary>
public static class DepositsEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/deposits", ConstituteAsync);
        app.MapPost("/v1/deposits/{id:guid}/maturity", MatureAsync);
        app.MapPost("/v1/deposits/{id:guid}/interest", PayInterestAsync);

        // Partial early withdrawal (F.12; 02 §2.4.1, bd qze9): reduce the principal, leaving the deposit
        // Active. A command surface mirroring the maturity/interest pattern — a domain rejection (not
        // Active, within carência, below the minimum, leaving too little, or the whole balance) surfaces
        // as a 422, never a phantom withdrawal.
        app.MapPost("/v1/deposits/{id:guid}/partial-withdrawal", WithdrawPartiallyAsync);

        // GDPR Article 17 right-to-be-forgotten (bd babelstone-nzw6): crypto-shred the subject's PII
        // key and record the erasure fact. A command surface, mandatory Idempotency-Key (ADR-PC-029
        // slot 4) — erasure must be safely retryable since key destruction is irreversible.
        app.MapPost("/v1/deposits/{id:guid}/erase-personal-data", ErasePersonalDataAsync);

        // The renewal-saga command surface (bd babelstone-mtto PR B): the two idempotent legs the
        // renewal saga drives, replacing the retired monolithic RenewAsync. {id} is the CLOSING (Matured)
        // deposit id; constitute-renewal opens the NEW stream (201 + Location /v1/deposits/{newId}),
        // renewal-link folds the closing stream Matured → Renewed (200). Both carry a mandatory
        // Idempotency-Key (ADR-PC-029 slot 4).
        app.MapPost("/v1/deposits/{id:guid}/constitute-renewal", ConstituteRenewalAsync);
        app.MapPost("/v1/deposits/{id:guid}/renewal-link", LinkRenewalAsync);

        // The CQRS query surface (ADR-IC-005, the I.2 Query API seam). ONE canonical deposit resource
        // — GET /v1/deposits/{id} — served from the denormalized read_model.deposits row by default and
        // folded from the event stream only as a read-your-writes fallback (see GetDepositAsync). There
        // is NO /read-model sibling: storage/mechanism never appears in a read URL, so consumers can't
        // gravitate to "the wrong GET". The maturities range scan is a separate query-named collection
        // (no write-side twin, so no duality). Read-only, no command path here (ADR-PC-018 §6 — the
        // engine never staples a command onto its read surface). The literal "/maturities" route shares
        // the prefix with the {id:guid} point lookup, but the :guid constraint already excludes the
        // word, so registration order is moot.
        app.MapGet("/v1/deposits/maturities", ListMaturitiesAsync);
        app.MapGet("/v1/deposits/{id:guid}", GetDepositAsync);
    }

    private static async Task<IResult> ConstituteAsync(
        ConstituteDepositRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        TermDepositConstitutionService service,
        ICommandLog commandLog,
        TimeProvider clock,
        CancellationToken ct)
    {
        // ADR-PC-029 slot 1: the caller MUST supply a deterministic command id as the Idempotency-Key
        // header (in practice the saga's saga_outbox row id). It is MANDATORY — the engine never
        // accepts a non-idempotent constitution, so a caller that omits or malforms the key fails loud
        // (400) rather than silently losing the dispatcher's at-least-once retry safety. The engine
        // cannot generate one itself: a server-minted id would change on every retry and defeat the
        // dedup entirely, so requiring it from the caller is the only safe contract.
        if (idempotencyKey is null || !Guid.TryParse(idempotencyKey, out var commandId))
        {
            return Results.Problem(
                "Idempotency-Key header is required and must be a UUID (ADR-PC-029 slot 1).",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Pre-check BEFORE any side effect (decide / eager settle / append): a known command id
        // replays the original outcome with NO second settle and NO second append (slot 4). The
        // crash-atomic guarantee is the in-transaction command_dedup INSERT inside the append (which
        // raises DuplicateCommandException on a concurrent racer that slips past this read); this read
        // keeps the common sequential retry off the write path entirely. The deposit id is read back
        // from the receipt because it is an OUTPUT for a constitution.
        var receipt = await commandLog.TryGetAsync(commandId, ct);
        if (receipt is not null)
        {
            return Results.Created(
                $"/v1/deposits/{receipt.StreamId}",
                new ConstituteDepositResponse(receipt.StreamId, "ACTIVE", receipt.CommitSequence));
        }

        var depositId = request.DepositId ?? Guid.NewGuid();
        var constitutedAt = request.ConstitutedAt ?? clock.GetUtcNow();
        var actor = request.Actor ?? "mcp:dev";

        // The host shell is the composition root that knows the command, so the product-semantic
        // span is opened HERE, never in the pure decider/fold (ADR-PC-010 §P5 / ADR-IC-007 P2/P3).
        // Only structural identifiers are tagged — partition_key (v1 = the deposit/stream id) and
        // product_code — no PII (ADR-PC-004 §P2 / catalogue OBS_NO_PII_ATTRS). With no tracer
        // listening, StartActivity returns null and the using-block is a no-op.
        using var span = BabelstoneTelemetry.ActivitySource.StartActivity(
            BabelstoneAttributes.SpanConstituted, ActivityKind.Internal);
        span?.SetTag(BabelstoneAttributes.PartitionKey, depositId.ToString());
        span?.SetTag(BabelstoneAttributes.ProductCode, request.ProductId);

        long commitSequence;
        try
        {
            // Fork B rework (bd t7o3.11 / 3k10 / c8d8): when the body omits the structural facts — the
            // MINIMAL saga body {deposit_id, product_id, principal_cents, funding_account} — the engine
            // RESOLVES the term / interest variant / renewal policy / coupon cadence / role from its
            // deployed product-config store, IN-TRANSACTION alongside the rate-sheet resolve (ADR-PC-008
            // §S2 / ADR-PC-009). The orchestrator carries no product-family knowledge. A direct caller
            // (the MCP agent, API tests) that DOES supply the full shape stays on the explicit path,
            // which honours every supplied field unchanged.
            commitSequence = HasStructuralFacts(request)
                ? await service.ConstituteAsync(
                    BuildFullCommand(request, depositId, constitutedAt, actor, commandId), ct)
                : await service.ConstituteFromProductConfigAsync(
                    new MinimalConstituteDepositRequest(
                        DepositId: depositId,
                        ProductId: request.ProductId,
                        PrincipalCents: request.PrincipalCents,
                        FundingAccount: request.FundingAccount,
                        ConstitutedAt: constitutedAt,
                        Actor: actor,
                        CommandId: commandId,
                        Role: request.Role),
                    ct);
        }
        catch (DuplicateCommandException dup)
        {
            // A concurrent duplicate slipped past the pre-check: the append rolled back (no second
            // append) and handed back the ORIGINAL outcome. Return it verbatim — the same 201 the
            // first apply returned (ADR-PC-029 slot 4 · idempotent replay).
            return Results.Created(
                $"/v1/deposits/{dup.StreamId}",
                new ConstituteDepositResponse(dup.StreamId, "ACTIVE", dup.CommitSequence));
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

        // commit_sequence is the head version the append reached: the caller threads it back as
        // If-Min-Sequence on the follow-up GET to read its own write (ADR-IC-005 §P3).
        return Results.Created(
            $"/v1/deposits/{depositId}", new ConstituteDepositResponse(depositId, "ACTIVE", commitSequence));
    }

    /// <summary>
    /// True when the caller supplied the full structural shape (term + start date + interest variant +
    /// renewal policy) on the body — the explicit, full-facts path a direct caller (the MCP agent, API
    /// tests) takes. False means the MINIMAL saga body, where the engine resolves the shape from the
    /// product config (Fork B rework, bd t7o3.11 / 3k10 / c8d8). Either all four are present or the
    /// engine resolves all of them — a partial shape is treated as minimal so a missing field is
    /// resolved rather than silently defaulted.
    /// </summary>
    private static bool HasStructuralFacts(ConstituteDepositRequest request) =>
        request.TermDays is not null
        && request.StartDate is not null
        && !string.IsNullOrWhiteSpace(request.InterestVariant)
        && !string.IsNullOrWhiteSpace(request.AutoRenewalPolicy);

    /// <summary>
    /// Build the full <see cref="ConstituteDepositCommand"/> from a body that carries the complete
    /// structural shape (the explicit, full-facts path). Honours every supplied field unchanged —
    /// <see cref="HasStructuralFacts"/> guards that the four shape fields are present, so the
    /// non-null assertions hold; the role defaults to <c>standard</c> and the cadence to 0 when those
    /// optional fields are omitted on the full path.
    /// </summary>
    private static ConstituteDepositCommand BuildFullCommand(
        ConstituteDepositRequest request, Guid depositId, DateTimeOffset constitutedAt, string actor, Guid commandId) =>
        new(
            DepositId: depositId,
            PrincipalCents: request.PrincipalCents,
            ProductId: request.ProductId,
            Role: request.Role ?? "standard",
            TermDays: request.TermDays!.Value,
            StartDate: request.StartDate!.Value,
            ConstitutedAt: constitutedAt,
            InterestVariant: request.InterestVariant!,
            AutoRenewalPolicy: request.AutoRenewalPolicy!,
            FundingAccount: request.FundingAccount,
            Actor: actor,
            PaymentPeriodMonths: request.PaymentPeriodMonths ?? 0,
            CommandId: commandId);

    private static async Task<IResult> GetDepositAsync(
        Guid id,
        [FromHeader(Name = "If-Min-Sequence")] long? minSequence,
        [FromQuery(Name = "as_of_sequence")] long? asOfSequence,
        IDepositReadModelStore readModel,
        AggregateRuntime<DepositPosition> runtime,
        CancellationToken ct)
    {
        // The as-of / point-in-time branch (the I.2 Query API as-of axis, bd babelstone-b4wp). When the
        // caller asks "?as_of_sequence=N", they want the HISTORICAL projection at per-stream sequence N,
        // not the current head — so this is NEVER served from the read model (which only ever holds the
        // current belief, ADR-IC-005 §P2 — one row per stream, no temporal history). It folds the event
        // stream up to and INCLUDING N (the same pure, deterministic fold the read-your-writes fallback
        // uses, generalised with an upper bound). The axis is the per-stream commit_sequence — the only
        // point identifier the event log carries a deterministic total order for; a wall-clock valid_time
        // axis (?as_of=<timestamp>) is deferred to the bitemporal projection runtime (Epic D /
        // ADR-PC-002), which the read model does not yet carry.
        if (asOfSequence is not null)
        {
            // Malformed point: a negative sequence is a bad request (the per-stream sequence_number
            // domain starts at 0). A clean 400, never a 500.
            if (asOfSequence.Value < 0)
            {
                return Results.Problem(
                    "as_of_sequence must be a non-negative per-stream sequence number.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var asOf = await runtime.LoadAsOfSequenceAsync(id, asOfSequence.Value, ct);

            // A genuinely non-existent deposit folds to Version < 0 → 404 (the as_of axis does not
            // change the unknown-stream verdict).
            if (asOf.Version < 0)
            {
                return Results.NotFound();
            }

            // The point is past the stream head — the caller asked for a sequence that does not exist
            // yet. The fold stopped at the real head (Version < the requested point), so we reject the
            // future point as a clean 422, never a silent fold-to-head that would pretend a not-yet
            // sequence is "now".
            if (asOf.Version < asOfSequence.Value)
            {
                return Results.Problem(
                    $"as_of_sequence {asOfSequence.Value} is beyond the stream head ({asOf.Version}).",
                    statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            return Results.Ok(DepositResponse.FromFold(asOf));
        }

        // The ONE canonical deposit read (ADR-IC-005). Default path: serve the denormalized read-model
        // row — fast, eventually consistent, the 99% case (listings, dashboards). The aggregate fold is
        // NOT a public URL; it is the internal read-your-writes fallback this handler reaches for when:
        //   * the projector has not yet materialised the row (row is null), or
        //   * the caller passed an If-Min-Sequence token (the commit_sequence a command returned) and
        //     the row is staler than it (row.LastSequence < token).
        // In both cases we fold the (short) deposit stream to the authoritative head and return that.
        // Either branch fills the SAME DepositResponse shape, so the consumer never sees which path
        // served it — that is what lets there be one URL, with consistency expressed as an optional
        // token rather than a separate /read-model endpoint (the duality-of-GETs settled at the
        // resource). A genuinely non-existent deposit folds to Version < 0 → 404.
        var row = await readModel.GetAsync(id, ct);
        if (row is not null && (minSequence is null || row.LastSequence >= minSequence.Value))
        {
            return Results.Ok(DepositResponse.FromReadModel(row));
        }

        var hydrated = await runtime.LoadAsync(id, ct);
        return hydrated.Version < 0
            ? Results.NotFound()
            : Results.Ok(DepositResponse.FromFold(hydrated));
    }

    private static async Task<IResult> ListMaturitiesAsync(
        [FromQuery] DateOnly from, [FromQuery] DateOnly to, IDepositReadModelStore readModel, CancellationToken ct)
    {
        // The CQRS range scan (ADR-IC-005 upcoming_maturities): deposits maturing in the half-open
        // [from, to) window, ordered by maturity date. A query-named collection with no write-side twin
        // (the fold cannot answer a cross-stream range scan), so no duality. A from >= to window is an
        // empty, well-formed result, not an error.
        if (to <= from)
        {
            return Results.Ok(new DepositMaturitiesResponse([]));
        }

        var rows = await readModel.ListByMaturityAsync(from, to, ct);
        var deposits = rows.Select(DepositResponse.FromReadModel).ToList();
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

        // The post-append fold is authoritative (read-your-writes by construction): its head version is
        // the commit_sequence, carried on the response as last_sequence (DepositResponse.FromFold).
        return Results.Ok(DepositResponse.FromFold(hydrated));
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

        // The post-append fold is authoritative (read-your-writes by construction): its head version is
        // the commit_sequence, carried on the response as last_sequence (DepositResponse.FromFold).
        return Results.Ok(DepositResponse.FromFold(hydrated));
    }

    private static async Task<IResult> WithdrawPartiallyAsync(
        Guid id,
        PartialWithdrawRequest request,
        TermDepositConstitutionService service,
        AggregateRuntime<DepositPosition> runtime,
        TimeProvider clock,
        CancellationToken ct)
    {
        // The host shell owns the wall-clock at this boundary: it stamps a missing withdrawn_at, and the
        // service derives the as-of withdrawal date from that instant and passes it to the pure decider as
        // an INPUT (ADR-PC-010 §P5). The withdrawal amount is a per-deposit cents input; the F.12 policy is
        // resolved engine-side from the deposit's product config (bd k6r8.8), never sent on the wire.
        var command = new PartialWithdrawCommand(
            DepositId: id,
            WithdrawnAt: request.WithdrawnAt ?? clock.GetUtcNow(),
            WithdrawnAmountCents: request.WithdrawnAmountCents,
            Actor: request.Actor ?? "mcp:dev");

        try
        {
            await service.WithdrawPartiallyAsync(command, ct);
        }
        catch (ConcurrencyException)
        {
            return Results.Problem($"Deposit {id} was modified concurrently.", statusCode: StatusCodes.Status409Conflict);
        }
        catch (DomainRejectedException e)
        {
            // The lifecycle + F.12 policy gates: not Active, within carência, below the minimum withdrawal,
            // leaving less than the minimum remaining balance, or the whole balance (which is a termination,
            // F.4). Surface as a 422 — never a phantom withdrawal. A mis-pinned pack / corrupt row / absent
            // product-config store throws other types and propagates as a 500, not a masquerading 422.
            return Results.Problem(e.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        // The post-append fold is authoritative (read-your-writes by construction): the deposit stays
        // Active with a reduced remaining principal; its head version is the commit_sequence on the response.
        var hydrated = await runtime.LoadAsync(id, ct);
        return Results.Ok(DepositResponse.FromFold(hydrated));
    }

    /// <summary>The secret name the pseudonym HMAC salt resolves under through <c>ISecretProvider</c>
    /// (ADR-PC-004 §A1). It is a SECRET — the same salt the Customer Data Store holds to reverse the
    /// pseudonym (ADR-IC-016 §8) — never a compile-time constant, never logged.</summary>
    private const string SubjectPseudonymSaltSecret = "SubjectPseudonymSalt";

    /// <summary>
    /// GDPR Article 17 right-to-be-forgotten (bd babelstone-nzw6): crypto-shred the data subject's
    /// encryption key (ADR-PC-004 §P3) and record the structural erasure fact. After this the subject's
    /// PII ciphertext is permanently unrecoverable and only the deposit's non-personal structural fields
    /// remain queryable. The raw subject id is used ONLY at this host shell — to destroy the key and to
    /// derive the salted one-way pseudonym that goes on the persisted event — and is never written to the
    /// bus, a span, or the response (ADR-PC-004 §P2 / ADR-IC-016 §8).
    /// </summary>
    private static async Task<IResult> ErasePersonalDataAsync(
        Guid id,
        ErasePersonalDataRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        TermDepositConstitutionService service,
        AggregateRuntime<DepositPosition> runtime,
        IPiiKeyStore piiKeyStore,
        ISecretProvider secrets,
        ICommandLog commandLog,
        TimeProvider clock,
        CancellationToken ct)
    {
        // ADR-PC-029 slot 4: a deterministic Idempotency-Key is MANDATORY — key destruction is
        // irreversible, so a non-idempotent retry must never double-fire or be lost. Fail loud (400)
        // rather than silently accept a non-idempotent erasure.
        if (idempotencyKey is null || !Guid.TryParse(idempotencyKey, out var commandId))
        {
            return Results.Problem(
                "Idempotency-Key header is required and must be a UUID (ADR-PC-029 slot 4).",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (string.IsNullOrWhiteSpace(request.SubjectId))
        {
            return Results.Problem(
                "subject_id is required to crypto-shred the subject's key.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Pre-check BEFORE any side effect (key destroy / append): a known command id replays the
        // ORIGINAL outcome with NO second crypto-shred and NO second append (ADR-PC-029 slot 4). Returns
        // the same commit_sequence the first apply did — the idempotent retry the mandatory key requires
        // (NOT the lifecycle 422, which only guards a fresh re-erasure with a NEW command id).
        var receipt = await commandLog.TryGetAsync(commandId, ct);
        if (receipt is not null)
        {
            var replay = await runtime.LoadAsync(id, ct);
            return Results.Ok(new ErasePersonalDataResponse(
                id, replay.State.Lifecycle.ToString().ToUpperInvariant(), receipt.CommitSequence));
        }

        var erasedAt = request.ErasedAt ?? clock.GetUtcNow();
        var actor = request.Actor ?? "gdpr:erasure";
        var reason = string.IsNullOrWhiteSpace(request.ErasureReason) ? "GDPR_ARTICLE_17" : request.ErasureReason;

        // Open a product-semantic span in the impure host shell (never in a fold). It carries ONLY the
        // structural partition_key and the salted subject_pseudonym — never the raw subject id
        // (ADR-IC-016 §8 / catalogue OBS_NO_PII_ATTRS). With no tracer listening this is a no-op.
        using var span = BabelstoneTelemetry.ActivitySource.StartActivity(
            BabelstoneAttributes.SpanConstituted, ActivityKind.Internal);

        // Derive the salted one-way pseudonym (the salt is an ISecretProvider secret — the same salt
        // the Customer Data Store holds, ADR-IC-016 §8). This is the ONLY value derived from the raw
        // subject id that ever leaves this method: the bus event and the span carry the hash, not the id.
        string subjectPseudonym;
        try
        {
            var salt = await secrets.GetSecretAsync(SubjectPseudonymSaltSecret, ct);
            subjectPseudonym = ClientPseudonym.Of(request.SubjectId, salt);
        }
        catch (SecretProviderException)
        {
            // No salt configured ⇒ we cannot mint a non-reversible pseudonym, so we must NOT proceed
            // (an un-salted reference would re-leak identity, ADR-IC-016 §8 residual risk). Fail loud.
            return Results.Problem(
                "Cannot derive the subject pseudonym: the pseudonym salt secret is not configured.",
                statusCode: StatusCodes.Status500InternalServerError);
        }

        span?.SetTag(BabelstoneAttributes.PartitionKey, id.ToString());
        span?.SetTag(BabelstoneAttributes.SubjectPseudonym, subjectPseudonym);

        long commitSequence;
        try
        {
            // Crypto-shred FIRST (ADR-PC-004 §P3) — irreversible, idempotent (destroying an absent key
            // is a no-op). Then record the structural audit fact. If the append fails after the destroy,
            // a retry re-destroys (no-op) and re-appends, converging on the erased state.
            await piiKeyStore.DestroyKeyAsync(request.SubjectId, ct);

            commitSequence = await service.ErasePersonalDataAsync(
                new ErasePersonalDataCommand(
                    DepositId: id,
                    SubjectPseudonym: subjectPseudonym,
                    ErasedAt: erasedAt,
                    ErasureReason: reason,
                    Actor: actor,
                    CommandId: commandId),
                ct);
        }
        catch (DuplicateCommandException dup)
        {
            // A concurrent duplicate slipped past the pre-check: the in-transaction command_dedup INSERT
            // rolled the append back (no second append) and handed back the ORIGINAL outcome. Return it
            // verbatim — the same idempotent replay slot 4 mandates (ADR-PC-029).
            var replay = await runtime.LoadAsync(id, ct);
            return Results.Ok(new ErasePersonalDataResponse(
                id, replay.State.Lifecycle.ToString().ToUpperInvariant(), dup.CommitSequence));
        }
        catch (ConcurrencyException)
        {
            return Results.Problem($"Deposit {id} was modified concurrently.", statusCode: StatusCodes.Status409Conflict);
        }
        catch (DomainRejectedException e)
        {
            // Not erasable from the current lifecycle (no deposit, or already erased under a DIFFERENT
            // command id — the lifecycle guard). Surface as 422; the key destroy above is idempotent so a
            // re-erase is harmless. (A retry of the SAME command id is handled by the dedup paths above.)
            return Results.Problem(e.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        // Read back the erased position: only non-personal structural fields remain (the PII lived
        // behind the now-destroyed key, never in this projection). Confirm the terminal Erased state.
        var hydrated = await runtime.LoadAsync(id, ct);
        return Results.Ok(
            new ErasePersonalDataResponse(id, hydrated.State.Lifecycle.ToString().ToUpperInvariant(), commitSequence));
    }

    /// <summary>
    /// Step 2 of the renewal saga (bd babelstone-mtto PR B): open the renewed instance off a CLOSING
    /// (Matured) deposit. <c>{id}</c> is the closing deposit id (the saga's process_id); the body is
    /// MINIMAL (bd babelstone-mtto.5) — the new deposit id, the renewal instant and the actor. The engine
    /// reads EVERY renewal fact (term / variant / cadence / policy AND product code / role / funding) off
    /// the closing deposit's folded state, so the orchestrator carries no product-family knowledge
    /// (ADR-IC-003 §A7). On success returns 201 with Location pointing at the NEW stream — the renewed
    /// instance is the resource this opens (mirroring <see cref="ConstituteAsync"/>'s 201). The full
    /// idempotency scaffold (ADR-PC-029 slot 4) is identical to ConstituteAsync.
    /// </summary>
    private static async Task<IResult> ConstituteRenewalAsync(
        Guid id,
        ConstituteRenewalRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        TermDepositConstitutionService service,
        ICommandLog commandLog,
        TimeProvider clock,
        CancellationToken ct)
    {
        // ADR-PC-029 slot 4: the dispatcher MUST supply a deterministic command id as the Idempotency-Key
        // (the saga's saga_outbox row id). MANDATORY — the engine never accepts a non-idempotent renewal
        // leg, so a missing/malformed key fails loud (400) rather than losing at-least-once retry safety.
        if (idempotencyKey is null || !Guid.TryParse(idempotencyKey, out var commandId))
        {
            return Results.Problem(
                "Idempotency-Key header is required and must be a UUID (ADR-PC-029 slot 4).",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Pre-check BEFORE any side effect: a known command id replays the original outcome with NO second
        // settle and NO second append. The receipt's StreamId is the NEW deposit id (the append opened that
        // stream), so the Location is read back from the receipt — it is an OUTPUT of this command.
        var receipt = await commandLog.TryGetAsync(commandId, ct);
        if (receipt is not null)
        {
            return Results.Created(
                $"/v1/deposits/{receipt.StreamId}",
                new ConstituteRenewalResponse(id, receipt.StreamId, "ACTIVE", receipt.CommitSequence));
        }

        var renewedAt = request.RenewedAt ?? clock.GetUtcNow();
        var actor = request.Actor ?? "saga:renewal";

        long commitSequence;
        try
        {
            commitSequence = await service.ConstituteRenewalAsync(
                new ConstituteRenewalCommand(
                    DepositId: id,
                    NewDepositId: request.NewDepositId,
                    RenewedAt: renewedAt,
                    Actor: actor,
                    CommandId: commandId),
                ct);
        }
        catch (DuplicateCommandException dup)
        {
            // A concurrent duplicate slipped past the pre-check: the append rolled back (no second append)
            // and handed back the ORIGINAL outcome — the same 201 the first apply returned (slot 4 replay).
            return Results.Created(
                $"/v1/deposits/{dup.StreamId}",
                new ConstituteRenewalResponse(id, dup.StreamId, "ACTIVE", dup.CommitSequence));
        }
        catch (ConcurrencyException)
        {
            // The new stream already exists at a head (a non-idempotent collision on NewDepositId).
            return Results.Problem(
                $"Renewed deposit {request.NewDepositId} already exists.", statusCode: StatusCodes.Status409Conflict);
        }
        catch (DomainRejectedException e)
        {
            // Closing deposit not Matured, policy NONE, or an unpriced renewal rate — surface as a 422,
            // never open the new stream on a silent default. Wiring faults propagate as 500, not a 422.
            return Results.Problem(e.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        return Results.Created(
            $"/v1/deposits/{request.NewDepositId}",
            new ConstituteRenewalResponse(id, request.NewDepositId, "ACTIVE", commitSequence));
    }

    /// <summary>
    /// Step 3 of the renewal saga (bd babelstone-mtto PR B): append DepositRenewed to the CLOSING stream,
    /// folding it Matured → Renewed. <c>{id}</c> is the closing deposit id; the body carries the new
    /// deposit id whose head DepositConstituted fills the link. Returns 200 (it mutates an existing stream,
    /// it does not create a resource). Full idempotency scaffold (ADR-PC-029 slot 4) as ConstituteAsync.
    /// </summary>
    private static async Task<IResult> LinkRenewalAsync(
        Guid id,
        LinkRenewalRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        TermDepositConstitutionService service,
        ICommandLog commandLog,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (idempotencyKey is null || !Guid.TryParse(idempotencyKey, out var commandId))
        {
            return Results.Problem(
                "Idempotency-Key header is required and must be a UUID (ADR-PC-029 slot 4).",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // A known command id replays the original outcome (the closing stream's post-link head) with no
        // second append. The receipt's StreamId is the CLOSING deposit id this command appended to.
        var receipt = await commandLog.TryGetAsync(commandId, ct);
        if (receipt is not null)
        {
            return Results.Ok(new LinkRenewalResponse(id, request.NewDepositId, "RENEWED", receipt.CommitSequence));
        }

        var renewedAt = request.RenewedAt ?? clock.GetUtcNow();
        var actor = request.Actor ?? "saga:renewal";

        long commitSequence;
        try
        {
            commitSequence = await service.LinkRenewalAsync(
                new LinkRenewalCommand(
                    DepositId: id,
                    NewDepositId: request.NewDepositId,
                    RenewedAt: renewedAt,
                    Actor: actor,
                    CommandId: commandId),
                ct);
        }
        catch (DuplicateCommandException dup)
        {
            return Results.Ok(new LinkRenewalResponse(id, request.NewDepositId, "RENEWED", dup.CommitSequence));
        }
        catch (ConcurrencyException)
        {
            return Results.Problem($"Deposit {id} was modified concurrently.", statusCode: StatusCodes.Status409Conflict);
        }
        catch (DomainRejectedException e)
        {
            // Closing deposit not Matured, or the new stream missing — surface as a 422. Wiring faults 500.
            return Results.Problem(e.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        return Results.Ok(new LinkRenewalResponse(id, request.NewDepositId, "RENEWED", commitSequence));
    }
}
