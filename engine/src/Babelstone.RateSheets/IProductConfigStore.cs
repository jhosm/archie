namespace Babelstone.RateSheets;

/// <summary>
/// In plain English: a product code like <c>dpz_pt_12m_juros_venc</c> stands for a specific deposit
/// SHAPE — how long the term is, when interest is paid, whether it auto-renews, the coupon cadence,
/// and the pricing role. This store turns a product code into those structural facts at the engine,
/// so the engine is the single place that knows what a product code means. The orchestrator carries
/// no product-family knowledge; it sends only the product code and lets the engine look the rest up.
/// </summary>
/// <remarks>
/// <para>
/// <b>The engine is the constitution authority (the maintainer's Q2 choice; resolution-in-transaction
/// is ADR-PC-008 §S2).</b> Where the rejected v1 stand-in pinned the structural facts at the
/// orchestrator edge and shipped them on the wire, the engine now resolves them itself — alongside the
/// rate-sheet resolve, in the SAME constitution transaction (the ADR-PC-008 §S2 in-transaction
/// property). The resolved facts are the SHAPE only (term, variant, renewal policy, coupon cadence,
/// role); the TAN is a separate rate-sheet resolve (ADR-PC-008 §P3), and the day-count / withholding
/// primitives are pack-resolved. This store NEVER carries a price.
/// </para>
/// <para>
/// <b>Family-agnostic seam (ADR-PC-021 §D2/§P2).</b> This interface lives in the generic
/// <c>Babelstone.RateSheets</c> spine, next to <see cref="IRateSheetStore"/>. A family decider
/// (e.g. <c>TermDepositConstitutionService</c>) consumes it — that is the family→spine arrow, which
/// is allowed. The spine never references a family, so the <c>EngineFamilyAgnosticTests</c> fitness
/// function stays green.
/// </para>
/// <para>
/// <b>Version pinning is a follow-up (ADR-PC-009 §P1, flagged for the maintainer).</b> v1 resolves
/// <c>product_code → structural facts</c> only; it does NOT yet stamp a per-instance product-config
/// VERSION on the constitution event (the way the rate sheet stamps <c>rate_sheet_version_id</c>).
/// The product-config YAMLs are static, deploy-time artefacts (there is no versioned
/// <c>POST /v1/product-configs</c> deploy timeline), so the event's <c>pack_version</c> already
/// encodes the operative configuration generation. Full product-config version pinning is later work.
/// </para>
/// </remarks>
public interface IProductConfigStore
{
    /// <summary>
    /// Resolve the structural facts for <paramref name="productId"/>, or <c>null</c> when the engine
    /// holds no config for that code (the family decider turns the null into a fail-loud refusal —
    /// it never constitutes on a silent default). Pure: an in-memory lookup over the facts loaded at
    /// startup, no I/O, no clock.
    /// </summary>
    ProductConfig? Resolve(string productId);
}

/// <summary>
/// The STRUCTURAL facts the engine resolves for a product code at constitution — the deposit's
/// shape, never its price. Mirrors the committed <c>product-configs/*.yaml</c> fields the engine
/// already validates at deploy time (term_days / interest_variant / auto_renewal_policy /
/// payment_period_months), plus the pricing role the rate-sheet resolve keys on. No PII (ADR-PC-004
/// §P2): every field is a closed code or an integer count.
/// </summary>
/// <param name="ProductId">The product code these facts describe (e.g. <c>dpz_pt_12m_juros_venc</c>).</param>
/// <param name="TermDays">The deposit term in days (e.g. 365).</param>
/// <param name="InterestVariant">The interest-variant code (AT_MATURITY / PERIODIC / ADVANCE).</param>
/// <param name="AutoRenewalPolicy">The auto-renewal policy code (NONE / SAME_TERM_*).</param>
/// <param name="PaymentPeriodMonths">The PERIODIC coupon cadence in months (0 for AT_MATURITY / ADVANCE).</param>
/// <param name="DefaultRole">The pricing role the rate-sheet resolve uses when the command supplies
/// none (v1: <c>standard</c> for every launch product).</param>
public sealed record ProductConfig(
    string ProductId,
    int TermDays,
    string InterestVariant,
    string AutoRenewalPolicy,
    int PaymentPeriodMonths,
    string DefaultRole);
