using System.Diagnostics;

namespace Babelstone.Telemetry;

/// <summary>
/// The single <see cref="ActivitySource"/> every Babelstone .NET host and the runtime shell
/// open manual spans on (ADR-IC-007 Layer 1). A process turns these spans on by registering
/// this source name with its tracer provider (<c>AddSource(BabelstoneTelemetry.ActivitySourceName)</c>);
/// when no listener is attached (the common test/library path) <see cref="ActivitySource.StartActivity(string,ActivityKind)"/>
/// returns <c>null</c> and the instrumentation is a near-zero-cost no-op. The source name doubles
/// as the OTel <c>service.instrumentation.scope</c>, so it is a stable, versioned identifier.
/// </summary>
public static class BabelstoneTelemetry
{
    /// <summary>The instrumentation scope / activity-source name. Stable — hosts register it by this exact string.</summary>
    public const string ActivitySourceName = "Babelstone.Engine";

    /// <summary>The process-wide source manual spans (e.g. <c>accrual.computed</c>) are started on.</summary>
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
}
