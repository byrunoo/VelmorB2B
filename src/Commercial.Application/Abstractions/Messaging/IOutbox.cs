namespace Commercial.Application.Abstractions.Messaging;

/// <summary>
/// Transactional outbox port. Messages enqueued inside
/// <see cref="Persistence.IUnitOfWork.ExecuteInTransactionAsync{T}"/> are persisted in the same
/// database transaction as the state change and dispatched after commit.
/// Correlation / causation / tenant are captured from the current execution scope.
/// </summary>
public interface IOutbox
{
    Guid Enqueue<TPayload>(string messageType, TPayload payload)
        where TPayload : notnull;
}

/// <summary>
/// Handles one outbox message type. Delivery is AT-LEAST-ONCE: implementations MUST be
/// idempotent (a duplicate delivery must not duplicate side effects).
/// </summary>
public interface IOutboxMessageHandler
{
    string MessageType { get; }

    Task HandleAsync(OutboxMessageContext message, CancellationToken cancellationToken);
}

public sealed record OutboxMessageContext(
    Guid MessageId,
    string MessageType,
    string Payload,
    string CorrelationId,
    string? CausationId,
    Guid? TenantId,
    int Attempt,
    DateTimeOffset OccurredAtUtc);
