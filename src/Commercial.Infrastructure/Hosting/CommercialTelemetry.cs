using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Commercial.Infrastructure.Hosting;

/// <summary>Platform-owned ActivitySource / Meter (registered with OpenTelemetry in all hosts).</summary>
public static class CommercialTelemetry
{
    public const string SourceName = "Commercial.Platform";
    public const string MeterName = "Commercial.Platform";

    public static readonly ActivitySource ActivitySource = new(SourceName);

    private static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> OutboxProcessed = Meter.CreateCounter<long>("commercial.outbox.processed");
    public static readonly Counter<long> OutboxFailed = Meter.CreateCounter<long>("commercial.outbox.failed");
    public static readonly Counter<long> OutboxDeadLettered = Meter.CreateCounter<long>("commercial.outbox.dead_lettered");
    public static readonly Counter<long> OutboxLeaseLost = Meter.CreateCounter<long>("commercial.outbox.lease_lost");
}
