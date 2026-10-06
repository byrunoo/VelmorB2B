using System.Data.Common;
using Commercial.Application.Abstractions.Scope;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Commercial.Infrastructure.Persistence.Session;

/// <summary>
/// Tenant-isolation seam (ADR-0001). Invoked every time EF opens a database connection
/// (including physical connections reused from the Npgsql pool), with the current execution scope.
///
/// Phase 0 default is <see cref="NoOpDbSessionInitializer"/>. If ADR-0001 selects PostgreSQL RLS,
/// the accepted implementation would set transaction/session settings here; if it selects EF global
/// query filters, this hook may remain a no-op. Phase 0 MUST NOT choose.
/// </summary>
public interface IDbSessionInitializer
{
    ValueTask InitializeAsync(DbConnection connection, ExecutionScope scope, CancellationToken cancellationToken);
}

public sealed class NoOpDbSessionInitializer : IDbSessionInitializer
{
    public ValueTask InitializeAsync(DbConnection connection, ExecutionScope scope, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}

/// <summary>Wires <see cref="IDbSessionInitializer"/> into every EF connection open.</summary>
public sealed class DbSessionInitializerInterceptor(
    IDbSessionInitializer initializer,
    IExecutionScopeAccessor scopeAccessor) : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) =>
        initializer.InitializeAsync(connection, scopeAccessor.Current, CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default) =>
        initializer.InitializeAsync(connection, scopeAccessor.Current, cancellationToken).AsTask();
}
