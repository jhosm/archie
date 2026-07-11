using System.Text;
using System.Text.Json;
using Babelstone.Families.TermDeposit.Orchestration;
using Babelstone.Orchestrator.Saga;
using Xunit;

namespace Babelstone.Orchestrator.Tests;

/// <summary>
/// Pure tests of the saga's command payload DTOs
/// (<see cref="Babelstone.Orchestrator.Commands"/>). The bodies carry process id + the identity
/// trio + structural REFERENCES only — NO PII (ADR-PC-004 §P2) and NO freshly minted
/// GUID/timestamp inside the serialized body (ADR-PC-010 §P5). These lock that in as a fitness
/// function: a positive ALLOW-LIST over the serialized fields, and byte-stability across two
/// serializations of the same logical command.
/// </summary>
public sealed class ConstitutionProcessCommandsTests
{
    private static readonly Guid ProcessId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid CausationId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid CorrelationId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    // The POLYMORPHIC saga-command DTOs — these serialize the saga envelope ($type discriminator +
    // structural references). ActivateDeposit is NOT here: it serializes the engine's snake_case
    // ConstituteDepositRequest instead (bd babelstone-t7o3.11), exercised by its own dedicated tests.
    public static TheoryData<CommandPayload, string> AllCommands() => new()
    {
        { Reserve(), ConstitutionProcess.ReserveAccountBalance },
        {
            new ValidateProductLimitsCommand
            {
                ProcessId = ProcessId, CausationMessageId = CausationId, CorrelationId = CorrelationId,
                DepositRef = "DEP-2026-00012345", ProductRef = "PROD-TD-12M",
            },
            ConstitutionProcess.ValidateProductLimits
        },
        {
            new ConfirmDebitCommand
            {
                ProcessId = ProcessId, CausationMessageId = CausationId, CorrelationId = CorrelationId,
                CoreHoldRef = "CORE-HOLD-554433",
            },
            ConstitutionProcess.ConfirmDebit
        },
        {
            new ReleaseBalanceReservationCommand
            {
                ProcessId = ProcessId, CausationMessageId = CausationId, CorrelationId = CorrelationId,
                ReservationRef = "RSV-11111111",
            },
            ConstitutionProcess.ReleaseBalanceReservation
        },
        {
            new ReverseCoreDebitCommand
            {
                ProcessId = ProcessId, CausationMessageId = CausationId, CorrelationId = CorrelationId,
                CoreTxnRef = "CT-2026-9988776655",
            },
            ConstitutionProcess.ReverseCoreDebit
        },
        {
            // Scenario C clearance query (bd babelstone-t7o3.10): carries the deposit + Core hold
            // references it queries Core by — both structural, no PII.
            new QueryCoreDebitStatusCommand
            {
                ProcessId = ProcessId, CausationMessageId = CausationId, CorrelationId = CorrelationId,
                DepositRef = "DEP-2026-00012345", CoreHoldRef = "CORE-HOLD-554433",
            },
            ConstitutionProcess.QueryCoreDebitStatus
        },
    };

    [Theory]
    [MemberData(nameof(AllCommands))]
    public void CommandType_matches_the_state_machine_constant(CommandPayload command, string expectedType)
    {
        // The DTO's command type is the SAME constant the ConstitutionProcess table emits — the
        // payload and the transition table cannot drift.
        Assert.Equal(expectedType, command.CommandType);
    }

    [Theory]
    [MemberData(nameof(AllCommands))]
    public void Serialized_body_is_byte_stable_across_two_serializations(CommandPayload command, string expectedType)
    {
        // Emitting the SAME logical command twice yields IDENTICAL payload bytes (ADR-PC-010 §P5)
        // — proof there is no Guid.NewGuid / DateTimeOffset.UtcNow minted inside the body.
        var first = command.ToBytes();
        var second = command.ToBytes();
        Assert.Equal(first, second);
        // The discriminator carries the command name, so the type round-trips through the bytes.
        Assert.Contains(expectedType, Encoding.UTF8.GetString(first), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(AllCommands))]
    public void Body_is_a_positive_allow_list_of_PII_free_reference_fields(CommandPayload command, string expectedType)
    {
        _ = expectedType;

        // A POSITIVE allow-list (ADR-PC-004 §P2): every property name in the serialized body must
        // be on the known-PII-free set. A new field that is not on the list fails CLOSED — the
        // test forces a deliberate review rather than silently letting a PII-bearing field ride
        // the durable bus. (Allow-list, never a deny-list of forbidden substrings.)
        var allowedFields = new HashSet<string>(StringComparer.Ordinal)
        {
            // Base envelope references (the identity trio + process reference).
            "ProcessId", "CausationMessageId", "CorrelationId",
            // The command NAME, both as the polymorphic discriminator ($type) and the explicit
            // CommandType property — a structural type name, never PII.
            "$type", "CommandType",
            // Per-command STRUCTURAL references — opaque tokens, never identity data. The engine-CA
            // funding legs (reserve / confirm-debit) serialize account/reservation/hold references
            // snake_case on the settlement/ingress wire (bd babelstone-u79p.3; JsonPropertyName); the
            // legacy-only compensation/clearance legs (release / reverse / query) keep the PascalCase
            // forms (they never reach the engine-CA ingress). Both forms are allowed — same opaque token.
            "account_ref", "reservation_ref", "core_hold_ref",
            "AccountRef", "ReservationRef", "CoreHoldRef",
            "DepositRef", "ProductRef", "CoreTxnRef",
            // The engine-CA funding-leg extras (bd babelstone-u79p.3; ADR-PC-043 §D5 amendment (b)) —
            // all STRUCTURAL: the promoted destination account_ref (already listed), the hold-linking
            // intent reference, the integer-cents amount (the WRONG-AMOUNT guard, never a formatted
            // amount string), and the settlement-target counterparty discriminator. NEVER PII.
            "intent_reference", "amount_cents", "settlement_target",
        };

        using var document = JsonDocument.Parse(command.ToBytes());
        foreach (var property in document.RootElement.EnumerateObject())
        {
            Assert.Contains(property.Name, allowedFields);
        }
    }

    [Fact]
    public void No_field_value_carries_a_freshly_minted_GUID_or_timestamp_token()
    {
        // Every GUID-shaped value in a body must be one of the references we SET (process id,
        // causation, correlation) — never a value the serializer minted. We assert the exact
        // GUIDs present, so a stray Guid.NewGuid() in the body would appear as an unexpected one.
        using var document = JsonDocument.Parse(Reserve().ToBytes());

        Assert.Equal(ProcessId, document.RootElement.GetProperty("ProcessId").GetGuid());
        Assert.Equal(CausationId, document.RootElement.GetProperty("CausationMessageId").GetGuid());
        Assert.Equal(CorrelationId, document.RootElement.GetProperty("CorrelationId").GetGuid());

        // No timestamp-shaped field exists on the body at all — there is no created_at / minted-at
        // key to carry a wall-clock value (those live on the outbox row, not the body).
        Assert.False(document.RootElement.TryGetProperty("CreatedAt", out _));
        Assert.False(document.RootElement.TryGetProperty("EmittedAt", out _));
        Assert.False(document.RootElement.TryGetProperty("Timestamp", out _));
    }

    [Fact]
    public void The_seam_envelope_body_is_byte_stable_and_PII_free()
    {
        // The seam-level body the SagaCommandOutboxSink writes is also byte-stable (no minted
        // value) and PII-free (only references + the command name).
        var body = new SagaCommandEnvelopeBody(ConstitutionProcess.ReserveAccountBalance)
        {
            ProcessId = ProcessId, CausationMessageId = CausationId, CorrelationId = CorrelationId,
        };

        Assert.Equal(body.ToBytes(), body.ToBytes());

        using var document = JsonDocument.Parse(body.ToBytes());
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "ProcessId", "CommandType", "CausationMessageId", "CorrelationId",
        };
        foreach (var property in document.RootElement.EnumerateObject())
        {
            Assert.Contains(property.Name, allowed);
        }
        Assert.Equal(ConstitutionProcess.ReserveAccountBalance, document.RootElement.GetProperty("CommandType").GetString());
    }

    [Fact]
    public void The_seam_envelope_ToBytes_is_selected_by_dynamic_dispatch_through_a_base_reference()
    {
        // ToBytes() is virtual/override, not new-shadowed: calling it through a CommandPayload-typed
        // reference still selects the seam projection (dynamic dispatch), so a call site that does
        // not know the static type gets the SAME byte-stable bytes as the direct call.
        var body = new SagaCommandEnvelopeBody(ConstitutionProcess.ReserveAccountBalance)
        {
            ProcessId = ProcessId, CausationMessageId = CausationId, CorrelationId = CorrelationId,
        };
        CommandPayload viaBase = body;

        Assert.Equal(body.ToBytes(), viaBase.ToBytes());
    }

    [Fact]
    public void Reissued_ConfirmDebit_carries_the_SAME_CoreHoldRef_as_the_original()
    {
        // The no-double-debit invariant at v1 (bd babelstone-t7o3.10), before DEF-1's ACL guard
        // exists. A RETRY_PERMITTED reissue of ConfirmDebit out of AWAIT_CORE_CLEARANCE (a
        // DebitNotExecuted clearance) goes through the SAME factory with the SAME processId, so it
        // must present the SAME CORE-HOLD-<processId> Core-facing reference as the original confirm.
        // That stable reference is the external_reference the ACL folds into its idempotency key
        // (ADR-IC-012 §P4), so even the worst case (original silently executed, clearance wrongly
        // said not-executed) cannot double-debit — the §332 guard returns the recorded core_reference.
        // The saga's operational message_id differing per emission does NOT touch this body field.
        var reference = new SagaBusinessReference(
            ProcessId: ProcessId,
            ProductRef: "PROD-TD-12M",
            AmountMinorUnits: 100_00,
            SourceAccountRef: "ACCT-REF-0001",
            InterestAccountRef: null,
            DepositRef: "DEP-2026-00012345",
            ClientType: ClientType.Existing,
            AutoApprovalThresholdMinorUnits: 25_000_00);

        var original = (ConfirmDebitCommand)SagaCommandPayloadFactory.Build(
            ConstitutionProcess.ConfirmDebit, ProcessId, CausationId, CorrelationId, reference)!;
        // The reissue is the SAME saga (same processId) but a DIFFERENT triggering event (the
        // clearance result), so its causation differs — yet the Core-facing reference must not.
        var reissue = (ConfirmDebitCommand)SagaCommandPayloadFactory.Build(
            ConstitutionProcess.ConfirmDebit, ProcessId, Guid.NewGuid(), CorrelationId, reference)!;

        Assert.Equal(original.CoreHoldRef, reissue.CoreHoldRef);
        // And the clearance query that resolved the wait queried Core by that SAME reference, so the
        // whole indeterminate→clearance→reissue cycle pins one Core hold (deterministic, not minted).
        var clearance = (QueryCoreDebitStatusCommand)SagaCommandPayloadFactory.Build(
            ConstitutionProcess.QueryCoreDebitStatus, ProcessId, CausationId, CorrelationId, reference)!;
        Assert.Equal(original.CoreHoldRef, clearance.CoreHoldRef);
    }

    // ---- ActivateDeposit serializes the engine's ConstituteDepositRequest (bd babelstone-t7o3.11) ----

    [Fact]
    public void ActivateDeposit_body_is_a_minimal_snake_case_ConstituteDepositRequest_with_deposit_id_equal_to_process_id()
    {
        // ActivateDeposit is delivered to the engine's POST /v1/deposits, so its wire body is the
        // engine's snake_case ConstituteDepositRequest — NOT the polymorphic saga envelope. The
        // load-bearing clause: deposit_id = process_id, so the relayed DepositConstituted carries
        // ce_subject = process_id and the saga correlates the engine's real event back to itself.
        using var document = JsonDocument.Parse(Activate().ToBytes());
        var root = document.RootElement;

        Assert.Equal(ProcessId, root.GetProperty("deposit_id").GetGuid());
        Assert.Equal("dpz_pt_12m_juros_venc", root.GetProperty("product_id").GetString());
        Assert.Equal(100_00, root.GetProperty("principal_cents").GetInt64());
        Assert.Equal("acct-ref-001", root.GetProperty("funding_account").GetString());

        // The orchestrator carries NO product-family knowledge (Fork B rework, bd t7o3.11 / 3k10 / c8d8):
        // the STRUCTURAL product facts are resolved ENGINE-side from the product code, so the body must
        // NOT carry them. This is the load-bearing assertion of the rework — the orchestrator stopped
        // knowing what a product code means (the maintainer's Q2 choice, ADR-PC-009).
        Assert.False(root.TryGetProperty("role", out _));
        Assert.False(root.TryGetProperty("term_days", out _));
        Assert.False(root.TryGetProperty("start_date", out _));
        Assert.False(root.TryGetProperty("interest_variant", out _));
        Assert.False(root.TryGetProperty("auto_renewal_policy", out _));
        Assert.False(root.TryGetProperty("payment_period_months", out _));

        // The TAN is NEVER sent — the engine resolves the rate in-transaction (bd babelstone-3k10).
        Assert.False(root.TryGetProperty("tan_basis_points", out _));
        Assert.False(root.TryGetProperty("tan", out _));
        // No saga-envelope discriminator leaks onto the engine body (the engine would reject $type).
        Assert.False(root.TryGetProperty("$type", out _));
    }

    [Fact]
    public void ActivateDeposit_body_is_byte_stable_across_two_serializations()
    {
        // Byte-stable (ADR-PC-010 §P5): no clock, no minted GUID inside the body. The start date is
        // PINNED at the edge (carried as a field), not "today at the engine", so re-emit is identical.
        var command = Activate();
        Assert.Equal(command.ToBytes(), command.ToBytes());
    }

    [Fact]
    public void ActivateDeposit_body_is_a_positive_allow_list_of_PII_free_structural_fields()
    {
        // A POSITIVE allow-list (ADR-PC-004 §P2) over the ENGINE body's snake_case fields: every field
        // is a structural product fact or an opaque reference — never a raw IBAN/NIF/name. funding_account
        // is the opaque source-account TOKEN the edge pinned, not an IBAN.
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "deposit_id", "product_id", "principal_cents", "funding_account",
        };

        using var document = JsonDocument.Parse(Activate().ToBytes());
        foreach (var property in document.RootElement.EnumerateObject())
        {
            Assert.Contains(property.Name, allowed);
        }
    }

    [Fact]
    public void ActivateDeposit_assembled_by_the_factory_carries_the_pinned_business_facts_and_process_id()
    {
        // The factory builds the engine body from the pinned business reference: deposit_id = process_id,
        // and the MINIMAL facts (product code, principal, funding account) come from the reference. The
        // structural product facts are NOT pinned at the edge any more — the engine resolves them from
        // the product code (Fork B rework, bd t7o3.11 / 3k10 / c8d8, ADR-PC-009).
        var reference = new SagaBusinessReference(
            ProcessId: ProcessId,
            ProductRef: "dpz_pt_12m_juros_venc",
            AmountMinorUnits: 100_00,
            SourceAccountRef: "acct-ref-001",
            InterestAccountRef: null,
            DepositRef: "DEP-2026-00012345",
            ClientType: ClientType.Existing,
            AutoApprovalThresholdMinorUnits: 25_000_00);

        var command = (ActivateDepositCommand)SagaCommandPayloadFactory.Build(
            ConstitutionProcess.ActivateDeposit, ProcessId, CausationId, CorrelationId, reference)!;

        using var document = JsonDocument.Parse(command.ToBytes());
        var root = document.RootElement;
        Assert.Equal(ProcessId, root.GetProperty("deposit_id").GetGuid());
        Assert.Equal("dpz_pt_12m_juros_venc", root.GetProperty("product_id").GetString());
        Assert.Equal(100_00, root.GetProperty("principal_cents").GetInt64());
        Assert.Equal("acct-ref-001", root.GetProperty("funding_account").GetString());
        // The structural facts are absent — resolved engine-side, not pinned at the edge.
        Assert.False(root.TryGetProperty("start_date", out _));
        Assert.False(root.TryGetProperty("term_days", out _));
    }

    private static ReserveAccountBalanceCommand Reserve() => new()
    {
        ProcessId = ProcessId,
        CausationMessageId = CausationId,
        CorrelationId = CorrelationId,
        AccountRef = "ACCT-REF-0001",
        ReservationRef = "RSV-11111111",
    };

    private static ActivateDepositCommand Activate() => new()
    {
        ProcessId = ProcessId,
        CausationMessageId = CausationId,
        CorrelationId = CorrelationId,
        DepositRef = "DEP-2026-00012345",
        CoreTxnRef = "CT-2026-9988776655",
        ProductCode = "dpz_pt_12m_juros_venc",
        PrincipalCents = 100_00,
        FundingAccount = "acct-ref-001",
    };
}
