using Babelstone.Engine;
using Babelstone.FinancialTypes;
using Babelstone.RateSheets;

namespace Babelstone.Families.TermDeposit.Application;

/// <summary>
/// The product's partial-withdrawal rules (F.12; 02 §2.4.1 / §B v1.x): the three policy gates a
/// partial early withdrawal must clear — a MINIMUM withdrawal amount, a MINIMUM remaining balance,
/// and a lock-up (<i>carência</i>) period after constitution during which no withdrawal is allowed.
/// </summary>
/// <remarks>
/// <para>
/// Pure config the bank's pricing team owns: it rides on the product config (like the day-count and
/// withholding primitives the constitution path resolves), NOT on a command input. A product that
/// permits partial withdrawals carries one of these; the decider takes it as an explicit INPUT, so
/// the decision stays deterministic and replayable (ADR-PC-010 §P5) — no clock, no I/O.
/// </para>
/// <para>
/// All three gates are inclusive-boundary "at least" / "on or after" rules (the boundary value
/// PASSES): a withdrawal exactly at the minimum amount, a remaining balance exactly at the minimum,
/// and a withdrawal exactly on the first day after the lock-up are all permitted. A degenerate policy
/// (zero minimums, zero <i>carência</i>) imposes no gate beyond the structural ones the decider always
/// applies (positive amount; cannot withdraw the whole balance — that is a termination, F.4).
/// </para>
/// </remarks>
/// <param name="MinWithdrawalCents">The smallest withdrawal the product allows, in cents. A withdrawal
/// strictly below this is refused. <c>0</c> imposes no minimum (any positive amount passes).</param>
/// <param name="MinRemainingBalanceCents">The smallest principal that may remain ON deposit after the
/// withdrawal, in cents. A withdrawal that would leave strictly less than this is refused. <c>0</c>
/// imposes no minimum-remaining floor (the decider still forbids withdrawing the whole balance —
/// reducing to exactly zero is a termination, not a partial withdrawal).</param>
/// <param name="CarenciaDays">The lock-up (<i>carência</i>) window in days from the constitution start
/// date during which no partial withdrawal is allowed. A withdrawal whose date is strictly before
/// <c>StartDate + CarenciaDays</c> is refused. <c>0</c> imposes no lock-up.</param>
public sealed record PartialWithdrawalPolicy(
    long MinWithdrawalCents,
    long MinRemainingBalanceCents,
    int CarenciaDays)
{
    /// <summary>A policy that imposes no F.12 gate — the structural rules (positive amount, cannot
    /// withdraw the whole balance) still apply. Useful for a product that permits unrestricted partial
    /// withdrawals, and as the test/degenerate baseline.</summary>
    public static PartialWithdrawalPolicy Unrestricted { get; } = new(0L, 0L, 0);

    /// <summary>
    /// Resolve the policy from a product's resolved <see cref="ProductConfig"/> (bd k6r8.8): map the
    /// three F.12 primitives the engine carries (<see cref="ProductConfig.MinWithdrawalCents"/> /
    /// <see cref="ProductConfig.MinRemainingBalanceCents"/> / <see cref="ProductConfig.CarenciaDays"/>)
    /// onto this policy. A config whose three gates are all zero — the shape of a variant that OMITS the
    /// <c>partial_withdrawal</c> block — resolves to <see cref="Unrestricted"/> (02 §2.4.1). Pure: a
    /// total function of the config, no clock and no I/O, so the constitution-boundary resolve stays
    /// deterministic (ADR-PC-008; ADR-PC-021 §D3 — the decider still takes the policy as an explicit input).
    /// </summary>
    public static PartialWithdrawalPolicy FromProductConfig(ProductConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config is { MinWithdrawalCents: 0, MinRemainingBalanceCents: 0, CarenciaDays: 0 }
            ? Unrestricted
            : new PartialWithdrawalPolicy(
                config.MinWithdrawalCents, config.MinRemainingBalanceCents, config.CarenciaDays);
    }
}

/// <summary>
/// The pure partial-withdrawal decision core (F.12, bd babelstone-k6r8.5; ADR-PC-021 §P3): given the
/// rehydrated position, a withdrawal amount, the as-of withdrawal date, and the product's
/// <see cref="PartialWithdrawalPolicy"/>, it either produces the single <see cref="DepositPartiallyWithdrawn"/>
/// event that reduces the principal, or refuses with a <see cref="DomainRejectedException"/> naming the
/// rule it broke.
/// </summary>
/// <remarks>
/// <para>
/// PURE — no clock, no I/O, no randomness (BENG001/002/003): the withdrawal date is an explicit INPUT
/// (the service derives it from the command instant), so band/lock-up evaluation is deterministic and
/// the decision replays identically. This mirrors how <c>DecideEarlyTermination</c> takes its
/// termination date as an input rather than reading a clock.
/// </para>
/// <para>
/// A partial withdrawal is a PRINCIPAL reduction only — it carries NO interest, withholding, or
/// settlement flow (02 §2.4.1: "the engine record reduces the principal"). There is therefore no
/// rate-scaling or tax math here at all; the only arithmetic is the exact integer-cent subtraction
/// <c>remaining = current − withdrawn</c> on <see cref="Money"/>, which never crosses the decimal→cents
/// rounding boundary (ADR-PC-010 §P1). Any accrued-interest consequence of a smaller principal is
/// handled by later accrual flows on the reduced base, not folded in here.
/// </para>
/// <para>
/// The lifecycle legality (a partial withdrawal is legal only from <see cref="DepositLifecycle.Active"/>)
/// is decided by the single <see cref="LifecycleTransitions"/> table — this decider consults it FIRST,
/// so a withdrawal on a Matured/closed (or not-yet-constituted) deposit is refused uniformly with every
/// other illegal transition, before any F.12 policy gate is even considered.
/// </para>
/// </remarks>
public static class PartialWithdrawalDecider
{
    /// <summary>
    /// Decide a partial withdrawal (F.12). Returns the single <see cref="DepositPartiallyWithdrawn"/>
    /// event reducing the principal, or throws <see cref="DomainRejectedException"/> naming the broken
    /// rule. Evaluation order is: lifecycle legality → positive amount → cannot withdraw the whole
    /// balance → <i>carência</i> lock-up → minimum withdrawal amount → minimum remaining balance.
    /// </summary>
    /// <param name="position">The rehydrated deposit position (its <see cref="DepositPosition.Lifecycle"/>,
    /// <see cref="DepositPosition.RemainingPrincipal"/>, and <see cref="DepositPosition.StartDate"/> drive
    /// the decision).</param>
    /// <param name="withdrawnAmount">The principal the depositor asks to take out.</param>
    /// <param name="withdrawnOn">The as-of withdrawal date — an INPUT (no clock in the decider), the
    /// date both the event records and the <i>carência</i> gate is measured against.</param>
    /// <param name="policy">The product's partial-withdrawal policy (the three F.12 gates).</param>
    /// <exception cref="DomainRejectedException">If any lifecycle, structural, or F.12 rule is broken.</exception>
    public static IReadOnlyList<DomainEvent> Decide(
        DepositPosition position,
        Money withdrawnAmount,
        DateOnly withdrawnOn,
        PartialWithdrawalPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        // 0. Lifecycle legality (F.3 table): a partial withdrawal is legal only from Active. Decided by
        //    the single transition table — not a scattered inline check — so a withdrawal on a closed or
        //    not-yet-constituted deposit is refused uniformly with every other illegal transition.
        if (!LifecycleTransitions.IsLegal(position.Lifecycle, LifecycleTransitions.Transition.PartiallyWithdraw))
        {
            throw new DomainRejectedException(
                $"Partial withdrawal on deposit {position.DepositId} is illegal from lifecycle " +
                $"{position.Lifecycle}: a partial withdrawal is legal only from Active (F.3 / F.12).");
        }

        // 0.5 Product-shape: a partial withdrawal is forbidden on an ADVANCE (juros antecipados) product
        //     (F.12, bd babelstone-emtr). ADVANCE pays the WHOLE term's interest up front at constitution
        //     on the full principal; reducing the principal later would leave the depositor holding
        //     interest on money no longer on deposit, with NO later accrual flow to re-base it (unlike
        //     AT_MATURITY/PERIODIC, whose remaining accrual folds over the reduced principal). The product
        //     shape itself is incompatible with partial withdrawal — refuse it here, the runtime backstop
        //     to the depth-4 config check that forbids declaring a partial_withdrawal block on an ADVANCE
        //     variant. Read off the pinned position (the variant resolved at constitution); no clock/I/O.
        if (position.InterestVariant == TermDepositDecider.Advance)
        {
            throw new DomainRejectedException(
                $"Partial withdrawal on deposit {position.DepositId} is not permitted: the product pays " +
                "interest in advance (ADVANCE / juros antecipados). Interest is pre-paid on the full " +
                "principal and cannot be re-based after a withdrawal, so partial withdrawal is not a legal " +
                "operation for this product shape (F.12).");
        }

        var current = position.RemainingPrincipal;

        // 1. Structural: the amount must be strictly positive — a zero/negative "withdrawal" is nonsense.
        if (withdrawnAmount.Cents <= 0)
        {
            throw new DomainRejectedException(
                $"Partial withdrawal on deposit {position.DepositId} must be a positive amount; " +
                $"got {withdrawnAmount.Cents} cents (F.12).");
        }

        // 2. Structural: cannot withdraw the WHOLE balance (or more). Reducing the principal to exactly
        //    zero — or asking for more than is on deposit — is a TERMINATION (F.4), not a partial
        //    withdrawal; route it through early termination so the penalty/settlement legs apply.
        if (withdrawnAmount.Cents >= current.Cents)
        {
            throw new DomainRejectedException(
                $"Partial withdrawal on deposit {position.DepositId} of {withdrawnAmount.Cents} cents is not " +
                $"less than the {current.Cents} cents on deposit; a full withdrawal is an early termination " +
                "(F.4), not a partial withdrawal (F.12).");
        }

        // 3. F.12 carência (lock-up): no withdrawal strictly before StartDate + CarenciaDays. The
        //    earliest permitted date is the first day on/after the lock-up window closes; the boundary
        //    day itself passes (inclusive). Date arithmetic on the INPUT date — no clock.
        var unlockDate = position.StartDate.AddDays(policy.CarenciaDays);
        if (withdrawnOn < unlockDate)
        {
            throw new DomainRejectedException(
                $"Partial withdrawal on deposit {position.DepositId} on {withdrawnOn:yyyy-MM-dd} falls inside the " +
                $"{policy.CarenciaDays}-day carência (lock-up) period; the earliest permitted date is " +
                $"{unlockDate:yyyy-MM-dd} (F.12).");
        }

        // 4. F.12 minimum withdrawal amount: at least MinWithdrawalCents (the boundary value passes).
        if (withdrawnAmount.Cents < policy.MinWithdrawalCents)
        {
            throw new DomainRejectedException(
                $"Partial withdrawal on deposit {position.DepositId} of {withdrawnAmount.Cents} cents is below the " +
                $"minimum withdrawal amount of {policy.MinWithdrawalCents} cents (F.12).");
        }

        // 5. F.12 minimum remaining balance: the principal left ON deposit after the withdrawal must be
        //    at least MinRemainingBalanceCents (the boundary value passes). Exact integer-cent
        //    subtraction (ADR-PC-010 §P1) — no rounding boundary, no interest math.
        var remaining = current - withdrawnAmount;
        if (remaining.Cents < policy.MinRemainingBalanceCents)
        {
            throw new DomainRejectedException(
                $"Partial withdrawal on deposit {position.DepositId} would leave {remaining.Cents} cents on deposit, " +
                $"below the minimum remaining balance of {policy.MinRemainingBalanceCents} cents (F.12).");
        }

        // All gates cleared: reduce the principal. The event carries the already-computed remaining
        // principal; the fold (DepositPartiallyWithdrawnHandler) records it — no arithmetic in the fold.
        return [new DepositPartiallyWithdrawn(position.DepositId, withdrawnAmount, remaining, withdrawnOn)];
    }
}
