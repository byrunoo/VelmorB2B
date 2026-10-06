using System.Text.Json;
using Commercial.Application.Abstractions.Messaging;
using Commercial.Application.Abstractions.Persistence;
using Commercial.Application.Abstractions.Requests;
using Commercial.Application.Abstractions.Scope;
using Microsoft.Extensions.Logging;

namespace Commercial.Application.Diagnostics;

// Phase 0 infrastructure diagnostic — NOT a business capability.
// Proves: application transaction -> outbox row -> commit -> Hangfire dispatcher -> idempotent handler.

public static class DiagnosticPingMessage
{
    public const string Type = "diagnostics.ping.v1";

    public const int MaxNoteLength = 200;
}

public sealed record DiagnosticPingPayload(string? Note, DateTimeOffset RequestedAtUtc);

public sealed record EnqueueDiagnosticPing(string? Note) : ICommand<DiagnosticPingAccepted>;

public sealed record DiagnosticPingAccepted(Guid MessageId, string CorrelationId);

public sealed record GetDiagnosticPingStatus(Guid MessageId) : IQuery<DiagnosticPingStatus?>;

public sealed record DiagnosticPingStatus(
    Guid MessageId,
    string OutboxStatus,
    int Attempts,
    DateTimeOffset? ProcessedAtUtc,
    bool HandlerEffectRecorded,
    string CorrelationId);

/// <summary>Port: idempotent side-effect sink for the diagnostic handler.</summary>
public interface IDiagnosticPingReceiptStore
{
    /// <summary>Records the effect once; returns <c>false</c> when it was already recorded.</summary>
    Task<bool> TryRecordAsync(Guid messageId, string correlationId, string? note, CancellationToken cancellationToken);
}

/// <summary>Port: read model used by the developer screen.</summary>
public interface IDiagnosticPingReadModel
{
    Task<DiagnosticPingStatus?> GetAsync(Guid messageId, CancellationToken cancellationToken);
}

internal sealed class EnqueueDiagnosticPingValidator : IRequestValidator<EnqueueDiagnosticPing>
{
    public IEnumerable<ValidationFailure> Validate(EnqueueDiagnosticPing request)
    {
        if (request.Note is { Length: > DiagnosticPingMessage.MaxNoteLength })
        {
            yield return new ValidationFailure(
                "note",
                $"Note must be at most {DiagnosticPingMessage.MaxNoteLength} characters.");
        }
    }
}

internal sealed class EnqueueDiagnosticPingHandler(
    IUnitOfWork unitOfWork,
    IOutbox outbox,
    IExecutionScopeAccessor scope,
    TimeProvider clock) : ICommandHandler<EnqueueDiagnosticPing, DiagnosticPingAccepted>
{
    public Task<DiagnosticPingAccepted> HandleAsync(EnqueueDiagnosticPing command, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteInTransactionAsync(
            _ =>
            {
                var id = outbox.Enqueue(
                    DiagnosticPingMessage.Type,
                    new DiagnosticPingPayload(command.Note, clock.GetUtcNow()));
                return Task.FromResult(new DiagnosticPingAccepted(id, scope.Current.CorrelationId));
            },
            cancellationToken);
}

internal sealed class GetDiagnosticPingStatusHandler(IDiagnosticPingReadModel readModel)
    : IQueryHandler<GetDiagnosticPingStatus, DiagnosticPingStatus?>
{
    public Task<DiagnosticPingStatus?> HandleAsync(GetDiagnosticPingStatus query, CancellationToken cancellationToken) =>
        readModel.GetAsync(query.MessageId, cancellationToken);
}

/// <summary>
/// Idempotent by construction: the side effect is keyed by the outbox message id, so duplicate
/// delivery (lease expiry, crash after effect but before ack) records the effect only once.
/// </summary>
internal sealed class DiagnosticPingOutboxHandler(
    IDiagnosticPingReceiptStore receipts,
    ILogger<DiagnosticPingOutboxHandler> logger) : IOutboxMessageHandler
{
    public string MessageType => DiagnosticPingMessage.Type;

    public async Task HandleAsync(OutboxMessageContext message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var payload = JsonSerializer.Deserialize<DiagnosticPingPayload>(message.Payload, JsonSerializerOptions.Web);
        var recorded = await receipts.TryRecordAsync(message.MessageId, message.CorrelationId, payload?.Note, cancellationToken);

        if (!recorded)
        {
            logger.LogInformation("Duplicate delivery of {MessageId} ignored (effect already recorded)", message.MessageId);
        }
    }
}
