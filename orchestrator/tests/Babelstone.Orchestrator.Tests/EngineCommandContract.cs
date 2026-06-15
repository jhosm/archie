using System.Text.Json;
using Xunit;

namespace Babelstone.Orchestrator.Tests;

/// <summary>
/// The dispatcher↔engine command contract (ENGINE_COMMAND_PACT, ADR-PC-029 slot 6 / ADR-IC-009) as a
/// single, explicit object both halves of the Pact-style CDC pin to. The CONSUMER half
/// (<see cref="EngineCommandPactConsumerTests"/>) asserts the dispatcher PRODUCES a request matching
/// <see cref="AssertConsumerRequest"/>; the PROVIDER half (<c>EngineCommandPactProviderTests</c> in
/// the engine API test project, anchored to the SAME Test ID) replays that request against the real
/// engine and asserts the engine HONOURS it. Keeping the contract's load-bearing facts here — the
/// route, the mandatory <c>Idempotency-Key</c> (a UUID), the snake_case body the engine's
/// SnakeCaseLower options expect, and the 201 + commit_sequence response — is what makes a
/// provider-side break a build failure.
/// </summary>
/// <remarks>
/// The catalogue Test ID <c>ENGINE_COMMAND_PACT</c> stays <c>Planned</c> until the formal PactNet
/// broker harness lands (a larger, CI-fragile greenfield change than this lane); this Pact-style CDC
/// pins the contract in code in the meantime — the contract is concretely held by the consumer +
/// provider tests, never faked Live. The string token below lets the spec-coverage checker find the
/// Test ID under the CODE_DIRS once row 20 is flipped.
/// </remarks>
public static class EngineCommandContract
{
    /// <summary>The catalogue Test ID this contract realises (ADR-PC-029 slot 6).</summary>
    public const string TestId = "ENGINE_COMMAND_PACT";

    /// <summary>The Pact-pinned engine command route (ADR-PC-029 slot 1; the write companion to
    /// ADR-PC-027's read surface).</summary>
    public const string ConstituteRoute = "/v1/deposits";

    /// <summary>The mandatory idempotency header (ADR-PC-029 slot 1): a deterministic UUID, in
    /// practice the saga_outbox row id; the engine 400s on absent/malformed.</summary>
    public const string IdempotencyHeader = "Idempotency-Key";

    /// <summary>
    /// The CONSUMER expectation: the request the dispatcher MUST produce for an ActivateDeposit →
    /// engine constitution. Pinned facts: the POST route, a present + parseable UUID Idempotency-Key,
    /// and a non-empty, well-formed JSON body. The provider verification replays the same shape.
    /// </summary>
    public static void AssertConsumerRequest(RecordingHttpServer.RecordedRequest request)
    {
        Assert.Equal(ConstituteRoute, request.Path);
        Assert.Equal(HttpMethod.Post, request.Method);

        // The Idempotency-Key is MANDATORY and a UUID (ADR-PC-029 slot 1) — the dispatcher always
        // sets it to the saga_outbox row's message_id.
        Assert.False(string.IsNullOrEmpty(request.IdempotencyKey), "Idempotency-Key must be present");
        Assert.True(Guid.TryParse(request.IdempotencyKey, out _), "Idempotency-Key must be a UUID");

        // A well-formed JSON body that IS a snake_case ConstituteDepositRequest (bd babelstone-t7o3.11):
        // the dispatcher's ActivateDeposit body now serializes the engine constitute shape, NOT the
        // polymorphic saga envelope. The load-bearing clauses: the structural product facts the engine
        // prices on (product_id, principal_cents, term_days, …) AND deposit_id — which the dispatcher
        // sets to the saga's process_id so the relayed DepositConstituted carries ce_subject = process_id.
        // The TAN is deliberately ABSENT: the engine resolves the rate in-transaction (bd babelstone-3k10).
        Assert.False(string.IsNullOrWhiteSpace(request.Body), "request body must be present");
        using var document = JsonDocument.Parse(request.Body);
        var root = document.RootElement;
        foreach (var field in new[]
                 {
                     "deposit_id", "product_id", "principal_cents", "role", "term_days",
                     "start_date", "interest_variant", "auto_renewal_policy", "funding_account",
                 })
        {
            Assert.True(root.TryGetProperty(field, out _),
                $"the constitute body must carry '{field}' (ENGINE_COMMAND_PACT, bd babelstone-t7o3.11)");
        }

        // deposit_id is a UUID (the saga's process_id) — the ce_subject correlation pin.
        Assert.True(Guid.TryParse(root.GetProperty("deposit_id").GetString(), out _),
            "deposit_id must be the saga's process_id (a UUID) so ce_subject = process_id");
    }

    /// <summary>The contract's expected 201 response body: a ConstituteDepositResponse with the
    /// snake_case <c>commit_sequence</c> the read-your-writes token rides on (ADR-IC-005 §P3). The
    /// consumer stub returns this so the dispatcher flips the row PUBLISHED; the provider verification
    /// asserts the REAL engine returns this same shape.</summary>
    public static string ExpectedCreatedBody(Guid depositId) =>
        $$"""{"deposit_id":"{{depositId}}","status":"ACTIVE","commit_sequence":0}""";
}
