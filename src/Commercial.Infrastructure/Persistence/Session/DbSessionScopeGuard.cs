using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Commercial.Application.Abstractions.Scope;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Commercial.Infrastructure.Persistence.Session;

/// <summary>Per-connection fingerprint as read back from PostgreSQL after initialization.</summary>
public sealed class DbSessionRegistry
{
    private readonly ConditionalWeakTable<DbConnection, string> _fingerprints = [];

    internal void Record(DbConnection connection, string serverFingerprint) => _fingerprints.AddOrUpdate(connection, serverFingerprint);

    internal void Forget(DbConnection connection) => _fingerprints.Remove(connection);

    internal bool TryGet(DbConnection connection, out string fingerprint) => _fingerprints.TryGetValue(connection, out fingerprint!);
}

/// <summary>
/// The session-context VERIFIER (ADR-0001 fingerprint guard). Before any SQL runs on a <see cref="CommercialDbContext"/>
/// connection it requires:
/// <list type="number">
/// <item>the connection was opened through the initializer (a server-confirmed fingerprint is recorded);</item>
/// <item>that fingerprint equals the CURRENT ambient <see cref="ExecutionScope.Fingerprint"/>;</item>
/// <item>the SQL does not itself change <c>commercial.*</c> session keys (only the initializer writes them).</item>
/// </list>
/// It never corrects a mismatch (no in-place scope switch of an open connection / transaction): it throws
/// <see cref="DbSessionScopeException"/>. Recovery = close and reopen (the initializer rewrites every key).
/// The guard does not distinguish "business" from "infrastructure" SQL: every command is verified; infrastructure work
/// simply runs under explicit non-business scopes (Unscoped / SystemInfrastructure) whose context has empty business keys.
/// </summary>
public sealed partial class DbSessionScopeGuard(IExecutionScopeAccessor scopeAccessor, DbSessionRegistry registry, ILogger<DbSessionScopeGuard> logger)
{
    /// <summary>Verifies the connection a command is about to run on. Also called by the narrow raw-ADO infrastructure path (outbox store).</summary>
    public void Verify(DbCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var scope = scopeAccessor.Current;

        if (SessionKeyMutation().IsMatch(command.CommandText ?? string.Empty))
        {
            logger.LogWarning("Rejected SQL that modifies commercial session keys ({ScopeKind}, {ScopeFingerprint}, {CorrelationId})",
                scope.Kind, scope.Fingerprint.ShortId, scope.CorrelationId);
            throw new DbSessionScopeException(DbSessionScopeException.KeyWriteForbidden, "only the session initializer may set commercial.* session keys");
        }

        var connection = command.Connection;
        if (connection is null || connection.State != ConnectionState.Open || !registry.TryGet(connection, out var sessionFingerprint))
        {
            logger.LogWarning("Rejected SQL on a connection without an initialized session scope ({ScopeKind}, {ScopeFingerprint}, {CorrelationId})",
                scope.Kind, scope.Fingerprint.ShortId, scope.CorrelationId);
            throw new DbSessionScopeException(DbSessionScopeException.NotInitialized, "the connection was not opened through the session initializer");
        }

        if (!string.Equals(sessionFingerprint, scope.Fingerprint.Value, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "DB session scope mismatch: session {SessionFingerprint} vs ambient {ScopeFingerprint} ({ScopeKind}, {PrincipalType}, {CorrelationId})",
                ShortId(sessionFingerprint), scope.Fingerprint.ShortId, scope.Kind, scope.PrincipalType, scope.CorrelationId);
            throw new DbSessionScopeException(DbSessionScopeException.ScopeMismatch,
                "the connection's database session belongs to a different execution scope; close and reopen it under the current scope");
        }
    }

    private static string ShortId(string fingerprint) =>
        fingerprint.StartsWith(ScopeFingerprint.Prefix, StringComparison.Ordinal) && fingerprint.Length >= ScopeFingerprint.Prefix.Length + 12
            ? fingerprint.Substring(ScopeFingerprint.Prefix.Length, 12)
            : "<none>";

    // Session-state changes only (any set_config — key names could be parameters); UPDATE ... SET role = ... (a column)
    // does not match.
    [GeneratedRegex(@"\bset_config\s*\(|\bSET\s+(SESSION\s+|LOCAL\s+)?commercial\.|\bRESET\s+(commercial\.|ALL\b|ROLE\b)|\bSET\s+(SESSION\s+|LOCAL\s+)?(ROLE|SESSION\s+AUTHORIZATION)\b(?!\s*=)|\bDISCARD\s+(ALL|PLANS|SEQUENCES|TEMP|TEMPORARY)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SessionKeyMutation();
}

/// <summary>EF Core command interception seam for <see cref="DbSessionScopeGuard"/> (LINQ, SaveChanges and raw SQL via EF).</summary>
public sealed class DbSessionScopeGuardInterceptor(DbSessionScopeGuard guard) : DbCommandInterceptor
{
    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        guard.Verify(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        guard.Verify(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        guard.Verify(command);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        guard.Verify(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        guard.Verify(command);
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        guard.Verify(command);
        return ValueTask.FromResult(result);
    }
}
