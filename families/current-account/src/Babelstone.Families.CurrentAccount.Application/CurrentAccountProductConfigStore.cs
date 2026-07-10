using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Babelstone.Families.CurrentAccount.Application;

/// <summary>
/// The current_account family's OWN product-config store (ADR-PC-037 §D5): reads the committed
/// <c>product-configs/current-account/*.yaml</c> files off disk once at startup and turns each product
/// code into the <see cref="CurrentAccountProductConfig"/> the authorize decider resolves its stage-4
/// rules from. In plain English: the family's single home for "what does this current-account product
/// code mean" — the arranged overdraft and the per-transaction cap.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately family-owned, not the spine store.</b> Term-deposit resolves its variants through the
/// spine <c>IProductConfigStore</c> / <c>YamlProductConfigStore</c> (<c>Babelstone.RateSheets</c>), which
/// is deposit-shaped and would reject a current-account config. Rather than couple the two families onto
/// that store, this family reads its OWN configs from the <c>product-configs/current-account/</c>
/// subdirectory; the deposit store reads only the top-level <c>product-configs/</c> directory, so this
/// subdirectory is out of its sight and the two families' config surfaces stay decoupled. The dependency
/// arrow stays family→engine (ENGINE_FAMILY_AGNOSTIC): the engine names no current-account config.
/// </para>
/// <para>
/// <b>Disk-backed, fail-loud, load-once</b> (mirrors the deposit <c>YamlProductConfigStore</c> and the
/// host pack loader). A missing directory or an unparseable file fails the load loud rather than
/// authorizing on a silent default. The closed-schema strictness lives in the CUE depths-1–4 validator
/// (ADR-PC-006), which the committed configs pass in CI; this is the runtime structural read of the limit
/// fields, not a re-run of that validation.
/// </para>
/// </remarks>
public sealed class CurrentAccountProductConfigStore
{
    // Tolerant: a current-account config carries fields this store does not read, so unmatched keys are
    // ignored rather than fatal — the same stance as the deposit YamlProductConfigStore. The full
    // closed-schema check (an unknown key is an error) is the CUE validator's job (ADR-PC-006), which the
    // committed configs pass in CI; this is only the runtime structural read of the limit fields.
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private readonly IReadOnlyDictionary<string, CurrentAccountProductConfig> _byProductCode;

    /// <summary>
    /// Load and cache every <c>*.yaml</c> under <paramref name="configsDir"/> at construction (default: the
    /// auto-discovered <c>product-configs/current-account/</c>). Fails loud if the directory does not exist
    /// or a file cannot be parsed into a coherent shape — the engine refuses to boot against an unreadable
    /// current-account config tree rather than discover the gap on the first authorization.
    /// </summary>
    public CurrentAccountProductConfigStore(string? configsDir = null)
    {
        var dir = configsDir ?? FindConfigsDir();
        if (!Directory.Exists(dir))
        {
            throw new InvalidOperationException(
                $"current_account product-configs directory '{dir}' not found; the family resolves "
                + "product_code → arranged overdraft / limits from it at authorization "
                + "(set Engine:CurrentAccountConfigsDir).");
        }

        var byProductCode = new Dictionary<string, CurrentAccountProductConfig>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(dir, "*.yaml").OrderBy(p => p, StringComparer.Ordinal))
        {
            var config = Parse(path);
            byProductCode[config.ProductCode] = config;
        }

        if (byProductCode.Count == 0)
        {
            throw new InvalidOperationException(
                $"current_account product-configs directory '{dir}' carried no *.yaml configs; the family "
                + "cannot resolve any product_code to authorize a debit.");
        }

        _byProductCode = byProductCode;
    }

    private CurrentAccountProductConfigStore(IReadOnlyDictionary<string, CurrentAccountProductConfig> byProductCode) =>
        _byProductCode = byProductCode;

    /// <summary>An in-memory store over the supplied configs — for tests and callers that resolve the
    /// configs another way. The disk path is the production loader; this skips the I/O.</summary>
    public static CurrentAccountProductConfigStore FromConfigs(IEnumerable<CurrentAccountProductConfig> configs)
    {
        ArgumentNullException.ThrowIfNull(configs);
        return new CurrentAccountProductConfigStore(
            configs.ToDictionary(c => c.ProductCode, StringComparer.Ordinal));
    }

    /// <summary>
    /// Resolve the config for <paramref name="productCode"/>, or <c>null</c> when the store holds none —
    /// the caller resolves that to the zero-overdraft degenerate (<see cref="CurrentAccountProductConfig.None"/>),
    /// the conservative no-headroom gate, rather than refusing a live account's every debit over a config
    /// gap. Pure: an in-memory lookup over the facts loaded at startup, no I/O, no clock.
    /// </summary>
    public CurrentAccountProductConfig? Resolve(string productCode) =>
        string.IsNullOrEmpty(productCode) ? null
        : _byProductCode.TryGetValue(productCode, out var config) ? config
        : null;

    private static CurrentAccountProductConfig Parse(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"current_account product config '{path}' could not be read.", ex);
        }

        CurrentAccountConfigYaml? yaml;
        try
        {
            yaml = Deserializer.Deserialize<CurrentAccountConfigYaml>(text);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"current_account product config '{path}' could not be parsed as a current-account config YAML.", ex);
        }

        if (yaml is null || string.IsNullOrWhiteSpace(yaml.VariantId))
        {
            throw new InvalidOperationException(
                $"current_account product config '{path}' is missing the required 'variant_id'.");
        }

        return new CurrentAccountProductConfig(
            ProductCode: yaml.VariantId,
            // Absent arranged_overdraft_limit ⇒ 0 (no overdraft headroom) — the shape of a ca_pt_basic
            // account that omits the field. Present-but-zero is the same degenerate.
            ArrangedOverdraftLimitCents: yaml.ArrangedOverdraftLimit ?? 0,
            // Absent cap ⇒ null (unconstrained): the per-transaction ceiling and the rolling daily/monthly
            // velocity caps all flow from transaction_limits onto the stage-4 rules (ADR-PC-037 §D5).
            PerTransactionLimitCents: yaml.TransactionLimits?.PerTransactionMaxCents,
            DailyVelocityLimitCents: yaml.TransactionLimits?.DailyVelocityCents,
            MonthlyVelocityLimitCents: yaml.TransactionLimits?.MonthlyVelocityCents,
            // Absent rate block ⇒ null (the product accrues no overdraft interest — a ca_pt_basic account).
            // A well-formed block needs BOTH coordinates; a half-declared rate is a config error the family
            // rejects loud rather than resolving against a silent default (ADR-PC-008 / ADR-PC-037 §D5).
            OverdraftRate: ToOverdraftRateRef(yaml.Rate, path));
    }

    private static OverdraftRateRef? ToOverdraftRateRef(RateRefYaml? rate, string path)
    {
        if (rate is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(rate.Sheet) || string.IsNullOrWhiteSpace(rate.RoleSelector))
        {
            throw new InvalidOperationException(
                $"current_account product config '{path}' declares a 'rate' block missing 'sheet' or "
                + "'role_selector'; an overdraft-interest rate reference needs both coordinates (ADR-PC-008).");
        }

        return new OverdraftRateRef(rate.Sheet, rate.RoleSelector);
    }

    // Walk up from the running binary to the repo's product-configs/current-account/ tree — the same
    // find-by-walking discipline the deposit YamlProductConfigStore uses, so dev/test boots with no
    // explicit Engine:CurrentAccountConfigsDir set.
    private static string FindConfigsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null
            && !Directory.Exists(Path.Combine(dir.FullName, "product-configs", "current-account")))
        {
            dir = dir.Parent;
        }

        return dir is not null
            ? Path.Combine(dir.FullName, "product-configs", "current-account")
            : throw new InvalidOperationException(
                $"product-configs/current-account/ not found from {AppContext.BaseDirectory}; set Engine:CurrentAccountConfigsDir.");
    }

    // The subset of the current-account config YAML this store reads — the LIMIT fields plus the overdraft
    // rate REFERENCE. Every other key the config carries is ignored (see the deserializer above). Mutable,
    // public-settable: YamlDotNet binds.
    private sealed class CurrentAccountConfigYaml
    {
        public string? VariantId { get; set; }
        public long? ArrangedOverdraftLimit { get; set; }
        public TransactionLimitsYaml? TransactionLimits { get; set; }
        public RateRefYaml? Rate { get; set; }
    }

    // The transaction_limits sub-block. Every cap is a nullable long-cents — absent ⇒ null. All three
    // (the per-transaction ceiling + the daily/monthly velocity caps) surface onto the stage-4 rules (ADR-PC-037 §D5).
    private sealed class TransactionLimitsYaml
    {
        public long? PerTransactionMaxCents { get; set; }
        public long? DailyVelocityCents { get; set; }
        public long? MonthlyVelocityCents { get; set; }
    }

    // The rate sub-block — the overdraft-interest rate REFERENCE (ADR-PC-008 #RateRef shape). Absent ⇒ the
    // product accrues no overdraft interest. The numeric TAN is never here — it lives in the rate sheet.
    private sealed class RateRefYaml
    {
        public string? Sheet { get; set; }
        public string? RoleSelector { get; set; }
    }
}
