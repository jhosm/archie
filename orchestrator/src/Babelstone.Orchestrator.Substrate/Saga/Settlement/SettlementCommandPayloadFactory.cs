namespace Babelstone.Orchestrator.Saga.Settlement;

/// <summary>
/// Assembles the typed, byte-stable command payloads the substrate-owned <see cref="SettlementProcess"/>
/// saga emits, from the saga's process id and identity trio alone (ADR-PC-032; modelled on the
/// constitution/renewal factories). Every account/hold/credit reference is a DETERMINISTIC namespacing of
/// the process id — never a freshly minted GUID and never a wall clock — so re-emitting the same logical
/// command yields byte-identical bytes (a crash-recovery reissue is replayable; the ACL dedups on the stable
/// reference).
/// </summary>
/// <remarks>
/// <para>
/// <b>Family-agnostic and PII-free (the narrowed ORCH-2 allow-list; ADR-PC-004 §P2).</b> Every reference the
/// factory produces is a process-id-derived, opaque token — never a deposit/loan-typed shape, never a raw
/// IBAN/NIF/name. The factory names no family, exactly as the substrate-owned saga it serves does not.
/// </para>
/// <para>
/// <b>The opaque <c>account_ref</c> seam.</b> ADR-PC-032 carries the real <c>Movement.AccountRef</c> as an
/// opaque reference; the engine relay promotes it (alongside <c>movementdirections</c>) to a CloudEvents
/// extension header on the carrying event, which the saga's start path reads (ADR-IC-018 §D5 — headers, never
/// the payload). At the PLATFORM layer this issue builds (the saga + the settlement command surface), the
/// command body uses the process-id-derived reference as the account/hold/credit token; the wiring that
/// threads the promoted opaque <c>account_ref</c> onto the body lands with each consuming family's
/// Movement migration (bd babelstone-t7o3.13 / t7o3.16, which this saga BLOCKS) — the same staged shape the
/// renewal sink took (minimal body now, the engine/ACL resolves the rest). No PII either way.
/// </para>
/// </remarks>
public static class SettlementCommandPayloadFactory
{
    // The derived-reference prefixes + the derivation live in the ONE shared SettlementReferences home
    // (feature-design §8/§10, the rule-of-three cleanup bd babelstone-t7o3.18) — so the substrate settlement
    // leg and a family's embedded debit leg derive the IDENTICAL external_reference for the same process id
    // (the cross-saga no-double-debit invariant is structural, not a pair of literals that agree). NOT minted.

    /// <summary>
    /// Build the full typed payload for <paramref name="commandType"/>, or null if there is no recipe for it
    /// (the caller surfaces that as a fail-closed wiring error). PURE and byte-stable: no clock, no GUID
    /// minting (ADR-PC-010 §P5) — every reference is a deterministic function of the process id AND, for the
    /// engine-CA settlement legs (the reserve, the confirm-debit, and the confirm-credit), the economic-intent
    /// id + promoted destination in <paramref name="intent"/>.
    /// </summary>
    /// <param name="commandType">The command NAME the state machine decided (a
    /// <see cref="SettlementProcess"/> command-name constant).</param>
    /// <param name="processId">The saga instance the command belongs to.</param>
    /// <param name="causationMessageId">The triggering event's message id (the §P7 causation reference) — a
    /// pre-existing id carried through, never minted here.</param>
    /// <param name="correlationId">The originating request's correlation reference, carried unchanged.</param>
    /// <param name="intent">The ADR-PC-043 slot-4 economic intent — the exactly-once <c>IntentId</c> the
    /// engine-CA settlement legs' <c>command_id</c> derives from (NOT the HTTP Idempotency-Key), the promoted
    /// destination <c>account_ref</c>, and the source <c>Movement.Amount</c> (integer cents) the CA writer
    /// lands. Present, it also flips each leg's <c>settlement_target</c> to <c>engine-ca</c> so the dispatcher
    /// routes to the engine-owned CA ingress. <c>null</c> for the legacy-DDA path (and for the platform-layer
    /// default before a family threads the promoted intent) — the legs then fall back to the process-id-derived
    /// reference + the <c>ACCT-{processId}</c> placeholder (unchanged), carry no amount, and route legacy. See
    /// the source→destination threading note in <see cref="SettlementIntent"/>.</param>
    public static SettlementCommandPayload? Build(
        string commandType,
        Guid processId,
        Guid causationMessageId,
        Guid? correlationId,
        SettlementIntent? intent = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandType);

        // ADR-PC-043 slot 4: the engine-CA confirm legs' idempotency reference is derived from the body's
        // economic-INTENT id, so a saga reissue with a fresh dispatch message_id collapses to ONE append. When
        // no intent is threaded (legacy-DDA, or the pre-threading platform default) the reference stays the
        // process-id derivation, byte-identical to before — legacy routing is UNCHANGED.
        var confirmDebitRef = intent is { } di
            ? SettlementReferences.DeriveFromIntent(SettlementReferences.CoreHoldPrefix, di.IntentId)
            : SettlementReferences.Derive(SettlementReferences.CoreHoldPrefix, processId);
        var confirmCreditRef = intent is { } ci
            ? SettlementReferences.DeriveFromIntent(SettlementReferences.CreditPrefix, ci.IntentId)
            : SettlementReferences.Derive(SettlementReferences.CreditPrefix, processId);
        var amountCents = intent?.AmountCents ?? 0L;

        // The destination account_ref (ADR-PC-043). On an engine-CA
        // leg the source family promotes the customer's REAL persistent conta-à-ordem account_ref onto the
        // intent, and the substrate FORWARDS it UNTOUCHED here as the credit/debit destination the CA writer
        // lands on — never re-derived. When no account_ref is threaded (the legacy-DDA path, or the
        // platform-layer default before a family promotes it) it falls back to the process-id-derived
        // ACCT-{processId} placeholder, byte-identical to before — the legacy core resolves the account from
        // the process-scoped business reference. Routing stays header-only: this value is a DESTINATION the
        // engine-CA WRITER reads, NOT a routing input the substrate router reads (ADR-IC-018).
        var accountRef = string.IsNullOrWhiteSpace(intent?.AccountRef)
            ? SettlementReferences.Derive(SettlementReferences.AccountPrefix, processId)
            : intent!.AccountRef!;

        // The reserve leg's reservation reference — ALSO the hold-linking intent reference the confirm leg
        // carries, so the engine-CA ingress captures exactly the hold the reserve's authorize placed
        // (deterministic, process-id-derived; ADR-PC-010, no mint/clock).
        var reservationRef = SettlementReferences.Derive(SettlementReferences.ReservationPrefix, processId);

        // ADR-PC-043 slot 1: a threaded engine-CA intent (a promoted destination account_ref is present) routes
        // the leg to the engine-owned CA ingress — the dispatcher's ProjectSettlementTargetHeader reads this
        // body field. Without an intent (the legacy-DDA path / no promotion) it stays null, so the router keeps
        // legacy routing UNCHANGED. The SAME target rides BOTH debit legs (reserve + confirm) so the hold the
        // reserve places and the hold the confirm captures are on the SAME counterparty (bd babelstone-u79p.22).
        var settlementTarget = intent is not null ? SettlementCommandRouter.EngineCaValue : null;

        return commandType switch
        {
            SettlementProcess.ReserveAccountBalance => new ReserveAccountBalanceCommand
            {
                ProcessId = processId,
                CausationMessageId = causationMessageId,
                CorrelationId = correlationId,
                AccountRef = accountRef,
                ReservationRef = reservationRef,
                // The shared hold-linking key the engine-CA ingress derives the authorize hold from — the
                // reserve and confirm legs carry the SAME value, so capture targets exactly the placed hold.
                IntentReference = reservationRef,
                // The promoted hold amount (bd babelstone-u79p.22) — null on the legacy path, so the reserve
                // body is byte-identical to before there; the engine-CA authorize ingress requires it positive.
                AmountCents = intent?.AmountCents,
                SettlementTarget = settlementTarget,
            },
            SettlementProcess.ConfirmDebit => new ConfirmDebitCommand
            {
                ProcessId = processId,
                CausationMessageId = causationMessageId,
                CorrelationId = correlationId,
                CoreHoldRef = confirmDebitRef,
                AccountRef = accountRef,
                IntentReference = reservationRef,
                AmountCents = amountCents,
                // Forward-propagated across the reserve→confirm hop (bd babelstone-u79p.22): the dispatcher
                // re-emits the reserve row's promoted destination onto the synthesized BalanceReserved event, so
                // this LATER-advance confirm re-threads the SAME intent and routes to the SAME engine-CA ingress.
                SettlementTarget = settlementTarget,
            },
            SettlementProcess.ConfirmCredit => new ConfirmCreditCommand
            {
                ProcessId = processId,
                CausationMessageId = causationMessageId,
                CorrelationId = correlationId,
                AccountRef = accountRef,
                CreditRef = confirmCreditRef,
                IntentReference = confirmCreditRef,
                AmountCents = amountCents,
                SettlementTarget = settlementTarget,
            },
            SettlementProcess.QueryCoreDebitStatus => new QueryCoreDebitStatusCommand
            {
                ProcessId = processId,
                CausationMessageId = causationMessageId,
                CorrelationId = correlationId,
                // The SAME reference the indeterminate ConfirmDebit used (the intent-derived one on the CA
                // path, the process-id one on the legacy path) — deterministic, not minted — so the clearance
                // query resolves exactly that in-flight operation and the retry never double-moves.
                CoreHoldRef = confirmDebitRef,
            },
            SettlementProcess.QueryCoreCreditStatus => new QueryCoreCreditStatusCommand
            {
                ProcessId = processId,
                CausationMessageId = causationMessageId,
                CorrelationId = correlationId,
                CreditRef = confirmCreditRef,
            },
            _ => null,
        };
    }
}

/// <summary>
/// The ADR-PC-043 slot-4 economic intent that pins a settlement leg's exactly-once identity — the
/// <c>IntentId = f(source_id, occurrence)</c> the engine-CA confirm legs' append <c>command_id</c> derives
/// from, plus the source <c>Movement.Amount</c> the CA writer lands (integer cents, the in-band
/// <c>WRONG-AMOUNT</c> guard). In plain English: it names WHICH economic payout a settlement command effects
/// and HOW MUCH, so a reissue or a re-route of that payout collapses to exactly one landing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Source→destination threading (ADR-PC-043 §Idempotency).</b> The intent id originates at the SOURCE
/// family (the deposit / loan) as <c>SettlementReferences.DeriveIntentId(source_id, occurrence)</c> — the
/// same stable occurrence key the source-family payout <c>LifecycleCommandKey</c> uses (ADR-PC-036), so it
/// is deterministic across reissues. The source family is EXPECTED to promote it (with the target header,
/// STEP B) onto the Movement-bearing event (the source-promotion + intent-threading wiring lands with bd
/// babelstone-98mj.3/.4 — this slice is derivation-only); once threaded, the substrate carries it UNTOUCHED
/// here and derives the CA-apply reference from it — NEVER from a fresh value and NEVER from the HTTP
/// Idempotency-Key. The derivation half IS wired and pinned: given an intent, resolution/retry keys derive
/// from the SAME intent id via <see cref="SettlementReferences.DeriveResolutionIntentId"/>, so a late
/// original apply and an operator re-target collapse to one landing by construction.
/// </para>
/// <para>
/// <b>Structural, PII-free (ADR-PC-004 §P2).</b> The intent id is an opaque token; the amount rides as
/// integer cents (a reference to a value, never a raw amount-bearing identity). The substrate does not
/// reference the engine's <c>Money</c> type (ADR-PC-019 §P2) — the receiver re-hydrates it.
/// </para>
/// </remarks>
/// <param name="IntentId">The economic-intent id from
/// <see cref="SettlementReferences.DeriveIntentId"/> — the per-payout exactly-once key.</param>
/// <param name="AmountCents">The source <c>Movement.Amount</c> in integer cents the CA writer lands.</param>
/// <param name="AccountRef">The promoted DESTINATION account_ref (ADR-PC-043) — the customer's REAL
/// persistent conta-à-ordem account_ref the engine-CA credit/debit
/// writer lands the value on. The source family promotes it; the substrate FORWARDS it untouched onto the
/// command body (never re-derived). <c>null</c> for the legacy-DDA path and for the platform-layer default
/// before a family threads it — the factory then falls back to the process-id-derived <c>ACCT-{processId}</c>
/// placeholder (byte-identical to before). Orthogonal to <paramref name="IntentId"/>: two payouts on ONE
/// account carry the SAME <c>AccountRef</c> while their intent ids differ (the account-identity axis vs the
/// exactly-once axis). Structural, PII-free (ADR-PC-004) — an opaque token, never a raw IBAN.</param>
public sealed record SettlementIntent(string IntentId, long AmountCents, string? AccountRef = null);
