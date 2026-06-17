namespace Babelstone.LoadHarness;

/// <summary>
/// One event class in the §8.2 steady-state mix: its share of <c>E_year</c>, whether its projection
/// is synchronous (blocks authorization, tight latency budget) or asynchronous (lag-bounded), and
/// whether the HARNESS emits it or the ENGINE generates it (ADR-PC-011 §P1 — the ~13% engine-generated
/// + cross-mode classes are NOT produced by the harness; the engine emits them when the clock advances).
/// </summary>
/// <param name="Name">The mix-class label from the §8.2 table (e.g. "card_transactions").</param>
/// <param name="Share">Fraction of <c>E_year</c> this class contributes. The harness-emitted shares are
/// re-normalised by the generator into an emission distribution; the engine-generated shares document
/// the full mix but are produced by the engine, not the driver.</param>
/// <param name="Sync">True for sync projections (current_balance, available_credit, hold_freeze_ledger);
/// false for async (statement_cycle, withholding_ledger, regulatory_reporting, bi_analytics).</param>
/// <param name="HarnessEmitted">True if the driver puts this class on the bus; false if the engine
/// emits it internally when simulated time advances (§P1 / §8.4 "not via internal entry points").</param>
public sealed record EventMixClass(string Name, double Share, bool Sync, bool HarnessEmitted);

/// <summary>
/// The §8.2 workload SHAPE — event mix, peak envelope, throughput targets — parameterised so the
/// absolute size (the §8.1 operator-calibration numbers, still pending per Q-AK) is config, not code
/// (ADR-PC-011 Residual risk #1: "the harness is built against the §8.2 shape, parameterised so the
/// absolute size is config").
/// </summary>
/// <remarks>
/// In plain English: this is the recipe for the test traffic — what kinds of events, in what
/// proportions, and how the rate swells at lunchtime, on payday, and on the busiest day of the year.
/// The actual customer numbers (millions of accounts) plug in later; the recipe's proportions stay the
/// same whatever the size, which is exactly what ADR-PC-011 promises.
/// </remarks>
public sealed record WorkloadSpec
{
    /// <summary>The §8.2 steady-state event mix (shares sum to 1.0 across all classes).</summary>
    public required IReadOnlyList<EventMixClass> Mix { get; init; }

    /// <summary>Sustained aggregate TPS across all classes (§8.3 throughput: 250 TPS for 24h).</summary>
    public required double SustainedTps { get; init; }

    /// <summary>Burst aggregate TPS (§8.3 throughput: 1000 TPS for 15 min, no event loss).</summary>
    public required double BurstTps { get; init; }

    /// <summary>Daily-peak multiplier over average (§8.2: lunch + after-work drive 2–3× average).</summary>
    public required double DailyPeakMultiplier { get; init; }

    /// <summary>Monthly-peak multiplier (§8.2: payday morning ~10× average for 10–15 min).</summary>
    public required double MonthlyPeakMultiplier { get; init; }

    /// <summary>Annual-peak multiplier (§8.2: Black Friday / Christmas Eve drive 4–5× across the day).</summary>
    public required double AnnualPeakMultiplier { get; init; }

    /// <summary>
    /// The default v1 shape from ADR-PC-011 §8.2 / §8.3. Shares are the table's midpoints; multipliers
    /// are the upper bound of each named range (the harder shape to pass). The card/transfer classes
    /// are the harness-emitted externally-ingested ~85%; engine-generated lifecycle (~10%) and
    /// cross-mode (~3%) are documented in the mix but produced by the engine itself (§P1). Operational
    /// externals (~2%) ARE harness-emitted (bursty, coexisting with the steady stream).
    /// </summary>
    public static WorkloadSpec Default() => new()
    {
        Mix =
        [
            // Externally-ingested, sync projections — the harness emits these (§8.2 / §P1).
            new EventMixClass("card_transactions", 0.70, Sync: true, HarnessEmitted: true),
            new EventMixClass("transfers_direct_debits", 0.15, Sync: true, HarnessEmitted: true),
            // Engine-generated lifecycle, async — the ENGINE emits these when the clock advances (§P1).
            new EventMixClass("engine_lifecycle", 0.10, Sync: false, HarnessEmitted: false),
            // Cross-mode settlement, engine-internal — NOT harness-emitted (§P1).
            new EventMixClass("cross_mode", 0.03, Sync: false, HarnessEmitted: false),
            // Operational externals (AccountFrozen, FundsHeld, DepositCorrected, …) — bursty, harness-emitted.
            new EventMixClass("operational", 0.02, Sync: true, HarnessEmitted: true),
        ],
        SustainedTps = 250.0,
        BurstTps = 1000.0,
        DailyPeakMultiplier = 3.0,
        MonthlyPeakMultiplier = 10.0,
        AnnualPeakMultiplier = 5.0,
    };
}

/// <summary>
/// The §8.1 operator-calibration numbers (still pending per Q-AK; ADR-PC-011 Open Action #1). They
/// parameterise absolute size — the shape in <see cref="WorkloadSpec"/> is independent of them. Held
/// in config (version-controlled alongside the engine, §P4) so a run names the numbers it ran against.
/// </summary>
/// <param name="ActiveAccounts">N_acct — active current accounts at v4 steady state (placeholder ~3M).</param>
/// <param name="ActiveCards">N_card — active cards at v4 steady state (placeholder ~1.5M).</param>
/// <param name="AnnualEventVolume">E_year — annual event volume to the engine (placeholder 200M–600M).</param>
public sealed record Calibration(long ActiveAccounts, long ActiveCards, long AnnualEventVolume)
{
    /// <summary>The §8.1 v4 PLACEHOLDER numbers — illustrative for a midsize PT retail bank, to be
    /// replaced by the operating bank's actuals at the v1 calibration step (Open Action #1).</summary>
    public static Calibration V4Placeholder() => new(
        ActiveAccounts: 3_000_000,
        ActiveCards: 1_500_000,
        AnnualEventVolume: 400_000_000);
}
