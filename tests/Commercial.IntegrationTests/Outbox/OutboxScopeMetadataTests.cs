using Commercial.Application.Abstractions.Messaging;
using Commercial.Application.Abstractions.Scope;
using Commercial.Application.Abstractions.Security;
using Commercial.Domain.Identity;
using Commercial.Infrastructure.Messaging.Outbox;
using Commercial.Infrastructure.Persistence;
using Commercial.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Commercial.IntegrationTests.Outbox;

/// <summary>Phase 1 WS2: outbox scope metadata contract — fresh scope from persisted metadata, never unrestricted SYSTEM.</summary>
public sealed class OutboxScopeMetadataTests(ContainersFixture containers)
{
    [Fact]
    public async Task Business_scoped_enqueue_is_refused_until_full_scope_metadata_can_be_persisted()
    {
        var cs = await containers.CreateMigratedDatabaseAsync();
        await using var provider = containers.BuildServices(cs);
        var workforce = ExecutionScope.WorkforceCompany(
            new PrincipalId(Guid.NewGuid()), new TenantId(Guid.NewGuid()), new CompanyId(Guid.NewGuid()), PermissionSet.Empty, "corr-ws2");

        using (provider.GetRequiredService<IExecutionScopeAccessor>().Begin(workforce))
        {
            await Assert.ThrowsAsync<InvalidExecutionScopeException>(() => OutboxSeed.EnqueueAsync(provider, RecordingHandler.Type, 1));
        }

        Assert.Empty(await OutboxSeed.AllAsync(provider)); // nothing partial persisted
    }

    [Fact]
    public async Task Row_with_partial_scope_metadata_is_dead_lettered_without_running_handlers()
    {
        var cs = await containers.CreateMigratedDatabaseAsync();
        var sink = new RecordingSink();
        var observed = new List<ExecutionScope>();
        await using var provider = containers.BuildServices(cs, after: s =>
        {
            s.AddScoped<IOutboxMessageHandler>(_ => new RecordingHandler(sink, "ws2"));
            s.AddScoped<IOutboxMessageHandler>(p => new ScopeCapturingHandler(p.GetRequiredService<IExecutionScopeAccessor>(), observed));
        });

        // A tenant-only row (tenant without company) cannot become a valid SYSTEM business scope.
        var partialId = Guid.CreateVersion7();
        var cleanId = Guid.CreateVersion7();
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CommercialDbContext>();
            db.OutboxMessages.Add(Row(partialId, RecordingHandler.Type, tenantId: Guid.NewGuid()));
            db.OutboxMessages.Add(Row(cleanId, ScopeCapturingHandler.Type, tenantId: null));
            await db.SaveChangesAsync();
        }

        var result = await provider.GetRequiredService<IOutboxDispatcher>().DispatchPendingAsync(CancellationToken.None);

        Assert.Equal(1, result.DeadLettered);
        Assert.Equal(1, result.Processed);
        Assert.Empty(sink.Deliveries); // handler never ran under a partial / unrestricted scope
        var dead = await OutboxSeed.GetAsync(provider, partialId);
        Assert.Equal(OutboxStatus.DeadLettered, dead.Status);
        Assert.StartsWith(InvalidExecutionScopeException.Code, dead.LastError, StringComparison.Ordinal);

        // Metadata-free row ran under a fresh SYSTEM infrastructure scope (no business access).
        var seen = Assert.Single(observed);
        Assert.Equal(ExecutionScopeKind.SystemInfrastructure, seen.Kind);
        Assert.False(seen.HasBusinessScope);
        Assert.Equal(cleanId.ToString("D"), seen.CausationId);
    }

    private static OutboxMessage Row(Guid id, string type, Guid? tenantId) => new()
    {
        Id = id,
        OccurredAtUtc = DateTimeOffset.UtcNow,
        Type = type,
        Payload = "{}",
        CorrelationId = "corr-" + id.ToString("N"),
        TenantId = tenantId,
        NextAttemptAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
        Status = OutboxStatus.Pending,
    };

    private sealed class ScopeCapturingHandler(IExecutionScopeAccessor accessor, List<ExecutionScope> observed) : IOutboxMessageHandler
    {
        public const string Type = "test.scope-capture.v1";

        public string MessageType => Type;

        public Task HandleAsync(OutboxMessageContext message, CancellationToken cancellationToken)
        {
            lock (observed)
            {
                observed.Add(accessor.Current);
            }

            return Task.CompletedTask;
        }
    }
}
