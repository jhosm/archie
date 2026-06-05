using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Babelstone.Telemetry;

/// <summary>
/// The single <see cref="ActivitySource"/> every Babelstone .NET host and the runtime shell
/// open manual spans on, plus the single <see cref="Meter"/> they record metrics on
/// (ADR-IC-007 Layer 1). A process turns these on by registering the names with its OTel
/// provider (<c>AddSource(BabelstoneTelemetry.ActivitySourceName)</c> /
/// <c>AddMeter(BabelstoneTelemetry.MeterName)</c>); when no listener is attached (the common
/// test/library path) <see cref="ActivitySource.StartActivity(string,ActivityKind)"/> returns
/// <c>null</c> and an instrument's <c>Record</c> is a near-zero-cost no-op. The source/meter
/// names double as the OTel <c>instrumentation.scope</c>, so they are stable, versioned identifiers.
/// </summary>
public static class BabelstoneTelemetry
{
    /// <summary>The instrumentation scope / activity-source name. Stable — hosts register it by this exact string.</summary>
    public const string ActivitySourceName = "Babelstone.Engine";

    /// <summary>The instrumentation scope / meter name. Stable — hosts register it via <c>AddMeter</c> by this exact string.</summary>
    public const string MeterName = "Babelstone.Engine";

    /// <summary>The process-wide source manual spans (e.g. <c>accrual.computed</c>) are started on.</summary>
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    /// <summary>The process-wide meter instruments (e.g. the outbox publish-lag SLI) are created on.</summary>
    public static readonly Meter Meter = new(MeterName);
}
