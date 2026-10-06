namespace Commercial.Domain.Primitives;

/// <summary>Marker for domain events raised by aggregates (no aggregates exist in Phase 0).</summary>
public interface IDomainEvent
{
    Guid EventId { get; }

    DateTimeOffset OccurredAtUtc { get; }
}
