using System.Collections.Concurrent;
using Commercial.Application.Abstractions.Messaging;
using Commercial.Application.Abstractions.Persistence;
using Commercial.Infrastructure.Messaging.Outbox;
using Commercial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Commercial.IntegrationTests.Outbox;

public sealed record TestPayload(int Sequence);

/// <summary>
/// Test sink shared by several "worker" service providers. Detects concurrent processing of the same
/// message (two active owners) and counts deliveries.
/// </summary>
public sealed class RecordingSink
{
    public ConcurrentDictionary<Guid, int> Deliveries { get; } = new();

    public ConcurrentDictionary<Guid, string> InFlight { get; } = new();

    public ConcurrentBag<string> Overlaps { get; } = [];

    public ConcurrentBag<(Guid MessageId, string Worker)> ProcessedBy { get; } = [];
}

public sealed class RecordingHandler(RecordingSink sink, string workerName) : IOutboxMessageHandler
{
    public const string Type = "test.recording.v1";

    public string MessageType => Type;

    public async Task HandleAsync(OutboxMessageContext message, CancellationToken cancellationToken)
    {
        if (!sink.InFlight.TryAdd(message.MessageId, workerName))
        {
            sink.Overlaps.Add($"{message.MessageId} already in flight on {sink.InFlight[message.MessageId]}, also {workerName}");
        }

        try
        {
            await Task.Delay(Random.Shared.Next(1, 6), cancellationToken);
            sink.Deliveries.AddOrUpdate(message.MessageId, 1, (_, n) => n + 1);
            sink.ProcessedBy.Add((message.MessageId, workerName));
        }
        finally
        {
            sink.InFlight.TryRemove(message.MessageId, out _);
        }
    }
}

public sealed class FailingHandler : IOutboxMessageHandler
{
    public const string Type = "test.failing.v1";

    public string MessageType => Type;

    public Task HandleAsync(OutboxMessageContext message, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Simulated handler failure");
}

public static class OutboxSeed
{
    public static async Task<IReadOnlyList<Guid>> EnqueueAsync(IServiceProvider provider, string type, int count)
    {
        await using var scope = provider.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutbox>();
        return await unitOfWork.ExecuteInTransactionAsync(
            _ => Task.FromResult<IReadOnlyList<Guid>>(Enumerable.Range(0, count).Select(i => outbox.Enqueue(type, new TestPayload(i))).ToList()),
            CancellationToken.None);
    }

    public static async Task<OutboxMessage> GetAsync(IServiceProvider provider, Guid id)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CommercialDbContext>().OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == id);
    }

    public static async Task<List<OutboxMessage>> AllAsync(IServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CommercialDbContext>().OutboxMessages.AsNoTracking().ToListAsync();
    }
}
