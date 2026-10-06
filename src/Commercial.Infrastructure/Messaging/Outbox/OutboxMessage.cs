namespace Commercial.Infrastructure.Messaging.Outbox;

/// <summary>
/// Persisted outbox row. Infrastructure-owned runtime table (not a business table).
/// Lease state (<see cref="LockedBy"/>, <see cref="LockedUntilUtc"/>) is written ONLY by
/// <see cref="OutboxStore"/> via guarded SQL — never through change tracking.
/// </summary>
public sealed class OutboxMessage
{
    public Guid Id { get; set; }

    public DateTimeOffset OccurredAtUtc { get; set; }

    public required string Type { get; set; }

    /// <summary>JSON payload (jsonb).</summary>
    public required string Payload { get; set; }

    public required string CorrelationId { get; set; }

    public string? CausationId { get; set; }

    /// <summary>Nullable until the Phase 1 tenant contract is finalized (ADR-0001).</summary>
    public Guid? TenantId { get; set; }

    public int Attempts { get; set; }

    public DateTimeOffset NextAttemptAtUtc { get; set; }

    public DateTimeOffset? ProcessedAtUtc { get; set; }

    public string? LastError { get; set; }

    public OutboxStatus Status { get; set; }

    public string? LockedBy { get; set; }

    public DateTimeOffset? LockedUntilUtc { get; set; }
}

public enum OutboxStatus
{
    Pending = 0,
    Processing = 1,
    Processed = 2,
    DeadLettered = 3,
}
