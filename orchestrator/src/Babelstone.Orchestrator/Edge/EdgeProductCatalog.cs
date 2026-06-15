namespace Babelstone.Orchestrator.Edge;

/// <summary>
/// In plain English: when a client asks to open a deposit it gives us a product code (like
/// "dpz_pt_12m_juros_venc"). The engine's constitute call needs a few STRUCTURAL facts about that
/// product — how long the term is, when interest is paid, whether it auto-renews, the coupon cadence,
/// and the pricing role. This little catalogue turns a product code into those facts at the edge, so the
/// saga can carry them. It is NOT a price source — the engine resolves the rate itself, in-transaction.
/// </summary>
/// <remarks>
/// <para>
/// <b>The walking-skeleton stand-in for a product-config registry (bd babelstone-t7o3.11, ADR-PC-009).</b>
/// The authoritative product configs live as YAML under <c>product-configs/</c> and are consumed by the
/// engine's pack/rate-sheet machinery; a per-deposit product-config REGISTRY the orchestrator could query
/// at admission is later work (the same "later work" the engine's
/// <c>TermDepositConstitutionService</c> documents). For the v1 walking skeleton the orchestrator pins
/// the STRUCTURAL shape from a small in-memory map keyed on the product code, mirroring the launch
/// products. The map is the orchestrator's own, not an engine reference — the orchestrator stays
/// extraction-ready (ADR-PC-019 §P2, no engine-kernel dependency). A client that already knows the
/// product (the MCP agent or Mission Control, both reading the catalogue) may OVERRIDE any field on the
/// request; this catalogue only fills the gaps.
/// </para>
/// <para>
/// <b>Structural, never price, never PII (ADR-PC-004 §P2 / ADR-PC-008 §S2).</b> Every field is the
/// product's shape — a term-day count, closed variant/policy codes, a coupon cadence, a pricing role.
/// The TAN is deliberately absent: the engine resolves the active rate sheet IN-TRANSACTION at
/// constitution (bd babelstone-3k10). An unknown product code yields the conservative default shape
/// (12-month AT_MATURITY, standard role) so a misconfigured-but-priced product still constitutes rather
/// than 500ing at the edge; the engine's own rate-sheet resolve is the fail-loud authority on whether
/// the (product, role) is actually priced.
/// </para>
/// </remarks>
public sealed record EdgeProductShape(
    int TermDays,
    string InterestVariant,
    string AutoRenewalPolicy,
    int PaymentPeriodMonths,
    string Role);

/// <summary>The orchestrator edge's product-shape resolver (the walking-skeleton stand-in described on
/// <see cref="EdgeProductShape"/>).</summary>
public static class EdgeProductCatalog
{
    // The conservative walking-skeleton default — the thinnest real PT deposit shape (12-month
    // AT_MATURITY, NONE renewal, standard role; 02 §2.1). Used for an unknown product code so the edge
    // never 500s on a structural lookup; the engine's rate-sheet resolve remains the fail-loud authority.
    private static readonly EdgeProductShape Default =
        new(TermDays: 365, InterestVariant: "AT_MATURITY", AutoRenewalPolicy: "NONE", PaymentPeriodMonths: 0, Role: "standard");

    // The launch products' structural shapes, keyed on the catalogue product code (mirroring the
    // product-configs/*.yaml variant_ids). Structural facts only — the rate is the engine's in-tx resolve.
    private static readonly IReadOnlyDictionary<string, EdgeProductShape> Shapes =
        new Dictionary<string, EdgeProductShape>(StringComparer.Ordinal)
        {
            // 12-month, simple interest at maturity (the canonical walking-skeleton variant).
            ["dpz_pt_12m_juros_venc"] =
                new(365, "AT_MATURITY", "NONE", 0, "standard"),
            // 12-month, monthly coupons (PERIODIC, cadence 1).
            ["dpz_pt_12m_juros_mensal"] =
                new(365, "PERIODIC", "NONE", 1, "standard"),
            // 24-month, quarterly coupons (PERIODIC, cadence 3).
            ["dpz_pt_24m_juros_trimestral"] =
                new(730, "PERIODIC", "NONE", 3, "standard"),
            // 6-month, interest paid up front (ADVANCE).
            ["dpz_pt_6m_juros_antecipados"] =
                new(182, "ADVANCE", "NONE", 0, "standard"),
            // 18-month, tiered early-redemption (AT_MATURITY shape for the structural facts the engine needs).
            ["dpz_pt_18m_resgate_escalonado"] =
                new(548, "AT_MATURITY", "NONE", 0, "standard"),
        };

    /// <summary>Resolve the STRUCTURAL shape for <paramref name="productCode"/>, falling back to the
    /// conservative walking-skeleton default for an unknown code. Pure — an in-memory lookup, no I/O,
    /// no clock.</summary>
    public static EdgeProductShape Resolve(string productCode) =>
        Shapes.TryGetValue(productCode, out var shape) ? shape : Default;
}
