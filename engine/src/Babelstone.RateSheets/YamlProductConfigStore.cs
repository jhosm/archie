using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Babelstone.RateSheets;

/// <summary>
/// In plain English: this reads the committed <c>product-configs/*.yaml</c> files off disk once at
/// startup and turns each product code into the structural facts the engine needs to constitute a
/// deposit (term, interest style, renewal policy, coupon cadence, pricing role). It is the engine's
/// single home for "what does this product code mean" — the orchestrator no longer carries that.
/// </summary>
/// <remarks>
/// <para>
/// <b>Disk-backed, fail-loud, load-once (mirrors the host's <c>HostPackStore</c> / <c>HostPack</c>).</b>
/// The product-config YAMLs are the same auditor-readable artefacts the depths-1–4 validator and the
/// rate-sheet cross-artefact checks already consume; there is no versioned deploy endpoint for them,
/// so they are loaded structurally at the engine host's startup and cached immutably. A missing
/// directory or an unparseable file fails the load loud rather than constituting on a silent default
/// (the same discipline the pack loader takes, ADR-PC-007 §P4).
/// </para>
/// <para>
/// <b>Structural facts only, never price (ADR-PC-008 §S2).</b> This store reads <c>term_days</c>,
/// <c>interest_variant</c>, <c>auto_renewal_policy</c>, and <c>payment_period_months</c> (0 when the
/// file omits it — AT_MATURITY / ADVANCE have no coupons). The TAN is the rate-sheet resolve's job;
/// the <c>rate:</c> / <c>early_termination:</c> / <c>principal_bounds:</c> blocks in the YAML are NOT
/// read here. The <see cref="ProductConfig.DefaultRole"/> is <c>standard</c> for every v1 launch
/// product (the role-selector machinery is a follow-up; the command may still override the role).
/// </para>
/// <para>
/// <b>Family-agnostic (ADR-PC-021 §P2).</b> This lives in the generic <c>Babelstone.RateSheets</c>
/// spine — a host composes it and a family decider consumes the <see cref="IProductConfigStore"/>
/// seam, the family→spine arrow. No family is referenced, so <c>EngineFamilyAgnosticTests</c> stays
/// green. The interest-variant / renewal-policy tokens it normalises to (AT_MATURITY / PERIODIC /
/// ADVANCE; NONE / SAME_TERM_*) are the same closed codes the CUE family schema and the events use —
/// the spine carries the vocabulary, not the family's behaviour.
/// </para>
/// </remarks>
public sealed class YamlProductConfigStore : IProductConfigStore
{
    // Tolerant by design: a product-config YAML carries blocks this store does not read (`rate:`,
    // `early_termination:`, `principal_bounds:`, `currency:`, `day_count:`, `schema:`, `pack:`), so
    // unmatched keys are ignored rather than fatal. It DOES read the optional `partial_withdrawal:`
    // block (the F.12 policy primitives, bd k6r8.8). The closed-schema strictness lives in the CUE
    // depths-1–4 validator (ADR-PC-006), which the committed configs already pass in CI; this is the
    // runtime structural read of the SHAPE fields, not a re-run of that validation.
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private readonly IReadOnlyDictionary<string, ProductConfig> _byProductId;

    /// <summary>
    /// Load and cache every <c>*.yaml</c> under <paramref name="productConfigsDir"/> at construction.
    /// Fails loud if the directory does not exist or a file cannot be parsed into a coherent shape —
    /// the engine refuses to boot against an unreadable product-config tree rather than discover the
    /// gap on the first constitution.
    /// </summary>
    public YamlProductConfigStore(string? productConfigsDir)
    {
        var dir = productConfigsDir ?? FindProductConfigsDir();
        if (!Directory.Exists(dir))
        {
            throw new InvalidOperationException(
                $"product-configs directory '{dir}' not found; the engine resolves product_code → "
                + "structural facts from it at constitution (set Engine:ProductConfigsDir).");
        }

        var byProductId = new Dictionary<string, ProductConfig>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(dir, "*.yaml").OrderBy(p => p, StringComparer.Ordinal))
        {
            var config = Parse(path);
            byProductId[config.ProductId] = config;
        }

        if (byProductId.Count == 0)
        {
            throw new InvalidOperationException(
                $"product-configs directory '{dir}' carried no *.yaml product configs; the engine "
                + "cannot resolve any product_code to constitute a deposit.");
        }

        _byProductId = byProductId;
    }

    private YamlProductConfigStore(IReadOnlyDictionary<string, ProductConfig> byProductId) =>
        _byProductId = byProductId;

    /// <summary>An in-memory store over the supplied configs — for tests and for callers that resolve
    /// the configs another way. The disk path is the production loader; this skips the I/O.</summary>
    public static YamlProductConfigStore FromConfigs(IEnumerable<ProductConfig> configs)
    {
        ArgumentNullException.ThrowIfNull(configs);
        var byProductId = configs.ToDictionary(c => c.ProductId, StringComparer.Ordinal);
        return new YamlProductConfigStore(byProductId);
    }

    /// <inheritdoc />
    public ProductConfig? Resolve(string productId)
    {
        // A null/empty product code is "no config for that code" — return null (the caller turns it
        // into a fail-loud DomainRejected refusal), NOT an ArgumentException. A malformed body that
        // binds to a null product_id must reach a clean 4xx domain rejection, never an infrastructure
        // 500 (the engine boundary's 4xx-never-5xx contract).
        if (string.IsNullOrEmpty(productId))
        {
            return null;
        }

        return _byProductId.TryGetValue(productId, out var config) ? config : null;
    }

    private static ProductConfig Parse(string path)
    {
        ProductConfigYaml? yaml;
        try
        {
            yaml = Deserializer.Deserialize<ProductConfigYaml>(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"product config '{path}' could not be parsed as a structural product-config YAML.", ex);
        }

        if (yaml is null || string.IsNullOrWhiteSpace(yaml.VariantId))
        {
            throw new InvalidOperationException(
                $"product config '{path}' is missing the required 'variant_id'.");
        }

        if (string.IsNullOrWhiteSpace(yaml.InterestVariant))
        {
            throw new InvalidOperationException(
                $"product config '{path}' is missing the required 'interest_variant'.");
        }

        if (yaml.TermDays <= 0)
        {
            throw new InvalidOperationException(
                $"product config '{path}' has a non-positive 'term_days' ({yaml.TermDays}).");
        }

        return new ProductConfig(
            ProductId: yaml.VariantId,
            TermDays: yaml.TermDays,
            InterestVariant: yaml.InterestVariant,
            // The committed configs omit auto_renewal_policy only for NONE in practice, but default
            // explicitly so a missing key is the conservative single-term shape, never a crash.
            AutoRenewalPolicy: string.IsNullOrWhiteSpace(yaml.AutoRenewalPolicy) ? "NONE" : yaml.AutoRenewalPolicy,
            // AT_MATURITY / ADVANCE omit payment_period_months → 0 (no coupons); PERIODIC carries 1|3.
            PaymentPeriodMonths: yaml.PaymentPeriodMonths,
            // v1 launch products all price under the standard role; the role-selector machinery on the
            // YAML's rate.flat.rate_ref is a follow-up, and the command may override the role anyway.
            DefaultRole: "standard",
            // F.12 partial-withdrawal gates (bd k6r8.8). An OMITTED partial_withdrawal block leaves all
            // three at 0 — the engine resolves that to PartialWithdrawalPolicy.Unrestricted (02 §2.4.1).
            // Present-but-zero on any gate means "no minimum / no lock-up" for that gate, the same
            // degenerate semantics. The depth-4 coherence of the values (carencia < term; min-remaining <
            // max corridor) was already enforced by pack-validate at deploy time (ADR-PC-006), not here.
            MinWithdrawalCents: yaml.PartialWithdrawal?.MinWithdrawalCents ?? 0,
            MinRemainingBalanceCents: yaml.PartialWithdrawal?.MinRemainingBalanceCents ?? 0,
            CarenciaDays: yaml.PartialWithdrawal?.CarenciaDays ?? 0);
    }

    // Walk up from the running binary to the repo's product-configs/ tree — the same find-by-walking
    // discipline HostPackStore.FindPacksDir and the test fixtures use, so dev/test boots with no
    // explicit Engine:ProductConfigsDir set.
    private static string FindProductConfigsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "product-configs")))
        {
            dir = dir.Parent;
        }

        return dir is not null
            ? Path.Combine(dir.FullName, "product-configs")
            : throw new InvalidOperationException(
                $"product-configs/ directory not found from {AppContext.BaseDirectory}; set Engine:ProductConfigsDir.");
    }

    // The subset of the product-config YAML this store reads — the SHAPE fields. Unmatched keys
    // (rate / early_termination / principal_bounds / currency / day_count / schema / pack) are
    // ignored (IgnoreUnmatchedProperties above). Mutable, public-settable: YamlDotNet binds to it.
    private sealed class ProductConfigYaml
    {
        public string? VariantId { get; set; }
        public int TermDays { get; set; }
        public string? InterestVariant { get; set; }
        public string? AutoRenewalPolicy { get; set; }
        public int PaymentPeriodMonths { get; set; }

        // The optional F.12 partial-withdrawal block (bd k6r8.8). Null when the variant omits it ⇒ the
        // engine resolves PartialWithdrawalPolicy.Unrestricted. Field names mirror the CUE
        // #PartialWithdrawal block (underscored keys via the deserializer's naming convention).
        public PartialWithdrawalYaml? PartialWithdrawal { get; set; }
    }

    // The partial_withdrawal sub-block. Cents are long, carencia_days is an int day count — the same
    // types the engine's PartialWithdrawalPolicy carries. Mutable, public-settable: YamlDotNet binds it.
    private sealed class PartialWithdrawalYaml
    {
        public long MinWithdrawalCents { get; set; }
        public long MinRemainingBalanceCents { get; set; }
        public int CarenciaDays { get; set; }
    }
}
