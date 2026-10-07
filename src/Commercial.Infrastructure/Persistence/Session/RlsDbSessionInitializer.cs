using System.Data.Common;
using System.Text;
using Commercial.Application.Abstractions.Scope;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Commercial.Infrastructure.Persistence.Session;

/// <summary>
/// PRODUCTION session-context WRITER (ADR-0001 §2.7). On every connection open it writes ALL
/// <see cref="DbSessionContext.Keys"/> from the current, validated <see cref="ExecutionScope"/>:
/// <list type="bullet">
/// <item><c>set_config(key, value, false)</c> — session level. Transaction-local (<c>true</c>) is NOT used: the ADR spike
/// showed it is lost between autocommit statements. The call runs right after open, outside any transaction (autocommit),
/// so it is committed immediately and a later ROLLBACK cannot revert it.</item>
/// <item>every key is written every time; "not applicable" is the empty string — a pooled connection never keeps a
/// previous request's value (in addition to Npgsql's pool reset, which must stay enabled: No Reset On Close is
/// rejected here at runtime).</item>
/// <item>one parameterized statement — key names and values are parameters, never concatenated into SQL.</item>
/// <item>PostgreSQL's returned values are compared with the intended ones; any difference fails closed.</item>
/// </list>
/// Empty / NULL / missing values are never broader access: future RLS helpers treat them as "scope unavailable".
/// The verifier (<see cref="DbSessionScopeGuard"/>) is separate: this class writes, the guard checks.
/// </summary>
public sealed class RlsDbSessionInitializer(ILogger<RlsDbSessionInitializer> logger) : IDbSessionInitializer
{
    private static readonly string SetAllSql = BuildSetAllSql();

    public async ValueTask InitializeAsync(DbConnection connection, ExecutionScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var context = Prepare(connection, scope);

        await using var command = CreateCommand(connection, context);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new DbSessionScopeException(DbSessionScopeException.InitializationFailed, "set_config returned no row");
        }

        Confirm(reader, context);
        Log(scope);
    }

    /// <summary>Validates everything that can be validated without the database (fail closed).</summary>
    internal static DbSessionContext Prepare(DbConnection connection, ExecutionScope? scope)
    {
        if (scope is null)
        {
            throw new DbSessionScopeException(DbSessionScopeException.InitializationFailed, "no execution scope");
        }

        if (new NpgsqlConnectionStringBuilder(connection.ConnectionString).NoResetOnClose)
        {
            throw new DbSessionScopeException(DbSessionScopeException.InitializationFailed,
                "'No Reset On Close' is forbidden: pooled connections would keep session scope (ADR-0001 §2.8)");
        }

        IReadOnlyList<string> violations;
        try
        {
            violations = ExecutionScopeValidator.Validate(scope);
        }
        catch (NullReferenceException ex)
        {
            throw new DbSessionScopeException(DbSessionScopeException.InitializationFailed, "execution scope is not a constructed, validated scope", ex);
        }

        if (violations.Count > 0 || string.IsNullOrEmpty(scope.Fingerprint.Value))
        {
            throw new DbSessionScopeException(DbSessionScopeException.InitializationFailed, "execution scope is invalid");
        }

        var context = DbSessionContext.FromScope(scope);
        if (context.Settings.Count != DbSessionContext.Keys.Count || context.Settings.Any(s => s.Value is null))
        {
            throw new DbSessionScopeException(DbSessionScopeException.InitializationFailed, "session context is incomplete");
        }

        return context;
    }

    private static DbCommand CreateCommand(DbConnection connection, DbSessionContext context)
    {
        var command = connection.CreateCommand();
        command.CommandText = SetAllSql;
        for (var i = 0; i < context.Settings.Count; i++)
        {
            DbSessionContextReader.AddParameter(command, $"k{i}", context.Settings[i].Key);
            DbSessionContextReader.AddParameter(command, $"v{i}", context.Settings[i].Value);
        }

        return command;
    }

    private static void Confirm(DbDataReader reader, DbSessionContext context)
    {
        for (var i = 0; i < context.Settings.Count; i++)
        {
            var actual = reader.IsDBNull(i) ? null : reader.GetString(i);
            if (!string.Equals(actual, context.Settings[i].Value, StringComparison.Ordinal))
            {
                throw new DbSessionScopeException(DbSessionScopeException.InitializationFailed, $"PostgreSQL did not confirm {context.Settings[i].Key}");
            }
        }
    }

    private void Log(ExecutionScope scope)
    {
        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug(
                "DB session context written: {ScopeKind} {PrincipalType} {ScopeFingerprint} {CorrelationId}",
                scope.Kind, scope.PrincipalType, scope.Fingerprint.ShortId, scope.CorrelationId);
        }
    }

    private static string BuildSetAllSql()
    {
        var sql = new StringBuilder("SELECT ");
        for (var i = 0; i < DbSessionContext.Keys.Count; i++)
        {
            sql.Append(i == 0 ? string.Empty : ", ").Append("set_config(@k").Append(i).Append(", @v").Append(i).Append(", false)");
        }

        return sql.ToString();
    }
}
