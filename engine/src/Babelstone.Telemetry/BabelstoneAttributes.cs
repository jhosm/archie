namespace Babelstone.Telemetry;

/// <summary>
/// The versioned <c>babelstone.*</c> span-attribute key contract and the manual span names
/// (ADR-IC-007 P2/P3). These keys are a wire contract read by Grafana/Tempo queries and
/// catalogue fitness functions — <b>never rename a key</b>; add a new one and deprecate the old.
///
/// Every key here is in the ADR-IC-007 P4 <i>operational</i> tier: structural identifiers only.
/// No NIF, IBAN, name, e-mail, or other personal/financial-restricted value may be carried under
/// these keys (catalogue <c>OBS_NO_PII_ATTRS</c> / ADR-PC-004 §P2). Money is carried as integer
/// cents under <see cref="InterestCents"/> / <see cref="TaxCents"/> — never a formatted decimal —
/// matching the engine's cents-native discipline.
/// </summary>
public static class BabelstoneAttributes
{
    /// <summary>The aggregate's partition key (v1: the stream id). Structural identifier, not PII.</summary>
    public const string PartitionKey = "babelstone.partition_key";

    /// <summary>The product code the command targets (e.g. a deposit product id). Structural, not PII.</summary>
    public const string ProductCode = "babelstone.product_code";

    /// <summary>Interest accrued, in integer cents (cents-native — never a formatted decimal).</summary>
    public const string InterestCents = "babelstone.interest_cents";

    /// <summary>Tax withheld, in integer cents (cents-native — never a formatted decimal).</summary>
    public const string TaxCents = "babelstone.tax_cents";

    /// <summary>The as-of date the computation is anchored to (a date, never a wall-clock-derived value at the call site).</summary>
    public const string AsOf = "babelstone.as_of";

    /// <summary>Manual span name for a deposit constitution (ADR-IC-007 P2 <c>&lt;entity&gt;.&lt;operation&gt;</c>).</summary>
    public const string SpanConstituted = "deposit.constituted";

    /// <summary>Manual span name for an interest-accrual computation (ADR-IC-007 P2 <c>&lt;layer&gt;.&lt;operation&gt;</c>).</summary>
    public const string SpanAccrualComputed = "accrual.computed";

    /// <summary>Manual span name for a withholding-tax application (ADR-IC-007 P2 <c>&lt;layer&gt;.&lt;operation&gt;</c>).</summary>
    public const string SpanWithholdingApplied = "withholding.applied";

    /// <summary>
    /// The outbox publish-lag SLI (ADR-IC-004 §P4): a histogram of the seconds between an outbox
    /// row's enqueue (<c>created_at</c>) and its successful publish ack (<c>published_at</c>). The
    /// metric name is the §P4 contract string — a Prometheus/Grafana query reads it by this exact
    /// name, so it follows snake_case-with-unit-suffix convention, never the <c>babelstone.*</c>
    /// span-key contract above. Warning/critical alert thresholds (30s / 5min) are deployment-time
    /// Grafana rules, not code.
    /// </summary>
    public const string OutboxPublishLagMetric = "outbox_publish_lag_seconds";

    /// <summary>
    /// The aggregate type the lagged row routes to (e.g. <c>term_deposit</c>) — the structural
    /// dimension the publish-lag histogram is tagged with so lag is breakable by topic. Operational
    /// tier, not PII; it is the same value carried as the row's <c>aggregate_type</c> / topic name.
    /// </summary>
    public const string AggregateType = "babelstone.aggregate_type";
}
