using Babelstone.Orchestrator.Inbox;
using Babelstone.Orchestrator.Saga.Settlement;
using Xunit;

namespace Babelstone.Families.PersonalLoan.Orchestration.Tests;

/// <summary>
/// The loan-installment end-to-end walk through the settlement saga, Docker-free (bd babelstone-9z9w;
/// ADR-PC-032 §A9/§A10 Revised 2026-07-04). In plain English: with the loan family's topics now feeding the
/// settlement machinery, each installment collection must get its OWN settlement saga — so installment N can
/// sit PARKED awaiting an operator while installment N+1's event arrives and drives its own instance, which
/// is exactly the shape the lifecycle driver's LCD-2 gate reads ("hold N+1 while N is parked"). This drives
/// the REAL pure pieces end-to-end — the fan-out projector (per-occurrence identity), the
/// <see cref="SettlementProcess"/> substitutor (direction branch off the headers), and its transition table —
/// with no substrate persistence, so it runs in the Docker-free lane.
/// </summary>
public sealed class LoanInstallmentSettlementWalkTests
{
    private static readonly Guid Loan = Guid.Parse("10a70000-0000-0000-0000-00000000f00d");

    private static SagaInboxEvent InstallmentCollected(int installment) => new(
        // Each installment's LoanInstallmentPaid is its OWN event (its own ce_id) on the SAME loan subject
        // — the recurring shape whose second occurrence used to have no settlement instance at all.
        MessageId: Guid.Parse($"aaaaaaaa-0000-0000-0000-{installment:d12}"),
        ProcessId: Loan,
        EventType: "LoanInstallmentPaid",
        SourceTopic: "personal_loan",
        CorrelationId: null,
        ExtensionHeaders: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // The engine-spine producer's promoted headers (MovementHeaders): an installment collection is
            // an Originated DEBIT on the borrower's collection account.
            ["movementorigin"] = "Originated",
            ["movementdirections"] = "Debit",
        });

    [Fact]
    public async Task Occurrence_N_parked_while_N_plus_1_arrives_is_representable_as_two_independent_sagas()
    {
        var machine = new SettlementProcess();

        // Installment 1's event projects to its own per-occurrence settlement instance...
        var occurrence1 = Assert.Single(
            SettlementSagaModule.FanOutByMovementDirection(InstallmentCollected(1)));
        // ...whose reserve leg is REFUSED (insufficient balance) — the saga parks fail-closed in
        // HUMAN_INTERVENTION_REQUIRED (ADR-IC-003 §P6: no hold exists, nothing to compensate).
        var state1 = await StartAsync(machine, occurrence1);
        Assert.Equal(SettlementProcess.States.Reserving, state1);
        Assert.True(machine.TryAdvance(state1, SettlementProcess.ReserveRefused, out var parked));
        Assert.Equal(SettlementProcess.States.HumanInterventionRequired, parked.Next);
        // Parked is a first-class WAIT, not a dead end: non-terminal by table (the operator edge exists).
        Assert.False(machine.IsTerminal(parked.Next));

        // Installment 2's event ARRIVES WHILE 1 IS PARKED: it projects to a DIFFERENT instance on the SAME
        // subject — the per-occurrence identity that makes "N parked, N+1 in flight" representable at all
        // (one shared ce_subject-keyed saga could hold only one of the two states).
        var occurrence2 = Assert.Single(
            SettlementSagaModule.FanOutByMovementDirection(InstallmentCollected(2)));
        Assert.NotEqual(occurrence1.ProcessId, occurrence2.ProcessId);
        Assert.Equal(occurrence1.SubjectId, occurrence2.SubjectId); // both hang off the loan (subject_id)

        // Installment 2's own saga walks the debit happy path to ITS terminal, untouched by 1's park.
        var state2 = await StartAsync(machine, occurrence2);
        Assert.True(machine.TryAdvance(state2, SettlementProcess.BalanceReserved, out var confirming));
        Assert.True(machine.TryAdvance(confirming.Next, SettlementProcess.DebitConfirmed, out var done));
        Assert.Equal(SettlementProcess.States.SettlementCompleted, done.Next);
        Assert.True(machine.IsTerminal(done.Next));

        // And installment 1's park resolves independently (the operator edge) — order-free of 2's completion.
        Assert.True(machine.TryAdvance(parked.Next, SettlementProcess.OperatorResolved, out var resolved));
        Assert.Equal(SettlementProcess.States.SettlementCompleted, resolved.Next);
    }

    [Fact]
    public async Task Each_installment_presents_its_own_acl_idempotency_tokens()
    {
        // The ACL's external_reference derives from the saga's process id (ADR-IC-012 §P4;
        // SettlementReferences) — per-occurrence process ids therefore yield per-occurrence debit tokens:
        // installment 2's ConfirmDebit can NEVER dedup against installment 1's (the design's point), while
        // a redelivery of the SAME installment re-derives the SAME token (the retry stays safe).
        var occurrence1 = Assert.Single(
            SettlementSagaModule.FanOutByMovementDirection(InstallmentCollected(1)));
        var occurrence2 = Assert.Single(
            SettlementSagaModule.FanOutByMovementDirection(InstallmentCollected(2)));
        var redelivery1 = Assert.Single(
            SettlementSagaModule.FanOutByMovementDirection(InstallmentCollected(1)));

        string HoldRef(SagaInboxEvent leg) =>
            SettlementReferences.Derive(SettlementReferences.CoreHoldPrefix, leg.ProcessId);

        Assert.NotEqual(HoldRef(occurrence1), HoldRef(occurrence2));
        Assert.Equal(HoldRef(occurrence1), HoldRef(redelivery1));

        // The commands the two occurrences' sagas decide are the same debit-path vocabulary — it is the
        // per-occurrence process id (hence the derived references), not the command names, that separates
        // the two cash legs.
        var machine = new SettlementProcess();
        var state = await StartAsync(machine, occurrence1);
        Assert.True(machine.TryAdvance(state, SettlementProcess.BalanceReserved, out var confirming));
        Assert.Equal(SettlementProcess.ConfirmDebit, Assert.Single(confirming.Commands));
    }

    [Fact]
    public async Task A_disbursement_takes_the_confirmation_gated_credit_branch()
    {
        // The loan's OTHER Movement-bearing leg (bd babelstone-9z9w names both): LoanDisbursed is an
        // Originated CREDIT (the lump sum enters the borrower's account) — confirmation-gated only, no
        // reserve leg (ADR-PC-016 slot 5).
        var disbursed = new SagaInboxEvent(
            MessageId: Guid.NewGuid(),
            ProcessId: Loan,
            EventType: "LoanDisbursed",
            SourceTopic: "personal_loan",
            CorrelationId: null,
            ExtensionHeaders: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["movementorigin"] = "Originated",
                ["movementdirections"] = "Credit",
            });

        var machine = new SettlementProcess();
        var leg = Assert.Single(SettlementSagaModule.FanOutByMovementDirection(disbursed));
        Assert.Equal(Loan, leg.SubjectId);

        var effective = await machine.SubstituteAsync(
            SettlementProcess.States.SettlementStarted, SettlementProcess.MovementOriginated,
            transitionLog: null!, connection: null!, transaction: null!,
            processId: leg.ProcessId, extensionHeaders: leg.ExtensionHeaders, ct: default);
        Assert.Equal(SettlementProcess.CreditMovementOriginated, effective);

        Assert.True(machine.TryAdvance(
            SettlementProcess.States.SettlementStarted, effective, out var confirming));
        Assert.Equal(SettlementProcess.States.ConfirmingCredit, confirming.Next);
        Assert.Equal(SettlementProcess.ConfirmCredit, Assert.Single(confirming.Commands));
    }

    /// <summary>Auto-start one projected occurrence leg through the machine's REAL substitutor (the
    /// generic MovementOriginated marker resolved to the leg's single-direction branch off its headers) and
    /// take the first table edge — the same substitute-then-advance the substrate handler performs.</summary>
    private static async Task<string> StartAsync(SettlementProcess machine, SagaInboxEvent leg)
    {
        var effective = await machine.SubstituteAsync(
            SettlementProcess.States.SettlementStarted, SettlementProcess.MovementOriginated,
            transitionLog: null!, connection: null!, transaction: null!,
            processId: leg.ProcessId, extensionHeaders: leg.ExtensionHeaders, ct: default);
        Assert.Equal(SettlementProcess.DebitMovementOriginated, effective);

        Assert.True(machine.TryAdvance(SettlementProcess.States.SettlementStarted, effective, out var outcome));
        Assert.Equal(SettlementProcess.ReserveAccountBalance, Assert.Single(outcome.Commands));
        return outcome.Next;
    }
}
