using System.Collections.Concurrent;
using System.Data.Common;
using Commercial.Application.Abstractions.Scope;
using Commercial.Infrastructure.Persistence;
using Commercial.Infrastructure.Persistence.Session;
using Commercial.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Commercial.IntegrationTests.Persistence;

/// <summary>
/// A014 / ADR-0001 seam proof: the DB-session initializer runs through the REAL DbContext and the
/// pooled Npgsql connection path, receives the current execution scope, and anything it sets on the
/// connection is visible to the context's own queries. This proves the seam only; it does not choose
/// RLS or query filters.
/// </summary>
public sealed class SessionInitializerTests(ContainersFixture containers)
{
    [Fact]
    public async Task Default_initializer_is_no_op()
    {
        var cs = await containers.CreateMigratedDatabaseAsync();
        await using var provider = containers.BuildServices(cs);
        Assert.IsType<NoOpDbSessionInitializer>(provider.GetRequiredService<IDbSessionInitializer>());

        await using var scope = provider.CreateAsyncScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<CommercialDbContext>().Database.CanConnectAsync());
    }

    [Fact]
    public async Task Hook_runs_on_every_open_of_a_pooled_connection_with_the_current_scope()
    {
        // Max Pool Size=1 forces physical connection reuse so the pooled path is exercised deterministically.
        var cs = await containers.CreateMigratedDatabaseAsync("Maximum Pool Size=1");
        var recorder = new RecordingSessionInitializer();
        await using var provider = containers.BuildServices(cs, before: s => s.AddSingleton<IDbSessionInitializer>(recorder));
        var accessor = provider.GetRequiredService<IExecutionScopeAccessor>();

        var first = await ReadProbeAsync(provider, accessor, "scope-one");
        var second = await ReadProbeAsync(provider, accessor, "scope-two");

        Assert.Equal("scope-one", first.Probe);
        Assert.Equal("scope-two", second.Probe);                 // re-initialized for the new scope
        Assert.Equal(first.BackendPid, second.BackendPid);       // same pooled physical connection
        Assert.Equal(["scope-one", "scope-two"], recorder.Invocations.OrderBy(x => x, StringComparer.Ordinal));
    }

    private static async Task<(string Probe, int BackendPid)> ReadProbeAsync(IServiceProvider provider, IExecutionScopeAccessor accessor, string correlationId)
    {
        using var _ = accessor.Begin(ExecutionScope.Anonymous(correlationId));
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CommercialDbContext>();

        // Opened by EF (interceptor path), then queried on the very same connection.
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT current_setting('commercial.session_probe', true), pg_backend_pid()";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.GetInt32(1));
    }

    private sealed class RecordingSessionInitializer : IDbSessionInitializer
    {
        public ConcurrentBag<string> Invocations { get; } = [];

        public async ValueTask InitializeAsync(DbConnection connection, ExecutionScope scope, CancellationToken cancellationToken)
        {
            Invocations.Add(scope.CorrelationId);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT set_config('commercial.session_probe', @value, false)";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "value";
            parameter.Value = scope.CorrelationId;
            command.Parameters.Add(parameter);
            await command.ExecuteScalarAsync(cancellationToken);
        }
    }
}
