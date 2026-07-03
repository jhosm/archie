using Babelstone.Engine.Hosting;
using Babelstone.Families.TermDeposit.Application;
using Babelstone.Orchestrator.Edge;
using Babelstone.RateSheets;
using Babelstone.RateSheets.Api;
using Xunit;

namespace Babelstone.ContractDrift.Tests;

/// <summary>
/// LAYER 1 of the spec&lt;-&gt;code drift guard: for every documented surface, the committed
/// OpenAPI schema and the shipped C# DTO must agree on the wire — same property-NAME set
/// (System.Text.Json semantics: [JsonPropertyName] else SnakeCaseLower), same scalar types,
/// and the same required set (see <see cref="WireShape"/> for the requiredness convention
/// and its assumed-not-observed naming-policy disclosure). The specs are hand-authored, so
/// this suite is what stops one lying about the code: mutating a DTO — add / rename /
/// re-require a field — without updating its spec turns the build RED and names the exact
/// mismatch. It is NOT Pact: the Pact-style CDC (EngineCommandContract) checks
/// consumer&lt;-&gt;provider compatibility; this checks spec&lt;-&gt;code honesty —
/// complementary axes, both kept (Layer 3 cross-checks them).
/// </summary>
public sealed class Layer1SpecDtoStructuralTests
{
    /// <summary>How the required set is asserted for a case.</summary>
    public enum Mode
    {
        /// <summary>A response body: required = the DTO's non-nullable properties.</summary>
        Response,

        /// <summary>A request body: required = non-nullable properties without a constructor default.</summary>
        Request,

        /// <summary>
        /// The public-edge request: property names/types are asserted here, but the REQUIRED
        /// set's source of truth is the Kong pre-function, and DTO nullability cannot stand
        /// in for it — the DTO mixes nullable strings with a non-nullable <c>amount</c>, so
        /// the reflection convention would derive a required set that matches neither the
        /// edge's enforcement nor the spec. Layer 2 owns the required-set assertion.
        /// </summary>
        EdgeRequest,
    }

    // One Layer-1 case per documented surface (the coverage suite proves this list is
    // exhaustive over the committed specs' operations).
    public static TheoryData<string, string, Type, Mode> Cases() => new()
    {
        // ── specs/ (public catalogue) ─────────────────────────────────────────────────────
        { "contracts/openapi/specs/engine-reads.openapi.yaml", "Deposit", typeof(DepositResponse), Mode.Response },
        { "contracts/openapi/specs/engine-reads.openapi.yaml", "DepositMaturities", typeof(DepositMaturitiesResponse), Mode.Response },
        { "contracts/openapi/specs/orchestrator-edge.openapi.yaml", "ConstituteRequest", typeof(ConstituteRequest), Mode.EdgeRequest },
        { "contracts/openapi/specs/orchestrator-edge.openapi.yaml", "ConstituteAccepted", typeof(ConstituteResponse), Mode.Response },
        { "contracts/openapi/specs/orchestrator-process-status.openapi.yaml", "ProcessStatus", typeof(ProcessStatusResponse), Mode.Response },

        // ── internal/ (the never-public catalogue) ────────────────────────────────────────
        { "contracts/openapi/internal/engine-deposit-commands.openapi.yaml", "ConstituteDepositRequest", typeof(ConstituteDepositRequest), Mode.Request },
        { "contracts/openapi/internal/engine-deposit-commands.openapi.yaml", "ConstituteDepositResponse", typeof(ConstituteDepositResponse), Mode.Response },
        { "contracts/openapi/internal/engine-deposit-commands.openapi.yaml", "MatureDepositRequest", typeof(MatureDepositRequest), Mode.Request },
        { "contracts/openapi/internal/engine-deposit-commands.openapi.yaml", "PayInterestRequest", typeof(PayInterestRequest), Mode.Request },
        { "contracts/openapi/internal/engine-deposit-commands.openapi.yaml", "TerminateEarlyRequest", typeof(TerminateEarlyRequest), Mode.Request },
        { "contracts/openapi/internal/engine-deposit-commands.openapi.yaml", "TerminateEarlyResponse", typeof(TerminateEarlyResponse), Mode.Response },
        { "contracts/openapi/internal/engine-deposit-commands.openapi.yaml", "PartialWithdrawRequest", typeof(PartialWithdrawRequest), Mode.Request },
        { "contracts/openapi/internal/engine-deposit-commands.openapi.yaml", "ErasePersonalDataRequest", typeof(ErasePersonalDataRequest), Mode.Request },
        { "contracts/openapi/internal/engine-deposit-commands.openapi.yaml", "ErasePersonalDataResponse", typeof(ErasePersonalDataResponse), Mode.Response },
        { "contracts/openapi/internal/engine-deposit-commands.openapi.yaml", "CorrectDepositRequest", typeof(CorrectDepositRequest), Mode.Request },
        { "contracts/openapi/internal/engine-deposit-commands.openapi.yaml", "CorrectDepositResponse", typeof(CorrectDepositResponse), Mode.Response },
        { "contracts/openapi/internal/engine-deposit-commands.openapi.yaml", "ConstituteRenewalRequest", typeof(ConstituteRenewalRequest), Mode.Request },
        { "contracts/openapi/internal/engine-deposit-commands.openapi.yaml", "ConstituteRenewalResponse", typeof(ConstituteRenewalResponse), Mode.Response },
        { "contracts/openapi/internal/engine-deposit-commands.openapi.yaml", "LinkRenewalRequest", typeof(LinkRenewalRequest), Mode.Request },
        { "contracts/openapi/internal/engine-deposit-commands.openapi.yaml", "LinkRenewalResponse", typeof(LinkRenewalResponse), Mode.Response },
        { "contracts/openapi/internal/engine-deposit-commands.openapi.yaml", "Deposit", typeof(DepositResponse), Mode.Response },
        { "contracts/openapi/internal/engine-rate-sheets.openapi.yaml", "RateSheetDeployRequest", typeof(RateSheetDeployRequest), Mode.Request },
        { "contracts/openapi/internal/engine-rate-sheets.openapi.yaml", "RateSheetResponse", typeof(RateSheetResponse), Mode.Response },
        { "contracts/openapi/internal/engine-rate-sheets.openapi.yaml", "RateSheetConflict", typeof(RateSheetConflict), Mode.Response },
        { "contracts/openapi/internal/engine-rate-sheets.openapi.yaml", "RoleRates", typeof(RoleRates), Mode.Response },
        { "contracts/openapi/internal/engine-pack-migrations.openapi.yaml", "PackMigrationRequest", typeof(PackMigrationRequest), Mode.Request },
        { "contracts/openapi/internal/engine-pack-migrations.openapi.yaml", "InstanceFilter", typeof(InstanceFilter), Mode.Request },
        { "contracts/openapi/internal/engine-pack-migrations.openapi.yaml", "PackMigrationResponse", typeof(PackMigrationResponse), Mode.Response },
        { "contracts/openapi/internal/engine-withholding-reads.openapi.yaml", "DepositWithholdingStatements", typeof(DepositWithholdingStatementsResponse), Mode.Response },
        { "contracts/openapi/internal/engine-withholding-reads.openapi.yaml", "WithholdingLedger", typeof(DepositWithholdingLedgerResponse), Mode.Response },
        { "contracts/openapi/internal/engine-withholding-reads.openapi.yaml", "WithholdingLedgerEntry", typeof(WithholdingLedgerEntryResponse), Mode.Response },
        { "contracts/openapi/internal/engine-withholding-reads.openapi.yaml", "Deposit", typeof(DepositResponse), Mode.Response },

        // NOT here, deliberately:
        //  * RateBand — its wire shape is OWNED by RateBandJsonConverter (the [lower, upper]
        //    principal_cents array), so reflection over From/To/TanBasisPoints would assert
        //    the WRONG shape; the spec documents the converter's form verbatim.
        //  * engine-sor-ops InstanceOperationRequest/Outcome — a PROVISIONAL (0.x) envelope
        //    with no shipped handler DTO yet; Layer0ProvisionalGuard below pins the exemption.
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Spec_schema_matches_dto_wire_shape(string specPath, string schemaName, Type dto, Mode mode)
    {
        var schema = OpenApiSpec.Load(specPath).Schema(schemaName);
        var wire = mode == Mode.Response ? WireShape.OfResponse(dto) : WireShape.OfRequest(dto);
        var context = $"{specPath} :: {schemaName} <-> {dto.Name}";

        // Property-NAME set equality: an added / renamed / removed DTO property without a
        // spec edit fails HERE, naming the drifted field(s).
        var specNames = schema.Properties.Keys.OrderBy(n => n, StringComparer.Ordinal).ToArray();
        var wireNames = wire.Properties.Keys.OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.True(specNames.SequenceEqual(wireNames, StringComparer.Ordinal),
            $"{context}: property sets differ — spec-only [{string.Join(", ", specNames.Except(wireNames))}], "
            + $"dto-only [{string.Join(", ", wireNames.Except(specNames))}]");

        // Scalar-type agreement (a $ref counts as object; an untyped oneOf is unasserted).
        foreach (var (name, specProperty) in schema.Properties)
        {
            if (specProperty.Type is null)
            {
                continue;
            }

            Assert.True(string.Equals(specProperty.Type, wire.Properties[name].Type, StringComparison.Ordinal),
                $"{context}: '{name}' — spec says type {specProperty.Type}, the DTO serializes {wire.Properties[name].Type}");
        }

        // Required-set equality (see the Mode docs for who owns the edge request's set).
        if (mode != Mode.EdgeRequest)
        {
            var specRequired = schema.Required.OrderBy(n => n, StringComparer.Ordinal).ToArray();
            var wireRequired = wire.Required.OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.True(specRequired.SequenceEqual(wireRequired, StringComparer.Ordinal),
                $"{context}: required sets differ — spec-only [{string.Join(", ", specRequired.Except(wireRequired))}], "
                + $"dto-only [{string.Join(", ", wireRequired.Except(specRequired))}]");
        }
    }

    /// <summary>
    /// The engine-sor-ops envelope is PROVISIONAL (SemVer 0.x — the gateway-keyed /operations
    /// handler is not shipped, so there is no DTO to bind; the shape is modelled). This guard
    /// pins that exemption to the version: promoting the spec to 1.0.0 MUST add the Layer-1
    /// cases binding InstanceOperationRequest/Outcome to the shipped handler DTOs (and settle
    /// the documented instance_id-vs-deposit_id field-name seam) — this test failing is the
    /// reminder.
    /// </summary>
    [Fact]
    public void Sor_ops_stays_layer1_exempt_only_while_provisional()
    {
        var version = OpenApiSpec.Load("contracts/openapi/specs/engine-sor-ops.openapi.yaml").InfoVersion;
        Assert.True(version.StartsWith("0.", StringComparison.Ordinal),
            "engine-sor-ops is no longer 0.x: bind InstanceOperationRequest/InstanceOperationOutcome to the "
            + "shipped /operations handler DTOs with Layer-1 cases (and reconcile the instance_id vs "
            + "deposit_id spelling) before promoting — the 0.x version was the exemption's whole basis.");
    }
}
