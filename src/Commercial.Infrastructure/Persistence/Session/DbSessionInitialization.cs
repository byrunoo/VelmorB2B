using System.Data.Common;
using Commercial.Application.Abstractions.Scope;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Commercial.Infrastructure.Persistence.Session;

/// <summary>
/// Tenant-isolation seam (ADR-0001): the session-context WRITER. Invoked every time EF opens a database connection
/// (including physical connections reused from the Npgsql pool) with the current execution scope. Production
/// implementation: <see cref="RlsDbSessionInitializer"/>.
/// </summary>
public interface IDbSessionInitializer
{
    ValueTask InitializeAsync(DbConnection connection, ExecutionScope scope, CancellationToken cancellationToken);
}

/// <summary>
/// Phase 0 placeholder kept only as a type the architecture rules can refer to. It must never be registered: with it,
/// no session fingerprint is written and the <see cref="DbSessionScopeGuard"/> rejects every command (fail closed).
/// </summary>
public sealed class NoOpDbSessionInitializer : IDbSessionInitializer
{
    public ValueTask InitializeAsync(DbConnection connection, ExecutionScope scope, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}

/// <summary>
/// Wires <see cref="IDbSessionInitializer"/> into every EF connection open, then records the scope fingerprint that
/// PostgreSQL ACTUALLY holds (server read-back, not the writer's claim) for <see cref="DbSessionScopeGuard"/>.
/// Any failure closes the connection and leaves no record, so the connection cannot run commands (fail closed).
/// On close the record is dropped; the next open re-initializes every key.
/// </summary>
public sealed class DbSessionInitializerInterceptor(
    IDbSessionInitializer initializer,
    IExecutionScopeAccessor scopeAccessor,
    DbSessionRegistry registry) : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        registry.Forget(connection);
        try
        {
            initializer.InitializeAsync(connection, scopeAccessor.Current, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            registry.Record(connection, DbSessionContextReader.ReadFingerprint(connection));
        }
        catch
        {
            Abandon(connection);
            throw;
        }
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        registry.Forget(connection);
        try
        {
            await initializer.InitializeAsync(connection, scopeAccessor.Current, cancellationToken);
            registry.Record(connection, await DbSessionContextReader.ReadFingerprintAsync(connection, cancellationToken));
        }
        catch
        {
            await AbandonAsync(connection);
            throw;
        }
    }

    public override void ConnectionClosed(DbConnection connection, ConnectionEndEventData eventData) => registry.Forget(connection);

    public override Task ConnectionClosedAsync(DbConnection connection, ConnectionEndEventData eventData)
    {
        registry.Forget(connection);
        return Task.CompletedTask;
    }

    private void Abandon(DbConnection connection)
    {
        registry.Forget(connection);
        connection.Close();
    }

    private async Task AbandonAsync(DbConnection connection)
    {
        registry.Forget(connection);
        await connection.CloseAsync();
    }
}
